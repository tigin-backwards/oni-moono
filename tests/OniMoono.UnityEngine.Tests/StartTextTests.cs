#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using OniMoono.Host.Launch;
using Xunit;

namespace OniMoono.Tests
{
    /// The messages before the game loads (StartText): which game-version message a mismatch
    /// gets, and the language chosen for them (PlayerLanguage) from the game's settings, then the
    /// system's language, else English.
    public sealed class StartTextTests : IDisposable
    {
        const string U59 = "6000.3.5f2", U60 = "6000.3.17f1";
        readonly string folder = Path.Combine(Path.GetTempPath(), "oni-start-text-" + Guid.NewGuid().ToString("N"));
        static readonly CultureInfo english = CultureInfo.GetCultureInfo("en-US"), chinese = CultureInfo.GetCultureInfo("zh-CN");

        public StartTextTests() => Directory.CreateDirectory(folder);

        public void Dispose() => Directory.Delete(folder, true);

        static string Translations([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "translations"));

        void Prefs(string strings) => File.WriteAllText(Path.Combine(folder, "kplayerprefs.yaml"),
            "strings:\n" + strings + "ints:\n  ResolutionWidth: 1024\nfloats:\n  Volume_Master: 0\n");

        /// A U60 build (master's, for the current game) on a U59 game (an older update kept as a
        /// Steam beta): switch the game to U60, or get the u59 line's last release.
        [Fact]
        public void AnOlderGameGetsSwitchToTheCurrentOneOrTheLastU59Build()
        {
            var text = StartText.VersionMismatch(U60, U59, null);
            Assert.StartsWith("Your game is the U59 update of Oxygen Not Included, and this Moono build is for U60, the current update.", text);
            Assert.Contains("switch the game to U60: in Steam, right-click Oxygen Not Included → Properties → Betas, and choose None.", text);
            Assert.Contains("Or get Moono for U59:\nhttps://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.19.03\n", text);
            Assert.Contains("clear the game's launch options", text);
            Assert.DoesNotContain(" beta", text);
            Assert.DoesNotContain("public_testing", text);
            Assert.DoesNotContain("Unity", text);
            Assert.DoesNotContain("6000", text);
            Assert.False(StartText.Updates[U60].Beta);
        }

        [Fact]
        public void AnUnknownUpdateHasNoBuildYet()
        {
            var text = StartText.VersionMismatch(U60, "6000.4.1f1", null);
            Assert.Contains("no Moono build supports it yet", text);
            Assert.Contains(StartText.Releases + "\n", text);
            Assert.DoesNotContain("6000", text);
            Assert.Contains("U60 (Unity 6000.3.17f1)", StartText.VersionDetail(U60, U59));
            Assert.Contains("the game is U59 (Unity 6000.3.5f2)", StartText.VersionDetail(U60, U59));
        }

        [Fact]
        public void TheGamesChineseWins()
        {
            Prefs("  SelectedLanguageType: Preinstalled\n  SelectedLanguageCode: zh_klei\n");
            var choice = PlayerLanguage.Choose(folder, english, Translations());
            Assert.Equal("zh", choice.Code);
            Assert.Equal(Path.Combine(Translations(), "zh.po"), choice.PoPath);
            Assert.Contains("zh_klei", choice.Why);
            var text = StartText.VersionMismatch(U60, U59, choice.Strings);
            Assert.StartsWith("你的《缺氧》是 U59 版本，而这个 Moono 版本是为当前的 U60 版本制作的。", text);
            Assert.Contains("属性 → 测试版", text);
            Assert.Contains("启动选项", text);
            Assert.Contains(StartText.Releases + "/tag/v2026.11.19.03", text);
            Assert.Contains("Moono 找到了 2 个《缺氧》安装", StartText.Text(choice.Strings, StartText.ChooseBox, 2, @"D:\Games\OxygenNotIncluded"));
        }

        [Fact]
        public void TheGamesEnglishWinsOverAChineseSystem()
        {
            Prefs("  SelectedLanguageType: None\n  SelectedLanguageCode: ''\n");
            var choice = PlayerLanguage.Choose(folder, chinese, Translations());
            Assert.Equal("", choice.Code);
            Assert.Null(choice.Strings);
            Assert.StartsWith("Your game", StartText.VersionMismatch(U60, U59, choice.Strings));
        }

