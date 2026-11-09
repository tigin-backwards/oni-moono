#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace OniMoono.Host.Launch
{
    /// Steam's text KeyValues files (libraryfolders.vdf, appmanifest_*.acf, localconfig.vdf,
    /// remotecache.vdf): `"key" "value"` pairs and `"key" { ... }` blocks, with backslash escapes
    /// in quoted strings and `//` comments. Keys compare without case, as Steam's do. Unquoted
    /// tokens are read too; anything malformed ends the block it is in, never throws.
    internal sealed class Vdf
    {
        readonly List<(string Key, string? Value, Vdf? Block)> entries = new();

        public IReadOnlyList<(string Key, string? Value, Vdf? Block)> Entries => entries;

        /// The first value named `key` in this block, or null.
        public string? Value(string key)
        {
            foreach (var e in entries)
                if (e.Value != null && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return e.Value;
            return null;
        }

        /// The first block named `key` in this block, or null.
        public Vdf? Block(string key)
        {
            foreach (var e in entries)
                if (e.Block != null && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return e.Block;
            return null;
        }

        /// The block at a path of keys ("UserLocalConfigStore", "Software", ...), or null.
        public Vdf? Path(params string[] keys)
        {
            Vdf? at = this;
            foreach (var key in keys) at = at?.Block(key);
            return at;
        }

        public static Vdf Parse(string text)
        {
            int i = 0;
            var root = new Vdf();
            ReadBlock(text, ref i, root, top: true);
            return root;
        }

        static void ReadBlock(string text, ref int i, Vdf block, bool top)
        {
            while (true)
            {
                var key = Token(text, ref i, out bool brace);
                if (key == null) return;
                if (brace)
                {
                    if (key == "}") return;
                    continue; // a stray "{"
                }
                var value = Token(text, ref i, out brace);
                if (value == null) return;
                if (brace && value == "{")
                {
                    var child = new Vdf();
                    ReadBlock(text, ref i, child, top: false);
                    block.entries.Add((key, null, child));
                }
                else if (brace) return; // "key }": malformed
                else block.entries.Add((key, value, null));
            }
        }

        /// The next string, "{" or "}" (brace true), or null at the end.
        static string? Token(string text, ref int i, out bool brace)
        {
            brace = false;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                break;
            }
            if (i >= text.Length) return null;
            if (text[i] == '{' || text[i] == '}')
            {
                brace = true;
                return text[i++].ToString();
            }
            var s = new StringBuilder();
            if (text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        char e = text[i + 1];
                        s.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                        i += 2;
                        continue;
                    }
                    s.Append(text[i++]);
                }
                i++; // the closing quote
                return s.ToString();
            }
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"') s.Append(text[i++]);
            return s.ToString();
        }
    }
}
