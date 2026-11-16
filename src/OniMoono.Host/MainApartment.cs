using System;
using System.Runtime.InteropServices;

namespace OniMoono.Host
{
    /// On Windows the main thread, which owns the game window, is a single-threaded COM
    /// apartment, as in Unity's player (issue #30; measured with CoGetApartmentType on the
    /// game's thread: APTTYPE_MAINSTA in the original, APTTYPE_MTA in ours, as .NET makes the
    /// thread of a Main without [STAThread]). Text Services Framework, through which Microsoft
    /// Pinyin and the other current IMEs take keys, is apartment-threaded (msctf.dll's thread
    /// manager and profile objects are registered ThreadingModel=Apartment). With ours MTA,
    /// Chinese players reported Pinyin composing the game's keys (W, A, S, D) outside text
    /// fields, its candidate window never shown, although the window has no input context there
    /// (Win32Ime), as in Unity's player, which takes no IME input. Not yet confirmed with a real
    /// keyboard: Text Services Framework takes no keys for a window that is not in the
    /// foreground, so test runs cannot show it.
    ///
    /// The thread's COM initialization is swapped before anything uses COM: the runtime's MTA
    /// one undone (CoUninitialize), and the thread initialized again as an STA. The runtime
    /// still counts the thread as MTA (Thread.GetApartmentState), so its waits stay plain
    /// kernel waits, as Unity's Mono's are: with [STAThread] every lock, Monitor and WaitHandle
    /// wait on the main thread would run COM's modal loop, dispatching messages mid-frame.
    /// ONI_MAIN_APARTMENT=mta leaves the thread as .NET made it (for comparing the two).
    internal static class MainApartment
    {
        const uint COINIT_MULTITHREADED = 0x0, COINIT_APARTMENTTHREADED = 0x2;

        /// Called first thing in Main. What happened, for the log.
        public static string? MakeSingleThreaded()
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (string.Equals(Environment.GetEnvironmentVariable("ONI_MAIN_APARTMENT"), "mta", StringComparison.OrdinalIgnoreCase))
                return "main thread: MTA (ONI_MAIN_APARTMENT=mta)";
            CoUninitialize();
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            if (hr >= 0) return "main thread: single-threaded COM apartment";
            // Something else holds the thread's MTA (its CoInitializeEx is still counted): back to MTA.
            CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            return $"main thread: stays MTA (CoInitializeEx STA: 0x{hr:X8})";
        }

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr reserved, uint flags);

        [DllImport("ole32.dll")]
        static extern void CoUninitialize();
    }
}
