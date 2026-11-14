using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `direct-rail-bridges` (opt-in; see "What differs"). A conveyor bridge that
    /// moves a whole item calls SolidConduitFlow.RemovePickupable for its input cell and then
    /// AddPickupable for its output cell: the item is told it left storage and, at once, that it
    /// is stored again. In between it loses and regains the Stored tags (four TagsChanged
    /// rounds), gets a faller, partitioner entries, a place in the cell's item list and in the
    /// save list, and loses them all again: about 50 µs a move on a late-game colony, for a state
    /// that ends where it began (docs/research/rail-bridge-moves.md has the audit).
    ///
    /// This moves the item's handle from the input cell to the output cell instead, as the rails'
    /// own flow moves items between cells, and does by hand what the round trip leaves behind:
    /// - the cell the item is filed under (Pickupable.cachedCell and its stored-items partitioner
    ///   entry) becomes the cell it is at, and its rotation is reset;
    /// - the cell-change monitor is told to report its next cell whatever the last one was;
    /// - the pickupables-changed event of that cell fires (weight plates and other listeners);
    /// - an egg's incubation monitor is told about the un-store and the re-store, so it passes
    ///   through its incubating state as in the game;
    /// - the conveyor overlay's look is cleared and, with the overlay open, applied again;
    /// - SolidConduitFlow.SetContents places the item in the output cell.
    /// Only SolidConduitBridge.ConduitUpdate's whole-item branch is rewritten (its second
    /// RemovePickupable and AddPickupable calls); the partial move, the meter valve's callbacks
    /// and `dispensing` are the game's code.
    ///
    /// The game's own round trip runs for: an item whose kind was never discovered (the round
    /// trip can discover it: WorldInventory.OnAddedFetchable); critters, Duplicants, robots,
    /// items with a Storage of their own, interplanetary payloads and landers; any item with a
    /// component or state machine that is not the game's (a mod's may listen for the store
    /// events); an item not in the state the rails leave items in; an output cell without a
    /// rail or with an item in it.
    ///
    /// What differs (the item, the rails' contents, the heat exchange, food, eggs, germs, the
    /// valve's counts and the save file are the same):
    /// - the item keeps its rail handle, where the game gives it a new one; the order the
    ///   conveyor overlay recolours rail items in when it opens or closes follows the handles;
    /// - a Duplicant-reachable item is not, for an instant, listed as fetchable: the world
    ///   inventory's per-tag sets of fetchable items are not made for tags that have none yet
    ///   (empty sets; the amounts are the same), and the fetch manager's and the partitioner's
    ///   internal handles differ;
    /// - status items the round trip removes and adds again (marked for compost, an unassigned
    ///   booster's icon) keep their place in the item's list.
    ///
    /// With ONI_TWEAK_VERIFY=1 every other eligible move is made by the game's round trip, and
    /// both kinds are compared with what the audit says the round trip leaves (Snapshot); counts
    /// are logged every 4,096 moves. Off for the session while another Harmony owner patches a
    /// method this relies on, or while Forbid Items, No Manual Delivery or Oxygen Multiplayer is
    /// active (checked at the first move and every 256).
    [GameMember(GameUse.Transpiler, "System.Void SolidConduitBridge::ConduitUpdate(System.Single)", "4595db52463b16ae linux-744825 win-744825", "4f06992e5537c9f2 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023", Rewrites = 2)]
    [GameMember(GameUse.Mirrors, "Pickupable SolidConduitFlow::RemovePickupable(System.Int32)", "4722b432433214e3 linux-744825 win-744825", "f40f24e523e27f69 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void SolidConduitFlow::AddPickupable(System.Int32,Pickupable)", "d71b705bc5c98c45 linux-744825 win-744825", "bd5577d4a07edc90 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void Pickupable::OnStore(System.Object)", "a183938bb7de8c9d linux-744825 win-744825", "ad091b7831e4bd9f linux-761015 linux-761211 linux-761654 linux-762107 win-761015 win-761211 win-761211-nosim win-761654 win-762107", "cecb96c1760cd9cc linux-762230 linux-763023 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void Pickupable::RemovedFromStorage()", "88bde551f73ed7d5 linux-744825 win-744825", "60adb7ace599f57e linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void Pickupable::UpdateListeners(System.Boolean)", "17f5db6133246c58 linux-744825 win-744825", "75fd221485ce3010 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void Pickupable::OnTagsChanged(System.Object)", "30d8bfcb5c48a4d4 linux-744825 win-744825", "dd53b44a284582b4 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void Pickupable::RefreshStorageTags(System.Object)", "7167f1f4d63423f6 linux-744825 win-744825", "2a04802d368e1f88 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void IncubationMonitor/Instance::OnStore(System.Object)", "f6780643b2701253 linux-744825 win-744825", "9fed6e48fa254016 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Mirrors, "System.Void WorldInventory::OnAddedFetchable(System.Object)", "8218588e0245965e linux-744825 win-744825", "9e8893618bb6f9cb linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761211-nosim win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "System.Void FetchableMonitor/Instance::RegisterFetchable()")]
    [GameMember(GameUse.Uses, "System.Void DiscoveredResources::Discover(Tag,Tag)")]
    [GameMember(GameUse.Uses, "System.Void SolidConduitFlow::SetContents(System.Int32,SolidConduitFlow/ConduitContents)")]
    [GameMember(GameUse.Uses, "System.Void SolidConduitFlow::SetContents(System.Int32,Pickupable)")]
    [GameMember(GameUse.Uses, "System.Void SolidConduitFlow::Sim200ms(System.Single)")]
    public static class DirectRailBridges
    {
        public const string Name = "direct-rail-bridges";

        const int OnStore = 856640610;

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";

        /// Test infrastructure (PerfProbe's ab.txt): true moves as the game does, for an A/B in one run.
        public static bool Bypass = Environment.GetEnvironmentVariable("ONI_RAIL_BRIDGES_BYPASS") == "1";

        /// Mods whose handlers for the store events come with no patch to find: Forbid Items and
        /// No Manual Delivery listen on every item, Oxygen Multiplayer writes the rails itself.
        static readonly string[] standDownMods = { "PeterHan.ForbidItems", "Sanchozz.ONIMods.NoManualDelivery", "OniMp" };
        static bool off;
        static long calls;

        /// Counts since the start: moves made directly; whole-item moves left to the game because
        /// the item's kind is not eligible, is not discovered yet, or the item is not in the state
        /// the rails leave items in; and (verify) the game's round trips made for comparison.
        public static long Moved => handOff.Direct;
        public static long Ineligible, Undiscovered, Unusual, Sampled;
        /// Verify: moves compared, and those that differed from what the round trip leaves.
        public static long Checked, Wrong;

        static RailHandOff<Pickupable> handOff;

        /// By prefab: 1 never eligible, 2 eligible and discovered, 3 the same with an incubation
        /// monitor. Kinds not discovered yet are looked at again each time.
        static readonly Dictionary<Tag, byte> kinds = new Dictionary<Tag, byte>();
        static DiscoveredResources kindsFor;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(SolidConduitBridge), nameof(SolidConduitBridge.ConduitUpdate)),
                transpiler: new HarmonyMethod(typeof(DirectRailBridges), nameof(Transpiler)));
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps): the ones
        /// a direct move does not call, or whose effects it makes by hand.
        internal static IEnumerable<MethodBase> Guarded() => new MethodBase[]
        {
            AccessTools.Method(typeof(SolidConduitBridge), nameof(SolidConduitBridge.ConduitUpdate)),
            AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.RemovePickupable)),
            AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.AddPickupable)),
            AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.SetContents), new[] { typeof(int), typeof(SolidConduitFlow.ConduitContents) }),
            AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.SetContents), new[] { typeof(int), typeof(Pickupable) }),
            AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.Sim200ms)),
            AccessTools.Method(typeof(Pickupable), nameof(Pickupable.OnStore)),
            AccessTools.Method(typeof(Pickupable), nameof(Pickupable.RemovedFromStorage)),
            AccessTools.Method(typeof(Pickupable), nameof(Pickupable.UpdateListeners)),
            AccessTools.Method(typeof(Pickupable), nameof(Pickupable.OnTagsChanged)),
            AccessTools.Method(typeof(Pickupable), nameof(Pickupable.RefreshStorageTags)),
            AccessTools.Method(typeof(IncubationMonitor.Instance), nameof(IncubationMonitor.Instance.OnStore)),
            AccessTools.Method(typeof(WorldInventory), nameof(WorldInventory.OnAddedFetchable)),
            AccessTools.Method(typeof(FetchableMonitor.Instance), nameof(FetchableMonitor.Instance.RegisterFetchable)),
            AccessTools.Method(typeof(DiscoveredResources), nameof(DiscoveredResources.Discover), new[] { typeof(Tag), typeof(Tag) }),
        };

        /// The whole-item branch's `flowManager.RemovePickupable(inputCell)` and
        /// `flowManager.AddPickupable(outputCell, pickupable)` (the second call of each in the
        /// method; the first are the destroyed-item branch's and the partial move's) become
        /// Remove and Add.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var remove = AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.RemovePickupable));
            var add = AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.AddPickupable));
            var list = new List<CodeInstruction>(instructions);
            var removes = new List<int>();
            var adds = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(remove)) removes.Add(i);
                else if (list[i].Calls(add)) adds.Add(i);
            }
            if (removes.Count != 2 || adds.Count != 2 || !(removes[0] < adds[0] && adds[0] < removes[1] && removes[1] < adds[1]))
                throw new InvalidOperationException($"{Name}: SolidConduitBridge.ConduitUpdate is not as expected ({removes.Count} removes, {adds.Count} adds)");
            list[removes[1]].opcode = OpCodes.Call;
            list[removes[1]].operand = AccessTools.Method(typeof(DirectRailBridges), nameof(Remove));
            list[adds[1]].opcode = OpCodes.Call;
            list[adds[1]].operand = AccessTools.Method(typeof(DirectRailBridges), nameof(Add));
            TranspilerCounts.Report(Name, original, 2);
            return list;
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
                if (method == null) continue;
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var o in info.Owners)
                    if (!ModOverlapsCore.IsTweakOwner(o) && !ModOverlapsCore.IsOurs(o))
                    {
                        global::Debug.Log($"{Name}: off ({o} patches {method.DeclaringType?.Name}.{method.Name})");
                        return true;
                    }
            }
            return false;
        }

        /// In place of the whole-item branch's `flowManager.RemovePickupable(inputCell)`.
        static Pickupable Remove(SolidConduitFlow flow, int cell)
        {
            if (off || Bypass) return flow.RemovePickupable(cell);
            if ((calls++ & 0xFF) == 0)
            {
                if (off = Foreign()) return flow.RemovePickupable(cell);
                if (verify && (calls & 0xFFF) == 1 && calls > 1) Report();
            }
            var ops = new Ops(flow);
            return handOff.Remove(ref ops, cell);
        }

        /// In place of the whole-item branch's `flowManager.AddPickupable(outputCell, pickupable)`.
        static void Add(SolidConduitFlow flow, int cell, Pickupable pickupable)
        {
            if (!handOff.Pending)
            {
                flow.AddPickupable(cell, pickupable);
                if (sampling) Sample(flow, cell, pickupable);
                return;
            }
            var ops = new Ops(flow);
            handOff.Add(ref ops, cell, pickupable);
        }

        static void Report() =>
            global::Debug.Log($"{Name}: {Moved} direct moves, {Sampled} by the game for comparison, {Ineligible} of kinds not eligible, {Undiscovered} not discovered, "
                + $"{Unusual} in an unusual state, {handOff.Undone} handed back; {Checked} checked, {Wrong} wrong");

        /// The rails, for RailHandOff.
        struct Ops : IRailOps<Pickupable>
        {
            readonly SolidConduitFlow flow;

            public Ops(SolidConduitFlow flow) => this.flow = flow;

            static HandleVector<int>.Handle Handle(int handle) => new HandleVector<int>.Handle { index = handle };

            public bool HasConduit(int cell) => flow.grid[cell].conduitIdx != -1;

            public int HandleAt(int cell)
            {
                var handle = flow.grid[cell].contents.pickupableHandle;
                return handle.IsValid() ? handle.index : -1;
            }

            public Pickupable ItemOf(int handle)
            {
                var pickupable = flow.conveyorPickupables.GetData(Handle(handle)).pickupable;
                return pickupable ? pickupable : null;
            }

            public bool Eligible(Pickupable pickupable, int handle, int cell)
            {
                // off-screen-conveyors' prefix of RemovePickupable: an item it left behind is put
                // where the game's last move had it, before anything reads its cell. (The game's
                // removal runs the prefix again, which then finds nothing to place.)
                OffscreenConveyors.RemovingPickupable(flow, cell);
                if (!KindEligible(pickupable, out bool egg)) return false;
                var data = flow.conveyorPickupables.GetData(Handle(handle));
                if (!Usual(pickupable, data.kbac)) { Unusual++; return false; }
                if (verify)
                {
                    // Every other eligible move is the game's, for comparison.
                    before.Take(flow, pickupable, data.kbac);
                    if (((Moved + Sampled) & 1) == 1)
                    {
                        sampling = true;
                        Sampled++;
                        return false;
                    }
                }
                movingEgg = egg;
                return true;
            }

            [MirrorsGame("Pickupable SolidConduitFlow::RemovePickupable(System.Int32)")]
            public void Empty(int cell, int handle)
            {
                var data = flow.conveyorPickupables.GetData(Handle(handle));
                flow.RemoveFromGrid(flow.GetConduit(cell));
                flow.ClearOverlayVisualization(data.kbac);
            }

            public void Unstore(Pickupable pickupable, int handle)
            {
                pickupable.Trigger(OnStore, (object)BoxedBools.False);
                flow.freedHandles.Add(Handle(handle));
            }

            /// What the round trip leaves, in its order: Pickupable.OnStore(false) (RemovedFromStorage
            /// and, through the Stored tag's removal, OnTagsChanged and UpdateListeners(true)), the
            /// egg's handler, AddPickupable's overlay look, Pickupable.OnStore(true)'s event, the
            /// egg's handler, SetContents.
            [MirrorsGame("System.Void SolidConduitFlow::AddPickupable(System.Int32,Pickupable)")]
            [MirrorsGame("System.Void Pickupable::OnStore(System.Object)")]
            [MirrorsGame("System.Void Pickupable::RemovedFromStorage()")]
            [MirrorsGame("System.Void Pickupable::UpdateListeners(System.Boolean)")]
            [MirrorsGame("System.Void Pickupable::OnTagsChanged(System.Object)")]
            [MirrorsGame("System.Void Pickupable::RefreshStorageTags(System.Object)")]
            [MirrorsGame("System.Void IncubationMonitor/Instance::OnStore(System.Object)")]
            [MirrorsGame("System.Void WorldInventory::OnAddedFetchable(System.Object)")]
            public void Place(int cell, Pickupable pickupable, int handle)
            {
                var data = flow.conveyorPickupables.GetData(Handle(handle));
                var transform = pickupable.transform;
                var egg = movingEgg ? pickupable.gameObject.GetSMI<IncubationMonitor.Instance>() : null;
                pickupable.UpdateCachedCell(Grid.PosToCell(pickupable));
                var monitor = Singleton<CellChangeMonitor>.Instance;
                monitor.MarkDirty(transform);
                monitor.ClearLastKnownCell(transform);
                transform.rotation = Quaternion.identity;
                egg?.OnStore(BoxedBools.False);
                if (flow.viewingConduits) flow.ApplyOverlayVisualization(data.kbac);
                pickupable.NotifyChanged(pickupable.cachedCell);
                egg?.OnStore(BoxedBools.True);
                flow.SetContents(cell, new SolidConduitFlow.ConduitContents { pickupableHandle = Handle(handle) });
                if (verify) before.Compare(flow, cell, pickupable, data.kbac, direct: true, handle);
            }

            public Pickupable GameRemove(int cell) => flow.RemovePickupable(cell);

            public void GameAdd(int cell, Pickupable pickupable) => flow.AddPickupable(cell, pickupable);
        }

        static bool movingEgg, sampling;
        static Snapshot before;

        /// Verify: after the game's own round trip of an eligible item.
        static void Sample(SolidConduitFlow flow, int cell, Pickupable pickupable)
        {
            sampling = false;
            var handle = flow.grid[cell].contents.pickupableHandle;
            if (!pickupable || !handle.IsValid() || !ReferenceEquals(before.Item, pickupable)) return;
            before.Compare(flow, cell, pickupable, flow.conveyorPickupables.GetData(handle).kbac, direct: false, handle.index);
        }

        /// Whether items of this prefab can move directly, and are discovered (see the class's notes).
        static bool KindEligible(Pickupable pickupable, out bool egg)
        {
            egg = false;
            var discovered = DiscoveredResources.Instance;
            if (discovered == null) return false;
            if (!ReferenceEquals(discovered, kindsFor))
            {
                kinds.Clear();
                kindsFor = discovered;
            }
            var id = pickupable.KPrefabID;
            var tag = id.PrefabTag;
            if (kinds.TryGetValue(tag, out byte kind))
            {
                if (kind == 1) { Ineligible++; return false; }
                egg = kind == 3;
                return true;
            }
            if (!KindAudited(pickupable.gameObject, id, out egg))
            {
                kinds[tag] = 1;
                Ineligible++;
                return false;
            }
            // The round trip lists a reachable item as fetchable for an instant, and the world
            // inventory discovers a kind it has not listed before: a no-op once the kind is
            // discovered and in its category. Until then the game's round trip runs.
            if (!discovered.Discovered.Contains(tag)
                || !discovered.DiscoveredCategories.TryGetValue(DiscoveredResources.GetCategoryForEntity(id), out var category) || !category.Contains(tag))
            {
                Undiscovered++;
                return false;
            }
            kinds[tag] = (byte)(egg ? 3 : 2);
            return true;
        }

        static readonly Assembly gameAssembly = typeof(Pickupable).Assembly, firstpassAssembly = typeof(KPrefabID).Assembly;

        static bool GameType(Type type)
        {
            var assembly = type.Assembly;
            return assembly == gameAssembly || assembly == firstpassAssembly || assembly == typeof(Transform).Assembly
                || (assembly.GetName().Name ?? "").StartsWith("Unity", StringComparison.Ordinal);
        }

        /// A kind of item whose listeners for the store events and the Stored tags the audit
        /// covers: only the game's own components and state machines, and none of those that keep
        /// something from the round trip (a critter's wrangling, navigation and traps, an item's
        /// own storage, a payload's and a lander's states).
        static bool KindAudited(GameObject go, KPrefabID id, out bool egg)
        {
            egg = false;
            if (id.HasTag(GameTags.PickupableStorage) || id.HasTag(GameTags.Creature) || id.HasTag(GameTags.BaseMinion)) return false;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) return false;
                if (!GameType(component.GetType())) return false;
                if (component is Navigator || component is Baggable || component is Trappable || component is Storage || component is Brain || component is Health) return false;
                if (component is StateMachineController controller)
                    foreach (var smi in controller.stateMachines)
                    {
                        if (smi == null) continue;
                        if (!GameType(smi.GetType())) return false;
                        if (smi is RailGunPayload.StatesInstance || smi is CargoLander.StatesInstance || smi is DeathMonitor.Instance
                            || smi is DecompositionMonitor.Instance || smi is IncubatorMonitor.Instance || smi is FallMonitor.Instance) return false;
                        if (smi is IncubationMonitor.Instance) egg = true;
                    }
            }
            return true;
        }

        /// Whether the item is in the state AddPickupable leaves a rail item in, which the round
        /// trip would only restore: anything else is the game's to sort out.
        static bool Usual(Pickupable pickupable, KBatchedAnimController kbac)
        {
            var id = pickupable.KPrefabID;
            return (object)pickupable.storage == null && id.HasTag(GameTags.Stored) && id.HasTag(GameTags.StoredPrivate)
                && pickupable.storedPartitionerEntry.IsValid() && !pickupable.solidPartitionerEntry.IsValid() && !pickupable.worldPartitionerEntry.IsValid()
                && pickupable.objectLayerListItem == null && !pickupable.isEntombed && pickupable.entombedCell == -1
                && !pickupable.Clearable.isMarkedForClear
                && kbac != null && kbac.enabled;
        }

        /// Verify: an item before its move, and the comparison after it with what the audit says
        /// the game's round trip leaves. Run on direct moves and on the game's own round trips
        /// alike, so the audit's reading of the game is checked against the game.
        struct Snapshot
        {
            public Pickupable Item;
            int cellAt, tagCount, tagHash, layer, simHandle, diseaseCount, discovered, reservations, statusItems;
            byte diseaseIdx;
            float mass, temperature, units;
            SimHashes element;
            Color32 tint;
            Transform parent;
            string states;
            bool sttEnabled, viewing;

            static readonly StringBuilder text = new StringBuilder();

            static string States(GameObject go)
            {
                text.Clear();
                var controller = go.GetComponent<StateMachineController>();
                if (controller != null)
                    foreach (var smi in controller.stateMachines)
                        if (smi != null) text.Append(smi.GetType().FullName).Append('=').Append(smi.GetCurrentState()?.name).Append(';');
                var rottable = go.GetSMI<Rottable.Instance>();
                if (rottable != null) text.Append("rot=").Append(rottable.RotValue.ToString("R")).Append(';');
                var egg = go.GetSMI<IncubationMonitor.Instance>();
                if (egg != null)
                    text.Append("egg=").Append(egg.incubation?.value.ToString("R")).Append('/').Append(egg.viability?.value.ToString("R")).Append('/')
                        .Append(egg.sm.isSuppressed.Get(egg)).Append(egg.sm.inIncubator.Get(egg)).Append(egg.sm.incubatorIsActive.Get(egg)).Append(';');
                var light = go.GetComponent<Light2D>();
                if (light != null) text.Append("light=").Append(light.enabled).Append(';');
                return text.ToString();
            }

            static void Tags(KPrefabID id, out int count, out int hash)
            {
                count = 0;
                hash = 0;
                foreach (var tag in id.Tags)
                {
                    count++;
                    hash ^= tag.GetHashCode() * 31 + 7;
                }
            }

            public void Take(SolidConduitFlow flow, Pickupable pickupable, KBatchedAnimController kbac)
            {
                Item = pickupable;
                var go = pickupable.gameObject;
                var pe = pickupable.PrimaryElement;
                cellAt = Grid.PosToCell(pickupable);
                Tags(pickupable.KPrefabID, out tagCount, out tagHash);
                mass = pe.Mass;
                temperature = pe.Temperature;
                units = pe.Units;
                element = pe.ElementID;
                diseaseIdx = pe.DiseaseIdx;
                diseaseCount = pe.DiseaseCount;
                var stt = go.GetComponent<SimTemperatureTransfer>();
                simHandle = stt != null ? stt.SimHandle : int.MinValue;
                sttEnabled = stt != null && stt.enabled;
                layer = go.layer;
                tint = kbac.TintColour;
                parent = pickupable.transform.parent;
                states = States(go);
                viewing = flow.viewingConduits;
                discovered = DiscoveredResources.Instance.Discovered.Count;
                reservations = pickupable.reservations.Count;
                statusItems = StatusItems(go);
            }

            static int StatusItems(GameObject go)
            {
                var group = go.GetComponent<KSelectable>()?.GetStatusItemGroup();
                int n = 0;
                if (group != null)
                    foreach (var _ in group) n++;
                return n;
            }

            public void Compare(SolidConduitFlow flow, int cell, Pickupable pickupable, KBatchedAnimController kbac, bool direct, int handle)
            {
                Checked++;
                text.Clear();
                var wrong = new StringBuilder();
                void Differs(string what, object had, object has) => wrong.Append(' ').Append(what).Append(' ').Append(had).Append("->").Append(has);
                var go = pickupable.gameObject;
                var pe = pickupable.PrimaryElement;
                var id = pickupable.KPrefabID;
                Tags(id, out int count, out int hash);
                if (count != tagCount || hash != tagHash) Differs("tags", tagCount, count);
                if (!id.HasTag(GameTags.Stored) || !id.HasTag(GameTags.StoredPrivate)) Differs("stored-tags", true, false);
                if ((object)pickupable.storage != null) Differs("storage", null, pickupable.storage);
                if (pickupable.transform.parent != null) Differs("parent", parent, pickupable.transform.parent);
                var at = Grid.CellToPosCCC(cell, Grid.SceneLayer.SolidConduitContents);
                if (pickupable.transform.GetPosition() != at) Differs("position", at, pickupable.transform.GetPosition());
                if (pickupable.transform.rotation != Quaternion.identity) Differs("rotation", Quaternion.identity, pickupable.transform.rotation);
                if (pickupable.cachedCell != cellAt) Differs("cachedCell", cellAt, pickupable.cachedCell);
                if (pe.Mass != mass) Differs("mass", mass, pe.Mass);
                if (pe.Temperature != temperature) Differs("temperature", temperature, pe.Temperature);
                if (pe.Units != units) Differs("units", units, pe.Units);
                if (pe.ElementID != element) Differs("element", element, pe.ElementID);
                if (pe.DiseaseIdx != diseaseIdx || pe.DiseaseCount != diseaseCount) Differs("germs", diseaseCount, pe.DiseaseCount);
                var stt = go.GetComponent<SimTemperatureTransfer>();
                if ((stt != null ? stt.SimHandle : int.MinValue) != simHandle) Differs("sim-handle", simHandle, stt != null ? stt.SimHandle : int.MinValue);
                if ((stt != null && stt.enabled) != sttEnabled) Differs("sim-enabled", sttEnabled, !sttEnabled);
                // The overlay look: AddPickupable's, whatever the item had.
                int wantLayer = viewing ? flow.maskedOverlayLayer : Game.PickupableLayer;
                Color32 wantTint = viewing ? SolidConduitFlow.OverlayColour : SolidConduitFlow.NormalColour;
                if (go.layer != wantLayer) Differs("layer", wantLayer, go.layer);
                Color32 has = kbac.TintColour;
                if (has.r != wantTint.r || has.g != wantTint.g || has.b != wantTint.b || has.a != wantTint.a) Differs("tint", wantTint, has);
                if (!kbac.enabled) Differs("anim", true, false);
                if (kbac.sceneLayer != Grid.SceneLayer.SolidConduitContents) Differs("scene-layer", Grid.SceneLayer.SolidConduitContents, kbac.sceneLayer);
                var selectable = go.GetComponent<KSelectable>();
                if (selectable != null && selectable.selectable) Differs("selectable", false, true);
                var root = go.GetComponent<SaveLoadRoot>();
                if (root != null && root.registered) Differs("save-registered", false, true);
                if (!pickupable.storedPartitionerEntry.IsValid() || pickupable.solidPartitionerEntry.IsValid() || pickupable.worldPartitionerEntry.IsValid()
                    || pickupable.objectLayerListItem != null) Differs("listeners", "stored", "world");
                if (pickupable.isEntombed || pickupable.Clearable.isMarkedForClear) Differs("entombed-or-marked", false, true);
                // The round trip adds a faller and gravity and removes both within the call.
                if (GameComps.Fallers.Has(go) || GameComps.Gravities.Has(go)) Differs("faller", false, true);
                if (pickupable.reservations.Count != reservations) Differs("reservations", reservations, pickupable.reservations.Count);
                if (StatusItems(go) != statusItems) Differs("status-items", statusItems, StatusItems(go));
                string now = States(go);
                if (now != states) Differs("states", states, now);
                if (DiscoveredResources.Instance.Discovered.Count != discovered) Differs("discovered", discovered, DiscoveredResources.Instance.Discovered.Count);
                var contents = flow.grid[cell].contents.pickupableHandle;
                if (!contents.IsValid() || !ReferenceEquals(flow.conveyorPickupables.GetData(contents).pickupable, pickupable)) Differs("contents", pickupable.name, "other");
                if (direct && contents.index != handle) Differs("handle", handle, contents.index);
                Item = null;
                if (wrong.Length == 0) return;
                Wrong++;
                if (Wrong <= 20) global::Debug.Log($"{Name}: a {(direct ? "direct move" : "game round trip")} of {pickupable.name} differs:{wrong}");
            }
        }
    }
}
