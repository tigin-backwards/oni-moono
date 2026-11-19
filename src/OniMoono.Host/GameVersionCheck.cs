#nullable enable
using System;
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
        public const string ReleasesUrl = Launch.StartText.Releases;

        /// The game update this build is for ("U60").
        public static string? ThisUpdate => Launch.StartText.Updates.TryGetValue(TypeTreeDatabase.UnityVersion, out var update) ? update.Name : null;

        /// Whether this build is for a Steam beta (its releases are pre-releases).
        public static bool ThisIsBeta => Launch.StartText.Updates.TryGetValue(TypeTreeDatabase.UnityVersion, out var update) && update.Beta;

        /// The game's Unity version when it is not this build's, else null.
        public static string? OtherUnity(string dataDir)
        {
            string game;
            try { game = SerializedFile.Open(Path.Combine(dataDir, "globalgamemanagers")).UnityVersion; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
            return game == TypeTreeDatabase.UnityVersion ? null : game;
        }

        /// Null when the game's data matches this build, else the message to show, in the
        /// player's language (Launch.StartText.VersionMismatch).
        public static string? Problem(string dataDir) => OtherUnity(dataDir) is { } game ? Launch.StartText.VersionMismatch(TypeTreeDatabase.UnityVersion, game) : null;

        /// Logs the problem (with the Unity versions, which the message leaves out) and, on
        /// Windows, where a player started from Steam sees no console, shows it in a message box
        /// unless this is a test run (ONI_HOST_NO_DIALOG=1).
        public static void Report(string dataDir, string message)
        {
            if (OtherUnity(dataDir) is { } game) EngineLog.Engine(Launch.StartText.VersionDetail(TypeTreeDatabase.UnityVersion, game));
            EngineLog.Engine(message);
            Console.Error.WriteLine(message);
            if (OperatingSystem.IsWindows() && !Launch.DesktopDialog.Quiet(Environment.GetEnvironmentVariable, test: false))
                MessageBoxW(IntPtr.Zero, message, "Moono", 0x10);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    }
}
