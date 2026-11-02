using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OniMoono.Engine
{
    /// What Player.log and the console get in place of personal paths, so a log attached to an
    /// issue does not name the player (`--log-paths hidden`, the default). Each rule is anchored
    /// to a value known at start-up, never guessed from free text:
    /// - the profile folder: `%USERPROFILE%` on Windows (either slash direction, any case; also
    ///   its short 8.3 form, as the temp folder names it), `~` elsewhere;
    /// - the Documents folder when it is not under the profile: `%DOCUMENTS%`;
    /// - the user name as a path segment (a profile on another drive, /mnt/c/Users/NAME): `<user>`.
    ///   The bare word is left alone.
    /// A path matches with any run of slashes between its folders (Windows: either kind, as in
    /// "C:/Users\Name" or an escaped "C:\\Users\\Name") and not inside a longer name.
    ///
    /// And the accounts, by their format or by the lines that name them:
    /// - a SteamID64 wherever it stands as a whole number (cloud_save_files\ID\…, a mod's
    ///   lobby): `<steam id>`. Only an individual account's (76561197960265729 to
    ///   76561202255233023: universe 1, type 1, instance 1 and a 32-bit account number), so
    ///   other numbers (mod and Workshop IDs, at most 10 digits; hashes; a Steam group's
    ///   103582791…) are not. Anyone's: a multiplayer mod logs other players' too.
    /// - the game's login line (Global), "Logged into Steam with ID:ID, NAME:NAME": the ID
    ///   `<steam id>`, the persona name to the line's end `<name>`. Only there: a name can be
    ///   any word, so it is not looked for elsewhere.
    /// - KleiAccount's lines: "[Account] Got login for user ID" (`<klei id>`) and the auth
    ///   ticket of "[Account] Error logging in: … for ticket=HEX" (`<ticket>`).
    /// The rules hold no state, so the log's threads share the scrubber freely.
    ///
    /// Each path rule needs a slash and one of a few words (the folders' last names, the user
    /// name), and each account rule one of three ("76561", "Logged into ", "[Account] "), so most
    /// lines cost a few vectorized searches; only a line with them is scanned, and for paths
    /// only at slashes, after them and at drive letters. Hand-written rather than a Regex: one
    /// built from the same values cost ~10 ms of the player's start-up (the first use of the
    /// Regex and SearchValues code), this ~2 ms.
    internal sealed class LogScrubber
    {
        public const string ProfileText = "%USERPROFILE%", HomeText = "~", DocumentsText = "%DOCUMENTS%",
            SteamIdText = "<steam id>", UserText = "<user>", NameText = "<name>", KleiIdText = "<klei id>", TicketText = "<ticket>";
        /// An individual account's SteamID64 in the public universe: this plus its account number.
        const ulong SteamIdBase = 76561197960265728;
        const string SteamIdStart = "76561";
        const int SteamIdLength = 17;
        /// Global's line: "Logged into " + platform + " with ID:" + ID + ", NAME:" + name.
        const string LoginStart = "Logged into ", LoginId = " with ID:", LoginName = ", NAME:";
        /// KleiAccount's lines.
        const string AccountStart = "[Account] ", AccountLogin = "[Account] Got login for user ",
            AccountError = "[Account] Error logging in: ", AccountTicket = " for ticket=";

        /// A folder's path by segments; `Rooted` when it starts with a slash.
        sealed record Rule(string[] Segments, bool Rooted, string Replacement);

        readonly bool windows;
        readonly StringComparison comparison;
        readonly Rule[] paths;
        readonly string userName;
        /// A line without any of these can match no rule.
        readonly string[] anchors;
        /// The first characters of the paths that start with no slash (a drive: C:, c:).
        readonly string starts;

        LogScrubber(bool windows, Rule[] paths, string userName, string[] anchors)
        {
            var first = new StringBuilder();
            foreach (var rule in paths)
                if (!rule.Rooted)
                {
                    char c = rule.Segments[0][0];
                    first.Append(c);
                    if (windows) first.Append(char.ToLowerInvariant(c)).Append(char.ToUpperInvariant(c));
                }
            starts = first.ToString();
            this.windows = windows;
            comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            this.paths = paths;
            this.userName = userName;
            this.anchors = anchors;
        }

        /// This machine's values: the profile and Documents folders .NET reports, the user name.
        public static LogScrubber ForThisMachine()
        {
            bool windows = OperatingSystem.IsWindows();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var aliases = new List<string>();
            if (windows)
            {
                // The temp folder can name the profile by its short form (C:\Users\ADMINI~1).
                const string tempTail = @"\AppData\Local\Temp\";
                string temp = Path.GetTempPath();
                if (temp.EndsWith(tempTail, StringComparison.OrdinalIgnoreCase)) aliases.Add(temp[..^tempTail.Length]);
            }
            return Create(windows, profile, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.UserName, aliases);
        }

        /// The rules for these values; `windows` makes them case-insensitive, with either slash.
        public static LogScrubber Create(bool windows, string profile, string documents, string userName, IEnumerable<string> profileAliases = null)
        {
            var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var rules = new List<Rule>();
            var anchors = new List<string>();
            void Add(string path, string replacement)
            {
                var segments = Segments(path, windows);
                if (segments.Length == 0) return;
                foreach (var rule in rules)
                    if (SameSegments(rule.Segments, segments, comparison)) return;
                rules.Add(new Rule(segments, path[0] is '/' or '\\', replacement));
                anchors.Add(segments[^1]);
            }
            Add(profile, windows ? ProfileText : HomeText);
            foreach (var alias in profileAliases ?? []) Add(alias, windows ? ProfileText : HomeText);
            // Documents on its own only when no profile rule covers it; a drive root is no anchor.
            var documentsSegments = Segments(documents, windows);
            bool covered = false;
            foreach (var rule in rules) covered |= IsUnder(documentsSegments, rule.Segments, comparison);
            if (documentsSegments.Length >= 2 && !covered) Add(documents, DocumentsText);
            // Longest first: at one position, the more specific path wins.
            rules.Sort((a, b) => b.Segments.Length.CompareTo(a.Segments.Length));
            // Short names (root, a, me) would hit unrelated segments.
            if (string.IsNullOrEmpty(userName) || userName.Length < 3 || userName.IndexOfAny(['/', '\\']) >= 0) userName = null;
            else anchors.Add(userName);
            return new LogScrubber(windows, rules.ToArray(), userName, anchors.ToArray());
        }

        static string[] Segments(string path, bool windows)
            => string.IsNullOrEmpty(path) ? [] : path.Split(windows ? ['\\', '/'] : ['/'], StringSplitOptions.RemoveEmptyEntries);

        static bool SameSegments(string[] a, string[] b, StringComparison comparison)
            => a.Length == b.Length && IsUnder(a, b, comparison);

        static bool IsUnder(string[] path, string[] folder, StringComparison comparison)
        {
            if (path.Length < folder.Length) return false;
            for (int i = 0; i < folder.Length; i++)
                if (!string.Equals(path[i], folder[i], comparison)) return false;
            return true;
        }

        bool IsSeparator(char c) => c == '/' || windows && c == '\\';

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// A character a file or folder name continues with.
        static bool IsNameChar(char c) => IsWordChar(c) || c is '.' or '-' or '~' or '$';

        /// A name ends at `at`: the end, a slash, or a character no name continues with.
        bool EndsName(string text, int at) => at == text.Length || IsSeparator(text[at]) || !IsNameChar(text[at]);

        bool Matches(string text, int at, string word)
        {
            if (at + word.Length > text.Length) return false;
            // The first character before the whole word: most candidates fail there.
            char a = text[at], b = word[0];
            if (a != b && !(windows && char.ToUpperInvariant(a) == char.ToUpperInvariant(b))) return false;
            return text.AsSpan(at, word.Length).Equals(word, comparison);
        }

        int SkipSeparators(string text, int at)
        {
            while (at < text.Length && IsSeparator(text[at])) at++;
            return at;
        }

        /// Where the rule's path ends if it starts at `at`, else -1.
        int MatchPath(string text, int at, Rule rule)
        {
            // Not the end of a longer name: "x/home/me", "CD:\" (Windows' drive or share names
            // stand after anything but a word's character).
            if (at > 0 && (windows ? IsWordChar(text[at - 1]) : IsNameChar(text[at - 1]))) return -1;
            int i = at;
            if (rule.Rooted)
            {
                if (!IsSeparator(text[i])) return -1;
                i = SkipSeparators(text, i);
            }
            for (int s = 0; s < rule.Segments.Length; s++)
            {
                if (s > 0)
                {
                    int next = SkipSeparators(text, i);
                    if (next == i) return -1;
                    i = next;
                }
                if (!Matches(text, i, rule.Segments[s])) return -1;
                i += rule.Segments[s].Length;
            }
            return EndsName(text, i) ? i : -1;
        }

        /// Where the user name as a path segment ends if it starts at `at`, else -1.
        int MatchSegment(string text, int at, out string replacement)
        {
            replacement = null;
            if (at == 0 || !IsSeparator(text[at - 1])) return -1;
            if (userName != null && Matches(text, at, userName) && EndsName(text, at + userName.Length))
            {
                replacement = UserText;
                return at + userName.Length;
            }
            return -1;
        }

        /// The text with the rules applied; the same instance when nothing matched.
        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var span = text.AsSpan();
            if (span.IndexOf(SteamIdStart, StringComparison.Ordinal) >= 0) text = HideSteamIds(text);
            if (span.IndexOf(LoginStart, StringComparison.Ordinal) >= 0) text = HideLogin(text);
            if (span.IndexOf(AccountStart, StringComparison.Ordinal) >= 0) text = HideKleiAccount(text);
            return ScrubPaths(text);
        }

        /// Puts `replacement` for [start, end) of `text` into what `result` builds of it.
        static void Replace(ref StringBuilder result, ref int copied, string text, int start, int end, string replacement)
        {
            result ??= new StringBuilder(text.Length);
            result.Append(text, copied, start - copied).Append(replacement);
            copied = end;
        }

        static string Finish(StringBuilder result, string text, int copied)
            => result == null ? text : result.Append(text, copied, text.Length - copied).ToString();

        /// Where the line that `at` is in ends (its line break, or the text's end).
        static int LineEnd(string text, int at)
        {
            int end = text.AsSpan(at).IndexOfAny('\r', '\n');
            return end < 0 ? text.Length : at + end;
        }

        /// Each whole number (no digit on either side) that is an individual account's SteamID64.
        static string HideSteamIds(string text)
        {
            StringBuilder result = null;
            int copied = 0;
            for (int at = text.IndexOf(SteamIdStart, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(SteamIdStart, at, StringComparison.Ordinal))
            {
                int end = at + SteamIdStart.Length;
                while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
                if (end - at == SteamIdLength && (at == 0 || !char.IsAsciiDigit(text[at - 1]))
                    && ulong.TryParse(text.AsSpan(at, SteamIdLength), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id > SteamIdBase && id - SteamIdBase <= uint.MaxValue)
                    Replace(ref result, ref copied, text, at, end, SteamIdText);
                at = end;
            }
            return Finish(result, text, copied);
        }

        /// The ID and the persona name of Global's login line.
        static string HideLogin(string text)
        {
            StringBuilder result = null;
            int copied = 0;
            for (int at = text.IndexOf(LoginStart, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(LoginStart, at, StringComparison.Ordinal))
            {
                int i = at += LoginStart.Length;
                while (i < text.Length && char.IsAsciiLetterOrDigit(text[i])) i++;
                if (i == at || string.CompareOrdinal(text, i, LoginId, 0, LoginId.Length) != 0) continue;
                int id = i + LoginId.Length, end = LineEnd(text, id);
                int name = text.IndexOf(LoginName, id, end - id, StringComparison.Ordinal);
                if (name < 0) continue;
                if (name > id) Replace(ref result, ref copied, text, id, name, SteamIdText);
                name += LoginName.Length;
                if (end > name) Replace(ref result, ref copied, text, name, end, NameText);
                at = end;
            }
            return Finish(result, text, copied);
        }

        /// KleiAccount's user ID, and the auth ticket it logs when the login fails.
        static string HideKleiAccount(string text)
        {
            StringBuilder result = null;
            int copied = 0;
            for (int at = text.IndexOf(AccountStart, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(AccountStart, at, StringComparison.Ordinal))
            {
                int end = LineEnd(text, at);
                if (string.CompareOrdinal(text, at, AccountLogin, 0, AccountLogin.Length) == 0)
                {
                    int id = at + AccountLogin.Length;
                    if (end > id) Replace(ref result, ref copied, text, id, end, KleiIdText);
                }
                else if (string.CompareOrdinal(text, at, AccountError, 0, AccountError.Length) == 0)
                {
                    // The server's reply stands before it and may hold anything: the last one.
                    int ticket = text.LastIndexOf(AccountTicket, end - 1, end - at, StringComparison.Ordinal);
                    if (ticket >= 0)
                    {
                        int hex = ticket + AccountTicket.Length, last = hex;
                        while (last < end && char.IsAsciiHexDigit(text[last])) last++;
                        if (last > hex) Replace(ref result, ref copied, text, hex, last, TicketText);
                    }
                }
                at = Math.Max(end, at + 1);
            }
            return Finish(result, text, copied);
        }

        string ScrubPaths(string text)
        {
            var span = text.AsSpan();
            if (span.IndexOfAny('/', '\\') < 0) return text;
            bool anchored = false;
            foreach (var anchor in anchors)
                if (span.IndexOf(anchor, comparison) >= 0) { anchored = true; break; }
            if (!anchored) return text;

            StringBuilder result = null;
            int copied = 0;
            for (int at = 0; at < text.Length;)
            {
                // Each rule starts at a slash, after one, or at a drive's letter.
                char c = text[at];
                if (!IsSeparator(c) && starts.IndexOf(c) < 0 && (at == 0 || !IsSeparator(text[at - 1])))
                {
                    at++;
                    continue;
                }
                int end = -1;
                string replacement = null;
                foreach (var rule in paths)
                {
                    end = MatchPath(text, at, rule);
                    if (end >= 0) { replacement = rule.Replacement; break; }
                }
                if (end < 0) end = MatchSegment(text, at, out replacement);
                if (end < 0)
                {
                    at++;
                    continue;
                }
                result ??= new StringBuilder(text.Length);
                result.Append(text, copied, at - copied).Append(replacement);
                at = copied = end;
            }
            if (result == null) return text;
            return result.Append(text, copied, text.Length - copied).ToString();
        }
    }
}
