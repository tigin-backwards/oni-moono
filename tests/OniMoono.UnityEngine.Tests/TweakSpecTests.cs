#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using OniMoono.GameTweaks;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The e2e specs of Moono's own screens (tests/e2e/moono-tweaks-*.yaml, mod-overlaps-*.yaml,
    /// workshop-loader.yaml and the others) against the catalog (GameTweakNames, TweakStrings,
    /// translations/) and the tweaks' declared patches: the rows they name, the text they quote
    /// and what the overlap test mod overlaps. The specs check the search against the screen
    /// itself (E2eDriver's TweaksScreen), so a catalog change reaches them only through these:
    /// when one of these tests fails, change the spec line it names (and the table here).
    public class TweakSpecTests
    {
        static string E2e([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "e2e"));

        static string Translations([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "translations"));

        static string Src([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src"));

        static string Tools([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "tools"));

        static IEnumerable<(string Name, string Text)> Specs() =>
            Directory.GetFiles(E2e(), "moono-tweaks*.yaml").Order(StringComparer.Ordinal).Select(f => (Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8)));

        /// Every spec, by its path under tests/e2e (the recorded results left out).
        static IEnumerable<(string Name, string Text)> AllSpecs() =>
            Directory.GetFiles(E2e(), "*.yaml", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(E2e(), f).Replace('\\', '/'))
                .Where(n => !n.StartsWith("results", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(n => (n, File.ReadAllText(Path.Combine(E2e(), n), Encoding.UTF8)));

        static string? Spec(string name) => File.Exists(Path.Combine(E2e(), name)) ? File.ReadAllText(Path.Combine(E2e(), name), Encoding.UTF8) : null;

        static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

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

        /// A tweak a spec names: a row it finds (`**/MoonoTweaksScreen/**/NAME`, `…/Content/NAME`),
        /// a launch option (`--game-tweak NAME`), its `game_tweaks` (`NAME`, `-NAME`), a tweak a
        /// test variable turns off, or a log line it expects (`[engine] game tweak NAME`). Row
        /// names are lowercase; the screen's own parts (ListView, Content, NoMatches) are not.
        static readonly Regex[] TweakNames =
        [
            new(@"\*\*/MoonoTweaksScreen/\*\*/(?:Content/)?([a-z0-9][a-z0-9-]*)"),
            new(@"--game-tweak[ =]([a-z0-9][a-z0-9-]*)"),
            new(@"ONI_TWEAK_TEST_UNREVIEWED: ""?([a-z0-9][a-z0-9-]*)"),
            new(@"\[engine\\?\] game tweak ([a-z0-9][a-z0-9-]*)"),
        ];

        static readonly string[] Modes = ["exact", "all", "none"];

        [Fact]
        public void SpecsNameRowsTheCatalogHas()
        {
            var ids = GameTweakNames.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var problems = new List<string>();
            int names = 0;
            foreach (var (name, text) in AllSpecs())
            {
                var found = new List<(string Id, int Index)>();
                foreach (var pattern in TweakNames)
                    foreach (Match m in pattern.Matches(text)) found.Add((m.Groups[1].Value, m.Groups[1].Index));
                foreach (Match m in Regex.Matches(text, @"^game_tweaks: \[([^\]\n]*)\]", RegexOptions.Multiline))
                    foreach (Match item in Regex.Matches(m.Groups[1].Value, @"[^,\s""']+"))
                        found.Add((item.Value.TrimStart('-'), m.Groups[1].Index + item.Index));
                foreach (var (id, index) in found)
                {
                    names++;
                    if (!ids.Contains(id) && !Modes.Contains(id))
                        problems.Add($"tests/e2e/{name}:{LineOf(text, index)} names the tweak '{id}', which the catalog (GameTweakNames.Rows) no longer has");
                }
            }
            Assert.True(names >= 20, $"found {names} tweak names in the specs: TweakSpecTests.TweakNames no longer matches how they name them");
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

        /// What tools/OverlapTestMod patches, and what the screen makes of each: the row of the one
        /// tweak that patches the same method, and the overlap's grade. mod-overlaps-en.yaml and
        /// mod-overlaps-zh.yaml expect these rows, counts and tooltips.
        static readonly (string Tweak, string Type, string Method, PatchKind Kind, bool CanSkip, OverlapGrade Grade)[] TestModPatches =
        [
            ("fast-jobs", "JobManager", "Run", PatchKind.Transpiler, false, OverlapGrade.High),
            ("fast-anim-cleanup", "KBatchedAnimUpdater", "CleanUp", PatchKind.Prefix, true, OverlapGrade.High),
            ("fast-sublimation", "Sublimates", "OnSpawn", PatchKind.Postfix, false, OverlapGrade.Low),
            ("fast-smi-lookups", "StateMachineController", "GetSMI", PatchKind.Postfix, false, OverlapGrade.StepsAside),
        ];

        const string TestModTitle = "Overlap Test";

        static Overlap[] TestModOverlaps() => TestModPatches.Select(p => new Overlap
        {
            Tweak = p.Tweak, Method = p.Type + "." + p.Method, Mod = TestModTitle, Owner = "OverlapTestMod", Kind = p.Kind, CanSkip = p.CanSkip, Grade = p.Grade,
        }).ToArray();

        /// Each method the test mod patches is patched by exactly the tweak TestModPatches names,
        /// as the tweaks declare their patches (GameMember, which `--check-tweaks` holds them to):
        /// a tweak that starts patching one of them adds a row, an overlap and an icon to the
        /// mod-overlaps specs.
        [Fact]
        public void TestModOverlapsTheTweaksTheSpecsExpect()
        {
            var mod = File.ReadAllText(Path.Combine(Tools(), "OverlapTestMod", "OverlapTestMod.cs"));
            Assert.Contains($"title: \"{TestModTitle}\"", File.ReadAllText(Path.Combine(Tools(), "OverlapTestMod", "mod.yaml")));
            var sources = new[] { "OniMoono.GameTweaks", Path.Combine("OniMoono.Host", "GameTweaks") }
                .SelectMany(d => Directory.GetFiles(Path.Combine(Src(), d), "*.cs")).Order(StringComparer.Ordinal)
                .Select(f => (File: Path.GetRelativePath(Src(), f), Text: File.ReadAllText(f))).ToArray();
            var problems = new List<string>();
            foreach (var p in TestModPatches)
            {
                if (!mod.Contains($"typeof({p.Type})", StringComparison.Ordinal) || !mod.Contains(p.Method, StringComparison.Ordinal))
                    problems.Add($"tools/OverlapTestMod no longer patches {p.Type}.{p.Method}: update TweakSpecTests.TestModPatches and tests/e2e/mod-overlaps-*.yaml");
                var patching = new List<string>();
                var declared = new Regex(@"\[GameMember\(GameUse\.([A-Za-z |.]+), ""[^""]*\b" + Regex.Escape(p.Type) + "::" + Regex.Escape(p.Method) + @"\(");
                foreach (var (file, text) in sources)
                    foreach (Match m in declared.Matches(text))
                        if (Regex.IsMatch(m.Groups[1].Value, @"\b(Prefix|Postfix|Transpiler|Finalizer)\b"))
                        {
                            var name = Regex.Match(text, @"const string Name = ""([a-z0-9-]+)""");
                            patching.Add(name.Success ? name.Groups[1].Value : file);
                        }
                patching = patching.Distinct().Order(StringComparer.Ordinal).ToList();
                if (patching.Count != 1 || patching[0] != p.Tweak)
                    problems.Add($"{p.Type}.{p.Method}, which tools/OverlapTestMod patches, is patched by {(patching.Count == 0 ? "no tweak" : string.Join(", ", patching))}, "
                        + $"not by {p.Tweak} alone: tests/e2e/mod-overlaps-*.yaml expect {p.Tweak}'s row only; change TweakSpecTests.TestModPatches and those specs' rows, counts and tooltips");
            }
            var overlaps = TestModOverlaps();
            var counts = new Dictionary<string, int>
            {
                ["High"] = overlaps.Count(o => o.Grade == OverlapGrade.High),
                ["Low"] = overlaps.Count(o => o.Grade == OverlapGrade.Low),
                ["StepsAside"] = overlaps.Count(o => o.Grade == OverlapGrade.StepsAside),
            };
            int checkedCounts = 0;
            foreach (var (name, text) in AllSpecs().Where(s => s.Text.Contains("test_mods: [OverlapTestMod]") && s.Name.StartsWith("mod-overlaps", StringComparison.Ordinal)))
                foreach (Match m in Regex.Matches(text, @"ModOverlaps::(High|Low|StepsAside)"": \[?(\d+)"))
                {
                    checkedCounts++;
                    if (int.Parse(m.Groups[2].Value) != counts[m.Groups[1].Value])
                        problems.Add($"tests/e2e/{name}:{LineOf(text, m.Index)} expects {m.Groups[2].Value} {m.Groups[1].Value} overlaps; the test mod makes {counts[m.Groups[1].Value]} (TweakSpecTests.TestModPatches)");
                }
            Assert.True(checkedCounts >= 6, $"found {checkedCounts} overlap counts in the mod-overlaps specs: TweakSpecTests' pattern no longer matches how they check them");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        /// The catalog's strings in a spec's language (`language: CODE`; English without one), by key.
        static Func<string, string> StringsOf(string spec)
        {
            var language = Regex.Match(spec, @"^language: (\w+)", RegexOptions.Multiline);
            Dictionary<string, string>? po = null;
            if (language.Success)
                po = TweakTranslationTests.Po.Parse(File.ReadAllText(Path.Combine(Translations(), language.Groups[1].Value.Split('_')[0] + ".po"), Encoding.UTF8))
                    .Where(e => e.Context != null).ToDictionary(e => e.Context!, e => e.Str, StringComparer.Ordinal);
            var keys = TweakStrings.All.Select(s => s.Key).ToArray();
            var resolved = TweakStrings.Resolve(po);
            return key => Array.IndexOf(keys, key) is var i and >= 0 ? resolved[i] : throw new InvalidOperationException("no string " + key);
        }

        static Func<string, string> Ui(Func<string, string> strings) => name => strings(TweakStrings.Prefix + "UI." + name);

        static string Loader(Func<string, string> strings, string name) => strings("STRINGS.MOONO.LOADER." + name);

        /// A row's overlap lines with the test mod loaded, as its tooltip ends.
        static string OverlapTip(Func<string, string> strings, string tweak) =>
            ModOverlapsCore.Tooltip(TestModOverlaps().Where(o => o.Tweak == tweak), Ui(strings), "FFB040");

        static string Detail(Func<string, string> strings, string tweak) =>
            strings(TweakStrings.RowKey(GameTweakNames.Rows.First(r => r.Id == tweak), "DETAIL"));

        /// The text of Moono's own screens that the specs quote (a `text:` or `value:`, as YAML
        /// reads it: `~TEXT` contained, `/REGEX/` matched, else the whole text), and what the
        /// screen shows there, from the catalog in the spec's language. A spec this branch does
        /// not have is skipped.
        static readonly (string Spec, string Quote, Func<Func<string, string>, string> Shows)[] Quotes =
        [
            ("moono-tweaks-search-en.yaml", "No tweaks match the search.", s => Ui(s)("NO_MATCHES")),
            ("moono-tweaks-search-zh.yaml", "没有与搜索匹配的调整项。", s => Ui(s)("NO_MATCHES")),
            ("moono-tweaks-unavailable.yaml", "NOT AVAILABLE", s => Ui(s)("NOT_AVAILABLE")),
            ("moono-tweaks-unavailable.yaml", "Not available on this game version.", s => Ui(s)("NOT_AVAILABLE_TIP")),
            ("moono-tweaks-unavailable.yaml", "~Not available on this game version.", s => Ui(s)("NOT_AVAILABLE_TIP")),
            ("moono-tweaks-unavailable.yaml", @"/^(<color=#FFB040>)?Not available on this game version\.(<\/color>)?\n\n\S/",
                s => $"<color=#FFB040>{Ui(s)("NOT_AVAILABLE_TIP")}</color>\n\n{Detail(s, "fast-jobs")}"),
            ("mod-overlaps-en.yaml", "2 overlaps", s => string.Format(Ui(s)("OVERLAPS_MANY"), ModOverlapsCore.HighCount(TestModOverlaps()))),
            ("mod-overlaps-en.yaml", "~Open Moono Tweaks", s => Ui(s)("OVERLAPS_TIP")),
            ("mod-overlaps-en.yaml", "~Overlap Test also patches JobManager.Run (it rewrites the game's code).", s => OverlapTip(s, "fast-jobs")),
            ("mod-overlaps-en.yaml", @"/JobManager.Run \(it rewrites the game's code\).<\/color>\nIf something looks wrong, turn this tweak off.$/", s => OverlapTip(s, "fast-jobs")),
            ("mod-overlaps-en.yaml", "~Overlap Test also patches KBatchedAnimUpdater.CleanUp (it can skip the game's code).", s => OverlapTip(s, "fast-anim-cleanup")),
            ("mod-overlaps-en.yaml", "~Overlap Test also patches Sublimates.OnSpawn.\nIf something looks wrong, turn this tweak off.", s => OverlapTip(s, "fast-sublimation")),
            ("mod-overlaps-en.yaml", "~Off while Overlap Test is loaded, as it patches StateMachineController.GetSMI.", s => OverlapTip(s, "fast-smi-lookups")),
            ("mod-overlaps-badges.yaml", "2 overlaps", s => string.Format(Ui(s)("OVERLAPS_MANY"), ModOverlapsCore.HighCount(TestModOverlaps()))),
            ("mod-overlaps-zh.yaml", "2 处重叠", s => string.Format(Ui(s)("OVERLAPS_MANY"), ModOverlapsCore.HighCount(TestModOverlaps()))),
            ("mod-overlaps-zh.yaml", "~打开 Moono 调整项", s => Ui(s)("OVERLAPS_TIP")),
            ("mod-overlaps-zh.yaml", "~Overlap Test 也修补了 JobManager.Run（它会改写游戏的代码）。", s => OverlapTip(s, "fast-jobs")),
            ("mod-overlaps-zh.yaml", "~Overlap Test 也修补了 Sublimates.OnSpawn。", s => OverlapTip(s, "fast-sublimation")),
            ("mod-overlaps-zh.yaml", "~加载 Overlap Test 时关闭", s => OverlapTip(s, "fast-smi-lookups")),
            ("workshop-loader.yaml", "Play with Moono?", s => Loader(s, "TITLE")),
            ("workshop-loader.yaml", "1. Click Install & copy launch option to install Moono and copy:", s => "1. " + Loader(s, "STEP_COPY")),
            ("workshop-loader.yaml", "3. Quit the game and press Play.", s => "3. " + Loader(s, "STEP_RESTART")),
            ("workshop-loader.yaml", "To stop seeing this, disable the Moono mod.", s => Loader(s, "DISABLE_HINT")),
            ("workshop-loader.yaml", "Not now", s => Loader(s, "NOT_NOW")),
            ("workshop-loader.yaml", "Install & copy launch option", s => Loader(s, "COPY")),
            ("workshop-loader.yaml", "Copied ✓", s => Loader(s, "COPIED")),
            ("workshop-loader.yaml", "1. Moono installed. Launch option copied.", s => "1. " + Loader(s, "INSTALLED")),
        ];

        /// The double-quoted YAML scalars of a spec, as YAML reads them, with where each starts.
        static IEnumerable<(string Value, int Index)> Scalars(string text)
        {
            foreach (Match m in Regex.Matches(text, @"""((?:[^""\\\n]|\\.)*)"""))
                yield return (Regex.Replace(m.Groups[1].Value, @"\\(.)", e => e.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c }), m.Index);
        }

        [Fact]
        public void SpecsQuoteWhatTheScreensShow()
        {
            var problems = new List<string>();
            int found = 0;
            foreach (var (spec, quote, shows) in Quotes)
            {
                var text = Spec(spec);
                if (text == null) continue;
                var at = Scalars(text).Where(s => s.Value == quote).Select(s => s.Index).DefaultIfEmpty(-1).First();
                if (at < 0)
                {
                    problems.Add($"tests/e2e/{spec} no longer quotes \"{quote}\": update TweakSpecTests.Quotes");
                    continue;
                }
                found++;
                var shown = shows(StringsOf(text));
                bool holds = quote.StartsWith('~') ? shown.Contains(quote[1..], StringComparison.Ordinal)
                    : quote.Length > 1 && quote.StartsWith('/') && quote.EndsWith('/') ? Regex.IsMatch(shown, quote[1..^1])
                    : shown == quote;
                if (!holds)
                    problems.Add($"tests/e2e/{spec}:{LineOf(text, at)} expects \"{quote}\", but the screen now shows: \"{shown}\" (change the spec line and TweakSpecTests.Quotes)");
            }
            Assert.True(found >= 10, $"found {found} of TweakSpecTests.Quotes in the specs");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        /// Each text the specs of Moono's own screens check on them (their `text:` and `value:`)
        /// is in Quotes, checked against the catalog, or a row's tooltip that
        /// SpecsQuoteTheRowsTooltips checks; only the few listed here are not the catalog's.
        static readonly string[] NotTheCatalogs =
        [
            "Search", // the game's own placeholder (UI.UISIDESCREENS.TREEFILTERABLESIDESCREEN.SEARCH_PLACEHOLDER)
            "MODS", // the game's main menu button
            "~OniMoono\" -- %command%", // the launch-option line, a path
        ];

        [Fact]
        public void SpecsQuoteOnlyCheckedText()
        {
            var quoted = Quotes.Select(q => (q.Spec, q.Quote)).ToHashSet();
            var problems = new List<string>();
            foreach (var (name, text) in AllSpecs().Where(s => Regex.IsMatch(s.Text, @"^(tweaks_menu|moono_item): true", RegexOptions.Multiline)))
                foreach (var line in text.Split('\n').Select((l, i) => (Text: l, Number: i + 1)))
                {
                    if (line.Text.TrimStart().StartsWith('#') || line.Text.TrimStart().StartsWith("- type:", StringComparison.Ordinal)
                        || line.Text.Contains("FilterInputField/**/Text", StringComparison.Ordinal)) continue;
                    foreach (Match m in Regex.Matches(line.Text, @"\b(?:text|value): (""(?:[^""\\]|\\.)*"")"))
                    {
                        var value = Scalars(m.Groups[1].Value).First().Value;
                        if (value.Contains("--game-tweak ", StringComparison.Ordinal) || value.StartsWith("~--", StringComparison.Ordinal)
                            || Regex.IsMatch(value, @"^v\d{4}\.\d\d\.\d\d\.\d\d") || NotTheCatalogs.Contains(value) || quoted.Contains((name, value))) continue;
                        problems.Add($"tests/e2e/{name}:{line.Number} checks \"{value}\", which no test checks against the catalog: add it to TweakSpecTests.Quotes");
                    }
                }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
    }
}
