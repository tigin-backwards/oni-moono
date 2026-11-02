#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OniMoono.Host
{
    /// One release of Moono, as GitHub's releases API lists it.
    internal sealed record MoonoRelease(string Tag, string Title, bool Prerelease, string Url, string Body);

    /// What the main menu's update badge shows: the newest release's tag and page, and the notes
    /// of every release newer than this build (TMP rich text, newest first); `Truncated` when
    /// the notes were cut to their line budget.
    internal sealed record UpdateFound(string Tag, string Url, string Notes, bool Truncated);

    /// "Check for updates" (on by default, Moono Tweaks' General section): one background request
    /// for Moono's releases list, kept for CacheFor in the oni-moono data folder, and the badge
    /// on the main menu when a newer release of this build's channel is out
    /// (OniMoono.GameTweaks.UpdateBadge). A build knows its release from the `MoonoRelease`
    /// assembly metadata (scripts/release.sh stamps the tag); a build without one (a developer's)
    /// checks nothing. U59 builds look at normal releases only, U60 builds at pre-releases only;
    /// releases marked "Superseded" are skipped. Failure of any kind shows no badge and logs one
    /// line. Plain .NET with no game types (the tests compile it in).
    ///
    /// ONI_UPDATE_RELEASES_URL: the releases list from elsewhere (`file://` path, plain path or
    /// http(s) URL; `none` checks nothing), never cached: scripts/run.sh sets `none`, test specs a
    /// fixture. ONI_UPDATE_TAG: the release this build counts as (tests).
    internal static class UpdateCheck
    {
        public const string ReleasesUrl = "https://api.github.com/repos/tigin-backwards/oni-moono/releases";
        public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
        /// The tooltip's budget in lines (a long bullet counts as the lines it wraps to, at
        /// about WrapAt characters).
        public const int MaxLines = 25, WrapAt = 60;

        static readonly Regex TagForm = new(@"^v?(\d{4}\.\d{2}\.\d{2}\.\d{2})(-u60)?$", RegexOptions.CultureInvariant);

        /// A tag's version, "2026.11.02.03", which compares as text; null for another form.
        public static string? Version(string tag) => TagForm.Match(tag.Trim()) is { Success: true } m ? m.Groups[1].Value : null;

        public static string CachePath(string persistentDataPath) => Path.Combine(persistentDataPath, "oni-moono", "update-check.json");

        public static List<MoonoRelease> Parse(string json)
        {
            var list = new List<MoonoRelease>();
            if (JsonNode.Parse(json) is not JsonArray releases) throw new FormatException("the releases list is not a JSON array");
            foreach (var node in releases)
            {
                if (node is not JsonObject r) continue;
                if (r["draft"]?.GetValue<bool>() == true) continue;
                var tag = r["tag_name"]?.GetValue<string>();
                var url = r["html_url"]?.GetValue<string>();
                if (tag == null || url == null) continue;
                list.Add(new MoonoRelease(tag, r["name"]?.GetValue<string>() ?? tag, r["prerelease"]?.GetValue<bool>() ?? false, url,
                    r["body"]?.GetValue<string>() ?? ""));
            }
            return list;
        }

        /// The releases of this build's channel newer than `current`, newest first.
        public static List<MoonoRelease> Newer(IEnumerable<MoonoRelease> releases, string current, bool u60)
        {
            var ours = Version(current);
            if (ours == null) return [];
            return releases
                .Where(r => r.Prerelease == u60 && Version(r.Tag) is { } v && string.CompareOrdinal(v, ours) > 0)
                .Where(r => !Superseded(r))
                .OrderByDescending(r => Version(r.Tag), StringComparer.Ordinal)
                .ToList();
        }

        /// A release marked superseded: in its title, or by its notes' first words
        /// ("**Superseded:** get the latest U60 build", as older U60 builds are marked). A mention
        /// further on ("replaces a superseded build") does not count.
        public static bool Superseded(MoonoRelease r) =>
            r.Title.Contains("Superseded", StringComparison.OrdinalIgnoreCase)
            || r.Body.TrimStart().TrimStart('*', '#', '>', ' ').StartsWith("Superseded", StringComparison.OrdinalIgnoreCase);

        public static UpdateFound? Find(IEnumerable<MoonoRelease> releases, string current, bool u60)
        {
            var newer = Newer(releases, current, u60);
            if (newer.Count == 0) return null;
            var notes = Notes(newer, out bool truncated);
            return new UpdateFound(newer[0].Tag, newer[0].Url, notes, truncated);
        }

        static readonly Regex Heading = new(@"^(?:\*\*(?<h>[^*]+)\*\*|#{1,6}\s+(?<h>.+?))\s*:?\s*$", RegexOptions.CultureInvariant);
        static readonly Regex Bullet = new(@"^(?<indent>\s*)[-*+]\s+(?<text>.+)$", RegexOptions.CultureInvariant);
        static readonly Regex Link = new(@"!?\[(?<text>[^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
        static readonly Regex Bold = new(@"\*\*(?<text>.+?)\*\*|__(?<text>.+?)__", RegexOptions.CultureInvariant);
        static readonly Regex Code = new(@"`(?<text>[^`]*)`", RegexOptions.CultureInvariant);

        /// The releases' notes as TMP rich text: each release's title in bold, then its sections
        /// (a line that is all bold, or a Markdown heading) and their bullets, nested bullets
        /// indented. Every other paragraph (who the build is for, how to install) is left out.
        /// Bold stays bold, links become their text, code its text. Cut after MaxLines.
        public static string Notes(IReadOnlyList<MoonoRelease> releases, out bool truncated)
        {
            // Each line with its visible length (a release's title, a section, a bullet).
            var all = new List<(string Text, int Length)>();
            foreach (var release in releases)
            {
                if (all.Count > 0) all.Add(("", 0));
                all.Add(($"<size=125%><b>{Escape(release.Title)}</b></size>", release.Title.Length));
                foreach (var raw in release.Body.ReplaceLineEndings("\n").Split('\n'))
                {
                    var line = raw.TrimEnd();
                    if (Heading.Match(line) is { Success: true } h)
                        all.Add(($"<b>{Inline(h.Groups["h"].Value.Trim(), out int length)}</b>", length));
                    else if (Bullet.Match(line) is { Success: true } b)
                    {
                        bool nested = b.Groups["indent"].Value.Replace("\t", "  ").Length >= 2;
                        var body = Inline(b.Groups["text"].Value, out int length);
                        // Hanging indents: an indent part-way along the line moves the pen to it,
                        // so the text and its wrapped lines start there, not under the bullet.
                        all.Add(nested ? ($"<indent=1em>–<indent=2em>{body}</indent></indent>", length + 4)
                            : ($"•<indent=1em>{body}</indent>", length + 2));
                    }
                }
            }
            int kept = 0, lines = 0;
            for (; kept < all.Count; kept++)
            {
                int cost = Math.Max(1, (all[kept].Length + WrapAt - 1) / WrapAt);
                if (lines + cost > MaxLines) break;
                lines += cost;
            }
            truncated = kept < all.Count;
            // A cut leaves no heading, blank line or bullet that introduces a list ("Text:")
            // hanging at the end.
            if (truncated)
                while (kept > 0 && (all[kept - 1].Length == 0 || all[kept - 1].Text.StartsWith("<b>", StringComparison.Ordinal)
                                    || all[kept - 1].Text.StartsWith("<size", StringComparison.Ordinal)
                                    || all[kept - 1].Text.EndsWith(":</indent>", StringComparison.Ordinal)))
                    kept--;
            return string.Join("\n", all.Take(kept).Select(l => l.Text));
        }

        /// A line's Markdown as TMP rich text; `length`: its visible characters.
        static string Inline(string markdown, out int length)
        {
            var plain = Code.Replace(Link.Replace(markdown, "${text}"), "${text}");
            length = Bold.Replace(plain, "${text}").Length;
            var parts = new StringBuilder();
            int at = 0;
            foreach (Match m in Bold.Matches(plain))
            {
                parts.Append(Escape(plain[at..m.Index])).Append("<b>").Append(Escape(m.Groups["text"].Value)).Append("</b>");
                at = m.Index + m.Length;
            }
            parts.Append(Escape(plain[at..]));
            return parts.ToString();
        }

        /// Text with no tags of its own: a `<` is shown as itself.
        static string Escape(string text) => text.Replace("<", "<noparse><</noparse>");

        /// This build's release tag: ONI_UPDATE_TAG, else the `MoonoRelease` assembly metadata;
        /// null for a build that is no release.
        public static string? CurrentTag()
        {
            var forced = Environment.GetEnvironmentVariable("ONI_UPDATE_TAG");
            if (!string.IsNullOrEmpty(forced)) return forced;
            foreach (var a in typeof(UpdateCheck).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                if (a is System.Reflection.AssemblyMetadataAttribute { Key: "MoonoRelease", Value: { Length: > 0 } value }) return value;
            return null;
        }

        static UpdateFound? result;
        static int done;

        /// The check's result: null while it runs, or when there is no newer release.
        public static UpdateFound? Result => Volatile.Read(ref result);
        public static bool Done => Volatile.Read(ref done) != 0;

        /// Starts the check on a background thread (once).
        public static void Start(string persistentDataPath, bool u60, Action<string> log)
        {
            var current = CurrentTag();
            var url = Environment.GetEnvironmentVariable("ONI_UPDATE_RELEASES_URL");
            bool overridden = !string.IsNullOrEmpty(url);
            if (current == null || url == "none")
            {
                Volatile.Write(ref done, 1);
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    var releases = await Releases(overridden ? url! : ReleasesUrl, overridden ? null : CachePath(persistentDataPath), DateTime.UtcNow);
                    var found = Find(releases, current, u60);
                    Volatile.Write(ref result, found);
                    log(found == null ? $"update check: {current} is the newest" : $"update check: {found.Tag} is out (this build is {current})");
                }
                catch (Exception e)
                {
                    // Offline, rate-limited, a changed format: no badge, one line.
                    log($"update check: no answer ({e.GetType().Name}: {e.Message})");
                }
                finally
                {
                    Volatile.Write(ref done, 1);
                }
            });
        }

        /// The releases list: from the cache when it is younger than CacheFor (`cache` null: never
        /// cached), else fetched (and cached).
        static async Task<List<MoonoRelease>> Releases(string url, string? cache, DateTime now)
        {
            if (cache != null && ReadCache(cache, now) is { } cached) return cached;
            string json;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                using var http = new HttpClient { Timeout = Timeout };
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("OniMoono-update-check");
                using var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync();
            }
            else json = await File.ReadAllTextAsync(uri is { IsFile: true } ? uri.LocalPath : url);
            var releases = Parse(json);
            if (cache != null) WriteCache(cache, now, releases);
            return releases;
        }

        static List<MoonoRelease>? ReadCache(string path, DateTime now)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return null;
                var checkedAt = root["checked"]!.GetValue<DateTime>().ToUniversalTime();
                if (now - checkedAt > CacheFor || checkedAt > now) return null;
                return Parse(root["releases"]!.ToJsonString());
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void WriteCache(string path, DateTime now, List<MoonoRelease> releases)
        {
            try
            {
                var list = new JsonArray();
                foreach (var r in releases)
                    list.Add(new JsonObject { ["tag_name"] = r.Tag, ["name"] = r.Title, ["prerelease"] = r.Prerelease, ["html_url"] = r.Url, ["body"] = r.Body });
                var root = new JsonObject { ["checked"] = now.ToUniversalTime(), ["releases"] = list };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", root.ToJsonString(), new UTF8Encoding(false));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