        [Fact]
        public void NoPrefsTakesTheSystemsChinese()
        {
            var choice = PlayerLanguage.Choose(folder, chinese, Translations());
            Assert.Equal("zh", choice.Code);
            Assert.Contains("no kplayerprefs.yaml", choice.Why);
            Assert.StartsWith("你的《缺氧》是 U59 版本", StartText.VersionMismatch(U60, U59, choice.Strings));
            // Traditional Chinese reads the Simplified translation too, rather than English.
            Assert.Equal("zh", PlayerLanguage.Choose(folder, CultureInfo.GetCultureInfo("zh-TW"), Translations()).Code);
        }

        [Fact]
        public void PrefsWithoutALanguageTakeTheSystems()
        {
            Prefs("  INSTALL_TIMESTAMP: 639303779797973285\n");
            Assert.Equal("zh", PlayerLanguage.Choose(folder, chinese, Translations()).Code);
        }

        [Fact]
        public void EnglishOtherwise()
        {
            var choice = PlayerLanguage.Choose(folder, english, Translations());
            Assert.Equal("", choice.Code);
            Assert.Null(choice.Strings);
            Assert.Equal("", PlayerLanguage.Choose(folder, CultureInfo.InvariantCulture, Translations()).Code);
            // A language with no translation is English, string by string.
            var italian = PlayerLanguage.Choose(folder, CultureInfo.GetCultureInfo("it-IT"), Translations());
            Assert.Equal("it-it", italian.Code);
            Assert.Null(italian.Strings);
            Assert.Contains("no translation", italian.Why);
            // A translation for the language's part: pt_BR and pt-PT are pt.po.
            var brazilian = PlayerLanguage.Choose(folder, CultureInfo.GetCultureInfo("pt-BR"), Translations());
            Assert.Equal(Path.Combine(Translations(), "pt.po"), brazilian.PoPath);
            Assert.NotNull(brazilian.Strings);
        }

        [Fact]
        public void DamagedPrefsTakeTheSystems()
        {
            File.WriteAllBytes(Path.Combine(folder, "kplayerprefs.yaml"), [0xff, 0x00, 0x13, 0x37, (byte)'\n', (byte)'{']);
            var choice = PlayerLanguage.Choose(folder, chinese, Translations());
            Assert.Equal("zh", choice.Code);
            Assert.Contains("unreadable", choice.Why);
            Assert.Equal("", PlayerLanguage.Choose(folder, english, Translations()).Code);
        }

        [Fact]
        public void ATranslationModNamesItsLanguage()
        {
            Prefs("  SelectedLanguageType: UGC\n  InstalledLanguage: 2906930548\n");
            var mod = Path.Combine(folder, "mods", "Steam", "2906930548");
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "strings.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: zh-CN\\n\"\n");
            Assert.Equal("zh", PlayerLanguage.Choose(folder, english, Translations()).Code);
            // A mod that is gone: the system's language.
            Directory.Delete(mod, true);
            Assert.Equal("", PlayerLanguage.Choose(folder, english, Translations()).Code);
        }

        [Fact]
        public void AStringWithOtherPlaceholdersStaysEnglish()
        {
            var strings = new System.Collections.Generic.Dictionary<string, string>
            {
                [StartText.Prefix + StartText.GameIsOther] = "没有链接 {1} {2}",
                [StartText.Prefix + StartText.NoSteam] = "需要 Steam。",
            };
            Assert.StartsWith("Your game is the U59 update", StartText.VersionMismatch(U60, U59, strings));
            Assert.Equal("需要 Steam。", StartText.Text(strings, StartText.NoSteam));
        }

        [Fact]
        public void SettingsFolderIsTheGames()
        {
            Assert.Equal(Path.Combine("R", "Documents", "Klei", "OxygenNotIncluded"), PlayerLanguage.SettingsFolder("R", true, _ => null, "H", "D"));
            Assert.Equal(Path.Combine("D", "Klei", "OxygenNotIncluded"), PlayerLanguage.SettingsFolder(null, true, _ => null, "H", "D"));
            Assert.Equal(Path.Combine("H", ".config", "unity3d", "Klei", "Oxygen Not Included"), PlayerLanguage.SettingsFolder(null, false, _ => null, "H", "D"));
            Assert.Equal(Path.Combine("X", "unity3d", "Klei", "Oxygen Not Included"), PlayerLanguage.SettingsFolder(null, false, v => v == "XDG_CONFIG_HOME" ? "X" : null, "H", "D"));
            Assert.Equal(Path.Combine("R", "config", "unity3d", "Klei", "Oxygen Not Included"), PlayerLanguage.SettingsFolder("R", false, _ => null, "H", "D"));
        }
    }
}
