using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `off-world-fallers` (a fix of the game's bug; out of `exact`).
    /// GravityComponents.FixedUpdate steps every falling object thirty times a game second. An
    /// object falling through a hole in the bottom of a world (a chute out of a rocket interior) is
    /// never moved again: each step's next position would leave its world, so the step leaves it
    /// half a cell below the grid's bottom row, still falling. It is stepped forever: a pile-heavy
    /// colony has 17,552 such objects (diamonds and egg shells below two rocket interiors), about
    /// 8.5 ms a frame.
    ///
    /// Such an object is landed where it is, through the game's landing for items
    /// (FallerComponents.OnLanded), once a step has left it in place and no later step could move
    /// it: it falls straight down, the cell under it is off the grid (no liquid can slow it), and
    /// at half its speed the next position and the cell under it would still be below the grid's
    /// bottom row (later steps are no slower; a step's time is fixed for its update rate). Only
    /// items are landed: objects whose landing is the item one.
    ///
    /// The game's step is not patched: a Harmony-patched copy of that loop runs about half as slow
    /// again for every falling object. The tweak runs around the step, from KComponentSpawn.Sim33ms
    /// (whose KComponents.Sim33ms calls every component manager's FixedUpdate; the gravity step is
    /// the only one). Before the step, the falling items near the grid's bottom row are noted with
    /// their positions (FallerCandidates: every falling item is looked at about twice a game
    /// second, those that just started falling at each step). After it, the noted ones the step
    /// reached and did not move are tested (OffWorldFallersCore.Lands) and landed in the step's
    /// order. An object that starts falling during a step lands one step late; one that another
    /// system moves to where it hangs, up to half a game second late.
    ///
    /// What differs: the object stops counting as falling (GameComps.Gravities.Has) and no Landed
    /// event is sent (an item's handler would only play a sound, or delete one landing outside the
    /// grid). It stays where it was. If its rocket interior is destroyed and its items move to the
    /// world below, it falls from there at rest. Gravity is not saved: after a load the object
    /// falls again until its first step lands it.
    ///
    /// Off for the session once another mod patches the step with anything but a postfix (checked
    /// at the first step, then every 256). ONI_TWEAK_VERIFY=1 also installs a transpiler of the
    /// step that notes what the step's test leaves in place: each step's landings are compared with
    /// its list (the same objects in the same order), and each landed object is checked for having
    /// stopped falling where it was.
    [GameMember(GameUse.Uses, "System.Action`1<UnityEngine.Transform> FallerComponents::OnLandedAction")]
    [GameMember(GameUse.Prefix | GameUse.Postfix | GameUse.Mirrors, "System.Void KComponentSpawn::Sim33ms(System.Single)", "74ea4782a52fac80 linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025 linux-764961 win-764961", "ec3fbbeed101a661 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    [GameMember(GameUse.Mirrors, "System.Void KComponents::Sim33ms(System.Single)", "c9d0da7c6bd7583c linux-744825 win-744825 linux-763796 win-763796 linux-764025 win-764025 linux-764961 win-764961", "c1a5748c67d71b42 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612")]
    [GameMember(GameUse.Mirrors, "System.Void GravityComponents::FixedUpdate(System.Single)", "740814f5168a2a2f linux-744825 win-744825", "8ae11e603474819f linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", "bb91ca62f15446b7 linux-763796 win-763796 linux-764025 win-764025 linux-764961 win-764961")]
    // Verify mode's transpiler (Transpiled).
    [GameMember(GameUse.Transpiler, "System.Void GravityComponents::FixedUpdate(System.Single)", "740814f5168a2a2f linux-744825 win-744825", "8ae11e603474819f linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 linux-763325 linux-763612 win-761211 win-761654 win-762107 win-762230 win-763023 win-763325 win-763612", "bb91ca62f15446b7 linux-763796 win-763796 linux-764025 win-764025 linux-764961 win-764961", Rewrites = 1, Conditional = true)]
    [GameMember(GameUse.Uses, "System.Boolean Grid::IsValidCellInWorld(System.Int32,System.Int32)")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector3 TransformExtensions::GetPosition(UnityEngine.Transform)")]
    [GameMember(GameUse.Uses, "System.Single GravityComponent::GetGroundOffset(GravityComponent)")]
    [GameMember(GameUse.Uses, "System.Boolean GravityComponent::mayLeaveWorld")]
    public static class OffWorldFallers
    {
        public const string Name = "off-world-fallers";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe): true leaves both passes out, for an A/B in one run.
        public static bool Bypass;

        static readonly FallerCandidates<Transform> candidates = new FallerCandidates<Transform>();
        static readonly List<(int Index, Transform Key)> landing = new List<(int, Transform)>();
        static Action<Transform> itemLanding;
        static GravityComponents watching;
        static MethodBase[] guarded;
        static bool off, loggedOnce;
        static long steps;

        /// For tests (E2eDriver's state reads) and the verify log: the objects landed, the steps
        /// compared with the transpiler's list and the differences found.
        public static long Landed, Compared, Differed;

        public static void Install(Harmony harmony)
        {
            itemLanding = FallerComponents.OnLandedAction;
            guarded = new List<MethodBase>(Guarded()).ToArray();
            harmony.Patch(Hook(), prefix: new HarmonyMethod(typeof(OffWorldFallers), nameof(Before)),
                postfix: new HarmonyMethod(typeof(OffWorldFallers), nameof(After)));
            if (verify) Transpiled.Install(harmony);
        }

        /// The scheduler's call of every component manager's FixedUpdate: the gravity step.
        [MirrorsGame("System.Void KComponentSpawn::Sim33ms(System.Single)")]
        [MirrorsGame("System.Void KComponents::Sim33ms(System.Single)")]
        static MethodBase Hook() => AccessTools.Method(typeof(KComponentSpawn), nameof(KComponentSpawn.Sim33ms));

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps): the step
        /// whose tests it mirrors.
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(GravityComponents), nameof(GravityComponents.FixedUpdate)),
        };

        /// Which patches of the step count: a postfix runs after the game's step as before.
        internal static ProbeRepairPolicy.GuardedKinds KindsOf(MethodBase m) => ProbeRepairPolicy.GuardedKinds.AllButPostfixes;

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded, ProbeRepairPolicy.GuardedKinds.AllButPostfixes) is not { } why) return false;
            global::Debug.Log($"[engine] {Name}: off ({why})");
            return true;
        }

        /// GameComps.Gravities and Grid, as the passes read them.
        struct Fallers : IFallerWorld<Transform>
        {
            public GravityComponents Gravities;
            public List<GravityComponent> Data;

            public int PosToCell(float x, float y) => Grid.PosToCell(new Vector2(x, y));
            public bool IsValidCell(int cell) => Grid.IsValidCell(cell);
            public byte World(int cell) => Grid.WorldIdx[cell];
            public int Count => Data.Count;
            public float Elapsed(int i) => Data[i].elapsedTime;
            public float MaxVelocity => TuningData<GravityComponents.Tuning>.Get().maxVelocity;

            public bool Item(int i, out Transform key, out Faller faller)
            {
                var component = Data[i];
                key = component.transform;
                faller = default;
                if (component.onLanded != itemLanding || component.mayLeaveWorld || key == null) return false;
                faller = Read(component, i);
                return true;
            }

            public bool Find(Transform key, bool falling, out Faller faller)
            {
                faller = default;
                if (key == null) return false;
                var go = key.gameObject;
                var handle = Gravities.GetHandle(go);
                if (!handle.IsValid() || (falling && !Gravities.Has(go))) return false;
                int index = Gravities.ComputeIndex(handle);
                faller = Read(Data[index], index);
                return true;
            }

            static Faller Read(GravityComponent component, int index)
            {
                var position = component.transform.GetPosition();
                return new Faller
                {
                    X = position.x, Y = position.y, VelocityX = component.velocity.x, VelocityY = component.velocity.y, Elapsed = component.elapsedTime,
                    GroundOffset = GravityComponent.GetGroundOffset(component), MayLeaveWorld = component.mayLeaveWorld,
                    ItemLanding = component.onLanded == itemLanding, Index = index,
                };
            }
        }

        static Fallers Watching() => new Fallers { Gravities = watching, Data = watching.GetDataList() };

        /// Before the step: the objects to test after it, as they are now.
        static void Before(float dt)
        {
            if (off || Bypass) return;
            if ((steps++ & 0xFF) == 0 && (off = Foreign()))
            {
                candidates.Clear();
                return;
            }
            var gravities = GameComps.Gravities;
            if (!ReferenceEquals(gravities, watching))
            {
                watching = gravities;
                candidates.Clear();
            }
            if (gravities == null) return;
            var world = Watching();
            OffWorldFallersCore.Before(candidates, ref world, dt);
        }

        /// After the step: the watched objects it left in place for good land, in its order.
        static void After(float dt)
        {
            if (off || Bypass || watching == null || !ReferenceEquals(GameComps.Gravities, watching))
            {
                Transpiled.Landing.Clear();
                return;
            }
            var world = Watching();
            OffWorldFallersCore.After(candidates, ref world, dt, landing);
            if (verify && Transpiled.Applied) Compare();
            if (landing.Count == 0) return;
            int count = 0;
            try
            {
                foreach (var (_, transform) in landing)
                {
                    if (transform == null || !watching.Has(transform.gameObject)) continue;
                    var was = verify ? transform.GetPosition() : default;
                    itemLanding(transform);
                    count++;
                    if (verify && (watching.Has(transform.gameObject) || transform.GetPosition() != was))
                    {
                        Differed++;
                        global::Debug.Log($"[engine] {Name}: {transform.name} still falls or moved when landed ({Differed})");
                    }
                }
            }
            finally
            {
                landing.Clear();
            }
            Landed += count;
            // Logged for the first landing and for large batches (a save's objects, after a load).
            if (count > 0 && (!loggedOnce || count >= 100)) Debug.Log($"[engine] {Name}: {count} objects landed below their worlds");
            loggedOnce |= count > 0;
        }

        /// ONI_TWEAK_VERIFY: this step's landings against the transpiler's list for it, which is
        /// what the game's test left in place for good (the objects still falling, as its
        /// postfix landed them).
        static void Compare()
        {
            var theirs = Transpiled.Landing;
            theirs.RemoveAll(t => t == null || !GameComps.Gravities.Has(t.gameObject));
            Compared++;
            bool same = theirs.Count == landing.Count;
            for (int i = 0; same && i < theirs.Count; i++) same = ReferenceEquals(theirs[i], landing[i].Key);
            if (!same)
            {
                Differed++;
                string first = "";
                for (int i = 0; i < Math.Max(theirs.Count, landing.Count); i++)
                    if (i >= theirs.Count || i >= landing.Count || !ReferenceEquals(theirs[i], landing[i].Key))
                    {
                        var t = i < theirs.Count ? theirs[i] : null;
                        var l = i < landing.Count ? landing[i].Key : null;
                        first = $", first at {i}: the transpiler's {(t != null ? t.name + " " + t.GetPosition() : "none")}, this one's {(l != null ? l.name + " " + l.GetPosition() : "none")}";
                        break;
                    }
                global::Debug.Log($"[engine] {Name}: step {Compared} differs from the transpiler's: it lands {landing.Count} objects, the transpiler {theirs.Count}{first}");
            }
            if (theirs.Count > 0 || (Compared & 0x3FF) == 0)
                global::Debug.Log($"[engine] {Name}: {Compared} steps compared with the transpiler, {Landed + landing.Count} objects landed, {Differed} differed");
            theirs.Clear();
        }

        /// Verify mode only: a transpiler of the step that notes what the step's test leaves in
        /// place for good (Landing), for Compare.
        static class Transpiled
        {
            internal static readonly List<Transform> Landing = new List<Transform>();
            /// Whether the transpiler found the step's test: without it there is nothing to compare.
            internal static bool Applied;

            public static void Install(Harmony harmony)
            {
                var fixedUpdate = AccessTools.Method(typeof(GravityComponents), nameof(GravityComponents.FixedUpdate));
                harmony.Patch(fixedUpdate, transpiler: new HarmonyMethod(typeof(Transpiled), nameof(Transpiler)));
            }

            /// The step's last test, `Grid.IsValidCellInWorld(next, world)` (false: the object is left
            /// in place), becomes `InWorld(next, world, component, position, groundOffset, dt)`.
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                var list = new List<CodeInstruction>(instructions);
                var inWorld = AccessTools.Method(typeof(Grid), nameof(Grid.IsValidCellInWorld));
                var getPosition = AccessTools.Method(typeof(TransformExtensions), nameof(TransformExtensions.GetPosition), [typeof(Transform)]);
                var groundOffset = AccessTools.Method(typeof(GravityComponent), nameof(GravityComponent.GetGroundOffset), [typeof(GravityComponent)]);
                var mayLeaveWorld = AccessTools.Field(typeof(GravityComponent), nameof(GravityComponent.mayLeaveWorld));
                int calls = list.FindAll(i => i.Calls(inWorld)).Count;
                int at = list.FindIndex(i => i.Calls(inWorld));
                int position = list.FindIndex(i => i.Calls(getPosition));
                int offset = list.FindIndex(i => i.Calls(groundOffset));
                int component = list.FindIndex(i => i.LoadsField(mayLeaveWorld));
                var loadPosition = position < 0 ? null : LoadOf(list[position + 1]);
                var loadOffset = offset < 0 ? null : LoadOf(list[offset + 1]);
                // Never thrown: Harmony runs this again inside a mod's later Patch of the step.
                Applied = calls == 1 && component >= 1 && loadPosition != null && loadOffset != null;
                OniMoono.Tweaks.TranspilerCounts.Report(Name, original, Applied ? 1 : 0);
                if (!Applied) return list;
                list[at].operand = AccessTools.Method(typeof(Transpiled), nameof(InWorld));
                list.InsertRange(at, new[]
                {
                    list[component - 1].Clone(),
                    loadPosition,
                    loadOffset,
                    new CodeInstruction(OpCodes.Ldarg_1),
                });
                return list;
            }

            /// The load of the local that `store` stores to, or null when it stores to none.
            static CodeInstruction LoadOf(CodeInstruction store)
            {
                if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
                if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
                if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
                if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
                if (store.opcode == OpCodes.Stloc_S || store.opcode == OpCodes.Stloc) return new CodeInstruction(OpCodes.Ldloc, store.operand);
                return null;
            }

            static bool InWorld(int cell, int world, GravityComponent component, Vector3 position, float groundOffset, float dt)
            {
                if (Grid.IsValidCellInWorld(cell, world)) return true;
                if (component.onLanded == itemLanding && component.velocity.x == 0f && component.velocity.y < 0f
                    && !Grid.IsValidCell(Grid.PosToCell((Vector2)position - new Vector2(0f, groundOffset))))
                {
                    var slowest = (Vector2)position + new Vector2(0f, component.velocity.y * 0.5f) * dt;
                    if (Grid.PosToCell(slowest) < 0 && Grid.PosToCell(slowest - new Vector2(0f, groundOffset)) < 0) Landing.Add(component.transform);
                }
                return false;
            }
        }
    }
}
