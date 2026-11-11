using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using Reads = HeaderReads<SaveGame.Header, SaveGame.GameInfo>;

    /// Game tweak `fast-load-screen`: the Load screen reads each save's header once a refresh,
    /// several at a time, with the same results.
    ///
    /// Each refresh of the screen (LoadScreen.RefreshColonyList: opening it, Load more, a
    /// conversion between cloud and local) makes two passes over every save
    /// (CheckCloudLocalOverlap's GetColonies, then the list's), and each pass reads every header
    /// twice (IsFileValid, then SaveGame.GetFileInfo): SaveLoader.LoadHeader opens the file,
    /// reads it and parses its JSON four times a save. With thousands of saves that is most of
    /// the screen's time, and on slow storage (a network drive, WSL's /mnt/c) all of it.
    ///
    /// - For the length of one refresh, LoadHeader's result for a file of the listing being read
    ///   (GetColoniesDetails' files) is kept, under that listing's last write time: the next read
    ///   of the same file in the refresh, while its listed time is the same, gets a copy (its own
    ///   worldTraits array and dlcIds list, as each parse makes new ones). A read that throws is
    ///   not kept, so the next caller reads the file again and logs the same warning. The memo is
    ///   dropped when the refresh returns: a file changed between refreshes is read again.
    /// - When a listing's reads begin, the raw headers of its files the memo does not hold are
    ///   read ahead on worker threads (HeaderReads), as LoadHeader reads them. The main thread
    ///   still asks for the files in the game's order and parses each itself
    ///   (SaveGame.GetHeader, with its fix-ups and once-only warning), so the results, the order
    ///   and the log lines are the game's. A file not read ahead yet, or whose read failed, or
    ///   whose bytes do not parse, is read by the game's own LoadHeader, which throws and logs
    ///   as it would.
    ///
    /// The bytes are read after the listing, as the game's own reads are; a file rewritten
    /// during a refresh is a race in the game too (its four reads can disagree). Mods' prefixes
    /// on LoadHeader run as before (this one runs last); a mod watching SaveGame.GetHeader or
    /// GetGameInfo sees one call a file a refresh instead of four.
    ///
    /// With ONI_TWEAK_VERIFY=1 each header served from the memo or parsed from bytes read ahead
    /// is checked against the game's own LoadHeader of the same file, and each refresh logs how
    /// many were checked and how many differed.
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<LoadScreen/SaveGameFileDetails>> LoadScreen::GetColoniesDetails(System.Collections.Generic.List`1<SaveLoader/SaveFileEntry>)")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Void LoadScreen::RefreshColonyList()")]
    [GameMember(GameUse.Prefix | GameUse.Postfix | GameUse.Replaces, "SaveGame/GameInfo SaveLoader::LoadHeader(System.String,SaveGame/Header&)", "747424c5f90b2bed linux-744825 win-744825", "556cd23977cbd791 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    public static class FastLoadScreen
    {
        public const string Name = "fast-load-screen";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        static Reads reads;
        static int depth, beganFrame;
        static bool bypass;
        static int verified, differed;

        [NameLookupReviewed("Every field of SaveGame.GameInfo, copied whatever they are")]
        public static void Install(Harmony harmony)
        {
            // Every field of GameInfo must be one a copy can be made of: a value, a string, a
            // string array or a list of strings (a game update adding another is left alone).
            foreach (var field in typeof(SaveGame.GameInfo).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var type = field.FieldType;
                if (!(type.IsPrimitive || type.IsEnum || type == typeof(Guid) || type == typeof(string) || type == typeof(string[]) || type == typeof(List<string>)))
                {
                    global::Debug.Log($"{Name}: SaveGame.GameInfo.{field.Name} is a {field.FieldType}, which this tweak does not copy; the Load screen reads as the game does");
                    return;
                }
            }
            var screen = typeof(LoadScreen);
            harmony.Patch(AccessTools.Method(screen, "RefreshColonyList"),
                prefix: new HarmonyMethod(typeof(FastLoadScreen), nameof(BeginRefresh)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(FastLoadScreen), nameof(EndRefresh)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(screen, "GetColoniesDetails"),
                prefix: new HarmonyMethod(typeof(FastLoadScreen), nameof(BeginListing)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FastLoadScreen), nameof(EndListing)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.LoadHeader)),
                prefix: new HarmonyMethod(typeof(FastLoadScreen), nameof(LoadHeader)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FastLoadScreen), nameof(LoadedHeader)) { priority = Priority.First });
        }

        static void BeginRefresh()
        {
            // The whole refresh runs in one frame: a refresh of an earlier frame that never
            // returned (it threw) left its memo behind.
            if (depth > 0 && beganFrame != UnityEngine.Time.frameCount) Drop();
            if (depth++ > 0) return;
            beganFrame = UnityEngine.Time.frameCount;
            reads = new Reads(Copy);
            verified = differed = 0;
        }

        static void EndRefresh()
        {
            if (depth == 0 || --depth > 0) return;
            if (verify && reads != null)
                global::Debug.Log($"{Name}: verified {verified} header reads ({reads.Hits} from the memo, {reads.ReadAheadTaken} read ahead): {differed} differed");
            Drop();
        }

        static void Drop()
        {
            reads?.End();
            reads = null;
            depth = 0;
        }

        static void BeginListing(List<SaveLoader.SaveFileEntry> files)
        {
            if (reads == null || files == null) return;
            var paths = new string[files.Count];
            var stamps = new System.DateTime[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                paths[i] = files[i].path;
                stamps[i] = files[i].timeStamp;
            }
            reads.Begin(paths, stamps);
        }

        static void EndListing() => reads?.End();

        static bool LoadHeader(string filename, ref SaveGame.Header header, ref SaveGame.GameInfo __result, bool __runOriginal, out bool __state)
        {
            __state = false;
            if (reads == null || bypass || !__runOriginal || filename == null) return true;
            if (reads.TryGet(filename, out var memoHeader, out var memoInfo))
            {
                header = memoHeader;
                __result = memoInfo;
                Check(filename, header, __result);
                return false;
            }
            var raw = reads.TakeRaw(filename);
            if (raw != null)
            {
                try
                {
                    __result = SaveGame.GetHeader(new FastReader(raw), out header, filename);
                }
                catch (Exception)
                {
                    // The game's own read throws (and its callers log) as it would.
                    __state = true;
                    return true;
                }
                reads.Store(filename, header, __result);
                Check(filename, header, __result);
                return false;
            }
            __state = true;
            return true;
        }

        /// After the game's own read (only when the prefix let it run): kept for the refresh.
        static void LoadedHeader(string filename, SaveGame.Header header, SaveGame.GameInfo __result, bool __runOriginal, bool __state)
        {
            if (__state && __runOriginal && reads != null && !bypass) reads.Store(filename, header, __result);
        }

        static SaveGame.GameInfo Copy(SaveGame.GameInfo info)
        {
            if (info.worldTraits != null) info.worldTraits = (string[])info.worldTraits.Clone();
            if (info.dlcIds != null)
            {
                var ids = new List<string>(info.dlcIds.Capacity);
                ids.AddRange(info.dlcIds);
                info.dlcIds = ids;
            }
            return info;
        }

        /// ONI_TWEAK_VERIFY: the game's own read of the file must give the same header.
        static void Check(string filename, SaveGame.Header header, SaveGame.GameInfo info)
        {
            if (!verify) return;
            verified++;
            bypass = true;
            try
            {
                var game = SaveLoader.LoadHeader(filename, out var gameHeader);
                if (!header.Equals(gameHeader) || !Same(info, game))
                {
                    if (differed++ < 5) global::Debug.Log($"{Name}: the header of {filename} differs from the game's read");
                }
            }
            catch (Exception e)
            {
                if (differed++ < 5) global::Debug.Log($"{Name}: the game's read of {filename} threw {e.GetType().Name}, this one did not");
            }
            finally
            {
                bypass = false;
            }
        }

        [NameLookupReviewed("Every field of SaveGame.GameInfo, compared whatever they are")]
        static bool Same(SaveGame.GameInfo a, SaveGame.GameInfo b)
        {
            foreach (var field in typeof(SaveGame.GameInfo).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object x = field.GetValue(a), y = field.GetValue(b);
                if (x is string[] xs && y is string[] ys)
                {
                    if (ReferenceEquals(xs, ys) || xs.Length != ys.Length) return false;
                    for (int i = 0; i < xs.Length; i++) if (xs[i] != ys[i]) return false;
                }
                else if (x is List<string> xl && y is List<string> yl)
                {
                    if (ReferenceEquals(xl, yl) || xl.Count != yl.Count) return false;
                    for (int i = 0; i < xl.Count; i++) if (xl[i] != yl[i]) return false;
                }
                else if (!Equals(x, y)) return false;
            }
            return true;
        }
    }
}
