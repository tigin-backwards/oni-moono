using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace RailLoopTestMod
{
    /// TEST INFRASTRUCTURE for the direct-rail-bridges game tweak
    /// (tests/e2e/direct-rail-bridges.yaml): on the early-base fixture, a closed conveyor loop
    /// next to the base, with ten items riding it.
    ///
    ///     top row (y + 1), westward:   R R R R R R R R R      R: rail   A, B: bridges (3 wide)
    ///     bottom row (y), eastward:    R A R R R B R R V      V: meter valve, in below, out above
    ///
    /// Each lap an item crosses both bridges and the valve, so the bridges' whole-item branch runs
    /// about 30 times a lap. The valve's limit (600) is reached after about eleven laps: the item
    /// that reaches it is split (the bridge's partial branch), the valve closes, and the items
    /// queue up behind it. From then on nothing moves, and `Digest` (every rail tile's item and
    /// mass in loop order, and the valve's count) is the same in every run that moves items as
    /// the game does, whenever it is read. The rails' flow has no randomness, so the digest of a
    /// run without the tweak is the one a run with it must reach.
    ///
    /// The valve is told it has power (a patch of EnergyConsumer.SetConnectionStatus), and the
    /// items' kinds are discovered up front, as a colony that carries them would have them.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(Mod), nameof(GameSpawned)));
            harmony.Patch(AccessTools.Method(typeof(EnergyConsumer), nameof(EnergyConsumer.SetConnectionStatus)), prefix: new HarmonyMethod(typeof(Mod), nameof(Powered)));
            global::Debug.Log("[RailLoopTestMod] loaded");
        }

        static void GameSpawned(Game __instance) => __instance.gameObject.AddComponent<Loop>();

        static void Powered(EnergyConsumer __instance, ref CircuitManager.ConnectionStatus connection_status)
        {
            if (__instance.gameObject == Loop.Valve) connection_status = CircuitManager.ConnectionStatus.Powered;
        }
    }

    public sealed class Loop : MonoBehaviour
    {
        // The loop's lower-left rail tile (early-base's coordinates, in rock beside the base).
        const int X = 140, Y = 209;
        const float ValveLimit = 600f;

        // For the spec (`type:RailLoopTestMod.Loop::Digest`).
        public static bool Ready, Failed, Stalled;
        public static int Items;
        public static float TotalMass, TemperatureSum, ValveAmount;
        public static string Digest = "";
        internal static GameObject Valve;

        readonly List<int> cells = new List<int>();
        readonly List<Pickupable> spawned = new List<Pickupable>();
        float start = -1;
        int stage, unchanged;
        float nextRead;
        string last = "";

        static int Cell(int x, int y) => Grid.XYToCell(x, y);

        void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                if (!Failed) global::Debug.LogWarning("[RailLoopTestMod] stopped: " + e);
                Failed = true;
            }
        }

        static GameObject Build(string id, int x, int y)
        {
            var def = Assets.GetBuildingDef(id);
            return def.Build(Cell(x, y), Orientation.Neutral, null, def.DefaultElements(), 300f, playsound: false);
        }

        void Rail(int x, int y, UtilityConnections connections)
        {
            var go = Build("SolidConduit", x, y);
            go.GetComponent<KAnimGraphTileVisualizer>().UpdateConnections(connections);
            cells.Add(Cell(x, y));
        }

        Pickupable Ore(SimHashes element, float mass, float temperature, int x)
        {
            var go = ElementLoader.FindElementByHash(element).substance.SpawnResource(Grid.CellToPosCCC(Cell(x, Y + 1), Grid.SceneLayer.Ore), mass, temperature, byte.MaxValue, 0, prevent_merge: true);
            return go.GetComponent<Pickupable>();
        }

        Pickupable Thing(string prefab, int x, int y)
        {
            var go = Util.KInstantiate(Assets.GetPrefab(prefab), Grid.CellToPosCCC(Cell(x, y), Grid.SceneLayer.Ore));
            go.SetActive(true);
            return go.GetComponent<Pickupable>();
        }

        void Tick()
        {
            if (Failed || SpeedControlScreen.Instance == null || SpeedControlScreen.Instance.IsPaused || GameClock.Instance == null) return;
            float now = GameClock.Instance.GetTime();
            if (start < 0)
            {
                if (Components.LiveMinionIdentities.Count == 0 || DiscoveredResources.Instance == null) return;
                var world = ClusterManager.Instance.activeWorld;
                if (world.WorldOffset.x + world.WorldSize.x <= X + 12 || world.WorldOffset.y + world.WorldSize.y <= Y + 4)
                {
                    global::Debug.LogWarning($"[RailLoopTestMod] not early-base's layout (world {world.WorldOffset} {world.WorldSize})");
                    Failed = true;
                    return;
                }
                start = now;
            }
            float t = now - start;
            var flow = Game.Instance.solidConduitFlow;
            if (stage == 0 && t >= 1f)
            {
                for (int x = X; x <= X + 8; x++)
                    for (int y = Y; y <= Y + 1; y++)
                        if (Grid.Objects[Cell(x, y), (int)ObjectLayer.Building] != null || Grid.Objects[Cell(x, y), (int)ObjectLayer.SolidConduit] != null)
                            throw new InvalidOperationException($"a building at ({x}, {y})");
                // In loop order from bridge A's output: the bottom row eastward, the valve, the top
                // row westward, down to bridge A's input.
                Rail(X + 2, Y, UtilityConnections.Right);
                Rail(X + 3, Y, UtilityConnections.Left | UtilityConnections.Right);
                Rail(X + 4, Y, UtilityConnections.Left);
                Rail(X + 6, Y, UtilityConnections.Right);
                Rail(X + 7, Y, UtilityConnections.Left | UtilityConnections.Right);
                Rail(X + 8, Y, UtilityConnections.Left);
                Rail(X + 8, Y + 1, UtilityConnections.Left);
                for (int x = X + 7; x >= X + 1; x--) Rail(x, Y + 1, UtilityConnections.Left | UtilityConnections.Right);
                Rail(X, Y + 1, UtilityConnections.Right | UtilityConnections.Down);
                Rail(X, Y, UtilityConnections.Up);
                Build("SolidConduitBridge", X + 1, Y);
                Build("SolidConduitBridge", X + 5, Y);
                Valve = Build("SolidLimitValve", X + 8, Y);
                // A wire at the valve's power input: on a circuit, the circuit manager tells the valve
                // its connection status, which this mod's patch turns into "powered".
                Build("Wire", X + 8, Y + 1);
                stage = 1;
                return;
            }
            if (stage == 1 && t >= 3f)
            {
                Valve.GetComponent<LimitValve>().Limit = ValveLimit;
                Game.Instance.solidConduitSystem.ForceRebuildNetworks();
                stage = 2;
                return;
            }
            if (stage == 2 && t >= 5f)
            {
                foreach (int cell in cells)
                    if (!flow.HasConduit(cell)) throw new InvalidOperationException($"no rail in the flow at cell {cell}");
                // Spawned and put on the rails in one frame, as a save's rail items are when it loads.
                // The top row's tiles east of the corner, west to east.
                spawned.Add(Ore(SimHashes.SandStone, 6f, 300f, X + 1));
                spawned.Add(Ore(SimHashes.Cuprite, 9f, 320f, X + 2));
                spawned.Add(Ore(SimHashes.SandStone, 4f, 290f, X + 3));
                spawned.Add(Ore(SimHashes.IronOre, 11f, 310f, X + 4));
                spawned.Add(Thing("FieldRation", X + 5, Y + 1));
                spawned.Add(Thing("HatchEgg", X + 6, Y + 1));
                spawned.Add(Ore(SimHashes.Dirt, 7f, 295f, X + 7));
                spawned.Add(Ore(SimHashes.Cuprite, 5f, 305f, X + 8));
                spawned.Add(Thing("BasicPlantFood", X + 3, Y));
                spawned.Add(Ore(SimHashes.SandStone, 8f, 300f, X + 7));
                foreach (var p in spawned)
                {
                    p.absorbable = false;
                    var id = p.KPrefabID;
                    DiscoveredResources.Instance.Discover(id.PrefabTag, DiscoveredResources.GetCategoryForEntity(id));
                }
                int[] at = { Cell(X + 1, Y + 1), Cell(X + 2, Y + 1), Cell(X + 3, Y + 1), Cell(X + 4, Y + 1), Cell(X + 5, Y + 1), Cell(X + 6, Y + 1), Cell(X + 7, Y + 1), Cell(X + 8, Y + 1), Cell(X + 3, Y), Cell(X + 7, Y) };
                for (int i = 0; i < spawned.Count; i++)
                {
                    if (spawned[i] == null) throw new InvalidOperationException($"item {i} is gone");
                    flow.AddPickupable(at[i], spawned[i]);
                }
                stage = 3;
                Ready = true;
                nextRead = now + 1f;
                global::Debug.Log($"[RailLoopTestMod] ready: {spawned.Count} items on {cells.Count} rail tiles");
                return;
            }
            if (stage != 3 || now < nextRead) return;
            nextRead = now + 1f;
            Read(flow);
        }

        void Read(SolidConduitFlow flow)
        {
            var text = new StringBuilder();
            int items = 0;
            float mass = 0, temperature = 0;
            foreach (int cell in cells)
            {
                var pickupable = flow.GetPickupable(flow.GetContents(cell).pickupableHandle);
                if (pickupable == null) { text.Append("-;"); continue; }
                var pe = pickupable.PrimaryElement;
                items++;
                mass += pe.Mass;
                temperature += pe.Temperature;
                text.Append(pickupable.KPrefabID.PrefabTag.Name).Append(' ').Append(pe.Mass.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }
            ValveAmount = Valve.GetComponent<LimitValve>().Amount;
            text.Append("valve ").Append(ValveAmount.ToString("R", CultureInfo.InvariantCulture));
            Items = items;
            TotalMass = mass;
            TemperatureSum = temperature;
            Digest = text.ToString();
            unchanged = Digest == last ? unchanged + 1 : 0;
            last = Digest;
            bool stalled = unchanged >= 10 && ValveAmount >= ValveLimit;
            if (stalled && !Stalled)
                global::Debug.Log($"[RailLoopTestMod] stalled after {GameClock.Instance.GetTime() - start:F0} s: {items} items, {mass.ToString("R", CultureInfo.InvariantCulture)} kg, temperatures {temperature:F2}, digest {Digest}");
            Stalled = stalled;
        }
    }
}
