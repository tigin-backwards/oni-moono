#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MoonoLoader;
using OniMoono.Host.Install;
using Xunit;

namespace OniMoono.Tests
{
    /// The Workshop item's loader mod (src/MoonoLoader): its Copy button's install, from a fake
    /// item laid out as scripts/workshop-item.py lays it out, is the one Moono's own installer
    /// reads (VersionedInstall); the install folder and the launch-option line are the ones
    /// Moono's installers use.
    public sealed class LoaderTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "oni-moono-loader-" + Guid.NewGuid().ToString("N"));
        string Item => Path.Combine(dir, "item");
        string Root => Path.Combine(dir, "versioned");

        public LoaderTests() => Directory.CreateDirectory(dir);
        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        /// The item's root: moono.json as workshop-item.py writes it, and the Linux player
        /// (the entry file, a library, a file in a folder) with its manifest.
        void FakeItem(string version = "2026.11.10.01-u60", string? tamper = null)
        {
            var platform = Path.Combine(Item, "linux-x64");
            Directory.CreateDirectory(Path.Combine(platform, "player", "packages"));
            var manifest = $"oni-moono-{version}-linux-x64.SHA256SUMS";
            File.WriteAllText(Path.Combine(Item, "mod_info.yaml"), $"minimumSupportedBuild: 762230\nAPIVersion: 2\nversion: \"{version}\"\n");
            File.WriteAllText(Path.Combine(Item, "moono.json"), $$"""
                {
                  "workshopItem": "oni-moono",
                  "version": "{{version}}",
                  "tag": "v2026.11.10.01",
                  "branch": "u60",
                  "repository": "tigin-backwards/oni-moono",
                  "platforms": {
                    "windows-x64": {
                      "folder": "windows-x64",
                      "manifest": "oni-moono-{{version}}-windows-x64.SHA256SUMS",
                      "archive": "oni-moono-{{version}}-windows-x64.zip"
                    },
                    "linux-x64": {
                      "folder": "linux-x64",
                      "manifest": "{{manifest}}",
                      "archive": "oni-moono-{{version}}-linux-x64.tar.gz"
                    }
                  }
                }

                """);
            File.WriteAllText(Path.Combine(platform, "player", "OniMoono"), "#!/bin/sh\necho " + version + "\n");
            File.WriteAllText(Path.Combine(platform, "player", "OniMoono.dll"), "dll");
            File.WriteAllText(Path.Combine(platform, "player", "packages", "UnityEngine.UI.dll"), "ui");
            File.WriteAllText(Path.Combine(platform, "setup.sh"), "setup");
            var lines = Directory.GetFiles(platform, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(platform, f).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(platform, p))))}  {p}\n");
            File.WriteAllText(Path.Combine(Item, manifest), string.Concat(lines));
            if (tamper != null) File.WriteAllText(Path.Combine(platform, "player", tamper), "changed");
        }

        static Func<string, string?> Env(params (string Name, string Value)[] values) =>
            name => values.FirstOrDefault(v => v.Name == name).Value;

        [Fact]
        public void InstallFolderIsTheInstallersOne()
        {
            Assert.Equal("/set", ItemInstall.DefaultRoot("/any", false, Env(("ONI_MOONO_INSTALL_ROOT", "/set"), ("HOME", "/h")), ""));
            Assert.Equal(Path.Combine("C:\\Users\\a\\AppData\\Local", "oni-moono", "versioned"), ItemInstall.DefaultRoot("D:\\Steam\\item", true, Env(), "C:\\Users\\a\\AppData\\Local"));
            Assert.Equal("/h/.local/share/oni-moono/versioned", ItemInstall.DefaultRoot("/h/.local/share/Steam/steamapps/workshop/content/457140/1", false, Env(("HOME", "/h")), ""));
            Assert.Equal("/x/oni-moono/versioned", ItemInstall.DefaultRoot("/h/.steam/steam/item", false, Env(("HOME", "/h"), ("XDG_DATA_HOME", "/x")), ""));
            // A sandboxed Steam's own folder, as ci/workshop/OniMoono.sh picks it.
            Assert.Equal("/h/.var/app/com.valvesoftware.Steam/data/oni-moono/versioned",
                ItemInstall.DefaultRoot("/h/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/workshop/content/457140/1", false, Env(("HOME", "/h"), ("XDG_DATA_HOME", "/elsewhere")), ""));
            Assert.Equal("/h/snap/steam/common/oni-moono/versioned", ItemInstall.DefaultRoot("/h/snap/steam/common/.local/share/Steam/item", false, Env(("HOME", "/h")), ""));
        }

        [Fact]
        public void LaunchOptionStartsTheEntryPoint()
        {
            Assert.Equal($"\"{Root}/OniMoono\" -- %command%", new ItemInstall(Item, Root, windows: false).LaunchOption);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}OniMoono.exe\" -- %command%", new ItemInstall(Item, Root, windows: true).LaunchOption);
        }

        [Fact]
        public void ItemRootOfAnArchivedVersion()
        {
            Assert.Equal(Item, ItemInstall.ItemRootOf(Item));
            Assert.Equal(Item, ItemInstall.ItemRootOf(Path.Combine(Item, "archived_versions", "u59")));
        }

        [Fact]
        public void ReadsMoonoJsonAsWorkshopItemPyWritesIt()
        {
            FakeItem();
            var info = ItemInstall.ReadInfo(Item, "linux-x64", out var problem);
            Assert.Null(problem);
            Assert.Equal("2026.11.10.01-u60", info!.Version);
            Assert.Equal("linux-x64", info.Folder);
            Assert.Equal("oni-moono-2026.11.10.01-u60-linux-x64.SHA256SUMS", info.Manifest);
            Assert.Null(ItemInstall.ReadInfo(Item, "osx-x64", out problem));
            Assert.Contains("no osx-x64 player", problem);
            File.WriteAllText(Path.Combine(Item, "moono.json"), "{\"version\": \"1\"}");
            Assert.Null(ItemInstall.ReadInfo(Item, "linux-x64", out problem));
        }

        [Fact]
        public void CopyInstallsWhatMoonosInstallerReads()
        {
            FakeItem();
            var install = new ItemInstall(Item, Root, windows: false);
            var executable = new List<string>();
            Assert.Equal($"Moono 2026.11.10.01-u60 installed in {Root}", install.Install(4242, executable.Add));
            var version = Path.Combine(Root, "player", "2026.11.10.01-u60");
            Assert.Equal("dll", File.ReadAllText(Path.Combine(version, "OniMoono.dll")));
            Assert.True(File.Exists(Path.Combine(version, "packages", "UnityEngine.UI.dll")));
            Assert.False(File.Exists(Path.Combine(version, "setup.sh")));
            Assert.Equal(PlayerFiles.EntryScript, File.ReadAllText(Path.Combine(Root, "OniMoono")));
            // Made executable as copied, in the folder moved in place after.
            Assert.Contains(Path.Combine(Root, "player", ".incoming-2026.11.10.01-u60-4242", "OniMoono"), executable);
            Assert.Equal(2, executable.Count);
            Assert.Contains(Path.Combine(Root, "OniMoono.new"), executable);
            Assert.Equal(Item + "\n", File.ReadAllText(Path.Combine(Root, "state", "source")));
            Assert.Equal("2026.11.10.01-u60\n", File.ReadAllText(Path.Combine(Root, "state", "current")));
            // Moono's own installer sees the version, the item as its source, and its game build's choice.
            var moono = new VersionedInstall(Root, "linux-x64", _ => { });
            Assert.Equal("2026.11.10.01-u60", moono.Choose(762230, new HashSet<string>())!.Name);
            Assert.Equal(Item, moono.ReadState("source"));
            Assert.Equal(VersionedInstall.SourceResult.AlreadyInstalled, moono.InstallFrom(Item, 762230, new HashSet<string>(), out _));
            // A second Copy leaves the version as it is.
            Assert.Equal($"Moono 2026.11.10.01-u60 is installed in {Root}", install.Install(4242, null));
        }

        [Fact]
        public void TamperedPlayerIsNotInstalled()
        {
            FakeItem(tamper: "OniMoono.dll");
            var e = Assert.Throws<ItemInstall.Failure>(() => new ItemInstall(Item, Root, windows: false).Install(4242, null));
            Assert.Contains("player/OniMoono.dll doesn't match the manifest", e.Message);
            Assert.Empty(Directory.GetDirectories(Path.Combine(Root, "player")));
            Assert.False(File.Exists(Path.Combine(Root, "OniMoono")));
        }

        [Fact]
        public void WindowsEntryPointIsTheVersionsExe()
        {
            FakeItem();
            var platform = Path.Combine(Item, "windows-x64");
            Directory.CreateDirectory(Path.Combine(platform, "player"));
            File.WriteAllText(Path.Combine(platform, "player", "OniMoono.exe"), "MZ new");
            var manifest = Path.Combine(Item, "oni-moono-2026.11.10.01-u60-windows-x64.SHA256SUMS");
            File.WriteAllText(manifest, $"{Convert.ToHexStringLower(SHA256.HashData("MZ new"u8.ToArray()))}  player/OniMoono.exe\n");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "OniMoono.exe"), "MZ old");
            new ItemInstall(Item, Root, windows: true).Install(1, null);
            Assert.Equal("MZ new", File.ReadAllText(Path.Combine(Root, "OniMoono.exe")));
            Assert.Equal("MZ old", File.ReadAllText(Path.Combine(Root, "OniMoono.exe.old")));
        }
    }
}
