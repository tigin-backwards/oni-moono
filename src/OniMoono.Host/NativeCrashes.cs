using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OniMoono.Engine;

namespace OniMoono.Host
{
    /// ARCHITECTURE §7, "Native crashes". A crash in native code (the GPU driver, SimDLL, a mod's
    /// library) has no managed exception, so the log just stops. On Windows OniMoono.exe
    /// (src/OniMoono.NativeHost/crash.c) starts a watcher process before the runtime, which writes
    /// to Player.log, once the game has ended without its own exit, the fault the game noted (code,
    /// module+offset, thread, a native stack), the runtime's "Fatal error." text and the exit code,
    /// with a minidump beside the log. This gives it the log's path, the main thread and the
    /// personal folders to hide, and tells it about the player's own exits.
    internal static unsafe class NativeCrashes
    {
        static delegate* unmanaged<char*, uint, int, char*, int> install;
        static delegate* unmanaged<uint, void> exiting;
        static delegate* unmanaged<char*, int> test;

        /// Called on the main thread once the log is open; returns the log line saying what is
        /// recorded. `scrubber`: the log's (null with --log-paths full).
        public static string Install(string logPath, bool dumps, LogScrubber? scrubber)
        {
            if (!OperatingSystem.IsWindows())
                return Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump") == "1"
                    ? $"native crashes: the runtime writes a dump (DOTNET_DbgEnableMiniDump; DOTNET_DbgMiniDumpName {Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName") ?? "unset: /tmp"})"
                    : "native crashes: recorded on Windows only";
            var exe = NativeLibrary.GetMainProgramHandle();
            if (!NativeLibrary.TryGetExport(exe, "moono_crash_install", out var installAt)
                || !NativeLibrary.TryGetExport(exe, "moono_crash_exiting", out var exitingAt)
                || !NativeLibrary.TryGetExport(exe, "moono_crash_test", out var testAt))
                return "native crashes: not recorded (this OniMoono.exe is the .NET SDK's apphost)";
            install = (delegate* unmanaged<char*, uint, int, char*, int>)installAt;
            exiting = (delegate* unmanaged<uint, void>)exitingAt;
            test = (delegate* unmanaged<char*, int>)testAt;
            var prefixes = new StringBuilder();
            if (scrubber != null)
                foreach (var (path, replacement) in HiddenFolders())
                    prefixes.Append(path).Append('\0').Append(replacement).Append('\0');
            prefixes.Append('\0');
            int watching;
            fixed (char* log = Path.GetFullPath(logPath), hidden = prefixes.ToString())
                watching = install(log, GetCurrentThreadId(), dumps ? 1 : 0, hidden);
            // Main's return and Environment.Exit; Player.ProcessExit says so itself.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Exiting(Environment.ExitCode);
            if (Environment.GetEnvironmentVariable("ONI_MOONO_CRASH_WATCH") == "off") return "native crashes: not recorded (ONI_MOONO_CRASH_WATCH=off)";
            return watching != 0
                ? $"native crashes: recorded in this log by a watcher process{(dumps ? ", with a minidump beside it" : " (no minidump: --crash-dumps off)")}"
                : "native crashes: not recorded (the watcher process did not start)";
        }

        /// The folders LogScrubber hides, as paths start with them (module paths, the dump's).
        static IEnumerable<(string Path, string Replacement)> HiddenFolders()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folders = new List<(string, string)>();
            if (documents.Length > 0 && !documents.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
                folders.Add((documents, LogScrubber.DocumentsText));
            if (profile.Length > 0) folders.Add((profile, LogScrubber.ProfileText));
            // The temp folder can name the profile by its short form (C:\Users\ADMINI~1).
            const string tempTail = @"\AppData\Local\Temp\";
            string temp = Path.GetTempPath();
            if (temp.EndsWith(tempTail, StringComparison.OrdinalIgnoreCase) && !temp.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
                folders.Add((temp[..^tempTail.Length], LogScrubber.ProfileText));
            return folders;
        }

        /// The player's own exit, with its exit code: not reported as a crash.
        public static void Exiting(int code)
        {
            if (exiting != null) exiting((uint)code);
        }

        /// --test-native-crash WHERE, once the log is open: a crash on purpose, for the checks
        /// (scripts/check-native-crash.sh). `main` and `worker` fault in native code called from
        /// managed code (the main thread; a managed thread), `thread` and `stack-overflow` on a
        /// native thread, `main-stack-overflow` a native recursion called from the main thread, `fastfail`, `heap`, `gs`, `abort` and `terminate` as named,
        /// `failfast` is Environment.FailFast; `managed-null` is a null reference the runtime turns
        /// into a NullReferenceException that is caught, and the run goes on. On Linux, `main` and
        /// `worker` fault in libc (strlen of address 1); the others are Windows'.
        public static void Test(string what)
        {
            EngineLog.Engine($"test native crash: {what}");
            // No error dialog for the crash on purpose (Windows Error Reporting still has it).
            if (OperatingSystem.IsWindows()) SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
            switch (what)
            {
                case "managed-null":
                    try { Touch(null!); }
                    catch (NullReferenceException) { EngineLog.Engine("test native crash: the null reference was caught"); }
                    return;
                case "failfast":
                    Environment.FailFast("test native crash");
                    return;
                case "main":
                    Fault("access-violation");
                    return;
                case "main-stack-overflow":
                    Fault("recursion");
                    return;
                case "worker":
                    var worker = new Thread(() => Fault("access-violation")) { Name = "Test worker" };
                    worker.Start();
                    worker.Join();
                    return;
                default:
                    Fault(what);
                    return;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Touch(int[] array) => array[0];

        static void Fault(string what)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (what == "access-violation") Strlen(1);
                else EngineLog.Engine($"test native crash: {what} is Windows'");
                return;
            }
            if (test == null)
            {
                EngineLog.Engine("test native crash: OniMoono.exe has no moono_crash_test");
                return;
            }
            fixed (char* name = what) test(name);
        }

        [DllImport("libc", EntryPoint = "strlen")]
        static extern nint Strlen(nint text);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        const uint SemFailCriticalErrors = 1, SemNoGpFaultErrorBox = 2;

        [DllImport("kernel32.dll")]
        static extern uint SetErrorMode(uint mode);
    }
}
