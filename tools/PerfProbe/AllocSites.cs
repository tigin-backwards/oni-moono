using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PerfProbe
{
    /// `sites.txt` (with `alloc.txt`): managed allocations by update section, in both players. Each
    /// main update method below gets a prefix and a finalizer, and the state machines' bucket loop
    /// (StateMachineUpdater.BucketGroup.AdvanceOneSubTick) is replaced by the same loop with each
    /// bucket bracketed. The counter's rise is credited to the innermost open section, so a section
    /// counts what it allocated outside the sections nested in it; what the main thread allocated
    /// outside them all is `(other)`. Logged after each `PERF alloc` line as a `PERF sites` line of
    /// running totals, `NAME=BYTES/CALLS` joined by ` | ` (buckets as `bucket NAME`).
    ///
    /// The counter: in the original, Boehm's GC_get_total_bytes (AllocMeter), which is process-wide,
    /// so a section also counts what other threads allocated meanwhile; in ours,
    /// GC.GetAllocatedBytesForCurrentThread on the main thread, the rest of the `PERF alloc` total
    /// being other threads'. scripts/alloc-compare.py reads the lines.
    static class AllocSites
    {
        /// The main update methods (type, method; every declared overload), as in
        /// docs/research/allocations-vs-original.md. One a player lacks is skipped with a log line.
        static readonly string[] Methods =
        {
            "Game.Update", "Game.LateUpdate", "Game.UnsafeSim200ms", "Game.LateUpdateComponents", "Global.Update", "Global.LateUpdate",
            "KBatchedAnimUpdater.LateUpdate", "KAnimBatchManager.Render", "AsyncPathProber.TickFrame", "Pathfinding.RenderEveryTick",
            "Pathfinding.UpdateNavGrids", "StatusItemRenderer.RenderEveryTick", "PrioritizableRenderer.RenderEveryTick",
            "PropertyTextures.LateUpdate", "CameraController.Update", "CircuitManager.RenderEveryTick", "LogicCircuitManager.RenderEveryTick",
            "World.LateUpdate", "World.UpdateCellInfo", "KScreenManager.Update", "SolidConduitFlow.RenderEveryTick", "ToolMenu.Update",
            "OverlayScreen.LateUpdate", "UnityEngine.UI.CanvasUpdateRegistry.PerformUpdate", "KComponentSpawn.RenderEveryTick",
            "BrainScheduler.RenderEveryTick",
        };

        sealed class Site
        {
            public string Name;
            public long Bytes, Calls;
        }

        static Func<long> counter;
        static int mainThread;
        static long mark;
        static readonly List<Site> stack = new List<Site>();
        static readonly Site other = new Site { Name = "(other)" };
        static readonly List<Site> all = new List<Site> { other };
        static readonly Dictionary<MethodBase, Site> byMethod = new Dictionary<MethodBase, Site>();
        static readonly Dictionary<StateMachineUpdater.BaseUpdateBucket, Site> byBucket = new Dictionary<StateMachineUpdater.BaseUpdateBucket, Site>();
        static readonly Dictionary<string, Site> byName = new Dictionary<string, Site>();
        static AccessTools.FieldRef<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>> bucketFrames;
        static AccessTools.FieldRef<StateMachineUpdater.BucketGroup, int> nextUpdateIndex;

        public static bool Enabled { get; private set; }

        static bool OnMain => System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread;

        public static void Install(Harmony harmony, string path)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(path, "sites.txt"))) return;
            counter = AllocMeter.MonoTotal();
            string kind = "Boehm's process-wide total";
            if (counter == null)
            {
                var perThread = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
                if (perThread == null) { Debug.Log("PERF sites: no allocation counter in this runtime"); return; }
                counter = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), perThread);
                kind = "the main thread's own";
            }
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var enter = new HarmonyMethod(typeof(AllocSites), nameof(Enter));
            var leave = new HarmonyMethod(typeof(AllocSites), nameof(Leave));
            int patched = 0;
            foreach (var entry in Methods)
            {
                int dot = entry.LastIndexOf('.');
                var type = AccessTools.TypeByName(entry.Substring(0, dot));
                string name = entry.Substring(dot + 1), label = entry.Substring(entry.LastIndexOf('.', dot - 1) + 1);
                bool found = false;
                if (type != null)
                    foreach (var method in AccessTools.GetDeclaredMethods(type))
                    {
                        if (method.Name != name || method.IsAbstract || method.ContainsGenericParameters) continue;
                        try
                        {
                            harmony.Patch(method, prefix: enter, finalizer: leave);
                            byMethod[method] = Named(label);
                            found = true;
                            patched++;
                        }
                        catch (Exception e) { Debug.Log($"PERF sites: {entry} not patched: {e.GetType().Name} {e.Message}"); }
                    }
                if (!found) Debug.Log($"PERF sites: no {entry} here");
            }
            bucketFrames = AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>>("bucketFrames");
            nextUpdateIndex = AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, int>("nextUpdateIndex");
            harmony.Patch(AccessTools.Method(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick)),
                prefix: new HarmonyMethod(typeof(AllocSites), nameof(Advance)));
            mark = counter();
            Enabled = true;
            Debug.Log($"PERF sites: {patched} methods and the bucket loop, counter {kind}");
        }

        static Site Named(string name)
        {
            if (!byName.TryGetValue(name, out var site))
            {
                byName[name] = site = new Site { Name = name };
                all.Add(site);
            }
            return site;
        }

        /// The counter's rise since the last mark, to the innermost open section.
        static void Credit()
        {
            long now = counter();
            (stack.Count > 0 ? stack[stack.Count - 1] : other).Bytes += now - mark;
            mark = now;
        }

        static void Push(Site site)
        {
            Credit();
            site.Calls++;
            stack.Add(site);
        }

        static void Pop()
        {
            Credit();
            stack.RemoveAt(stack.Count - 1);
        }

        static void Enter(MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            if (!OnMain || !byMethod.TryGetValue(__originalMethod, out var site)) return;
            Push(site);
            __state = true;
        }

        static void Leave(bool __state)
        {
            if (__state) Pop();
        }

        /// StateMachineUpdater.BucketGroup.AdvanceOneSubTick's loop, the same calls in the same
        /// order, each bucket bracketed (main thread only).
        static bool Advance(StateMachineUpdater.BucketGroup __instance, float dt)
        {
            if (!OnMain) return true;
            var frames = bucketFrames(__instance);
            var list = frames[nextUpdateIndex(__instance)];
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                var bucket = list[i];
                if (bucket.count == 0) continue;
                if (!byBucket.TryGetValue(bucket, out var site)) byBucket[bucket] = site = Named("bucket " + bucket.name);
                Push(site);
                try { bucket.Update(dt); }
                finally { Pop(); }
            }
            nextUpdateIndex(__instance) = (nextUpdateIndex(__instance) + 1) % frames.Count;
            return false;
        }

        /// The running totals, after the open sections are credited up to now.
        public static string Window()
        {
            if (!Enabled) return "";
            Credit();
            var parts = new List<string>();
            foreach (var site in all)
                if (site.Bytes != 0 || site.Calls != 0) parts.Add($"{site.Name}={site.Bytes}/{site.Calls}");
            return $"PERF sites f{Time.frameCount} " + string.Join(" | ", parts);
        }
    }
}
