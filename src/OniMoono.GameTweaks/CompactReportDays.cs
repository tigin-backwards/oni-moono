using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using DailyReport = ReportManager.DailyReport;
    using ReportEntry = ReportManager.ReportEntry;
    using Store = ReportDayStore<ReportManager.DailyReport, ReportManager.ReportEntry, CompactReportDays.Shapes>;

    /// Game tweak `compact-report-entries` (exact): past days' report entries in flat arrays
    /// instead of objects (ReportDayStore, in CompactReportDaysCore.cs).
    ///
    /// The game keeps every day's DailyReport for the colony's life, each with its
    /// `List<ReportEntry> reportEntries` (about 240 a day) and each entry with an ArrayRef of
    /// per-Duplicant or per-building entries: on a late-game colony (cycle 17,000) 4.1 million
    /// objects in 187,000 arrays, about a tenth of what a blocking full collection traces.
    /// compact-report-history already keeps their notes in flat arrays; this keeps the entries
    /// there too. Today's report is never packed.
    ///
    /// - **Packing.** After a load (ReportManager.OnDeserialized) every past day, and at each
    ///   night (OnNightTime) the day that ended, goes into the store, and its `reportEntries`
    ///   becomes null. A day of a shape the store does not keep exactly stays as the game has it.
    /// - **Reading.** The game reads `reportEntries` itself only in DailyReport's constructor (a
    ///   new day), DailyReport.GetEntry, SaveLoader.GetDailyReportMetrics (a day from
    ///   FindReport) and SimpleInfoScreen.RefreshStressPanel (today's), checked at start-up from
    ///   its IL (any other method keeps the tweak off). GetEntry on a packed day gives out that
    ///   entry as an object (a prefix: the game's search, the first entry of the type); when the
    ///   day has none, the day's list is given out whole and the game's GetEntry adds one to it,
    ///   as it would. FindReport gives out its day's list whole. Objects given out are kept for
    ///   the most recent 32 days read, so paging the reports screen reads the same objects; a day
    ///   leaving those is compared with its records, and packed again from the objects when
    ///   something wrote to it.
    /// - **The colony summary.** Every save's RetiredColonyData reads 16 entries of every day
    ///   through GetEntry and keeps only numbers: its calls go to Peek (a transpiler), which
    ///   fills reused scratch entries from the records instead of making 300,000 objects a save.
    /// - **Saving.** fast-serialization's writer for the history (ReportEntries) writes a
    ///   packed day from its records, the same bytes. A save that would go through the game's
    ///   own writer (fast-serialization off, a mod patching what that writer stands in for) gets
    ///   every day's list back in its field for the save (Give, Take).
    ///
    /// Stands down (off for the session, one line in the log) when a mod could see past days'
    /// entries other than through those methods: a loaded mod assembly that references the
    /// field, takes it as a Harmony parameter or names it in a string (the host's
    /// ReportAccessScan), or another owner's patch of what the tweak relies on (Guarded).
    /// Entries' `contextEntries` are not watched: every entry given out is a whole object.
    ///
    /// ONI_TWEAK_VERIFY=1 keeps each packed day's original list, compares every entry given out
    /// and every Peek with it, and writes each save's history the game's way over those lists to
    /// compare the bytes.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "ReportManager/ReportEntry ReportManager/DailyReport::GetEntry(ReportManager/ReportType)", "1f05791de6eef6d9 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025", "7e220c7a830b7e97 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    [GameMember(GameUse.Postfix, "ReportManager/DailyReport ReportManager::FindReport(System.Int32)")]
    [GameMember(GameUse.Transpiler, "System.Void RetiredColonyData::.ctor(System.String,System.Int32,System.String,System.String[],MinionAssignablesProxy[],BuildingComplete[],System.String,System.Collections.Generic.Dictionary`2<System.String,System.String>)", "49f886f6874d6663 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025", "c5d5d52628e18262 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", Rewrites = 16)]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Void ReportManager::OnNightTime(System.Object)")]
    [GameMember(GameUse.Postfix, "System.Void ReportManager::OnDeserialized()")]
    [GameMember(GameUse.Postfix, "System.Void ReportManager::OnCleanUp()")]
    [GameMember(GameUse.Postfix, "System.Void ReportManager::DestroyInstance()")]
    [GameMember(GameUse.Prefix | GameUse.Finalizer, "System.Void SaveLoader::Save(System.IO.BinaryWriter)")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<ReportManager/DailyReport> ReportManager::dailyReports")]
    [GameMember(GameUse.Uses, "ReportManager/DailyReport ReportManager::todaysReport")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<ReportManager/ReportEntry> ReportManager/DailyReport::reportEntries")]
    [GameMember(GameUse.Uses, "ArrayRef`1<ReportManager/ReportEntry> ReportManager/ReportEntry::contextEntries")]
    public static class CompactReportDays
    {
        public const string Name = "compact-report-entries";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// From the host (ReportAccessScan): why a loaded mod assembly could see past days'
        /// entries other than through the game's methods, or null; and the game's methods that
        /// read or write `reportEntries` ("Type::Method kinds").
        public static Func<Assembly, string> ModAccess;
        public static Func<string[]> GameSites;

        /// The game's reads of `reportEntries` this tweak serves (see the class).
        static readonly string[] knownSites =
        {
            "ReportManager/DailyReport::.ctor ldfld stfld",
            "ReportManager/DailyReport::GetEntry ldfld",
            "SaveLoader::GetDailyReportMetrics ldfld",
            "SimpleInfoScreen::RefreshStressPanel ldfld",
        };

        /// Test infrastructure (PerfProbe's savetest.txt): true unpacks every day for good at the
        /// next save, as if the tweak were off.
        public static bool Unpack;

        /// The game's types for the store.
        public struct Shapes : IReportDayShapes<DailyReport, ReportEntry>
        {
            public int Day(DailyReport day) => day.day;
            public List<ReportEntry> Entries(DailyReport day) => day.reportEntries;
            public int NoteStorageId(ReportEntry entry) => entry.noteStorageId;
            public int GameHash(ReportEntry entry) => entry.gameHash;
            public int ReportType(ReportEntry entry) => (int)entry.reportType;
            public string Context(ReportEntry entry) => entry.context;
            public int ContextId(ReportEntry entry) => entry.contextId;
            public float Accumulate(ReportEntry entry) => entry.accumulate;
            public float AccPositive(ReportEntry entry) => entry.accPositive;
            public float AccNegative(ReportEntry entry) => entry.accNegative;
            public ReportEntry[] ContextElements(ReportEntry entry) => entry.contextEntries.elements;
            public int ContextSize(ReportEntry entry) => entry.contextEntries.sizeImpl;
            public int ContextCapacity(ReportEntry entry) => entry.contextEntries.capacityImpl;
            public void DayTemplate() { }
            public void EntryTemplate() { }
            public void ContextTemplate() { }
            public void LargeString(string text, int byteCount) { }
            public ReportEntry New() => new ReportEntry(default, 0, null);
            public void Fill(ReportEntry e, in PackedEntry r, string context, ReportEntry[] elements)
            {
                e.noteStorageId = r.Id;
                e.gameHash = -1;
                e.reportType = (ReportManager.ReportType)r.Type;
                e.context = context;
                e.contextId = r.ContextId;
                e.accumulate = r.Accumulate;
                e.accPositive = r.Positive;
                e.accNegative = r.Negative;
                e.contextEntries.elements = elements;
                e.contextEntries.sizeImpl = r.Size;
                e.contextEntries.capacityImpl = r.Capacity;
                e.isChild = r.Child != 0;
            }
            public bool IsChild(ReportEntry entry) => entry.isChild;
            public bool Plain(ReportEntry entry) => entry.GetType() == typeof(ReportEntry);
            public void SetEntries(DailyReport day, List<ReportEntry> entries) => day.reportEntries = entries;
        }

        /// The store of the ReportManager in use, until the colony ends.
        static Store store;
        static ReportManager owner;
        /// 0 not decided yet, 1 on, -1 off for the session.
        static int state;
        static Harmony harmony;

        public static void Install(Harmony h)
        {
            harmony = h;
            var day = typeof(DailyReport);
            var manager = typeof(ReportManager);
            h.Patch(AccessTools.Method(day, nameof(DailyReport.GetEntry)), prefix: new HarmonyMethod(typeof(CompactReportDays), nameof(GettingEntry)));
            h.Patch(AccessTools.Method(manager, nameof(ReportManager.FindReport)), postfix: new HarmonyMethod(typeof(CompactReportDays), nameof(FoundReport)));
            h.Patch(SummaryConstructor(), transpiler: new HarmonyMethod(typeof(CompactReportDays), nameof(SummaryReads)));
            h.Patch(AccessTools.Method(manager, "OnNightTime"), prefix: new HarmonyMethod(typeof(CompactReportDays), nameof(NightStarting)),
                postfix: new HarmonyMethod(typeof(CompactReportDays), nameof(NightEnded)) { priority = Priority.Last });
            h.Patch(AccessTools.Method(manager, "OnDeserialized"), postfix: new HarmonyMethod(typeof(CompactReportDays), nameof(Loaded)) { priority = Priority.Last });
            h.Patch(AccessTools.Method(manager, "OnCleanUp"), postfix: new HarmonyMethod(typeof(CompactReportDays), nameof(CleanedUp)));
            h.Patch(AccessTools.Method(manager, nameof(ReportManager.DestroyInstance)), postfix: new HarmonyMethod(typeof(CompactReportDays), nameof(Released)));
            h.Patch(AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(System.IO.BinaryWriter) }),
                prefix: new HarmonyMethod(typeof(CompactReportDays), nameof(SaveStarting)), finalizer: new HarmonyMethod(typeof(CompactReportDays), nameof(SaveEnded)));
        }

        static ConstructorInfo SummaryConstructor() => AccessTools.Constructor(typeof(RetiredColonyData), new[]
        {
            typeof(string), typeof(int), typeof(string), typeof(string[]), typeof(MinionAssignablesProxy[]), typeof(BuildingComplete[]), typeof(string), typeof(Dictionary<string, string>),
        });

        /// The methods another owner's patch of makes the tweak stand down (all but postfixes).
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(DailyReport), nameof(DailyReport.GetEntry)),
            AccessTools.Method(typeof(ReportManager), nameof(ReportManager.FindReport)),
            SummaryConstructor(),
        };

        internal static ProbeRepairPolicy.GuardedKinds KindsOf(MethodBase m) => ProbeRepairPolicy.GuardedKinds.AllButPostfixes;

        static void Log(string line) => global::Debug.Log($"{Name}: {line}");

        // ---- Whether it runs ----

        /// At the first day to pack: on, or off for the session with one line saying why.
        static bool On()
        {
            if (state != 0) return state > 0;
            string why;
            try
            {
                why = Reason();
            }
            catch (Exception e)
            {
                why = $"{e.GetType().Name}: {e.Message}";
            }
            state = why == null ? 1 : -1;
            if (why != null) Log($"off for this session: {why}");
            return state > 0;
        }

        [NameLookupReviewed("ReportEntry's, DailyReport's and ArrayRef's own fields, listed to check they are all the store keeps")]
        static string Reason()
        {
            const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (Fields(typeof(ReportEntry), All) != "accNegative accPositive accumulate context contextEntries contextId gameHash isChild noteStorageId reportType")
                return $"ReportEntry's fields are {Fields(typeof(ReportEntry), All)}";
            if (Fields(typeof(DailyReport), All) != "day reportEntries") return $"DailyReport's fields are {Fields(typeof(DailyReport), All)}";
            if (Fields(typeof(ArrayRef<ReportEntry>), All) != "capacityImpl elements sizeImpl") return "ArrayRef's fields changed";
            if (!ReportEntries.Writes) return "fast-serialization's writer of the report history is not in use";
            if (GameSites == null || ModAccess == null) return "the host's assembly scan is not there";
            var sites = GameSites();
            foreach (var site in sites)
                if (Array.IndexOf(knownSites, site) < 0) return $"the game's {site} reads DailyReport.reportEntries itself";
            if (ForeignPatches.Find(Guarded(), KindsOf) is { } patch) return patch;
            var mods = Global.Instance?.modManager?.mods;
            if (mods != null)
                foreach (var mod in mods)
                {
                    var dlls = mod?.loaded_mod_data?.dlls;
                    if (dlls == null) continue;
                    // Test infrastructure (ONI_MOD_OVERLAPS_IGNORE: OniMcp in every e2e spec) and our perf tool.
                    if (mod.loaded_mod_data.harmony?.Id is { } id && !ModOverlapsCore.IsForeign(id)) continue;
                    foreach (var dll in dlls)
                        if (dll != null && ModAccess(dll) is { } access)
                            return $"{(string.IsNullOrEmpty(mod.title) ? mod.label.id : mod.title)} ({dll.GetName().Name}) {access}";
                }
            return null;
        }

        static string Fields(Type type, BindingFlags flags)
        {
            var names = new List<string>();
            foreach (var f in type.GetFields(flags)) names.Add(f.Name);
            names.Sort(StringComparer.Ordinal);
            return string.Join(" ", names);
        }

        /// A patch of another owner made since: every day unpacked for good, the tweak off.
        static void CheckForeign()
        {
            if (store == null || ForeignPatches.Find(Guarded(), KindsOf) is not { } patch) return;
            StandDown($"{patch} since the colony loaded");
        }

        static void StandDown(string why)
        {
            if (store != null)
            {
                store.SyncViews();
                store.Give();
                store = null;
            }
            ReportEntries.Packed = null;
            state = -1;
            Log($"off for this session: {why}; every day's entries are objects again");
        }

        static Store StoreOf(ReportManager manager)
        {
            if (manager != owner || store == null)
            {
                owner = manager;
                store = new Store();
                shadows = verify ? new Dictionary<DailyReport, List<ReportEntry>>() : null;
                if (verify) store.Repacked = Repacked;
                ReportEntries.Packed = store;
            }
            return store;
        }

        // ---- Packing ----

        /// After a load: every past day into the store.
        static void Loaded(ReportManager __instance)
        {
            if (!On()) return;
            var watch = Stopwatch.StartNew();
            var s = StoreOf(__instance);
            int kept = 0;
            foreach (var day in __instance.dailyReports)
            {
                var list = day?.reportEntries;
                if (!s.Pack(day, checkShared: false)) kept++;
                else if (shadows != null) shadows[day] = list;
            }
            Log($"{Describe(s)} packed in {watch.ElapsedMilliseconds} ms" + (kept > 0 ? $"; {kept} kept as the game has them" : ""));
        }

        static string Describe(Store s) => $"{s.Days:N0} days, {s.Records.Count:N0} entries, {s.Records.Strings:N0} contexts, {s.Bytes / 1e6:F1} MB";

        static DailyReport endingDay;

        static void NightStarting(ReportManager __instance) => endingDay = __instance.todaysReport;

        /// The day that ended into the store (after compact-report-history's move of its notes,
        /// and any mod's postfix that trims the history: Priority.Last).
        static void NightEnded(ReportManager __instance)
        {
            var day = endingDay;
            endingDay = null;
            if (day == null || day == __instance.todaysReport || !On()) return;
            CheckForeign();
            if (state < 0) return;
            var s = StoreOf(__instance);
            var list = day.reportEntries;
            if (s.Pack(day, checkShared: true) && shadows != null) shadows[day] = list;
            int pruned = s.Prune(__instance.dailyReports);
            if (shadows != null && pruned > 0)
                foreach (var d in new List<DailyReport>(shadows.Keys))
                    if (!s.IsPacked(d)) shadows.Remove(d);
            s.Compact();
        }

        static void CleanedUp(ReportManager __instance)
        {
            if (__instance == owner) Released();
        }

        /// ReportManager.DestroyInstance: the colony is left (Game.OnDestroy). A scene unload
        /// destroys the manager without its OnCleanUp, so the store is let go here.
        static void Released()
        {
            store = null;
            owner = null;
            shadows = null;
            ReportEntries.Packed = null;
        }

        // ---- Reading ----

        /// DailyReport.GetEntry on a packed day: its entry of that type as an object, or the
        /// day's list given out whole for the game's code to add one.
        static bool GettingEntry(DailyReport __instance, ReportManager.ReportType reportType, ref ReportEntry __result)
        {
            if (__instance.reportEntries != null || store == null) return true;
            var e = store.Top(__instance, (int)reportType, out bool packed);
            if (!packed) return true;
            if (e != null)
            {
                if (shadows != null) VerifyEntry(__instance, reportType, e, "GetEntry");
                __result = e;
                return false;
            }
            store.Full(__instance);
            if (shadows != null) VerifyDay(__instance, "GetEntry of a type it has not");
            return true;
        }

        /// ReportManager.FindReport: a packed day's list given out whole (the reports screen
        /// reads all of it; SaveLoader.GetDailyReportMetrics reads the field).
        static void FoundReport(DailyReport __result)
        {
            if (__result == null || __result.reportEntries != null || store == null || !store.IsPacked(__result)) return;
            store.Full(__result);
            if (shadows != null) VerifyDay(__result, "FindReport");
        }

        /// RetiredColonyData's `DailyReport.GetEntry` calls call Peek.
        static IEnumerable<CodeInstruction> SummaryReads(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var getEntry = AccessTools.Method(typeof(DailyReport), nameof(DailyReport.GetEntry));
            var peek = AccessTools.Method(typeof(CompactReportDays), nameof(Peek));
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(getEntry))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = peek;
                    n++;
                }
                yield return ci;
            }
            TranspilerCounts.Report(Name, original, n);
        }

        /// DailyReport.GetEntry for the colony summary, which keeps only numbers of what it is
        /// given: a packed day's entry in a scratch object (Store.Peek), else GetEntry.
        public static ReportEntry Peek(DailyReport day, ReportManager.ReportType reportType)
        {
            if (day == null) throw new NullReferenceException();
            if (day.reportEntries == null && store != null && store.Peek(day, (int)reportType) is { } e)
            {
                if (shadows != null) VerifyEntry(day, reportType, e, "the colony summary");
                return e;
            }
            return day.GetEntry(reportType);
        }

        // ---- Saving ----

        static bool given;

        /// A save the game's own writer will write: every day's list in its field for it.
        static void SaveStarting()
        {
            if (store == null) return;
            CheckForeign();
            if (store == null) return;
            if (Unpack)
            {
                Unpack = false;
                StandDown("unpacked for a test save");
                return;
            }
            if (ReportEntries.WritesPacked()) return;
            Log("this save goes through the game's writer: every day's entries are objects while it writes");
            store.SyncViews();
            store.Give();
            given = true;
        }

        static void SaveEnded()
        {
            if (!given) return;
            given = false;
            store?.Take();
        }

        /// For ReportEntries: the game's writer runs now (a mod's patch, a test): the days' lists
        /// in their fields while it writes, the original ones in verify mode.
        internal static void WithEntries(System.Action write, bool original)
        {
            if (store == null || given)
            {
                write();
                return;
            }
            store.SyncViews();
            if (original && shadows != null) store.Give(shadows);
            else store.Give();
            try
            {
                write();
            }
            finally
            {
                store.Take();
            }
        }

        // ---- Verify mode ----

        static Dictionary<DailyReport, List<ReportEntry>> shadows;
        static long verifiedReads, mismatches;

        static void Mismatch(string what)
        {
            mismatches++;
            global::Debug.LogWarning($"{Name}: verify: {what} differs from the game's ({mismatches} so far)");
        }

        static ReportEntry ShadowEntry(DailyReport day, ReportManager.ReportType type)
        {
            if (!shadows.TryGetValue(day, out var list)) return null;
            foreach (var e in list)
                if (e.reportType == type) return e;
            return null;
        }

        static void VerifyEntry(DailyReport day, ReportManager.ReportType type, ReportEntry e, string how)
        {
            verifiedReads++;
            if (!shadows.ContainsKey(day)) return;
            if (store.Differs(e, ShadowEntry(day, type), $"day {day.day} {type}") is { } why) Mismatch($"{how}: {why}");
            Summary();
        }

        static void VerifyDay(DailyReport day, string how)
        {
            verifiedReads++;
            if (!shadows.TryGetValue(day, out var list)) return;
            if (store.Differs(day.reportEntries, list) is { } why) Mismatch($"{how}: day {day.day}: {why}");
            Summary();
        }

        static long summarized;

        static void Summary()
        {
            if (verifiedReads < summarized * 2 && verifiedReads - summarized < 100_000) return;
            summarized = verifiedReads;
            Log($"verify: {verifiedReads:N0} reads compared, {mismatches} mismatches; {store.Tops:N0} entries and {store.Fulls:N0} days given out, {store.Peeks:N0} peeks, {store.Evictions:N0} views closed, {store.Writebacks:N0} written back");
        }

        /// A day written to and packed again: what the game would now hold.
        static void Repacked(DailyReport day, List<ReportEntry> list)
        {
            if (shadows == null) return;
            Log($"verify: day {day.day} was written to; its record follows");
            shadows[day] = store.Copy(list);
        }

        internal static string VerifyLine() => store == null || shadows == null ? null
            : $"{verifiedReads:N0} reads compared, {mismatches} mismatches, {Describe(store)}";
    }
}
