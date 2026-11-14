using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-overlay-removals`. While the Decor overlay is open it keeps every object
    /// with a DecorProvider (buildings, plants, critters and every loose item) in a UniformGrid,
    /// and listens for saved objects leaving the save list (SaveManager.onUnregister: an item
    /// stored, absorbed into a pile, eaten or destroyed). OverlayModes.Decor.OnSaveLoadRootUnregistered
    /// then calls UniformGrid.Remove, which for each grid cell the object covers (four for a
    /// point) takes it out of the cell's list and out of the grid's list of all entries with
    /// List.Remove: a search of the whole list from its start, calling UnityEngine.Object.Equals
    /// on every entry. On a late-game colony that list has several hundred thousand entries, so
    /// each object that leaves costs milliseconds, and a frame in which many leave is a hitch.
    ///
    /// The tweak runs the same handler with the grid's removal done by reference
    /// (GridRemovalCore): for a provider that is alive, Object.Equals is reference equality, so
    /// the same entries are found and removed, in the same order, and the lists come out the
    /// same. A destroyed root, or one without a live provider, leaves the grid alone as in the
    /// game. With ONI_TWEAK_VERIFY=1 every removal is checked against
    /// the game's walk on copies of the lists, and the counts are logged every 256 removals.
    ///
    /// While another Harmony owner patches the handler or UniformGrid&lt;DecorProvider&gt;.Remove
    /// (checked at the first call and every 4,096 calls), the game's method runs.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void OverlayModes/Decor::OnSaveLoadRootUnregistered(SaveLoadRoot)", "4f8aa9bbabcfb043 linux-744825 win-744825", "3e7c816f6220e013 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void UniformGrid`1::Remove(!0)", "81a124a416e5a0f4 linux-744825 win-744825", "30dd203307616f25 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    public static class FastOverlayRemovals
    {
        public const string Name = "fast-overlay-removals";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe's brackets): true runs the game's method, for an A/B in one run.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_OVERLAY_REMOVALS_BYPASS") == "1";

        /// Counts since the start: providers removed by reference, grid entries taken out, and
        /// indices that differed from List.IndexOf's (verify mode).
        public static long Removals, Entries, Wrong;
        static long calls;
        static bool off, bypassLogged;
        static MethodBase[] guarded;

        public static void Install(Harmony harmony)
        {
            guarded = new List<MethodBase>(Guarded()).ToArray();
            harmony.Patch(guarded[0], prefix: new HarmonyMethod(typeof(FastOverlayRemovals), nameof(Unregistered)) { priority = Priority.Last });
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps): the
        /// handler first.
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(OverlayModes.Decor), "OnSaveLoadRootUnregistered", new[] { typeof(SaveLoadRoot) }),
            AccessTools.Method(typeof(UniformGrid<DecorProvider>), nameof(UniformGrid<DecorProvider>.Remove), new[] { typeof(DecorProvider) }),
        };

        static bool Foreign()
        {
            foreach (var method in guarded)
            {
                if (method == null) continue;
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var o in info.Owners)
                    if (!ModOverlapsCore.IsTweakOwner(o))
                    {
                        global::Debug.Log($"{Name}: off ({o} patches {method.DeclaringType.Name}.{method.Name})");
                        return true;
                    }
            }
            return false;
        }

        /// OverlayModes.Decor.OnSaveLoadRootUnregistered, with the grid's removal by reference.
        static bool Unregistered(OverlayModes.Decor __instance, SaveLoadRoot item, bool __runOriginal)
        {
            if (!__runOriginal || off) return __runOriginal;
            if (Bypass)
            {
                if (!bypassLogged) global::Debug.Log($"{Name}: bypassed, the game's method runs");
                bypassLogged = true;
                return true;
            }
            if ((calls++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
            }
            if (item == null || item.gameObject == null) return false;
            DecorProvider component = item.GetComponent<DecorProvider>();
            if (component != null)
            {
                if (__instance.layerTargets.Contains(component)) __instance.layerTargets.Remove(component);
                Remove(__instance.partition, component);
            }
            return false;
        }

        /// UniformGrid.Remove for an item that is alive.
        [MirrorsGame("System.Void UniformGrid`1::Remove(!0)")]
        static void Remove(UniformGrid<DecorProvider> grid, DecorProvider item)
        {
            Vector2 vector = item.PosMin();
            Vector2 vector2 = item.PosMax();
            int cellWidth = grid.cellWidth, cellHeight = grid.cellHeight, numXCells = grid.numXCells, numYCells = grid.numYCells;
            int num = (int)Math.Max(vector.x / (float)cellWidth, 0f);
            int num2 = (int)Math.Max(vector2.y / (float)cellHeight, 0f);
            int num3 = Math.Min(numXCells - 1, (int)Math.Ceiling(vector2.x / (float)cellWidth));
            int num4 = Math.Min(numYCells - 1, (int)Math.Ceiling(vector2.y / (float)cellHeight));
            if (verify) Check(grid, item, num, num2, num3, num4);
            Entries += GridRemovalCore.Remove(grid.cells, grid.items, numXCells, num, num2, num3, num4, item);
            // The overlay is open for a short while at a time: the first removal and every 256th report.
            if ((Removals++ & 0xFF) == 0 && verify) global::Debug.Log($"{Name}: {Removals} removals, {Entries} entries, {Wrong} wrong");
        }

        /// The game's walk on copies of the lists, against the tweak's on other copies.
        static void Check(UniformGrid<DecorProvider> grid, DecorProvider item, int x0, int y0, int x1, int y1)
        {
            var cells = grid.cells;
            if (cells == null) return;
            int numXCells = grid.numXCells;
            var theirItems = new List<DecorProvider>(grid.items);
            var ourCells = new List<DecorProvider>[cells.Length];
            var theirCells = new List<List<DecorProvider>>();
            for (int i = y0; i <= y1; i++)
                for (int j = x0; j <= x1; j++)
                {
                    var list = cells[i * numXCells + j];
                    if (list == null) continue;
                    var theirs = new List<DecorProvider>(list);
                    ourCells[i * numXCells + j] = new List<DecorProvider>(list);
                    if (theirs.IndexOf(item) != -1)
                    {
                        theirs.Remove(item);
                        theirItems.Remove(item);
                    }
                    theirCells.Add(theirs);
                }
            var ourItems = new List<DecorProvider>(grid.items);
            GridRemovalCore.Remove(ourCells, ourItems, numXCells, x0, y0, x1, y1, item);
            bool same = Same(theirItems, ourItems);
            int k = 0;
            for (int i = y0; same && i <= y1; i++)
                for (int j = x0; same && j <= x1; j++)
                    if (ourCells[i * numXCells + j] != null) same = Same(theirCells[k++], ourCells[i * numXCells + j]);
            if (!same) global::Debug.Log($"{Name}: a removal differs from UniformGrid.Remove's ({++Wrong})");
        }

        static bool Same(List<DecorProvider> a, List<DecorProvider> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }
    }
}
