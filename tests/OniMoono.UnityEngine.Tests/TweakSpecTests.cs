#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The Moono Tweaks screen's e2e specs (tests/e2e/moono-tweaks-*.yaml) against the catalog
    /// (GameTweakNames, TweakStrings, translations/): the few rows they name and the tooltip text
    /// they quote. The specs check the search against the screen itself (E2eDriver's
    /// TweaksScreen), so a catalog change reaches them only through these: when one of these
    /// tests fails, change the spec (and the table below) to another row that fits.
    public class TweakSpecTests
    {
        static string E2e([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "e2e"));

        static string Translations([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "translations"));

        static IEnumerable<(string Name, string Text)> Specs() =>
            Directory.GetFiles(E2e(), "moono-tweaks*.yaml").Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8)));

        /// The rows the specs take for what they show, and what each needs to be.
        static readonly (string Spec, string Row, string Needs, Func<TweakInfo, bool> Holds)[] Uses =
        [
            ("moono-tweaks-en.yaml", "frame-stats", "a row (row-tip shot)", _ => true),
            ("moono-tweaks-en.yaml", "gpu", "a row with choices (choice-tip shot)", r => r.Choices != null),
            ("moono-tweaks-en.yaml", "brain-rate", "a row of Changes gameplay (gameplay-tip shot)", r => r.Group == GameTweakNames.ChangesGameplay),
            ("moono-tweaks-en.yaml", "fix-load-screen-clicks", "a row of Bug fixes (bugfix-tip)", r => r.Group == GameTweakNames.BugFixes),
            ("moono-tweaks-en.yaml", "fast-conveyor-paths", "a row with its own measured gain (gain-tip)", r => r.Gain != null),
            ("moono-tweaks-zh.yaml", "frame-stats", "a row (row-tip shot)", _ => true),
            ("moono-tweaks-zh.yaml", "gpu", "a row with choices (choice-tip shot)", r => r.Choices != null),
            ("moono-tweaks-zh.yaml", "brain-rate", "a row of Changes gameplay (gameplay-tip shot)", r => r.Group == GameTweakNames.ChangesGameplay),
            ("moono-tweaks-zh.yaml", "fix-load-screen-clicks", "a row of Bug fixes (bugfix-tip)", r => r.Group == GameTweakNames.BugFixes),
            ("moono-tweaks-zh.yaml", "fast-conveyor-paths", "a row with its own measured gain (gain-tip)", r => r.Gain != null),
        ];

        /// A row a spec finds by name (`**/MoonoTweaksScreen/**/NAME`, `--game-tweak NAME`): row
        /// names are lowercase, the screen's own parts (ListView, Content, NoMatches) are not.
        static readonly Regex RowPath = new(@"\*\*/MoonoTweaksScreen/\*\*/([a-z0-9][a-z0-9-]*)|--game-tweak ([a-z0-9][a-z0-9-]*)");

        [Fact]
        public void SpecsNameRowsTheCatalogHas()
        {
            var ids = GameTweakNames.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var (name, text) in Specs())
                foreach (Match m in RowPath.Matches(text))
                {
                    var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!ids.Contains(id)) problems.Add($"tests/e2e/{name} names the row '{id}', which the catalog (GameTweakNames.Rows) no longer has");
                }
            Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        }

        [Fact]
        public void SpecsUseRowsThatFit()
        {
            var specs = Specs().ToDictionary(s => s.Name, s => s.Text);
            var problems = new List<string>();
            foreach (var (spec, id, needs, holds) in Uses)
            {
                Assert.True(specs.ContainsKey(spec), $"no tests/e2e/{spec}");
                if (!specs[spec].Contains("/" + id + "\"") && !specs[spec].Contains("/" + id + "/"))
                    problems.Add($"tests/e2e/{spec} no longer uses '{id}': update TweakSpecTests.Uses");
                var row = GameTweakNames.Rows.FirstOrDefault(r => r.Id == id);
                if (row == null || !holds(row))
                    problems.Add($"tests/e2e/{spec} takes '{id}' for {needs}, which it {(row == null ? "no longer is (no such row)" : "no longer is")}: pick another row in the spec and in TweakSpecTests.Uses");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        /// A tooltip a spec quotes (`text: "/^QUOTE[\\s\\S]*--game-tweak NAME$/"`) is that row's
        /// details or gain, in the spec's language (its translation, else the English the screen
        /// falls back to).
        [Fact]
        public void SpecsQuoteTheRowsTooltips()
        {
            var quote = new Regex(@"text: ""/\^?([^""\[/]+)\[\\\\s\\\\S\]\*--game-tweak ([a-z0-9-]+)\$/""");
            var keys = TweakStrings.All.Select(s => s.Key).ToArray();
            var problems = new List<string>();
            int quotes = 0;
            foreach (var (name, text) in Specs())
            {
                var language = Regex.Match(text, @"^language: (\w+)", RegexOptions.Multiline);
                Dictionary<string, string>? po = null;
                if (language.Success)
                {
                    var code = language.Groups[1].Value.Split('_')[0];
                    po = TweakTranslationTests.Po.Parse(File.ReadAllText(Path.Combine(Translations(), code + ".po"), Encoding.UTF8))
                        .Where(e => e.Context != null).ToDictionary(e => e.Context!, e => e.Str, StringComparer.Ordinal);
                }
                var resolved = TweakStrings.Resolve(po);
                string Text(string key) => Array.IndexOf(keys, key) is var i and >= 0 ? resolved[i] : "";
                foreach (Match m in quote.Matches(text))
                {
                    quotes++;
                    var (quoted, id) = (Regex.Unescape(m.Groups[1].Value), m.Groups[2].Value);
                    var row = GameTweakNames.Rows.FirstOrDefault(r => r.Id == id);
                    if (row == null) continue; // SpecsNameRowsTheCatalogHas says so
                    var tip = Text(TweakStrings.RowKey(row, "DETAIL"));
                    if (GameTweakNames.GainText(row) is { } gain) tip += "\n" + Text(TweakStrings.GainKey(gain.Gain));
                    if (!tip.Contains(quoted, StringComparison.Ordinal))
                        problems.Add($"tests/e2e/{name} expects '{quoted}' in {id}'s tooltip, which now reads: {tip}");
                }
            }
            Assert.True(quotes >= 4, $"found {quotes} quoted tooltips in the specs: TweakSpecTests' pattern no longer matches how they quote");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
    }
}
