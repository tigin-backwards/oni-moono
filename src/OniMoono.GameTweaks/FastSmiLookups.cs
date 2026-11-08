using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Linq = System.Linq.Expressions;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-smi-lookups`. Every `GetSMI<T>()` in the game (about 650 call sites:
    /// state-machine updates, workables, chores, sensors) ends in
    /// StateMachineController.GetSMI(Type), which walks the object's state machines in order and
    /// returns the first whose type is assignable to the one asked for. A Duplicant has about 50,
    /// each a separate object, so a lookup reads that many scattered objects' types; the same
    /// lookups are made every tick (a sleeping Duplicant asks for its StaminaMonitor every 33 ms).
    ///
    /// The tweak keeps, per controller, the answers to the types asked since its list of state
    /// machines last changed, and returns the kept answer while it is unchanged. The list is the
    /// controller's private List<StateMachine.Instance>; every change to it (Add, Remove, the
    /// indexer, Clear) moves the list's version, and an instance's type never changes, so the
    /// kept answer is the walk's answer. A walk that throws is never kept. Only the main thread
    /// keeps and reads answers: chore preconditions look state machines up on the job workers,
    /// where the game's walk runs as before. With ONI_TWEAK_VERIFY=1 every kept answer is
    /// checked against a walk and differences logged.
    ///
    /// While another Harmony owner patches GetSMI(Type) (checked at the first call and every 2^18
    /// lookups), the game's method runs.
    [GameMember(GameUse.Postfix, "System.Void Game::OnPrefabInit()")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "StateMachine/Instance StateMachineController::GetSMI(System.Type)", "d570e9bc4aade1e1 linux-744825 win-744825", "bac4850e3c6abb42 linux-761211 linux-761654 linux-762107 linux-762230 win-761211 win-761654 win-762107 win-762230")]
    public static class FastSmiLookups
    {
        public const string Name = "fast-smi-lookups";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly AccessTools.FieldRef<StateMachineController, List<StateMachine.Instance>> machines =
            AccessTools.FieldRefAccess<StateMachineController, List<StateMachine.Instance>>("stateMachines");
        static readonly Func<List<StateMachine.Instance>, int> version = CompileVersion();
        static System.Reflection.MethodBase target;

        /// One controller's kept answers, valid while its list is `List` at `Version`.
        sealed class Kept
        {
            public List<StateMachine.Instance> List;
            public int Version;
            public readonly Dictionary<Type, StateMachine.Instance> Answers = new Dictionary<Type, StateMachine.Instance>();
        }

        static readonly ConditionalWeakTable<StateMachineController, Kept> kept = new ConditionalWeakTable<StateMachineController, Kept>();
        static readonly ConditionalWeakTable<StateMachineController, Kept>.CreateValueCallback newKept = _ => new Kept();
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt): true runs the game's walk, for an A/B in one run.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_SMI_LOOKUPS_BYPASS") == "1";
        /// Counts since the start (lookups, kept answers returned, kept answers that differed from a walk).
        public static long Lookups, Hits, Wrong;

        public static void Install(Harmony harmony)
        {
            target = AccessTools.Method(typeof(StateMachineController), nameof(StateMachineController.GetSMI), new[] { typeof(Type) });
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FastSmiLookups), nameof(Lookup)) { priority = Priority.Last });
            MainThread.Install(harmony);
        }

        /// List<T>._version, which every change to the list moves.
        static Func<List<StateMachine.Instance>, int> CompileVersion()
        {
            var list = Linq.Expression.Parameter(typeof(List<StateMachine.Instance>));
            var field = AccessTools.Field(typeof(List<StateMachine.Instance>), "_version")
                ?? throw new MissingFieldException("List<T>._version");
            return Linq.Expression.Lambda<Func<List<StateMachine.Instance>, int>>(Linq.Expression.Field(list, field), list).Compile();
        }

        /// The method another owner's patch makes this tweak stand down (ModOverlaps).
        internal static System.Reflection.MethodBase Guarded() =>
            AccessTools.Method(typeof(StateMachineController), nameof(StateMachineController.GetSMI), new[] { typeof(Type) });

        static bool Foreign()
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) return false;
            foreach (var o in info.Owners)
                if (!ModOverlapsCore.IsTweakOwner(o))
                {
                    global::Debug.Log($"{Name}: off ({o} patches StateMachineController.GetSMI)");
                    return true;
                }
            return false;
        }

        /// StateMachineController.GetSMI(Type), from the kept answers.
        static bool Lookup(StateMachineController __instance, Type type, ref StateMachine.Instance __result, bool __runOriginal)
        {
            if (!__runOriginal || off || Bypass || !MainThread.Is) return __runOriginal;
            if ((Lookups++ & 0x3FFFF) == 0)
            {
                if (off = Foreign()) return true;
                if (verify && Lookups > 1) global::Debug.Log($"{Name}: {Lookups} lookups, {Hits} from the cache, {Wrong} wrong");
            }
            var list = machines(__instance);
            if (list == null || type == null) return true;
            var answers = kept.GetValue(__instance, newKept);
            int now = version(list);
            if (!ReferenceEquals(answers.List, list) || answers.Version != now)
            {
                answers.Answers.Clear();
                answers.List = list;
                answers.Version = now;
            }
            else if (answers.Answers.TryGetValue(type, out var answer))
            {
                Hits++;
                if (verify) Check(list, type, answer);
                __result = answer;
                return false;
            }
            // The game's walk; kept only once it has returned.
            __result = Walk(list, type);
            answers.Answers[type] = __result;
            return false;
        }

        static StateMachine.Instance Walk(List<StateMachine.Instance> list, Type type)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var instance = list[i];
                if (type.IsAssignableFrom(instance.GetType())) return instance;
            }
            return null;
        }

        static void Check(List<StateMachine.Instance> list, Type type, StateMachine.Instance answer)
        {
            if (!ReferenceEquals(Walk(list, type), answer))
                global::Debug.Log($"{Name}: a kept answer for {type.Name} differs from the walk ({++Wrong})");
        }
    }
}
