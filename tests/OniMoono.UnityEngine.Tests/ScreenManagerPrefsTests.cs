using System.IO;
using OniMoono.Engine;
using OniMoono.Engine.Platform;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // The start window as Unity's player opens it: the Screenmanager keys saved last run, else
    // PlayerSettings' window, with -screen-* / -window-mode over either.
    public class ScreenManagerPrefsTests
    {
        // This game's PlayerSettings: 1024x768, native resolution, fullscreen window.
        static readonly ScreenManagerPrefs.Defaults Game = new(1024, 768, true, FullScreenMode.FullScreenWindow);

        static void WithPrefs(string prefs, System.Action test)
        {
            var dir = Directory.CreateTempSubdirectory("oni-moono-screenprefs-").FullName;
            var saved = EngineConfig.PersistentDataPath;
            try
            {
                EngineConfig.PersistentDataPath = dir;
                if (prefs != null)
                    File.WriteAllText(Path.Combine(dir, "prefs"), "<unity_prefs version_major=\"1\" version_minor=\"1\">\n" + prefs + "</unity_prefs>");
                PlayerPrefs.Reload();
                test();
            }
            finally
            {
                EngineConfig.PersistentDataPath = saved;
                PlayerPrefs.Reload();
                Directory.Delete(dir, true);
            }
        }

        static string Pref(string name, int value) => $"\t<pref name=\"{name}\" type=\"int\">{value}</pref>\n";

        // As the original wrote them for a 1280x720 window (its registry, without the _h suffixes).
        static string Windowed1280 =>
            Pref("Screenmanager Resolution Width Default", 1024) + Pref("Screenmanager Resolution Height Default", 768)
            + Pref("Screenmanager Resolution Use Native Default", 1) + Pref("Screenmanager Fullscreen mode Default", 1)
            + Pref("Screenmanager Resolution Width", 1280) + Pref("Screenmanager Resolution Height", 720)
            + Pref("Screenmanager Resolution Use Native", 0) + Pref("Screenmanager Fullscreen mode", 3);

        [Fact]
        public void NothingSavedOpensPlayerSettingsWindow()
            => WithPrefs(null, () => Assert.Equal(new ScreenManagerPrefs.Window(-1, -1, FullScreenMode.FullScreenWindow), ScreenManagerPrefs.Saved(Game)));

        [Fact]
        public void SavedWindowedSizeOpensWindowed()
            => WithPrefs(Windowed1280, () => Assert.Equal(new ScreenManagerPrefs.Window(1280, 720, FullScreenMode.Windowed), ScreenManagerPrefs.Saved(Game)));

        [Fact]
        public void SavedNativeSizeFollowsTheMonitor()
            => WithPrefs(Windowed1280.Replace("Use Native\" type=\"int\">0", "Use Native\" type=\"int\">1"),
                () => Assert.Equal(new ScreenManagerPrefs.Window(-1, -1, FullScreenMode.Windowed), ScreenManagerPrefs.Saved(Game)));

        [Fact]
        public void SavedUnderOtherDefaultsIsIgnored()
            => WithPrefs(Windowed1280.Replace("Width Default\" type=\"int\">1024", "Width Default\" type=\"int\">800"),
                () => Assert.Equal(Game.Window, ScreenManagerPrefs.Saved(Game)));

        [Fact]
        public void InvalidSavedValuesAreIgnored()
        {
            WithPrefs(Windowed1280.Replace("mode\" type=\"int\">3", "mode\" type=\"int\">7"), () => Assert.Equal(Game.Window, ScreenManagerPrefs.Saved(Game)));
            WithPrefs(Windowed1280.Replace("Resolution Width\" type=\"int\">1280", "Resolution Width\" type=\"int\">0"), () => Assert.Equal(Game.Window, ScreenManagerPrefs.Saved(Game)));
            WithPrefs(Pref("Screenmanager Fullscreen mode", 3), () => Assert.Equal(Game.Window, ScreenManagerPrefs.Saved(Game)));
        }

        [Fact]
        public void ArgumentsWinOverTheSavedWindow()
        {
            var saved = new ScreenManagerPrefs.Window(1280, 720, FullScreenMode.Windowed);
            Assert.Equal(saved, ScreenManagerPrefs.Start(saved, -1, -1, -1, null));
            Assert.Equal(new ScreenManagerPrefs.Window(1600, 900, FullScreenMode.Windowed), ScreenManagerPrefs.Start(saved, 1600, 900, -1, null));
            Assert.Equal(new ScreenManagerPrefs.Window(1280, 720, FullScreenMode.FullScreenWindow), ScreenManagerPrefs.Start(saved, -1, -1, 1, null));
            Assert.Equal(new ScreenManagerPrefs.Window(-1, -1, FullScreenMode.Windowed), ScreenManagerPrefs.Start(Game.Window, -1, -1, 0, null));
            // -screen-fullscreen 1 keeps a saved fullscreen mode; -window-mode wins over it.
            var exclusive = new ScreenManagerPrefs.Window(-1, -1, FullScreenMode.ExclusiveFullScreen);
            Assert.Equal(exclusive, ScreenManagerPrefs.Start(exclusive, -1, -1, 1, null));
            Assert.Equal(new ScreenManagerPrefs.Window(1280, 720, FullScreenMode.ExclusiveFullScreen), ScreenManagerPrefs.Start(saved, -1, -1, 0, "exclusive"));
            Assert.Equal(new ScreenManagerPrefs.Window(1280, 720, FullScreenMode.Windowed), ScreenManagerPrefs.Start(saved with { Mode = FullScreenMode.FullScreenWindow }, -1, -1, 1, "windowed"));
        }

        [Fact]
        public void RecordWritesWhatTheOriginalWrites()
            => WithPrefs(null, () =>
            {
                ScreenManagerPrefs.Record(Game, FullScreenMode.Windowed, 1280, 720, new DisplayMode(2560, 1440, 60, 1));
                PlayerPrefs.Save();
                var text = File.ReadAllText(Path.Combine(EngineConfig.PersistentDataPath, "prefs"));
                Assert.Equal("<unity_prefs version_major=\"1\" version_minor=\"1\">\n" + Pref("Screenmanager Fullscreen mode", 3)
                    + Pref("Screenmanager Resolution Width", 1280) + Pref("Screenmanager Resolution Height", 720) + Pref("Screenmanager Resolution Use Native", 0)
                    + Pref("Screenmanager Resolution Width Default", 1024) + Pref("Screenmanager Resolution Height Default", 768)
                    + Pref("Screenmanager Resolution Use Native Default", 1) + Pref("Screenmanager Fullscreen mode Default", 1) + "</unity_prefs>", text);
                PlayerPrefs.Reload();
                Assert.Equal(new ScreenManagerPrefs.Window(1280, 720, FullScreenMode.Windowed), ScreenManagerPrefs.Saved(Game));
                ScreenManagerPrefs.Record(Game, FullScreenMode.FullScreenWindow, 2560, 1440, new DisplayMode(2560, 1440, 60, 1));
                Assert.Equal(new ScreenManagerPrefs.Window(-1, -1, FullScreenMode.FullScreenWindow), ScreenManagerPrefs.Saved(Game));
            });
    }
}
