using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-pipe-rebuilds` (U60). After each rebuild of the gas or liquid pipe
    /// networks, ConduitFlow.OnUtilityNetworksRebuilt triggers ConduitFlowRebuilt (2038377759) on
    /// Game with the rebuilt ConduitType. Since U60 every pipe segment subscribes to it on Game
    /// twice: Conduit (OnSpawn) for the "requires building intake" status item, and, on the pipes
    /// that have one, ConduitFlowKAnimVisualizer (OnSpawn) for the Pipe Flow overlay's arrows.
    /// So each rebuild calls every gas and liquid pipe's handlers. Conduit's ignores the type and
    /// runs RefreshInvalidNetworkStatusItem: its own network (gas or liquid) looked up, then
    /// LiquidPipeNetworkNoSink or GasPipeNetworkNoSink added (a network without sinks) or both
    /// removed. The visualizer's updates its directions only while the player looks at the Pipe
    /// Flow overlay of the rebuilt type.
    ///
    /// The tweak gives those subscriptions to one subscriber on Game: each OnSpawn's Subscribe of
    /// its own handler becomes a registration here (returning -1, so OnCleanUp skips its
    /// Unsubscribe; a prefix on OnCleanUp unregisters instead). On each trigger it runs the
    /// game's RefreshInvalidNetworkStatusItem for:
    /// - every conduit whose network manager is the rebuilt one;
    /// - every conduit of the other manager while that manager is dirty;
    /// - every conduit not refreshed since it spawned.
    /// A conduit of the other manager that it skips would come out unchanged in the original:
    /// the refresh reads only its manager's state (its network list, its grid's network index
    /// at the cell, the networks' sinks), which changes only in that manager's Update (a rebuild,
    /// after which every conduit of it was refreshed) and in RemoveFromNetworks, which marks the
    /// manager dirty first; a refresh it already made against the same state adds and removes
    /// nothing (AddStatusItem of an item present and RemoveStatusItem of one absent do nothing).
    /// Visualizers: UpdateDirections only on those whose conduit has the rebuilt type, only while
    /// the player looks at that type's Pipe Flow overlay, as their handler does. Each object's
    /// status items and anim parameters go through the game's own code, so they come out as the
    /// original's after every trigger; the order between objects changes, and nothing reads one
    /// object's from another's handler.
    ///
    /// Left to the game's own subscriptions: a Conduit or visualizer whose handler is another
    /// method (a subclass's override). While another Harmony owner patches a handler or
    /// RefreshInvalidNetworkStatusItem (checked at each colony's first trigger), every registered
    /// handler is called on every trigger, as the game's subscriptions would. A trigger whose data
    /// is not a gas or liquid ConduitType (a mod's) refreshes every conduit and calls every
    /// visualizer's handler.
    ///
    /// ONI_VERIFY_PIPE_NETWORK_STATUS=1 (or ONI_TWEAK_VERIFY=1): after each trigger, every
    /// registered conduit is checked against the original's refresh run as a shadow (would it
    /// add or remove an item now?), every skipped visualizer against its handler's condition,
    /// and every 64th trigger the registrations against the colony's spawned conduits;
    /// mismatches are logged, with a summary every 200 triggers.
    [GameMember(GameUse.Transpiler, "System.Void Conduit::OnSpawn()", "cebec800f1b22dc4 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Prefix, "System.Void Conduit::OnCleanUp()")]
    [GameMember(GameUse.Mirrors, "System.Void Conduit::OnConduitFlowRebuilt(System.Object)", "d039a0aa9bb0a24e linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Void Conduit::RefreshInvalidNetworkStatusItem()", "a9c9d12a3a538eca linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "System.Int32 Conduit::cell")]
    [GameMember(GameUse.Uses, "KSelectable Conduit::selectable")]
    [GameMember(GameUse.Transpiler, "System.Void ConduitFlowKAnimVisualizer::OnSpawn()", "9b3fbdcdb765f0b3 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230", Rewrites = 1)]
    [GameMember(GameUse.Prefix, "System.Void ConduitFlowKAnimVisualizer::OnCleanUp()")]
    [GameMember(GameUse.Mirrors, "System.Void ConduitFlowKAnimVisualizer::OnConduitFlowRebuilt(System.Object)", "85725c82285daefe linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "System.Void ConduitFlowKAnimVisualizer::UpdateDirections()")]
    [GameMember(GameUse.Uses, "System.Boolean ConduitFlowKAnimVisualizer::IsPlayerLookingAtOverlay(ConduitType)")]
    [GameMember(GameUse.Uses, "Conduit ConduitFlowKAnimVisualizer::conduit")]
    [GameMember(GameUse.Mirrors, "System.Void ConduitFlow::OnUtilityNetworksRebuilt(System.Collections.Generic.IList`1<UtilityNetwork>,System.Collections.Generic.ICollection`1<System.Int32>)", "54e5008df8102068 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Void UtilityNetworkManager`2::Update()", "4452c202e10d4017 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Mirrors, "System.Void UtilityNetworkManager`2::RemoveFromNetworks(System.Int32,System.Object,System.Boolean)", "b230a8939b2e26d5 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 win-761015 win-761211 win-761654 win-762107 win-762230")]
    [GameMember(GameUse.Uses, "System.Boolean UtilityNetworkManager`2::get_IsDirty()")]
    [GameMember(GameUse.Uses, "System.Int32 KMonoBehaviour::Subscribe(System.Int32,System.Action`1<System.Object>)")]
    public static class FastPipeRebuilds
    {
        public const string Name = "fast-pipe-rebuilds";
        const int Rebuilt = 2038377759;

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_VERIFY_PIPE_NETWORK_STATUS") == "1" || Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly bool stats = verify || Environment.GetEnvironmentVariable("ONI_PIPE_REBUILD_STATS") == "1";

        /// Test infrastructure (ONI_FRAME_STATS_AB): true calls every registered handler on every
        /// trigger, the game's work through one subscription.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_PIPE_REBUILDS_BYPASS") == "1";

        static MethodInfo conduitHandler, visualizerHandler, conduitRefresh;

        sealed class Entry
        {
            public KMonoBehaviour Owner;
            public Action<object> Handler;
            public int Group, Index;
            public bool Fresh, Live;
            /// The trigger (Triggers) that last ran its handler or update, for verify mode.
            public long Called;
        }

        sealed class ByReference : IEqualityComparer<KMonoBehaviour>
        {
            public bool Equals(KMonoBehaviour a, KMonoBehaviour b) => ReferenceEquals(a, b);
            public int GetHashCode(KMonoBehaviour o) => RuntimeHelpers.GetHashCode(o);
        }

        static Game game;
        static readonly Dictionary<KMonoBehaviour, Entry> entries = new Dictionary<KMonoBehaviour, Entry>(new ByReference());
        /// Conduits by network manager (0 gas, 1 liquid, as Conduit.GetNetworkManager picks), and
        /// the visualizers.
        static readonly List<Entry>[] conduits = { new List<Entry>(), new List<Entry>() };
        static readonly List<Entry> visualizers = new List<Entry>();
        /// Conduits not refreshed since they registered.
        static readonly List<Entry> fresh = new List<Entry>();
        static bool checkedPatches, foreignConduit, foreignVisualizer;

        static long lastSummary;
        public static long Triggers, Refreshed, WouldRefresh, Updated, WouldCall, Wrong;

        public static void Install(Harmony harmony)
        {
            conduitHandler = AccessTools.Method(typeof(Conduit), "OnConduitFlowRebuilt");
            conduitRefresh = AccessTools.Method(typeof(Conduit), "RefreshInvalidNetworkStatusItem");
            visualizerHandler = AccessTools.Method(typeof(ConduitFlowKAnimVisualizer), "OnConduitFlowRebuilt");
            var transpiler = new HarmonyMethod(typeof(FastPipeRebuilds), nameof(Transpiler));
            harmony.Patch(AccessTools.Method(typeof(Conduit), "OnSpawn"), transpiler: transpiler);
            harmony.Patch(AccessTools.Method(typeof(ConduitFlowKAnimVisualizer), "OnSpawn"), transpiler: transpiler);
            var cleanUp = new HarmonyMethod(typeof(FastPipeRebuilds), nameof(CleanUp)) { priority = Priority.Last };
            harmony.Patch(AccessTools.Method(typeof(Conduit), "OnCleanUp"), prefix: cleanUp);
            harmony.Patch(AccessTools.Method(typeof(ConduitFlowKAnimVisualizer), "OnCleanUp"), prefix: cleanUp);
        }

        /// The methods whose patching by another owner makes the tweak call every handler (ModOverlaps).
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(Conduit), "OnConduitFlowRebuilt"),
            AccessTools.Method(typeof(Conduit), "RefreshInvalidNetworkStatusItem"),
            AccessTools.Method(typeof(ConduitFlowKAnimVisualizer), "OnConduitFlowRebuilt"),
        };

        /// OnSpawn's `Game.Instance.Subscribe(2038377759, handler)` becomes Subscribe(game, hash, handler).
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);
            var subscribe = AccessTools.Method(typeof(KMonoBehaviour), nameof(KMonoBehaviour.Subscribe), new[] { typeof(int), typeof(Action<object>) });
            int hash = list.FindIndex(i => i.LoadsConstant(Rebuilt));
            int at = hash < 0 ? -1 : list.FindIndex(hash, i => i.Calls(subscribe));
            if (at < 0 || list.FindIndex(at + 1, i => i.LoadsConstant(Rebuilt)) >= 0)
                throw new InvalidOperationException($"{Name}: {original.DeclaringType?.Name}.OnSpawn is not as expected");
            list[at].opcode = OpCodes.Call;
            list[at].operand = AccessTools.Method(typeof(FastPipeRebuilds), nameof(Subscribe));
            TranspilerCounts.Report(Name, original, 1);
            return list;
        }

        /// In place of KMonoBehaviour.Subscribe(hash, handler) on Game in Conduit's and the
        /// visualizer's OnSpawn: the handle (-1 when registered here).
        static int Subscribe(KMonoBehaviour target, int hash, Action<object> handler)
        {
            var owner = handler?.Target as KMonoBehaviour;
            bool isConduit = owner is Conduit && handler.Method.MethodHandle == conduitHandler.MethodHandle;
            bool isVisualizer = owner is ConduitFlowKAnimVisualizer && handler.Method.MethodHandle == visualizerHandler.MethodHandle;
            if (hash != Rebuilt || !(target is Game g) || !(isConduit || isVisualizer) || entries.ContainsKey(owner))
                return target.Subscribe(hash, handler);
            if (!ReferenceEquals(g, game)) Start(g);
            var entry = new Entry { Owner = owner, Handler = handler, Live = true };
            if (isConduit)
            {
                entry.Group = ((Conduit)owner).type == ConduitType.Gas ? 0 : 1;
                var list = conduits[entry.Group];
                entry.Index = list.Count;
                list.Add(entry);
                entry.Fresh = true;
                fresh.Add(entry);
            }
            else
            {
                entry.Group = -1;
                entry.Index = visualizers.Count;
                visualizers.Add(entry);
            }
            entries.Add(owner, entry);
            return -1;
        }

        /// A colony's first registration: one subscription on its Game, and nothing kept from the last.
        static void Start(Game g)
        {
            foreach (var e in entries.Values) e.Live = false;
            entries.Clear();
            conduits[0].Clear();
            conduits[1].Clear();
            visualizers.Clear();
            fresh.Clear();
            checkedPatches = false;
            game = g;
            g.Subscribe(Rebuilt, OnRebuilt);
        }

        /// Conduit.OnCleanUp, ConduitFlowKAnimVisualizer.OnCleanUp: where their Unsubscribe was.
        static void CleanUp(KMonoBehaviour __instance, bool __runOriginal)
        {
            if (!__runOriginal || !entries.TryGetValue(__instance, out var entry)) return;
            entries.Remove(__instance);
            entry.Live = false;
            var list = entry.Group < 0 ? visualizers : conduits[entry.Group];
            var last = list[list.Count - 1];
            list[entry.Index] = last;
            last.Index = entry.Index;
            list.RemoveAt(list.Count - 1);
        }

        static bool Foreign(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            foreach (var o in info.Owners)
                if (!ModOverlapsCore.IsTweakOwner(o) && !ModOverlapsCore.IsOurs(o))
                {
                    global::Debug.Log($"{Name}: {o} patches {method.DeclaringType?.Name}.{method.Name}: every pipe's handler runs on each rebuild");
                    return true;
                }
            return false;
        }

        [MirrorsGame("System.Void ConduitFlow::OnUtilityNetworksRebuilt(System.Collections.Generic.IList`1<UtilityNetwork>,System.Collections.Generic.ICollection`1<System.Int32>)")]
        static void OnRebuilt(object data)
        {
            if (!checkedPatches)
            {
                checkedPatches = true;
                foreignConduit = Foreign(conduitHandler) | Foreign(conduitRefresh);
                foreignVisualizer = Foreign(visualizerHandler);
            }
            Triggers++;
            int rebuilt = -1;
            ConduitType type = ConduitType.None;
            if (data is Boxed<ConduitType> boxed)
            {
                type = boxed.value;
                rebuilt = type == ConduitType.Gas ? 0 : type == ConduitType.Liquid ? 1 : -1;
            }
            bool all = Bypass || rebuilt < 0;
            Conduits(data, rebuilt, all || foreignConduit);
            Visualizers(data, type, all || foreignVisualizer);
            if (verify) Verify(type, all || foreignVisualizer || !(data is Boxed<ConduitType>));
            if (stats && (Triggers % 200 == 0 || System.Diagnostics.Stopwatch.GetTimestamp() - lastSummary > 30 * System.Diagnostics.Stopwatch.Frequency))
            {
                lastSummary = System.Diagnostics.Stopwatch.GetTimestamp();
                global::Debug.Log($"{Name}: {Triggers} rebuilds, {Refreshed} of {WouldRefresh} pipe refreshes made, {Updated} of {WouldCall} flow-arrow handlers run"
                    + (verify ? $", {Wrong} mismatches" : ""));
            }
        }

        [MirrorsGame("System.Void Conduit::OnConduitFlowRebuilt(System.Object)")]
        [MirrorsGame("System.Void Conduit::RefreshInvalidNetworkStatusItem()")]
        [MirrorsGame("System.Void UtilityNetworkManager`2::Update()")]
        [MirrorsGame("System.Void UtilityNetworkManager`2::RemoveFromNetworks(System.Int32,System.Object,System.Boolean)")]
        static void Conduits(object data, int rebuilt, bool every)
        {
            int count = conduits[0].Count + conduits[1].Count;
            WouldRefresh += count;
            if (every)
            {
                // The handlers as the game's subscriptions call them (a copy: one may spawn or clean up pipes).
                foreach (var e in Snapshot(conduits[0], conduits[1]))
                    if (e.Live)
                    {
                        e.Fresh = false;
                        e.Handler(data);
                        Refreshed++;
                    }
                fresh.Clear();
                return;
            }
            var managers = new IUtilityNetworkMgr[] { game.gasConduitSystem, game.liquidConduitSystem };
            for (int g = 0; g < 2; g++)
            {
                if (g != rebuilt && !IsDirty(managers[g])) continue;
                var list = conduits[g];
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    e.Fresh = false;
                    ((Conduit)e.Owner).RefreshInvalidNetworkStatusItem();
                    Refreshed++;
                }
            }
            for (int i = 0; i < fresh.Count; i++)
            {
                var e = fresh[i];
                if (!e.Live || !e.Fresh) continue;
                e.Fresh = false;
                ((Conduit)e.Owner).RefreshInvalidNetworkStatusItem();
                Refreshed++;
            }
            fresh.Clear();
        }

        static bool IsDirty(IUtilityNetworkMgr manager) => !(manager is UtilityNetworkManager<FlowUtilityNetwork, Vent> m) || m.IsDirty;

        [MirrorsGame("System.Void ConduitFlowKAnimVisualizer::OnConduitFlowRebuilt(System.Object)")]
        static void Visualizers(object data, ConduitType type, bool every)
        {
            WouldCall += visualizers.Count;
            if (visualizers.Count == 0) return;
            if (every || !(data is Boxed<ConduitType>))
            {
                foreach (var e in Snapshot(visualizers, null))
                    if (e.Live)
                    {
                        e.Called = Triggers;
                        e.Handler(data);
                        Updated++;
                    }
                return;
            }
            // The handler's condition: looking at the overlay of its conduit's type, and that type rebuilt.
            if (!ConduitFlowKAnimVisualizer.IsPlayerLookingAtOverlay(type)) return;
            for (int i = 0; i < visualizers.Count; i++)
            {
                var e = visualizers[i];
                var v = (ConduitFlowKAnimVisualizer)e.Owner;
                if (v.conduit.type != type) continue;
                e.Called = Triggers;
                v.UpdateDirections();
                Updated++;
            }
        }

        static List<Entry> Snapshot(List<Entry> a, List<Entry> b)
        {
            var copy = new List<Entry>(a.Count + (b?.Count ?? 0));
            copy.AddRange(a);
            if (b != null) copy.AddRange(b);
            return copy;
        }

        // -- verify mode

        static void Verify(ConduitType type, bool every)
        {
            var items = Db.Get().BuildingStatusItems;
            for (int g = 0; g < 2; g++)
                foreach (var e in conduits[g])
                {
                    var c = (Conduit)e.Owner;
                    var group = c.selectable?.GetStatusItemGroup();
                    if (group == null || c.gameObject == null) continue;
                    // The original's refresh, as a shadow: what it would add or remove now.
                    var network = c.GetNetworkManager().GetNetworkForCell(c.cell) as FlowUtilityNetwork;
                    bool noSink = network != null && !network.HasSinks;
                    bool liquid = group.HasStatusItem(items.LiquidPipeNetworkNoSink), gas = group.HasStatusItem(items.GasPipeNetworkNoSink);
                    bool changes = noSink
                        ? (c.type == ConduitType.Liquid && !liquid) || (c.type == ConduitType.Gas && !gas)
                        : liquid || gas;
                    if (changes)
                        global::Debug.Log($"{Name}: mismatch ({++Wrong}): {c.type} pipe at cell {c.cell}: network {(network == null ? "none" : network.HasSinks ? "with sinks" : "without sinks")}, "
                            + $"liquid item {liquid}, gas item {gas}, after a {type} rebuild");
                }
            foreach (var e in visualizers)
            {
                var v = (ConduitFlowKAnimVisualizer)e.Owner;
                if (v.conduit == null) continue;
                // Its handler's own condition, against what the pass above did.
                bool calls = every || ConduitFlowKAnimVisualizer.IsPlayerLookingAtOverlay(v.conduit.type) && type == v.conduit.type;
                if (calls != (e.Called == Triggers))
                    global::Debug.Log($"{Name}: mismatch ({++Wrong}): the flow-arrow handler at cell {v.cell} was {(calls ? "not " : "")}run after a {type} rebuild");
            }
            if (Triggers % 64 == 1) Census();
        }

        /// The registrations against the colony's spawned conduits and visualizers.
        static void Census()
        {
            int missing = 0, stale = 0;
            var seen = new HashSet<KMonoBehaviour>(new ByReference());
            foreach (var c in UnityEngine.Object.FindObjectsByType<Conduit>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None))
            {
                if (!c.isSpawned || c.GetType() != typeof(Conduit) && c.GetType().GetMethod("OnConduitFlowRebuilt", BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType != typeof(Conduit)) continue;
                seen.Add(c);
                if (!entries.ContainsKey(c)) missing++;
            }
            foreach (var v in UnityEngine.Object.FindObjectsByType<ConduitFlowKAnimVisualizer>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None))
            {
                if (!v.isSpawned) continue;
                seen.Add(v);
                if (!entries.ContainsKey(v)) missing++;
            }
            foreach (var owner in entries.Keys)
                if (!seen.Contains(owner)) stale++;
            if (missing + stale > 0)
            {
                Wrong += missing + stale;
                global::Debug.Log($"{Name}: mismatch ({Wrong}): {missing} spawned pipes not registered, {stale} registered pipes not spawned");
            }
            else global::Debug.Log($"{Name}: census: {conduits[0].Count} gas and {conduits[1].Count} liquid pipes, {visualizers.Count} flow-arrow handlers registered");
        }
    }
}
