using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// The mods' Harmony patches on methods the game tweaks this run has on touch, for the Moono
    /// Tweaks screen (an icon and tooltip lines on the affected rows, mod names its search finds)
    /// and the main menu's count of high-grade ones (OverlapBadge). Informational only: nothing
    /// is turned on or off here.
    ///
    /// Read at each main menu (MoonoTweaksMenu.AddButton), when every mod has loaded and made
    /// its patches: every patched method Harmony knows, its owners' patches split into a tweak's
    /// (each tweak patches under its own ID, ModOverlapsCore.OwnerOf) and the others'; the
    /// methods the host's load-time IL rewrite changed for a tweak (`rewrittenBy`); and the
    /// methods whose patching by another owner makes a tweak step aside (Guards). Graded and
    /// worded by ModOverlapsCore. Player.log gets the block when it differs from the last one.
    /// Anything that throws leaves no overlaps, with a log line.
    public static class ModOverlaps
    {
        static HashSet<string> enabled = new HashSet<string>(StringComparer.Ordinal);
        /// Test infrastructure's Harmony IDs, left out like ours (ONI_MOD_OVERLAPS_IGNORE,
        /// comma-separated: scripts/e2e.py names OniMcp's, which every spec loads).
        static readonly HashSet<string> ignored = new HashSet<string>(
            (Environment.GetEnvironmentVariable("ONI_MOD_OVERLAPS_IGNORE") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        static bool Ours(string owner) => ModOverlapsCore.IsOurs(owner) || ignored.Contains(owner);
        static Func<MethodBase, string> rewrittenBy;
        static HashSet<string> reviewed = new HashSet<string>(StringComparer.Ordinal);
        static string lastLog;

        /// The last reading's overlaps (empty until the first main menu, or after a failure).
        internal static List<Overlap> Found = new List<Overlap>();

        /// From the host: the tweaks on this run, which tweak's rewrite changed a method (null for
        /// none), and the transpilers reviewed against compact-nav-links' rewrite (each
        /// "Type.Method" with its patch class, in pairs).
        public static void Install(string[] enabledTweaks, Func<MethodBase, string> rewritten, string[] reviewedTargets, string[] reviewedTranspilers)
        {
            enabled = new HashSet<string>(enabledTweaks ?? Array.Empty<string>(), StringComparer.Ordinal);
            rewrittenBy = rewritten;
            reviewed = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; reviewedTargets != null && i < reviewedTargets.Length; i++) reviewed.Add(reviewedTargets[i] + "\n" + reviewedTranspilers[i]);
        }

        /// The overlaps of one tweak.
        internal static IEnumerable<Overlap> For(string tweak) => Found.Where(o => o.Tweak == tweak);

        /// The main menu's count; and for tests (E2eDriver's state reads,
        /// `type:OniMoono.GameTweaks.ModOverlaps::High`), the overlaps by grade.
        public static int HighCount => ModOverlapsCore.HighCount(Found);
        public static int High => Found.Count(o => o.Grade == OverlapGrade.High);
        public static int Low => Found.Count(o => o.Grade == OverlapGrade.Low);
        public static int StepsAside => Found.Count(o => o.Grade == OverlapGrade.StepsAside);

        /// Reads the patches again; never throws.
        internal static void Refresh()
        {
            try
            {
                var watch = Stopwatch.StartNew();
                int patched = 0;
                var found = enabled.Count == 0 ? new List<Overlap>() : Read(out patched);
                watch.Stop();
                Found = found;
                var block = ModOverlapsCore.LogBlock(found, patched, watch.Elapsed.TotalMilliseconds);
                // The same overlaps as last time: no new block (the cost changes every time).
                var lines = block.Substring(block.IndexOf('\n') < 0 ? block.Length : block.IndexOf('\n'));
                var key = found.Count + lines;
                if (key != lastLog)
                {
                    lastLog = key;
                    global::Debug.Log(block);
                }
            }
            catch (Exception e)
            {
                Found = new List<Overlap>();
                global::Debug.LogWarning("[Moono] mod overlaps: not read: " + e);
            }
        }

        static List<Overlap> Read(out int patchedCount)
        {
            var titles = ModTitles(out var byId);
            var guards = Guards();
            var methods = new List<PatchedMethod>();
            var all = Harmony.GetAllPatchedMethods().ToList();
            patchedCount = all.Count;
            foreach (var method in all)
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                bool foreign = false;
                foreach (var owner in info.Owners)
                    if (!Ours(owner)) { foreign = true; break; }
                if (!foreign) continue;
                var uses = new Dictionary<string, TweakUse>(StringComparer.Ordinal);
                TweakUse Use(string tweak) => uses.TryGetValue(tweak, out var u) ? u : uses[tweak] = new TweakUse { Tweak = tweak };
                var patches = new List<ForeignPatch>();
                void Add(IEnumerable<Patch> list, PatchKind kind)
                {
                    foreach (var p in list)
                    {
                        bool skips = kind == PatchKind.Prefix && p.PatchMethod is MethodInfo pm && pm.ReturnType == typeof(bool);
                        var tweak = ModOverlapsCore.TweakOf(p.owner);
                        if (tweak != null)
                        {
                            var u = Use(tweak);
                            if (kind == PatchKind.Prefix) { u.Prefix = true; u.SkipPrefix |= skips; }
                            else if (kind == PatchKind.Postfix) u.Postfix = true;
                            else if (kind == PatchKind.Transpiler) u.Transpiler = true;
                            else u.Finalizer = true;
                        }
                        else if (!Ours(p.owner))
                        {
                            var assembly = p.PatchMethod?.DeclaringType?.Assembly;
                            string mod = assembly != null && titles.TryGetValue(assembly, out var t) ? t
                                : byId.TryGetValue(p.owner, out var t2) ? t2 : p.owner;
                            patches.Add(new ForeignPatch { Owner = p.owner, Mod = mod, Kind = kind, CanSkip = skips, PatchClass = p.PatchMethod?.DeclaringType?.FullName ?? "" });
                        }
                    }
                }
                Add(info.Prefixes, PatchKind.Prefix);
                Add(info.Postfixes, PatchKind.Postfix);
                Add(info.Transpilers, PatchKind.Transpiler);
                Add(info.Finalizers, PatchKind.Finalizer);
                var rewriter = rewrittenBy?.Invoke(method);
                if (rewriter != null && enabled.Contains(rewriter))
                {
                    var u = Use(rewriter);
                    u.Rewrite = true;
                    // Both rewrites run flat for a session when a transpiler they did not review
                    // changes a method they rewrote (CompactNavLinks, PagedPathGrids).
                    u.StepsAsideOnTranspiler = true;
                }
                if (guards.TryGetValue((method.Module, method.MetadataToken), out var guarding))
                    foreach (var (tweak, kinds) in guarding)
                    {
                        var u = Use(tweak);
                        u.Guard = true;
                        u.GuardKinds = kinds;
                    }
                // Uses of a tweak that is off this run (none: its patches are not made) are kept
                // out all the same.
                foreach (var tweak in uses.Keys.ToList())
                    if (!enabled.Contains(tweak)) uses.Remove(tweak);
                if (uses.Count == 0 || patches.Count == 0) continue;
                var entry = new PatchedMethod { Method = Name(method), Patches = patches };
                entry.Uses.AddRange(uses.Values);
                methods.Add(entry);
            }
            return ModOverlapsCore.Find(methods, (tweak, target, patchClass) => reviewed.Contains(target + "\n" + patchClass), MoonoTweaksMenu.Ids);
        }

        /// "Type.Method", as the tweaks' own logs name methods.
        internal static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

        /// Each loaded mod's assemblies and Harmony ID, to its title in the game's mod list.
        static Dictionary<Assembly, string> ModTitles(out Dictionary<string, string> byId)
        {
            var byAssembly = new Dictionary<Assembly, string>();
            byId = new Dictionary<string, string>(StringComparer.Ordinal);
            var mods = Global.Instance?.modManager?.mods;
            if (mods == null) return byAssembly;
            foreach (var mod in mods)
            {
                var data = mod?.loaded_mod_data;
                if (data == null) continue;
                var title = string.IsNullOrEmpty(mod.title) ? mod.label.id : mod.title;
                if (data.dlls != null)
                    foreach (var dll in data.dlls)
                        if (dll != null && !byAssembly.ContainsKey(dll)) byAssembly[dll] = title;
                if (data.harmony?.Id is { } id && !byId.ContainsKey(id)) byId[id] = title;
            }
            return byAssembly;
        }

        /// The methods whose patching by another owner makes an enabled tweak step aside (each
        /// tweak's own list, which its check reads), and which kinds of patch count there
        /// (`kindsOf`, default all).
        static Dictionary<(Module, int), List<(string, ProbeRepairPolicy.GuardedKinds)>> Guards()
        {
            var guards = new Dictionary<(Module, int), List<(string, ProbeRepairPolicy.GuardedKinds)>>();
            void Add(string tweak, Func<IEnumerable<MethodBase>> methods, Func<MethodBase, ProbeRepairPolicy.GuardedKinds> kindsOf = null)
            {
                if (!enabled.Contains(tweak)) return;
                try
                {
                    foreach (var m in methods())
                        if (m != null)
                        {
                            if (!guards.TryGetValue((m.Module, m.MetadataToken), out var list)) guards[(m.Module, m.MetadataToken)] = list = new List<(string, ProbeRepairPolicy.GuardedKinds)>();
                            list.Add((tweak, kindsOf?.Invoke(m) ?? ProbeRepairPolicy.GuardedKinds.All));
                        }
                }
                catch (Exception e)
                {
                    global::Debug.LogWarning($"[Moono] mod overlaps: {tweak}'s methods not read: {e.Message}");
                }
            }
            Add(ProbeRepair.Name, ProbeRepair.Guarded);
            Add(FastAnimBatches.Name, FastAnimBatches.WritePath);
            Add(FastSymbolOverrides.Name, FastSymbolOverrides.KeptCalls);
            Add(FastSmiLookups.Name, () => new[] { FastSmiLookups.Guarded() });
            Add(FastSaveRoots.Name, () => new[] { FastSaveRoots.Guarded() });
            Add(FastConveyorPaths.Name, () => new[] { FastConveyorPaths.Guarded() });
            Add(FastOverlays.Name, FastOverlays.Guarded);
            Add(FastOverlayTextures.Name, () => new[] { FastOverlayTextures.Guarded() });
            Add(FastAnimEvents.Name, FastAnimEvents.Guarded, FastAnimEvents.KindsOf);
            return guards;
        }
    }
}
