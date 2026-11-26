#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The main menu's "Updated" badge (src/OniMoono.Host/UpdatedNotice.cs, compiled in here):
    /// when it shows, how it goes, and the notes it lists, on the notes file the updated-badge
    /// specs use (tests/e2e/fixtures/update-notes.json).
    public class UpdatedNoticeTests
    {
        static string Fixture(string name, [CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here)!, "..", "e2e", "fixtures", name);

        static List<MoonoRelease> Notes() => UpdateCheck.Parse(File.ReadAllText(Fixture("update-notes.json")));

        /// Launches in a row: each one's badge, carrying the state from one to the next.
        static List<UpdatedNotice?> Run(ref UpdatedState? state, params string[] versions)
        {
            var shown = new List<UpdatedNotice?>();
            foreach (var v in versions)
            {
                var (next, show) = Updated.Decide(state, v);
                state = next;
                shown.Add(show);
            }
            return shown;
        }

        [Fact]
        public void AFreshInstallRecordsItsVersionAndShowsNothing()
        {
            UpdatedState? state = null;
            Assert.Equal([null, null], Run(ref state, "2026.11.20.01", "2026.11.20.01"));
            Assert.Equal(new UpdatedState("2026.11.20.01", null), state);
        }

        [Fact]
        public void AnUpgradeShowsOnTheNextThreeLaunchesThenStops()
        {
            UpdatedState? state = new("2026.11.20.01", null);
            var shown = Run(ref state, "2026.11.24.01", "2026.11.24.01", "2026.11.24.01", "2026.11.24.01", "2026.11.24.01");
            Assert.Equal([1, 2, 3], shown.Take(3).Select(s => s!.Launches));
            Assert.All(shown.Take(3), s => Assert.Equal(("2026.11.20.01", "2026.11.24.01"), (s!.From, s.To)));
            Assert.Equal([null, null], shown.Skip(3));
            Assert.Equal(new UpdatedState("2026.11.24.01", null), state);
        }

        /// Several updates skipped: the badge covers every version after the one last run.
        [Fact]
        public void SkippedVersionsAllShow()
        {
            UpdatedState? state = new("2026.11.20.01", null);
            var shown = Assert.Single(Run(ref state, "2026.11.26.01"))!;
            var badge = Updated.Badge(null, shown, "v2026.11.26.01", Notes(), beta: false)!;
            Assert.True(badge.Updated);
            Assert.Equal("v2026.11.26.01", badge.Tag);
            Assert.Equal("https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.26.01", badge.Url);
            // Newest first; the one run before (11.20.01) and older ones left out.
            Assert.Equal(["v2026.11.26.01", "v2026.11.24.01", "v2026.11.22.02"], Updated.Since(Notes(), shown, beta: false).Select(r => r.Tag));
            Assert.True(badge.Notes.IndexOf("2026.11.26.01") < badge.Notes.IndexOf("2026.11.24.01"));
            Assert.DoesNotContain("2026.11.20.01", badge.Notes);

            // Updated again before the badge was seen: it still starts at the version last seen.
            UpdatedState? again = new("2026.11.22.02", new UpdatedNotice("2026.11.20.01", "2026.11.22.02", 1));
            Assert.Equal(new UpdatedNotice("2026.11.20.01", "2026.11.26.01", 1), Assert.Single(Run(ref again, "2026.11.26.01")));
        }

        [Fact]
        public void ADowngradeShowsNothingNorTheWayBackUp()
        {
            UpdatedState? state = new("2026.11.24.01", null);
            Assert.Equal([null, null, null], Run(ref state, "2026.11.20.01", "2026.11.22.02", "2026.11.24.01"));
            Assert.Equal(new UpdatedState("2026.11.24.01", null), state);
            // A version past the one run before shows, from that one.
            Assert.Equal(new UpdatedNotice("2026.11.24.01", "2026.11.26.01", 1), Assert.Single(Run(ref state, "2026.11.26.01")));
        }

        /// `--use-version` pinning an older version while a badge is due: nothing while pinned,
        /// and the badge is gone on the way back up.
        [Fact]
        public void APinnedOlderVersionShowsNothing()
        {
            UpdatedState? state = new("2026.11.20.01", null);
            Assert.NotNull(Assert.Single(Run(ref state, "2026.11.24.01")));
            Assert.Equal([null, null, null], Run(ref state, "2026.11.20.01", "2026.11.20.01", "2026.11.24.01"));
        }

        [Fact]
        public void HoverOrClickDismisses()
        {
            UpdatedState? state = new("2026.11.20.01", null);
            Assert.NotNull(Assert.Single(Run(ref state, "2026.11.24.01")));
            state = Updated.Dismiss(state!);
            Assert.Equal([null, null], Run(ref state, "2026.11.24.01", "2026.11.24.01"));
        }

        [Fact]
        public void UpdateAvailableWins()
        {
            var notice = new UpdatedNotice("2026.11.20.01", "2026.11.24.01", 1);
            var available = new UpdateFound("v2026.11.26.01", "https://example.invalid/26", "notes", false);
            var badge = Updated.Badge(available, notice, "v2026.11.24.01", Notes(), beta: false)!;
            Assert.False(badge.Updated);
            Assert.Equal(("v2026.11.26.01", "https://example.invalid/26", "notes"), (badge.Tag, badge.Url, badge.Notes));
            Assert.True(Updated.Badge(null, notice, "v2026.11.24.01", Notes(), beta: false)!.Updated);
            Assert.Null(Updated.Badge(null, null, "v2026.11.24.01", Notes(), beta: false));
        }

        /// No notes to read (a GitHub install with the check off): the label, no notes, and a
        /// click opens the release page.
        [Fact]
        public void WithoutNotesTheBadgeLinksTheReleasePage()
        {
            var notice = new UpdatedNotice("2026.11.20.01", "2026.11.24.01", 1);
            var badge = Updated.Badge(null, notice, "v2026.11.24.01", null, beta: false)!;
            Assert.Equal(("", "https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.24.01"), (badge.Notes, badge.Url));
        }

        [Fact]
        public void TheStateFileRoundTrips()
        {
            var dir = Path.Combine(Path.GetTempPath(), "moono-updated-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var path = Updated.StatePath(dir);
                Assert.EndsWith(Path.Combine("oni-moono", "last-version.json"), path);
                Assert.Null(Updated.Read(path));
                var state = new UpdatedState("2026.11.24.01", new UpdatedNotice("2026.11.20.01", "2026.11.24.01", 2));
                Updated.Write(path, state);
                Assert.Equal(state, Updated.Read(path));
                File.WriteAllText(path, "{ not json");
                Assert.Null(Updated.Read(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
