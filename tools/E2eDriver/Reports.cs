using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// The colony's daily reports in one value each, for comparing two players (README.md,
    /// "Report reads"): `type:E2eDriver.Reports::Read`. Through reflection only, the same in U59
    /// (ReportEntry.IterateNotes) and U60 (ReportEntry.CollectNotes).
    public static class Reports
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        static object NoteStorage() =>
            AccessTools.Field(typeof(ReportManager), "noteStorage").GetValue(ReportManager.Instance ?? throw new DriverError("no ReportManager"));

        /// The note storage's bytes as a save writes them (NoteStorage.Serialize, which
        /// ReportManager's custom serializer calls): "bytes=N fnv=HEX".
        public static string Notes
        {
            get
            {
                var storage = NoteStorage();
                var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    storage.GetType().GetMethod("Serialize", Any, null, new[] { typeof(BinaryWriter) }, null).Invoke(storage, new object[] { w });
                return Digest(ms.ToArray(), 0, (int)ms.Length);
            }
        }

        /// ReportManager's whole component as a save writes it (Serializer.SerializeTypeless: every
        /// day's report and today's through the serializer, then the custom serializer's notes):
        /// "bytes=N fnv=HEX". The templates it makes on the way are dropped by the next save.
        public static string Object
        {
            get
            {
                var manager = ReportManager.Instance ?? throw new DriverError("no ReportManager");
                var ms = new MemoryStream();
                // Not a save: `reports.watch`'s count and bytes stay as they were.
                var (saved, saves) = (Saved, Saves);
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) KSerialization.Serializer.SerializeTypeless(manager, w);
                (Saved, Saves) = (saved, saves);
                return Digest(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        /// Every report's entries, the days before today and then today, each entry with its
        /// context entries after it: day, depth, type, context, note storage id, and its notes in
        /// the order the game reads them (value bits and text), hashed as they are read:
        /// "reports=R entries=E notes=N fnv=HEX". `Past`: the same without today's report, which
        /// the game writes to as it runs. Typed field reads and a delegate for the notes: the
        /// original's Mono takes about 100 s over 4 M entries through reflection.
        public static string Read => ReadReports(true);
        public static string Past => ReadReports(false);

        static string ReadReports(bool today)
        {
            var manager = ReportManager.Instance ?? throw new DriverError("no ReportManager");
            var days = new List<ReportManager.DailyReport>(manager.reports);
            if (today && manager.TodaysReport != null) days.Add(manager.TodaysReport);
            var walk = new Walker();
            foreach (var day in days)
                foreach (var entry in day.reportEntries) walk.Entry(day.day, entry, 0);
            return "reports=" + days.Count + " entries=" + walk.Entries + " notes=" + walk.Notes + " fnv=" + walk.Hash.ToString("x16");
        }

        sealed class Walker
        {
            public long Entries, Notes;
            public ulong Hash = 14695981039346656037UL;
            readonly List<ReportManager.ReportEntry.Note> notes = new List<ReportManager.ReportEntry.Note>();
            readonly Action<ReportManager.ReportEntry.Note> add;

            public Walker() => add = notes.Add;

            void Int(int v)
            {
                for (int b = 0; b < 32; b += 8) Hash = (Hash ^ (byte)(v >> b)) * 1099511628211UL;
            }

            void Text(string t)
            {
                Int(t == null ? -1 : t.Length);
                if (t != null)
                    foreach (char c in t) Int(c);
            }

            public void Entry(int day, ReportManager.ReportEntry e, int depth)
            {
                Entries++;
                Int(day);
                Int(depth);
                Int((int)e.reportType);
                Text(e.context);
                Int(e.noteStorageId);
                notes.Clear();
                if (collectNotes != null) collectNotes(e, notes);
                else iterateNotes(e, add);
                Int(notes.Count);
                foreach (var note in notes)
                {
                    Notes++;
                    Int(BitConverter.ToInt32(BitConverter.GetBytes(note.value), 0));
                    Text(note.note);
                }
                var children = e.contextEntries;
                for (int i = 0; i < children.Count; i++) Entry(day, children[i], depth + 1);
            }
        }

        /// ReportEntry's note reader: U59 IterateNotes(Action<Note>), U60 CollectNotes(List<Note>).
        static readonly Action<ReportManager.ReportEntry, Action<ReportManager.ReportEntry.Note>> iterateNotes =
            Open<Action<ReportManager.ReportEntry, Action<ReportManager.ReportEntry.Note>>>("IterateNotes", typeof(Action<ReportManager.ReportEntry.Note>));
        static readonly Action<ReportManager.ReportEntry, List<ReportManager.ReportEntry.Note>> collectNotes =
            Open<Action<ReportManager.ReportEntry, List<ReportManager.ReportEntry.Note>>>("CollectNotes", typeof(List<ReportManager.ReportEntry.Note>));

        static T Open<T>(string name, Type parameter) where T : Delegate
        {
            var m = typeof(ReportManager.ReportEntry).GetMethod(name, Any, null, new[] { parameter }, null);
            return m == null ? null : (T)Delegate.CreateDelegate(typeof(T), m);
        }

        /// The minus of a number that rounds to zero ("-0", "-0.0%"): ours keeps it, Mono's
        /// formatting drops it (ARCHITECTURE D2).
        static readonly Regex NegativeZero = new Regex(@"(?<![\w.,])-(?=0(?:[.,]0+)?(?![.,]?\d))");

        /// The open reports screen's rows, in their order: the text of each row's three tooltips
        /// (added, removed, net), which name the entry and list its notes; negative zeros lose
        /// their minus (the original prints none). A tooltip's lines
        /// are sorted first: the game sorts its notes by value with List.Sort, which may order
        /// equal values differently in each runtime. "rows=N fnv=HEX".
        public static string Screen
        {
            get
            {
                var screen = ReportScreen.Instance ?? throw new DriverError("no ReportScreen");
                var text = new StringBuilder();
                int rows = 0;
                foreach (var row in screen.GetComponentsInChildren<ReportScreenEntryRow>(false))
                {
                    rows++;
                    foreach (var part in new[] { row.added, row.removed, row.net })
                    {
                        var tip = part.GetComponent<ToolTip>();
                        string tipText = tip != null && tip.OnToolTip != null ? tip.OnToolTip() : "";
                        var lines = NegativeZero.Replace(tipText, "").Split('\n');
                        Array.Sort(lines, StringComparer.Ordinal);
                        text.Append('|').Append(string.Join("\n", lines));
                    }
                    text.Append('\n');
                }
                ScreenText = text.ToString();
                var bytes = Encoding.UTF8.GetBytes(ScreenText);
                return "rows=" + rows + " fnv=" + Hash(bytes, 0, bytes.Length).ToString("x16");
            }
        }

        /// The text `Screen` hashed last, for finding what differs.
        public static string ScreenText { get; private set; } = "";

        /// The bytes ReportManager wrote into the last save made since `reports.watch`
        /// (ReportManager.CustomSerialize: its marker and the note storage), "bytes=N fnv=HEX";
        /// "none" before one.
        public static string Saved { get; private set; } = "none";
        public static int Saves { get; private set; }

        static bool patched;

        public static JToken Watch(JObject a)
        {
            if (!patched)
            {
                var method = AccessTools.Method(typeof(ReportManager), "CustomSerialize", new[] { typeof(BinaryWriter) })
                    ?? throw new DriverError("reports.watch: no ReportManager.CustomSerialize(BinaryWriter)");
                Mod.Harmony.Patch(method, prefix: new HarmonyMethod(typeof(Reports), nameof(Serializing)),
                    postfix: new HarmonyMethod(typeof(Reports), nameof(Serialized)));
                patched = true;
            }
            Saved = "none";
            Saves = 0;
            return new JObject { ["watching"] = true };
        }

        static void Serializing(BinaryWriter writer, out long __state)
        {
            writer.Flush();
            __state = writer.BaseStream.CanSeek ? writer.BaseStream.Position : -1;
        }

        static void Serialized(BinaryWriter writer, long __state)
        {
            writer.Flush();
            Saves++;
            if (__state < 0 || !(writer.BaseStream is MemoryStream ms))
            {
                Saved = "unreadable " + writer.BaseStream.GetType().Name;
                return;
            }
            int end = (int)ms.Position, start = (int)__state;
            byte[] bytes;
            int offset;
            if (ms.TryGetBuffer(out var buffer))
            {
                bytes = buffer.Array;
                offset = buffer.Offset + start;
            }
            else
            {
                bytes = ms.ToArray();
                offset = start;
            }
            Saved = Digest(bytes, offset, end - start);
            Debug.Log("[E2eDriver] reports saved " + Saved);
        }

        static string Digest(byte[] bytes, int offset, int count) => "bytes=" + count + " fnv=" + Hash(bytes, offset, count).ToString("x16");

        static ulong Hash(byte[] bytes, int offset, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int i = offset; i < offset + count; i++) h = (h ^ bytes[i]) * 1099511628211UL;
            return h;
        }
    }
}
