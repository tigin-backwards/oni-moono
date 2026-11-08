using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OniMoono.GameTweaks;
using OniMoono.Host.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The mod overlaps of the Moono Tweaks screen (src/OniMoono.GameTweaks/ModOverlapsCore.cs,
    /// compiled in): the grades, the tweaks' Harmony IDs, the log block, the menu's count and the
    /// rows' tooltips; and, from the built assemblies, that every tweak's patches carry its own
    /// Harmony ID, so the table of the methods each tweak touches has them all.
    public class ModOverlapsTests
    {
        static TweakUse Use(string tweak = "fast-jobs", bool skipPrefix = false, bool prefix = false, bool postfix = false, bool transpiler = false,
            bool rewrite = false, bool guard = false, bool asideOnTranspiler = false) =>
            new TweakUse { Tweak = tweak, Prefix = prefix || skipPrefix, SkipPrefix = skipPrefix, Postfix = postfix, Transpiler = transpiler, Rewrite = rewrite, Guard = guard, StepsAsideOnTranspiler = asideOnTranspiler };

        static ForeignPatch Patch(PatchKind kind, bool canSkip = false, string mod = "Some Mod", string owner = "some.mod", string patchClass = "SomeMod.Patches") =>
            new ForeignPatch { Kind = kind, CanSkip = canSkip, Mod = mod, Owner = owner, PatchClass = patchClass };

        [Fact]
        public void TranspilersOnAnyTweakMethodAreHigh()
        {
            foreach (var use in new[] { Use(postfix: true), Use(prefix: true), Use(skipPrefix: true), Use(transpiler: true), Use(rewrite: true) })
                Assert.Equal(OverlapGrade.High, ModOverlapsCore.Grade(use, Patch(PatchKind.Transpiler), reviewed: false));
        }

        [Fact]
        public void SkippingPrefixesAreHighOnlyWhereTheTweakReplaces()
        {
            var skip = Patch(PatchKind.Prefix, canSkip: true);
            Assert.Equal(OverlapGrade.High, ModOverlapsCore.Grade(Use(skipPrefix: true), skip, false));
            Assert.Equal(OverlapGrade.High, ModOverlapsCore.Grade(Use(transpiler: true), skip, false));
            Assert.Equal(OverlapGrade.High, ModOverlapsCore.Grade(Use(rewrite: true), skip, false));
            // The tweak's own patch leaves the body as it is: a skip skips only the game's code.
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(Use(postfix: true), skip, false));
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(Use(prefix: true), skip, false));
        }

        [Fact]
        public void EverythingElseIsLow()
        {
            var replaces = Use(skipPrefix: true);
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(replaces, Patch(PatchKind.Prefix), false));
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(replaces, Patch(PatchKind.Postfix), false));
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(replaces, Patch(PatchKind.Finalizer), false));
        }

        [Fact]
        public void GuardedMethodsAndUnreviewedTranspilersOnRewritesStepAside()
        {
            foreach (var kind in new[] { PatchKind.Prefix, PatchKind.Postfix, PatchKind.Transpiler, PatchKind.Finalizer })
                Assert.Equal(OverlapGrade.StepsAside, ModOverlapsCore.Grade(Use(guard: true, skipPrefix: true), Patch(kind, canSkip: true), false));
            var rewrite = Use("compact-nav-links", rewrite: true, asideOnTranspiler: true);
            Assert.Equal(OverlapGrade.StepsAside, ModOverlapsCore.Grade(rewrite, Patch(PatchKind.Transpiler), reviewed: false));
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(rewrite, Patch(PatchKind.Transpiler), reviewed: true));
            Assert.Equal(OverlapGrade.High, ModOverlapsCore.Grade(rewrite, Patch(PatchKind.Prefix, canSkip: true), false));
        }

        [Fact]
        public void GuardsStepAsideOnlyForTheKindsTheyCount()
        {
            // fast-anim-events: postfixes pass on the loop, all but finalizers on the flagged methods.
            var loop = Use("fast-anim-events", skipPrefix: true, guard: true);
            loop.GuardKinds = ProbeRepairPolicy.GuardedKinds.AllButPostfixes;
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(loop, Patch(PatchKind.Postfix), false));
            foreach (var kind in new[] { PatchKind.Prefix, PatchKind.Transpiler, PatchKind.Finalizer })
                Assert.Equal(OverlapGrade.StepsAside, ModOverlapsCore.Grade(loop, Patch(kind, canSkip: kind == PatchKind.Prefix), false));
            var flagged = Use("fast-anim-events", postfix: true, guard: true);
            flagged.GuardKinds = ProbeRepairPolicy.GuardedKinds.Finalizers;
            Assert.Equal(OverlapGrade.StepsAside, ModOverlapsCore.Grade(flagged, Patch(PatchKind.Finalizer), false));
            Assert.Equal(OverlapGrade.Low, ModOverlapsCore.Grade(flagged, Patch(PatchKind.Prefix, canSkip: true), false));
            Assert.Equal(OverlapGrade.High, ModOverlapsCore.Grade(flagged, Patch(PatchKind.Transpiler), false));
        }

        [Fact]
        public void EachTweakHasItsOwnHarmonyId()
        {
            Assert.Equal("OniMoono.Host.GameTweaks.fast-jobs", ModOverlapsCore.OwnerOf("fast-jobs"));
            Assert.Equal("fast-jobs", ModOverlapsCore.TweakOf("OniMoono.Host.GameTweaks.fast-jobs"));
            Assert.Null(ModOverlapsCore.TweakOf("OniMoono.Host.GameTweaks"));
            Assert.Null(ModOverlapsCore.TweakOf("OniMoono.Host.FrameStats"));
            Assert.Null(ModOverlapsCore.TweakOf("PeterHan.FastTrack"));
            Assert.True(ModOverlapsCore.IsTweakOwner("OniMoono.Host.GameTweaks.path-probe-repair"));
            Assert.False(ModOverlapsCore.IsTweakOwner("OniMoono.Host.FrameStats"));
            // Ours, never an overlap: the host's patches and the perf tool's stopwatches.
            Assert.True(ModOverlapsCore.IsOurs("OniMoono.Host.MonoTypeByName"));
            Assert.True(ModOverlapsCore.IsOurs("PerfProbe.Frames"));
            Assert.False(ModOverlapsCore.IsOurs("PeterHan.FastTrack"));
            Assert.False(ModOverlapsCore.IsOurs("OniMooner"));
            // path-probe-repair's own check knows its ID.
            Assert.Equal(ProbeRepairPolicy.Owner, ModOverlapsCore.OwnerOf("path-probe-repair"));
            Assert.Null(ProbeRepairPolicy.Foreign(new[] { new KeyValuePair<string, IEnumerable<string>>("Navigator.TakeResult", new[] { ModOverlapsCore.OwnerOf("brain-rate") }) }));
        }

        static List<Overlap> Sample()
        {
            var run = new PatchedMethod { Method = "JobManager.Run" };
            run.Uses.Add(Use("fast-jobs", skipPrefix: true));
            run.Patches.Add(Patch(PatchKind.Transpiler, mod: "Fast Track", owner: "PeterHan.FastTrack"));
            run.Patches.Add(Patch(PatchKind.Postfix, mod: "Debug Console", owner: "debug.console"));
            run.Patches.Add(Patch(PatchKind.Postfix, mod: "ours", owner: "OniMoono.Host.FrameStats"));
            var cleanUp = new PatchedMethod { Method = "KBatchedAnimUpdater.CleanUp" };
            cleanUp.Uses.Add(Use("fast-anim-cleanup", skipPrefix: true));
            cleanUp.Patches.Add(Patch(PatchKind.Prefix, canSkip: true, mod: "Fast Track", owner: "PeterHan.FastTrack"));
            var probe = new PatchedMethod { Method = "PathProber.Run" };
            probe.Uses.Add(Use("path-probe-repair", guard: true));
            probe.Patches.Add(Patch(PatchKind.Prefix, canSkip: true, mod: "Fast Track", owner: "PeterHan.FastTrack"));
            return ModOverlapsCore.Find(new[] { probe, cleanUp, run }, null, new[] { "fast-jobs", "fast-anim-cleanup", "path-probe-repair" });
        }

        [Fact]
        public void FindIgnoresOurOwnAndSortsByTheScreen()
        {
            var found = Sample();
            Assert.Equal(new[] { "fast-jobs", "fast-jobs", "fast-anim-cleanup", "path-probe-repair" }, found.Select(o => o.Tweak));
            Assert.DoesNotContain(found, o => o.Owner.StartsWith("OniMoono.", StringComparison.Ordinal));
            Assert.Equal(new[] { OverlapGrade.Low, OverlapGrade.High, OverlapGrade.High, OverlapGrade.StepsAside }, found.Select(o => o.Grade));
            // The badge: each tweak and mod with a high one, once; where a tweak steps aside does not count.
            Assert.Equal(2, ModOverlapsCore.HighCount(found));
            Assert.Equal(OverlapGrade.High, ModOverlapsCore.Worst(found.Where(o => o.Tweak == "fast-jobs")));
            Assert.Equal(OverlapGrade.StepsAside, ModOverlapsCore.Worst(found.Where(o => o.Tweak == "path-probe-repair")));
            Assert.Null(ModOverlapsCore.Worst(found.Where(o => o.Tweak == "splash")));
        }

        [Fact]
        public void TheLogListsEveryOverlap()
        {
            var block = ModOverlapsCore.LogBlock(Sample(), 812, 0.42);
            var lines = block.Split('\n');
            Assert.Equal("[Moono] mod overlaps: 4 (2 high, 1 low, 1 where the tweak steps aside); 812 patched method(s) read in 0.42 ms", lines[0]);
            Assert.Equal("  fast-jobs | JobManager.Run | Debug Console (debug.console) | postfix | low", lines[1]);
            Assert.Equal("  fast-jobs | JobManager.Run | Fast Track (PeterHan.FastTrack) | transpiler | high", lines[2]);
            Assert.Equal("  fast-anim-cleanup | KBatchedAnimUpdater.CleanUp | Fast Track (PeterHan.FastTrack) | prefix (can skip) | high", lines[3]);
            Assert.Equal("  path-probe-repair | PathProber.Run | Fast Track (PeterHan.FastTrack) | prefix (can skip) | steps aside", lines[4]);
            Assert.Equal("[Moono] mod overlaps: none; 0 patched method(s) read in 0.00 ms", ModOverlapsCore.LogBlock(new List<Overlap>(), 0, 0));
        }

        static string Ui(string name) => TweakStrings.Ui.Single(u => u.Name == name).English;

        [Fact]
        public void TooltipsNameTheModsAndWhatTheyPatch()
        {
            var found = Sample();
            Assert.Equal("<color=#FFB040>Fast Track also patches JobManager.Run (it rewrites the game's code).</color>\n"
                         + "Debug Console also patches JobManager.Run.\nIf something looks wrong, turn this tweak off.",
                ModOverlapsCore.Tooltip(found.Where(o => o.Tweak == "fast-jobs"), Ui, "FFB040"));
            Assert.Equal("<color=#FFB040>Fast Track also patches KBatchedAnimUpdater.CleanUp (it can skip the game's code).</color>\n"
                         + "If something looks wrong, turn this tweak off.",
                ModOverlapsCore.Tooltip(found.Where(o => o.Tweak == "fast-anim-cleanup"), Ui, "FFB040"));
            // Stepping aside is no conflict: no advice to turn it off.
            Assert.Equal("Off while Fast Track is loaded, as it patches PathProber.Run.",
                ModOverlapsCore.Tooltip(found.Where(o => o.Tweak == "path-probe-repair"), Ui, "FFB040"));
            Assert.Equal("", ModOverlapsCore.Tooltip(Array.Empty<Overlap>(), Ui, "FFB040"));
        }

        [Fact]
        public void AModNamesItsMethodsOnceUnderItsStrongestReason()
        {
            var methods = new List<PatchedMethod>();
            foreach (var name in new[] { "A.One", "A.Two", "A.Three", "A.Four", "A.Five" })
            {
                var m = new PatchedMethod { Method = name };
                m.Uses.Add(Use("fast-jobs", skipPrefix: true));
                m.Patches.Add(Patch(name == "A.One" ? PatchKind.Transpiler : PatchKind.Prefix, canSkip: name != "A.Five", mod: "<color=red>Big</color> Mod"));
                methods.Add(m);
            }
            var tip = ModOverlapsCore.Tooltip(ModOverlapsCore.Find(methods, null), Ui, "FFB040");
            Assert.Equal("<color=#FFB040>Big Mod also patches A.One (it rewrites the game's code).</color>\nIf something looks wrong, turn this tweak off.", tip);
            // Four methods under one reason: three named, then how many more.
            methods.RemoveAt(methods.FindIndex(m => m.Method == "A.One"));
            foreach (var m in methods) m.Patches[0].CanSkip = true;
            tip = ModOverlapsCore.Tooltip(ModOverlapsCore.Find(methods, null), Ui, "FFB040");
            Assert.Equal("<color=#FFB040>Big Mod also patches A.Five, A.Four, A.Three and 1 more (it can skip the game's code).</color>\nIf something looks wrong, turn this tweak off.", tip);
        }

        [Fact]
        public void TheScreenHasEveryOverlapString()
        {
            foreach (var name in new[] { "OVERLAP_REWRITES", "OVERLAP_SKIPS", "OVERLAP_PATCHES", "OVERLAP_STEPS_ASIDE", "OVERLAP_ADVICE", "OVERLAP_MORE_METHODS",
                         "OVERLAPS_ONE", "OVERLAPS_MANY", "OVERLAPS_TIP" })
                Assert.Contains(TweakStrings.Ui, u => u.Name == name);
        }

        /// The table of the methods each tweak touches comes from the patches' Harmony IDs, so a
        /// tweak whose patches carried another ID would be missing from it. The tweaks get their
        /// Harmony from the host (one per tweak, OniMoono.Host.GameTweaks.GameTweaks.Install):
        /// none of OniMoono.GameTweaks' code makes one or patches through Harmony's attributes,
        /// and the host makes the tweaks' only through ModOverlapsCore.OwnerOf's format.
        [Fact]
        public void EveryTweakPatchesUnderItsOwnHarmonyId()
        {
            var typed = Built("OniMoono.GameTweaks", "OniMoono.GameTweaks.dll");
            using (var module = ModuleDefinition.ReadModule(typed))
            {
                var made = Calls(module, "HarmonyLib.Harmony", ".ctor", "PatchAll", "CreateClassProcessor").ToList();
                Assert.True(made.Count == 0, "OniMoono.GameTweaks makes or attribute-patches its own Harmony: " + string.Join(", ", made));
                Assert.DoesNotContain(module.Types, t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"));
            }
            // Each typed tweak is installed by Patches.Install (the host hands it its Harmony).
            var installed = File.ReadAllText(Path.Combine(RepoRoot(), "src", "OniMoono.GameTweaks", "Patches.cs"));
            var source = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "OniMoono.GameTweaks"), "*.cs").Select(File.ReadAllText).ToList();
            foreach (var name in GameTweakNames.Typed)
            {
                var file = source.FirstOrDefault(s => s.Contains($"public const string Name = \"{name}\";"));
                Assert.True(file != null, $"no class names the typed tweak {name}");
                var type = System.Text.RegularExpressions.Regex.Match(file, @"public static class (\w+)").Groups[1].Value;
                Assert.Contains($"case {type}.Name:", installed);
            }
            // The host: one Harmony a tweak, with the ID ModOverlapsCore reads, made by the
            // guarded install (TweakInstall.Guarded) every tweak goes through.
            var guard = File.ReadAllText(Path.Combine(RepoRoot(), "src", "OniMoono.Host", "GameTweaks", "TweakInstall.cs"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(guard, @"new Harmony\("));
            Assert.Contains($"new Harmony(\"{ModOverlapsCore.TweakOwnerPrefix}.\" + name)", guard);
            var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "OniMoono.Host", "GameTweaks", "GameTweaks.cs"));
            var install = host.Substring(host.IndexOf("public static void Install(", StringComparison.Ordinal));
            install = install.Substring(0, install.IndexOf("public static void InstallFrameStats", StringComparison.Ordinal));
            Assert.Contains("TweakInstall.Guarded(name, install", install);
            Assert.DoesNotMatch(@"\(harmony[,)]", install);
        }

        static IEnumerable<string> Calls(ModuleDefinition module, string type, params string[] names)
        {
            foreach (var t in module.GetTypes())
                foreach (var m in t.Methods.Where(m => m.HasBody))
                    foreach (var i in m.Body.Instructions)
                        if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj) && i.Operand is MethodReference r
                            && r.DeclaringType.FullName == type && names.Contains(r.Name))
                            yield return $"{t.FullName}.{m.Name} calls {r.Name}";
        }

        static string Built(string project, string file)
        {
            foreach (var config in new[] { "debug", "release" })
            {
                var path = Path.Combine(RepoRoot(), "out", "bin", project, config, file);
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException($"{project} is not built (out/bin/{project}/debug or release)");
        }

        static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }
}
