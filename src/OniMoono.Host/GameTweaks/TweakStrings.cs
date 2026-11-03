#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OniMoono.Host.GameTweaks
{
    /// One translatable string of the Moono Tweaks screen: its key (STRINGS.MOONO.TWEAKS....),
    /// its English and a note for translators on where it shows.
    internal sealed record TweakString(string Key, string English, string Note);

    /// Every string the Moono Tweaks screen shows, keyed as the game keys its own (STRINGS.…),
    /// for translations in the game's .po format: the screen's own labels (Ui) and the catalog's
    /// text (GameTweakNames: groups, rows, measured gains, choices). English is the catalog's;
    /// translations/moono.pot is generated from All (GameTweakCatalogTests,
    /// `ONI_UPDATE_DOCS=1` rewrites it) and translations/CODE.po holds a language. Launch
    /// options and paths are not translated. Plain .NET with no game types (the tests compile it
    /// in); the game side (OniMoono.GameTweaks.MoonoTweaksMenu) reads the .po for the game's
    /// language and resolves it with Resolve.
    internal static class TweakStrings
    {
        public const string Prefix = "STRINGS.MOONO.TWEAKS.";

        /// The screen's own labels: the last part of each key (STRINGS.MOONO.TWEAKS.UI.NAME, as
        /// the screen names them), its English and where it shows. `{0}`, `{1}`: what the screen
        /// puts there.
        public static readonly (string Name, string English, string Note)[] Ui =
        [
            ("TITLE", "MOONO TWEAKS", "The main menu's button and the screen's title"),
            ("CHANGES_APPLY_ON_RESTART", "Changes apply when the game restarts.", "The footer, left of the buttons (one line)"),
            ("RESET_TO_DEFAULTS", "RESET TO DEFAULTS", "Footer button"),
            ("RESTART_TO_APPLY", "RESTART TO APPLY", "Footer button"),
            ("CHANGES_GAMEPLAY_NOTE", "These trade a little exactness for speed. Most are off by default.", "Under the Changes gameplay heading"),
            ("BUG_FIXES_NOTE", "These fix bugs in the game, so they change what the original does.", "Under the Bug fixes heading"),
            ("NO_MATCHES", "No tweaks match the search.", "In the list when the search box matches no row"),
            ("LAUNCH_OPTION", "LAUNCH OPTION", "Beside a row the launch options set (one line)"),
            ("LAUNCH_OPTION_LINE", "Launch option: {0}", "A row's tooltip, last line; {0}: the launch option"),
            ("LOCKED_ON", "Set by the launch options (on)", "Tooltip of a toggle the launch options set"),
            ("LOCKED_OFF", "Set by the launch options (off)", "Tooltip of a toggle the launch options set"),
            ("LOCKED_CHOICE", "Set by the launch options ({0})", "Tooltip of a choice button the launch options set; {0}: the choice"),
            ("NEXT_CHOICE", "Click for the next choice: {0}", "Tooltip of a choice button; {0}: the choices, LIST_SEPARATOR between them"),
            ("LIST_SEPARATOR", ", ", "Between the choices in NEXT_CHOICE"),
            ("SAVE_FAILED", "The settings could not be saved to {0}:\n{1}", "Dialog; {0}: the settings file, {1}: the error"),
            ("SAVED_RESTART", "Your changes are saved and apply when the game restarts.", "Dialog on closing with changes"),
            ("LATER", "LATER", "The dialog's button beside Restart"),
            ("UPDATE_HEADLINE", "Moo! An update is available", "The main menu's update badge, first line"),
            ("UPDATE_MORE", "…more on the release page", "The update badge's tooltip, last line, when the notes are cut"),
            ("OVERLAP_REWRITES", "{0} also patches {1} (it rewrites the game's code).", "A row's tooltip: a mod's transpiler on a method the tweak changes; {0}: the mod, {1}: the methods"),
            ("OVERLAP_SKIPS", "{0} also patches {1} (it can skip the game's code).", "A row's tooltip: a mod's prefix that can skip a method the tweak replaces; {0}: the mod, {1}: the methods"),
            ("OVERLAP_PATCHES", "{0} also patches {1}.", "A row's tooltip: a mod's other patches of methods the tweak changes; {0}: the mod, {1}: the methods"),
            ("OVERLAP_STEPS_ASIDE", "Off while {0} is loaded, as it patches {1}.", "A row's tooltip: the tweak turns itself off for the mod; {0}: the mod, {1}: the methods"),
            ("OVERLAP_ADVICE", "If something looks wrong, turn this tweak off.", "A row's tooltip, after the mods that patch the same methods"),
            ("OVERLAP_MORE_METHODS", "{0} and {1} more", "In a row's tooltip; {0}: the first methods, LIST_SEPARATOR between them, {1}: how many more"),
            ("OVERLAPS_ONE", "1 overlap", "The main menu's badge beside the Moono Tweaks button: one tweak overlaps a mod"),
            ("OVERLAPS_MANY", "{0} overlaps", "The main menu's badge; {0}: how many tweaks and mods overlap"),
            ("OVERLAPS_TIP", "Some mods patch the same code as Moono's tweaks. Open Moono Tweaks to see which.", "The main menu's overlap badge's tooltip"),
        ];

        public static string GroupKey(TweakGroup group) => Prefix + "GROUPS." + group.Key;
        public static string RowKey(TweakInfo row, string part) => Prefix + "ROWS." + Name(row.Id) + "." + part;
        public static string GainKey(TweakGain gain) => Prefix + "GAINS." + gain.Key;
        public static string ChoiceKey(TweakInfo row, string choice) => Prefix + "CHOICES." + Name(row.Id) + "." + Name(choice);
        static string Name(string id) => id.ToUpperInvariant().Replace('-', '_');
        /// A choice's English: its value with spaces for hyphens ("low-memory": "low memory").
        public static string ChoiceEnglish(string choice) => choice.Replace('-', ' ');

        /// Every string, in the screen's order: its labels, then each group, its rows (title,
        /// one-liner, details, choices) and the gains as their tooltips show them.
        public static readonly TweakString[] All = Collect();

        static TweakString[] Collect()
        {
            var all = Ui.Select(u => new TweakString(Prefix + "UI." + u.Name, u.English, u.Note)).ToList();
            var gains = new HashSet<TweakGain>();
            foreach (var group in GameTweakNames.Groups)
            {
                all.Add(new(GroupKey(group), group.Title, "Section heading (shown in capitals)"));
                foreach (var row in GameTweakNames.Rows.Where(r => r.Group == group))
                {
                    all.Add(new(RowKey(row, "TITLE"), row.Title, $"Row title ({row.Id})"));
                    all.Add(new(RowKey(row, "EFFECT"), row.Effect, $"The line under the title ({row.Id})"));
                    if (row.Detail.Length > 0) all.Add(new(RowKey(row, "DETAIL"), row.Detail, $"Tooltip: how it works ({row.Id})"));
                    foreach (var choice in row.Choices ?? [])
                        all.Add(new(ChoiceKey(row, choice), ChoiceEnglish(choice), $"A choice on the row's button (shown in capitals) and in its tooltip ({row.Id})"));
                    if (GameTweakNames.GainText(row) is { } gain && gains.Add(gain.Gain))
                        all.Add(new(GainKey(gain.Gain), gain.Text, $"Tooltip: the measured gain ({(row.Gain != null ? row.Id : "every " + group.Title + " row")})"));
                }
            }
            return all.ToArray();
        }

        /// Each of All's strings in a language: `translated` (key → text, as the game's
        /// Localization.LoadStringsFile reads a .po) where it has the key with a text that keeps
        /// the English's placeholders and tags (SameMarkup), else the English.
        public static string[] Resolve(IReadOnlyDictionary<string, string>? translated) =>
            All.Select(s => translated != null && translated.TryGetValue(s.Key, out var text) && !string.IsNullOrEmpty(text)
                && SameMarkup(s.English, text) ? text : s.English).ToArray();

        static readonly Regex Markup = new(@"\{[^{}]*\}|<[^<>]*>", RegexOptions.CultureInvariant);

        /// The format placeholders (`{0}`) and rich-text tags (`<color=…>`) of a string, sorted:
        /// a translation must have the same ones as the English.
        public static string[] MarkupOf(string text) => Markup.Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

        public static bool SameMarkup(string english, string translated) => MarkupOf(english).SequenceEqual(MarkupOf(translated));

        /// A string as a .po file quotes it: `"` and line breaks escaped. (The game's reader
        /// unescapes only those two: a backslash stays as written, so the catalog has none.)
        public static string Quote(string text) => "\"" + text.Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        /// translations/moono.pot: every string with its key as msgctxt, its English as msgid and
        /// an empty msgstr.
        public static string Pot()
        {
            var pot = new StringBuilder();
            pot.Append("# The Moono Tweaks screen's strings: the template for a translation.\n");
            pot.Append("# Generated from src/OniMoono.Host/GameTweaks/TweakStrings.cs; ONI_UPDATE_DOCS=1 with the unit tests rewrites it.\n");
            pot.Append("# Copy it to CODE.po (the game's language code: zh, ko, ru, ...) and fill in each msgstr (docs/TWEAKS.md).\n");
            pot.Append("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n");
            foreach (var s in All)
            {
                pot.Append("\n#. ").Append(s.Note).Append('\n');
                pot.Append("msgctxt ").Append(Quote(s.Key)).Append('\n');
                pot.Append("msgid ").Append(Quote(s.English)).Append('\n');
                pot.Append("msgstr \"\"\n");
            }
            return pot.ToString();
        }
    }
}
