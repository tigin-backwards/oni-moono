using System;
using System.Runtime.InteropServices;

namespace OniMoono.Host
{
    /// The Windows player is a GUI-subsystem executable (OutputType WinExe), so Steam or Explorer
    /// starting it opens no console window of its own, with a taskbar entry beside the game's.
    /// Started from a terminal (cmd, PowerShell, Windows Terminal), it writes to that terminal's
    /// console instead: the standard streams the parent left unset or pointing at a console are
    /// bound to the parent's console. Streams the parent redirected (a pipe or a file, as WSL
    /// interop and `> out.txt` hand over) are kept as they are.
    internal static class WindowsConsole
    {
        /// Called first thing in Main, before anything touches Console (which reads the standard
        /// handles once, on first use).
        public static void AttachToParent()
        {
            if (!OperatingSystem.IsWindows()) return;
            bool output = Unredirected(StdOutput), error = Unredirected(StdError);
            if (!output && !error) return;
            // AttachConsole sets every standard handle to the console's, the redirected ones too
            // (OniMoono.exe's standard error pipe, NativeCrashes): those are put back.
            IntPtr keptOutput = output ? IntPtr.Zero : GetStdHandle(StdOutput), keptError = error ? IntPtr.Zero : GetStdHandle(StdError);
            if (!AttachConsole(AttachParentProcess)) return; // started without a console (Steam, Explorer)
            if (!output) SetStdHandle(StdOutput, keptOutput);
            if (!error) SetStdHandle(StdError, keptError);
            var conout = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (conout == InvalidHandle) return;
            if (output && !OtherDevice(StdOutput)) SetStdHandle(StdOutput, conout);
            if (error && !OtherDevice(StdError)) SetStdHandle(StdError, conout);
        }

        /// A stream that is missing or a character device: a console handle the parent passed on
        /// (unusable until this process attaches), or NUL. A pipe or a disk file is a redirection.
        static bool Unredirected(int which)
        {
            var handle = GetStdHandle(which);
            return handle == IntPtr.Zero || handle == InvalidHandle || GetFileType(handle) is FileTypeUnknown or FileTypeChar;
        }

        /// Once attached: a character device that isn't the console (`> NUL`), kept as it is.
        static bool OtherDevice(int which)
        {
            var handle = GetStdHandle(which);
            return handle != IntPtr.Zero && handle != InvalidHandle && GetFileType(handle) == FileTypeChar && !GetConsoleMode(handle, out _);
        }

        const int StdOutput = -11, StdError = -12;
        const uint AttachParentProcess = unchecked((uint)-1);
        const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
        const uint FileTypeUnknown = 0, FileTypeChar = 2;
        static readonly IntPtr InvalidHandle = new(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetStdHandle(int which, IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetFileType(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
