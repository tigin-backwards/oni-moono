using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace ReachStressTestMod
{
    /// Makes and unmakes the Duplicants' reach over and over, for the instant-reachability game
    /// tweak's verify mode (tests/e2e/instant-reachability-stress.yaml). Next to the early-base
    /// fixture's base (dug at y 207..209 up to x 135) it walls in a test area of granite and digs
    /// rooms in it:
    ///
    ///   C (x 137..142, y 211..213) above A, reached up a ladder in a shaft at x 139
    ///   A (x 137..146, y 207..209) behind a door at x 136
    ///   B (x 148..156) behind wall W at x 147 (y 207..208): a storage bin, a move marker, dig,
    ///     mop and construction orders, a Hatch
    ///   D (x 160..168), sealed: an auto-sweeper whose range is all of D, powered by this mod's
    ///     patch of EnergyConsumer.SetConnectionStatus
    ///
    /// with thousands of debris items across the rooms. Then, in phases of 20 game seconds each,
    /// one kind of change at a time, the rest at a baseline (door open, W open, ladder up,
    /// sweeper built): the door locked and opened and closed to all Duplicants by their access
    /// permissions; W filled and dug; the ladder (or a fire pole) built and removed; debris
    /// teleported in and out of a locked A (SetPosition, as the debug teleport does); the
    /// construction order, the move marker and the bins moved by raw transform sets; debris
    /// dropped from C down the shaft;
    /// a Duplicant spawned in C, the only one who reaches it, then deleted (and a bionic one in
    /// the base, where the DLC is on); the table's cells occupied and vacated within one frame;
    /// the sweeper built and removed; copper ore on C's floor, C cut off, whose work offsets this
    /// mod's postfix on OffsetTableTracker's private UpdateOffsets stretches to B's floor, outside
    /// its table's rows (as a mod could), with W filled and dug. Doors change state at once (as in
    /// instant build mode).
    /// Duplicants who wandered into an area are moved out before its way in closes. Each phase logs what it did and what the tweak counted
    /// while it ran (crossed cells, notified objects, moves, rechecks: the tweak's statics, read
    /// by name), and the static totals here are what the spec checks.
    ///
    /// The cost of reachability is timed in every run, with or without the tweak: the game's
    /// reachability polling (its per-object update), the prober table's writers on the main thread,
    /// and the tweak's drain. The timing patches use a "PerfProbe." Harmony ID, which the tweaks
    /// ignore as the perf tool's stopwatches.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(Mod), nameof(GameSpawned)));
            harmony.Patch(AccessTools.Method(typeof(EnergyConsumer), nameof(EnergyConsumer.SetConnectionStatus)), prefix: new HarmonyMethod(typeof(Mod), nameof(Powered)));
            harmony.Patch(AccessTools.Method(typeof(OffsetTableTracker), "UpdateOffsets", new[] { typeof(int), typeof(CellOffset[][]) }), postfix: new HarmonyMethod(typeof(Mod), nameof(Stretch)));
            Timing.Install(new Harmony("PerfProbe.ReachStress"));
            global::Debug.Log("[ReachStressTestMod] loaded");
        }

        static void GameSpawned(Game __instance) => __instance.gameObject.AddComponent<Stress>();

        static readonly AccessTools.FieldRef<OffsetTracker, CellOffset[]> trackerOffsets = AccessTools.FieldRefAccess<OffsetTracker, CellOffset[]>("offsets");
        static readonly AccessTools.FieldRef<OffsetTableTracker, KMonoBehaviour> tableOwner = AccessTools.FieldRefAccess<OffsetTableTracker, KMonoBehaviour>("cmp");

        /// Copper ore at Stress.WideCell also works from B's floor: an offset outside its table's.
        static void Stretch(OffsetTableTracker __instance, int cell)
        {
            if (cell != Stress.WideCell) return;
            var owner = tableOwner(__instance);
            if (owner == null || owner.GetComponent<PrimaryElement>()?.ElementID != SimHashes.Copper) return;
            ref var offsets = ref trackerOffsets(__instance);
            Array.Resize(ref offsets, offsets.Length + 1);
            offsets[offsets.Length - 1] = Stress.WideOffset;
        }

        /// The test's sweeper is always powered.
        static void Powered(EnergyConsumer __instance, ref CircuitManager.ConnectionStatus connection_status)
        {
            if (__instance.gameObject == Stress.Arm) connection_status = CircuitManager.ConnectionStatus.Powered;
        }
    }

    static class Timing
    {
        public static long BucketTicks, WriterTicks, WorkerWriterTicks, BucketUpdates;
        static int mainThread;

        public static void Install(Harmony harmony)
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            // The monitor's per-object update (not its bucket's: UpdateBucketWithUpdater's code is
            // shared by every reference-type instantiation, and a patch of it would run for all).
            var poll = AccessTools.Method(AccessTools.Inner(typeof(ReachabilityMonitor), "UpdateReachabilityCB"), "Update");
            harmony.Patch(poll, prefix: new HarmonyMethod(typeof(Timing), nameof(BucketPre)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Timing), nameof(BucketPost)) { priority = Priority.Last });
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(MinionGroupProber)))
                if (m.Name is "Occupy" or "OccupyST" or "Vacate" or "VacateST")
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(Timing), nameof(WriterPre)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(Timing), nameof(WriterPost)) { priority = Priority.Last });
        }

        static void BucketPre(out long __state) => __state = Stopwatch.GetTimestamp();

        static void BucketPost(long __state)
        {
            BucketTicks += Stopwatch.GetTimestamp() - __state;
            BucketUpdates++;
        }

        static void WriterPre(out long __state) => __state = Stopwatch.GetTimestamp();

        static void WriterPost(long __state)
        {
            long t = Stopwatch.GetTimestamp() - __state;
            if (Thread.CurrentThread.ManagedThreadId == mainThread) WriterTicks += t;
            else Interlocked.Add(ref WorkerWriterTicks, t);
        }
    }

    public sealed class Stress : MonoBehaviour
    {
        // The area (early-base's coordinates).
        const int Y = 207, DoorX = 136, WallX = 147, ShaftX = 139;
        const float PhaseSeconds = 20f;
        /// The stretched copper's cell (C's floor) and its offset to B's floor.
        internal static int WideCell = -1;
        internal static readonly CellOffset WideOffset = new CellOffset(152 - 142, Y - (Y + 4));
        /// ONI_REACH_STRESS_PHASES (comma-separated) runs only those, for a quicker look at one.
        static readonly string[] Phases = (Environment.GetEnvironmentVariable("ONI_REACH_STRESS_PHASES") ?? "offsets,door,wall,ladder,teleport,raw,falling,dupes,flip,sweeper")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        // Totals per phase, for the spec (`type:ReachStressTestMod.Stress::DoorCrossings`).
        public static long DoorCrossings, WallCrossings, LadderCrossings, DupesCrossings, FlipCrossings, SweeperCrossings, FallingCrossings, OffsetsCrossings;
        public static long TeleportMoves, RawMoves, RawTransformNotices, FallingMoves;
        public static long Actions, PhasesRun, Frames, Evictions;
        public static bool Ready, Failed;
        public static double BucketMs => Timing.BucketTicks * 1000.0 / Stopwatch.Frequency;
        public static double WriterMs => Timing.WriterTicks * 1000.0 / Stopwatch.Frequency;
        public static double DrainMs => Tweak("DrainMs") is double d ? d : 0;

        static PropertyInfo[] tweakProps;
        static object Tweak(string name)
        {
            if (tweakProps == null)
            {
                var type = Type.GetType("OniMoono.GameTweaks.InstantReachability, OniMoono.GameTweaks", false);
                tweakProps = type?.GetProperties(BindingFlags.Public | BindingFlags.Static) ?? new PropertyInfo[0];
            }
            foreach (var p in tweakProps) if (p.Name == name) return p.GetValue(null);
            return null;
        }
        static long Count(string name) => Tweak(name) is long l ? l : 0;

        float start = -1;
        int stage, phase = -1, step;
        float nextStep;
        long[] at = new long[5];
        long phaseActions;
        GameObject door, locker, cLocker, placer, blueprint, cDupe, bionic;
        internal static GameObject Arm;
        readonly List<GameObject> ladder = new List<GameObject>();
        readonly List<Pickupable> aDebris = new List<Pickupable>();
        readonly List<GameObject> dropped = new List<GameObject>();
        readonly List<int> flipCells = new List<int>();
        bool usePole;
        long frame0Bucket, frame0Writer; double frame0Drain; long frames0;

        static int Cell(int x, int y) => Grid.XYToCell(x, y);
        static Vector3 Pos(int x, int y) => Grid.CellToPosCBC(Cell(x, y), Grid.SceneLayer.Ore);

        static void Solid(int x, int y) => SimMessages.ReplaceElement(Cell(x, y), SimHashes.Granite, CellEventLogger.Instance.DebugTool, 1000f, 300f);
        static void Open(int x, int y) => SimMessages.ReplaceElement(Cell(x, y), SimHashes.Oxygen, CellEventLogger.Instance.DebugTool, 1f, 300f);

        static void Rect(int x0, int y0, int x1, int y1, Action<int, int> f)
        {
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) f(x, y);
        }

        static GameObject Build(string id, int x, int y)
        {
            var def = Assets.GetBuildingDef(id);
            var go = def.Build(Cell(x, y), Orientation.Neutral, null, def.DefaultElements(), 300f, playsound: false);
            Actions++;
            return go;
        }

        static Pickupable Debris(SimHashes element, Vector3 pos, float mass = 5f)
        {
            var go = ElementLoader.FindElementByHash(element).substance.SpawnResource(pos, mass, 300f, byte.MaxValue, 0, prevent_merge: true);
            // Kept apart: a pile landing on another of its element would merge into it.
            var p = go.GetComponent<Pickupable>();
            p.absorbable = false;
            return p;
        }

        static readonly System.Random rng = new System.Random(57);

        void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                if (!Failed) global::Debug.LogWarning("[ReachStressTestMod] stopped: " + e);
                Failed = true;
            }
        }

        void Tick()
        {
            if (Failed || SpeedControlScreen.Instance == null || SpeedControlScreen.Instance.IsPaused || GameClock.Instance == null) return;
            float now = GameClock.Instance.GetTime();
            if (start < 0)
            {
                if (Components.LiveMinionIdentities.Count == 0 || MinionGroupProber.Get() == null) return;
                var world = ClusterManager.Instance.activeWorld;
                if (world.WorldOffset.x + world.WorldSize.x <= 172 || world.WorldOffset.y + world.WorldSize.y <= 216 || Grid.Solid[Cell(135, Y)])
                {
                    global::Debug.LogWarning($"[ReachStressTestMod] not early-base's layout (world {world.WorldOffset} {world.WorldSize}, (135, {Y}) solid {Grid.Solid[Cell(135, Y)]})");
                    Failed = true;
                    return;
                }
                start = now;
            }
            float t = now - start;
            if (stage == 0)
            {
                Rect(DoorX, 205, 170, 215, Solid);
                stage = 1;
                return;
            }
            if (stage == 1 && t >= 1f)
            {
                Rect(137, Y, 146, Y + 2, Open);             // A
                Rect(DoorX, Y, DoorX, Y + 1, Open);         // the door's cells
                Rect(WallX, Y, WallX, Y + 1, Open);         // W, open
                Rect(148, Y, 156, Y + 2, Open);             // B
                Open(ShaftX, Y + 3);                         // the shaft through C's floor
                Rect(137, Y + 4, 142, Y + 6, Open);         // C
                Rect(160, Y, 168, Y + 2, Open);             // D
                stage = 2;
                return;
            }
            if (stage == 2 && t >= 2.5f)
            {
                Setup();
                stage = 3;
                Ready = true;
                nextStep = now;
                frame0Bucket = Timing.BucketTicks; frame0Writer = Timing.WriterTicks; frame0Drain = DrainMs; frames0 = Frames;
                return;
            }
            if (stage != 3) return;
            Frames++;
            int p = (int)((t - 2.5f) / PhaseSeconds);
            if (p != phase)
            {
                if (phase >= 0) EndPhase();
                phase = p;
                step = 0;
                Baseline();
                at = new[] { Count("Crossings"), Count("Notified"), Count("Moves"), Count("Rechecks"), Count("TransformNotices") };
                phaseActions = Actions;
                nextStep = now + 1f;
            }
            string name = Phases[phase % Phases.Length];
            if (name == "flip") Flip();
            if (now < nextStep) return;
            nextStep = now + 2f;
            Step(name);
            step++;
        }

        void Setup()
        {
            door = Build("Door", DoorX, Y);
            BuildLadder();
            locker = Build("StorageLocker", 150, Y);
            cLocker = Build("StorageLocker", 141, Y + 4);
            Arm = Build("SolidTransferArm", 164, Y);
            // The sweeper works only powered: a wire on its power cell puts it on a circuit, and
            // this mod's patch of EnergyConsumer.SetConnectionStatus tells it the circuit has power.
            Build("Wire", 164, Y);
            placer = Util.KInstantiate(Assets.GetPrefab(MovePickupablePlacerConfig.ID));
            placer.transform.SetPosition(Grid.CellToPosCBC(Cell(152, Y), Grid.SceneLayer.Move));
            placer.SetActive(true);
            // Orders in B: dig its ceiling, a blueprint, a puddle to mop, a Hatch.
            for (int x = 149; x <= 152; x++) DigTool.PlaceDig(Cell(x, Y + 3));
            var tile = Assets.GetBuildingDef("Tile");
            blueprint = tile.TryPlace(null, Grid.CellToPosCBC(Cell(155, Y + 2), Grid.SceneLayer.Building), Orientation.Neutral, tile.DefaultElements());
            SimMessages.AddRemoveSubstance(Cell(154, Y), SimHashes.Water, CellEventLogger.Instance.SublimatesEmit, 10f, 300f, byte.MaxValue, 0);
            var hatch = Util.KInstantiate(Assets.GetPrefab("Hatch"), Grid.CellToPosCBC(Cell(149, Y), Grid.SceneLayer.Creatures));
            hatch.SetActive(true);
            // Debris: thousands, so crossings come in bulk.
            for (int i = 0; i < 1500; i++) aDebris.Add(Debris(SimHashes.Sand, Pos(137 + rng.Next(10), Y + rng.Next(3))));
            for (int i = 0; i < 1500; i++) Debris(SimHashes.Cuprite, Pos(148 + rng.Next(9), Y + rng.Next(3)));
            for (int i = 0; i < 500; i++) Debris(SimHashes.SandStone, Pos(137 + rng.Next(6), Y + 4 + rng.Next(3)));
            for (int i = 0; i < 500; i++) Debris(SimHashes.Dirt, Pos(160 + rng.Next(9), Y + rng.Next(3)));
            var storage = locker.GetComponent<Storage>();
            for (int i = 0; i < 100; i++) storage.Store(Debris(SimHashes.Granite, Pos(150, Y), 2f).gameObject, hide_popups: true);
            for (int x = 149; x <= 156; x++) flipCells.Add(Cell(x, Y));
            WideCell = Cell(142, Y + 4);
            global::Debug.Log($"[ReachStressTestMod] area ready: {Components.Pickupables.Count} pickupables");
        }

        void MopPuddle()
        {
            int cell = Cell(154, Y);
            if (Grid.Objects[cell, 8] != null || !Grid.Element[cell].IsLiquid) return;
            var go = Grid.Objects[cell, 8] = Util.KInstantiate(Assets.GetPrefab(new Tag("MopPlacer")));
            go.transform.SetPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront));
            go.SetActive(true);
        }

        void BuildLadder()
        {
            foreach (var go in ladder) if (go != null) go.DeleteObject();
            ladder.Clear();
            for (int y = Y; y <= Y + 4; y++) ladder.Add(Build(usePole ? "FirePole" : "Ladder", ShaftX, y));
        }

        void RemoveLadder()
        {
            foreach (var go in ladder) if (go != null) { go.DeleteObject(); Actions++; }
            ladder.Clear();
        }

        void Baseline()
        {
            var d = door.GetComponent<Door>();
            SetDoor(d, Door.ControlState.Opened);
            var access = door.GetComponent<AccessControl>();
            SetAll(access, AccessControl.Permission.Both);
            Open(WallX, Y); Open(WallX, Y + 1);
            if (ladder.Count == 0) BuildLadder();
            if (Arm == null) Arm = Build("SolidTransferArm", 164, Y);
            if (cDupe != null) { cDupe.DeleteObject(); cDupe = null; }
            if (bionic != null) { bionic.DeleteObject(); bionic = null; }
            foreach (var go in dropped) if (go != null) go.DeleteObject();
            dropped.Clear();
            MopPuddle();
        }

        void Step(string name)
        {
            bool odd = (step & 1) == 1;
            switch (name)
            {
                case "door":
                    var d = door.GetComponent<Door>();
                    if (step % 4 == 2)
                    {
                        // Opened, but closed to every Duplicant by their permissions.
                        SetDoor(d, Door.ControlState.Opened);
                        var access = door.GetComponent<AccessControl>();
                        SetAll(access, AccessControl.Permission.Neither);
                    }
                    else if (step % 4 == 3)
                    {
                        var access = door.GetComponent<AccessControl>();
                        SetAll(access, AccessControl.Permission.Both);
                    }
                    else
                    {
                        if (!odd) Evict(DoorX, 156, Y + 6);
                        SetDoor(d, odd ? Door.ControlState.Opened : Door.ControlState.Locked);
                    }
                    Actions++;
                    break;
                case "wall":
                    if (odd) { Open(WallX, Y); Open(WallX, Y + 1); } else { Evict(WallX, 156, Y + 2); Solid(WallX, Y); Solid(WallX, Y + 1); }
                    Actions++;
                    break;
                case "ladder":
                    if (odd) { usePole = !usePole; BuildLadder(); } else { Evict(137, 142, Y + 6, Y + 3); RemoveLadder(); }
                    break;
                case "teleport":
                    // A locked: half of its debris to the base and back, as the debug teleport moves.
                    if (step == 0) Evict(DoorX, 156, Y + 6);
                    SetDoor(door.GetComponent<Door>(), Door.ControlState.Locked);
                    for (int i = 0; i < 200 && i < aDebris.Count; i++)
                    {
                        var p = aDebris[i];
                        if (p == null || p.storage != null) continue;
                        p.transform.SetPosition(odd ? Pos(137 + i % 10, Y) : Pos(112 + i % 6, Y));
                        Actions++;
                    }
                    break;
                case "raw":
                    // A mod's raw transform sets: the move marker and the storage bin, between B
                    // (locked away) and the base.
                    if (step == 0) Evict(DoorX, 156, Y + 6);
                    SetDoor(door.GetComponent<Door>(), Door.ControlState.Locked);
                    if (placer != null)
                    {
                        placer.transform.position = odd ? Grid.CellToPosCBC(Cell(152, Y), Grid.SceneLayer.Move) : Grid.CellToPosCBC(Cell(114, Y), Grid.SceneLayer.Move);
                        Actions++;
                    }
                    // A construction order (its Constructable's cell is its transform's).
                    if (blueprint != null)
                    {
                        blueprint.transform.position = odd ? Grid.CellToPosCBC(Cell(155, Y + 2), Grid.SceneLayer.Building) : Grid.CellToPosCBC(Cell(120, Y + 2), Grid.SceneLayer.Building);
                        Actions++;
                    }
                    locker.transform.position = odd ? Grid.CellToPosCBC(Cell(150, Y), Grid.SceneLayer.Building) : Grid.CellToPosCBC(Cell(116, Y), Grid.SceneLayer.Building);
                    cLocker.transform.position = odd ? Grid.CellToPosCBC(Cell(141, Y + 4), Grid.SceneLayer.Building) : Grid.CellToPosCBC(Cell(118, Y), Grid.SceneLayer.Building);
                    Actions += 2;
                    break;
                case "falling":
                    // No ladder: C is out of reach, A is not; debris dropped over the shaft
                    // falls from C into A.
                    if (ladder.Count > 0) { Evict(137, 142, Y + 6, Y + 3); RemoveLadder(); }
                    for (int i = 0; i < 50; i++)
                    {
                        dropped.Add(Debris(SimHashes.Sand, Grid.CellToPosCBC(Cell(ShaftX, Y + 6), Grid.SceneLayer.Ore) + new Vector3(0, 0.2f * i, 0)).gameObject);
                        Actions++;
                    }
                    break;
                case "dupes":
                    // No ladder: a Duplicant spawned in C is the only one who reaches it; then deleted.
                    if (ladder.Count > 0) { Evict(137, 142, Y + 6, Y + 3); RemoveLadder(); }
                    if (cDupe == null) cDupe = SpawnMinion(GameTags.Minions.Models.Standard, 140, Y + 4);
                    else { cDupe.DeleteObject(); cDupe = null; }
                    if (bionic == null && Game.IsDlcActiveForCurrentSave(DlcManager.DLC3_ID)) bionic = SpawnMinion(GameTags.Minions.Models.Bionic, 125, Y);
                    else if (bionic != null) { bionic.DeleteObject(); bionic = null; }
                    Actions++;
                    break;
                case "flip":
                    if (step == 0) Evict(DoorX, 156, Y + 6);
                    if (step == 3)
                    {
                        var where = new System.Text.StringBuilder();
                        foreach (var m in Components.LiveMinionIdentities.Items) if (m != null) where.Append($" {m.name}@{Grid.CellToXY(Grid.PosToCell(m))}");
                        global::Debug.Log($"[ReachStressTestMod] flip: B's floor reached by {Traverse.Create(MinionGroupProber.Get()).Field<int[]>("cells").Value[flipCells[0]]}; Duplicants{where}");
                    }
                    SetDoor(door.GetComponent<Door>(), Door.ControlState.Locked);
                    break;
                case "offsets":
                    // C cut off; copper on its floor works from B's floor too (Mod.Stretch), so
                    // only W decides its reach: filled and dug.
                    if (step == 0)
                    {
                        if (ladder.Count > 0) { Evict(137, 142, Y + 6, Y + 3); RemoveLadder(); }
                        for (int i = 0; i < 10; i++) dropped.Add(Debris(SimHashes.Copper, Pos(142, Y + 4)).gameObject);
                    }
                    else if (odd) { Evict(WallX, 156, Y + 2); Solid(WallX, Y); Solid(WallX, Y + 1); }
                    else { Open(WallX, Y); Open(WallX, Y + 1); }
                    Actions++;
                    break;
                case "sweeper":
                    if (Arm != null && step < 2)
                    {
                        var ec = Arm.GetComponent<EnergyConsumer>();
                        global::Debug.Log($"[ReachStressTestMod] sweeper: operational {Arm.GetComponent<Operational>().IsOperational}, powered {ec?.IsPowered}, circuit {ec?.CircuitID}");
                    }
                    if (Arm != null) { Arm.DeleteObject(); Arm = null; Actions++; }
                    else Arm = Build("SolidTransferArm", 164, Y);
                    break;
            }
        }

        /// The same frame: B's floor occupied and vacated again (B locked away), and the door
        /// opened and locked again.
        void Flip()
        {
            var prober = MinionGroupProber.Get();
            prober.OccupyST(flipCells);
            prober.VacateST(flipCells);
            var d = door.GetComponent<Door>();
            SetDoor(d, Door.ControlState.Opened);
            SetDoor(d, Door.ControlState.Locked);
            Actions++;
        }

        /// The Duplicants who wandered into x0..x1 (rows Y or yMin up to yMax) back to the base, so
        /// that closing the way in leaves the area out of everyone's reach.
        void Evict(int x0, int x1, int yMax, int yMin = Y)
        {
            foreach (var minion in Components.LiveMinionIdentities.Items)
            {
                if (minion == null || minion.gameObject == cDupe) continue;
                Grid.CellToXY(Grid.PosToCell(minion), out int x, out int y);
                if (x < x0 || x > x1 || y < yMin || y > yMax) continue;
                var nav = minion.GetComponent<Navigator>();
                if (nav != null && nav.IsMoving()) nav.Stop();
                minion.transform.SetPosition(Grid.CellToPosCBC(Cell(124, Y), Grid.SceneLayer.Move));
                Evictions++;
            }
        }

        /// The door's control state, at once (as the game applies it in instant build mode; else a
        /// Duplicant goes to toggle it, and asking twice for one state cancels the request).
        static void SetDoor(Door door, Door.ControlState state)
        {
            var t = Traverse.Create(door);
            if (t.Field<Door.ControlState>("controlState").Value == state) return;
            t.Field<Door.ControlState>("requestedState").Value = state;
            t.Method("ApplyRequestedControlState").GetValue();
        }

        static void SetAll(AccessControl access, AccessControl.Permission permission)
        {
            for (int i = 0; i < Components.MinionAssignablesProxy.Count; i++)
                if (Components.MinionAssignablesProxy[i] != null) access.SetPermission(Components.MinionAssignablesProxy[i], permission);
        }

        static GameObject SpawnMinion(Tag model, int x, int y)
        {
            var stats = new MinionStartingStats(model, false, null, null, isDebugMinion: true);
            var prefab = Assets.GetPrefab(BaseMinionConfig.GetMinionIDForModel(model));
            var go = Util.KInstantiate(prefab);
            go.name = prefab.name;
            Immigration.Instance.ApplyDefaultPersonalPriorities(go);
            go.transform.SetLocalPosition(Grid.CellToPosCBC(Cell(x, y), Grid.SceneLayer.Move));
            go.SetActive(true);
            stats.Apply(go);
            return go;
        }

        void EndPhase()
        {
            string name = Phases[phase % Phases.Length];
            long crossings = Count("Crossings") - at[0], notified = Count("Notified") - at[1], moves = Count("Moves") - at[2], rechecks = Count("Rechecks") - at[3];
            switch (name)
            {
                case "door": DoorCrossings += crossings; break;
                case "wall": WallCrossings += crossings; break;
                case "ladder": LadderCrossings += crossings; break;
                case "dupes": DupesCrossings += crossings; break;
                case "flip": FlipCrossings += crossings; break;
                case "sweeper": SweeperCrossings += crossings; break;
                case "offsets": OffsetsCrossings += crossings; break;
                case "teleport": TeleportMoves += moves; break;
                case "raw": RawMoves += moves; RawTransformNotices += Count("TransformNotices") - at[4]; break;
                case "falling": FallingMoves += moves; FallingCrossings += crossings; break;
            }
            PhasesRun++;
            long frames = Frames - frames0;
            global::Debug.Log($"[ReachStressTestMod] phase {name}: {Actions - phaseActions} actions; {crossings} cells crossed, {notified} objects notified, {moves} moves, {rechecks} rechecks; "
                + $"cost so far a frame: polling {(frames > 0 ? (Timing.BucketTicks - frame0Bucket) * 1000.0 / Stopwatch.Frequency / frames : 0):F4} ms, "
                + $"table writers (main thread) {(frames > 0 ? (Timing.WriterTicks - frame0Writer) * 1000.0 / Stopwatch.Frequency / frames : 0):F4} ms, "
                + $"drain {(frames > 0 ? (DrainMs - frame0Drain) / frames : 0):F4} ms over {frames} frames; {Components.Pickupables.Count} pickupables");
        }
    }
}
