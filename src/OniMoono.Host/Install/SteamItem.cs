#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Host.Launch;

namespace OniMoono.Host.Install
{
    /// Steam's copy of Moono's Workshop item: LIBRARY/steamapps/workshop/content/457140/ID, the
    /// folder Steam downloads the item to and updates. The game copies every subscribed mod from
    /// there into its mods folder (Documents\Klei\OxygenNotIncluded\mods\Steam\ID on Windows,
    /// ~/.config/unity3d/Klei/Oxygen Not Included/mods/Steam/ID on Linux) and runs the loader mod
    /// from that copy, which only the original game refreshes. So an install updates from
    /// Steam's folder, wherever it was first started from (docs/INSTALL-MODES.md). Read only.
    internal static class SteamItem
    {
        /// A copy found: its folder, its Workshop ID, and why Steam isn't done with it (null
        /// when nothing is pending).
        public sealed record Copy(string Dir, string Id, string? Pending);

        /// appworkshop_457140.acf is read capped.
        const long MaxWorkshopState = 16 << 20;

        /// The item's copies in the library that holds the game and in every library of every
        /// Steam install: the folders of workshop/content/457140 whose moono.json names Moono's
        /// item (MoonoItemMarker), whatever their ID. Test runs (ONI_MOONO_STEAM_ROOTS) look in
        /// the made-up Steam folders alone.
        public static IReadOnlyList<Copy> Find(SteamLibrary steam, string? gameDir)
        {
            var libraries = new List<string>();
            if (!steam.IsTest && LibraryOf(gameDir) is { } holding) libraries.Add(holding);
            libraries.AddRange(steam.AllLibraries());
            var found = new List<Copy>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var library in libraries)
            {
                var workshop = Path.Combine(library, "steamapps", "workshop");
                var content = Path.Combine(workshop, "content", SteamLibrary.AppId);
                try
                {
                    if (!Directory.Exists(content)) continue;
                    foreach (var dir in Directory.GetDirectories(content))
                    {
                        if (!OniMoono.GameTweaks.MoonoItemMarker.IsMoonoItem(dir) || !seen.Add(SteamLibrary.Resolved(dir))) continue;
                        var id = Path.GetFileName(dir);
                        found.Add(new Copy(dir, id, Pending(Path.Combine(workshop, $"appworkshop_{SteamLibrary.AppId}.acf"), id)));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return found;
        }

        /// The library a game folder is in (LIBRARY/steamapps/common/FOLDER), or null.
        public static string? LibraryOf(string? gameDir)
        {
            if (string.IsNullOrEmpty(gameDir)) return null;
            try
            {
                var common = Path.GetDirectoryName(Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var steamapps = Path.GetDirectoryName(common);
                if (!string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase)) return null;
                if (!string.Equals(Path.GetFileName(steamapps), "steamapps", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.GetDirectoryName(steamapps);
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { return null; }
        }

        /// Why Steam isn't done with item `id`, by the library's appworkshop_457140.acf: the
        /// manifest installed (WorkshopItemsInstalled) is not the latest Steam knows
        /// (WorkshopItemDetails' latest_manifest), so a download is pending or running. Null
        /// when they agree, and when the file or either entry isn't there: the files' hashes
        /// are checked in any case.
        public static string? Pending(string workshopState, string id)
        {
            Vdf vdf;
            try
            {
                if (!File.Exists(workshopState)) return null;
                vdf = Vdf.Parse(System.Text.Encoding.UTF8.GetString(VersionedInstall.ReadCapped(workshopState, MaxWorkshopState)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            var latest = vdf.Path("AppWorkshop", "WorkshopItemDetails", id)?.Value("latest_manifest");
            if (string.IsNullOrEmpty(latest) || latest == "0") return null;
            var installed = vdf.Path("AppWorkshop", "WorkshopItemsInstalled", id)?.Value("manifest");
            if (string.IsNullOrEmpty(installed) || installed == latest) return null;
            return $"Steam has manifest {installed} of it installed and {latest} to download";
        }
    }
}
