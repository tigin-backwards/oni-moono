using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The main menu's update check (src/OniMoono.Host/UpdateCheck.cs, compiled in here), on the
    /// releases list the update-badge spec uses (tests/e2e/fixtures/update-releases.json): which
    /// releases count for each channel, and the notes the badge's tooltip shows.
    public class UpdateCheckTests
    {
        static string FixturePath([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here)!, "..", "e2e", "fixtures", "update-releases.json");

        static System.Collections.Generic.List<MoonoRelease> Fixture() => UpdateCheck.Parse(File.ReadAllText(FixturePath()));

        /// An install from the Workshop item (VersionedStart.UpdatesFrom set) reads no releases
        /// list and shows no badge, with one log line; any other install checks as before.
        [Fact]
        public void WorkshopInstallsCheckNothing()
        {
            var log = new System.Collections.Generic.List<string>();
            var data = Path.Combine(Path.GetTempPath(), "moono-update-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                // A list that can't be read: a check that read it would log "no answer".
                UpdateCheck.Start(data, false, log.Add, "/steam/workshop/content/457140/1", "v2026.11.01.01", Path.Combine(data, "missing.json"));
                Assert.True(UpdateCheck.Done);
                Assert.Null(UpdateCheck.Result);
                Assert.Equal("update check: not done, Steam updates this install from the Workshop item (/steam/workshop/content/457140/1)", Assert.Single(log));

                log.Clear();
                UpdateCheck.Start(data, false, log.Add, null, "v2026.11.01.01", FixturePath());
                Assert.True(SpinWait.SpinUntil(() => UpdateCheck.Done, 10_000));
                Assert.NotNull(UpdateCheck.Result);
                Assert.StartsWith("update check: ", Assert.Single(log));
                Assert.DoesNotContain("not done", log[0]);
                Assert.False(Directory.Exists(data));
            }
            finally
            {
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
        }

        [Fact]
        public void TagsCompareAsDateAndCounter()
        {
            Assert.Equal("2026.11.03.01", UpdateCheck.Version("v2026.11.03.01"));
            Assert.Equal("2026.11.03.01", UpdateCheck.Version("v2026.11.03.01-u60"));
            Assert.Equal("2026.11.03.01", UpdateCheck.Version("2026.11.03.01"));
            Assert.Null(UpdateCheck.Version("v2026.11.03"));
            Assert.Null(UpdateCheck.Version("nightly"));
        }

        /// The Moono Tweaks screen's version label: the release's name as GitHub titles it, else
        /// the short commit of a build that is no release.
        [Fact]
        public void VersionLabelIsTheReleaseNameElseTheShortCommit()
        {
            Assert.Equal("Moono 2026.11.19.01", UpdateCheck.VersionLabel("v2026.11.19.01", "1.0.0+095fcc721abcdef0123456789abcdef01234567"));
            Assert.Equal("Moono 2026.11.19.02", UpdateCheck.VersionLabel("v2026.11.19.02-u60", null));
            Assert.Equal("Moono test-tag", UpdateCheck.VersionLabel("vtest-tag", null));
            Assert.Equal("Moono 095fcc721", UpdateCheck.VersionLabel(null, "1.0.0+095fcc721abcdef0123456789abcdef01234567"));
            Assert.Equal("Moono 095fcc721", UpdateCheck.VersionLabel("", "1.0.0+095fcc721abcdef0123456789abcdef01234567"));
            Assert.Equal("Moono abc", UpdateCheck.VersionLabel(null, "1.0.0+abc"));
            Assert.Null(UpdateCheck.VersionLabel(null, "1.0.0"));
            Assert.Null(UpdateCheck.VersionLabel(null, "1.0.0+"));
            Assert.Null(UpdateCheck.VersionLabel(null, null));
            // As moono-tweaks-en.yaml checks the header.
            const string spec = @"^Moono (\d{4}\.\d\d\.\d\d\.\d\d|[0-9a-f]{1,9})$";
            Assert.Matches(spec, UpdateCheck.VersionLabel("v2026.11.19.02-u60", null));
            Assert.Matches(spec, UpdateCheck.VersionLabel(null, "1.0.0+095fcc721abcdef0123456789abcdef01234567"));
        }

        [Fact]
        public void NormalBuildsSeeNewerNormalReleasesOnly()
        {
            // Newer: 11.03.01 and 11.02.02; 11.03.02 is a U60 pre-release, 11.02.03 superseded.
            var newer = UpdateCheck.Newer(Fixture(), "v2026.11.01.04", beta: false);
            Assert.Equal(["v2026.11.03.01", "v2026.11.02.02"], newer.Select(r => r.Tag));
            Assert.Empty(UpdateCheck.Newer(Fixture(), "v2026.11.03.01", beta: false));
            Assert.Null(UpdateCheck.Find(Fixture(), "v2026.11.03.01", beta: false));
            Assert.Empty(UpdateCheck.Newer(Fixture(), "not-a-release", beta: false));
        }

        [Fact]
        public void BetaBuildsSeeNewerPreReleasesOnly()
        {
            Assert.Equal(["v2026.11.03.02"], UpdateCheck.Newer(Fixture(), "v2026.11.02.04", beta: true).Select(r => r.Tag));
            Assert.Equal(["v2026.11.03.02"], UpdateCheck.Newer(Fixture(), "v2026.11.02.04-u60", beta: true).Select(r => r.Tag));
            Assert.Empty(UpdateCheck.Newer(Fixture(), "v2026.11.03.02", beta: true));
        }

        /// The U60 cut-over (docs/RELEASES.md): the last U59 release (v2026.11.19.03) and the last
        /// U60 beta pre-release (v2026.11.19.04) match as builds before it did (their U59 builds
        /// normal releases, their U60 builds pre-releases), so the U60 builds see only the one
        /// bridging pre-release, whose notes send them to the latest normal release; U59 builds and
        /// new builds (for the current game, U60) see the new normal releases and not the bridge.
        [Fact]
        public void TheCutOverReachesOldU59AndU60BuildsAndNewOnes()
        {
            static MoonoRelease R(string tag, string title, bool pre, string body = "**Fixes**\n- Something.") =>
                new(tag, title, pre, "https://github.com/tigin-backwards/oni-moono/releases/tag/" + tag, body);
            MoonoRelease[] releases =
            [
                R("v2026.11.16.04", "2026.11.16.04 (U60 Beta)", true),
                R("v2026.11.19.03", "2026.11.19.03", false),
                R("v2026.11.19.04", "2026.11.19.04 (U60 Beta)", true),
                R("v2026.11.20.01", "2026.11.20.01", false, "**U60 is the normal game now**\n- Moono follows it."),
                R("v2026.11.20.02", "2026.11.20.02 (U60 is the normal game now)", true,
                    "**U60 is the normal game now**\n- Moono's updates are normal releases from now on: get the [latest release](https://github.com/tigin-backwards/oni-moono/releases/latest)."),
                R("v2026.11.21.01", "2026.11.21.01", false),
            ];
            // An old U60 build (a pre-release before the cut-over): the bridge alone, link and notes.
            var u60 = UpdateCheck.Find(releases, "v2026.11.19.04", beta: true)!;
            Assert.Equal("v2026.11.20.02", u60.Tag);
            Assert.Contains("normal releases from now on: get the latest release.", u60.Notes);
            Assert.Equal(["v2026.11.20.02", "v2026.11.19.04"], UpdateCheck.Newer(releases, "v2026.11.16.04", beta: true).Select(r => r.Tag));
            // An old U59 build (a normal release before the cut-over): the new normal releases.
            Assert.Equal(["v2026.11.21.01", "v2026.11.20.01"], UpdateCheck.Newer(releases, "v2026.11.19.03", beta: false).Select(r => r.Tag));
            // A new build: newer normal releases only, never the bridge.
            Assert.Equal(["v2026.11.21.01"], UpdateCheck.Newer(releases, "v2026.11.20.01", beta: false).Select(r => r.Tag));
            Assert.Null(UpdateCheck.Find(releases, "v2026.11.21.01", beta: false));
            // Marked superseded, the bridge would hide from old U60 builds: it must not be.
            releases[4] = releases[4] with { Title = "2026.11.20.02 (Superseded)" };
            Assert.Null(UpdateCheck.Find(releases, "v2026.11.19.04", beta: true));
        }

        /// As the real U60 builds are marked when a newer one ships (first words of the notes);
        /// a mention further on is not a mark.
        [Fact]
        public void SupersededIsTheTitleOrTheNotesFirstWords()
        {
            static MoonoRelease R(string title, string body) => new("v2026.11.04.01", title, true, "u", body);
            Assert.True(UpdateCheck.Superseded(R("2026.11.02.01 (U60 Beta)", "**Superseded:** get [the latest U60 build](https://x.invalid).\n\n**Only for**...")));
            Assert.True(UpdateCheck.Superseded(R("2026.11.02.01 (U60 Beta, Superseded)", "**Only for** ...")));
            Assert.False(UpdateCheck.Superseded(R("2026.11.04.01 (U60 Beta)", "**Only for** ...\n\n**Fixes**\n- Replaces the superseded 2026.11.02.01.")));
        }

        [Fact]
        public void NotesKeepSectionsAndBulletsOnly()
        {
            var found = UpdateCheck.Find(Fixture(), "v2026.11.02.04", beta: true)!;
            Assert.Equal("https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.03.02", found.Url);
            var notes = found.Notes;
            Assert.StartsWith("<size=125%><b>2026.11.03.02 (U60 Beta)</b></size>\n<b>Faster</b>\n•<indent=1em>Drawing costs less", notes);
            // The preamble (who the build is for), its links and the install footer are left out.
            Assert.DoesNotContain("Only for", notes);
            Assert.DoesNotContain("An update to the U60 beta", notes);
            Assert.DoesNotContain("Install or update", notes);
            Assert.DoesNotContain("](", notes);
            Assert.Contains("<b>Fixes</b>", notes);
            Assert.Contains("(\"Faster worker threads\" tweak)", notes);
            Assert.False(found.Truncated);
        }

        [Fact]
        public void NotesConvertMarkdownAndCutAtTheirBudget()
        {
            var found = UpdateCheck.Find(Fixture(), "v2026.11.01.04", beta: false)!;
            Assert.Equal("v2026.11.03.01", found.Tag);
            Assert.True(found.Truncated);
            var lines = found.Notes.Split('\n');
            Assert.Equal("<size=125%><b>2026.11.03.01</b></size>", lines[0]);
            Assert.Contains("<size=125%><b>2026.11.02.02</b></size>", lines);
            Assert.True(lines.Length <= UpdateCheck.MaxLines);
            // Code spans lose their backticks; a cut leaves no heading or list intro at the end.
            Assert.DoesNotContain("`", found.Notes);
            Assert.DoesNotContain("**", found.Notes);
            Assert.StartsWith("•", lines[^1]);
            Assert.DoesNotContain("Text:", lines[^1]);
        }

        [Fact]
        public void NestedBulletsLinksAndAngleBrackets()
        {
            var release = new MoonoRelease("v2026.12.01.01", "2026.12.01.01", false, "https://example.invalid/r",
                "For the normal game. See [the U60 build](https://example.invalid).\n\n**Fixes**\n- Text:\n  - Labels **fit** in [All Diagnostics](https://x.invalid).\n"
                + "- A <b>tag</b> shows as written.\n\nInstall or update: run the setup.\n");
            var notes = UpdateCheck.Notes([release], out bool truncated);
            Assert.False(truncated);
            Assert.Equal(
                "<size=125%><b>2026.12.01.01</b></size>\n<b>Fixes</b>\n•<indent=1em>Text:</indent>\n"
                + "<indent=1em>–<indent=2em>Labels <b>fit</b> in All Diagnostics.</indent></indent>\n"
                + "•<indent=1em>A <noparse><</noparse>b>tag<noparse><</noparse>/b> shows as written.</indent>", notes);
        }
    }
}
