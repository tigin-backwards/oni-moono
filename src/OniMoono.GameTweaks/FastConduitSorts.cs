using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-conduit-sorts`. The gas, liquid and rail flows' Sim200ms sort their list of
    /// updaters (every pipe and rail building's once-a-second update, 2,857 liquid ones on a
    /// late-game colony) by priority whenever dirtyConduitUpdaters is set, which adding or removing
    /// an updater sets and nothing clears: so every game second, three introsorts of lists that
    /// are already sorted. The sort is not stable, and it does reorder updaters of equal priority
    /// each time, so it cannot be left out.
    ///
    /// The sort's moves depend only on what the comparison returns, which reads only each
    /// updater's priority: the same priorities in list order give the same permutation. Each
    /// flow's `conduitUpdaters.Sort(comparison)` becomes a call that keeps the priorities and the
    /// permutation of its last sort and applies the permutation again while the priorities are
    /// the same (SortReplay). Other priorities, or a comparison that is not the game's, sort as
    /// the game does.
    ///
    /// With ONI_TWEAK_VERIFY=1 the game's sort runs on a copy of the list each time and any
    /// difference is logged. Off for the session while another mod patches a flow's Sim200ms or
    /// while Fast Track, MoreTek, Colony Performance Fixes or Multiplayer Mod is active (checked
    /// at the first sort and every 256).
    [GameMember(GameUse.Transpiler, "System.Void ConduitFlow::Sim200ms(System.Single)", "b517f5dd283fca91 linux-744825 win-744825", "29247c95bdb99fcf linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023", Rewrites = 1)]
    [GameMember(GameUse.Transpiler, "System.Void SolidConduitFlow::Sim200ms(System.Single)", "539f9ff0e4a120a4 linux-744825 win-744825", "0049abbfd5ce9c0f linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023", Rewrites = 1)]
    public static class FastConduitSorts
    {
        public const string Name = "fast-conduit-sorts";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe's ab.txt): true sorts as the game does, for an A/B in one run.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_CONDUIT_SORTS_BYPASS") == "1";

        static readonly string[] standDownMods = { "PeterHan.FastTrack", "MoreTek", "SaitoColonyPerf", "multiplayerMod" };
        static bool off;
        static long calls;
        public static long Wrong;

        /// One replay for each flow's list.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SortReplay<ConduitFlow.ConduitUpdater>> pipes = new();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SortReplay<SolidConduitFlow.ConduitUpdater>> rails = new();
        static int[] keys = Array.Empty<int>();

        public static void Install(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(FastConduitSorts), nameof(Transpiler));
            harmony.Patch(AccessTools.Method(typeof(ConduitFlow), nameof(ConduitFlow.Sim200ms)), transpiler: transpiler);
            harmony.Patch(AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.Sim200ms)), transpiler: transpiler);
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps).
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(ConduitFlow), nameof(ConduitFlow.Sim200ms)),
            AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.Sim200ms)),
        };

        /// `conduitUpdaters.Sort(comparison)` becomes Sort(conduitUpdaters, comparison).
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var pipeSort = AccessTools.Method(typeof(List<ConduitFlow.ConduitUpdater>), nameof(List<ConduitFlow.ConduitUpdater>.Sort), new[] { typeof(Comparison<ConduitFlow.ConduitUpdater>) });
            var railSort = AccessTools.Method(typeof(List<SolidConduitFlow.ConduitUpdater>), nameof(List<SolidConduitFlow.ConduitUpdater>.Sort), new[] { typeof(Comparison<SolidConduitFlow.ConduitUpdater>) });
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(pipeSort)) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(FastConduitSorts), nameof(SortPipes)); n++; }
                else if (ci.Calls(railSort)) { ci.opcode = OpCodes.Call; ci.operand = AccessTools.Method(typeof(FastConduitSorts), nameof(SortRails)); n++; }
                yield return ci;
            }
            if (n != 1) throw new InvalidOperationException($"{Name}: {original.DeclaringType?.Name}.Sim200ms is not as expected ({n} sorts)");
            TranspilerCounts.Report(Name, original, n);
        }

        static bool Foreign()
        {
            if (ModStandDown.Active(standDownMods) is { } mod)
            {
                global::Debug.Log($"{Name}: off ({mod} is active)");
                return true;
            }
            foreach (var method in Guarded())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var o in info.Owners)
                    if (!ModOverlapsCore.IsTweakOwner(o) && !ModOverlapsCore.IsOurs(o))
                    {
                        global::Debug.Log($"{Name}: off ({o} patches {method.DeclaringType?.Name}.Sim200ms)");
                        return true;
                    }
            }
            return false;
        }

        /// Whether to replay: on, not stood down, and `compare` the game's own lambda of `flow`.
        static bool Replays(Delegate compare, Type flow)
        {
            if (off || Bypass) return false;
            if ((calls++ & 0xFF) == 0)
            {
                if (off = Foreign()) return false;
                if (verify && calls > 1) global::Debug.Log($"{Name}: {calls} sorts, {Wrong} wrong");
            }
            var m = compare.Method;
            return m.DeclaringType?.DeclaringType == flow && m.Name.Contains("<Sim200ms>");
        }

        static int[] Keys(int n)
        {
            if (keys.Length < n) keys = new int[Math.Max(n, keys.Length * 2)];
            return keys;
        }

        static void SortPipes(List<ConduitFlow.ConduitUpdater> list, Comparison<ConduitFlow.ConduitUpdater> compare)
        {
            if (!Replays(compare, typeof(ConduitFlow))) { list.Sort(compare); return; }
            var k = Keys(list.Count);
            for (int i = 0; i < list.Count; i++) k[i] = (int)list[i].priority;
            List<ConduitFlow.ConduitUpdater> theirs = null;
            if (verify) { theirs = new List<ConduitFlow.ConduitUpdater>(list); theirs.Sort(compare); }
            pipes.GetOrCreateValue(list).Sort(list, k, compare);
            if (verify)
                for (int i = 0; i < list.Count; i++)
                    if (!ReferenceEquals(list[i].callback, theirs[i].callback)) { Mismatch(list.Count, i); list.Clear(); list.AddRange(theirs); break; }
        }

        static void SortRails(List<SolidConduitFlow.ConduitUpdater> list, Comparison<SolidConduitFlow.ConduitUpdater> compare)
        {
            if (!Replays(compare, typeof(SolidConduitFlow))) { list.Sort(compare); return; }
            var k = Keys(list.Count);
            for (int i = 0; i < list.Count; i++) k[i] = (int)list[i].priority;
            List<SolidConduitFlow.ConduitUpdater> theirs = null;
            if (verify) { theirs = new List<SolidConduitFlow.ConduitUpdater>(list); theirs.Sort(compare); }
            rails.GetOrCreateValue(list).Sort(list, k, compare);
            if (verify)
                for (int i = 0; i < list.Count; i++)
                    if (!ReferenceEquals(list[i].callback, theirs[i].callback)) { Mismatch(list.Count, i); list.Clear(); list.AddRange(theirs); break; }
        }

        static void Mismatch(int count, int at)
        {
            global::Debug.Log($"{Name}: a sort of {count} updaters differs from the game's at {at} ({++Wrong})");
            off = true;
        }
    }
}
