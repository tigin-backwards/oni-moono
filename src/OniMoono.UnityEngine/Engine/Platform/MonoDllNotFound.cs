using System;
using System.Runtime.InteropServices;

namespace OniMoono.Engine.Platform
{
    /// ARCHITECTURE D59. The DllNotFoundException a game or mod P/Invoke throws when its native
    /// library does not load, with the message Unity's Mono gives it. CoreCLR's reads "Unable to
    /// load shared library 'SimDLL' or one of its dependencies…"; the game looks for Mono's text:
    /// U60's KCrashReporter.IsOperatingSystemBlockedError matches "DllNotFoundException: SimDLL" in
    /// the exception's ToString to show its "OS Error" popup (u60-qa-spec.md P1).
    ///
    /// Measured in Unity's Linux player (761211): with libSimDLL.so absent, the message is the
    /// DllImport's library name and Mono's three placeholders, "SimDLL assembly:<unknown assembly>
    /// type:<unknown type> member:(null)"; with the file present but unreadable, the dynamic
    /// loader's error stands for the name, "/…/libSimDLL.so: cannot open shared object file:
    /// Permission denied assembly:…". On Windows the message is taken to be the name's form in both
    /// cases, as Klei's matcher for a blocked SimDLL.dll assumes (not yet measured there).
    internal static class MonoDllNotFound
    {
        const string Placeholders = " assembly:<unknown assembly> type:<unknown type> member:(null)";

        /// Mono's message for library `name` (a DllImport's) whose loading failed; `loaderError`
        /// is the dynamic loader's error for the file that was found, null when none was.
        public static string Message(string name, string loaderError) =>
            (string.IsNullOrEmpty(loaderError) ? name : loaderError) + Placeholders;

        /// The exception for library `name` when no file of it was found.
        public static DllNotFoundException Missing(string name) => new(Message(name, null));

        /// The exception for library `name` whose file at `path` was found but did not load.
        public static DllNotFoundException Failed(string name, string path) =>
            new(Message(name, OperatingSystem.IsWindows() ? null : LoaderError(path)));

        /// What dlopen reports for the file at `path` (dlerror), null if it loads after all.
        public static string LoaderError(string path)
        {
            if (OperatingSystem.IsWindows()) return null;
            try
            {
                // A first call binds dlerror (its own dlsym would clear the error dlopen leaves) and
                // clears any earlier error.
                _ = OperatingSystem.IsMacOS() ? MacDl.dlerror() : LinuxDl.dlerror();
                var handle = OperatingSystem.IsMacOS() ? MacDl.dlopen(path, RtldLazy) : LinuxDl.dlopen(path, RtldLazy);
                if (handle != IntPtr.Zero)
                {
                    if (OperatingSystem.IsMacOS()) MacDl.dlclose(handle); else LinuxDl.dlclose(handle);
                    return null;
                }
                return Marshal.PtrToStringUTF8(OperatingSystem.IsMacOS() ? MacDl.dlerror() : LinuxDl.dlerror());
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        const int RtldLazy = 1;

        static class LinuxDl
        {
            [DllImport("libdl.so.2")] public static extern IntPtr dlopen(string path, int flags);
            [DllImport("libdl.so.2")] public static extern IntPtr dlerror();
            [DllImport("libdl.so.2")] public static extern int dlclose(IntPtr handle);
        }

        static class MacDl
        {
            [DllImport("/usr/lib/libSystem.B.dylib")] public static extern IntPtr dlopen(string path, int flags);
            [DllImport("/usr/lib/libSystem.B.dylib")] public static extern IntPtr dlerror();
            [DllImport("/usr/lib/libSystem.B.dylib")] public static extern int dlclose(IntPtr handle);
        }
    }
}
