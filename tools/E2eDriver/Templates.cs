using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// `templates.load` (README.md): loads every template file of the install through the game's
    /// own loader (TemplateCache.LoadTemplateYaml in U60, YamlIO.LoadFile as GetTemplate calls
    /// it in U59), and reduces each to one line, so the two players' readings of the same data
    /// compare as text (U60 QA spec, A1).
    ///
    /// A line is "PATH cells=N prefabs=N mass=BITS temp=BITS ids=ID,ID,... hash=HEX". PATH is
    /// relative to StreamingAssets with '/', and the lines are in ordinal order of PATH. mass and
    /// temp are the sums, as doubles in file order, of the cells' mass and temperature, written
    /// as the double's bits in hex so that no float formatting enters. ids are the prefab ids of
    /// buildings, pickupables, elementalOres, otherEntities and backwallEntities in that order.
    /// hash is FNV-1a 64 over a canonical rendering of the whole object: public instance fields
    /// and readable properties by ordinal name, floats by their bits, enums by their number,
    /// lists in order, dictionaries in enumeration order. A file the loader rejects reads
    /// "PATH null".
    public static class Templates
    {
        static readonly List<string> lines = new List<string>();

        /// Every line, '\n'-joined.
        public static string Lines => string.Join("\n", lines.ToArray());
        /// How many files were read.
        public static int Count => lines.Count;
        /// How many of them the loader returned null for.
        public static int Failed => lines.Count(l => l.EndsWith(" null", StringComparison.Ordinal));
        /// FNV-1a 64 of Lines, in hex: one value to compare.
        public static string Digest => Hex(Fnv(Lines));

        public static JToken Load(JObject a)
        {
            lines.Clear();
            string root = Application.streamingAssetsPath.Replace('\\', '/');
            var files = new List<string>();
            void Add(string folder)
            {
                if (Directory.Exists(folder))
                    files.AddRange(Directory.GetFiles(folder, "*.yaml", SearchOption.AllDirectories));
            }
            Add(Path.Combine(root, "templates"));
            string dlc = Path.Combine(root, "dlc");
            if (Directory.Exists(dlc))
                foreach (var d in Directory.GetDirectories(dlc)) Add(Path.Combine(d, "templates"));
            var rel = files.Select(f => f.Replace('\\', '/'))
                .Select(f => f.StartsWith(root + "/", StringComparison.Ordinal) ? f.Substring(root.Length + 1) : f)
                .Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            foreach (var path in rel)
            {
                string line;
                try { line = path + " " + Describe(Read(root + "/" + path)); }
                catch (Exception e) { line = path + " threw " + e.GetType().Name; }
                lines.Add(line);
                Debug.Log("[E2eDriver] template " + line);
            }
            return new JObject { ["count"] = Count, ["failed"] = Failed, ["digest"] = Digest };
        }

        // U60's loader, TemplateCache.LoadTemplateYaml; U59 has none, and its GetTemplate reads
        // a file through YamlIO.LoadFile.
        static readonly MethodInfo loadTemplateYaml = typeof(TemplateCache).GetMethod("LoadTemplateYaml",
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

        static TemplateContainer Read(string path)
        {
            if (loadTemplateYaml == null) return Klei.YamlIO.LoadFile<TemplateContainer>(path);
            try { return (TemplateContainer)loadTemplateYaml.Invoke(null, new object[] { path }); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        static string Describe(TemplateContainer t)
        {
            if (t == null) return "null";
            double mass = 0, temp = 0;
            int cells = 0;
            if (t.cells != null)
                foreach (var c in t.cells)
                {
                    cells++;
                    mass += c.mass;
                    temp += c.temperature;
                }
            var ids = new List<string>();
            foreach (var list in new[] { t.buildings, t.pickupables, t.elementalOres, t.otherEntities, t.backwallEntities })
                if (list != null)
                    foreach (var p in list) ids.Add(p?.id ?? "(null)");
            var sb = new StringBuilder();
            Render(t, sb, 0);
            return "cells=" + cells + " prefabs=" + ids.Count
                + " mass=" + BitConverter.DoubleToInt64Bits(mass).ToString("x16")
                + " temp=" + BitConverter.DoubleToInt64Bits(temp).ToString("x16")
                + " ids=" + string.Join(",", ids.ToArray())
                + " hash=" + Hex(Fnv(sb.ToString()));
        }

        static readonly Dictionary<Type, MemberInfo[]> members = new Dictionary<Type, MemberInfo[]>();

        static MemberInfo[] MembersOf(Type type)
        {
            if (members.TryGetValue(type, out var m)) return m;
            var flags = BindingFlags.Public | BindingFlags.Instance;
            m = type.GetFields(flags).Cast<MemberInfo>()
                .Concat(type.GetProperties(flags).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            members[type] = m;
            return m;
        }

        static void Render(object value, StringBuilder sb, int depth)
        {
            if (depth > 16) { sb.Append("<deep>"); return; }
            switch (value)
            {
                case null: sb.Append("~"); return;
                case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); return;
                case float f: sb.Append("f").Append(BitConverter.SingleToInt32Bits(f).ToString("x8")); return;
                case double d: sb.Append("d").Append(BitConverter.DoubleToInt64Bits(d).ToString("x16")); return;
                case bool b: sb.Append(b ? "T" : "F"); return;
                case Enum e: sb.Append("e").Append(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
                case IConvertible c when value.GetType().IsPrimitive:
                    sb.Append(c.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                    sb.Append('{');
                    foreach (DictionaryEntry kv in dict)
                    {
                        Render(kv.Key, sb, depth + 1);
                        sb.Append(':');
                        Render(kv.Value, sb, depth + 1);
                        sb.Append(',');
                    }
                    sb.Append('}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    foreach (var item in list)
                    {
                        Render(item, sb, depth + 1);
                        sb.Append(',');
                    }
                    sb.Append(']');
                    return;
            }
            var type = value.GetType();
            sb.Append(type.Name).Append('(');
            foreach (var m in MembersOf(type))
            {
                object v;
                try { v = m is FieldInfo fi ? fi.GetValue(value) : ((PropertyInfo)m).GetValue(value); }
                catch (Exception e) { v = "!" + (e.InnerException ?? e).GetType().Name; }
                sb.Append(m.Name).Append('=');
                Render(v, sb, depth + 1);
                sb.Append(';');
            }
            sb.Append(')');
        }

        static ulong Fnv(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }

        static string Hex(ulong v) => v.ToString("x16");
    }
}
