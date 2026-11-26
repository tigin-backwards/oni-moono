using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OniMoono.Desktop;
using Xunit;

namespace OniMoono.Tests
{
    /// A failed RandR request does not end the process (#129): Xlib's default handler would print the
    /// error and exit(1), as for a RandR output gone before GLFW saw it go.
    public class X11ErrorsTests
    {
        [Fact]
        public void A_request_for_a_removed_output_fails_without_ending_the_process()
        {
            if (!OperatingSystem.IsLinux()) Assert.Skip("X11 is Linux's.");
            if (!File.Exists("/usr/bin/Xvfb")) Assert.Skip("needs Xvfb");
            // An Xvfb of its own, on a display above the e2e lanes' (:300 and up).
            var (xvfb, display) = StartXvfb();
            try
            {
                try
                {
                    X11Errors.Install(display);
                    int before = X11Errors.Count;
                    var resources = XRRGetScreenResourcesCurrent(display, XDefaultRootWindow(display));
                    Assert.NotEqual(0, resources);
                    // An output id the server never made, as one it has removed: BadRROutput.
                    var info = XRRGetOutputInfo(display, resources, 0x7ffff0);
                    XRRFreeScreenResources(resources);
                    Assert.Equal(0, info);
                    Assert.Equal(before + 1, X11Errors.Count);
                }
                finally
                {
                    XCloseDisplay(display);
                }
            }
            finally
            {
                xvfb.Kill();
                xvfb.WaitForExit();
            }
        }

        static (Process, nint) StartXvfb()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var name = ":" + Random.Shared.Next(500, 600);
                var xvfb = Process.Start(new ProcessStartInfo("/usr/bin/Xvfb", name + " -screen 0 640x480x24 -nolisten tcp")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                // Its output read and dropped: a full pipe would stop it.
                xvfb.OutputDataReceived += (_, _) => { };
                xvfb.ErrorDataReceived += (_, _) => { };
                xvfb.BeginOutputReadLine();
                xvfb.BeginErrorReadLine();
                for (int wait = 0; wait < 100 && !xvfb.HasExited; wait++)
                {
                    var display = XOpenDisplay(name);
                    if (display != 0) return (xvfb, display);
                    System.Threading.Thread.Sleep(50);
                }
                if (!xvfb.HasExited) xvfb.Kill();
                xvfb.WaitForExit();
            }
            throw new InvalidOperationException("no Xvfb started");
        }

        [Fact]
        public void Only_RandR_errors_are_taken()
        {
            Assert.True(X11Errors.Handles(140, 140));
            Assert.False(X11Errors.Handles(14, 140));  // X_GetGeometry: Xlib's default handler, as before
            Assert.False(X11Errors.Handles(140, -1)); // no RandR on the display
        }

        [Fact]
        public void An_error_is_logged_with_its_codes_and_resource()
        {
            Assert.Equal("x11: X error 147 on RandR request 140.9 (resource 0x7ffff0), ignored (a monitor changed before GLFW saw it)",
                X11Errors.Describe(147, 140, 9, 0x7ffff0, 1));
            Assert.EndsWith("; further ones are counted, not logged", X11Errors.Describe(147, 140, 9, 0x7ffff0, 20));
        }

        [DllImport("libX11.so.6")]
        static extern nint XOpenDisplay(string name);

        [DllImport("libX11.so.6")]
        static extern int XCloseDisplay(nint display);

        [DllImport("libX11.so.6")]
        static extern nuint XDefaultRootWindow(nint display);

        [DllImport("libXrandr.so.2")]
        static extern nint XRRGetScreenResourcesCurrent(nint display, nuint window);

        [DllImport("libXrandr.so.2")]
        static extern nint XRRGetOutputInfo(nint display, nint resources, nuint output);

        [DllImport("libXrandr.so.2")]
        static extern void XRRFreeScreenResources(nint resources);
    }
}
