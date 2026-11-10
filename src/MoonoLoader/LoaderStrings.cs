#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MoonoLoader
{
    /// The loader mod's strings (STRINGS.MOONO.LOADER.…), translated in Moono's own .po files
    /// (translations/CODE.po, which the item carries in each player's translations/ folder) and
    /// listed in translations/moono.pot with the Moono Tweaks screen's (TweakStrings.All, which
    /// compiles this file in). Plain .NET: the loader, the host and the tests compile it.
    internal static class LoaderStrings
    {
        public const string Prefix = "STRINGS.MOONO.LOADER.";

        public const string Title = "TITLE", Intro = "INTRO", StepCopy = "STEP_COPY", StepPaste = "STEP_PASTE", StepRestart = "STEP_RESTART",
            DisableHint = "DISABLE_HINT", NotNow = "NOT_NOW", Copy = "COPY", Copied = "COPIED", Installed = "INSTALLED", Failed = "FAILED",
            ModsButton = "MODS_BUTTON", ModsTip = "MODS_TIP";

        /// Each string's name (the key's last part), English and where it shows.
        public static readonly (string Name, string English, string Note)[] All =
        {
            (Title, "Play with Moono?", "The loader mod's dialog in the original game: its title"),
            (Intro, "Moono makes Oxygen Not Included run faster. To play with it:", "The dialog's first line, above three numbered steps"),
            (StepCopy, "Click Copy launch option. This installs Moono and copies:", "Step 1, above the launch-option line; Copy launch option is the COPY button"),
            (StepPaste, "In Steam, right-click Oxygen Not Included → Properties → Launch Options, and paste.", "Step 2"),
            (StepRestart, "Quit the game and press Play.", "Step 3"),
            (DisableHint, "To stop seeing this, disable the Moono mod.", "A small line under the steps"),
            (NotNow, "Not now", "The dialog's button that closes it"),
            (Copy, "Copy launch option", "The dialog's main button"),
            (Copied, "Copied ✓", "The main button's text once it has copied the line"),
            (Installed, "Moono installed. Launch option copied.", "Step 1's text once the button installed Moono and copied the line"),
            (Failed, "Moono could not be installed: {0}", "Step 1's text when the install fails; {0}: why (in English)"),
            (ModsButton, "Play with Moono", "The button on the item's row in the Mods screen that shows the dialog"),
            (ModsTip, "Install Moono and copy the launch option for Steam", "That button's tooltip"),
        };

        /// The text of the string `name`: `translated` (key → text, as the game's
        /// Localization.LoadStringsFile reads a .po) where it has the key with a text keeping the
        /// English's placeholders, else the English.
        public static string Text(IReadOnlyDictionary<string, string>? translated, string name)
        {
            var english = All.First(s => s.Name == name).English;
            return translated != null && translated.TryGetValue(Prefix + name, out var text) && !string.IsNullOrEmpty(text)
                && Placeholders(text).SequenceEqual(Placeholders(english)) ? text : english;
        }

        static readonly Regex placeholder = new Regex(@"\{[^{}]*\}|<[^<>]*>", RegexOptions.CultureInvariant);

        static IEnumerable<string> Placeholders(string text) =>
            placeholder.Matches(text).Cast<Match>().Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal);

        /// The launch-option line shortened to `max` characters for the dialog: the start of its
        /// path left out at a folder boundary, an ellipsis in its place
        /// (`"…\oni-moono\versioned\OniMoono.exe" -- %command%`). The clipboard gets the whole line.
        public static string Shorten(string line, int max)
        {
            if (line.Length <= max || line.Length < 2 || line[0] != '"') return line;
            int from = line.Length - (max - 2);
            int boundary = line.IndexOfAny(new[] { '/', '\\' }, from);
            int close = line.IndexOf('"', 1);
            if (boundary < 0 || (close > 0 && boundary > close)) boundary = from;
            return "\"…" + line.Substring(boundary);
        }
    }
}
