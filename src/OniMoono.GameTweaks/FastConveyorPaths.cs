using System;
using System.Collections.Generic;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    using Conduit = SolidConduitFlow.Conduit;

    /// Game tweak `fast-conveyor-paths` (opt-in): the conveyor rails' network rebuild, the same
    /// paths.
    ///
    /// Every change to the rails (one built, removed or reconnected, and the load) rebuilds the
    /// whole conveyor network: SolidConduitFlow walks from every source to every sink and keeps
    /// each source-to-sink path it finds in a list (TryAdd). For each new path TryAdd asks three
    /// things of every path already kept, each by searching the paths through: whether one of them
    /// already holds the new one (then the new one is dropped), which of them the new one holds
    /// (they are dropped), and, for each rail of the new path, whether any remaining path has it
    /// (a rail with only one way to go is then left out of the new path). The cost grows with the
    /// paths kept times their lengths, for every path found: U57 (447 paths kept out of 735
    /// found) spends 76–770 ms of each rebuild there, on the main thread, a hitch on every edit.
    ///
    /// The tweak keeps an index of the kept paths beside the game's list: for each rail, the paths
    /// that have it and its place in each, how many of them there are, and the paths starting with
    /// it. The three questions are then asked only of the paths that can answer yes: those having
    /// the new path's first rail; those starting with one of its rails; the counts. Each answer is
    /// the game's own test, with the same places compared, so the list ends up the same, in the
    /// same order (paths dropped are taken out of the list keeping the others' order, as the
    /// game's removals one by one do). The index follows the game's list: when the list is not
    /// what the index last left it (cleared by the rebuild, or changed by anyone else), it is
    /// made again from the list. While another Harmony owner patches TryAdd, the game's runs.
    ///
    /// With ONI_TWEAK_VERIFY=1 the game's TryAdd also runs on a copy of the list and of the new
    /// path, and both lists must end up the same: the same paths, in the same order, with the
    /// same rails.
    [GameMember(GameUse.Postfix, "System.Void SolidConduitFlow::OnUtilityNetworksRebuilt(System.Collections.Generic.IList`1<UtilityNetwork>,System.Collections.Generic.ICollection`1<System.Int32>)")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SolidConduitFlow::TryAdd(System.Collections.Generic.List`1<SolidConduitFlow/Conduit>)", "72811b7834149aa6 linux-744825 win-744825")]
    public static class FastConveyorPaths
    {
        public const string Name = "fast-conveyor-paths";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        static readonly AccessTools.FieldRef<SolidConduitFlow, List<List<Conduit>>> pathList =
            AccessTools.FieldRefAccess<SolidConduitFlow, List<List<Conduit>>>("pathList");
        static System.Reflection.MethodInfo tryAdd;
        static Action<SolidConduitFlow, List<Conduit>> gameTryAdd;

        public static void Install(Harmony harmony)
        {
            tryAdd = AccessTools.Method(typeof(SolidConduitFlow), "TryAdd");
            gameTryAdd = AccessTools.MethodDelegate<Action<SolidConduitFlow, List<Conduit>>>(tryAdd);
            harmony.Patch(tryAdd, prefix: new HarmonyMethod(typeof(FastConveyorPaths), nameof(TryAdd)) { priority = Priority.Last });
            if (verify)
                harmony.Patch(AccessTools.Method(typeof(SolidConduitFlow), "OnUtilityNetworksRebuilt"), postfix: new HarmonyMethod(typeof(FastConveyorPaths), nameof(Report)));
        }

        // ---- The index ----

        /// A kept path: its rails' indices, by place.
        sealed class Path
        {
            public List<Conduit> List;
            public int[] Rails;
            public bool Dropped;
            /// Stamp and place for the first question's lookup of the new path's last rail.
            public int Mark, MarkPlace;
        }

        struct Place
        {
            public Path Path;
            public int At;
        }

        static SolidConduitFlow owner;
        /// The paths in the game's list order, as the index last left them.
        static readonly List<Path> kept = new List<Path>();
        static List<Place>[] places = new List<Place>[0];
        static List<Path>[] starting = new List<Path>[0];
        static int[] holding = new int[0];
        static int[] newMark = new int[0], newPlace = new int[0];
        static int stamp, empty;
        static int checks;
        static bool foreign;

        static void Reset(int rails)
        {
            kept.Clear();
            empty = 0;
            if (places.Length < rails)
            {
                int size = Math.Max(rails, places.Length * 2);
                Array.Resize(ref places, size);
                Array.Resize(ref starting, size);
                Array.Resize(ref holding, size);
                Array.Resize(ref newMark, size);
                Array.Resize(ref newPlace, size);
            }
            for (int i = 0; i < places.Length; i++)
            {
                places[i]?.Clear();
                starting[i]?.Clear();
                holding[i] = 0;
            }
        }

        static void Grow(int rail)
        {
            if (rail < places.Length) return;
            int size = Math.Max(rail + 1, places.Length * 2);
            Array.Resize(ref places, size);
            Array.Resize(ref starting, size);
            Array.Resize(ref holding, size);
            Array.Resize(ref newMark, size);
            Array.Resize(ref newPlace, size);
        }

        static void Add(List<Conduit> list)
        {
            var path = new Path { List = list, Rails = new int[list.Count] };
            for (int i = 0; i < list.Count; i++)
            {
                int rail = list[i].idx;
                Grow(rail);
                path.Rails[i] = rail;
                (places[rail] ??= new List<Place>()).Add(new Place { Path = path, At = i });
                holding[rail]++;
            }
            if (list.Count == 0) empty++;
            else (starting[path.Rails[0]] ??= new List<Path>()).Add(path);
            kept.Add(path);
        }

        static void Drop(Path path)
        {
            path.Dropped = true;
            foreach (int rail in path.Rails) holding[rail]--;
        }

        /// The index made again when the game's list is not what it last left (cleared by a
        /// rebuild, or changed by someone else).
        static void Follow(SolidConduitFlow flow, List<List<Conduit>> paths)
        {
            bool same = ReferenceEquals(owner, flow) && kept.Count == paths.Count;
            for (int i = 0; same && i < paths.Count; i++)
                same = ReferenceEquals(kept[i].List, paths[i]) && kept[i].Rails.Length == paths[i].Count;
            if (same) return;
            owner = flow;
            Reset(flow.GetSOAInfo().NumEntries);
            foreach (var list in paths) Add(list);
        }

        // ---- TryAdd ----

        static bool bypass;

        static bool TryAdd(SolidConduitFlow __instance, List<Conduit> new_path)
        {
            if (bypass || new_path == null || new_path.Count == 0) return true;
            if (checks++ % 256 == 0) foreign = Foreign();
            if (foreign) return true;
            var paths = pathList(__instance);
            Follow(__instance, paths);
            List<List<Conduit>> before = null;
            List<Conduit> theirs = null;
            if (verify)
            {
                // The game's TryAdd on copies of the list and the new path.
                before = new List<List<Conduit>>(paths);
                theirs = new List<Conduit>(new_path);
                pathList(__instance) = before;
                bypass = true;
                try { gameTryAdd(__instance, theirs); }
                finally { bypass = false; pathList(__instance) = paths; }
            }
            Keep(__instance, paths, new_path);
            if (verify) Compare(paths, new_path, before, theirs);
            return false;
        }

        /// The game's TryAdd, its three searches answered from the index.
        static void Keep(SolidConduitFlow flow, List<List<Conduit>> paths, List<Conduit> new_path)
        {
            int count = new_path.Count;
            foreach (var c in new_path) Grow(c.idx);
            // A kept path at least as long holding the new one's first and last rails, with the
            // rails from the first's place up to the last's those of the new path: the new path
            // is not kept.
            int first = new_path[0].idx, last = new_path[count - 1].idx;
            if (places[first] != null && places[last] != null && holding[first] > 0 && holding[last] > 0)
            {
                int mark = ++stamp;
                foreach (var p in places[last])
                    if (!p.Path.Dropped) { p.Path.Mark = mark; p.Path.MarkPlace = p.At; }
                foreach (var p in places[first])
                {
                    var path = p.Path;
                    if (path.Dropped || path.Mark != mark || path.Rails.Length < count) continue;
                    bool held = true;
                    for (int i = p.At, j = 0; i < path.MarkPlace; i++, j++)
                        if (path.Rails[i] != new_path[j].idx) { held = false; break; }
                    if (held) return;
                }
            }
            // The kept paths no longer than the new one that it holds the same way are dropped,
            // and so are empty ones.
            int newStamp = ++stamp;
            for (int i = 0; i < count; i++) { newMark[new_path[i].idx] = newStamp; newPlace[new_path[i].idx] = i; }
            bool dropping = empty > 0;
            for (int k = 0; k < count; k++)
            {
                var starts = starting[new_path[k].idx];
                if (starts == null) continue;
                foreach (var path in starts)
                {
                    if (path.Dropped || path.Rails.Length > count) continue;
                    int end = path.Rails[path.Rails.Length - 1];
                    if (newMark[end] != newStamp) continue;
                    bool held = true;
                    for (int i = k, j = 0; i < newPlace[end]; i++, j++)
                        if (new_path[i].idx != path.Rails[j]) { held = false; break; }
                    if (held) { Drop(path); dropping = true; }
                }
            }
            if (dropping)
            {
                int to = 0;
                for (int i = 0; i < kept.Count; i++)
                {
                    var path = kept[i];
                    if (path.Dropped || path.Rails.Length == 0) continue;
                    kept[to] = path;
                    paths[to] = paths[i];
                    to++;
                }
                kept.RemoveRange(to, kept.Count - to);
                paths.RemoveRange(to, paths.Count - to);
                empty = 0;
            }
            // A rail of the new path that a kept path has and that has a single way to go is left
            // out of it.
            for (int i = count - 1; i >= 0; i--)
            {
                var rail = new_path[i];
                if (holding[rail.idx] > 0 && UnityEngine.Mathf.IsPowerOfTwo(flow.GetSOAInfo().GetPermittedFlowDirections(rail.idx)))
                    new_path.RemoveAt(i);
            }
            paths.Add(new_path);
            Add(new_path);
        }

        // ---- Verify ----

        static long pathsChecked, pathsWrong;

        static void Compare(List<List<Conduit>> paths, List<Conduit> ours, List<List<Conduit>> before, List<Conduit> theirs)
        {
            pathsChecked++;
            bool same = paths.Count == before.Count && Same(ours, theirs);
            for (int i = 0; same && i < paths.Count; i++)
                same = ReferenceEquals(before[i], theirs) ? ReferenceEquals(paths[i], ours) : ReferenceEquals(paths[i], before[i]);
            if (!same) global::Debug.Log($"{Name}: kept paths differ from the game's ({++pathsWrong})");
        }

        static bool Same(List<Conduit> a, List<Conduit> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].idx != b[i].idx) return false;
            return true;
        }

        static void Report() => global::Debug.Log($"{Name}: {pathsChecked} paths checked, {pathsWrong} wrong");

        /// The method another owner's patch makes this tweak stand down (ModOverlaps).
        internal static System.Reflection.MethodBase Guarded() => AccessTools.Method(typeof(SolidConduitFlow), "TryAdd");

        static bool Foreign()
        {
            var info = Harmony.GetPatchInfo(tryAdd);
            if (info == null) return false;
            // PerfProbe's stopwatches (test infrastructure) only time the method.
            foreach (var o in info.Owners)
                if (!ModOverlapsCore.IsTweakOwner(o) && o != "PerfProbe" && !o.StartsWith("PerfProbe.", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
