using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OniMoono.Engine.Platform;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// #74: a DllImport of a game plugin binds the game's file in Plugins/ even when another file
    /// of that name sits where the runtime probes first. Here the game's "plugin" is a copy of
    /// zlib, and the stray copy beside the importing assembly (the player's folder, where the
    /// runtime looks before anywhere else) is libm, which lacks zlibVersion, as the older
    /// steam_api64.dll that bound in #74 lacked SteamAPI_ManualDispatch_Init. Linux only: the
    /// libraries are the system's.
    public sealed class GamePluginsTests : IDisposable
    {
        const string Name = "moono_issue74_plugin";

        [DllImport(Name, EntryPoint = "zlibVersion")]
        static extern IntPtr ZlibVersion();

        readonly string root = Directory.CreateTempSubdirectory("oni-moono-plugins-").FullName;
        readonly string stray = Path.Combine(AppContext.BaseDirectory, $"lib{Name}.so");

        public void Dispose()
        {
            Directory.Delete(root, true);
            File.Delete(stray);
        }

        static string SystemLibrary(string file)
        {
            foreach (var dir in new[] { "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib" })
                if (File.Exists(Path.Combine(dir, file))) return Path.Combine(dir, file);
            return null;
        }

        [Fact]
        public void GamePluginWinsOverACopyTheRuntimeFindsFirst()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
            var zlib = SystemLibrary("libz.so.1");
            var libm = SystemLibrary("libm.so.6");
            Assert.NotNull(zlib);
            Assert.NotNull(libm);
            var plugins = Path.Combine(root, GameLayout.DataFolderName, "Plugins");
            Directory.CreateDirectory(plugins);
            var game = Path.Combine(plugins, $"lib{Name}.so");
            File.Copy(zlib, game);
            File.Copy(libm, stray, true);
            var assembly = typeof(GamePluginsTests).Assembly;

            // The runtime's own probing (no resolver): the stray copy, without the export.
            var probed = NativeLibrary.Load(Name, assembly, null);
            Assert.False(NativeLibrary.TryGetExport(probed, "zlibVersion", out _));

            var resolver = new GamePlugins(GameLayout.Find(root), new Dictionary<string, string>(), Path.Combine(root, "none"), NativeExports.X8664);
            Assert.NotEqual(IntPtr.Zero, resolver.Resolve(Name, assembly, null));
            Assert.Equal(IntPtr.Zero, resolver.Resolve("moono_issue74_absent", assembly, null));
            resolver.Register(assembly);
            Assert.StartsWith("1.", Marshal.PtrToStringUTF8(ZlibVersion()));
        }
    }
}
