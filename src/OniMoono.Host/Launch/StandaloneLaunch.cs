#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OniMoono.Host.Launch
{
    /// How this start came about, and for a standalone start (OniMoono started directly, with
    /// neither Steam's `-- %command%` nor `--game`) where the game is and whether to start at all
    /// (docs/INSTALL-MODES.md):
    /// - the game is found through Steam's libraries (SteamLibrary, setup's places); with several
    ///   installs the player is asked once and the answer kept;
    /// - an install with an update pending or not fully installed (its appmanifest), or with no
    ///   appmanifest, is Steam's to finish: Steam opens on the game's page
    ///   (`steam://nav/games/details/457140`) and Moono doesn't start. Never
    ///   `steam://rungameid/457140`: Steam would start whatever the launch options say, the
    ///   original game included;
    /// - Steam is started and waited for when it isn't running, and SteamAppId=457140 is set for
    ///   the game's SteamAPI, so its RestartAppIfNecessary(457140) doesn't bounce the start to Steam;
    /// - a log line saying so: the game's saves sync through Steam Auto-Cloud, which Steam runs
    ///   around the sessions it starts, so they sync on the next start from Steam.
    internal static class StandaloneLaunch
    {
        public enum Mode { SteamCommand, Direct, Standalone, HandOff, Failed }

        /// The outcome: the mode, the game folder to play (Standalone), the lines for the log, and
        /// for HandOff and Failed what to tell the player.
        public sealed record Plan(Mode Mode, string? GameDir, IReadOnlyList<string> Log, string? Message = null, string? Url = null, SteamLibrary.Root? Root = null);

        public const string GamePage = "steam://nav/games/details/" + SteamLibrary.AppId;
        /// Test runs: the install to choose (its index in GameInstalls' order) when there are several.
        public const string ChooseVariable = "ONI_MOONO_CHOOSE_GAME";

        /// What a standalone start keeps between runs: the install chosen among several.
        internal sealed class Memory
        {
            public string? Game { get; set; }

            public static Memory Read(string path)
            {
                try { return JsonSerializer.Deserialize<Memory>(File.ReadAllText(path)) ?? new Memory(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new Memory(); }
            }

            public void Write(string path)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(this));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        /// Where Memory is kept: oni-moono/launch.json in the local application data folder
        /// (Windows), XDG_DATA_HOME or ~/.local/share (Linux); under the run home in test runs.
        public static string MemoryPath(string? runHome, SteamLibrary.Machine machine)
        {
            if (runHome != null) return Path.Combine(runHome, "oni-moono", "launch.json");
            if (machine.Windows) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oni-moono", "launch.json");
            var data = machine.Env("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(machine.Home, ".local", "share");
            return Path.Combine(data, "oni-moono", "launch.json");
        }

        /// The plan for a start: SteamCommand or Direct as the options say, else the standalone
        /// steps above. `choose` picks among several installs (null: give up);
        public static Plan Decide(bool steamCommand, bool gameGiven, SteamLibrary library, SteamClient client, string memoryPath,
            Func<IReadOnlyList<SteamLibrary.GameInstall>, int?> choose, TimeSpan steamTimeout)
        {
            if (steamCommand) return new Plan(Mode.SteamCommand, null, ["launch: from Steam's launch options (-- %command%)"]);
            if (gameGiven) return new Plan(Mode.Direct, null, ["launch: direct (--game)"]);
            var log = new List<string>();
            var installs = library.GameInstalls();
            if (installs.Count == 0)
                return new Plan(Mode.Failed, null, ["launch: standalone: no Oxygen Not Included in any Steam library"],
                    "Oxygen Not Included wasn't found in any Steam library. Install it in Steam, or start Moono from Steam's launch options.");
            var memory = Memory.Read(memoryPath);
            SteamLibrary.GameInstall install;
            if (installs.Count == 1) install = installs[0];
            else if (installs.FirstOrDefault(i => memory.Game != null && SameDir(i.Dir, memory.Game)) is { } kept)
            {
                install = kept;
                log.Add($"launch: {installs.Count} installs of the game; using the one chosen before");
            }
            else
            {
                int? index = choose(installs);
                if (index is not int n || n < 0 || n >= installs.Count)
                    return new Plan(Mode.Failed, null, [.. log, $"launch: standalone: {installs.Count} installs of the game and none chosen"],
                        "Moono found several installs of Oxygen Not Included and none was chosen.");
                install = installs[n];
                memory.Game = install.Dir;
                memory.Write(memoryPath);
                log.Add($"launch: {installs.Count} installs of the game; chose {install.Dir} (kept for later starts)");
            }
            log.Add($"launch: standalone: the game at {install.Dir} (Steam library {install.Library})");

            if (install.Manifest is not { Ready: true })
            {
                var state = install.Manifest?.Describe() ?? "it has no appmanifest in its library";
                log.Add($"launch: standalone: the install isn't ready: {state}; opening Steam on the game's page");
                return new Plan(Mode.HandOff, install.Dir, log,
                    "Steam has to finish installing or updating Oxygen Not Included first. Steam opens on the game's page; start Moono again once it is done.", GamePage, install.Root);
            }
            if (!client.EnsureRunning(install.Root, steamTimeout))
                return new Plan(Mode.Failed, install.Dir, [.. log, "launch: standalone: Steam isn't running"],
                    "Oxygen Not Included needs Steam, and Steam could not be started. Start Steam, then Moono again.");
            log.Add("started directly, not by Steam: Steam Cloud syncs on the next start from Steam");
            return new Plan(Mode.Standalone, install.Dir, log, null, null, install.Root);
        }

        static bool SameDir(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// SteamAppId=457140 for the process and the native code in it (the game's
        /// steam_api reads it with getenv, which .NET's own environment doesn't reach on Unix).
        public static void SetAppId()
        {
            Environment.SetEnvironmentVariable("SteamAppId", SteamLibrary.AppId);
            if (!OperatingSystem.IsWindows()) setenv("SteamAppId", SteamLibrary.AppId, 1);
        }

        [DllImport("libc", EntryPoint = "setenv")]
        static extern int setenv(string name, string value, int overwrite);

        /// Asks which install to use: ONI_MOONO_CHOOSE_GAME in test runs; else on Windows a
        /// message box per install, on Linux the terminal when there is one, else a desktop
        /// dialog (DesktopDialog: zenity's list, kdialog's menu); else the first.
        public static int? Choose(IReadOnlyList<SteamLibrary.GameInstall> installs, SteamLibrary.Machine machine, bool test, DesktopDialog? dialog = null)
        {
            if (machine.Env(ChooseVariable) is { Length: > 0 } chosen) return int.TryParse(chosen, out var i) ? i : null;
            if (test) return null;
            if (machine.Windows)
            {
                if (DesktopDialog.Quiet(machine.Env, test)) return 0;
                for (int i = 0; i < installs.Count; i++)
                {
                    int answer = MessageBoxW(IntPtr.Zero, $"Moono found {installs.Count} installs of Oxygen Not Included. Play the one at\n\n{installs[i].Dir}\n\n(Yes: this one, kept for later starts. No: the next one.)", "Moono", 0x3 | 0x20);
                    if (answer == 6) return i;   // Yes
                    if (answer == 2) return null; // Cancel
                }
                return null;
            }
            dialog ??= new DesktopDialog { Env = machine.Env };
            if (dialog.InputIsTerminal)
            {
                Console.Error.WriteLine($"Moono found {installs.Count} installs of Oxygen Not Included:");
                for (int i = 0; i < installs.Count; i++) Console.Error.WriteLine($"  {i + 1}. {installs[i].Dir}");
                Console.Error.Write("Which one (kept for later starts)? ");
                return int.TryParse(Console.ReadLine(), out var n) ? n - 1 : null;
            }
            var (asked, index) = dialog.Choose($"Moono found {installs.Count} installs of Oxygen Not Included. Which one should it play (kept for later starts)?",
                installs.Select(i => i.Dir).ToList(), test);
            return asked ? index : 0;
        }

        /// A message for the player: stderr, and unless this is a test run (`test`, or
        /// ONI_HOST_NO_DIALOG=1) a message box on Windows, on Linux a desktop dialog when stderr
        /// is not a terminal (DesktopDialog: started from the launcher or a file manager).
        public static void Tell(string message, bool error, bool test, DesktopDialog? dialog = null)
        {
            Console.Error.WriteLine("Moono: " + message);
            if (OperatingSystem.IsWindows())
            {
                if (!DesktopDialog.Quiet(Environment.GetEnvironmentVariable, test)) MessageBoxW(IntPtr.Zero, message, "Moono", error ? 0x10u : 0x40u);
            }
            else (dialog ?? new DesktopDialog()).Tell(message, error, test);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    }
}
