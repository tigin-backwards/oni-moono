#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OniMoono.Host.Install
{
    /// A folder's mod_info.yaml, as the game's KMod.Mod.GetModInfoForFolder reads the fields its
    /// choice of version uses: minimumSupportedBuild (lastWorkingBuild in its place when only
    /// that is given), APIVersion, the DLC restrictions (the deprecated supportedContent turned
    /// into them as the game does) and version. The file is flat YAML: `key: value`, lists as
    /// `[A, B]` or `- A` lines.
    internal sealed record ModInfo(int MinimumSupportedBuild, int ApiVersion, string[]? RequiredDlcIds, string[]? ForbiddenDlcIds, string? Version)
    {
        public const string FileName = "mod_info.yaml";
        public const string Expansion1 = "EXPANSION1_ID";

        /// The folder's mod_info.yaml, or null when it has none or it doesn't parse (the game
        /// skips such a folder too).
        public static ModInfo? ReadFolder(string dir)
        {
            // The game finds the file by its lowercased name among the folder's files.
            string? path = null;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                    if (Path.GetFileName(file).ToLowerInvariant() == FileName) { path = file; break; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            if (path == null) return null;
            // A source's file too, before anything else reads it: capped.
            try { return new FileInfo(path).Length > 64 << 10 ? null : Parse(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        public static ModInfo? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string? listKey = null;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = StripComment(raw);
                if (line.Trim().Length == 0) continue;
                var trimmed = line.Trim();
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
                {
                    if (listKey == null) return null;
                    lists[listKey].Add(Unquote(trimmed.Substring(1).Trim()));
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) return null;
                var key = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                listKey = null;
                if (value.Length == 0) { lists[key] = new List<string>(); listKey = key; continue; }
                if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    lists[key] = value[1..^1].Split(',').Select(v => Unquote(v.Trim())).Where(v => v.Length > 0).ToList();
                    continue;
                }
                values[key] = Unquote(value);
            }
            int Int(string key) => values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : 0;
            string[]? List(string key) => lists.TryGetValue(key, out var l) ? l.ToArray() : null;

            var required = List("requiredDlcIds");
            var forbidden = List("forbiddenDlcIds");
            // supportedContent (deprecated), only when neither list is given, as the game reads it.
            if (values.TryGetValue("supportedContent", out var supported) && required == null && forbidden == null)
            {
                supported = supported.ToUpperInvariant();
                if (!supported.Contains("ALL"))
                {
                    bool vanilla = supported.Contains("VANILLA_ID"), expansion1 = supported.Contains(Expansion1);
                    var found = new List<string>();
                    foreach (Match m in Regex.Matches(supported, @"\b\w+_ID\b"))
                        if (m.Value != "VANILLA_ID" && !(m.Value == Expansion1 && vanilla) && m.Value == Expansion1) found.Add(m.Value);
                    if (found.Count > 0) required = found.ToArray();
                    if (!expansion1) forbidden = [Expansion1];
                }
            }
            required = required?.Select(d => d.ToUpperInvariant()).ToArray();
            forbidden = forbidden?.Select(d => d.ToUpperInvariant()).ToArray();
            int minimum = Int("minimumSupportedBuild");
            if (Int("lastWorkingBuild") is int last and not 0 && minimum == 0) minimum = last;
            return new ModInfo(minimum, Int("APIVersion"), required, forbidden, values.TryGetValue("version", out var version) ? version : null);
        }

        static string StripComment(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"' || line[i] == '\'') quoted = !quoted;
                else if (line[i] == '#' && !quoted && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
            }
            return line;
        }

        static string Unquote(string v) =>
            v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;

        /// DlcManager.IsCorrectDlcSubscribed: every required DLC subscribed and no forbidden one.
        public bool DlcAllowed(IReadOnlySet<string> subscribed) =>
            (RequiredDlcIds ?? []).All(subscribed.Contains) && !(ForbiddenDlcIds ?? []).Any(subscribed.Contains);
    }

    /// One version in a folder laid out as a Workshop item: the root, or a folder of
    /// archived_versions, with its mod_info.yaml.
    internal sealed record ArchivedVersion(string RelativePath, string Dir, ModInfo Info);

    /// The game's choice among a mod's versions (KMod.Mod.GetMostSuitableArchive), with the
    /// installed game's build in place of the KleiVersion.ChangeList it is compiled with:
    /// - no mod_info.yaml in the root: none;
    /// - no archived_versions folder, or one without folders: the root, when its DLC
    ///   restrictions allow, whatever its minimumSupportedBuild;
    /// - else the root and each archived_versions folder with a mod_info.yaml, those whose DLC
    ///   restrictions allow and whose APIVersion is 2 or 0, the one with the highest
    ///   minimumSupportedBuild at or below the build (the first listed of equals, the root
    ///   first).
    internal static class ArchiveChoice
    {
        public const string ArchivedVersions = "archived_versions";

        public static IReadOnlyList<ArchivedVersion> Versions(string root)
        {
            var versions = new List<ArchivedVersion>();
            var rootInfo = ModInfo.ReadFolder(root);
            if (rootInfo == null) return versions;
            versions.Add(new ArchivedVersion("", root, rootInfo));
            var archived = Path.Combine(root, ArchivedVersions);
            if (!Directory.Exists(archived)) return versions;
            foreach (var dir in Directory.GetDirectories(archived).OrderBy(d => d, StringComparer.Ordinal))
                if (ModInfo.ReadFolder(dir) is { } info) versions.Add(new ArchivedVersion(Path.Combine(ArchivedVersions, Path.GetFileName(dir)), dir, info));
            return versions;
        }

        public static ArchivedVersion? MostSuitable(string root, long gameBuild, IReadOnlySet<string> subscribedDlc)
        {
            var rootInfo = ModInfo.ReadFolder(root);
            if (rootInfo == null) return null;
            var rootVersion = new ArchivedVersion("", root, rootInfo);
            var archived = Path.Combine(root, ArchivedVersions);
            if (!Directory.Exists(archived) || Directory.GetFileSystemEntries(archived).Length == 0)
                return rootInfo.DlcAllowed(subscribedDlc) ? rootVersion : null;
            return Versions(root)
                .Where(v => v.Info.DlcAllowed(subscribedDlc))
                .Where(v => v.Info.ApiVersion is 2 or 0)
                .Where(v => v.Info.MinimumSupportedBuild <= gameBuild)
                .OrderByDescending(v => v.Info.MinimumSupportedBuild)
                .FirstOrDefault();
        }
    }
}
