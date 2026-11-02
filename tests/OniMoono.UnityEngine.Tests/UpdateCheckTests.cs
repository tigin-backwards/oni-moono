using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Host;
using Xunit;

namespace OniMoono.Tests
{
    /// The main menu's update check (src/OniMoono.Host/UpdateCheck.cs, compiled in here), on the
    /// releases list the update-badge spec uses (tests/e2e/fixtures/update-releases.json): which
    /// releases count for each channel, and the notes the badge's tooltip shows.
    public class UpdateCheckTests
    {
        static System.Collections.Generic.List<MoonoRelease> Fixture([CallerFilePath] string here = "") =>
            UpdateCheck.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "e2e", "fixtures", "update-releases.json")));

        [Fact]
        public void TagsCompareAsDateAndCounter()
        {
            Assert.Equal("2026.11.03.01", UpdateCheck.Version("v2026.11.03.01"));
            Assert.Equal("2026.11.03.01", UpdateCheck.Version("v2026.11.03.01-u60"));
            Assert.Equal("2026.11.03.01", UpdateCheck.Version("2026.11.03.01"));
            Assert.Null(UpdateCheck.Version("v2026.11.03"));
            Assert.Null(UpdateCheck.Version("nightly"));
        }

        [Fact]
        public void NormalBuildsSeeNewerNormalReleasesOnly()
        {
            // Newer: 11.03.01 and 11.02.02; 11.03.02 is a U60 pre-release, 11.02.03 superseded.
            var newer = UpdateCheck.Newer(Fixture(), "v2026.11.01.04", u60: false);
            Assert.Equal(["v2026.11.03.01", "v2026.11.02.02"], newer.Select(r => r.Tag));
            Assert.Empty(UpdateCheck.Newer(Fixture(), "v2026.11.03.01", u60: false));
            Assert.Null(UpdateCheck.Find(Fixture(), "v2026.11.03.01", u60: false));
            Assert.Empty(UpdateCheck.Newer(Fixture(), "not-a-release", u60: false));
        }

        [Fact]
        public void U60BuildsSeeNewerPreReleasesOnly()
        {
            Assert.Equal(["v2026.11.03.02"], UpdateCheck.Newer(Fixture(), "v2026.11.02.04", u60: true).Select(r => r.Tag));
            Assert.Equal(["v2026.11.03.02"], UpdateCheck.Newer(Fixture(), "v2026.11.02.04-u60", u60: true).Select(r => r.Tag));
            Assert.Empty(UpdateCheck.Newer(Fixture(), "v2026.11.03.02", u60: true));
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
            var found = UpdateCheck.Find(Fixture(), "v2026.11.02.04", u60: true)!;
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
            var found = UpdateCheck.Find(Fixture(), "v2026.11.01.04", u60: false)!;
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
