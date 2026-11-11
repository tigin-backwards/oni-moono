using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `compact-nav-links` (NavGridLinks, NavGridLinksRewrite): enabled before any
    /// game assembly loads, as the rewrite runs while they load; the store is used or not from
    /// the first nav grid on (Decide), for the whole session.
    [GameMember(GameUse.Uses, "NavGrid/Link[] NavGrid::Links")]
    [GameMember(GameUse.Uses, "System.String NavGrid::id")]
    [GameMember(GameUse.Postfix, "System.Void Pathfinding::FlushNavGridsOnLoad()")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<NavGrid> Pathfinding::GetNavGrids()")]
    [GameMember(GameUse.Uses, "Pathfinding Pathfinding::Instance")]
    [GameMember(GameUse.Postfix, "System.Void Pathfinding::UpdateNavGrids(System.Boolean)")]
    [GameMember(GameUse.Uses, "System.Int32 NavGrid::get_maxLinksPerCell()")]
    [GameMember(GameUse.Uses, "KMod.LoadedModData KMod.DLLLoader::LoadDLLs(KMod.Mod,System.String,System.String,System.Boolean)")]
    internal static class CompactNavLinks
    {
        public const string Name = GameTweakNames.CompactNavLinks;

        /// The transpilers of methods that touch links that were read against the rewrite
        /// (docs/research/navgrid-links-store.md §3): the patched method and the transpiler's
        /// class. Any other transpiler of such a method makes the session flat.
        internal static readonly (string Target, string Transpiler, string Mod)[] reviewed =
        [
            ("PathProber.Run", "PeterHan.FastTrack.PathPatches.PathProber_RunAsync_Patch", "Fast Track"),
            ("PathFinder.AddPotentials", "HazardPathing.HazardCostTranspiler", "Hazard Pathing"),
            ("TravelTube.OnDirtyNavCellUpdated", "TransitTubeOverlay.Patches.OverlayPatches+TravelTube_OnDirtyNavCellUpdated_Patch", "Transit Tube Overlay"),
            ("TravelTube.OnDirtyNavCellUpdated", "TravelTubesExpanded.Patches+TravelTube_OnDirtyNavCellUpdated", "Travel Tubes Expanded"),
        ];

        static readonly string[] hotMethods = ["PathFinder:AddPotentials", "PathFinder:ValidatePath", "TravelTube:OnDirtyNavCellUpdated"];

        /// Program, before the game's assemblies load: they and mods are rewritten, and the
        /// first nav grid decides. (OniMoono.GameTweaks is not rewritten: path-probe-repair, the
        /// one tweak there that reads links, reads a store through NavGridLinks.Load.)
        public static void Enable()
        {
            NavGridLinksRewrite.Enabled = true;
            NavGridLinks.Decide = Decide;
        }

        /// GameTweaks.Install: the load's summary (and verify mode's comparisons) after the grids
        /// are built.
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method("Pathfinding:FlushNavGridsOnLoad") ?? throw new MissingMethodException("Pathfinding", "FlushNavGridsOnLoad"),
                postfix: new HarmonyMethod(typeof(CompactNavLinks), nameof(Loaded)));
            if (NavGridLinks.Verify)
                harmony.Patch(AccessTools.Method("Pathfinding:UpdateNavGrids") ?? throw new MissingMethodException("Pathfinding", "UpdateNavGrids"),
                    postfix: new HarmonyMethod(typeof(CompactNavLinks), nameof(Updated)));
            var constructor = AccessTools.TypeByName("NavGrid")?.GetConstructors().FirstOrDefault();
            if (constructor == null || !Calls(constructor, nameof(NavGridLinks.New)))
                EngineLog.Engine($"game tweak {Name}: flat arrays: the game's NavGrid constructor is not rewritten (the log of its first load says why)");
        }

        /// Whether the method's IL calls the NavGridLinks helper of that name.
        static bool Calls(MethodBase method, string helper)
        {
            try
            {
                return PatchProcessor.GetOriginalInstructions(method).Any(i => i.operand is MethodInfo { Name: var n, DeclaringType: var t } && n == helper && t == typeof(NavGridLinks));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }

        /// At the first NavGrid: null for the store, else why the session runs flat. Logged.
        static string? Decide()
        {
            string? reason = Reason(out var notes);
            EngineLog.Engine(reason == null
                ? $"game tweak {Name}: compact stores" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "")
                : $"game tweak {Name}: flat arrays for this session: {reason}");
            return reason;
        }

        [NameLookupReviewed("The methods in `hotMethods`, for the log's note; one missing is left out of it")]
        static string? Reason(out List<string> notes)
        {
            notes = new List<string>();
            // Its checks against the game (TweakInstall): off, the session keeps the game's flat arrays.
            if (TweakInstall.Unavailable.TryGetValue(Name, out var off)) return off;
            lock (NavGridLinksRewrite.Problems)
                if (NavGridLinksRewrite.Problems.Count > 0) return "a mod's access to the links cannot be rewritten: " + NavGridLinksRewrite.Problems[0];
            var linkType = AccessTools.TypeByName("NavGrid+Link");
            if (linkType == null) return "the game has no NavGrid.Link";
            // Every assembly that names the link type went through the rewrite.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var name = assembly.GetName().Name ?? "";
                if (name == "Assembly-CSharp" || name.StartsWith("OniMoono", StringComparison.Ordinal) || AssemblyPolicy.IsFrameworkAssembly(name)) continue;
                bool rewritten;
                lock (NavGridLinksRewrite.Rewritten) rewritten = NavGridLinksRewrite.Rewritten.Contains(name);
                if (!rewritten && NamesLink(assembly)) return $"{name} names NavGrid.Link but was loaded without the rewrite";
            }
            // Transpilers of methods that touch links: only reviewed ones.
            var used = new List<string>();
            foreach (var method in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null || info.Transpilers.Count == 0 || !Touches(method, linkType)) continue;
                string target = $"{method.DeclaringType?.Name}.{method.Name}";
                foreach (var patch in info.Transpilers)
                {
                    string transpiler = patch.PatchMethod.DeclaringType?.FullName ?? "";
                    var review = reviewed.FirstOrDefault(r => r.Target == target && r.Transpiler == transpiler);
                    if (review.Mod == null) return $"{transpiler}.{patch.PatchMethod.Name} ({patch.owner}) transpiles {target}, which touches the links, and was not reviewed";
                    used.Add($"{review.Mod}'s transpiler of {target}");
                }
            }
            var walks = hotMethods.Where(m => AccessTools.Method(m) is { } hot && Calls(hot, nameof(NavGridLinks.Walk))).Select(m => m.Replace(':', '.')).ToList();
            notes.Add(walks.Count == hotMethods.Length ? "the 3 hot walks read through a cursor"
                : $"hot walks through a cursor: {(walks.Count == 0 ? "none" : string.Join(", ", walks))}; the others read through the generic helpers");
            if (used.Count > 0) notes.Add("reviewed: " + string.Join(", ", used));
            return null;
        }

        /// Whether a loaded assembly's metadata references or defines NavGrid's Link type.
        static unsafe bool NamesLink(Assembly assembly)
        {
            try
            {
                if (!assembly.TryGetRawMetadata(out byte* blob, out int length)) return true;
                var reader = new MetadataReader(blob, length);
                foreach (var handle in reader.TypeReferences)
                {
                    var type = reader.GetTypeReference(handle);
                    if (!reader.StringComparer.Equals(type.Name, "Link") || type.ResolutionScope.Kind != HandleKind.TypeReference) continue;
                    if (reader.StringComparer.Equals(reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope).Name, "NavGrid")) return true;
                }
                return false;
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
            {
                return true;
            }
        }

        /// Whether the method's signature, locals or IL name the link type.
        static bool Touches(MethodBase method, Type link)
        {
            bool Names(Type? type)
            {
                while (type != null)
                {
                    if (type == link) return true;
                    if (type.IsGenericType && type.GetGenericArguments().Any(Names)) return true;
                    type = type.HasElementType ? type.GetElementType() : null;
                }
                return false;
            }
            if (method is MethodInfo info && Names(info.ReturnType)) return true;
            if (method.GetParameters().Any(p => Names(p.ParameterType))) return true;
            try
            {
                if (method.GetMethodBody()?.LocalVariables.Any(v => Names(v.LocalType)) == true) return true;
                foreach (var i in PatchProcessor.GetOriginalInstructions(method))
                    switch (i.operand)
                    {
                        case Type t when Names(t): return true;
                        case FieldInfo f when Names(f.FieldType) || Names(f.DeclaringType): return true;
                        case MethodBase m when Names(m.DeclaringType) || m.GetParameters().Any(p => Names(p.ParameterType)) || (m is MethodInfo mi && Names(mi.ReturnType)):
                            return true;
                    }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return true;
            }
            return false;
        }

        /// --check-types: the hot walks in the loaded game differ from the game's file only in
        /// their walk reads (`ldelem.any` → `ldloca` of the cursor and NavGridLinks.Walk, one
        /// local more), every other instruction, short branches included, as it was; and the
        /// reviewed transpilers of those methods apply to them (when the mods are at hand:
        /// ONI_NAVLINKS_MODS, their DLLs separated by ':').
        internal static string SelfCheck()
        {
            string result = NavGridLinksRewrite.Enabled ? WalksOnly() : "off: the game's IL as it is";
            var mods = (Environment.GetEnvironmentVariable("ONI_NAVLINKS_MODS") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (mods.Length == 0) return result + "; reviewed transpilers not applied (ONI_NAVLINKS_MODS unset)";
            return result + "; " + ApplyReviewed(mods);
        }

        static string WalksOnly()
        {
            var path = System.IO.Path.Combine(AssemblyPolicy.ReferenceDirectories[0], "Assembly-CSharp.dll");
            var image = MonoConversions.AdjustGame(MonoFieldSemantics.Adjust(path) ?? System.IO.File.ReadAllBytes(path), path)
                        ?? throw new InvalidOperationException("Assembly-CSharp is not rewritten");
            using var before = Mono.Cecil.ModuleDefinition.ReadModule(path);
            using var after = Mono.Cecil.ModuleDefinition.ReadModule(new System.IO.MemoryStream(image));
            var report = new List<string>();
            foreach (var (typeName, methodName, walks, _) in NavGridLinksRewrite.HotMethods)
            {
                var original = before.GetType(typeName).Methods.Single(m => m.Name == methodName).Body;
                var rewritten = after.GetType(typeName).Methods.Single(m => m.Name == methodName).Body;
                string where = $"{typeName}.{methodName}";
                if (rewritten.Variables.Count != original.Variables.Count + 1) throw new InvalidOperationException($"{where}: {rewritten.Variables.Count} locals, {original.Variables.Count} before");
                for (int v = 0; v < original.Variables.Count; v++)
                    if (rewritten.Variables[v].VariableType.FullName != original.Variables[v].VariableType.FullName) throw new InvalidOperationException($"{where}: local {v} changed");
                var map = new Dictionary<Mono.Cecil.Cil.Instruction, Mono.Cecil.Cil.Instruction>();
                int j = 0, replaced = 0;
                var a = rewritten.Instructions;
                foreach (var o in original.Instructions)
                {
                    if (o.OpCode.Code == Mono.Cecil.Cil.Code.Ldelem_Any && a[j].OpCode.Code is Mono.Cecil.Cil.Code.Ldloca_S or Mono.Cecil.Cil.Code.Ldloca)
                    {
                        if (a[j + 1].Operand is not Mono.Cecil.MethodReference { Name: nameof(NavGridLinks.Walk) }) throw new InvalidOperationException($"{where}: IL_{o.Offset:x4}: {a[j]} {a[j + 1]}");
                        map[o] = a[j];
                        j += 2;
                        replaced++;
                        continue;
                    }
                    var r = a[j++];
                    map[o] = r;
                    if (r.OpCode != o.OpCode || (o.Operand is not Mono.Cecil.Cil.Instruction && o.Operand is not Mono.Cecil.Cil.Instruction[] && $"{o.Operand}" != $"{r.Operand}"))
                        throw new InvalidOperationException($"{where}: IL_{o.Offset:x4} {o} became {r}");
                }
                if (j != a.Count) throw new InvalidOperationException($"{where}: {a.Count - j} instruction(s) added");
                foreach (var o in original.Instructions)
                    if (o.Operand is Mono.Cecil.Cil.Instruction target && map[o].Operand != map[target]) throw new InvalidOperationException($"{where}: IL_{o.Offset:x4} branches elsewhere");
                if (replaced != walks) throw new InvalidOperationException($"{where}: {replaced} walk reads replaced, {walks} expected");
                report.Add($"{where} ({replaced})");
            }
            return $"only the walk reads differ in {string.Join(", ", report)}";
        }

        /// The reviewed transpilers applied to the loaded methods: what each did.
        [NameLookupReviewed("Mods' transpiler types and the targets in `reviewed`, by those names; one missing is skipped")]
        static string ApplyReviewed(string[] mods)
        {
            ModAccess.Install();
            var applied = new List<string>();
            var harmony = new Harmony("OniMoono.Host.CompactNavLinks.Check");
            try
            {
                foreach (var file in mods)
                {
                    var mod = ModAccess.LoadFrom(file);
                    foreach (var (target, transpilerType, name) in reviewed)
                    {
                        var type = mod.GetType(transpilerType);
                        if (type == null) continue;
                        var transpiler = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.ReturnType == typeof(IEnumerable<CodeInstruction>))
                            ?? throw new InvalidOperationException($"{name}: no transpiler in {transpilerType}");
                        var method = TargetOf(target);
                        try
                        {
                            harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
                        }
                        catch (Exception e)
                        {
                            var cause = e;
                            while (cause.InnerException != null) cause = cause.InnerException;
                            applied.Add($"{name} on {target} throws ({cause.Message})");
                            continue;
                        }
                        var now = PatchProcessor.GetCurrentInstructions(method);
                        // What each one inserts, and the walk reads still there.
                        bool inserted = name switch
                        {
                            "Hazard Pathing" => type.GetProperty("Ready", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is true,
                            "Transit Tube Overlay" => now.Any(i => i.operand is MethodInfo { Name: "SetIsValidExitOnly" }),
                            "Travel Tubes Expanded" => now.Any(i => i.operand is MethodInfo { Name: "DirectionFromToCell" } m && m.DeclaringType?.Assembly == mod),
                            "Fast Track" => now.Any(i => i.operand is MethodInfo { Name: "BeginUpdate" } m && m.DeclaringType?.Assembly == mod),
                            _ => false,
                        };
                        if (!inserted) applied.Add($"{name} on {target} changed nothing");
                        int walkReads = now.Count(i => i.operand is MethodInfo { Name: nameof(NavGridLinks.Walk) });
                        var hot = NavGridLinksRewrite.HotMethods.FirstOrDefault(h => $"{h.Type}.{h.Method}" == target);
                        if (NavGridLinksRewrite.Enabled && hot.Type != null && walkReads != hot.Walks) throw new InvalidOperationException($"{name}'s transpiler of {target}: {walkReads} walk reads left");
                        if (inserted) applied.Add($"{name} on {target} applied");
                        harmony.Unpatch(method, HarmonyPatchType.Transpiler, harmony.Id);
                    }
                }
            }
            finally
            {
                harmony.UnpatchAll(harmony.Id);
            }
            return applied.Count == 0 ? "no reviewed transpiler found in ONI_NAVLINKS_MODS" : string.Join(", ", applied);
        }

        [NameLookupReviewed("The targets in `reviewed`, for mods' reviewed transpilers; one missing is skipped")]
        static MethodBase TargetOf(string target)
        {
            var dot = target.IndexOf('.');
            var type = AccessTools.TypeByName(target[..dot]);
            var name = target[(dot + 1)..];
            var methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name).ToList();
            // PathProber.Run: Fast Track patches the overload with a List<int> of found cells.
            return methods.Count == 1 ? methods[0] : methods.OrderByDescending(m => m.GetParameters().Length).First();
        }

        /// The grids' stores, by grid id.
        static List<(string Id, INavLinkStore Store)> Stores()
        {
            var result = new List<(string, INavLinkStore)>();
            var instance = AccessTools.Field("Pathfinding:Instance")?.GetValue(null);
            if (instance == null || AccessTools.Method("Pathfinding:GetNavGrids")?.Invoke(instance, null) is not IList grids) return result;
            var gridType = AccessTools.TypeByName("NavGrid");
            var id = AccessTools.Field(gridType, "id");
            var links = AccessTools.Field(gridType, "Links");
            foreach (var grid in grids)
                if (links.GetValue(grid) is { } handle && NavLinkStores.ByHandle.TryGetValue(handle, out var store))
                    result.Add((id?.GetValue(grid) as string ?? "?", store));
            return result;
        }

        /// After the load's build: what the stores hold, and in verify mode whether every cell
        /// matches the game's array.
        /// The first store summarized: the game flushes its grids more than once in a load.
        static readonly WeakReference<INavLinkStore?> summarized = new(null);

        static void Loaded()
        {
            try
            {
                var stores = Stores();
                if (stores.Count == 0 || summarized.TryGetTarget(out var last) && last == stores[0].Store) return;
                summarized.SetTarget(stores[0].Store);
                long bytes = 0, links = 0, slots = 0;
                bool wide = false;
                foreach (var (_, store) in stores)
                {
                    var size = store.Size();
                    bytes += size.Bytes;
                    links += size.Links;
                    slots += size.Slots;
                    wide |= store.Wide;
                }
                EngineLog.Engine($"nav links: {stores.Count} grids in compact stores: {links:N0} links in {bytes / 1e6:N1} MB (the arrays: {slots * 8 / 1e6:N1} MB); "
                                 + (wide ? "64-bit heads" : "32-bit heads"));
                if (NavGridLinks.Verify) VerifyAll("after the load");
                Dump();
            }
            catch (Exception e)
            {
                EngineLog.Engine($"nav links: summary failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// ONI_NAVLINKS_DUMP=DIR: each grid's links after the load's first flush, as NavProbe
        /// dumps them (DIR/GRID.bin): the stores', or with the tweak off the game's arrays (for
        /// comparing the two from runs of the same save). GameTweaks.Install adds it.
        internal static void InstallDump(Harmony harmony) =>
            harmony.Patch(AccessTools.Method("Pathfinding:FlushNavGridsOnLoad"), postfix: new HarmonyMethod(typeof(CompactNavLinks), nameof(Dump)));

        static bool dumped;

        static void Dump()
        {
            if (dumped || Environment.GetEnvironmentVariable("ONI_NAVLINKS_DUMP") is not { Length: > 0 } dir) return;
            dumped = true;
            try
            {
                var instance = AccessTools.Field("Pathfinding:Instance")?.GetValue(null);
                if (instance == null || AccessTools.Method("Pathfinding:GetNavGrids")?.Invoke(instance, null) is not IList grids) return;
                var gridType = AccessTools.TypeByName("NavGrid");
                var id = AccessTools.Field(gridType, "id");
                var field = AccessTools.Field(gridType, "Links");
                var max = AccessTools.Property(gridType, "maxLinksPerCell");
                System.IO.Directory.CreateDirectory(dir);
                int stores = 0;
                foreach (var grid in grids)
                {
                    var links = (Array)field.GetValue(grid)!;
                    using var file = System.IO.File.Create(System.IO.Path.Combine(dir, id.GetValue(grid) + ".bin"));
                    if (NavLinkStores.ByHandle.TryGetValue(links, out var store))
                    {
                        store.Dump(file);
                        stores++;
                        continue;
                    }
                    int perCell = (int)max.GetValue(grid)!, cells = links.Length / perCell;
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
                        ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(links), links.Length * 8).ToArray();
                    using var w = new System.IO.BinaryWriter(file);
                    w.Write(cells);
                    w.Write(perCell);
                    var counts = new byte[cells];
                    for (int c = 0; c < cells; c++)
                    {
                        int n = 0;
                        while (n < perCell && BitConverter.ToInt32(bytes, (c * perCell + n) * 8) != -1) n++;
                        counts[c] = (byte)Math.Min(n, 255);
                    }
                    w.Write(counts);
                    for (int c = 0; c < cells; c++) w.Write(bytes, c * perCell * 8, counts[c] * 8);
                }
                EngineLog.Engine($"nav links: {grids.Count} grids dumped to {dir} ({stores} from compact stores)");
            }
            catch (Exception e)
            {
                EngineLog.Engine($"nav links: dump failed: {e.GetType().Name}: {e.Message}");
            }
        }

        static readonly Stopwatch sinceVerify = Stopwatch.StartNew();

        /// Verify mode: every 30 s of play, the stores against the game's arrays.
        static void Updated()
        {
            if (sinceVerify.Elapsed.TotalSeconds < 30) return;
            VerifyAll("in play");
        }

        static void VerifyAll(string when)
        {
            sinceVerify.Restart();
            var watch = Stopwatch.StartNew();
            int grids = 0, wrong = 0;
            long links = 0;
            string? first = null;
            foreach (var (id, store) in Stores())
            {
                grids++;
                int differ = store.Verify(out long held, out string? detail);
                links += held;
                if (differ > 0 && first == null) first = $"{id}: {detail}";
                wrong += differ;
            }
            EngineLog.Engine($"nav links verify ({when}): {grids} grids, {links:N0} links, {wrong} cell(s) differ from the game's arrays"
                             + (first != null ? $"; first: {first}" : "") + $" ({watch.ElapsedMilliseconds} ms)");
        }
    }
}
