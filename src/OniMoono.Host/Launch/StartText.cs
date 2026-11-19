#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OniMoono.Host.Launch
{
    /// The messages Moono shows before the game loads (the game-version check, the standalone
    /// launch), in the player's language. The game's localization is not loaded yet, so the
    /// language is chosen here (PlayerLanguage) and the text read from the player's
    /// translations/CODE.po, the files the Moono Tweaks screen reads (STRINGS.MOONO.START.…, listed
    /// in translations/moono.pot through TweakStrings.All). A string a .po lacks, leaves empty or
    /// whose placeholders differ stays English. Plain .NET: the tests compile it in.
    internal static class StartText
    {
        public const string Prefix = "STRINGS.MOONO.START.";

        public const string GameIsOther = "GAME_IS_OTHER", GameUnknown = "GAME_UNKNOWN",
            NotFound = "NOT_FOUND", NoneChosen = "NONE_CHOSEN", SteamBusy = "STEAM_BUSY", NoSteam = "NO_STEAM",
            ChooseBox = "CHOOSE_BOX", ChooseDialog = "CHOOSE_DIALOG";

        /// Each string's name (the key's last part), English and where it shows. `{0}`, `{1}`:
        /// what the message puts there.
        public static readonly (string Name, string English, string Note)[] All =
        [
            (GameIsOther,
                "Your game is the {1} update of Oxygen Not Included, and this Moono build is for {2}, the current update.\n\n" +
                "To use this build, switch the game to {2}: in Steam, right-click Oxygen Not Included → Properties → Betas, and choose None.\n\n" +
                "Or get Moono for {1}:\n{0}\n\n" +
                "To play now without Moono, clear the game's launch options in Steam.",
                "Message box before the game starts: this build is for the current game update, the game is another one it knows (an older update kept as a Steam beta); {0}: the download page of Moono for the game's update, {1}: the game's update (U59), {2}: this build's (U60). None is the choice in Steam's Betas list"),
            (GameUnknown,
                "This Moono build doesn't support your version of Oxygen Not Included, and no Moono build supports it yet. New builds appear at:\n{0}\n\n" +
                "To play now without Moono, clear the game's launch options in Steam.",
                "Message box before the game starts: a game update no Moono build knows; {0}: the download page"),
            (NotFound, "Oxygen Not Included wasn't found in any Steam library. Install it in Steam, or start Moono from Steam's launch options.",
                "Message box when Moono is started directly and finds no game"),
            (NoneChosen, "Moono found several installs of Oxygen Not Included and none was chosen.",
                "Message box when Moono found several games and the player chose none"),
            (SteamBusy, "Steam has to finish installing or updating Oxygen Not Included first. Steam opens on the game's page; start Moono again once it is done.",
                "Message box when the game's install or update isn't finished"),
            (NoSteam, "Oxygen Not Included needs Steam, and Steam could not be started. Start Steam, then Moono again.",
                "Message box when Steam could not be started"),
            (ChooseBox, "Moono found {0} installs of Oxygen Not Included. Play the one at\n\n{1}\n\n(Yes: this one, kept for later starts. No: the next one.)",
                "Windows question with Yes, No and Cancel, once per game install; {0}: how many, {1}: the folder"),
            (ChooseDialog, "Moono found {0} installs of Oxygen Not Included. Which one should it play (kept for later starts)?",
                "Linux dialog above the list of game installs; {0}: how many"),
        ];

        /// key → text of the chosen language (Use); none: English.
        static IReadOnlyDictionary<string, string>? translated;

        /// The language for every later Text: a .po's strings (PlayerLanguage.Choice.Strings).
        public static void Use(IReadOnlyDictionary<string, string>? strings) => translated = strings;

        /// The string `name` in the chosen language, `args` in its placeholders.
        public static string Text(string name, params object[] args) => Text(translated, name, args);

        public static string Text(IReadOnlyDictionary<string, string>? strings, string name, params object[] args)
        {
            var english = All.First(s => s.Name == name).English;
            var text = strings != null && strings.TryGetValue(Prefix + name, out var t) && !string.IsNullOrEmpty(t)
                && Placeholders(t).SequenceEqual(Placeholders(english)) ? t : english;
            return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
        }

        static readonly Regex placeholder = new(@"\{[^{}]*\}|<[^<>]*>", RegexOptions.CultureInvariant);

        static IEnumerable<string> Placeholders(string text) => placeholder.Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal);

        /// The game update each Unity version shipped with: its name, whether it is a Steam beta
        /// (its builds are pre-releases, and look for updates among pre-releases only), and where
        /// its Moono builds are. U60 is the current game (master's releases); U59's last build is
        /// the frozen u59 line's last release.
        public sealed record Update(string Name, bool Beta, string Builds);

        public const string Releases = "https://github.com/tigin-backwards/oni-moono/releases";

        public static readonly IReadOnlyDictionary<string, Update> Updates = new Dictionary<string, Update>(StringComparer.Ordinal)
        {
            ["6000.3.5f2"] = new("U59", false, Releases + "/tag/v2026.11.19.03"),
            ["6000.3.17f1"] = new("U60", false, Releases + "/latest"),
        };

        /// The message for a game whose Unity version `game` is not this build's (`ours`): an
        /// update no build knows, or another known one (get its build, or switch the game to this
        /// build's update). The second fits a build for the current game, as master's are; a
        /// beta's build would need its own messages (docs/RELEASES.md, "A future beta", says
        /// where the U60 beta's are).
        public static string VersionMismatch(string ours, string game, IReadOnlyDictionary<string, string>? strings)
        {
            if (!Updates.TryGetValue(game, out var theirs)) return Text(strings, GameUnknown, Releases);
            var name = Updates.TryGetValue(ours, out var update) ? update.Name : "another update";
            return Text(strings, GameIsOther, theirs.Builds, theirs.Name, name);
        }

        public static string VersionMismatch(string ours, string game) => VersionMismatch(ours, game, translated);

        /// The log's line on the versions, in English with the Unity versions the message leaves out.
        public static string VersionDetail(string ours, string game)
        {
            static string Describe(string unity) => Updates.TryGetValue(unity, out var u) ? $"{u.Name} (Unity {unity})" : $"Unity {unity}";
            return $"game version check: this Moono build is for {Describe(ours)}, the game is {Describe(game)}";
        }
    }

    /// The player's language for StartText, as the game would choose it: its own choice in
    /// kplayerprefs.yaml (a language it ships, SelectedLanguageType Preinstalled with
    /// SelectedLanguageCode such as zh_klei; a translation mod, UGC, whose strings.po names its
    /// language; English, None), else the system's UI language. A choice the game can't have
    /// made (no file, no key, a damaged file) falls to the system's.
    internal static class PlayerLanguage
    {
        /// The language's code ("zh"; "" for English), why, and its .po's strings (null for
        /// English or when the player ships none for it).
        public sealed record Choice(string Code, string Why, string? PoPath, IReadOnlyDictionary<string, string>? Strings);

        /// The game's own codes (Localization.Locales): a code that contains one is that one, as
        /// Locale.MatchesCode says ("zh_klei" and "zh-CN" are zh).
        static readonly string[] gameCodes = ["zh", "ja", "ko", "ru", "th", "ar", "he"];

        /// The game's settings folder, where kplayerprefs.yaml and mods/ are (Util.RootFolder):
        /// Documents\Klei\OxygenNotIncluded on Windows, Unity's persistent data folder on Linux
        /// ($XDG_CONFIG_HOME/unity3d/Klei/Oxygen Not Included); under the run home in test runs,
        /// as RunHome and Paths place them.
        public static string SettingsFolder(string? runHome, bool windows, Func<string, string?> env, string home, string documents)
        {
            if (windows) return Path.Combine(runHome != null ? Path.Combine(runHome, "Documents") : documents, "Klei", "OxygenNotIncluded");
            var config = runHome != null ? Path.Combine(runHome, "config") : env("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");
            return Path.Combine(config, "unity3d", "Klei", "Oxygen Not Included");
        }

        public static Choice Choose(string settingsFolder, CultureInfo uiCulture, string translations)
        {
            var (code, why) = GameChoice(settingsFolder);
            if (code == null)
            {
                code = uiCulture.TwoLetterISOLanguageName is var two && two != "iv" && two != "en" ? Normalize(uiCulture.Name) : "";
                why = $"{why}; the system's language, {(uiCulture.Name.Length > 0 ? uiCulture.Name : "invariant")}";
            }
            if (code.Length == 0) return new Choice("", why!, null, null);
            int cut = code.IndexOfAny(['_', '-']);
            foreach (var name in cut > 0 ? new[] { code, code[..cut] } : [code])
            {
                var path = Path.Combine(translations, name + ".po");
                if (!File.Exists(path)) continue;
                try { return new Choice(code, why!, path, PoStrings.Read(File.ReadAllText(path, Encoding.UTF8))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                {
                    return new Choice(code, $"{why}; {path} could not be read ({e.Message}), English instead", null, null);
                }
            }
            return new Choice(code, $"{why}; no translation, English instead", null, null);
        }

        /// A code as the game's locale reads it: one of its own when the code contains it, else as is.
        static string Normalize(string code)
        {
            var lower = code.ToLowerInvariant();
            return gameCodes.FirstOrDefault(lower.Contains) ?? lower;
        }

        /// The game's choice: its code ("" for English), or null when it made none (with why).
        static (string? Code, string Why) GameChoice(string folder)
        {
            var path = Path.Combine(folder, "kplayerprefs.yaml");
            Dictionary<string, string> strings;
            try
            {
                if (!File.Exists(path)) return (null, "no kplayerprefs.yaml");
                strings = PrefsStrings(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                return (null, $"kplayerprefs.yaml unreadable ({e.Message})");
            }
            if (!strings.TryGetValue("SelectedLanguageType", out var type) || type.Length == 0) return (null, "no language in kplayerprefs.yaml");
            switch (type.ToLowerInvariant())
            {
                case "none":
                    return ("", "the game's language, English (kplayerprefs.yaml)");
                case "preinstalled":
                    var code = strings.GetValueOrDefault("SelectedLanguageCode") ?? "";
                    return code.Length > 0 && code != "en" ? (Normalize(code), $"the game's language, {code} (kplayerprefs.yaml)") : ("", "the game's language, English (kplayerprefs.yaml)");
                case "ugc":
                    var mod = strings.GetValueOrDefault("InstalledLanguage") ?? "";
                    if (mod.Length > 0 && !mod.Contains('/') && !mod.Contains('\\') && mod != "..")
                        foreach (var platform in new[] { "Steam", "Local", "Dev" })
                            if (ModLanguage(Path.Combine(folder, "mods", platform, mod, "strings.po")) is { } modCode)
                                return (Normalize(modCode), $"the game's language, {modCode} (translation mod {mod})");
                    return (null, $"the game's translation mod {(mod.Length > 0 ? mod : "(none named)")} not found");
                default:
                    return (null, $"unknown SelectedLanguageType {type} in kplayerprefs.yaml");
            }
        }

        /// A translation's language, from its header's `"Language: CODE\n"` line (as
        /// Localization.GetLanguageCode reads it); null without one.
        static string? ModLanguage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                    if (line.StartsWith("\"Language:", StringComparison.Ordinal))
                        return line["\"Language:".Length..].Trim().Replace("\\n", "").Replace("\"", "") is { Length: > 0 } code ? code : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }

        /// The `strings:` map of kplayerprefs.yaml (KPlayerPrefs, written by the game's YAML
        /// serializer): its indented `key: value` lines, values unquoted. A file without the
        /// map's three sections is damaged (FormatException).
        public static Dictionary<string, string> PrefsStrings(string text)
        {
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            if (!lines.Any(l => Regex.IsMatch(l, @"^strings:\s*(\{\s*\})?\s*$")) || !lines.Any(l => l.StartsWith("ints:", StringComparison.Ordinal)))
                throw new FormatException("not the game's settings (no strings: and ints: sections)");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            bool inStrings = false;
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                if (!char.IsWhiteSpace(line[0])) { inStrings = Regex.IsMatch(line, @"^strings:\s*$"); continue; }
                if (!inStrings) continue;
                var m = Regex.Match(line, @"^\s+([^:\s][^:]*?):\s*(.*?)\s*$");
                if (m.Success) map[m.Groups[1].Value] = Unquote(m.Groups[2].Value);
            }
            return map;
        }

        static string Unquote(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1].Replace("\\\"", "\"");
            return value == "~" || value == "null" ? "" : value;
        }
    }

    /// The .po subset the game's reader (Localization.LoadStringsFile) reads, as the Moono Tweaks
    /// screen's translations are written: `msgctxt` (the key), `msgid` and `msgstr`, each quoted and
    /// continued on lines that start with a quote, `\"` and `\n` the only escapes, `#` comments,
    /// blank lines between entries. key → msgstr, the empty ones left out.
    internal static class PoStrings
    {
        public static Dictionary<string, string> Read(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string? context = null, str = null, last = null;
            void End()
            {
                if (context != null && !string.IsNullOrEmpty(str)) map[context] = str;
                context = str = last = null;
            }
            foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) { End(); continue; }
                if (line[0] == '#') continue;
                if (line[0] == '"')
                {
                    if (last == "msgctxt") context += Unquote(line);
                    else if (last == "msgstr") str += Unquote(line);
                    continue;
                }
                int space = line.IndexOf(' ');
                if (space < 0) throw new FormatException($"not a .po line: {line}");
                last = line[..space];
                var value = Unquote(line[(space + 1)..]);
                if (last == "msgctxt") { if (context != null || str != null) End(); last = "msgctxt"; context = value; }
                else if (last == "msgstr") str = value;
            }
            End();
            return map;
        }

        static string Unquote(string quoted)
        {
            if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"') throw new FormatException($"not a quoted string: {quoted}");
            return quoted[1..^1].Replace("\\n", "\n").Replace("\\\"", "\"");
        }
    }
}
