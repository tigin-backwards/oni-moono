using System;
using System.Collections;
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

        /// Every report's entries, the days before today and then today, each entry with its
        /// context entries after it: day, type, context, note storage id, and its notes in the
        /// order the game reads them (value bits and text): "reports=R entries=E notes=N fnv=HEX".
        /// `Past`: the same without today's report, which the game writes to as it runs.
        public static string Read => ReadReports(true);
        public static string Past => ReadReports(false);

        static string ReadReports(bool today)
        {
            var manager = ReportManager.Instance ?? throw new DriverError("no ReportManager");
            var text = new StringBuilder();
            long entries = 0, notes = 0;
            var days = new List<ReportManager.DailyReport>(manager.reports);
            if (today && manager.TodaysReport != null) days.Add(manager.TodaysReport);
            var reportEntries = AccessTools.Field(typeof(ReportManager.DailyReport), "reportEntries");
            foreach (var day in days)
                foreach (var entry in Items(reportEntries.GetValue(day)))
                    Walk(day.day, (ReportManager.ReportEntry)entry, 0, text, ref entries, ref notes);
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            return "reports=" + days.Count + " entries=" + entries + " notes=" + notes + " fnv=" + Hash(bytes, 0, bytes.Length).ToString("x16");
        }

        static readonly FieldInfo contextEntries = AccessTools.Field(typeof(ReportManager.ReportEntry), "contextEntries");
        static readonly FieldInfo noteStorageId = AccessTools.Field(typeof(ReportManager.ReportEntry), "noteStorageId");
        static readonly FieldInfo reportType = AccessTools.Field(typeof(ReportManager.ReportEntry), "reportType");
        static readonly FieldInfo context = AccessTools.Field(typeof(ReportManager.ReportEntry), "context");

        static void Walk(int day, ReportManager.ReportEntry e, int depth, StringBuilder text, ref long entries, ref long notes)
        {
            entries++;
            text.Append(day).Append(' ').Append(depth).Append(' ').Append(reportType.GetValue(e)).Append(' ')
                .Append(context.GetValue(e)).Append(' ').Append(noteStorageId.GetValue(e)).Append(':');
            foreach (var note in NotesOf(e))
            {
                notes++;
                text.Append(' ').Append(BitConverter.ToInt32(BitConverter.GetBytes(note.value), 0).ToString("x8")).Append('=').Append(note.note);
            }
            text.Append('\n');
            foreach (var child in Items(contextEntries.GetValue(e))) Walk(day, (ReportManager.ReportEntry)child, depth + 1, text, ref entries, ref notes);
        }

        /// A list's items: a List, or Klei's ArrayRef (contextEntries), which is no IEnumerable.
        static IEnumerable<object> Items(object list)
        {
            if (list is IEnumerable items)
            {
                foreach (var item in items) yield return item;
                yield break;
            }
            var type = list.GetType();
            int count = (int)type.GetProperty("Count").GetValue(list);
            var indexer = type.GetProperty("Item");
            for (int i = 0; i < count; i++) yield return indexer.GetValue(list, new object[] { i });
        }

        static readonly MethodInfo iterateNotes = typeof(ReportManager.ReportEntry).GetMethod("IterateNotes", Any, null, new[] { typeof(Action<ReportManager.ReportEntry.Note>) }, null);
        static readonly MethodInfo collectNotes = typeof(ReportManager.ReportEntry).GetMethod("CollectNotes", Any, null, new[] { typeof(List<ReportManager.ReportEntry.Note>) }, null);

        /// An entry's notes as the reports screen reads them.
        static List<ReportManager.ReportEntry.Note> NotesOf(ReportManager.ReportEntry e)
        {
            var list = new List<ReportManager.ReportEntry.Note>();
            if (collectNotes != null) collectNotes.Invoke(e, new object[] { list });
            else iterateNotes.Invoke(e, new object[] { new Action<ReportManager.ReportEntry.Note>(list.Add) });
            return list;
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
