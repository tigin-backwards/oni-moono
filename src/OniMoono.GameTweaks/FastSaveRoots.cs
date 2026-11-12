using System;
using System.Collections.Generic;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-save-roots`. SaveManager keeps every saved object (SaveLoadRoot) in a
    /// list per prefab tag, and SaveManager.Unregister takes one out with List.Remove. Items
    /// unregister whenever they are stored and register again (at the list's end) whenever they
    /// leave storage, so each conveyor hand-off (a bridge, a consumer or a dispenser moving an
    /// item) appends the item and then removes it again: List.Remove searches the whole list
    /// from its start, calling UnityEngine.Object.Equals on every root of that kind, which reads
    /// each one's liveness (a cache miss for every root of the kind in the colony).
    ///
    /// For a root that is alive, Object.Equals is reference equality, so the tweak finds the
    /// same index by comparing references and removes it with RemoveAt, as List.Remove does.
    /// Everything else runs as in the game, in the game's order: the onUnregister event (behind
    /// the game's own check of onRegister), the list lookup (which makes a missing list), and
    /// for a destroyed root the game's List.Remove (whose Equals then matches the first
    /// destroyed root instead). With ONI_TWEAK_VERIFY=1 every index is checked against
    /// List.IndexOf and the counts logged every 4,096 removals.
    ///
    /// While another Harmony owner patches SaveManager.Unregister (checked at the first call and
    /// every 4,096 calls), the game's method runs.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SaveManager::Unregister(SaveLoadRoot)", "a9c1eb3a6c848563 linux-744825 win-744825", "6d8f6b37bb4200dc linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<SaveLoadRoot> SaveManager::GetSaveLoadRootList(SaveLoadRoot)")]
    [GameMember(GameUse.Uses, "System.Action`1<SaveLoadRoot> SaveManager::onRegister")]
    [GameMember(GameUse.Uses, "System.Action`1<SaveLoadRoot> SaveManager::onUnregister")]
    public static class FastSaveRoots
    {
        public const string Name = "fast-save-roots";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly AccessTools.FieldRef<SaveManager, Action<SaveLoadRoot>> onRegister = AccessTools.FieldRefAccess<SaveManager, Action<SaveLoadRoot>>("onRegister");
        static readonly AccessTools.FieldRef<SaveManager, Action<SaveLoadRoot>> onUnregister = AccessTools.FieldRefAccess<SaveManager, Action<SaveLoadRoot>>("onUnregister");
        static Func<SaveManager, SaveLoadRoot, List<SaveLoadRoot>> listOf;
        static System.Reflection.MethodBase target;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt): true runs the game's method, for an A/B in one run.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_SAVE_ROOTS_BYPASS") == "1";
        /// Counts since the start (removals by reference, and those whose index differed from List.IndexOf's).
        public static long Removals, Wrong;
        static long calls;

        public static void Install(Harmony harmony)
        {
            target = Guarded();
            listOf = AccessTools.MethodDelegate<Func<SaveManager, SaveLoadRoot, List<SaveLoadRoot>>>(
                AccessTools.Method(typeof(SaveManager), "GetSaveLoadRootList", new[] { typeof(SaveLoadRoot) }));
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FastSaveRoots), nameof(Unregister)) { priority = Priority.Last });
        }

        /// The method another owner's patch makes this tweak stand down (ModOverlaps).
        internal static System.Reflection.MethodBase Guarded() =>
            AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Unregister), new[] { typeof(SaveLoadRoot) });

        static bool Foreign()
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) return false;
            foreach (var o in info.Owners)
                if (!ModOverlapsCore.IsTweakOwner(o))
                {
                    global::Debug.Log($"{Name}: off ({o} patches SaveManager.Unregister)");
                    return true;
                }
            return false;
        }

        /// SaveManager.Unregister, with the root found by reference.
        static bool Unregister(SaveManager __instance, SaveLoadRoot root, bool __runOriginal)
        {
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if ((calls++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
                if (verify && calls > 1) global::Debug.Log($"{Name}: {Removals} removals, {Wrong} wrong");
            }
            // A destroyed root (or none): the game's method, whose Equals differs from a reference test.
            if (root == null) return true;
            if (onRegister(__instance) != null) onUnregister(__instance)(root);
            var list = listOf(__instance, root);
            if (list == null) return false;
            int index = SaveRootsCore.IndexOfSame(list, root);
            if (verify)
            {
                int theirs = list.IndexOf(root);
                if (theirs != index) global::Debug.Log($"{Name}: index {index} differs from List.IndexOf's {theirs} ({++Wrong})");
            }
            if (index >= 0)
            {
                list.RemoveAt(index);
                Removals++;
            }
            return false;
        }
    }
}
