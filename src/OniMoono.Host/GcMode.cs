#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OniMoono.Host
{
    /// The garbage collection option (the Moono Tweaks screen's "Garbage collection" row,
    /// settings.json's "gc", `--gc fast|low-memory`). The runtime fixes its GC when it starts,
    /// before any of our code runs, so a choice applies from the next launch:
    ///
    /// - fast (the default): Server GC (OniMoono.Host.csproj's ServerGarbageCollection), with
    ///   DATAS, .NET's dynamic adaptation of the heap count, on by default for Server GC since
    ///   .NET 9. Collections run on every core: short pauses, more memory.
    /// - low-memory: Workstation GC, concurrent (ConcurrentGarbageCollection): one heap, less
    ///   memory, pauses a little longer or more often.
    ///
    /// Windows: OniMoono.exe (src/OniMoono.NativeHost) reads the choice itself (the command line,
    /// else the settings file) and sets the runtime property System.GC.Server to false before the
    /// runtime starts. Linux and macOS start through the SDK's apphost, which reads only the
    /// runtimeconfig: there the host, finding the runtime's GC not the one asked for, starts
    /// itself again in place (execve, the same process) with DOTNET_gcServer=0, which the runtime
    /// reads over the runtimeconfig, before the log opens. ONI_MOONO_GC_RELAUNCH names the choice
    /// that run was started for; the run removes both variables from its environment, so a run
    /// App.Restart starts decides again. A DOTNET_gcServer (or COMPlus_gcServer) the user set wins
    /// over the option, and the log says so.
    internal static class GcMode
    {
        public const string Fast = "fast", LowMemory = "low-memory";
        public static readonly string[] Choices = [Fast, LowMemory];
        public const string Default = Fast;
        public static bool IsChoice(string value) => Array.IndexOf(Choices, value) >= 0;
        public static bool WantsServer(string choice) => choice != LowMemory;

        /// Set in the environment of the run started again for a choice (its value: the choice).
        public const string RelaunchVariable = "ONI_MOONO_GC_RELAUNCH";
        public const string ServerVariable = "DOTNET_gcServer";
        static readonly string[] ServerVariables = [ServerVariable, "COMPlus_gcServer"];

        public enum Outcome
        {
            /// The runtime's GC is the one asked for (as started, or set by OniMoono.exe).
            Applied,
            /// Started again for this choice (Linux, macOS), and it took.
            Relaunched,
            /// To start again with Workstation or Server GC (Linux, macOS).
            Relaunch,
            /// A DOTNET_gcServer of the user's chose the GC.
            Environment,
            /// Not the one asked for, and nothing more to do (Windows: an exe that does not set it).
            NotApplied,
        }

        /// What to do, from the choice, the runtime's GC and the environment.
        public static Outcome Plan(string choice, bool isServer, Func<string, string?> getenv, bool canRelaunch)
        {
            bool wanted = WantsServer(choice);
            if (getenv(RelaunchVariable) != null) return isServer == wanted ? Outcome.Relaunched : Outcome.NotApplied;
            if (UserVariable(getenv) != null) return Outcome.Environment;
            if (isServer == wanted) return Outcome.Applied;
            return canRelaunch ? Outcome.Relaunch : Outcome.NotApplied;
        }

        /// The user's DOTNET_gcServer or COMPlus_gcServer ("NAME=VALUE"), if any.
        public static string? UserVariable(Func<string, string?> getenv)
        {
            if (getenv(RelaunchVariable) != null) return null;
            foreach (var name in ServerVariables)
                if (getenv(name) is { } value) return name + "=" + value;
            return null;
        }

        /// The GC as the runtime has it, by its technical name: "Server GC with DATAS",
        /// "Workstation concurrent GC" and the like. `config`: GC.GetConfigurationVariables().
        public static string Describe(bool isServer, IReadOnlyDictionary<string, object> config)
        {
            bool concurrent = config.TryGetValue("ConcurrentGC", out var c) && c is true;
            if (!isServer) return concurrent ? "Workstation concurrent GC" : "Workstation non-concurrent GC";
            long datas = config.TryGetValue("GCDynamicAdaptationMode", out var d) ? Convert.ToInt64(d) : 0;
            string heaps = config.TryGetValue("HeapCount", out var h) ? Convert.ToString(h, System.Globalization.CultureInfo.InvariantCulture)! : "?";
            return (datas == 1 ? $"Server GC with DATAS (up to {heaps} heaps)" : $"Server GC with {heaps} heaps (DATAS off)")
                   + (concurrent ? ", concurrent" : ", non-concurrent");
        }

        /// The log's line: the GC, then the choice, where it came from and what was done.
        public static string LogLine(string described, string choice, string from, Outcome outcome, Func<string, string?> getenv) => outcome switch
        {
            Outcome.Applied => $"gc: {described}: the gc option is {choice} ({from})",
            Outcome.Relaunched => $"gc: {described}: the gc option is {choice} ({from}); started again with {ServerVariable}=0 to apply it",
            Outcome.Environment => $"gc: {described}: {UserVariable(getenv)} in the environment wins over the gc option ({choice}, {from})",
            Outcome.NotApplied => $"gc: {described}: the gc option is {choice} ({from}) but could not be applied"
                                  + (OperatingSystem.IsWindows() ? " (OniMoono.exe did not set it: an old or replaced executable?)" : ""),
            _ => $"gc: {described}: the gc option is {choice} ({from}); starting again to apply it",
        };

        /// The runtime's GC settings in one line, for the log.
        public static string ConfigLine(IReadOnlyDictionary<string, object> config)
            => "gc config: " + string.Join(", ", config.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)));

        /// The environment of the run started again for `choice`: this one's, with the GC
        /// variable and the marker.
        public static string[] RelaunchEnvironment(System.Collections.IDictionary current, string choice)
        {
            var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in current)
                if (entry.Key is string name && entry.Value is string value) variables[name] = value;
            variables[ServerVariable] = WantsServer(choice) ? "1" : "0";
            variables[RelaunchVariable] = choice;
            return variables.Select(p => p.Key + "=" + p.Value).ToArray();
        }

        /// The run started again for a choice: the variables it was started with taken out of the
        /// environment, so that processes it starts (App.Restart's) choose for themselves.
        public static void ForgetRelaunch()
        {
            if (Environment.GetEnvironmentVariable(RelaunchVariable) == null) return;
            Environment.SetEnvironmentVariable(RelaunchVariable, null);
            Environment.SetEnvironmentVariable(ServerVariable, null);
            if (!OperatingSystem.IsWindows())
            {
                unsetenv(RelaunchVariable);
                unsetenv(ServerVariable);
            }
        }

        /// Linux and macOS: this process replaced by the same executable with the same arguments,
        /// with the GC variable set (execve). Returns only when that fails, with the reason.
        public static string Relaunch(string[] args, string choice)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "the executable's path is unknown";
            var argv = new List<string> { exe };
            // Started as `dotnet OniMoono.dll`: the dll again after the dotnet host.
            if (System.IO.Path.GetFileNameWithoutExtension(exe) == "dotnet") argv.Add(typeof(GcMode).Assembly.Location);
            argv.AddRange(args);
            var envp = RelaunchEnvironment(Environment.GetEnvironmentVariables(), choice);
            // NUL-terminated UTF-8 strings in NULL-terminated arrays; freed only if execve fails.
            static IntPtr[] Native(IReadOnlyList<string> strings)
            {
                var native = new IntPtr[strings.Count + 1];
                for (int i = 0; i < strings.Count; i++) native[i] = Marshal.StringToCoTaskMemUTF8(strings[i]);
                return native;
            }
            var nativeArgv = Native(argv);
            var nativeEnvp = Native(envp);
            Console.Out.Flush();
            Console.Error.Flush();
            execve(exe, nativeArgv, nativeEnvp);
            int errno = Marshal.GetLastPInvokeError();
            foreach (var p in nativeArgv) Marshal.FreeCoTaskMem(p);
            foreach (var p in nativeEnvp) Marshal.FreeCoTaskMem(p);
            return "execve failed: errno " + errno;
        }

        [DllImport("libc", SetLastError = true)]
        static extern int execve([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr[] argv, IntPtr[] envp);

        [DllImport("libc", SetLastError = true)]
        static extern int unsetenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    }
}
