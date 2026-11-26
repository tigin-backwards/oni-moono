using System;
using System.Runtime.InteropServices;
using System.Threading;
using OniMoono.Desktop.D3d11;

namespace OniMoono.Desktop
{
    /// Xlib's error handlers for the process. Xlib's default handler for a protocol error prints
    /// it and calls exit(1), so one failed request ends the player. A RandR request can fail
    /// whenever a monitor is unplugged, sleeps or changes: one naming an output or CRTC the
    /// server has just removed, before GLFW has handled the server's RRNotify for it
    /// (ScreenDpi.ReadX11's XRRGetOutputInfo, GLFW's XRRGetCrtcInfo for a monitor's mode or
    /// position; #129). Ours logs such an error and goes on: the request returns its failure
    /// (XRRGetOutputInfo and XRRGetCrtcInfo NULL, which ScreenDpi and GLFW's mode and position
    /// queries check). Other errors go to the handler before ours (Xlib's default, which ends
    /// the process, as before). GLFW's own handler, which it puts in around requests whose
    /// errors it reads, saves and puts back the one before it, so ours stays.
    ///
    /// A lost connection (the X server or XWayland gone) still ends the process, as Xlib does
    /// after any IO error handler returns; ours logs it first, so the log says why it ended.
    ///
    /// Unity's player sets its own handlers too (UnityPlayer.so looks up XSetErrorHandler and
    /// XSetIOErrorHandler); what they do is unmeasured.
    internal static unsafe class X11Errors
    {
        /// The errors logged one by one; after them, only how many.
        const int Logged = 20;

        static int installed, count, randrOpcode = -1;

        /// The handler before ours (Xlib's default), for the errors ours does not take.
        [NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        static delegate* unmanaged<nint, XErrorEvent*, int> previous;

        /// The RandR errors handled so far.
        internal static int Count => Volatile.Read(ref count);

        /// Installs the handlers, once a process, for `display`'s RandR requests.
        internal static void Install(nint display)
        {
            if (display == 0 || Interlocked.Exchange(ref installed, 1) != 0) return;
            if (XQueryExtension(display, "RANDR", out var opcode, out _, out _) != 0) randrOpcode = opcode;
            previous = XSetErrorHandler(&OnError);
            XSetIOErrorHandler(&OnIOError);
        }

        /// Whether an error is one ours takes: a RandR request's.
        internal static bool Handles(byte requestCode, int randr) => randr > 0 && requestCode == randr;

        /// One error's log line.
        internal static string Describe(byte errorCode, byte requestCode, byte minorCode, nuint resource, int seen)
            => $"x11: X error {errorCode} on RandR request {requestCode}.{minorCode} (resource 0x{resource:x}), ignored (a monitor changed before GLFW saw it)"
                + (seen == Logged ? "; further ones are counted, not logged" : "");

        [UnmanagedCallersOnly]
        static int OnError(nint display, XErrorEvent* e)
        {
            if (!Handles(e->RequestCode, randrOpcode)) return previous != null ? previous(display, e) : 0;
            try
            {
                int seen = Interlocked.Increment(ref count);
                if (seen <= Logged) OniMoono.Engine.EngineLog.Engine(Describe(e->ErrorCode, e->RequestCode, e->MinorCode, e->ResourceId, seen));
            }
            catch { }
            return 0;
        }

        [UnmanagedCallersOnly]
        static int OnIOError(nint display)
        {
            try
            {
                OniMoono.Engine.EngineLog.Engine("x11: the connection to the X server is gone; Xlib ends the process");
            }
            catch { }
            return 0;
        }

        /// XErrorEvent (Xlib.h).
        [StructLayout(LayoutKind.Sequential)]
        [NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        internal struct XErrorEvent
        {
            public int Type;
            public nint Display;
            public nuint ResourceId;
            public nuint Serial;
            public byte ErrorCode, RequestCode, MinorCode;
        }

        [NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        [DllImport("libX11.so.6")]
        static extern delegate* unmanaged<nint, XErrorEvent*, int> XSetErrorHandler(delegate* unmanaged<nint, XErrorEvent*, int> handler);

        [NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        [DllImport("libX11.so.6")]
        static extern nint XSetIOErrorHandler(delegate* unmanaged<nint, int> handler);

        [NotNative("a Linux library (libX11, libXrandr, libxkbcommon, libc), not in the Windows SDK")]
        [DllImport("libX11.so.6")]
        static extern int XQueryExtension(nint display, [MarshalAs(UnmanagedType.LPStr)] string name, out int majorOpcode, out int firstEvent, out int firstError);
    }
}
