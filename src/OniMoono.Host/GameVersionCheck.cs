#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OniMoono.Assets.Files;
using OniMoono.Engine;
using OniMoono.Assets.TypeTrees;

namespace OniMoono.Host
{
    /// Each Moono build reads one Unity version's data layouts (TypeTreeDatabase), so it supports
    /// the game updates built with that version. Another game update would fail deep in the asset
    /// reader; this tells the player which Moono build to get instead (Program.Main, before the
    /// assemblies load). The game's Unity version is the globalgamemanagers header's.
    internal static class GameVersionCheck
    {
        /// The game update each Unity version shipped with, for the message.
        static readonly Dictionary<string, string> updates = new(StringComparer.Ordinal)
        {
            ["6000.3.5f2"] = "U59",
            ["6000.3.17f1"] = "U60",
        };

        public const string ReleasesUrl = "https://github.com/tigin-backwards/oni-moono/releases";

        /// The game update this build is for ("U59", "U60").
        public static string? ThisUpdate => updates.TryGetValue(TypeTreeDatabase.UnityVersion, out var update) ? update : null;

        static string Describe(string unity) => updates.TryGetValue(unity, out var update) ? $"{update} (Unity {unity})" : $"Unity {unity}";

        /// Null when the game's data matches this build, else the message to show.
        public static string? Problem(string dataDir)
        {
            string game;
            try { game = SerializedFile.Open(Path.Combine(dataDir, "globalgamemanagers")).UnityVersion; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
            string ours = TypeTreeDatabase.UnityVersion;
            if (game == ours) return null;
            string wanted = updates.TryGetValue(game, out var update)
                ? $"Get the Moono build for {update} from {ReleasesUrl}."
                : $"No Moono build supports it yet; see {ReleasesUrl}.";
            return $"This Moono build is for Oxygen Not Included {Describe(ours)}, but the game is {Describe(game)}. {wanted} " +
                   "To play now, clear the game's launch options in Steam.";
        }

        /// Logs the problem and, on Windows, where a player started from Steam sees no console,
        /// shows it in a message box.
        public static void Report(string message)
        {
            EngineLog.Engine(message);
            Console.Error.WriteLine(message);
            if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "Moono", 0x10);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    }
}
