using System;
using System.Collections.Generic;
using HarmonyLib;
using KMod;
using Klei.AI;
using UnityEngine;

namespace CritterTurnsTestMod
{
    /// TEST INFRASTRUCTURE for brain-rate's critter turns (issue #85,
    /// tests/e2e/issue-85-critters-excrete.yaml): on the early-base fixture, three sealed rooms
    /// in the world's rock, away from the base:
    /// - 8 tame, groomed Molten Slicksters in carbon dioxide;
    /// - 8 tame, groomed Pufts in polluted oxygen;
    /// - 1,300 wild Molten Slicksters in oxygen, which find nothing to inhale and only take
    ///   their brain turns.
    ///
    /// The game gives 5 critters a brain turn a frame and a frame advances at most 0.2 game
    /// seconds, so at 15 frames a second or fewer a critter's turn comes every `critters / 25`
    /// game seconds whatever the machine's speed: about 53 here, longer than a Slickster's meal
    /// lasts it (45) or a Puft's (18). In that order a gas-eater is hungry on every turn, eats
    /// and never excretes. The mod holds the frame rate to 15 (Application.targetFrameRate, no
    /// vSync), so the round is that long on any machine; `RoundSeconds` is the round as measured.
    ///
    /// It counts the 16's excretions by kind (CreatureCalorieMonitor.Instance.Poop), and how many
    /// of each kind have excreted at all, and logs them every 150 game seconds.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(Mod), nameof(GameSpawned)));
            harmony.Patch(
                AccessTools.Method(typeof(CreatureCalorieMonitor.Instance), nameof(CreatureCalorieMonitor.Instance.Poop), new[] { typeof(PoopData) }),
                prefix: new HarmonyMethod(typeof(Mod), nameof(Excreted)));
            global::Debug.Log("[CritterTurnsTestMod] loaded");
        }

        static void GameSpawned(Game __instance) => __instance.gameObject.AddComponent<Rooms>();

        static void Excreted(CreatureCalorieMonitor.Instance __instance)
        {
            int id = __instance.gameObject.GetInstanceID();
            if (Rooms.SlicksterIds.TryGetValue(id, out int slickster))
            {
                Rooms.SlicksterIds[id] = slickster + 1;
                Rooms.SlicksterExcretions++;
                if (slickster == 0) Rooms.SlickstersExcreting++;
            }
            else if (Rooms.PuftIds.TryGetValue(id, out int puft))
            {
                Rooms.PuftIds[id] = puft + 1;
                Rooms.PuftExcretions++;
                if (puft == 0) Rooms.PuftsExcreting++;
            }
        }
    }

    public sealed class Rooms : MonoBehaviour
    {
        const int Slicksters = 8, Pufts = 8, Others = 1300;
        const int FrameRate = 15;
        // A room, walls included.
        const int W = 20, H = 8;
        const string Slickster = "OilfloaterHighTemp", Puft = "Puft";

        // For the spec (`type:CritterTurnsTestMod.Rooms::Ready`).
        public static bool Ready, Failed;
        public static int SlicksterExcretions, PuftExcretions;
        /// The Slicksters and Pufts that have excreted at least once.
        public static int SlickstersExcreting, PuftsExcreting;
        /// The critter brains the game schedules: the rooms' and the fixture's.
        public static int CritterBrains;
        /// The game seconds a round of the critters' turns takes at the game's 5 a frame, over
        /// the frames played since the rooms were ready (0 before any).
        public static float RoundSeconds;

        // Each test critter's excretions, by instance id.
        internal static readonly Dictionary<int, int> SlicksterIds = new Dictionary<int, int>(), PuftIds = new Dictionary<int, int>();

        readonly List<int[]> rooms = new List<int[]>();
        int stage, waited;
        long frames;
        float played, nextLog = 150f;

        void Awake()
        {
            Ready = Failed = false;
            SlicksterExcretions = PuftExcretions = SlickstersExcreting = PuftsExcreting = CritterBrains = 0;
            RoundSeconds = 0f;
            SlicksterIds.Clear();
            PuftIds.Clear();
        }

        void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                if (!Failed) global::Debug.LogWarning("[CritterTurnsTestMod] stopped: " + e);
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
                    // The rooms: blocks of natural rock become obsidian shells around a gas.
                    Room(SimHashes.CarbonDioxide, 200f, 420f);
                    Room(SimHashes.ContaminatedOxygen, 20f, 300f);
                    Room(SimHashes.Oxygen, 2f, 420f);
                    stage = 1;
                    break;
                case 1:
                    // The sim replaces the cells over the next frames; critters go into gas.
                    if (++waited > 600) throw new InvalidOperationException("the rooms' cells stayed solid");
                    foreach (var room in rooms)
                        if (Grid.Solid[Grid.XYToCell(room[0] + 1, room[1] + 1)] || Grid.Solid[Grid.XYToCell(room[0] + W - 2, room[1] + H - 2)]) return;
                    Spawn(rooms[0], Slickster, Slicksters, SlicksterIds);
                    Spawn(rooms[1], Puft, Pufts, PuftIds);
                    Spawn(rooms[2], Slickster, Others, null);
                    stage = 2;
                    break;
                case 2:
                    // A frame later the new critters' brains are scheduled.
                    CritterBrains = Brains();
                    Ready = true;
                    stage = 3;
                    global::Debug.Log($"[CritterTurnsTestMod] ready: {Slicksters} Slicksters at {rooms[0][0]},{rooms[0][1]}, {Pufts} Pufts at {rooms[1][0]},{rooms[1][1]}, {Others} others at {rooms[2][0]},{rooms[2][1]}; {CritterBrains} critter brains");
                    break;
                default:
                    if (Time.timeScale <= 0f) return;
                    frames++;
                    played += Mathf.Min(Time.deltaTime, 0.2f);
                    CritterBrains = Brains();
                    RoundSeconds = CritterBrains * played / (5f * frames);
                    if (played < nextLog) break;
                    nextLog += 150f;
                    global::Debug.Log($"[CritterTurnsTestMod] {played:F0} game seconds, {frames} frames, {CritterBrains} critter brains, a round every {RoundSeconds:F1} game seconds; excretions: Slicksters {string.Join(" ", SlicksterIds.Values)}, Pufts {string.Join(" ", PuftIds.Values)}");
                    break;
            }
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

        /// The first block of natural solid cells in the active world, bottom up, clear of the
        /// rooms already placed: the same block in every run of a fixture.
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

        /// COUNT critters spread over the room's inside; those with `ids` tame and groomed.
        static void Spawn(int[] room, string prefabId, int count, Dictionary<int, int> ids)
        {
            var prefab = Assets.GetPrefab(prefabId);
            for (int i = 0; i < count; i++)
            {
                int cell = Grid.XYToCell(room[0] + 1 + i % (W - 2), room[1] + 1 + i / (W - 2) % (H - 2));
                var critter = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, Grid.SceneLayer.Creatures), Grid.SceneLayer.Creatures);
                critter.SetActive(true);
                if (ids == null) continue;
                ids[critter.GetInstanceID()] = 0;
                Db.Get().Amounts.Wildness.Lookup(critter).value = 0f;
                critter.GetComponent<Effects>().Add("Ranched", true);
            }
        }
    }
}
