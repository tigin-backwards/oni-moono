#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace BeetaTurnsTestMod
{
    /// A test mod for critter-needs-first's threat turns (tests/e2e/critter-threat-turns.yaml):
    /// on the early-base fixture, three sealed rooms in the rock:
    /// - 12 adult Beetas in cold oxygen (270 K);
    /// - BEETA_FILLERS (default 500) wild Molten Slicksters in hot oxygen, which only take brain
    ///   turns, so a round of the game's critter turns (5 a frame) is long;
    /// - a parking room for one Pip, the Beetas' target.
    ///
    /// The Pip visits the Beetas' room for 8 game seconds, then is parked for 30 to 40. A Beeta's
    /// sting on it is counted and dropped (Weapon.AttackTarget skipped), so the Pip neither dies
    /// nor flees, and the Beeta that stung is replaced after the visit (a Beeta dies when it
    /// stings). Also counted: the Beetas' brain turns and threat acquisitions (ThreatMonitor's
    /// GoToThreatened from a safe state). BEETA_FPS (default 30) holds the frame rate.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(Mod), nameof(GameSpawned)));
            harmony.Patch(AccessTools.Method(typeof(Weapon), nameof(Weapon.AttackTarget)), prefix: new HarmonyMethod(typeof(Mod), nameof(Attacking)));
            harmony.Patch(AccessTools.Method(typeof(Brain), nameof(Brain.UpdateBrain)), postfix: new HarmonyMethod(typeof(Mod), nameof(BrainUpdated)));
            harmony.Patch(AccessTools.Method(typeof(ThreatMonitor.Instance), nameof(ThreatMonitor.Instance.GoToThreatened)), prefix: new HarmonyMethod(typeof(Mod), nameof(Threatened)));
            global::Debug.Log("[BeetaTurnsTestMod] loaded");
        }

        static void GameSpawned(Game __instance) => __instance.gameObject.AddComponent<Rooms>();

        static bool Attacking(Weapon __instance, GameObject target)
        {
            if (target == null || target != Rooms.Target) return true;
            if (Rooms.Bees.TryGetValue(__instance.gameObject.GetInstanceID(), out var bee) && Rooms.Measuring && !bee.Stung)
            {
                Rooms.Hits++;
                Rooms.Stung(bee);
            }
            return false;
        }

        static void BrainUpdated(Brain __instance)
        {
            if (!Rooms.Bees.TryGetValue(__instance.gameObject.GetInstanceID(), out var bee)) return;
            float now = GameClock.Instance.GetTime();
            if (Rooms.Measuring && bee.LastTurn >= 0f)
            {
                Rooms.TurnGaps += now - bee.LastTurn;
                Rooms.TurnGapCount++;
            }
            bee.LastTurn = now;
        }

        static void Threatened(ThreatMonitor.Instance __instance)
        {
            if (!Rooms.Measuring || !Rooms.Bees.ContainsKey(__instance.gameObject.GetInstanceID())) return;
            // Only a change from safe to threatened is an acquisition.
            if (__instance.GetCurrentState() == __instance.sm.threatened.creature) return;
            Rooms.Acquisitions++;
        }
    }

    internal sealed class Bee
    {
        public GameObject Go;
        public float LastTurn = -1f;
        public bool Stung;
    }

    public sealed class Rooms : MonoBehaviour
    {
        const int Beetas = 12;
        // A visit: the Pip is in the Beetas' room this long, then parked out of reach.
        const float Visit = 8f, Gap = 30f;
        const int W = 20, H = 8;

        // For the spec (`type:BeetaTurnsTestMod.Rooms::Hits`).
        public static bool Ready, Failed, Measuring;
        public static int Hits, VisitStings, Acquisitions, CritterBrains, Fillers, FrameRate, Visits, BeeVisits, TurnGapCount;
        public static float RoundSeconds, MeasuredSeconds, TurnGaps, MeanTurnGap;

        internal static readonly Dictionary<int, Bee> Bees = new Dictionary<int, Bee>();
        internal static GameObject Target;
        static float visitStart = -1f;
        static bool inVisit;

        internal static void Stung(Bee bee)
        {
            bee.Stung = true;
            if (inVisit) VisitStings++;
        }

        readonly List<int[]> rooms = new List<int[]>();
        int stage, waited;
        long frames;
        float played, start, nextLog, nextVisit;
        readonly System.Random random = new System.Random(1);

        void Awake()
        {
            Ready = Failed = Measuring = inVisit = false;
            Hits = VisitStings = Acquisitions = CritterBrains = Visits = BeeVisits = TurnGapCount = 0;
            RoundSeconds = MeasuredSeconds = TurnGaps = MeanTurnGap = 0f;
            Bees.Clear();
            Target = null;
            FrameRate = int.TryParse(Environment.GetEnvironmentVariable("BEETA_FPS"), out int fps) && fps > 0 ? fps : 30;
            Fillers = int.TryParse(Environment.GetEnvironmentVariable("BEETA_FILLERS"), out int n) && n >= 0 ? n : 500;
        }

        void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                if (!Failed) global::Debug.LogWarning("[BeetaTurnsTestMod] stopped: " + e);
                Failed = true;
            }
        }

        void Tick()
        {
            if (Failed || GameClock.Instance == null || ClusterManager.Instance == null) return;
            if (Application.targetFrameRate != FrameRate) Application.targetFrameRate = FrameRate;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            switch (stage)
            {
                case 0:
                    Room(SimHashes.Oxygen, 2f, 270f);
                    Room(SimHashes.Oxygen, 2f, 420f);
                    Room(SimHashes.Oxygen, 2f, 290f);
                    stage = 1;
                    break;
                case 1:
                    if (++waited > 600) throw new InvalidOperationException("the rooms' cells stayed solid");
                    foreach (var room in rooms)
                        if (Grid.Solid[Grid.XYToCell(room[0] + 1, room[1] + 1)] || Grid.Solid[Grid.XYToCell(room[0] + W - 2, room[1] + H - 2)]) return;
                    foreach (var go in Spawn(rooms[0], "Bee", Beetas, 3)) Bees[go.GetInstanceID()] = new Bee { Go = go };
                    Target = Spawn(rooms[2], "Squirrel", 1, 1)[0];
                    Spawn(rooms[1], "OilfloaterHighTemp", Fillers, 1);
                    stage = 2;
                    break;
                case 2:
                    // Let the critters land and settle before counting.
                    if (++waited < 660) return;
                    CritterBrains = Brains();
                    Ready = Measuring = true;
                    start = GameClock.Instance.GetTime();
                    nextLog = 60f;
                    nextVisit = start + 5f;
                    stage = 3;
                    global::Debug.Log($"[BeetaTurnsTestMod] ready: {Beetas} Beetas at {rooms[0][0]},{rooms[0][1]}, {Fillers} fillers at {rooms[1][0]},{rooms[1][1]}; {CritterBrains} critter brains; fps cap {FrameRate}");
                    break;
                default:
                    if (Time.timeScale <= 0f) return;
                    frames++;
                    played += Mathf.Min(Time.deltaTime, 0.2f);
                    CritterBrains = Brains();
                    RoundSeconds = CritterBrains * played / (5f * frames);
                    MeasuredSeconds = GameClock.Instance.GetTime() - start;
                    MeanTurnGap = TurnGapCount > 0 ? TurnGaps / TurnGapCount : 0f;
                    Visits_();
                    if (MeasuredSeconds < nextLog) break;
                    nextLog += 60f;
                    global::Debug.Log($"[BeetaTurnsTestMod] {MeasuredSeconds:F0} game s, speed {Time.timeScale}, {CritterBrains} critter brains, round {RoundSeconds:F1} game s; stings {Hits} ({VisitStings} in a visit), acquisitions {Acquisitions}, mean Beeta turn gap {MeanTurnGap:F2} game s; visits {Visits}, Beeta-visits {BeeVisits}");
                    break;
            }
        }

        /// The Pip visits the Beetas' room for VISIT game seconds every GAP to GAP + 10; after
        /// each visit, a Beeta that stung is replaced.
        void Visits_()
        {
            float now = GameClock.Instance.GetTime();
            if (!inVisit && now >= nextVisit)
            {
                Move(Target, rooms[0], 1);
                inVisit = true;
                visitStart = now;
                Visits++;
                foreach (var b in Bees.Values) if (b.Go != null && !b.Stung) BeeVisits++;
            }
            else if (inVisit && now >= visitStart + Visit)
            {
                Move(Target, rooms[2], 1);
                inVisit = false;
                nextVisit = now + Gap + (float)random.NextDouble() * 10f;
                var gone = new List<int>();
                foreach (var kv in Bees) if (kv.Value.Go == null || kv.Value.Stung || kv.Value.Go.HasTag(GameTags.Dead)) gone.Add(kv.Key);
                foreach (int id in gone)
                {
                    var go = Bees[id].Go;
                    if (go != null) Util.KDestroyGameObject(go);
                    Bees.Remove(id);
                }
                foreach (var go in Spawn(rooms[0], "Bee", gone.Count, 3)) Bees[go.GetInstanceID()] = new Bee { Go = go };
            }
        }

        static void Move(GameObject go, int[] room, int row)
        {
            var nav = go.GetComponent<Navigator>();
            if (nav != null) nav.Stop();
            go.transform.SetPosition(Grid.CellToPosCBC(Grid.XYToCell(room[0] + W / 2, room[1] + row), Grid.SceneLayer.Creatures));
            // U59's CellChangeMonitor never raises GameHashes.CellChanged, so AttackableBase's
            // scene-partitioner entry stays where the critter spawned (U60 raises it). Raise it
            // here, so the Beetas' threat search sees the Pip where it is.
            go.Trigger((int)GameHashes.CellChanged, null);
        }

        static int Brains()
        {
            foreach (var group in Game.BrainScheduler.debugGetBrainGroups())
                if (group.tag == GameTags.CreatureBrain) return group.BrainCount;
            return 0;
        }

        void Room(SimHashes gas, float kg, float temperature)
        {
            if (!Find(out int x, out int y)) throw new InvalidOperationException("no block of rock for a room");
            rooms.Add(new[] { x, y });
            for (int dy = 0; dy < H; dy++)
                for (int dx = 0; dx < W; dx++)
                {
                    bool wall = dx == 0 || dy == 0 || dx == W - 1 || dy == H - 1;
                    SimMessages.ReplaceElement(Grid.XYToCell(x + dx, y + dy), wall ? SimHashes.Obsidian : gas, CellEventLogger.Instance.SandBoxTool, wall ? 2000f : kg, temperature);
                }
        }

        bool Find(out int fx, out int fy)
        {
            var world = ClusterManager.Instance.GetWorld(ClusterManager.Instance.activeWorldId);
            for (int y = (int)world.minimumBounds.y + 4; y + H < (int)world.maximumBounds.y - 4; y++)
                for (int x = (int)world.minimumBounds.x + 4; x + W < (int)world.maximumBounds.x - 4; x++)
                {
                    bool near = false;
                    foreach (var room in rooms)
                        if (Math.Abs(x - room[0]) <= W + 1 && Math.Abs(y - room[1]) <= H + 1) near = true;
                    if (near || !Rock(x, y, world.id)) continue;
                    fx = x; fy = y;
                    return true;
                }
            fx = fy = 0;
            return false;
        }

        static bool Rock(int x, int y, int world)
        {
            for (int dy = 0; dy < H; dy++)
                for (int dx = 0; dx < W; dx++)
                {
                    int cell = Grid.XYToCell(x + dx, y + dy);
                    if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world) return false;
                    if (!Grid.Solid[cell] || Grid.Foundation[cell] || Grid.Element[cell].id == SimHashes.Unobtanium) return false;
                    if (Grid.Objects[cell, (int)ObjectLayer.Building] != null || Grid.Objects[cell, (int)ObjectLayer.FoundationTile] != null) return false;
                }
            return true;
        }

        /// COUNT critters spread over the room's inside from row ROW up.
        static List<GameObject> Spawn(int[] room, string prefabId, int count, int row)
        {
            var spawned = new List<GameObject>();
            var prefab = Assets.GetPrefab(prefabId);
            for (int i = 0; i < count; i++)
            {
                int cell = Grid.XYToCell(room[0] + 1 + (i * 3) % (W - 2), room[1] + row + i / (W - 2) % (H - 1 - row));
                var critter = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, Grid.SceneLayer.Creatures), Grid.SceneLayer.Creatures);
                critter.SetActive(true);
                spawned.Add(critter);
            }
            return spawned;
        }
    }
}
