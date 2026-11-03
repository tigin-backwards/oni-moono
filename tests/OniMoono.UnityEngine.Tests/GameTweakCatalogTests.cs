using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The Moono Tweaks catalog (src/OniMoono.Host/GameTweaks/GameTweakNames.cs, compiled in
    /// here): every row complete, and docs/TWEAKS.md's list generated from it.
    public class GameTweakCatalogTests
    {
        [Fact]
        public void EveryRowHasAGroupTitleAndEffect()
        {
            Assert.NotEmpty(GameTweakNames.Rows);
            foreach (var row in GameTweakNames.Rows)
            {
                Assert.False(string.IsNullOrWhiteSpace(row.Id));
                Assert.Contains(row.Group, GameTweakNames.Groups);
                Assert.False(string.IsNullOrWhiteSpace(row.Title), $"{row.Id} has no title");
                Assert.False(string.IsNullOrWhiteSpace(row.Effect), $"{row.Id} has no one-liner");
                // Sentence case: a capital first letter, and no capital after it but in a name.
                Assert.True(char.IsUpper(row.Title[0]), $"{row.Id}: '{row.Title}' should start with a capital");
                foreach (var word in row.Title.Split(' ').Skip(1))
                    Assert.True(!char.IsUpper(word[0]) || word is "Gassy" or "Moo" or "Direct3D" or "Windows" or "Load", $"{row.Id}: '{row.Title}' is not in sentence case ('{word}')");
                // The screen's font (the Mods screen's) draws a lowercase f as a capital F, which in
                // a title reads as a stray capital ("No Forced cleanup"): titles do without.
                Assert.True(!row.Title.Contains('f'), $"{row.Id}: '{row.Title}' has a lowercase f, which the screen's font draws as a capital");
                Assert.True(row.Effect.EndsWith('.'), $"{row.Id}: the one-liner should be a sentence");
                Assert.True(row.Effect.Length <= 100, $"{row.Id}: the one-liner is {row.Effect.Length} characters, more than one short line");
            }
            Assert.Equal(GameTweakNames.Rows.Length, GameTweakNames.Rows.Select(r => r.Id).Distinct().Count());
        }

        [Fact]
        public void GainsNameTheirSource()
        {
            var gains = GameTweakNames.Rows.Select(r => r.Gain).Concat(GameTweakNames.Groups.Select(g => g.Gain)).Where(g => g != null);
            Assert.NotEmpty(gains);
            foreach (var g in gains)
                Assert.All(new[] { g!.Effect, g.Colony, g.Platform, g.Measured, g.Source }, f => Assert.False(string.IsNullOrWhiteSpace(f)));
        }

        [Fact]
        public void RowsComeInGroupsWithChangesGameplayLast()
        {
            // Each group's rows together, in the order of Groups.
            var order = GameTweakNames.Rows.Select(r => Array.IndexOf(GameTweakNames.Groups, r.Group)).ToArray();
            Assert.Equal(order.OrderBy(i => i), order);
            Assert.Same(GameTweakNames.ChangesGameplay, GameTweakNames.Groups[^1]);
            Assert.Equal(new[] { "brain-rate", "fix-load-screen-clicks", "off-screen-conveyors", "off-world-fallers", "path-probe-repair", "slow-reachability" }, GameTweakNames.ChangingBehaviour.Order());
            // path-probe-repair and fix-load-screen-clicks change behaviour (out of `exact`, in the
            // Changes gameplay group) but are on by default (the player's choice); the others are off.
            Assert.Equal(GameTweakNames.ChangingBehaviour.Where(n => n is not ("path-probe-repair" or "fix-load-screen-clicks")).Order(), GameTweakNames.OffByDefault.Order());
            Assert.Same(GameTweakNames.ChangesGameplay, GameTweakNames.Rows.Single(r => r.Id == "path-probe-repair").Group);
            Assert.Same(GameTweakNames.ChangesGameplay, GameTweakNames.Rows.Single(r => r.Id == "fix-load-screen-clicks").Group);
            // Every typed tweak is a row; the options that aren't tweaks aren't `--game-tweak` names.
            Assert.All(GameTweakNames.Typed, n => Assert.Contains(n, GameTweakNames.All));
            Assert.DoesNotContain(GameTweakNames.SplashRow, GameTweakNames.All);
            Assert.DoesNotContain(GameTweakNames.FrameStatsRow, GameTweakNames.All);
        }

        /// docs/TWEAKS.md's list between its markers is GameTweakNames.Markdown();
        /// `ONI_UPDATE_DOCS=1` rewrites it there instead of failing.
        [Fact]
        public void TweaksDocMatchesTheCatalog()
        {
            var path = Path.Combine(RepoRoot(), "docs", "TWEAKS.md");
            var doc = File.ReadAllText(path).ReplaceLineEndings("\n");
            int begin = doc.IndexOf("<!-- tweaks:begin", StringComparison.Ordinal);
            const string endMarker = "<!-- tweaks:end -->";
            int end = doc.IndexOf(endMarker, StringComparison.Ordinal);
            Assert.True(begin >= 0 && end > begin, "docs/TWEAKS.md has no <!-- tweaks:begin --> ... <!-- tweaks:end --> block");
            var current = doc[begin..(end + endMarker.Length)];
            var expected = GameTweakNames.Markdown();
            if (current != expected && Environment.GetEnvironmentVariable("ONI_UPDATE_DOCS") == "1")
            {
                File.WriteAllText(path, doc[..begin] + expected + doc[(end + endMarker.Length)..]);
                return;
            }
            Assert.True(current == expected, "docs/TWEAKS.md's list differs from GameTweakNames.Rows; run the unit tests with ONI_UPDATE_DOCS=1 to regenerate it");
        }

        static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
