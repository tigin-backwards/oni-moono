using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// A sampler's texture as recorded: render textures are made ready for sampling first.
    internal readonly record struct SamplerTexture(DeviceTexture Texture, bool RenderTexture, bool AutoGenerateMips);

    /// Everything a draw's program reads, recorded on the main thread. Instances belong to the
    /// frame they are recorded in and are reused by its next recording (FrameArena.Next), so a
    /// draw allocates nothing: the mesh draw it runs is a field, its command a delegate made once
    /// per instance.
    internal sealed class ProgramInputs
    {
        /// Shader and pass (diagnostics, --gpu-profile).
        public string Label;
        public DeviceProgram Program;
        public ResolvedState State;
        public float[] Values;
        public int ValuesOffset;
        public byte[] Blocks;
        public int BlocksOffset;
        /// Textures[0..TextureCount): the program's samplers.
        public SamplerTexture[] Textures = Array.Empty<SamplerTexture>();
        public int TextureCount;
        // A mesh draw's geometry.
        public DeviceMesh Mesh;
        public MeshLayout Layout;
        public (int Offset, int Count, uint Mode, int BaseVertex) SubMesh;
        /// Runs the mesh draw (GL thread).
        public Action RunDraw;

        // Recorded ahead (GlDrawRecorder.DrawQueue): the pass's shader, and the textures of
        // samplers left to prepare at the draw's place in the queue.
        public ShaderNative Shader;
        public TextureNative[] Pending = Array.Empty<TextureNative>();
        public bool HasPending;

        /// The same command: program, state, values and block bytes (bitwise), textures and
        /// geometry (verify mode).
        public bool SameAs(ProgramInputs o)
        {
            if (Program != o.Program || !State.Equals(o.State) || Label != o.Label || TextureCount != o.TextureCount) return false;
            if (Mesh != o.Mesh || Layout != o.Layout || SubMesh != o.SubMesh) return false;
            int loose = Program.LooseLength, blocks = Program.BlocksLength;
            if (!MemoryMarshal.Cast<float, int>(Values.AsSpan(ValuesOffset, loose)).SequenceEqual(MemoryMarshal.Cast<float, int>(o.Values.AsSpan(o.ValuesOffset, loose)))) return false;
            if (!Blocks.AsSpan(BlocksOffset, blocks).SequenceEqual(o.Blocks.AsSpan(o.BlocksOffset, blocks))) return false;
            for (int i = 0; i < TextureCount; i++)
                if (Textures[i] != o.Textures[i]) return false;
            return true;
        }
    }

    /// Records draws for the GL thread (main thread): resolves each draw's program, state,
    /// uniform values, textures and mesh, preparing the device resources they use.
    ///
    /// A draw queue (a camera's sorted renderers, an overlay canvas's batches) is recorded in
    /// parallel: it is a plain loop over draws, with no game code between them, so nothing the
    /// draws read (materials, property blocks, globals, meshes, textures) changes while it is
    /// recorded. What must stay in order is what posts work for the GL thread (a program
    /// compiled, a mesh or texture prepared) and what adds cache entries. So worker threads
    /// record the draws in chunks: a material's values found out of date are resolved again by
    /// one of them (a pure function of the material, the globals and the program, all fixed
    /// here), and a changed mesh or texture is left pending; a draw needing a new cache entry or
    /// a program is left out. Then the main thread walks the queue in order, preparing each
    /// recorded draw's pending mesh and textures as the serial code does and posting it, and
    /// recording the others as before: the commands, their order and their data are those of the
    /// serial loop.
    internal sealed class GlDrawRecorder
    {
        readonly GlWorker worker;
        readonly Func<string, uint, DeviceTexture> defaultTexture;
        readonly Action<ProgramInputs> runDraw;
        readonly Dictionary<ShaderPass, PassState> passStates = new();

        /// The camera constants of the following draws.
        public CameraConstants Camera;
        /// The following draws go into a render texture (Direct3DClip).
        public bool RenderTexture;

        /// Draw queues are recorded on worker threads (ONI_PARALLEL_DRAWS=0: never).
        public static readonly bool ParallelByDefault = Environment.GetEnvironmentVariable("ONI_PARALLEL_DRAWS") != "0";

        /// Measurements (ONI_PARALLEL_DRAWS=alternate, with ONI_RENDER_TIMING=1): the timing
        /// windows alternate between parallel and serial recording, so that both are measured in
        /// one run under the same conditions.
        static readonly bool alternate = Environment.GetEnvironmentVariable("ONI_PARALLEL_DRAWS") == "alternate";
        static bool serialWindow;

        /// This recorder records draw queues in parallel (tests compare it with one that does not).
        public bool ParallelQueues = ParallelByDefault;

        /// Verify mode (ONI_VERIFY_PARALLEL_DRAWS=1): each draw recorded ahead is recorded again
        /// serially at its place, without posting, and the two compared.
        public static bool Verify = Environment.GetEnvironmentVariable("ONI_VERIFY_PARALLEL_DRAWS") == "1";

        /// Queues shorter than this are recorded serially (the fork and join cost more).
        public static int MinParallelDraws = 96;

        /// Draws per work item, at least.
        const int MinChunk = 24;

        /// The threads recording a queue besides the main thread (shared by recorders).
        static readonly int helpers = ForkJoin.DefaultHelpers;
        static ForkJoin forkJoin;

        /// Work items per queue, at most: a few per thread, for balance.
        static readonly int MaxChunks = (helpers + 1) * 4;

        /// `defaultTexture`: a shader property's default texture by name and sampler target.
        /// `runDraw`: executes a recorded mesh draw (GL thread).
        public GlDrawRecorder(GlWorker worker, Func<string, uint, DeviceTexture> defaultTexture, Action<ProgramInputs> runDraw)
        {
            this.worker = worker;
            this.defaultTexture = defaultTexture;
            this.runDraw = runDraw;
            newInputs = NewInputs;
        }

        // ----- Serial recording -----

        /// Forward rendering draws the passes for unlit and base lighting; others serve other
        /// rendering paths, shadows or lightmapping (ShadowCaster, Deferred, and ForwardAdd,
        /// which only additional lights would need). Values compare without case: the build
        /// stores the passes' LightMode upper-case ("FORWARDBASE").
        static bool IsForwardPass(ShaderPass pass, SubShader sub)
        {
            if (!pass.Tags.TryGetValue("LightMode", out var mode) && !sub.Tags.TryGetValue("LightMode", out mode)) return true;
            foreach (var forward in ForwardLightModes)
                if (string.Equals(mode, forward, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static readonly string[] ForwardLightModes = ["", "Always", "ForwardBase", "Vertex", "SRPDefaultUnlit"];

        /// Adds the passes a draw runs to `passes`; false (and none added) when one is a UsePass,
        /// which `resolveUsePasses` false leaves unresolved (resolving can load a shader).
        internal static bool Passes(List<(ShaderNative Shader, ShaderPass Pass)> passes, ShaderNative shader, MaterialNative material, int pass, bool allPasses, bool resolveUsePasses = true)
        {
            var sub = shader.ActiveSubShader;
            if (sub == null) return true;
            for (int i = 0; i < sub.Passes.Length; i++)
            {
                if (pass >= 0 && i != pass) continue;
                var p = sub.Passes[i];
                if (pass < 0 && !allPasses && !IsForwardPass(p, sub)) continue;
                if (p.Tags.TryGetValue("LightMode", out var lm) && material.DisabledPasses.Contains(lm)) continue;
                if (p.Type == 1)
                {
                    if (!resolveUsePasses)
                    {
                        passes.Clear();
                        return false;
                    }
                    var used = ResolveUsePass(p.UseName);
                    if (used.Pass != null) passes.Add(used);
                    continue;
                }
                if (p.Type != 0) continue; // GrabPass: no shader in this build uses one.
                passes.Add((shader, p));
            }
            return true;
        }

        /// UsePass "Shader/PASSNAME": the named pass of another shader (names compare in upper case).
        static (ShaderNative Shader, ShaderPass Pass) ResolveUsePass(string useName)
        {
            int slash = useName.LastIndexOf('/');
            if (slash <= 0) return (null, null);
            var shader = ShaderRegistry.Find(useName[..slash]);
            if (shader == null) return (null, null);
            var native = (ShaderNative)shader.NativeOrThrow;
            var passName = useName[(slash + 1)..];
            foreach (var s in native.SubShaders)
                foreach (var p in s.Passes)
                    if (string.Equals(p.Name, passName, StringComparison.OrdinalIgnoreCase)) return (native, p);
            return (null, null);
        }

        public PassState StateOf(ShaderPass pass)
        {
            if (!passStates.TryGetValue(pass, out var s))
            {
                s = new PassState(pass.Data.Struct("m_State"));
                passStates[pass] = s;
            }
            return s;
        }

        public static bool KeywordEnabled(string keyword, MaterialNative material, string[] extra)
            => material.Keywords.Contains(keyword) || ShaderGlobals.Keywords.Contains(keyword) || (extra != null && Array.IndexOf(extra, keyword) >= 0);

        /// A shader variant's program: compiled on the GL thread the first time it is used.
        public DeviceProgram Program(ShaderNative shader, int blob)
        {
            if (shader.Gpu is DeviceProgramSet set && set.Programs.TryGetValue(blob, out var p)) return p;
            // Built by the warm-up since the last present (GlWarmup).
            if (GlWarmup.Enabled)
            {
                GlWarmup.Publish();
                if (shader.Gpu is DeviceProgramSet set2 && set2.Programs.TryGetValue(blob, out p)) return p;
            }
            return Build(shader, blob);
        }

        // Its own method: the closure captures the parameters, and C# allocates such a closure
        // on entry to the method, so in Program it was made for every pass of every draw.
        DeviceProgram Build(ShaderNative shader, int blob)
        {
            long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var built = worker.Call(() => GlPrograms.Get(shader, blob));
            System.Threading.Interlocked.Add(ref OniMoono.Engine.StartupStats.ProgramWaitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - waitStart);
            return built;
        }

        ProgramInputs NewInputs()
        {
            var r = new ProgramInputs();
            r.RunDraw = () => runDraw(r);
            return r;
        }

        readonly Func<ProgramInputs> newInputs;

        /// Records a draw's program inputs into `arena`.
        public ProgramInputs Record(DeviceProgram program, PassState state, DrawInputs inputs, FrameArena arena)
        {
            var r = arena.Next(newInputs);
            r.Label = null;
            r.Program = program;
            (r.Values, r.ValuesOffset) = arena.Floats(program.LooseLength);
            (r.Blocks, r.BlocksOffset) = arena.Bytes(program.BlocksLength);
            var materialValues = GlUniforms.Record(program, inputs, r.Values.AsSpan(r.ValuesOffset, program.LooseLength), r.Blocks.AsSpan(r.BlocksOffset, program.BlocksLength));
            r.State = ResolvedState.Of(state, inputs, materialValues);
            r.TextureCount = program.Samplers.Count;
            if (r.Textures.Length < r.TextureCount) r.Textures = new SamplerTexture[r.TextureCount];
            for (int i = 0; i < r.TextureCount; i++)
            {
                var s = program.Samplers[i];
                FindTexture(inputs, materialValues, i, s, out bool found, out var binding, out bool global);
                if (Missing(found, binding, global, out string fallback))
                {
                    r.Textures[i] = new SamplerTexture(defaultTexture(fallback ?? DefaultTextures.Of(inputs.Shader, s.PropertyId), s.Target), false, false);
                    continue;
                }
                var tn = (TextureNative)binding.Texture.Native;
                var device = GlTextures.Prepare(worker, tn);
                if (SamplerOf(device, tn, s, out r.Textures[i])) continue;
                r.Textures[i] = DefaultSampler(inputs.Shader, s);
            }
            return r;
        }

        /// The draw's block first, then what the material and globals give (resolved once).
        /// `global`: the texture is the global's.
        static void FindTexture(DrawInputs inputs, MaterialValues materialValues, int i, SamplerBinding s, out bool found, out TextureBinding binding, out bool global)
        {
            global = false;
            if (inputs.Block != null && inputs.Block.Textures.TryGetValue(s.PropertyId, out binding)) found = true;
            else if (materialValues != null)
            {
                found = materialValues.Found[i];
                binding = materialValues.Textures[i];
                global = materialValues.Global[i];
            }
            else found = inputs.TryTexture(s.PropertyId, out binding, out global);
        }

        /// Whether the sampler has no texture to read, and then which default it reads
        /// (`fallback`; null: the property's default). A global since destroyed counts as not
        /// set; a material's or block's texture since destroyed, and a render texture not created
        /// (released, or resized and not created again), read as nothing bound (DefaultTextures).
        static bool Missing(bool found, in TextureBinding binding, bool global, out string fallback)
        {
            fallback = null;
            if (!found || binding.Texture is null) return true;
            if (binding.Texture.Native is not TextureNative tn)
            {
                if (!global) fallback = DefaultTextures.Unbound;
                return true;
            }
            if (tn is RenderTextureNative { Created: false })
            {
                fallback = DefaultTextures.Unbound;
                return true;
            }
            return false;
        }

        /// A prepared texture as a sampler reads it; false when it does not fit the sampler.
        bool SamplerOf(DeviceTexture device, TextureNative tn, SamplerBinding s, out SamplerTexture sampler)
        {
            if (device.RecordedEmpty)
            {
                sampler = new SamplerTexture(defaultTexture(DefaultTextures.Unbound, s.Target), false, false);
                return true;
            }
            if (device.RecordedTarget == s.Target)
            {
                var rt = tn as RenderTextureNative;
                sampler = new SamplerTexture(device, rt != null, rt?.AutoGenerateMips ?? false);
                return true;
            }
            sampler = default;
            return false;
        }

        /// The property's default texture (DefaultTextures.Of): what a sampler reads with no
        /// texture set, or with a global set to one since destroyed (the gas shader's
        /// _GasOffsetTex after a load, while paused: FlowOffsetRenderer makes new offset textures
        /// on the first resize and sets the global again only when time runs).
        SamplerTexture DefaultSampler(ShaderNative shader, SamplerBinding s)
            => new(defaultTexture(DefaultTextures.Of(shader, s.PropertyId), s.Target), false, false);

        // Draw's scratch (main thread; a draw records completely before the next starts).
        readonly DrawInputs drawInputs = new();
        readonly List<(ShaderNative Shader, ShaderPass Pass)> drawPasses = new();

        public void Draw(in DrawRequest draw) => Draw(draw, worker.Arena, null);

        /// Records a draw into `arena` and posts its passes, or with `sink` adds them to it
        /// instead (verify mode).
        void Draw(in DrawRequest draw, FrameArena arena, List<ProgramInputs> sink)
        {
            if (draw.Material == null || draw.Mesh == null) return;
            var material = (MaterialNative)draw.Material.NativeOrThrow;
            if (material.Shader == null) return;
            var shader = (ShaderNative)material.Shader.NativeOrThrow;
            if (!shader.HasOpenGLCorePrograms) return;
            if (draw.SubMesh < 0 || draw.SubMesh >= draw.Mesh.SubMeshes.Count) return;
            var mesh = GlMeshes.Prepare(worker, draw.Mesh);
            var layout = mesh.Layout;
            var sm = mesh.SubMeshes[draw.SubMesh];
            var inputs = drawInputs;
            inputs.ObjectToWorld = draw.ObjectToWorld;
            inputs.Camera = Camera;
            inputs.RenderTexture = RenderTexture;
            inputs.Block = draw.Properties;
            if (PropertySheet.Verifying) PropertySheet.CheckDraw(draw.Properties);
            inputs.Material = material;
            drawPasses.Clear();
            Passes(drawPasses, shader, material, draw.Pass, draw.AllPasses);
            foreach (var (passShader, pass) in drawPasses)
            {
                int blob = Variant(material, passShader, pass, draw.Keywords);
                if (blob < 0) continue;
                var program = Program(passShader, blob);
                if (program.Id == 0) continue;
                inputs.Shader = passShader;
                var recorded = Record(program, StateOf(pass), inputs, arena);
                if (GlGpuProfile.Enabled) recorded.Label = Label(passShader, pass);
                recorded.Mesh = mesh;
                recorded.Layout = layout;
                recorded.SubMesh = sm;
                if (sink != null) sink.Add(recorded);
                else worker.Post(recorded.RunDraw);
            }
            inputs.Block = null;
            inputs.Material = null;
            inputs.Shader = null;
        }

        static string Label(ShaderNative shader, ShaderPass pass) => shader.ShaderName + "/" + (pass.Name.Length > 0 ? pass.Name : "pass");

        /// The pass's program variant for the material's, the draw's and the global keywords,
        /// selected once per material, pass and draw keyword list while the keywords are unchanged.
        /// The device's immediate draws (no draw keywords) use it too.
        internal static int Variant(MaterialNative material, ShaderNative passShader, ShaderPass pass, string[] keywords)
        {
            var cache = material.Variants ??= new();
            var key = (pass, keywords);
            int materialVersion = material.Keywords.Version, globalVersion = ShaderGlobals.Keywords.Version;
            if (cache.TryGetValue(key, out var c) && c.MaterialVersion == materialVersion && c.GlobalVersion == globalVersion) return c.Blob;
            int blob = SelectVariant(material, passShader, pass, keywords);
            cache[key] = (materialVersion, globalVersion, blob);
            return blob;
        }

        // Its own method, so the predicate's closure is allocated only when a variant is selected.
        static int SelectVariant(MaterialNative material, ShaderNative passShader, ShaderPass pass, string[] keywords)
            => ShaderPrograms.SelectVariant(pass, k => KeywordEnabled(k, material, keywords));

        // ----- Draw queues -----

        /// One work item's state: its scratch and what it recorded (reused from queue to queue).
        sealed class Chunk
        {
            public readonly DrawInputs Inputs = new();
            public readonly List<(ShaderNative Shader, ShaderPass Pass)> Passes = new();
            public readonly List<ProgramInputs> Recorded = new();
            public FrameArena Arena;
            public int Start, End;
        }

        readonly List<Chunk> chunks = new();
        List<DrawItem> queue;
        int chunkCount;
        /// Per draw of the queue: where its recorded passes start in its chunk's list and how many
        /// there are; -1 when it is left to the serial walk.
        int[] firsts = Array.Empty<int>(), counts = Array.Empty<int>();
        /// Per draw recorded ahead: its mesh or a texture is left to prepare at its place.
        bool[] prepares = Array.Empty<bool>();
        Action<int> recordChunk;

        /// Draws of the queues and those of them recorded ahead, and the main thread's time in
        /// the parallel recording and in the walk after it (ONI_RENDER_TIMING).
        static long queueDraws, aheadDraws, parallelTicks, walkTicks;

        public static string TakeCounters(int frames)
        {
            double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, frames);
            long waited = forkJoin?.WaitTicks ?? 0;
            if (forkJoin != null) forkJoin.WaitTicks = 0;
            var text = $"{(queueDraws > 0 ? 100.0 * aheadDraws / queueDraws : 0):F1}% recorded ahead, parallel part {ms(parallelTicks):F3} ms (waiting for helpers {ms(waited):F3}), walk {ms(walkTicks):F3} ms";
            if (alternate)
            {
                text += serialWindow ? " (serial window)" : " (parallel window)";
                serialWindow = !serialWindow;
            }
            var left = new System.Text.StringBuilder();
            for (int i = 0; i < reasons.Length; i++)
                if (reasons[i] > 0) left.Append(left.Length == 0 ? "; left for " : ", ").Append((Reason)i).Append(' ').Append(reasons[i] / Math.Max(1, frames));
            text += left.ToString();
            Array.Clear(reasons);
            queueDraws = aheadDraws = parallelTicks = walkTicks = 0;
            return text;
        }

        static long verified, wrong, incidents;

        /// Draws recorded ahead so far, and verify mode's draws checked and found different.
        internal static long AheadDrawCount => aheadDraws;
        internal static (long Verified, long Wrong) VerifyCounts => (verified, wrong);
        readonly FrameArena verifyArena = new();
        readonly List<ProgramInputs> verifyRecorded = new();

        static DrawRequest RequestOf(DrawItem d) => new(d.Mesh, d.SubMesh, d.ObjectToWorld, d.Material, d.Pass, d.Properties, d.Keywords);

        /// Records a queue's draws in its order: the same commands as Draw for each in turn.
        public void DrawQueue(List<DrawItem> items)
        {
            int n = items.Count;
            queueDraws += n;
            if (!ParallelQueues || helpers == 0 || n < MinParallelDraws || serialWindow || FrameRenderer.SerialQueues)
            {
                foreach (var d in items) Draw(RequestOf(d));
                return;
            }
            var frame = worker.Recording;
            int size = Math.Max(MinChunk, (n + MaxChunks - 1) / MaxChunks);
            chunkCount = (n + size - 1) / size;
            while (chunks.Count < chunkCount) chunks.Add(new Chunk());
            if (firsts.Length < n)
            {
                firsts = new int[Math.Max(n, firsts.Length * 2)];
                counts = new int[firsts.Length];
                prepares = new bool[firsts.Length];
            }
            for (int k = 0; k < chunkCount; k++)
            {
                var c = chunks[k];
                c.Arena = frame.Arena.Child(k);
                c.Start = k * size;
                c.End = Math.Min(n, c.Start + size);
                c.Recorded.Clear();
            }
            queue = items;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                (forkJoin ??= new ForkJoin(helpers)).For(chunkCount, recordChunk ??= RecordChunk);
            }
            finally
            {
                queue = null;
            }
            long walk = System.Diagnostics.Stopwatch.GetTimestamp();
            parallelTicks += walk - start;
            // In order: each draw recorded ahead has what it left prepared (posting any upload at
            // its place) and is posted; the others are recorded here. A program compiled on the
            // way (worker.Call) hands the frame over: the draws after it record into the next
            // frame, serially, since their data lives in the frame handed over.
            for (int i = 0; i < n; i++)
            {
                int count = counts[i];
                if (count < 0 || worker.Recording != frame)
                {
                    Draw(RequestOf(items[i]));
                    continue;
                }
                aheadDraws++;
                var recorded = chunks[i / size].Recorded;
                int first = firsts[i];
                var item = items[i];
                DeviceMesh mesh = null;
                if (prepares[i]) mesh = GlMeshes.Prepare(worker, item.Mesh);
                for (int j = 0; j < count; j++)
                {
                    var r = recorded[first + j];
                    if (mesh != null)
                    {
                        r.Mesh = mesh;
                        r.Layout = mesh.Layout;
                        r.SubMesh = mesh.SubMeshes[item.SubMesh];
                    }
                    if (r.HasPending) PreparePending(r);
                    worker.Post(r.RunDraw);
                }
                if (Verify) VerifyDraw(item, recorded, first, count);
            }
            for (int k = 0; k < chunkCount; k++) chunks[k].Recorded.Clear();
            walkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - walk;
        }

        /// The textures a draw recorded ahead found changed, prepared in sampler order as Record
        /// prepares them (main thread).
        void PreparePending(ProgramInputs r)
        {
            var samplers = r.Program.Samplers;
            for (int i = 0; i < r.TextureCount; i++)
            {
                var tn = r.Pending[i];
                if (tn == null) continue;
                r.Pending[i] = null;
                var device = GlTextures.Prepare(worker, tn);
                if (!SamplerOf(device, tn, samplers[i], out r.Textures[i])) r.Textures[i] = DefaultSampler(r.Shader, samplers[i]);
            }
            r.HasPending = false;
        }

        /// Why draws were left to the serial walk (ONI_RENDER_TIMING).
        enum Reason { UsePass, Variant, Program, PassState, MaterialValues, Exception, Count }

        static readonly long[] reasons = new long[(int)Reason.Count];

        static bool Left(Reason r)
        {
            System.Threading.Interlocked.Increment(ref reasons[(int)r]);
            return false;
        }

        // A worker thread: the chunk's draws, each recorded unless it needs a cache entry made
        // or a program compiled.
        void RecordChunk(int k)
        {
            var c = chunks[k];
            var items = queue;
            for (int i = c.Start; i < c.End; i++)
            {
                int first = c.Recorded.Count;
                bool recorded, prepare = false;
                try
                {
                    recorded = TryRecordAhead(RequestOf(items[i]), c, out prepare);
                }
                catch (Exception)
                {
                    // The serial walk records it again, and raises the same exception at its place.
                    recorded = Left(Reason.Exception);
                }
                if (!recorded)
                {
                    for (int j = first; j < c.Recorded.Count; j++) Array.Clear(c.Recorded[j].Pending);
                    c.Recorded.RemoveRange(first, c.Recorded.Count - first);
                }
                firsts[i] = first;
                counts[i] = recorded ? c.Recorded.Count - first : -1;
                prepares[i] = prepare;
            }
            c.Inputs.Block = null;
            c.Inputs.Material = null;
            c.Inputs.Shader = null;
        }

        /// Draw's work for a draw on a worker thread, while nothing changes the inputs: false for
        /// one that needs a variant selected, a program compiled or a cache entry made, which is
        /// left to Draw. A changed mesh or texture is left to prepare at the draw's place
        /// (`prepare`): preparing posts its upload, which must come between the draws around it.
        bool TryRecordAhead(in DrawRequest draw, Chunk c, out bool prepare)
        {
            prepare = false;
            if (draw.Material == null || draw.Mesh == null) return true;
            var material = (MaterialNative)draw.Material.NativeOrThrow;
            if (material.Shader == null) return true;
            var shader = (ShaderNative)material.Shader.NativeOrThrow;
            if (!shader.HasOpenGLCorePrograms) return true;
            if (draw.SubMesh < 0 || draw.SubMesh >= draw.Mesh.SubMeshes.Count) return true;
            DeviceMesh mesh = null;
            if (GlMeshes.IsCurrent(draw.Mesh)) mesh = (DeviceMesh)draw.Mesh.Gpu;
            else prepare = true;
            var inputs = c.Inputs;
            inputs.ObjectToWorld = draw.ObjectToWorld;
            inputs.Camera = Camera;
            inputs.RenderTexture = RenderTexture;
            inputs.Block = draw.Properties;
            if (PropertySheet.Verifying) PropertySheet.CheckDraw(draw.Properties);
            inputs.Material = material;
            var passes = c.Passes;
            passes.Clear();
            if (!Passes(passes, shader, material, draw.Pass, draw.AllPasses, resolveUsePasses: false)) return Left(Reason.UsePass);
            var variants = material.Variants;
            int materialVersion = material.Keywords.Version, globalVersion = ShaderGlobals.Keywords.Version;
            foreach (var (passShader, pass) in passes)
            {
                if (variants == null || !variants.TryGetValue((pass, draw.Keywords), out var v) || v.MaterialVersion != materialVersion || v.GlobalVersion != globalVersion) return Left(Reason.Variant);
                if (v.Blob < 0) continue;
                if (passShader.Gpu is not DeviceProgramSet set || !set.Programs.TryGetValue(v.Blob, out var program)) return Left(Reason.Program);
                if (program.Id == 0) continue;
                if (!passStates.TryGetValue(pass, out var state)) return Left(Reason.PassState);
                inputs.Shader = passShader;
                var recorded = RecordAhead(program, state, inputs, c.Arena);
                if (recorded == null) return Left(Reason.MaterialValues);
                if (GlGpuProfile.Enabled) recorded.Label = Label(passShader, pass);
                if (mesh != null)
                {
                    recorded.Mesh = mesh;
                    recorded.Layout = mesh.Layout;
                    recorded.SubMesh = mesh.SubMeshes[draw.SubMesh];
                }
                prepare |= recorded.HasPending;
                c.Recorded.Add(recorded);
            }
            return true;
        }

        /// Record on a worker thread: the material's values resolved again if out of date (once,
        /// by whichever thread comes first), textures that changed left pending; null when the
        /// material has no values entry for the program yet.
        ProgramInputs RecordAhead(DeviceProgram program, PassState state, DrawInputs inputs, FrameArena arena)
        {
            var materialValues = GlUniforms.SharedValuesOf(program, inputs);
            if (materialValues == null) return null;
            var r = arena.Next(newInputs);
            r.Label = null;
            r.Program = program;
            r.Shader = inputs.Shader;
            (r.Values, r.ValuesOffset) = arena.Floats(program.LooseLength);
            (r.Blocks, r.BlocksOffset) = arena.Bytes(program.BlocksLength);
            GlUniforms.RecordFrom(materialValues, program, inputs, r.Values.AsSpan(r.ValuesOffset, program.LooseLength), r.Blocks.AsSpan(r.BlocksOffset, program.BlocksLength));
            r.State = ResolvedState.Of(state, inputs, materialValues);
            int samplers = program.Samplers.Count;
            r.TextureCount = samplers;
            if (r.Textures.Length < samplers) r.Textures = new SamplerTexture[samplers];
            if (r.Pending.Length < samplers) r.Pending = new TextureNative[samplers];
            r.HasPending = false;
            for (int i = 0; i < samplers; i++)
            {
                var s = program.Samplers[i];
                r.Pending[i] = null;
                FindTexture(inputs, materialValues, i, s, out bool found, out var binding, out bool global);
                if (Missing(found, binding, global, out string fallback))
                {
                    r.Textures[i] = new SamplerTexture(defaultTexture(fallback ?? DefaultTextures.Of(inputs.Shader, s.PropertyId), s.Target), false, false);
                    continue;
                }
                var tn = (TextureNative)binding.Texture.Native;
                if (!GlTextures.IsCurrent(tn))
                {
                    r.Pending[i] = tn;
                    r.HasPending = true;
                    continue;
                }
                if (SamplerOf((DeviceTexture)tn.Gpu, tn, s, out r.Textures[i])) continue;
                r.Textures[i] = DefaultSampler(inputs.Shader, s);
            }
            return r;
        }

        /// Verify mode: the draw recorded again as the serial loop records it, into scratch and
        /// without posting, against what was recorded ahead.
        void VerifyDraw(DrawItem item, List<ProgramInputs> recorded, int first, int count)
        {
            int posted = worker.Recording.Commands.Count;
            verifyArena.Reset();
            verifyRecorded.Clear();
            Draw(RequestOf(item), verifyArena, verifyRecorded);
            string problem = null;
            if (worker.Recording.Commands.Count != posted) problem = "the serial recording posted work";
            else if (verifyRecorded.Count != count) problem = $"{count} passes recorded ahead, {verifyRecorded.Count} serially";
            else
                for (int j = 0; j < count; j++)
                    if (!recorded[first + j].SameAs(verifyRecorded[j]))
                    {
                        problem = $"pass {j} ({recorded[first + j].Program.Id}) differs";
                        break;
                    }
            verified++;
            if (problem != null)
            {
                wrong++;
                if (incidents++ < 20)
                    EngineLog.Engine($"parallel draws: mismatch: {item.Material?.name} on {item.Mesh?.Name}: {problem}");
            }
            if (verified % 100_000 == 0) EngineLog.Engine($"parallel draws: {verified} draws verified, {wrong} wrong");
        }
    }
}
