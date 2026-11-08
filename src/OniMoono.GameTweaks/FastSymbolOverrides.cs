using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-symbol-overrides` (opt-in). An animation batch writes a controller's
    /// symbol-override block (12 floats per symbol frame of its build) into its texture whenever
    /// the block's version changed. The controller re-applies its overrides when they change, and
    /// Duplicants (and the foreground layers of layered animations) on every dirty mark: their
    /// SymbolOverrideController has applySymbolOverridesEveryFrame, because FaceGraph draws the
    /// face (eyes, mouth, speech) into the block after the overrides on each application. Both
    /// SymbolOverrideInfoGpuData.SetSymbolOverrideInfo overloads raise the version even when they
    /// store the values already there.
    ///
    /// 1. Unchanged values. A setter call that would store only the values already held is
    ///    skipped: the block stays the same and its version is not raised. Exact by construction:
    ///    a skipped call would have written each field's current value.
    ///
    /// 2. Applications whose inputs did not change (Duplicants, about every frame each). The
    ///    application (SymbolOverrideController.ApplyOverrides) resolves the override list by
    ///    priority, looks up each source build's atlas and data and each target symbol, rebuilds
    ///    the controller's atlas list, writes each resolved override's frames into the block,
    ///    then calls FaceGraph.ApplyShape. What it writes depends only on: the override list
    ///    (changed only through the controller's methods, each raising its version), the batch
    ///    and its atlas list, the controller's batch group and its symbols, the block itself and
    ///    the anim data loaded (KAnimBatchManager's instance). The tweak keeps, per controller,
    ///    the calls the last application made (atlas adds and block writes, in order) with those
    ///    inputs. When they are the same, it makes the same atlas calls, then rewrites only the
    ///    block entries someone else wrote since (the setters are the block's only writers, and
    ///    the tweak logs every entry they write outside an application), so the block ends as a
    ///    full rewrite leaves it; then FaceGraph.ApplyShape runs as before. Any other input,
    ///    a sort pending, more than OverrideBlockLog.MaxWritten such entries, or an exception
    ///    in the lookups: the application is made in full, in the game's order (the lookups
    ///    first, then its calls), or by the game's own code.
    ///
    /// 3. Applications that leave the block as they found it. The face is drawn over the
    ///    entries the overrides just wrote, so each application changed the block twice and
    ///    raised its version, and the batch uploaded the whole block every frame for every
    ///    Duplicant. The tweak keeps the bytes of each entry an application writes (before its
    ///    first write); when at the end every one holds its old bytes again, the version is put
    ///    back to what it was before the application. Nothing reads the version in between (the
    ///    batch's write check runs after the application), and the version had not been seen
    ///    with any other bytes, so the batch's check gives the same answer for the same bytes.
    ///
    /// Parts 2 and 3 only for controllers with applySymbolOverridesEveryFrame, and only while no
    /// other Harmony owner patches the methods the kept calls stand for. ONI_SYMBOL_PLANS=0 leaves
    /// only part 1 (with ONI_TWEAK_TIMING, the applications are still timed and classified).
    /// With ONI_TWEAK_VERIFY=1 each shortened application is checked against a full one: the
    /// lookups are redone and must give the same calls, and every entry the calls write must hold
    /// what they write.
    [GameMember(GameUse.Postfix, "System.Boolean KAnimBatch::WriteSymbolOverrideInfoTex(System.Int32,KAnimConverter/IAnimConverter,Unity.Collections.NativeArray`1<System.Byte>)")]
    [GameMember(GameUse.Postfix, "System.Void KAnimBatchManager::CreateInstance()")]
    [GameMember(GameUse.Postfix, "System.Void KAnimBatchManager::DestroyInstance()")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SymbolOverrideController::ApplyOverrides()", "2a4ef94fd098635f linux-744825 win-744825")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SymbolOverrideInfoGpuData::SetSymbolOverrideInfo(System.Int32,KAnim/Build/SymbolFrameInstance&)", "24a459881b542db2 linux-744825 win-744825")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SymbolOverrideInfoGpuData::SetSymbolOverrideInfo(System.Int32,System.Int32,System.Int32,KBatchGroupData,System.Int32,System.Int32)", "3e09bb5981edcea0 linux-744825 win-744825")]
    [GameMember(GameUse.Uses, "System.Int32 KAnimControllerBase::pendingSymbolOverrideInfoSymbolCount", Optional = true)]
    [GameMember(GameUse.Uses, "SymbolOverrideInfoGpuData KAnimControllerBase::_symbolOverrideInfoGpuData", Optional = true)]
    public static class FastSymbolOverrides
    {
        public const string Name = "fast-symbol-overrides";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly bool timing = Environment.GetEnvironmentVariable("ONI_TWEAK_TIMING") == "1";
        static readonly bool plans = Environment.GetEnvironmentVariable("ONI_SYMBOL_PLANS") != "0";

        static readonly AccessTools.FieldRef<SymbolOverrideInfoGpuData, SymbolOverrideInfoGpuData.SymbolOverrideInfoToByteConverter> converter =
            AccessTools.FieldRefAccess<SymbolOverrideInfoGpuData, SymbolOverrideInfoGpuData.SymbolOverrideInfoToByteConverter>("symbolOverrideInfoConverter");
        static readonly AccessTools.FieldRef<SymbolOverrideInfoGpuData, int> blockVersion =
            AccessTools.FieldRefAccess<SymbolOverrideInfoGpuData, int>("<version>k__BackingField");
        static readonly AccessTools.FieldRef<SymbolOverrideController, List<SymbolOverrideController.SymbolEntry>> overridesOf =
            AccessTools.FieldRefAccess<SymbolOverrideController, List<SymbolOverrideController.SymbolEntry>>("symbolOverrides");
        static readonly AccessTools.FieldRef<SymbolOverrideController, KAnimBatch.AtlasList> atlasesOf =
            AccessTools.FieldRefAccess<SymbolOverrideController, KAnimBatch.AtlasList>("atlases");
        static readonly AccessTools.FieldRef<SymbolOverrideController, KBatchedAnimController> animOf =
            AccessTools.FieldRefAccess<SymbolOverrideController, KBatchedAnimController>("animController");
        static readonly AccessTools.FieldRef<SymbolOverrideController, FaceGraph> faceOf =
            AccessTools.FieldRefAccess<SymbolOverrideController, FaceGraph>("faceGraph");
        static readonly AccessTools.FieldRef<SymbolOverrideController, bool> sortPending =
            AccessTools.FieldRefAccess<SymbolOverrideController, bool>("requiresSorting");
        static readonly AccessTools.FieldRef<KAnimBatch.AtlasList, List<Texture2D>> texturesOf =
            AccessTools.FieldRefAccess<KAnimBatch.AtlasList, List<Texture2D>>("atlases");
        static readonly AccessTools.FieldRef<KAnimBatch.AtlasList, int> startOf =
            AccessTools.FieldRefAccess<KAnimBatch.AtlasList, int>("startIdx");
        // Since U60 a controller's block is made on its getter's first read after a reset (a
        // pending symbol count); before, the getter returns the field.
        static readonly bool lazyBlocks = AccessTools.Field(typeof(KAnimControllerBase), "pendingSymbolOverrideInfoSymbolCount") != null;
        static readonly AccessTools.FieldRef<KAnimControllerBase, SymbolOverrideInfoGpuData> blockField = lazyBlocks
            ? AccessTools.FieldRefAccess<KAnimControllerBase, SymbolOverrideInfoGpuData>("_symbolOverrideInfoGpuData") : null;
        static readonly AccessTools.FieldRef<KAnimControllerBase, int> blockPending = lazyBlocks
            ? AccessTools.FieldRefAccess<KAnimControllerBase, int>("pendingSymbolOverrideInfoSymbolCount") : null;

        /// The controller's block as it is, without making one: null when it has none, or a new
        /// one is pending.
        static SymbolOverrideInfoGpuData BlockOf(KAnimControllerBase anim)
            => !lazyBlocks ? anim.symbolOverrideInfoGpuData : blockPending(anim) >= 0 ? null : blockField(anim);

        static readonly Comparison<SymbolOverrideController.SymbolEntry> byPriority = (x, y) => x.priority - y.priority;

        public static void Install(Harmony harmony)
        {
            var type = typeof(SymbolOverrideInfoGpuData);
            harmony.Patch(AccessTools.Method(type, nameof(SymbolOverrideInfoGpuData.SetSymbolOverrideInfo),
                    new[] { typeof(int), typeof(int), typeof(int), typeof(KBatchGroupData), typeof(int), typeof(int) }),
                prefix: new HarmonyMethod(typeof(FastSymbolOverrides), nameof(SetRange)));
            harmony.Patch(AccessTools.Method(type, nameof(SymbolOverrideInfoGpuData.SetSymbolOverrideInfo),
                    new[] { typeof(int), typeof(KAnim.Build.SymbolFrameInstance).MakeByRefType() }),
                prefix: new HarmonyMethod(typeof(FastSymbolOverrides), nameof(SetOne)));
            if (plans || timing)
            {
                foreach (var method in KeptCalls())
                    if (method == null) throw new InvalidOperationException($"{Name}: a method the kept applications stand for was not found");
                harmony.Patch(AccessTools.Method(typeof(SymbolOverrideController), nameof(SymbolOverrideController.ApplyOverrides)),
                    prefix: new HarmonyMethod(typeof(FastSymbolOverrides), nameof(Apply)) { priority = Priority.Last },
                    postfix: timing ? new HarmonyMethod(typeof(FastSymbolOverrides), nameof(Applied)) : null);
                var manager = new HarmonyMethod(typeof(FastSymbolOverrides), nameof(NewAnimData));
                harmony.Patch(AccessTools.Method(typeof(KAnimBatchManager), nameof(KAnimBatchManager.CreateInstance)), postfix: manager);
                harmony.Patch(AccessTools.Method(typeof(KAnimBatchManager), nameof(KAnimBatchManager.DestroyInstance)), postfix: manager);
            }
            if (timing)
                harmony.Patch(AccessTools.Method(typeof(KAnimBatch), "WriteSymbolOverrideInfoTex"),
                    postfix: new HarmonyMethod(typeof(FastSymbolOverrides), nameof(Uploaded)));
        }

        /// What a kept application stands for: patched by another owner, applications run in full.
        internal static IEnumerable<System.Reflection.MethodBase> KeptCalls()
        {
            yield return AccessTools.Method(typeof(SymbolOverrideController), nameof(SymbolOverrideController.ApplyOverrides));
            yield return AccessTools.Method(typeof(KBatchedAnimController), nameof(KBatchedAnimController.SetSymbolOverrides));
            yield return AccessTools.Method(typeof(KBatchedAnimController), nameof(KBatchedAnimController.GetBatch));
            yield return AccessTools.PropertyGetter(typeof(KBatchedAnimController), nameof(KBatchedAnimController.batchGroupID));
            yield return AccessTools.PropertyGetter(typeof(KAnimControllerBase), nameof(KAnimControllerBase.symbolOverrideInfoGpuData));
            yield return AccessTools.Method(typeof(KAnimBatchManager), nameof(KAnimBatchManager.Instance));
            yield return AccessTools.Method(typeof(KAnimBatchManager), nameof(KAnimBatchManager.GetBatchGroupData));
            yield return AccessTools.Method(typeof(KBatchGroupData), nameof(KBatchGroupData.GetSymbol), new[] { typeof(KAnimHashedString) });
            yield return AccessTools.Method(typeof(KAnim.Build), nameof(KAnim.Build.GetTexture), new[] { typeof(int) });
            var atlas = typeof(KAnimBatch.AtlasList);
            yield return AccessTools.Method(atlas, nameof(KAnimBatch.AtlasList.GetAtlasIdx));
            yield return AccessTools.Method(atlas, nameof(KAnimBatch.AtlasList.Add));
            yield return AccessTools.Method(atlas, nameof(KAnimBatch.AtlasList.Clear));
            var type = typeof(SymbolOverrideInfoGpuData);
            yield return AccessTools.Method(type, nameof(SymbolOverrideInfoGpuData.SetSymbolOverrideInfo),
                new[] { typeof(int), typeof(int), typeof(int), typeof(KBatchGroupData), typeof(int), typeof(int) });
            yield return AccessTools.Method(type, nameof(SymbolOverrideInfoGpuData.SetSymbolOverrideInfo),
                new[] { typeof(int), typeof(KAnim.Build.SymbolFrameInstance).MakeByRefType() });
        }

        static bool checkedPatches, off;
        static int callsSincePatchCheck;

        static void CheckPatches()
        {
            foreach (var method in KeptCalls())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var owner in info.Owners)
                    if (!ModOverlapsCore.IsTweakOwner(owner))
                    {
                        off = true;
                        global::Debug.Log($"{Name}: applications run in full ({owner} patches {method.DeclaringType?.Name}.{method.Name})");
                        return;
                    }
            }
        }

        /// The anim data's generation: kept calls name its batch group data and textures.
        static int animData;

        static void NewAnimData() => animData++;

        /// Whether an entry already holds what the setters store for this frame instance, value for
        /// value (Vector2's == is approximate, so components are compared).
        static bool Holds(in SymbolOverrideInfoGpuData.SymbolOverrideInfo info, float atlas, in KAnim.Build.SymbolFrameInstance frame)
            => info.atlas == atlas && info.isoverriden == 1f && Same(info.bboxMin, frame.bboxMin) && Same(info.bboxMax, frame.bboxMax)
               && Same(info.uvMin, frame.uvMin) && Same(info.uvMax, frame.uvMax);

        static bool Same(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;

        // The block of the application running now (main thread), its log, and whether the
        // write going on is one of the application's own kept calls.
        static SymbolOverrideInfoGpuData openBlock;
        static OverrideBlockLog openLog;
        static bool ownWrite;

        static readonly ConditionalWeakTable<SymbolOverrideInfoGpuData, OverrideBlockLog> logs = new ConditionalWeakTable<SymbolOverrideInfoGpuData, OverrideBlockLog>();
        static readonly ConditionalWeakTable<SymbolOverrideInfoGpuData, OverrideBlockLog>.CreateValueCallback newLog = _ => new OverrideBlockLog();

        static OverrideBlockLog LogOf(SymbolOverrideInfoGpuData block)
        {
            if (ReferenceEquals(block, openBlock)) return openLog;
            return logs.TryGetValue(block, out var log) ? log : null;
        }

        /// A setter call is going to write entries [start, start + count) of `block`.
        static void Writing(SymbolOverrideInfoGpuData block, int start, int count)
        {
            var log = LogOf(block);
            if (log == null) return;
            if (log.Open)
            {
                var bytes = converter(block).bytes;
                for (int i = 0; i < count; i++) log.Writing(bytes, start + i);
            }
            if (!ownWrite)
                for (int i = 0; i < count; i++) log.Written(start + i);
        }

        /// SetSymbolOverrideInfo(start, frames, atlas, source, sourceStart, sourceFrames): false
        /// (skipped) when every entry it writes already holds its value.
        static bool SetRange(SymbolOverrideInfoGpuData __instance, int symbol_start_idx, int symbol_num_frames, int atlas_idx, KBatchGroupData source_data, int source_start_idx, int source_num_frames)
        {
            var infos = converter(__instance).symbolOverrideInfos;
            for (int i = 0; i < symbol_num_frames; i++)
            {
                var frame = source_data.symbolFrameInstances[source_start_idx + Math.Min(source_num_frames - 1, i)];
                if (!Holds(infos[symbol_start_idx + i], atlas_idx, frame))
                {
                    Writing(__instance, symbol_start_idx, symbol_num_frames);
                    return true;
                }
            }
            return false;
        }

        /// SetSymbolOverrideInfo(index, ref frame): false (skipped) when the entry already holds it.
        static bool SetOne(SymbolOverrideInfoGpuData __instance, int symbol_idx, ref KAnim.Build.SymbolFrameInstance symbol_frame_instance)
        {
            if (Holds(converter(__instance).symbolOverrideInfos[symbol_idx], symbol_frame_instance.buildImageIdx, symbol_frame_instance)) return false;
            Writing(__instance, symbol_idx, 1);
            return true;
        }

        /// One call of an application: an atlas add (Texture set), else a block write.
        struct Step
        {
            public Texture2D Texture;
            public int Start, Frames, Atlas, SourceStart, SourceFrames;
            public KBatchGroupData Source;
        }

        /// A controller's last application: its inputs and calls.
        sealed class Plan
        {
            public bool Valid;
            public int Version, Count, AnimData, BatchId, BatchStart, GroupSymbols, AtlasCount;
            public KAnimBatch Batch;
            public HashedString GroupId;
            public KBatchGroupData Group;
            public SymbolOverrideInfoGpuData Block;
            public OverrideBlockLog Log;
            public Texture2D[] BatchAtlases = new Texture2D[8];
            public Step[] Steps = new Step[32];
            /// Each step's entries written: [Starts[i], Starts[i] + Lengths[i]) (none for an add).
            public int[] Starts = new int[32], Lengths = new int[32];
            public int StepCount;
            /// Two steps write a same entry: applications are made in full.
            public bool Overlapping;
            // Steps to redo (Redo[i] == Pass: step i is in ToRedo), scratch for Overlap.
            public int[] Redo = new int[32], ToRedo = new int[32], Order = new int[32];
            public int Pass;

            public void Add(in Step step)
            {
                if (StepCount == Steps.Length)
                {
                    Array.Resize(ref Steps, Steps.Length * 2);
                    Array.Resize(ref Starts, Steps.Length);
                    Array.Resize(ref Lengths, Steps.Length);
                    Array.Resize(ref Redo, Steps.Length);
                    Array.Resize(ref ToRedo, Steps.Length);
                    Array.Resize(ref Order, Steps.Length);
                }
                Starts[StepCount] = step.Start;
                Lengths[StepCount] = step.Texture != null ? 0 : step.Frames;
                Steps[StepCount++] = step;
            }
        }

        static readonly ConditionalWeakTable<SymbolOverrideController, Plan> plansOf = new ConditionalWeakTable<SymbolOverrideController, Plan>();
        static readonly ConditionalWeakTable<SymbolOverrideController, Plan>.CreateValueCallback newPlan = _ => new Plan();

        // Counts and times (ONI_TWEAK_TIMING) and checks (ONI_TWEAK_VERIFY), logged every 600 frames.
        static long verified, others, calls, kept, restored, unchangedBlocks, uploads, mismatches, checks;
        static readonly long[] misses = new long[Enum.GetValues(typeof(Miss)).Length];
        static long ticks, started;
        static int windowStart = -1;

        enum Miss { None, First, Overlap, Version, Sort, Count, Block, Batch, Atlases, Group, AnimData, Written }

        /// ONI_TWEAK_TIMING: the counts a frame over the last 600 frames or more; ONI_TWEAK_VERIFY:
        /// the checks so far.
        static void Frame()
        {
            int frame = Time.frameCount;
            if (windowStart < 0) windowStart = frame;
            int n = frame - windowStart;
            if (n < 600) return;
            windowStart = frame;
            double ms = 1000.0 / Stopwatch.Frequency / n;
            var why = new System.Text.StringBuilder();
            for (int i = 1; i < misses.Length; i++)
                if (misses[i] > 0) why.Append($" {(Miss)i}={misses[i] / (double)n:F2}");
            if (timing)
                global::Debug.Log($"{Name}: over {n} frames, a frame: {ticks * ms:F3} ms, {calls / (double)n:F1} every-frame applications ({kept / (double)n:F1} kept,{why}), {others / (double)n:F2} others, {restored / (double)n:F1} writes redone, {unchangedBlocks / (double)n:F1} blocks unchanged, {uploads / (double)n:F1} uploads");
            if (verify) global::Debug.Log($"{Name}: {verified} kept applications checked, {checks} checks, {mismatches} mismatches");
            others = calls = kept = restored = unchangedBlocks = uploads = ticks = 0;
            Array.Clear(misses, 0, misses.Length);
        }

        static void Uploaded(bool __result)
        {
            if (__result) uploads++;
        }

        static void Applied()
        {
            if (started != 0) ticks += Stopwatch.GetTimestamp() - started;
            started = 0;
        }

        /// SymbolOverrideController.ApplyOverrides: false (made here) when the application ran
        /// from its kept calls or in full here.
        static bool Apply(SymbolOverrideController __instance, bool __runOriginal)
        {
            if (timing || verify) Frame();
            if (timing)
            {
                if (!__instance.applySymbolOverridesEveryFrame) others++;
                started = Stopwatch.GetTimestamp();
            }
            if (!__runOriginal || !__instance.applySymbolOverridesEveryFrame) return true;
            if (!checkedPatches || ++callsSincePatchCheck >= 100000)
            {
                checkedPatches = true;
                callsSincePatchCheck = 0;
                if (!off) CheckPatches();
            }
            if (timing) calls++;
            if (off) return true;
            var anim = animOf(__instance);
            var batch = anim != null ? anim.GetBatch() : null;
            if (batch == null) return true;
            var plan = plansOf.GetValue(__instance, newPlan);
            var miss = Check(__instance, anim, batch, plan);
            if (timing) misses[(int)miss]++;
            if (!plans)
            {
                // Classified only: the inputs kept for the next call, the game applies.
                Keep(__instance, anim, batch, plan);
                return true;
            }
            bool made = miss == Miss.None ? Replay(__instance, anim, plan) : Full(__instance, anim, batch, plan);
            return !made;
        }

        /// Why the kept calls can't stand for this application (None: they can).
        static Miss Check(SymbolOverrideController soc, KBatchedAnimController anim, KAnimBatch batch, Plan plan)
        {
            if (!plan.Valid) return plan.Overlapping ? Miss.Overlap : Miss.First;
            if (sortPending(soc)) return Miss.Sort;
            if (plan.Version != soc.version) return Miss.Version;
            if (plan.Count != overridesOf(soc).Count) return Miss.Count;
            var block = BlockOf(anim);
            if (block == null || !ReferenceEquals(block, plan.Block)) return Miss.Block;
            if (!ReferenceEquals(batch, plan.Batch) || batch.id != plan.BatchId) return Miss.Batch;
            var atlases = texturesOf(batch.atlases);
            if (atlases.Count != plan.AtlasCount || startOf(batch.atlases) != plan.BatchStart) return Miss.Atlases;
            for (int i = 0; i < atlases.Count; i++)
                if (!ReferenceEquals(atlases[i], plan.BatchAtlases[i])) return Miss.Atlases;
            if (plan.AnimData != animData) return Miss.AnimData;
            if (anim.batchGroupID != plan.GroupId || (plan.Group != null ? plan.Group.frameElementSymbols.Count : -1) != plan.GroupSymbols) return Miss.Group;
            if (plans && (plan.Log == null || plan.Log.WrittenOverflow)) return Miss.Written;
            return Miss.None;
        }

        /// Keeps this application's inputs (not its calls) in `plan`.
        static void Keep(SymbolOverrideController soc, KBatchedAnimController anim, KAnimBatch batch, Plan plan)
        {
            plan.Version = soc.version;
            plan.Count = overridesOf(soc).Count;
            plan.Block = BlockOf(anim);
            plan.Batch = batch;
            plan.BatchId = batch.id;
            var atlases = texturesOf(batch.atlases);
            plan.AtlasCount = atlases.Count;
            plan.BatchStart = startOf(batch.atlases);
            if (plan.BatchAtlases.Length < atlases.Count) plan.BatchAtlases = new Texture2D[atlases.Count];
            atlases.CopyTo(plan.BatchAtlases);
            plan.AnimData = animData;
            plan.GroupId = anim.batchGroupID;
            plan.GroupSymbols = plan.Group != null ? plan.Group.frameElementSymbols.Count : -1;
            plan.Valid = true;
            if (plans && plan.Block != null) plan.Log = logs.GetValue(plan.Block, newLog);
        }

        // Lookups' scratch (main thread).
        static readonly Dictionary<HashedString, (int Priority, int Index)> byTarget = new Dictionary<HashedString, (int, int)>();
        static readonly List<SymbolOverrideController.SymbolEntry> resolved = new List<SymbolOverrideController.SymbolEntry>();
        static readonly Dictionary<KAnim.Build, (int Atlas, KBatchGroupData Data)> byBuild = new Dictionary<KAnim.Build, (int, KBatchGroupData)>();
        static readonly List<Texture2D> added = new List<Texture2D>();

        /// The game's lookups for this application, as ApplyOverrides makes them (no side effect
        /// but the pending sort, which comes first there too): its calls into `plan`, or false
        /// when one of them throws or would log (the game's code then runs).
        static bool Lookups(SymbolOverrideController soc, KBatchedAnimController anim, KAnimBatch batch, Plan plan)
        {
            try
            {
                var list = overridesOf(soc);
                if (sortPending(soc))
                {
                    list.Sort(byPriority);
                    sortPending(soc) = false;
                }
                var manager = KAnimBatchManager.Instance();
                var group = manager.GetBatchGroupData(anim.batchGroupID);
                int count = batch.atlases.Count;
                byTarget.Clear();
                resolved.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    var entry = list[i];
                    if (byTarget.TryGetValue(entry.targetSymbol, out var value))
                    {
                        if (entry.priority > value.Priority)
                        {
                            byTarget[entry.targetSymbol] = (entry.priority, value.Index);
                            resolved[value.Index] = entry;
                        }
                    }
                    else
                    {
                        byTarget[entry.targetSymbol] = (entry.priority, resolved.Count);
                        resolved.Add(entry);
                    }
                }
                byBuild.Clear();
                added.Clear();
                plan.StepCount = 0;
                for (int i = 0; i < resolved.Count; i++)
                {
                    var entry = resolved[i];
                    var build = entry.sourceSymbol.build;
                    if (!byBuild.TryGetValue(build, out var info))
                    {
                        // GetTexture logs a bad index (left to the game's code).
                        if (build.textureCount < 1) return false;
                        var data = manager.GetBatchGroupData(build.batchTag);
                        var texture = build.GetTexture(0);
                        int atlas = batch.atlases.GetAtlasIdx(texture);
                        if (atlas < 0)
                        {
                            // AtlasList.Add: the index of the texture in the controller's list
                            // (cleared to start at `count`), added when missing.
                            if (texture == null) return false;
                            plan.Add(new Step { Texture = texture });
                            int at = added.IndexOf(texture);
                            if (at == -1)
                            {
                                at = added.Count;
                                added.Add(texture);
                            }
                            atlas = at + count;
                        }
                        info = (atlas, data);
                        byBuild[build] = info;
                    }
                    var symbol = group.GetSymbol(entry.targetSymbol);
                    if (symbol != null)
                        plan.Add(new Step
                        {
                            Start = symbol.firstFrameIdx, Frames = symbol.numFrames, Atlas = info.Atlas, Source = info.Data,
                            SourceStart = entry.sourceSymbol.firstFrameIdx, SourceFrames = entry.sourceSymbol.numFrames,
                        });
                }
                plan.Group = group;
                plan.Overlapping = OverrideBlockLog.Overlap(plan.Starts, plan.Lengths, plan.StepCount, plan.Order);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// The application in full: the lookups, then their calls in order and the face.
        static bool Full(SymbolOverrideController soc, KBatchedAnimController anim, KAnimBatch batch, Plan plan)
        {
            plan.Valid = false;
            // The inputs as they are before the application (the face's speech can add an
            // override during it: the next application is then a full one again).
            int version = soc.version, count = overridesOf(soc).Count;
            if (!Lookups(soc, anim, batch, plan)) return false;
            var block = anim.symbolOverrideInfoGpuData;
            Keep(soc, anim, batch, plan);
            plan.Version = version;
            plan.Count = count;
            plan.Log?.ClearWritten();
            Run(soc, anim, plan, block, full: true);
            plan.Valid = !plan.Overlapping && plan.Block != null && ReferenceEquals(BlockOf(anim), plan.Block);
            return true;
        }

        /// The application from its kept calls: the atlas calls, the block entries written since
        /// redone, the face.
        static bool Replay(SymbolOverrideController soc, KBatchedAnimController anim, Plan plan)
        {
            if (timing) kept++;
            if (verify) Verify(soc, anim, plan);
            Run(soc, anim, plan, plan.Block, full: false);
            return true;
        }

        static void Run(SymbolOverrideController soc, KBatchedAnimController anim, Plan plan, SymbolOverrideInfoGpuData block, bool full)
        {
            var atlases = atlasesOf(soc);
            var log = plan.Log;
            int before = block != null ? block.version : 0;
            if (log != null)
            {
                openBlock = block;
                openLog = log;
                log.Begin();
            }
            bool done = false;
            try
            {
                atlases.Clear(plan.AtlasCount);
                var steps = plan.Steps;
                if (full)
                {
                    ownWrite = true;
                    for (int i = 0; i < plan.StepCount; i++)
                    {
                        ref var s = ref steps[i];
                        if (s.Texture != null) atlases.Add(s.Texture);
                        else anim.SetSymbolOverrides(s.Start, s.Frames, s.Atlas, s.Source, s.SourceStart, s.SourceFrames);
                    }
                    ownWrite = false;
                }
                else
                {
                    for (int i = 0; i < plan.StepCount; i++)
                        if (steps[i].Texture != null) atlases.Add(steps[i].Texture);
                    // The steps writing an entry written since, redone in order: the others would
                    // find their entries holding their values (the setters skip such a call).
                    int n = log.TakeWritten(out var entries), redo = 0;
                    if (++plan.Pass == int.MaxValue)
                    {
                        Array.Clear(plan.Redo, 0, plan.Redo.Length);
                        plan.Pass = 1;
                    }
                    for (int e = 0; e < n; e++)
                    {
                        int i = OverrideBlockLog.LastWriting(plan.Starts, plan.Lengths, plan.StepCount, entries[e]);
                        if (i < 0 || plan.Redo[i] == plan.Pass) continue;
                        plan.Redo[i] = plan.Pass;
                        plan.ToRedo[redo++] = i;
                    }
                    Array.Sort(plan.ToRedo, 0, redo);
                    ownWrite = true;
                    for (int k = 0; k < redo; k++)
                    {
                        ref var s = ref steps[plan.ToRedo[k]];
                        anim.SetSymbolOverrides(s.Start, s.Frames, s.Atlas, s.Source, s.SourceStart, s.SourceFrames);
                    }
                    if (timing) restored += redo;
                    ownWrite = false;
                }
                var face = faceOf(soc);
                if (face != null) face.ApplyShape();
                done = true;
            }
            finally
            {
                ownWrite = false;
                if (log != null)
                {
                    bool changed = log.End(converter(block).bytes);
                    if (done && !changed && block.version != before)
                    {
                        blockVersion(block) = before;
                        if (timing) unchangedBlocks++;
                    }
                    openBlock = null;
                    openLog = null;
                }
                if (!done) plan.Valid = false;
            }
        }

        /// ONI_TWEAK_VERIFY: the lookups redone give the kept calls, and every entry they write
        /// holds what they write (but the logged ones, whose steps are redone).
        static void Verify(SymbolOverrideController soc, KBatchedAnimController anim, Plan plan)
        {
            verified++;
            checks++;
            var redo = new Plan();
            if (!Lookups(soc, anim, anim.GetBatch(), redo))
            {
                global::Debug.Log($"{Name}: the lookups failed for {anim.name}, kept calls used ({++mismatches})");
                return;
            }
            bool same = redo.StepCount == plan.StepCount;
            for (int i = 0; same && i < plan.StepCount; i++)
            {
                var a = plan.Steps[i];
                var b = redo.Steps[i];
                same = ReferenceEquals(a.Texture, b.Texture) && a.Start == b.Start && a.Frames == b.Frames && a.Atlas == b.Atlas
                       && ReferenceEquals(a.Source, b.Source) && a.SourceStart == b.SourceStart && a.SourceFrames == b.SourceFrames;
            }
            if (!same)
            {
                global::Debug.Log($"{Name}: the kept calls of {anim.name} differ from its lookups ({++mismatches})");
                return;
            }
            // Steps writing a logged entry are redone next; every other entry must hold its value.
            var log = plan.Log;
            int n = log.TakeWritten(out var entries);
            var written = new HashSet<int>();
            for (int e = 0; e < n; e++)
            {
                written.Add(entries[e]);
                log.Written(entries[e]);
            }
            var infos = converter(plan.Block).symbolOverrideInfos;
            var last = new Dictionary<int, int>();
            for (int i = 0; i < plan.StepCount; i++)
                if (plan.Steps[i].Texture == null)
                    for (int f = 0; f < plan.Steps[i].Frames; f++) last[plan.Steps[i].Start + f] = i;
            foreach (var pair in last)
            {
                if (written.Contains(pair.Key)) continue;
                var s = plan.Steps[pair.Value];
                var frame = s.Source.symbolFrameInstances[s.SourceStart + Math.Min(s.SourceFrames - 1, pair.Key - s.Start)];
                checks++;
                if (!Holds(infos[pair.Key], s.Atlas, frame))
                {
                    global::Debug.Log($"{Name}: entry {pair.Key} of {anim.name} no longer holds its kept call's value and was not logged ({++mismatches})");
                    return;
                }
            }
        }
    }
}
