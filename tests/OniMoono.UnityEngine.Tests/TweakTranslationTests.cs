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
    /// The Moono Tweaks screen's translations (TweakStrings, translations/): the template
    /// generated from the catalog, every translation readable and matching it, and the English
    /// fallback. A translation's stale or missing strings are reported, not failures: the screen
    /// shows those in English.
    public class TweakTranslationTests
    {
        readonly ITestOutputHelper output;

        public TweakTranslationTests(ITestOutputHelper output) => this.output = output;

        static string Translations([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "translations"));

        [Fact]
        public void KeysAreUniqueAndTextIsQuotable()
        {
            var keys = TweakStrings.All.Select(s => s.Key).ToArray();
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
            foreach (var s in TweakStrings.All)
            {
                Assert.Matches(@"^STRINGS\.MOONO\.TWEAKS\.[A-Z0-9_]+(\.[A-Z0-9_]+)*$", s.Key);
                Assert.False(string.IsNullOrWhiteSpace(s.English), $"{s.Key} has no English");
                // The game's .po reader keeps a backslash as written: the text has none.
                Assert.DoesNotContain('\\', s.English);
            }
            // Every row's title and one-liner, every group and every choice has its string.
            foreach (var row in GameTweakNames.Rows)
            {
                Assert.Contains(TweakStrings.RowKey(row, "TITLE"), keys);
                Assert.Contains(TweakStrings.RowKey(row, "EFFECT"), keys);
                Assert.Equal(row.Detail.Length > 0, keys.Contains(TweakStrings.RowKey(row, "DETAIL")));
                if (GameTweakNames.GainText(row) is { } gain) Assert.Contains(TweakStrings.GainKey(gain.Gain), keys);
                foreach (var choice in row.Choices ?? []) Assert.Contains(TweakStrings.ChoiceKey(row, choice), keys);
            }
            foreach (var group in GameTweakNames.Groups) Assert.Contains(TweakStrings.GroupKey(group), keys);
        }

        /// The labels the screen and the update badge ask for (MoonoTweaksMenu.Ui("NAME") in the game-side source,
        /// which the tests cannot compile) are the catalog's, and the catalog has no others.
        [Fact]
        public void ScreenLabelsAreTheCatalogs()
        {
            var source = string.Join("\n", new[] { "MoonoTweaksMenu.cs", "UpdateBadge.cs", "OverlapBadge.cs" }
                .Select(f => File.ReadAllText(Path.Combine(Translations(), "..", "src", "OniMoono.GameTweaks", f))));
            var asked = new HashSet<string>(StringComparer.Ordinal);
            // The rows' overlap lines name their labels in ModOverlapsCore (`ui(reason)`).
            var core = File.ReadAllText(Path.Combine(Translations(), "..", "src", "OniMoono.GameTweaks", "ModOverlapsCore.cs"));
            foreach (Match m in Regex.Matches(core, @"""(OVERLAP_[A-Z_]+)"""))
                asked.Add(m.Groups[1].Value);
            foreach (var line in source.Split('\n').Where(l => Regex.IsMatch(l, @"\bUi\(")))
                foreach (Match m in Regex.Matches(line, @"Ui\((?:[^()""]*\?\s*)?""([A-Z_]+)""(?:\s*:\s*""([A-Z_]+)"")?"))
                    for (int g = 1; g < m.Groups.Count; g++)
                        if (m.Groups[g].Success) asked.Add(m.Groups[g].Value);
            var names = TweakStrings.Ui.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Empty(asked.Except(names));
            Assert.Empty(names.Except(asked));
        }

        /// translations/moono.pot is TweakStrings.Pot(); `ONI_UPDATE_DOCS=1` rewrites it instead
        /// of failing.
        [Fact]
        public void TemplateMatchesTheCatalog()
        {
            var path = Path.Combine(Translations(), "moono.pot");
            var expected = TweakStrings.Pot();
            var current = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
            if (current != expected && Environment.GetEnvironmentVariable("ONI_UPDATE_DOCS") == "1")
            {
                File.WriteAllText(path, expected);
                return;
            }
            Assert.True(current == expected, "translations/moono.pot differs from TweakStrings.All; run the unit tests with ONI_UPDATE_DOCS=1 to regenerate it");
            var template = Po.Parse(current).Where(e => e.Context != null).ToArray();
            Assert.Equal(TweakStrings.All.Select(s => (s.Key, s.English)), template.Select(e => (e.Context!, e.Id)));
            Assert.All(template, e => Assert.Equal("", e.Str));
        }

        /// Each translations/CODE.po reads, has only the catalog's keys, each once, and keeps each
        /// English's placeholders and tags. Strings whose English has changed since (stale) and
        /// ones it lacks or leaves empty are listed in the test's output.
        [Fact]
        public void TranslationsMatchTheCatalog()
        {
            var files = Directory.GetFiles(Translations(), "*.po").Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);
            var english = TweakStrings.All.ToDictionary(s => s.Key, s => s.English, StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                List<Po.Entry> entries;
                try
                {
                    entries = Po.Parse(File.ReadAllText(file, Encoding.UTF8));
                }
                catch (FormatException e)
                {
                    problems.Add($"{name}: {e.Message}");
                    continue;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int stale = 0, translated = 0;
                foreach (var e in entries)
                {
                    if (e.Context == null) continue; // the header
                    if (!seen.Add(e.Context)) problems.Add($"{name}:{e.Line}: {e.Context} is there twice");
                    if (!english.TryGetValue(e.Context, out var current))
                    {
                        problems.Add($"{name}:{e.Line}: unknown key {e.Context}");
                        continue;
                    }
                    if (e.Str.Length == 0) continue;
                    translated++;
                    if (!TweakStrings.SameMarkup(current, e.Str))
                        problems.Add($"{name}:{e.Line}: {e.Context} has {Show(TweakStrings.MarkupOf(e.Str))}, the English {Show(TweakStrings.MarkupOf(current))}");
                    if (e.Id != current)
                    {
                        stale++;
                        output.WriteLine($"{name}:{e.Line}: stale: {e.Context} was translated from \"{e.Id}\", the English is now \"{current}\"");
                    }
                }
                var missing = english.Keys.Where(k => !entries.Any(e => e.Context == k && e.Str.Length > 0)).ToArray();
                foreach (var key in missing) output.WriteLine($"{name}: missing: {key} (shown in English)");
                output.WriteLine($"{name}: {translated} of {english.Count} strings translated, {stale} stale, {missing.Length} missing");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        static string Show(string[] markup) => markup.Length == 0 ? "no placeholders" : string.Join(" ", markup);

        [Fact]
        public void ResolveFallsBackToEnglish()
        {
            var title = TweakStrings.Prefix + "UI.TITLE";
            var locked = TweakStrings.Prefix + "UI.LAUNCH_OPTION_LINE";
            var later = TweakStrings.Prefix + "UI.LATER";
            int Index(string key) => Array.FindIndex(TweakStrings.All, s => s.Key == key);
            var texts = TweakStrings.Resolve(new Dictionary<string, string>
            {
                [title] = "TITRE",
                [locked] = "sans le paramètre", // {0} lost: English
                [later] = "",
            });
            Assert.Equal(TweakStrings.All.Length, texts.Length);
            Assert.Equal("TITRE", texts[Index(title)]);
            Assert.Equal("Launch option: {0}", texts[Index(locked)]);
            Assert.Equal("LATER", texts[Index(later)]);
            Assert.Equal(TweakStrings.All.Select(s => s.English), TweakStrings.Resolve(null));
            Assert.True(TweakStrings.SameMarkup("a {0} <b>{1}</b>", "{1} x <b>{0}</b>"));
            Assert.False(TweakStrings.SameMarkup("{0}: {1}", "{0}"));
            Assert.False(TweakStrings.SameMarkup("<color=#fff>x</color>", "x"));
        }

        [Fact]
        public void PoReaderReadsTheGamesFormat()
        {
            var entries = Po.Parse("# comment\nmsgid \"\"\nmsgstr \"\"\n\"Language: zh\\n\"\n\n#. note\nmsgctxt \"K\"\nmsgid \"a \\\"b\\\"\\nc\"\nmsgstr \"\"\n\"x\"\n\"y\"\n");
            Assert.Equal(2, entries.Count);
            Assert.Null(entries[0].Context);
            Assert.Equal("K", entries[1].Context);
            Assert.Equal("a \"b\"\nc", entries[1].Id);
            Assert.Equal("xy", entries[1].Str);
            Assert.Throws<FormatException>(() => Po.Parse("msgctxt \"K\"\nmsgid \"a\\tb\"\nmsgstr \"\"\n"));
            Assert.Throws<FormatException>(() => Po.Parse("msgctxt \"K\"\nmsgstr \"x\"\n"));
            Assert.Throws<FormatException>(() => Po.Parse("msgctxt K\nmsgid \"a\"\nmsgstr \"\"\n"));
        }

        /// The .po subset the game's reader (Localization.LoadStringsFile) reads: entries
        /// separated by blank lines, `#` comments, `msgctxt` (the key), `msgid` and `msgstr`, each
        /// a quoted string continued on lines that start with a quote, with `\"` and `\n` its only
        /// escapes. Anything else is a FormatException naming its line.
        internal static class Po
        {
            public sealed record Entry(string? Context, string Id, string Str, int Line);

            public static List<Entry> Parse(string text)
            {
                var lines = text.ReplaceLineEndings("\n").Split('\n');
                var entries = new List<Entry>();
                var fields = new Dictionary<string, string>();
                string? last = null;
                int start = 0;
                void End(int line)
                {
                    if (fields.Count == 0) return;
                    if (!fields.TryGetValue("msgid", out var id) || !fields.TryGetValue("msgstr", out var str))
                        throw new FormatException($"line {start}: an entry needs a msgid and a msgstr");
                    fields.TryGetValue("msgctxt", out var context);
                    if (context == null && id.Length > 0) throw new FormatException($"line {start}: an entry needs a msgctxt (its key)");
                    entries.Add(new Entry(context, id, str, start));
                    fields.Clear();
                    last = null;
                }
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    int number = i + 1;
                    if (line.Length == 0) End(number);
                    else if (line.StartsWith('#')) continue;
                    else if (line.StartsWith('"'))
                    {
                        if (last == null) throw new FormatException($"line {number}: a continued string with nothing before it");
                        fields[last] += Unquote(line, number);
                    }
                    else
                    {
                        var m = Regex.Match(line, @"^(msgctxt|msgid|msgstr) ("".*)$");
                        if (!m.Success) throw new FormatException($"line {number}: not a msgctxt, msgid, msgstr, comment or continued string: {line}");
                        if (fields.Count == 0) start = number;
                        last = m.Groups[1].Value;
                        if (fields.ContainsKey(last)) throw new FormatException($"line {number}: a second {last} in one entry (a blank line ends an entry)");
                        fields[last] = Unquote(m.Groups[2].Value, number);
                    }
                }
                End(lines.Length);
                return entries;
            }

            static string Unquote(string quoted, int line)
            {
                if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"')
                    throw new FormatException($"line {line}: a string is in double quotes: {quoted}");
                var s = new StringBuilder();
                for (int i = 1; i < quoted.Length - 1; i++)
                {
                    char c = quoted[i];
                    if (c == '"') throw new FormatException($"line {line}: a quote in a string is written \\\"");
                    if (c != '\\') { s.Append(c); continue; }
                    char next = i + 1 < quoted.Length - 1 ? quoted[++i] : ' ';
                    if (next == 'n') s.Append('\n');
                    else if (next == '"') s.Append('"');
                    else throw new FormatException($"line {line}: \\{next}: the game reads only \\\" and \\n as escapes");
                }
                return s.ToString();
            }
        }
    }
}
