using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Platform;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // PlayerPrefs in Unity's stores: the Windows registry's encoding (over an in-memory key), the
    // store each run uses, and the import of Moono's old prefs file.
    public class PlayerPrefsStoreTests
    {
        // An in-memory registry: keys by path, each a list of (value name, type, data).
        sealed class FakeRegistry
        {
            public readonly Dictionary<string, List<(string Name, uint Type, byte[] Data)>> Keys = new(StringComparer.OrdinalIgnoreCase);

            public RegistryPrefsStore.IKey Open(string path, bool write)
            {
                if (!Keys.TryGetValue(path, out var values))
                {
                    if (!write) return null;
                    Keys[path] = values = new();
                }
                return new Key(values);
            }

            sealed class Key(List<(string Name, uint Type, byte[] Data)> values) : RegistryPrefsStore.IKey
            {
                public List<(string Name, uint Type, byte[] Data)> Values() => values.ToList();

                public void Set(string valueName, uint type, byte[] data)
                {
                    Delete(valueName);
                    values.Add((valueName, type, data));
                }

                // Registry value names ignore case.
                public void Delete(string valueName) => values.RemoveAll(v => string.Equals(v.Name, valueName, StringComparison.OrdinalIgnoreCase));

                public void Dispose() { }
            }
        }

        const string Key = @"Software\Klei\Oxygen Not Included";

        static void WithStore(FakeRegistry registry, Action<string> test, string oldFile = null)
        {
            var dir = Directory.CreateTempSubdirectory("oni-moono-regprefs-").FullName;
            var saved = EngineConfig.PersistentDataPath;
            try
            {
                EngineConfig.PersistentDataPath = dir;
                if (oldFile != null) File.WriteAllText(Path.Combine(dir, "prefs"), "<unity_prefs version_major=\"1\" version_minor=\"1\">\n" + oldFile + "</unity_prefs>");
                PlayerPrefs.Reload(new RegistryPrefsStore(Key, registry.Open));
                test(dir);
            }
            finally
            {
                EngineConfig.PersistentDataPath = saved;
                PlayerPrefs.Reload();
                Directory.Delete(dir, true);
            }
        }

        static string Hex(byte[] data) => Convert.ToHexString(data);

        [Fact]
        public void ValueNamesCarryUnitysHash()
        {
            // As the original wrote them (HKCU\Software\Klei\Oxygen Not Included).
            Assert.Equal("Screenmanager Fullscreen mode_h3630240806", RegistryPrefsStore.ValueName("Screenmanager Fullscreen mode"));
            Assert.Equal("Screenmanager Resolution Width_h182942802", RegistryPrefsStore.ValueName("Screenmanager Resolution Width"));
            Assert.Equal("Screenmanager Resolution Height Default_h1380706816", RegistryPrefsStore.ValueName("Screenmanager Resolution Height Default"));
            Assert.Equal("unity.player_session_count_h922449978", RegistryPrefsStore.ValueName("unity.player_session_count"));
            Assert.Equal("UnitySelectMonitor_h17969598", RegistryPrefsStore.ValueName("UnitySelectMonitor"));
            Assert.Equal("Screenmanager Window Position X", RegistryPrefsStore.NameOf("Screenmanager Window Position X_h4088080503"));
            Assert.Null(RegistryPrefsStore.NameOf("Screenmanager Window Position X_h4088080502"));
            Assert.Null(RegistryPrefsStore.NameOf("Plain value"));
            Assert.Null(RegistryPrefsStore.NameOf("x_h+177678"));
            Assert.Equal("a_hb", RegistryPrefsStore.NameOf(RegistryPrefsStore.ValueName("a_hb")));
        }

        [Fact]
        public void ValuesAreEncodedAsTheOriginalWritesThem()
        {
            var (type, data) = RegistryPrefsStore.Encode(new PrefsEntry(PrefsKind.Int, 1280));
            Assert.Equal((RegistryPrefsStore.RegDword, "00050000"), (type, Hex(data)));
            (type, data) = RegistryPrefsStore.Encode(new PrefsEntry(PrefsKind.String, "10"));
            Assert.Equal((RegistryPrefsStore.RegBinary, "313000"), (type, Hex(data)));
            (type, data) = RegistryPrefsStore.Encode(new PrefsEntry(PrefsKind.String, ""));
            Assert.Equal((RegistryPrefsStore.RegBinary, "00"), (type, Hex(data)));
            (type, data) = RegistryPrefsStore.Encode(new PrefsEntry(PrefsKind.Float, 1.5f));
            Assert.Equal((RegistryPrefsStore.RegDword, "000000000000F83F"), (type, Hex(data)));
        }

        [Theory]
        [InlineData(0, -42)]
        [InlineData(0, int.MaxValue)]
        [InlineData(1, 1f / 3f)]
        [InlineData(1, -1e-30f)]
        [InlineData(2, "héllo, <x> & \"y\"\nline2")]
        [InlineData(2, "")]
        public void ValuesRoundTrip(int kindNumber, object value)
        {
            var kind = (PrefsKind)kindNumber;
            var (type, data) = RegistryPrefsStore.Encode(new PrefsEntry(kind, value));
            var back = RegistryPrefsStore.Decode(type, data);
            Assert.Equal(kind, back?.Kind);
            Assert.Equal(value, back?.Value);
        }

        [Fact]
        public void OtherValueTypesAreNotPrefs()
        {
            Assert.Null(RegistryPrefsStore.Decode(1, new byte[] { 0x41, 0, 0, 0 })); // REG_SZ
            Assert.Null(RegistryPrefsStore.Decode(RegistryPrefsStore.RegDword, new byte[] { 1, 2 }));
            Assert.Equal("ab", RegistryPrefsStore.Decode(RegistryPrefsStore.RegBinary, new byte[] { 0x61, 0x62 })?.Value);
        }

        [Fact]
        public void PlayerPrefsReadAndWriteTheKey()
        {
            var registry = new FakeRegistry();
            registry.Keys[Key] = new()
            {
                ("Screenmanager Resolution Width_h182942802", RegistryPrefsStore.RegDword, BitConverter.GetBytes(1280)),
                ("unity.player_session_count_h922449978", RegistryPrefsStore.RegBinary, new byte[] { 0x37, 0x34, 0 }),
                ("Not a pref", RegistryPrefsStore.RegDword, BitConverter.GetBytes(9)),
            };
            WithStore(registry, dir =>
            {
                Assert.Equal(1280, PlayerPrefs.GetInt("Screenmanager Resolution Width"));
                Assert.Equal("74", PlayerPrefs.GetString("unity.player_session_count"));
                Assert.Equal(0f, PlayerPrefs.GetFloat("Screenmanager Resolution Width"));
                Assert.False(PlayerPrefs.HasKey("Not a pref"));
                PlayerPrefs.SetFloat("Volume", 0.25f);
                PlayerPrefs.SetInt("Screenmanager Resolution Width", 1920);
                PlayerPrefs.DeleteKey("unity.player_session_count");
                PlayerPrefs.Save();
                var values = registry.Keys[Key].ToDictionary(v => v.Name, v => (v.Type, Hex(v.Data)));
                Assert.Equal(3, values.Count);
                Assert.Equal((RegistryPrefsStore.RegDword, "80070000"), values["Screenmanager Resolution Width_h182942802"]);
                Assert.Equal((RegistryPrefsStore.RegDword, "000000000000D03F"), values[RegistryPrefsStore.ValueName("Volume")]);
                Assert.True(values.ContainsKey("Not a pref"));
                // No prefs file is written beside the key.
                Assert.False(File.Exists(Path.Combine(dir, "prefs")));
                PlayerPrefs.DeleteAll();
                PlayerPrefs.SetInt("After", 1);
                PlayerPrefs.Save();
                Assert.Equal(new[] { RegistryPrefsStore.ValueName("After"), "Not a pref" }, registry.Keys[Key].Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            });
        }

        [Fact]
        public void NothingIsWrittenWithoutChanges()
        {
            var registry = new FakeRegistry();
            WithStore(registry, _ =>
            {
                Assert.False(PlayerPrefs.HasKey("x"));
                PlayerPrefs.Save();
                PlayerPrefs.SaveOnQuit();
                Assert.Empty(registry.Keys);
            });
        }

        [Fact]
        public void OldFileFillsWhatTheKeyLacksOnce()
        {
            var registry = new FakeRegistry();
            registry.Keys[Key] = new() { ("Screenmanager Fullscreen mode_h3630240806", RegistryPrefsStore.RegDword, BitConverter.GetBytes(3)) };
            var old = "\t<pref name=\"Screenmanager Fullscreen mode\" type=\"int\">1</pref>\n"
                + "\t<pref name=\"ModSetting\" type=\"string\">aMOpbGxv</pref>\n"
                + "\t<pref name=\"ModFloat\" type=\"float\">0.333333</pref>\n";
            WithStore(registry, dir =>
            {
                // The key, which the original wrote, wins; the rest comes over and is saved at once.
                Assert.Equal(3, PlayerPrefs.GetInt("Screenmanager Fullscreen mode"));
                Assert.Equal("héllo", PlayerPrefs.GetString("ModSetting"));
                Assert.Equal(0.333333f, PlayerPrefs.GetFloat("ModFloat"));
                Assert.Equal(3, registry.Keys[Key].Count);
                Assert.False(File.Exists(Path.Combine(dir, "prefs")));
                Assert.True(File.Exists(Path.Combine(dir, "prefs.imported")));
                // A later start finds no file to import.
                PlayerPrefs.SetString("ModSetting", "changed");
                PlayerPrefs.Save();
                PlayerPrefs.Reload(new RegistryPrefsStore(Key, registry.Open));
                Assert.Equal("changed", PlayerPrefs.GetString("ModSetting"));
            }, old);
        }

        [Fact]
        public void EachRunGetsItsStore()
        {
            Assert.Equal(@"Software\Klei\Oxygen Not Included", PrefsStore.RegistryKeyFor("Klei", "Oxygen Not Included", null));
            // A run home: a key of its own under the tests' root, never the game's.
            Assert.Equal(@"Software\OniMoonoTests\C:\Users\u\AppData\Local\Temp\oni-runq\j0000042\home\Klei\Oxygen Not Included",
                PrefsStore.RegistryKeyFor("Klei", "Oxygen Not Included", @"C:\Users\u\AppData\Local\Temp\oni-runq\j0000042\home\"));
            Assert.Equal(@"Software\OniMoonoTests\C:\long\home\Klei\Oxygen Not Included", PrefsStore.RegistryKeyFor("Klei", "Oxygen Not Included", @"\\?\C:\long\home"));
            Assert.Equal(@"Software\OniMoonoTests\server\share\home\Klei\Oxygen Not Included", PrefsStore.RegistryKeyFor("Klei", "Oxygen Not Included", @"\\server\share\home"));
            Assert.Throws<ArgumentException>(() => PrefsStore.RegistryKeyFor("Klei", "Oxygen Not Included", @"\\"));
            Assert.Equal(@"Software\A_B\C", PrefsStore.RegistryKeyFor(@"A\B", "C", null));
            // Unit tests and other platforms: the file in the persistent data folder.
            var saved = EngineConfig.PlayerPrefsRegistryKey;
            try
            {
                EngineConfig.PlayerPrefsRegistryKey = null;
                Assert.IsType<FilePrefsStore>(PrefsStore.ForConfig());
                EngineConfig.PlayerPrefsRegistryKey = Key;
                if (OperatingSystem.IsWindows()) Assert.IsType<RegistryPrefsStore>(PrefsStore.ForConfig());
                else Assert.IsType<FilePrefsStore>(PrefsStore.ForConfig());
            }
            finally
            {
                EngineConfig.PlayerPrefsRegistryKey = saved;
            }
        }

        // The real registry, on Windows only, under a key of this test's alone.
        [Fact]
        public void WindowsRegistryRoundTrip()
        {
            if (!OperatingSystem.IsWindows()) return;
            var key = $@"{PrefsStore.TestRoot}\unit-{Guid.NewGuid():N}\Klei\Oxygen Not Included";
            try
            {
                var entries = new Dictionary<string, PrefsEntry>(StringComparer.Ordinal)
                {
                    ["i"] = new(PrefsKind.Int, -7), ["f"] = new(PrefsKind.Float, 0.1f), ["s"] = new(PrefsKind.String, "ü"),
                };
                var order = entries.Keys.ToList();
                new RegistryPrefsStore(key).Save(entries, order, new HashSet<string>(order), new HashSet<string>(), false);
                var back = new Dictionary<string, PrefsEntry>(StringComparer.Ordinal);
                new RegistryPrefsStore(key).Load(back, new List<string>());
                Assert.Equal(-7, back["i"].Value);
                Assert.Equal(0.1f, back["f"].Value);
                Assert.Equal("ü", back["s"].Value);
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key.Substring(0, key.IndexOf(@"\Klei", StringComparison.Ordinal)), false);
            }
        }
    }
}
