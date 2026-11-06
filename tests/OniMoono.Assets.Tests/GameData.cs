using System;
using System.IO;

namespace OniMoono.Assets.Tests
{
    /// The reference game install (ONI_GAME_DIR, default ~/projects/oni-linux-762230). Corpus tests need
    /// it; they fail with a clear message when it is missing rather than silently passing.
    static class GameData
    {
        public static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762230");
                var dir = Path.Combine(game, "OxygenNotIncluded_Data");
                if (!Directory.Exists(dir)) throw new InvalidOperationException($"Game data not found at {dir}; set ONI_GAME_DIR.");
                return dir;
            }
        }

        /// The macOS build's data folder (`OxygenNotIncluded.app/Contents/Resources/Data`), from
        /// ONI_MAC_APP (the .app), by default the Steam depot download of this machine; null when
        /// absent. Tests that need it are skipped without it (it is not part of a normal install).
        public static string? MacDataDir
        {
            get
            {
                var app = Environment.GetEnvironmentVariable("ONI_MAC_APP") ?? DefaultMacApp;
                var dir = Path.Combine(app, "Contents", "Resources", "Data");
                return Directory.Exists(dir) ? dir : null;
            }
        }

        public const string DefaultMacApp = "/mnt/c/Program Files (x86)/Steam/steamapps/content/app_457140/depot_457142/OxygenNotIncluded.app";

        /// The files holding the build's shaders.
        public static readonly string[] ShaderFiles =
        [
            "level0", "level1", "level2", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets",
            "Resources/unity default resources", "Resources/unity_builtin_extra",
        ];

        public static readonly string[] SerializedFiles =
        [
            "globalgamemanagers", "globalgamemanagers.assets", "level0", "level1", "level2",
            "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets",
            "Resources/unity default resources", "Resources/unity_builtin_extra",
        ];
    }
}
