using System;
using System.Collections;
using System.Collections.Generic;
using Linq = System.Linq.Expressions;
using HarmonyLib;
using UnityEngine;
using Entry = UpdateBucketWithUpdater<ISim200ms>.Entry;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-sublimation` (opt-in). Every sublimating item (polluted dirt, bleach
    /// stone, oxylite…) runs Sublimates.Sim200ms five times a game second. In a large colony
    /// most of them lie loose in cells already full of gas and are already marked blocked: the
    /// call changes nothing, but it reads the item, its transform, tags, element and
    /// temperature, scattered objects that cost about 1.8 ms a frame in U57 (1,880 of 2,450
    /// items in that state).
    ///
    /// The game's update buckets for Sublimates get a batch function (the scheduler's
    /// batch_update_delegate) that runs the same items, in the same order, with the same time
    /// steps, at the same sub-ticks. For each item it keeps, from its last full update, what
    /// that update read: its cell, its transform, its temperature source (the sim's element
    /// chunk), its element's lowest temperature and the cell's mass limit. An item is skipped
    /// only when its full update would change nothing: it is not sealed and has no tag changed
    /// since (watched through its own tag-change event), it is in the same cell (read from its
    /// transform), its temperature in the sim's chunk is above the limit, its cell is still full,
    /// and it was left blocked on pressure. The skipped update's one side effect, the
    /// temperature read copying the chunk's heat delta onto the item's SimTemperatureTransfer,
    /// is made too. Every other item runs Sublimates.Sim200ms as before.
    ///
    /// With ONI_TWEAK_VERIFY=1 the items that would be skipped run their full update instead,
    /// and any change it makes (state, sublimated mass, the item's mass) is logged.
    [GameMember(GameUse.Uses, "StateMachineUpdater/BaseUpdateBucket[] SimAndRenderScheduler/Entry::buckets")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<System.String,SimAndRenderScheduler/Entry> SimAndRenderScheduler::bucketTable")]
    [GameMember(GameUse.Prefix, "System.Void Sublimates::OnCleanUp()")]
    [GameMember(GameUse.Postfix, "System.Void Sublimates::OnSpawn()")]
    [GameMember(GameUse.Mirrors, "System.Void Sublimates::Sim200ms(System.Single)", "86bc4dc07c603b92 linux-744825 win-744825", "32cbb2fea2652722 linux-761211 win-761211", "c2fd396cf612bb8a linux-761654 linux-762107 linux-762230 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "Sublimates/EmitState Sublimates::lastEmitState")]
    [GameMember(GameUse.Mirrors, "System.Single SimTemperatureTransfer::OnGetTemperature(PrimaryElement)", "ede264fbd3c16edb linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Void UpdateBucketWithUpdater`1::Update(System.Single)", "ffb17e937154691b linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    public static class FastSublimation
    {
        public const string Name = "fast-sublimation";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        const int BlockedOnPressure = 1; // Sublimates.EmitState
        const int TagsChanged = -1582839653; // GameHashes.TagsChanged, as KPrefabID triggers it

        static readonly Func<Sublimates, int> emitState = CompileEmitState();
        /// Each watched item's index into `changed` (set when one of its tags changes, its Sealed
        /// tag in particular); indices of cleaned-up items are reused.
        static readonly Dictionary<Sublimates, int> watches = new Dictionary<Sublimates, int>();
        static readonly Stack<int> freeWatches = new Stack<int>();
        static bool[] changed = new bool[1024];
        static int nextWatch;
        static object installedFor;
        static long skipped, ran, wrong;

        /// What an item's last full update read.
        struct Row
        {
            public Sublimates Item;
            public Transform Transform;
            public SimTemperatureTransfer Temperature;
            public int Watch;
            public int Cell;
            public float LowTemperature, MaxMass;
            public bool Skippable;
        }

        /// The rows of one bucket, by position in its entry list.
        sealed class Mirror
        {
            public Row[] Rows = new Row[0];
        }

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Sublimates), "OnSpawn"), postfix: new HarmonyMethod(typeof(FastSublimation), nameof(Spawned)));
            harmony.Patch(AccessTools.Method(typeof(Sublimates), "OnCleanUp"), prefix: new HarmonyMethod(typeof(FastSublimation), nameof(CleanedUp)));
        }

        static Func<Sublimates, int> CompileEmitState()
        {
            var item = Linq.Expression.Parameter(typeof(Sublimates));
            var field = Linq.Expression.Field(item, AccessTools.Field(typeof(Sublimates), "lastEmitState"));
            return Linq.Expression.Lambda<Func<Sublimates, int>>(Linq.Expression.Convert(field, typeof(int)), item).Compile();
        }

        /// Sublimates are scheduled as they spawn: the first one's buckets get the batch function.
        [NameLookupReviewed("SimAndRenderScheduler.Entry.buckets, declared")]
        static void Spawned()
        {
            var scheduler = SimAndRenderScheduler.instance;
            if (ReferenceEquals(installedFor, scheduler)) return;
            var table = (IDictionary)scheduler.bucketTable;
            var entry = table["Sublimates SIM_200ms"];
            if (entry == null) return;
            // A new colony (the game makes a scheduler per game): the previous one's items were
            // destroyed with their scene, without OnCleanUp, so their watches go here.
            watches.Clear();
            freeWatches.Clear();
            Array.Clear(changed, 0, changed.Length);
            nextWatch = 0;
            var buckets = (StateMachineUpdater.BaseUpdateBucket[])AccessTools.Field(entry.GetType(), "buckets").GetValue(entry);
            foreach (var bucket in buckets)
            {
                var mirror = new Mirror();
                ((UpdateBucketWithUpdater<ISim200ms>)bucket).batch_update_delegate = (items, dt) => Batch(mirror, items, dt);
            }
            installedFor = scheduler;
        }

        static void CleanedUp(Sublimates __instance)
        {
            if (!watches.TryGetValue(__instance, out int watch)) return;
            watches.Remove(__instance);
            changed[watch] = true; // stale rows holding it are never skipped
            freeWatches.Push(watch);
        }

        /// UpdateBucketWithUpdater.Update's loop, with the items that would not change skipped.
        [MirrorsGame("System.Void UpdateBucketWithUpdater`1::Update(System.Single)")]
        static void Batch(Mirror mirror, List<Entry> items, float dt)
        {
            int count = items.Count;
            if (mirror.Rows.Length < count) Array.Resize(ref mirror.Rows, Math.Max(count, mirror.Rows.Length * 2));
            var rows = mirror.Rows;
            for (int i = 0; i < count; i++)
            {
                var entry = items[i];
                if (entry.updater == null) continue;
                float step = dt - entry.lastUpdateTime;
                entry.lastUpdateTime = 0f;
                items[i] = entry;
                ref var row = ref rows[i];
                // The row is this entry's only if it holds the same item (a reference compare).
                if (row.Skippable && ReferenceEquals(row.Item, entry.data) && !changed[row.Watch] && Unchanged(ref row))
                {
                    if (!verify)
                    {
                        skipped++;
                        continue;
                    }
                    Verify(row.Item, step);
                    continue;
                }
                ran++;
                entry.data.Sim200ms(step);
                Refresh(ref row, entry.data as Sublimates);
            }
        }

        /// Whether the item's full update would leave it as it is; if so, makes the temperature
        /// read's side effect.
        [MirrorsGame("System.Void Sublimates::Sim200ms(System.Single)")]
        [MirrorsGame("System.Single SimTemperatureTransfer::OnGetTemperature(PrimaryElement)")]
        static unsafe bool Unchanged(ref Row row)
        {
            if (Grid.PosToCell(row.Transform.position) != row.Cell) return false;
            var stt = row.Temperature;
            int handle = stt.simHandle;
            if (!Sim.IsValidHandle(handle)) return false;
            var chunk = Game.Instance.simData.elementChunks[Sim.GetHandleIndex(handle)];
            // The update's own tests: too cold, then room in the cell.
            if (chunk.temperature <= row.LowTemperature) return false;
            if (Grid.Mass[row.Cell] < row.MaxMass) return false;
            stt.deltaKJ = chunk.deltaKJ;
            return true;
        }

        /// What the item's update just read, for its next turn.
        static void Refresh(ref Row row, Sublimates item)
        {
            row.Skippable = false;
            row.Item = item;
            if (item == null) return;
            var pe = item.primaryElement;
            var stt = pe != null ? pe.sttOptimizationHook : null;
            var element = ElementLoader.FindElementByHash(item.info.sublimatedElement);
            // The temperature must come from the sim's chunk through SimTemperatureTransfer.
            if (stt == null || element == null || pe.getTemperatureCallback?.Method.DeclaringType != typeof(SimTemperatureTransfer)) return;
            int cell = Grid.PosToCell(item.transform.position);
            if (!Grid.IsValidCell(cell) || emitState(item) != BlockedOnPressure || item.HasTag(GameTags.Sealed)) return;
            if (!watches.TryGetValue(item, out int watch))
            {
                watch = freeWatches.Count > 0 ? freeWatches.Pop() : nextWatch++;
                if (watch >= changed.Length) Array.Resize(ref changed, changed.Length * 2);
                watches[item] = watch;
                int id = watch;
                item.Subscribe(TagsChanged, _ => changed[id] = true);
            }
            changed[watch] = false;
            row.Watch = watch;
            row.Transform = item.transform;
            row.Temperature = stt;
            row.Cell = cell;
            row.LowTemperature = element.lowTemp;
            row.MaxMass = item.info.maxDestinationMass;
            row.Skippable = true;
        }

        /// Test runs: the full update instead of the skip, which must change nothing.
        static void Verify(Sublimates item, float step)
        {
            int state = emitState(item);
            float accumulated = item.sublimatedMass, mass = item.primaryElement.Mass;
            item.Sim200ms(step);
            skipped++;
            if (emitState(item) != state || item.sublimatedMass != accumulated || item.primaryElement.Mass != mass)
                global::Debug.Log($"{Name}: a skipped update would have changed {item.name} ({++wrong})");
            if (skipped % 100000 == 0) global::Debug.Log($"{Name}: {skipped} updates skipped, {ran} run, {wrong} wrong skips");
        }
    }
}
