using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using KSerialization;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using NoteEntries = ReportManager.NoteStorage.NoteEntries;
    using NoteEntryKey = ReportManager.NoteStorage.NoteEntries.NoteEntryKey;

    /// Game tweak `compact-report-history` (exact): the notes of past days' reports in flat
    /// arrays instead of a dictionary per report entry.
    ///
    /// The game keeps every day's report for the colony's life, and each report entry's notes
    /// (what added to or took from its value: "Stress: Ate in the Mess Hall +5") in
    /// ReportManager.NoteStorage.NoteEntries.entries, one Dictionary<NoteEntryKey, float> per
    /// entry: on a late-game colony (cycle 17,000) 3.9 M dictionaries, 1.15 GiB. Only today's
    /// entries are written (ReportManager.ReportValue* go to TodaysReport); a past day's notes
    /// are read by the reports screen (NoteEntries.CollectNotes; U59 IterateNotes) and written
    /// out by every save.
    ///
    /// Here the notes of every entry but today's live in a compact store (ReportNoteStore, in
    /// CompactReportsCore.cs): per entry its id, where its notes start and how many there are;
    /// per note its hash, sign and value, in the dictionary's order. `entries` keeps today's
    /// entries (Fast Track reads them there) and any entry written since:
    /// - loading (NoteEntries.Deserialize, version 6) reads the notes straight into the store;
    ///   older saves are read by the game and then moved in. ReportManager.OnDeserialized moves
    ///   today's report's entries back into `entries`, as dictionaries built in the store's
    ///   order;
    /// - NoteEntries.Add to an entry in the store first moves it back the same way (only a mod
    ///   would write to a past day), so the write lands as in the game;
    /// - the night (ReportManager.OnNightTime) moves the day that ended into the store; a colony
    ///   started in this session gets its store at its first night;
    /// - reading an entry's notes reads the store in its order, the dictionary's;
    /// - NoteEntries.Serialize writes every entry in the order the game's dictionary holds them
    ///   (the order they were first added: it never removes one), each from the store or from
    ///   `entries`: the same bytes. The store's notes are packed into blocks of the bytes the
    ///   game's three writes a note make, instead of three calls of the writer for every note.
    /// Code that reads `entries` directly sees only today's entries and those written since the
    /// load; no Workshop mod scanned reads or writes a past day's there.
    ///
    /// ONI_TWEAK_VERIFY=1 keeps the game's own NoteEntries alongside, written as the game
    /// writes, and compares every read and save with it.
    [GameMember(GameUse.Prefix | GameUse.Postfix | GameUse.Replaces, "System.Void ReportManager/NoteStorage/NoteEntries::Deserialize(System.IO.BinaryReader,System.Int32)", "93e6eaf5ebc7a283 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void ReportManager/NoteStorage/NoteEntries::Serialize(System.IO.BinaryWriter)", "eaaf26378714f7d0 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Prefix | GameUse.Mirrors, "System.Void ReportManager/NoteStorage/NoteEntries::Add(System.Int32,System.Single,System.Int32)", "5f2da166a92be7f1 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void ReportManager/NoteStorage/NoteEntries::CollectNotes(ReportManager/NoteStorage/StringTable,System.Int32,System.Collections.Generic.List`1<ReportManager/ReportEntry/Note>)", "595ebe03ba762105 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void KSerialization.IOHelper::WriteSingleFast(System.IO.BinaryWriter,System.Single)", "06b887c62310083b linux-744825 win-744825", "801f48d5743265aa linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Postfix, "System.Void ReportManager::OnDeserialized()")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Void ReportManager::OnNightTime(System.Object)")]
    [GameMember(GameUse.Postfix, "System.Void ReportManager::OnCleanUp()")]
    public static class CompactReports
    {
        public const string Name = "compact-report-history";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// The game's note key for the store.
        public struct Keys : INoteKeys<NoteEntryKey>
        {
            public NoteEntryKey Key(int hash, bool positive) => new NoteEntryKey { noteHash = hash, isPositive = positive };
            public int Hash(NoteEntryKey key) => key.noteHash;
            public bool Positive(NoteEntryKey key) => key.isPositive;
            public IEqualityComparer<NoteEntryKey> Comparer => NoteEntries.sKeyComparer;
            public void WriteValue(BinaryWriter writer, float value) => writer.WriteSingleFast(value);
        }

        /// The store of the NoteEntries in use (the game has one, ReportManager's), until the
        /// next load or the colony's end.
        static ReportNoteStore<NoteEntryKey, Keys> store;
        static NoteEntries storeOwner;

        public static void Install(Harmony harmony)
        {
            var t = typeof(NoteEntries);
            harmony.Patch(AccessTools.Method(t, "Deserialize"), prefix: new HarmonyMethod(typeof(CompactReports), nameof(Deserializing)),
                postfix: new HarmonyMethod(typeof(CompactReports), nameof(Deserialized)));
            harmony.Patch(AccessTools.Method(t, "Serialize"), prefix: new HarmonyMethod(typeof(CompactReports), nameof(Serializing)));
            harmony.Patch(AccessTools.Method(t, "Add"), prefix: new HarmonyMethod(typeof(CompactReports), nameof(Adding)));
            harmony.Patch(AccessTools.Method(t, "CollectNotes"), prefix: new HarmonyMethod(typeof(CompactReports), nameof(CollectingNotes)));
            var manager = typeof(ReportManager);
            harmony.Patch(AccessTools.Method(manager, "OnDeserialized"), postfix: new HarmonyMethod(typeof(CompactReports), nameof(ManagerDeserialized)));
            harmony.Patch(AccessTools.Method(manager, "OnNightTime"), prefix: new HarmonyMethod(typeof(CompactReports), nameof(NightStarting)),
                postfix: new HarmonyMethod(typeof(CompactReports), nameof(NightEnded)));
            harmony.Patch(AccessTools.Method(manager, "OnCleanUp"), postfix: new HarmonyMethod(typeof(CompactReports), nameof(ManagerCleanedUp)));
        }

        static ReportNoteStore<NoteEntryKey, Keys> StoreOf(NoteEntries owner) => owner == storeOwner ? store : null;

        static void Own(NoteEntries owner, ReportNoteStore<NoteEntryKey, Keys> s, NoteEntries gameCopy)
        {
            store = s;
            storeOwner = owner;
            shadow = gameCopy;
        }

        /// ONI_TWEAK_VERIFY=1: the game's own NoteEntries kept alongside the store's owner,
        /// written to as the game writes, and every read and save compared with it.
        static NoteEntries shadow;
        static long verifiedSaves, verifiedReads, mismatches;

        static void Mismatch(string what)
        {
            mismatches++;
            global::Debug.LogWarning($"{Name}: verify: {what} differs from the game's ({mismatches} so far)");
        }

        static string Describe(ReportNoteStore<NoteEntryKey, Keys> s) =>
            $"{s.Records:N0} entries, {s.Notes:N0} notes, {s.Bytes / (1024.0 * 1024.0):F1} MiB";

        // ---- Loading ----

        /// NoteEntries.Deserialize for version 6 and later: as the game reads it, into the store.
        static bool Deserializing(NoteEntries __instance, BinaryReader reader, int version)
        {
            if (version < 6 || __instance == shadow) return true;
            long from = reader.BaseStream.Position;
            var s = ReportNoteStore<NoteEntryKey, Keys>.Read(reader);
            __instance.entries = new Dictionary<int, Dictionary<NoteEntryKey, float>>();
            NoteEntries gameCopy = null;
            if (verify)
            {
                long to = reader.BaseStream.Position;
                reader.BaseStream.Position = from;
                gameCopy = new NoteEntries();
                shadow = gameCopy;
                gameCopy.Deserialize(reader, version);
                if (reader.BaseStream.Position != to) Mismatch($"the bytes read ({to - from} against {reader.BaseStream.Position - from})");
            }
            Own(__instance, s, gameCopy);
            return false;
        }

        /// Older saves: the game's own reading (its Add calls find no store yet), then all of it
        /// into the store, in the dictionary's order.
        static void Deserialized(NoteEntries __instance, int version)
        {
            if (version >= 6 || __instance == shadow) return;
            var s = ReportNoteStore<NoteEntryKey, Keys>.Adopt(__instance.entries);
            s.MoveAllIn(__instance.entries);
            Own(__instance, s, null);
        }

        /// Today's report back into `entries`: it is written to, and some mods read it there.
        static void ManagerDeserialized(ReportManager __instance)
        {
            var entries = __instance.noteStorage.noteEntries;
            var s = StoreOf(entries);
            if (s == null) return;
            if (__instance.todaysReport != null)
                foreach (var entry in __instance.todaysReport.reportEntries) MoveOut(s, entries, entry);
            if (verify) global::Debug.Log($"{Name}: loaded {Describe(s)}");
        }

        static void MoveOut(ReportNoteStore<NoteEntryKey, Keys> s, NoteEntries entries, ReportManager.ReportEntry entry)
        {
            s.MoveOut(entry.noteStorageId, entries.entries);
            for (int i = 0; i < entry.contextEntries.Count; i++) MoveOut(s, entries, entry.contextEntries[i]);
        }

        /// The colony ends (back to the main menu, or another load): its store goes with it.
        static void ManagerCleanedUp(ReportManager __instance)
        {
            if (__instance.noteStorage != null && __instance.noteStorage.noteEntries == storeOwner) Own(null, null, null);
        }

        // ---- Writing ----

        /// NoteEntries.Add: an entry the store holds goes back to `entries` first; a new entry
        /// gets its place in the store's order.
        [MirrorsGame("System.Void ReportManager/NoteStorage/NoteEntries::Add(System.Int32,System.Single,System.Int32)")]
        static void Adding(NoteEntries __instance, int report_entry_id, float value, int note_id)
        {
            var s = StoreOf(__instance);
            if (s == null) return;
            shadow?.Add(report_entry_id, value, note_id);
            s.Adding(report_entry_id, __instance.entries);
        }

        static ReportManager.DailyReport endingDay;

        static void NightStarting(ReportManager __instance) => endingDay = __instance.todaysReport;

        /// The day that ended goes into the store (a colony started in this session gets one now,
        /// its records in its dictionary's order).
        static void NightEnded(ReportManager __instance)
        {
            var day = endingDay;
            endingDay = null;
            var entries = __instance.noteStorage.noteEntries;
            if (day == null || day == __instance.todaysReport) return;
            var s = StoreOf(entries);
            if (s == null)
            {
                s = ReportNoteStore<NoteEntryKey, Keys>.Adopt(entries.entries);
                Own(entries, s, null);
            }
            foreach (var entry in day.reportEntries) MoveIn(s, entries, entry);
        }

        static void MoveIn(ReportNoteStore<NoteEntryKey, Keys> s, NoteEntries entries, ReportManager.ReportEntry entry)
        {
            s.MoveIn(entry.noteStorageId, entries.entries);
            for (int i = 0; i < entry.contextEntries.Count; i++) MoveIn(s, entries, entry.contextEntries[i]);
        }

        // ---- Reading ----

        /// NoteEntries.CollectNotes for an entry in the store: its notes added in the dictionary's
        /// order (the reports screen then sorts them by value, as before).
        static bool CollectingNotes(NoteEntries __instance, ReportManager.NoteStorage.StringTable string_table, int report_entry_id, List<ReportManager.ReportEntry.Note> notes)
        {
            var s = StoreOf(__instance);
            if (s == null || !s.TryNotes(report_entry_id, __instance.entries, out int start, out int n)) return true;
            if (shadow != null) VerifyNotes(s, string_table, report_entry_id, start, n);
            for (int i = start; i < start + n; i++)
                notes.Add(new ReportManager.ReportEntry.Note(s.NoteValue(i), string_table.GetStringByHash(s.NoteHash(i))));
            return false;
        }

        static void VerifyNotes(ReportNoteStore<NoteEntryKey, Keys> s, ReportManager.NoteStorage.StringTable string_table, int id, int start, int n)
        {
            var game = new List<ReportManager.ReportEntry.Note>();
            shadow.CollectNotes(string_table, id, game);
            verifiedReads++;
            bool same = game.Count == n;
            for (int i = 0; same && i < n; i++)
                same = BitConverter.SingleToInt32Bits(game[i].value) == BitConverter.SingleToInt32Bits(s.NoteValue(start + i))
                    && game[i].note == string_table.GetStringByHash(s.NoteHash(start + i));
            if (!same) Mismatch($"entry {id}'s notes");
        }

        /// Test infrastructure (PerfProbe's savetest.txt): true writes each note with the game's
        /// calls, for comparing saves in one run.
        public static bool Unpacked;

        static MethodBase writeSingleFast;
        static bool loggedForeign;

        /// Whether the store's notes go out in blocks: not while another mod patches the game's
        /// WriteSingleFast, which then writes each value.
        static bool Packed()
        {
            if (Unpacked || !BitConverter.IsLittleEndian) return false;
            writeSingleFast ??= AccessTools.Method(typeof(IOHelper), nameof(IOHelper.WriteSingleFast), new[] { typeof(BinaryWriter), typeof(float) });
            var info = Harmony.GetPatchInfo(writeSingleFast);
            if (info != null)
                foreach (var owner in info.Owners)
                    if (!ModOverlapsCore.IsOurs(owner))
                    {
                        if (!loggedForeign) global::Debug.Log($"{Name}: the notes are written one value at a time ({owner} patches IOHelper.WriteSingleFast)");
                        loggedForeign = true;
                        return false;
                    }
            return true;
        }

        /// NoteEntries.Serialize: the game's bytes, in the order of its dictionary.
        [MirrorsGame("System.Void KSerialization.IOHelper::WriteSingleFast(System.IO.BinaryWriter,System.Single)")]
        static bool Serializing(NoteEntries __instance, BinaryWriter writer)
        {
            var s = StoreOf(__instance);
            if (s == null) return true;
            bool packed = Packed();
            if (shadow != null)
            {
                var ours = new MemoryStream();
                var game = new MemoryStream();
                using (var w = new BinaryWriter(ours, System.Text.Encoding.UTF8, true)) s.Write(w, __instance.entries, packed);
                using (var w = new BinaryWriter(game, System.Text.Encoding.UTF8, true)) shadow.Serialize(w);
                verifiedSaves++;
                if (!ours.ToArray().AsSpan().SequenceEqual(game.ToArray())) Mismatch($"the saved notes ({ours.Length:N0} bytes against {game.Length:N0})");
                else global::Debug.Log($"{Name}: verify: saved notes identical ({ours.Length:N0} bytes; {verifiedSaves} saves, {verifiedReads} reads, {mismatches} mismatches)");
            }
            s.Write(writer, __instance.entries, packed);
            return false;
        }
    }
}
