using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-anim-batches` (opt-in). Every frame KAnimBatchManager.UpdateDirty walks
    /// the visible batch sets, and each batch with dirty controllers writes, per dirty
    /// controller and in order: its instance data (frame, position, transform, colours: 112
    /// bytes into the batch's data texture), its symbol instance block (into the batch's symbol
    /// texture, when its version changed), then applies its symbol overrides (game code with side
    /// effects: SymbolOverrideController, FaceGraph, the batch's atlases and property block) and
    /// writes the override block. Gluttony: about 1.9 ms a frame on Linux, spent reading
    /// scattered controllers and copying blocks.
    ///
    /// The tweak does the first two writes for every dirty batch beforehand, on worker threads
    /// (one batch to a worker at a time, the main thread waiting), then lets the game's loop run
    /// as before, with those two calls replaced by a check that skips a controller already
    /// written. The override block write, which must follow the controller's own override
    /// application, is answered in the loop (written or not, by the game's own slot test) and
    /// its copy made after the loop, again on workers. Everything else (applying overrides,
    /// texture creation, Apply, the dirty bookkeeping) runs in the game's own order on the main
    /// thread; an override texture whose blocks were written after the loop in the pass after it
    /// was made is applied again after them (its first Apply started its upload). Each batch's
    /// writes touch only its own textures and slots, and the controllers registered with it (a
    /// controller is in one batch). What the writes read (frame, visibility, position,
    /// transforms, the symbol instance and override blocks) is not changed by applying another
    /// controller's overrides, so writing them earlier or later gives the same bytes. The
    /// engine's transform caches are safe for concurrent readers. Since U60 a controller's
    /// override block is created on first read (KAnimControllerBase.symbolOverrideInfoGpuData);
    /// the loop's own read in WriteOverrides, on the main thread, creates it before a deferred
    /// write reads it on a worker, and only LoadAnims (never reached from the loop) can reset it.
    ///
    /// Written on the main thread in the game's order as before: batches being set up (Init),
    /// UI batches (their transform reads UI layout), controllers that are not exactly a
    /// KBatchedAnimController, and those whose position comes from a delegate other than
    /// KBatchedAnimTracker's. Frames with few dirty controllers are left to the game. If another
    /// Harmony owner patches any method on the write path, the tweak stays off.
    ///
    /// With ONI_TWEAK_VERIFY=1 each skipped write is redone at its original place into scratch
    /// memory and compared byte for byte with what the worker wrote, the symbol slot's version is
    /// checked against the controller's, a written controller the game's loop never reached
    /// counts as a mismatch, and each override block written after the loop must still have the
    /// version it had when the loop reached it. Gluttony (Linux, stopwatch, steady windows):
    /// 1.92–2.01 → 1.49–1.65 ms a frame; what stays on the main thread is mostly applying the
    /// overrides (SymbolOverrideController, FaceGraph), which is game logic with side effects.
    [GameMember(GameUse.Transpiler, "System.Int32 KAnimBatch::UpdateDirty(System.Int32)", "179b53cbc38cbf63 linux-744825 win-744825", "a037122a00fc858c linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023", Rewrites = 3)]
    [GameMember(GameUse.Uses, "System.Void KAnimBatch::WriteBatchedAnimInstanceData(System.Int32,KAnimConverter/IAnimConverter,Unity.Collections.NativeArray`1<System.Byte>)")]
    [GameMember(GameUse.Uses, "System.Boolean KAnimBatch::WriteSymbolInstanceData(System.Int32,KAnimConverter/IAnimConverter,Unity.Collections.NativeArray`1<System.Byte>)")]
    [GameMember(GameUse.Mirrors, "System.Boolean KAnimBatch::WriteSymbolOverrideInfoTex(System.Int32,KAnimConverter/IAnimConverter,Unity.Collections.NativeArray`1<System.Byte>)", "4ddcc4d476fe3321 linux-761015 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761015 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Prefix | GameUse.Postfix, "System.Int32 KAnimBatchManager::UpdateDirty(System.Int32)")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector4 KBatchedAnimTracker::MyAnimGetPosition()")]
    [GameMember(GameUse.Uses, "SymbolOverrideInfoGpuData KAnimControllerBase::get_symbolOverrideInfoGpuData()")]
    [GameMember(GameUse.Uses, "System.Int32 KBatchedAnimController::GetCurrentFrameIndex()")]
    [GameMember(GameUse.Uses, "System.Boolean KBatchedAnimController::IsVisible()")]
    [GameMember(GameUse.Uses, "System.Int32 KBatchedAnimController::GetCurrentNumFrames()")]
    [GameMember(GameUse.Uses, "System.UInt32 KBatchedAnimController::GetCustomShaderParams()")]
    [GameMember(GameUse.Uses, "System.Int32 KBatchedAnimController::GetFirstFrameIndex()")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector4 KBatchedAnimController::GetPositionData()")]
    [GameMember(GameUse.Uses, "KBatchedAnimInstanceData KBatchedAnimController::GetBatchInstanceData()")]
    [GameMember(GameUse.Uses, "UnityEngine.Matrix4x4 KBatchedAnimController::GetSymbolTransform(HashedString,System.Boolean&)")]
    [GameMember(GameUse.Uses, "Matrix2x3 KBatchedAnimController::GetSymbolLocalTransform(HashedString,System.Boolean&)")]
    [GameMember(GameUse.Uses, "Matrix2x3 KBatchedAnimController::GetTransformMatrix()")]
    [GameMember(GameUse.Uses, "SymbolInstanceGpuData KAnimControllerBase::get_symbolInstanceGpuData()")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector3 KAnimControllerBase::get_PositionIncludingOffset()")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector3 KAnimControllerBase::get_Offset()")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector3 KAnimControllerBase::GetPivotSymbolPosition()")]
    [GameMember(GameUse.Uses, "System.Void SymbolOverrideInfoGpuData::WriteToTexture(Unity.Collections.NativeArray`1<System.Byte>,System.Int32,System.Int32)")]
    [GameMember(GameUse.Uses, "System.Void KBatchedAnimInstanceData::WriteToTexture(Unity.Collections.NativeArray`1<System.Byte>,System.Int32,System.Int32)")]
    [GameMember(GameUse.Uses, "System.Void SymbolInstanceGpuData::WriteToTexture(Unity.Collections.NativeArray`1<System.Byte>,System.Int32,System.Int32)")]
    [GameMember(GameUse.Uses, "UnityEngine.Vector3 TransformExtensions::GetPosition(UnityEngine.Transform)")]
    public static class FastAnimBatches
    {
        public const string Name = "fast-anim-batches";

        const int InstanceBytes = 112;
        /// Below this many dirty controllers in a frame the game's loop runs alone.
        const int MinParallel = 128;

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static readonly bool timing = Environment.GetEnvironmentVariable("ONI_TWEAK_TIMING") == "1";
        static readonly bool serialOnly = Environment.GetEnvironmentVariable("ONI_FAST_ANIM_BATCHES_SERIAL") == "1";


        static readonly MethodInfo writeInstanceMethod = AccessTools.Method(typeof(KAnimBatch), "WriteBatchedAnimInstanceData");
        static readonly MethodInfo writeSymbolsMethod = AccessTools.Method(typeof(KAnimBatch), "WriteSymbolInstanceData");
        static readonly MethodInfo writeOverridesMethod = AccessTools.Method(typeof(KAnimBatch), "WriteSymbolOverrideInfoTex");
        static readonly Action<KAnimBatch, int, KAnimConverter.IAnimConverter, NativeArray<byte>> writeInstance =
            AccessTools.MethodDelegate<Action<KAnimBatch, int, KAnimConverter.IAnimConverter, NativeArray<byte>>>(writeInstanceMethod);
        static readonly Func<KAnimBatch, int, KAnimConverter.IAnimConverter, NativeArray<byte>, bool> writeSymbols =
            AccessTools.MethodDelegate<Func<KAnimBatch, int, KAnimConverter.IAnimConverter, NativeArray<byte>, bool>>(writeSymbolsMethod);
        static readonly Func<KAnimBatch, int, KAnimConverter.IAnimConverter, NativeArray<byte>, bool> writeOverrides =
            AccessTools.MethodDelegate<Func<KAnimBatch, int, KAnimConverter.IAnimConverter, NativeArray<byte>, bool>>(writeOverridesMethod);
        static readonly MethodInfo trackerPosition = AccessTools.Method(typeof(KBatchedAnimTracker), "MyAnimGetPosition");
        /// Delegate.HasSingleTarget (.NET 9; this assembly is built for netstandard2.1), or null.
        static readonly Func<Delegate, bool> hasSingleTarget = typeof(Delegate).GetProperty("HasSingleTarget")?.GetGetMethod() is MethodInfo getter
            ? (Func<Delegate, bool>)Delegate.CreateDelegate(typeof(Func<Delegate, bool>), getter) : null;

        /// A batch's writes done ahead for the current pass, by controller index.
        sealed class Work
        {
            public KAnimBatch Batch;
            public int Pass;
            public int[] DonePass = new int[0];
            public KAnimConverter.IAnimConverter[] Controller = new KAnimConverter.IAnimConverter[0];
            public bool[] SymbolsWritten = new bool[0];
            /// Override blocks to write after the loop: index, controller, its version then.
            public int DeferPass;
            public NativeArray<byte> OverrideData;
            /// The override texture whose first upload the deferred writes have waited for.
            public Texture2D OverrideTexture;
            public readonly List<(int Index, KAnimConverter.IAnimConverter Controller, int Version)> Deferred = new List<(int, KAnimConverter.IAnimConverter, int)>();

            public void Fit(int count)
            {
                if (DonePass.Length >= count) return;
                int n = Math.Max(count, DonePass.Length * 2);
                Array.Resize(ref DonePass, n);
                Array.Resize(ref Controller, n);
                Array.Resize(ref SymbolsWritten, n);
            }
        }

        /// By KAnimBatch.id (sequential from 0).
        static Work[] works = new Work[256];
        static readonly List<Work> pending = new List<Work>();
        static int pass;
        static bool checkedPatches, off;
        static int passesSincePatchCheck;
        static long written, consumed, mismatches, checks, frames, parallelFrames;
        static long ticksTotal, ticksParallel;
        static long started;
        // Verify mode's scratch for redone writes (main thread only): a texture's memory, as
        // the game makes its NativeArrays (64 KB).
        static Texture2D scratchTexture;
        static NativeArray<byte> scratch;

        static readonly Action<int> body = Body;
        static readonly List<Work> deferring = new List<Work>();
        static readonly List<Work> firstUploads = new List<Work>();
        static readonly Action<int> deferBody = DeferBody;
        static long deferred;

        public static void Install(Harmony harmony)
        {
            if (writeInstanceMethod == null || writeSymbolsMethod == null || writeOverridesMethod == null || trackerPosition == null)
                throw new InvalidOperationException($"{Name}: the game's batch write methods were not found");
            // Every write-path method must exist: a missing one (a game update renamed it) fails
            // the install here, naming it, instead of turning the tweak off at its first frame.
            foreach (var method in WritePath())
                if (method == null) throw new InvalidOperationException($"{Name}: a write-path method was not found");
            harmony.Patch(AccessTools.Method(typeof(KAnimBatch), nameof(KAnimBatch.UpdateDirty)),
                transpiler: new HarmonyMethod(typeof(FastAnimBatches), nameof(Transpile)));
            harmony.Patch(AccessTools.Method(typeof(KAnimBatchManager), nameof(KAnimBatchManager.UpdateDirty)),
                prefix: new HarmonyMethod(typeof(FastAnimBatches), nameof(Prefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FastAnimBatches), nameof(Postfix)) { priority = Priority.First });
        }

        /// KAnimBatch.UpdateDirty: its two per-controller write calls go through WriteInstance
        /// and WriteSymbols (same arguments, the batch first).
        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int found = 0;
            foreach (var i in instructions)
            {
                if (i.Calls(writeInstanceMethod)) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(FastAnimBatches), nameof(WriteInstance)); found++; }
                else if (i.Calls(writeSymbolsMethod)) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(FastAnimBatches), nameof(WriteSymbols)); found++; }
                else if (i.Calls(writeOverridesMethod)) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(FastAnimBatches), nameof(WriteOverrides)); found++; }
                yield return i;
            }
            // The three write calls (TweakInstall checks the count).
            OniMoono.Tweaks.TranspilerCounts.Report(Name, original, found);
        }

        /// The methods whose code runs on the workers: patched by anyone else, the tweak is off.
        internal static IEnumerable<MethodBase> WritePath()
        {
            yield return AccessTools.Method(typeof(KAnimBatch), nameof(KAnimBatch.UpdateDirty));
            yield return writeInstanceMethod;
            yield return writeSymbolsMethod;
            yield return writeOverridesMethod;
            yield return AccessTools.Method(typeof(SymbolOverrideInfoGpuData), nameof(SymbolOverrideInfoGpuData.WriteToTexture));
            yield return AccessTools.PropertyGetter(typeof(KBatchedAnimController), "symbolOverrideInfoGpuData");
            yield return AccessTools.Method(typeof(KBatchedAnimInstanceData), nameof(KBatchedAnimInstanceData.WriteToTexture));
            yield return AccessTools.Method(typeof(SymbolInstanceGpuData), nameof(SymbolInstanceGpuData.WriteToTexture));
            var c = typeof(KBatchedAnimController);
            // U60 renamed GetPostProcessingParams (a float) to GetCustomShaderParams (a uint,
            // packed with the visibility bit by KBatchedAnimInstanceData).
            foreach (var name in new[] { "GetCurrentFrameIndex", "IsVisible", "GetCurrentNumFrames", "GetCustomShaderParams", "GetFirstFrameIndex",
                         "GetPositionData", "GetBatchInstanceData", "GetSymbolTransform", "GetSymbolLocalTransform" })
                yield return AccessTools.Method(c, name) ?? throw new MissingMethodException(nameof(KBatchedAnimController), name);
            yield return AccessTools.Method(c, nameof(KBatchedAnimController.GetTransformMatrix), Type.EmptyTypes);
            yield return AccessTools.PropertyGetter(c, "symbolInstanceGpuData");
            var b = typeof(KAnimControllerBase);
            yield return AccessTools.PropertyGetter(b, nameof(KAnimControllerBase.PositionIncludingOffset));
            yield return AccessTools.PropertyGetter(b, nameof(KAnimControllerBase.Offset));
            yield return AccessTools.Method(b, nameof(KAnimControllerBase.GetPivotSymbolPosition));
            yield return trackerPosition;
            yield return AccessTools.Method(typeof(TransformExtensions), nameof(TransformExtensions.GetPosition));
        }

        static void CheckPatches()
        {
            IEnumerable<MethodBase> path;
            try { path = new List<MethodBase>(WritePath()); }
            catch (MissingMethodException e) { Disable(e.Message); return; }
            foreach (var method in path)
            {
                if (method == null) { Disable("a write-path method was not found"); return; }
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var owner in info.Owners)
                    if (!ModOverlapsCore.IsTweakOwner(owner)) { Disable($"{owner} patches {method.DeclaringType?.Name}.{method.Name}"); return; }
            }
        }

        static void Disable(string why)
        {
            off = true;
            global::Debug.Log($"{Name}: off ({why})");
        }

        static void Prefix(KAnimBatchManager __instance, bool __runOriginal)
        {
            if (timing) started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!__runOriginal || !__instance.isReady || serialOnly) return;
            // Mods patch while loading; the check is repeated now and then for late ones.
            if (!checkedPatches || ++passesSincePatchCheck >= 3000)
            {
                checkedPatches = true;
                passesSincePatchCheck = 0;
                if (!off) CheckPatches();
            }
            if (off) return;
            ReportAlone();
            frames++;
            pass++;
            pending.Clear();
            int dirty = 0;
            foreach (var set in __instance.activeBatchSets)
            {
                if (!set.dirty) continue;
                for (int i = 0; i < set.batchCount; i++)
                {
                    var batch = set.GetBatch(i);
                    if (!batch.needsWrite || batch.dataTex == null || !batch.isSetup || batch.symbolInstanceTex == null
                        || batch.materialType == KAnimBatchGroup.MaterialType.UI) continue;
                    var ds = batch.dirtySet;
                    if (ds.Count == 0) continue;
                    var work = WorkOf(batch);
                    if (work.Pass == pass) continue; // a set listed twice: its second walk finds it clean
                    work.Pass = pass;
                    work.Fit(batch.size);
                    pending.Add(work);
                    dirty += ds.Count;
                }
            }
            if (dirty < MinParallel || pending.Count < 2)
            {
                pending.Clear();
                return;
            }
            parallelFrames++;
            long t0 = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            WorkerLoop.For(pending.Count, body);
            if (timing) ticksParallel += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        static float aloneLogAt = -1f;
        static int aloneCalls, aloneSeen;

        /// Player.log gets a line at most once a minute while calls of the worker loop found the
        /// pool so far behind that they ran on the main thread alone (WorkerLoop).
        static void ReportAlone()
        {
            float t = Time.realtimeSinceStartup;
            if (aloneLogAt < 0f)
            {
                aloneLogAt = t + 60f;
                aloneCalls = WorkerLoop.Calls;
                aloneSeen = WorkerLoop.CallerAlone;
            }
            if (t < aloneLogAt) return;
            int calls = WorkerLoop.Calls, alone = WorkerLoop.CallerAlone;
            if (alone != aloneSeen)
                global::Debug.Log($"{Name}: worker loop: {alone - aloneSeen} of {calls - aloneCalls} calls ran on the caller alone (pool behind), last minute");
            aloneCalls = calls;
            aloneSeen = alone;
            aloneLogAt = t + 60f;
        }

        static void Postfix()
        {
            if (pending.Count > 0)
            {
                // The override blocks the loop left: each batch's on one worker, then checked.
                if (deferring.Count > 0)
                {
                    long t1 = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    // The loop's Apply came before these writes. A texture's first Apply starts its
                    // upload there (as in Unity), copying the texels while the workers would write
                    // them: in the first pass after a batch gets its override texture, the writes
                    // wait for that copy (the engine's access waits) and the texture is applied
                    // again after them, as the game applies after its writes. Later uploads are
                    // made at the draw, after these writes, so they need neither; applying again
                    // every pass made the device send more of the textures (a late-game colony:
                    // 915 -> 1,442 KB of texel uploads a frame).
                    firstUploads.Clear();
                    foreach (var work in deferring)
                    {
                        var texture = work.Batch.symbolOverrideInfoTex.texture;
                        if (ReferenceEquals(texture, work.OverrideTexture)) continue;
                        work.OverrideTexture = texture;
                        work.Batch.symbolOverrideInfoTex.GetDataPointer();
                        firstUploads.Add(work);
                    }
                    if (deferring.Count == 1) DeferBody(0);
                    else WorkerLoop.For(deferring.Count, deferBody);
                    foreach (var work in firstUploads) work.Batch.symbolOverrideInfoTex.Apply();
                    firstUploads.Clear();
                    if (timing) ticksParallel += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
                    if (verify) VerifyDeferred();
                    deferring.Clear();
                }
                if (verify)
                {
                    // Every controller written ahead must have been reached by the game's loop.
                    foreach (var work in pending)
                        for (int i = 0; i < work.DonePass.Length; i++)
                            if (work.DonePass[i] == work.Pass)
                            {
                                work.DonePass[i] = 0;
                                global::Debug.Log($"{Name}: a controller written ahead was not reached by the game's loop ({++mismatches})");
                            }
                    if (frames % 600 == 0)
                        global::Debug.Log($"{Name}: {written} written ahead, {consumed} skipped in the loop, {deferred} override blocks written after it, {checks} checks, {mismatches} mismatches");
                }
                pending.Clear();
            }
            if (timing)
            {
                ticksTotal += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                if (++timedFrames % 600 == 0)
                {
                    double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency / 600;
                    global::Debug.Log($"{Name}: timing {ticksTotal * ms:F3} ms a frame, workers {ticksParallel * ms:F3} ms, {parallelFrames} of {frames} frames in parallel");
                    ticksTotal = ticksParallel = 0;
                }
            }
        }

        /// KAnimBatch.WriteSymbolOverrideInfoTex, in a frame the workers took part in: when the
        /// slot is stale (the game would write), the block is written after the loop instead
        /// and the game's answer (written) returned now. A new texture's Apply that follows is made
        /// again after those writes (Postfix).
        [MirrorsGame("System.Boolean KAnimBatch::WriteSymbolOverrideInfoTex(System.Int32,KAnimConverter/IAnimConverter,Unity.Collections.NativeArray`1<System.Byte>)")]
        static bool WriteOverrides(KAnimBatch batch, int index, KAnimConverter.IAnimConverter controller, NativeArray<byte> data)
        {
            if (pending.Count == 0) return writeOverrides(batch, index, controller, data);
            var slot = batch.symbolOverrideInfoSlots[index];
            var gpu = controller.symbolOverrideInfoGpuData;
            if (slot.symbolOverrideInfo == gpu && slot.dataVersion == gpu.version) return false;
            var work = WorkOf(batch);
            if (work.DeferPass != pass)
            {
                work.DeferPass = pass;
                work.Deferred.Clear();
                work.OverrideData = data;
                deferring.Add(work);
            }
            work.Deferred.Add((index, controller, gpu.version));
            deferred++;
            return true;
        }

        static void DeferBody(int index)
        {
            var work = deferring[index];
            foreach (var (i, controller, _) in work.Deferred) writeOverrides(work.Batch, i, controller, work.OverrideData);
        }

        /// Each block written after the loop is the one the game would have written in it: its
        /// version unchanged since, and now current in its slot.
        static void VerifyDeferred()
        {
            foreach (var work in deferring)
            {
                var slots = work.Batch.symbolOverrideInfoSlots;
                foreach (var (i, controller, version) in work.Deferred)
                {
                    checks++;
                    var gpu = controller.symbolOverrideInfoGpuData;
                    if (gpu.version != version || slots[i].symbolOverrideInfo != gpu || slots[i].dataVersion != version)
                        global::Debug.Log($"{Name}: override block of {controller.GetName()} changed after the game's loop reached it ({++mismatches})");
                }
            }
        }

        static long timedFrames;

        static Work WorkOf(KAnimBatch batch)
        {
            int id = batch.id;
            if (id >= works.Length) Array.Resize(ref works, Math.Max(id + 1, works.Length * 2));
            var work = works[id];
            if (work == null || work.Batch != batch) works[id] = work = new Work { Batch = batch };
            return work;
        }

        /// One batch's instance and symbol writes, for its dirty controllers in the game's order;
        /// any it cannot write here are left to the game's loop.
        static void Body(int index)
        {
            var work = pending[index];
            var batch = work.Batch;
            var data = batch.dataTex.GetDataPointer();
            var symbols = batch.symbolInstanceTex.GetDataPointer();
            var controllers = batch.Controllers;
            int count = 0;
            foreach (int i in batch.dirtySet)
            {
                var c = controllers[i];
                // The game's loop skips these itself (null or destroyed).
                if (c == null || !(c as UnityEngine.Object != null)) continue;
                if (!(c is KBatchedAnimController k) || k.GetType() != typeof(KBatchedAnimController)
                    || k.materialType == KAnimBatchGroup.MaterialType.UI || !PlainPosition(k.getPositionDataFunctionInUse)) continue;
                try
                {
                    writeInstance(batch, i, c, data);
                    work.SymbolsWritten[i] = writeSymbols(batch, i, c, symbols);
                }
                catch (Exception)
                {
                    // Left to the game's loop, which throws at the same place.
                    break;
                }
                work.Controller[i] = c;
                work.DonePass[i] = work.Pass;
                count++;
            }
            Interlocked.Add(ref written, count);
        }

        static bool PlainPosition(Func<Vector4> f)
        {
            if (f == null) return true;
            // One target, the usual case: its method, without GetInvocationList's array.
            if (hasSingleTarget != null && hasSingleTarget(f)) return f.Method == trackerPosition;
            foreach (var d in f.GetInvocationList())
                if (d.Method != trackerPosition) return false;
            return true;
        }

        static Work Done(KAnimBatch batch, int index, KAnimConverter.IAnimConverter controller)
        {
            if (pending.Count == 0) return null;
            int id = batch.id;
            if (id >= works.Length) return null;
            var work = works[id];
            if (work == null || work.Batch != batch || work.Pass != pass || index >= work.DonePass.Length
                || work.DonePass[index] != pass || !ReferenceEquals(work.Controller[index], controller)) return null;
            return work;
        }

        /// KAnimBatch.WriteBatchedAnimInstanceData, skipped when a worker wrote it this pass.
        static void WriteInstance(KAnimBatch batch, int index, KAnimConverter.IAnimConverter controller, NativeArray<byte> data)
        {
            var work = Done(batch, index, controller);
            if (work == null)
            {
                writeInstance(batch, index, controller, data);
                return;
            }
            consumed++;
            if (verify)
            {
                if (scratchTexture == null)
                {
                    scratchTexture = new Texture2D(64, 64, TextureFormat.RGBAFloat, false);
                    scratch = scratchTexture.GetRawTextureData<byte>();
                }
                controller.GetBatchInstanceData().WriteToTexture(scratch, 0, index);
                checks++;
                if (!Same(scratch, 0, data, index * InstanceBytes, InstanceBytes))
                    global::Debug.Log($"{Name}: instance data of {controller.GetName()} differs from the game's write ({++mismatches})");
            }
        }

        /// KAnimBatch.WriteSymbolInstanceData, answered from the worker's write this pass. The
        /// skip marks the controller reached (the symbol write follows the instance write).
        static bool WriteSymbols(KAnimBatch batch, int index, KAnimConverter.IAnimConverter controller, NativeArray<byte> data)
        {
            var work = Done(batch, index, controller);
            if (work == null) return writeSymbols(batch, index, controller, data);
            work.DonePass[index] = 0;
            work.Controller[index] = null;
            if (verify)
            {
                // The game would find the slot current now, and the texture holds the block.
                var slot = batch.symbolInstanceSlots[index];
                var gpu = controller.symbolInstanceGpuData;
                checks++;
                if (slot.symbolInstanceData != gpu || slot.dataVersion != gpu.version)
                    global::Debug.Log($"{Name}: symbol block of {controller.GetName()} changed after the worker's write ({++mismatches})");
                int size = batch.group.data.maxSymbolsPerBuild * 8 * 4;
                if (size <= scratch.Length)
                {
                    gpu.WriteToTexture(scratch, 0, index);
                    if (!Same(scratch, 0, data, index * size, size))
                        global::Debug.Log($"{Name}: symbol block of {controller.GetName()} differs from the game's write ({++mismatches})");
                }
            }
            return work.SymbolsWritten[index];
        }

        static bool Same(NativeArray<byte> a, int aStart, NativeArray<byte> b, int bStart, int length)
        {
            for (int i = 0; i < length; i++)
                if (a[aStart + i] != b[bStart + i]) return false;
            return true;
        }
    }
}
