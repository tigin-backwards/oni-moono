using System;
using System.IO;
using OniMoono.Engine.Platform;
using Xunit;

namespace OniMoono.Tests
{
    /// D59: Mono's DllNotFoundException text, as Unity's Linux player (761211) gave it for an absent
    /// and an unreadable libSimDLL.so (e2e u60/p1-sim-blocked), and as U60's crash reporter matches it.
    public sealed class MonoDllNotFoundTests : IDisposable
    {
        readonly string root = Directory.CreateTempSubdirectory("oni-moono-dllnotfound-").FullName;

        public void Dispose() => Directory.Delete(root, true);

        [Fact]
        public void A_missing_library_is_named_as_the_DllImport_names_it()
        {
            var e = MonoDllNotFound.Missing("SimDLL");
            Assert.Equal("SimDLL assembly:<unknown assembly> type:<unknown type> member:(null)", e.Message);
            // KCrashReporter.IsOperatingSystemBlockedError's test, on the exception's text.
            Assert.Contains("DllNotFoundException: SimDLL", e.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_unreadable_plugin_gives_the_loaders_error()
        {
            if (OperatingSystem.IsWindows()) return;
            var path = Path.Combine(root, "libSimDLL.so");
            File.WriteAllBytes(path, new byte[64]);
            File.SetUnixFileMode(path, UnixFileMode.None);
            var e = MonoDllNotFound.Failed("SimDLL", path);
            Assert.Equal(path + ": cannot open shared object file: Permission denied"
                         + " assembly:<unknown assembly> type:<unknown type> member:(null)", e.Message);
            Assert.DoesNotContain("DllNotFoundException: SimDLL", e.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_unreadable_plugin_is_taken_to_have_the_architecture()
        {
            if (OperatingSystem.IsWindows()) return;
            var path = Path.Combine(root, "libSimDLL.so");
            File.WriteAllBytes(path, new byte[64]);
            File.SetUnixFileMode(path, UnixFileMode.None);
            Assert.True(GameLayout.HasArchitecture(path, NativeExports.X8664));
        }
    }
}
