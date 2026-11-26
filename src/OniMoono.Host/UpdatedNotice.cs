#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace OniMoono.Host
{
    /// An "Updated" badge to show: Moono went from version `From` to `To` (versions as
    /// UpdateCheck.Version gives them), shown for the `Launches`th time.
    internal sealed record UpdatedNotice(string From, string To, int Launches);

    /// What Moono remembers between starts: the newest version it ran (a high-water mark, so a
    /// downgrade and the way back up show nothing), and the "Updated" badge still due.
    internal sealed record UpdatedState(string? LastRun, UpdatedNotice? Pending);

    /// What the main menu's badge shows: an update available (UpdateCheck) or Moono updated;
    /// the tag on its second line, the page a click opens, its tooltip (TMP rich text, "" for
    /// none) and whether those notes were cut.
    internal sealed record MenuBadge(bool Updated, string Tag, string Url, string Notes, bool Truncated);

    /// The main menu's "Updated" badge (OniMoono.GameTweaks.UpdateBadge, the update badge's slot):
    /// on the first launches after the running Moono version changed, for any install mode and
    /// whatever the Update check setting, as it makes no request of its own. The last version run
    /// is kept in `oni-moono/last-version.json` beside settings.json. A fresh install records its
    /// version and shows nothing; a lower version (a downgrade, or `--use-version` pinning an
    /// older one) shows nothing and leaves the record as it is, so the way back up shows nothing
    /// either. The badge goes once hovered or clicked, or after Launches launches; an update
    /// available (UpdateCheck) takes its place.
    ///
    /// Its tooltip lists the notes of every release since the version last run, newest first,
    /// from a releases list in GitHub's form: the Workshop item's release-notes.json (written by
    /// scripts/workshop-item.py once the release is published), else the list the update check
    /// read (GitHub installs with the check on). With neither, a line points to the release page.
    /// Plain .NET with no game types (the tests compile it in).
    ///
    /// ONI_UPDATE_NOTES: a notes file to read instead of the item's (tests). ONI_UPDATE_PREVIOUS:
    /// the version recorded as last run, in place of the file's (tests).
    internal static class Updated
    {
        /// How many launches show the badge when it is never hovered or clicked.
        public const int Launches = 3;
        /// The notes file at the Workshop item's root.
        public const string NotesFile = "release-notes.json";
        const long MaxNotes = 4_000_000;
        public const string ReleasePages = "https://github.com/tigin-backwards/oni-moono/releases";

        public static string StatePath(string persistentDataPath) => Path.Combine(persistentDataPath, "oni-moono", "last-version.json");

        /// The state after this launch of `current`, and the badge to show (null: none).
        public static (UpdatedState Next, UpdatedNotice? Show) Decide(UpdatedState? saved, string current)
        {
            if (saved?.LastRun is not { } last) return (new UpdatedState(current, null), null);
            int order = string.CompareOrdinal(current, last);
            // Lower than a version run before: nothing now, and nothing on the way back up.
            if (order < 0) return (saved with { Pending = null }, null);
            if (order > 0)
            {
                // A badge still due (not yet seen) keeps its start, so its notes go on listing
                // the versions it covered.
                var from = saved.Pending is { } due && due.To == last ? due.From : last;
                var notice = new UpdatedNotice(from, current, 1);
                return (new UpdatedState(current, notice), notice);
            }
            if (saved.Pending is { } pending && pending.To == current)
            {
                if (pending.Launches >= Launches) return (saved with { Pending = null }, null);
                var next = pending with { Launches = pending.Launches + 1 };
                return (saved with { Pending = next }, next);
            }
            return (saved with { Pending = null }, null);
        }

        /// Hovered or clicked: no badge from the next launch on.
        public static UpdatedState Dismiss(UpdatedState state) => state with { Pending = null };

        /// The releases after `From` up to `To`, newest first: this build's channel (normal
        /// releases, or pre-releases for a beta build), superseded ones left out; `To`'s own
        /// release always.
        public static List<MoonoRelease> Since(IEnumerable<MoonoRelease> releases, UpdatedNotice notice, bool beta) =>
            releases
                .Where(r => UpdateCheck.Version(r.Tag) is { } v && string.CompareOrdinal(v, notice.From) > 0 && string.CompareOrdinal(v, notice.To) <= 0
                            && (v == notice.To || (r.Prerelease == beta && !UpdateCheck.Superseded(r))))
                .GroupBy(r => UpdateCheck.Version(r.Tag)).Select(g => g.First())
                .OrderByDescending(r => UpdateCheck.Version(r.Tag), StringComparer.Ordinal)
                .ToList();

        /// The badge: an update available wins; else the "Updated" one, with the notes since
        /// the version last run from `releases` (null: none to read, a click opens the page).
        public static MenuBadge? Badge(UpdateFound? available, UpdatedNotice? notice, string currentTag, IReadOnlyList<MoonoRelease>? releases, bool beta)
        {
            if (available != null) return new MenuBadge(false, available.Tag, available.Url, available.Notes, available.Truncated);
            if (notice == null) return null;
            var since = releases == null ? [] : Since(releases, notice, beta);
            var url = since.FirstOrDefault(r => UpdateCheck.Version(r.Tag) == notice.To)?.Url ?? $"{ReleasePages}/tag/{currentTag.Trim()}";
            if (since.Count == 0) return new MenuBadge(true, currentTag, url, "", false);
            return new MenuBadge(true, currentTag, url, UpdateCheck.Notes(since, out bool truncated), truncated);
        }

        public static UpdatedState? Read(string path)
        {
            try
            {
                if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return null;
                UpdatedNotice? pending = null;
                if (root["pending"] is JsonObject p && UpdateCheck.Version(p["from"]!.GetValue<string>()) is { } from
                    && UpdateCheck.Version(p["to"]!.GetValue<string>()) is { } to)
                    pending = new UpdatedNotice(from, to, p["launches"]!.GetValue<int>());
                return new UpdatedState(root["lastRun"]?.GetValue<string>() is { } last ? UpdateCheck.Version(last) : null, pending);
            }
            catch (Exception)
            {
                // Unreadable: as a fresh install (records this version, shows nothing).
                return null;
            }
        }

        public static void Write(string path, UpdatedState state)
        {
            var root = new JsonObject { ["lastRun"] = state.LastRun };
            if (state.Pending is { } p) root["pending"] = new JsonObject { ["from"] = p.From, ["to"] = p.To, ["launches"] = p.Launches };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", root.ToJsonString(), new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
        }

        /// A notes file (a releases list in GitHub's form); null when there is none or it can't
        /// be read.
        public static List<MoonoRelease>? ReadNotes(string? path, Action<string> log)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                if (new FileInfo(path).Length > MaxNotes) throw new InvalidDataException($"over {MaxNotes / 1_000_000} MB");
                return UpdateCheck.Parse(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                log($"updated badge: {path} not read: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        static string? statePath;
        static UpdatedState? state;

        /// This launch's badge, if one is due: the state file read and written for this launch
        /// (once per start). Null for a build that is no release.
        public static UpdatedNotice? Begin(string persistentDataPath, string? currentTag, Action<string> log)
        {
            if (currentTag == null || UpdateCheck.Version(currentTag) is not { } current) return null;
            statePath = StatePath(persistentDataPath);
            var saved = Read(statePath);
            if (Environment.GetEnvironmentVariable("ONI_UPDATE_PREVIOUS") is { Length: > 0 } previous)
                saved = new UpdatedState(UpdateCheck.Version(previous), null);
            var (next, show) = Decide(saved, current);
            state = next;
            try
            {
                if (next != saved) Write(statePath, next);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"updated badge: could not write {statePath}: {e.Message}");
            }
            if (show != null) log($"updated badge: Moono {show.From} → {show.To}, launch {show.Launches} of {Launches}");
            return show;
        }

        /// The badge was hovered or clicked: it shows no more from the next launch on.
        public static void Dismissed(Action<string> log)
        {
            if (state?.Pending == null || statePath == null) return;
            state = Dismiss(state);
            try
            {
                Write(statePath, state);
                log("updated badge: seen; not shown again");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"updated badge: could not write {statePath}: {e.Message}");
            }
        }
    }
}
