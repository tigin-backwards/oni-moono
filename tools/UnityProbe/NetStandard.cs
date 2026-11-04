// A mod built for .NET Standard 2.1 against 761654's Managed (u60-761654.md, W1): the game and
// its libraries reference netstandard 2.1.0.0 since 761654, and so does this assembly. At
// OnLoad it records values from the BCL areas a mod built that way reaches through netstandard
// (Linq, HashSet, Span and MemoryExtensions, System.Xml.Linq, System.Numerics, System.IO
// .Compression, culture formatting, Type.GetType), each as a static field a spec reads with
// expect_state (type:SemanticsProbe.NetStandard::NAME), and as a line "NS21 NAME=VALUE". A Harmony
// postfix on Game.OnSpawn counts its calls (GameSpawns).
// Run alone with PROBE_SCENARIO=ns21, built against 761654's Managed (-p:OniManaged=...).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using UnityEngine;

namespace SemanticsProbe
{
    public static class NetStandard
    {
        public static string Linq, HashSetValue, SpanValue, XmlValue, VectorValue, VectorLength,
            GzipRoundTrip, GzipLength, DeflateRoundTrip, FormatDe, FormatFr, FormatRu, FormatInvariant,
            Int32Assembly, NetstandardRef, Done;
        public static int GameSpawns;

        static string Codes(string s) => string.Join(" ", s.Select(c => ((int)c).ToString("X4")));

        static string Try(Func<string> f)
        {
            try { return f(); }
            catch (Exception e) { return "EXCEPTION " + e.GetType().FullName; }
        }

        public static void Run(HarmonyLib.Harmony harmony)
        {
            Linq = Try(() => Enumerable.Range(1, 20).Where(i => i % 3 == 0).Select(i => i * i).Sum() + ";"
                + string.Join(",", new[] { "pear", "Apple", "fig", "apple" }.OrderBy(s => s, StringComparer.Ordinal))
                + ";" + string.Join(",", Enumerable.Reverse(new[] { 5, 3, 9, 1 }).Skip(1).Take(2)));
            HashSetValue = Try(() =>
            {
                var set = new HashSet<int> { 3, 1, 4, 1, 5, 9, 2, 6 };
                set.ExceptWith(new[] { 4, 9 });
                return set.Count + ";" + set.Contains(5) + ";" + set.Contains(4) + ";" + string.Join(",", set.OrderBy(i => i));
            });
            SpanValue = Try(() =>
            {
                ReadOnlySpan<char> span = "hello, wide world".AsSpan();
                int comma = span.IndexOf(',');
                var slice = span.Slice(comma + 2, 4);
                Span<int> ints = stackalloc int[4] { 4, 2, 3, 1 };
                ints.Reverse();
                return comma + ";" + slice.ToString() + ";" + span.LastIndexOf('o') + ";" + ints[0] + ints[1] + ints[2] + ints[3]
                    + ";" + MemoryExtensions.SequenceEqual("abc".AsSpan(), "abc".AsSpan());
            });
            XmlValue = Try(() =>
            {
                var e = XElement.Parse("<a x='1'><b y=\"2\">t</b><!-- c --><c/></a>");
                return e.ToString(SaveOptions.DisableFormatting) + ";" + (string)e.Attribute("x") + ";" + e.Elements().Count()
                    + ";" + e.Element("b").Value;
            });
            VectorValue = Try(() => System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(1, 2, 3)).ToString());
            VectorLength = Try(() => System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(1, 2, 3)).Length().ToString("R", CultureInfo.InvariantCulture));
            byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Oxygen Not Included ", 200)));
            GzipRoundTrip = Try(() =>
            {
                var packed = new MemoryStream();
                using (var gz = new GZipStream(packed, CompressionMode.Compress, true)) gz.Write(data, 0, data.Length);
                GzipLength = packed.Length.ToString();
                packed.Position = 0;
                var unpacked = new MemoryStream();
                using (var gz = new GZipStream(packed, CompressionMode.Decompress)) gz.CopyTo(unpacked);
                return unpacked.ToArray().SequenceEqual(data).ToString();
            });
            DeflateRoundTrip = Try(() =>
            {
                var packed = new MemoryStream();
                using (var d = new DeflateStream(packed, System.IO.Compression.CompressionLevel.Optimal, true)) d.Write(data, 0, data.Length);
                packed.Position = 0;
                var unpacked = new MemoryStream();
                using (var d = new DeflateStream(packed, CompressionMode.Decompress)) d.CopyTo(unpacked);
                return unpacked.ToArray().SequenceEqual(data).ToString();
            });
            FormatDe = Try(() => Codes(string.Format(new CultureInfo("de-DE"), "{0:N2}|{1:D}", 1234567.891, new System.DateTime(2026, 11, 3))));
            FormatFr = Try(() => Codes(string.Format(new CultureInfo("fr-FR"), "{0:N2}", 1234567.891)));
            FormatRu = Try(() => Codes(string.Format(new CultureInfo("ru-RU"), "{0:N2}|{1:C}", 1234567.891, 12.5m)));
            FormatInvariant = Try(() => string.Format(CultureInfo.InvariantCulture, "{0:N2}|{1:E3}|{2}|{3:P1}", 1234567.891, 0.000123, 1.0f / 3, 0.256));
            Int32Assembly = Try(() => Type.GetType("System.Int32").Assembly.GetName().Name);
            NetstandardRef = Try(() => string.Join(",", typeof(NetStandard).Assembly.GetReferencedAssemblies()
                .Where(a => a.Name == "netstandard").Select(a => a.Name + " " + a.Version)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Game), "OnSpawn"),
                postfix: new HarmonyLib.HarmonyMethod(typeof(NetStandard), nameof(GameSpawnPostfix)));
            foreach (var field in typeof(NetStandard).GetFields())
                if (field.FieldType == typeof(string) && field.Name != nameof(Done))
                    Debug.Log("NS21 " + field.Name + "=" + field.GetValue(null));
            Done = "yes";
        }

        static void GameSpawnPostfix() => GameSpawns++;
    }
}
