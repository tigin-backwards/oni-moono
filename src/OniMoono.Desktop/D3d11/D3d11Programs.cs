using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OniMoono.Assets.Shaders;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.D3d11
{
    /// What one stage of a program binds, by D3D register (D3d11Bindings: Unity's parameters
    /// give t#, s# and b# directly): each constant buffer's slot, its block among the draw's
    /// recorded blocks (DeviceProgram.BlockSizes) and size; each texture's slot and its index in
    /// DeviceProgram.Samplers; each sampler slot and the texture whose sampler state it takes.
    internal sealed class D3d11Stage
    {
        public (int Slot, int Block, int Size)[] ConstantBuffers = [];
        public (int Slot, int Texture)[] Textures = [];
        public (int Slot, int Texture)[] Samplers = [];
    }

    /// A vertex-shader input the input assembler feeds: the signature element (semantic, index,
    /// D3D_REGISTER_COMPONENT_TYPE) and the mesh channel it reads (-1: none, a constant).
    internal readonly record struct D3d11Input(string Semantic, uint Index, D3D_REGISTER_COMPONENT_TYPE ComponentType, int Channel);

    /// An input layout made for one mesh layout: the layout, and per input-assembler slot past the
    /// channels' (Constants), the offset in the constants buffer it reads.
    internal sealed unsafe class D3d11Layout
    {
        public ID3D11InputLayout* Layout;
        public int[] Constants = [];
    }

    /// A program on Direct3D 11 (DeviceProgram.D3d): the shader objects made from the shipped
    /// DXBC of each stage, unmodified, and what each stage binds.
    internal sealed unsafe class D3d11Shaders
    {
        public ID3D11VertexShader* Vertex;
        public ID3D11PixelShader* Pixel;
        public ID3D11GeometryShader* Geometry;
        /// The vertex stage's DXBC (its input signature validates the input layouts).
        public byte[] VertexBytecode;
        /// Vertex, pixel and geometry stages (the geometry null when there is none).
        public D3d11Stage[] Stages = new D3d11Stage[3];
        public D3d11Input[] Inputs = [];
        /// Render targets the pixel stage writes: one past its highest SV_Target register.
        public int Targets;
        /// Input layouts by mesh layout (VkPipelines.LayoutKey; ImmediateLayout for GL.Begin).
        public readonly Dictionary<long, D3d11Layout> Layouts = new();
        public string Name;
        /// What it draws, for the coverage count (done criterion 1): the shader, subshader and
        /// pass, and each stage's program entry (its blob index) with the keywords selected.
        public string Coverage;
        /// It has drawn (its coverage line logged).
        public bool Drawn;

        public void Release()
        {
            foreach (var l in Layouts.Values) Com.Release(ref l.Layout);
            Layouts.Clear();
            Com.Release(ref Vertex);
            Com.Release(ref Pixel);
            Com.Release(ref Geometry);
        }
    }

    /// Unity's shipped Direct3D 11 programs (shader platform 4), read by M1's reader
    /// (D3d11Program, D3d11Bindings): one program per stage, each selected by the keywords that
    /// stage knows (D3d11Variants), combined into a draw's program. The recorder's variant key
    /// (GlDrawRecorder.SelectStages) is the index of a (pass, vertex, pixel, geometry) choice in
    /// the shader's list here; the device thread builds its program from it. A program that
    /// cannot be built is logged once per shader and reason and kept with Id 0 (drawn by none).
    internal static unsafe class D3d11Programs
    {
        sealed class Choices
        {
            public readonly List<(ShaderPass Pass, PlayerSubprogram?[] Stages)> List = new();
            public readonly Dictionary<(ShaderPass, int, int, int, int, int, int), int> Index = new();
        }

        static readonly ConditionalWeakTable<ShaderNative, Choices> choices = new();
        static readonly HashSet<string> reported = new();
        static int nextId = 1;

        /// Variants that could not be built (--check-shaders).
        public static int Failures;

        /// Variants the device refuses to draw (NotDrawn: a hull or domain stage, inline
        /// samplers, structured buffers, multisample textures, bindings unlike their DXBC).
        public static int Refused;

        /// Of Refused, the variants the user accepted as unreachable (d3d11-plan.md M3 part C; the
        /// 68 programs of U60 764025's data): no engine code sets their keywords
        /// (PROCEDURAL_INSTANCING_ON, _MSAA_*) or uses their shaders, and no device of Moono's
        /// binds structured buffers or multisample textures. --check-shaders counts them apart,
        /// not as failures. Only those programs, by shader and keyword (AcceptedUnreachable),
        /// with the reason they were accepted for; any other refusal for those reasons fails.
        public static int RefusedUnreachable;

        /// A refused variant among the ones accepted as unreachable: the procedural-instancing
        /// variants of the three particle shaders and the five Physics2D debug shaders (structured
        /// buffers), the frame debugger display's _MSAA_ variants and BlitToDepth_MSAA
        /// (multisample textures).
        public static bool AcceptedUnreachable(string shader, string[] keywords, string why) => why switch
        {
            "structured buffers" => shader is "Particles/Standard Surface" or "Particles/Standard Unlit" or "Klei/Standard Unlit"
                                        ? Array.IndexOf(keywords, "PROCEDURAL_INSTANCING_ON") >= 0
                                        : shader is "Hidden/Physics2D/SDF_CapsuleGeometry" or "Hidden/Physics2D/SDF_CircleGeometry" or "Hidden/Physics2D/SDF_Line"
                                            or "Hidden/Physics2D/SDF_Point" or "Hidden/Physics2D/SDF_PolygonGeometry",
            "multisample textures" => shader == "Hidden/BlitToDepth_MSAA"
                                      || shader == "Hidden/FrameDebuggerRenderTargetDisplay" && Array.Exists(keywords, k => k.StartsWith("_MSAA_", StringComparison.Ordinal)),
            _ => false,
        };

        /// The stage entries (blob indexes) of a key Select gave.
        public static int[] EntriesOfKey(ShaderNative shader, int key)
        {
            var c = choices.GetOrCreateValue(shader);
            lock (c)
            {
                if (key < 0 || key >= c.List.Count) return [];
                var list = new List<int>();
                foreach (var st in c.List[key].Stages)
                    if (st is { } sub) list.Add(sub.BlobIndex);
                return list.ToArray();
            }
        }

        /// The reason the latest Build on this thread refused its program (null: none), for the
        /// check's per-program list.
        [ThreadStatic] public static string LastRefusal;

        /// Whether that refusal was of an accepted, unreachable program (AcceptedUnreachable).
        [ThreadStatic] public static bool LastRefusalAccepted;

        /// Platform 4 when the shader carries Direct3D 11 programs the device reads, else 0 (not
        /// drawn, logged once); kept on the shader until it is bound again. A shader serialized by
        /// Unity 2018 (a mod's bundle) carries them in a program table of another format, which is
        /// not read: a shader with normal passes all of whose Direct3D 11 subprograms are of that
        /// layout (PlayerSubprograms.Has2018D3d11) is not drawn. A shader with no normal pass
        /// (UsePass and GrabPass alone) keeps 4: the recorder resolves its used passes.
        public static uint PlatformOf(ShaderNative shader)
        {
            int known = shader.D3d11Readable;
            if (known >= 0) return (uint)known;
            string why = null;
            if (!shader.HasProgramsFor(ShaderNative.D3D11Platform)) why = "has no Direct3D 11 programs";
            else if (shader.SubShaders.Length > 0 && Array.TrueForAll(shader.SubShaders, s => Array.TrueForAll(s.Passes, p => p.Legacy)))
            {
                bool normal = false, readable = false, of2018 = false;
                foreach (var sub in shader.SubShaders)
                    foreach (var pass in sub.Passes)
                    {
                        if (pass.Type != 0) continue;
                        normal = true;
                        if (!pass.D3d11.IsEmpty) readable = true;
                        foreach (var stage in PlayerSubprograms.Stages) of2018 |= PlayerSubprograms.Has2018D3d11(pass.Data, stage);
                    }
                if (normal && !readable)
                    why = of2018 ? "has Direct3D 11 programs of Unity 2018's layout, which are not read" : "has no Direct3D 11 programs this device reads";
            }
            if (why != null)
                lock (reported)
                    if (reported.Add($"{shader.ShaderName}\nno programs"))
                        EngineLog.Engine($"d3d11: shader '{shader.ShaderName}' {why}; it is not drawn");
            uint platform = why == null ? ShaderNative.D3D11Platform : 0;
            shader.D3d11Readable = (int)platform;
            return platform;
        }

        /// The key of the pass's program for the enabled keywords (main thread, any recorder
        /// thread): each stage selected from its own list (D3d11Variants.Select); -1 when the
        /// pass has no vertex or pixel program.
        public static int Select(ShaderNative shader, ShaderPass pass, Func<string, bool> isEnabled)
        {
            var variants = pass.D3d11;
            if (variants.IsEmpty) return -1;
            var stages = variants.Select(isEnabled);
            if (stages[0] == null || stages[1] == null) return -1;
            static int Blob(PlayerSubprogram? s) => s?.BlobIndex ?? -1;
            static int Parameters(PlayerSubprogram? s) => s?.ParameterBlobIndex ?? -1;
            // Hull and domain stages are refused when built; the key need not tell them apart.
            var key = (pass, Blob(stages[0]), Parameters(stages[0]), Blob(stages[1]), Parameters(stages[1]), Blob(stages[2]), Parameters(stages[2]));
            var c = choices.GetOrCreateValue(shader);
            lock (c)
            {
                if (c.Index.TryGetValue(key, out int index)) return index;
                index = c.List.Count;
                c.List.Add((pass, stages));
                c.Index[key] = index;
                return index;
            }
        }

        static void NotDrawn(ShaderNative shader, string[] keywords, string why)
        {
            System.Threading.Interlocked.Increment(ref Refused);
            LastRefusalAccepted = AcceptedUnreachable(shader.ShaderName, keywords, why);
            if (LastRefusalAccepted) System.Threading.Interlocked.Increment(ref RefusedUnreachable);
            LastRefusal = why;
            lock (reported)
                if (reported.Add($"{shader.ShaderName}\n{why}"))
                    EngineLog.Engine($"d3d11: shader '{shader.ShaderName}' variants with {why} are not drawn (first: [{string.Join(" ", keywords)}])");
        }

        /// The program for a key Select gave (device thread), kept in the shader's Gpu slot.
        public static DeviceProgram Get(ID3D11Device* device, ShaderNative shader, int key)
        {
            if (shader.Gpu is not DeviceProgramSet set)
            {
                set = new DeviceProgramSet();
                shader.Gpu = set;
                GraphicsDevice.Adopt(shader, set);
            }
            if (set.Programs.TryGetValue(key, out var p)) return p;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                p = Build(device, shader, key);
            }
            catch (Exception e) when (e is System.IO.InvalidDataException or NotSupportedException or ArgumentException or D3d11UnavailableException)
            {
                // Damaged data, or a shader the device refused: never drawn, and kept so the
                // next draw does not try again.
                System.Threading.Interlocked.Increment(ref Failures);
                lock (reported)
                    if (reported.Add($"{shader.ShaderName}\nfailed"))
                        EngineLog.Engine($"d3d11: shader '{shader.ShaderName}' program {key} could not be built, its draws are left out: {e.GetType().Name}: {e.Message}");
                p = new DeviceProgram();
            }
            System.Threading.Interlocked.Add(ref StartupStats.ProgramTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            System.Threading.Interlocked.Increment(ref StartupStats.Programs);
            set.Programs[key] = p;
            return p;
        }

        static DeviceProgram Build(ID3D11Device* device, ShaderNative shader, int key)
        {
            var program = new DeviceProgram();
            (ShaderPass Pass, PlayerSubprogram?[] Stages) choice;
            var c = choices.GetOrCreateValue(shader);
            lock (c)
            {
                if (key < 0 || key >= c.List.Count) return program;
                choice = c.List[key];
            }
            var stages = choice.Stages;
            var keywords = stages[0].Value.Keywords;
            for (int s = 3; s < stages.Length; s++)
                if (stages[s] != null)
                {
                    NotDrawn(shader, keywords, $"a {PlayerSubprograms.Stages[s]} stage");
                    return program;
                }
            var programs = shader.ProgramsFor(ShaderNative.D3D11Platform);
            if (programs == null) return program;
            var d3d = new D3d11Shaders { Name = shader.ShaderName, Coverage = CoverageOf(shader, choice.Pass, stages) };
            var blockSizes = new List<int>();
            var dxbc = new byte[3][];
            for (int s = 0; s < 3; s++)
            {
                if (stages[s] is not { } sub) continue;
                var entry = programs.D3d11(sub.BlobIndex);
                // A 2020.3 shader's subprograms name 2020.3 entries, U60's U60's.
                if (entry.Legacy != (sub.LegacyParameters != null))
                    throw new System.IO.InvalidDataException($"a {(entry.Legacy ? "2020.3" : "U60")} program entry for a {(sub.LegacyParameters != null ? "2020.3" : "U60")} subprogram");
                var bindings = D3d11Bindings.From(programs.Parameters(choice.Pass.Data, sub));
                var errors = bindings.Check(entry);
                if (errors.Count > 0)
                {
                    NotDrawn(shader, sub.Keywords, $"bindings that do not match their DXBC ({errors[0]})");
                    return program;
                }
                // The inline sampler state's encoding is not read (d3d11-plan.md M1, "Carried to
                // M2, then to M7"): no game shader has one, and the only mod shader with one
                // (Aki's Extra Twitch Events' Hidden/Dither) is never drawn by the original, whose
                // mod loader fails on that mod (d3d11-plan.md M7).
                if (bindings.Samplers.Length > 0)
                {
                    NotDrawn(shader, sub.Keywords, "inline sampler states");
                    return program;
                }
                if (bindings.StructuredBuffers.Length > 0)
                {
                    NotDrawn(shader, sub.Keywords, "structured buffers");
                    return program;
                }
                var stage = new D3d11Stage();
                var cbs = new List<(int, int, int)>();
                foreach (var cb in bindings.ConstantBuffers)
                {
                    int block = blockSizes.Count, size = Math.Max(16, (cb.Size + 15) & ~15);
                    blockSizes.Add(size);
                    cbs.Add((cb.Slot, block, size));
                    // Members at Unity's offsets; matrices as one register per column (the
                    // original's cbuffers are column-major: d3d11-unity-probes.md, measured).
                    foreach (var m in cb.Members) program.Uniforms.Add(Vulkan.VkPrograms.Uniform(m, block));
                }
                var textures = new List<(int, int)>();
                var samplers = new List<(int, int)>();
                foreach (var t in bindings.Textures)
                {
                    if (t.MultiSampled)
                    {
                        NotDrawn(shader, sub.Keywords, "multisample textures");
                        return program;
                    }
                    uint target = t.Dimension switch { 3 => TexTarget3D, 4 => TexTargetCubeMap, 5 => TexTarget2DArray, _ => TexTarget2D };
                    int index = program.Samplers.Count;
                    program.Samplers.Add(new SamplerBinding { Location = t.Slot, Unit = index, Target = target, PropertyId = ShaderPropertyIds.Get(t.Name) });
                    textures.Add((t.Slot, index));
                    // A sampler slot two textures name takes the first's state. Whether any
                    // program shares one is UNVERIFIED (d3d11-plan.md, B2's list).
                    if (t.Sampler >= 0 && !samplers.Exists(x => x.Item1 == t.Sampler)) samplers.Add((t.Sampler, index));
                }
                stage.ConstantBuffers = cbs.ToArray();
                stage.Textures = textures.ToArray();
                stage.Samplers = samplers.ToArray();
                d3d.Stages[s] = stage;
                dxbc[s] = entry.Dxbc.Bytes;
                if (s == 1)
                    foreach (var e in entry.Dxbc.Outputs)
                        // SV_Target<n> writes render target n (in an output signature SV_Target
                        // goes by its semantic; its system value need not be D3D_NAME_TARGET).
                        if ((D3D_NAME)e.SystemValue == D3D_NAME.D3D_NAME_TARGET || string.Equals(e.SemanticName, "SV_Target", StringComparison.OrdinalIgnoreCase))
                            d3d.Targets = Math.Max(d3d.Targets, (int)e.SemanticIndex + 1);
                if (s == 0)
                {
                    var inputs = new List<D3d11Input>();
                    foreach (var e in entry.Dxbc.Inputs)
                    {
                        // System values (SV_VertexID, SV_InstanceID) come from the input
                        // assembler, not from a buffer.
                        if ((D3D_NAME)e.SystemValue != D3D_NAME.D3D_NAME_UNDEFINED) continue;
                        inputs.Add(new D3d11Input(e.SemanticName, e.SemanticIndex, (D3D_REGISTER_COMPONENT_TYPE)e.ComponentType, ChannelOf(e.SemanticName, e.SemanticIndex)));
                    }
                    d3d.Inputs = inputs.ToArray();
                }
            }
            program.BlockSizes = blockSizes.ToArray();
            foreach (var size in program.BlockSizes) program.BlocksLength += size;
            for (int ch = 0; ch < program.Attributes.Length; ch++) program.Attributes[ch] = -1;
            for (int i = 0; i < d3d.Inputs.Length; i++)
                if (d3d.Inputs[i].Channel >= 0) program.Attributes[d3d.Inputs[i].Channel] = i;
            try
            {
                CreateShaders(device, d3d, dxbc);
            }
            catch
            {
                d3d.Release();
                throw;
            }
            d3d.VertexBytecode = dxbc[0];
            program.Id = (uint)System.Threading.Interlocked.Increment(ref nextId);
            program.D3d = d3d;
            return program;
        }

        /// "NAME sSUBpPASS vs#BLOB [keywords] ps#BLOB [keywords] gs#BLOB": the pass by its indexes,
        /// each stage by its program entry.
        /// CoverageOf for a key Select gave (its pass and stages, as the coverage log names them).
        public static string CoverageOfKey(ShaderNative shader, int key)
        {
            var c = choices.GetOrCreateValue(shader);
            lock (c)
                return key >= 0 && key < c.List.Count ? CoverageOf(shader, c.List[key].Pass, c.List[key].Stages) : "?";
        }

        static string CoverageOf(ShaderNative shader, ShaderPass pass, PlayerSubprogram?[] stages)
        {
            string where = "s?p?";
            for (int si = 0; si < shader.SubShaders.Length; si++)
            {
                int pi = Array.IndexOf(shader.SubShaders[si].Passes, pass);
                if (pi >= 0) where = $"s{si}p{pi}";
            }
            var text = new System.Text.StringBuilder($"{shader.ShaderName} {where}");
            string[] names = ["vs", "ps", "gs"];
            for (int s = 0; s < 3 && s < stages.Length; s++)
                if (stages[s] is { } st) text.Append($" {names[s]}#{st.BlobIndex} [{string.Join(" ", st.Keywords)}]");
            return text.ToString();
        }

        /// The shader objects, from the DXBC as shipped (ID3D11Device::CreateVertexShader and its
        /// siblings take "a pointer to the compiled shader" and its length; no class linkage).
        static void CreateShaders(ID3D11Device* device, D3d11Shaders d3d, byte[][] dxbc)
        {
            fixed (byte* vs = dxbc[0])
            {
                ID3D11VertexShader* v;
                D3d11Context.Check(device->lpVtbl->CreateVertexShader(device, vs, (nuint)dxbc[0].Length, null, &v), $"CreateVertexShader ({d3d.Name})");
                d3d.Vertex = v;
            }
            fixed (byte* ps = dxbc[1])
            {
                ID3D11PixelShader* p;
                D3d11Context.Check(device->lpVtbl->CreatePixelShader(device, ps, (nuint)dxbc[1].Length, null, &p), $"CreatePixelShader ({d3d.Name})");
                d3d.Pixel = p;
            }
            if (dxbc[2] != null)
                fixed (byte* gs = dxbc[2])
                {
                    ID3D11GeometryShader* g;
                    D3d11Context.Check(device->lpVtbl->CreateGeometryShader(device, gs, (nuint)dxbc[2].Length, null, &g), $"CreateGeometryShader ({d3d.Name})");
                    d3d.Geometry = g;
                }
        }

        /// The mesh channel a vertex input semantic reads: Unity's channel order (Channel), the
        /// semantics the original's input layouts name (d3d11-captures.md §3: POSITION0,
        /// NORMAL0, TANGENT0, COLOR0, TEXCOORD0-7); -1 for others (BLENDWEIGHTS, BLENDINDICES).
        public static int ChannelOf(string semantic, uint index) => (semantic.ToUpperInvariant(), index) switch
        {
            ("POSITION", 0) => (int)Channel.Position,
            ("NORMAL", 0) => (int)Channel.Normal,
            ("TANGENT", 0) => (int)Channel.Tangent,
            ("COLOR", 0) => (int)Channel.Color,
            ("TEXCOORD", <= 7) => (int)Channel.Uv0 + (int)index,
            _ => -1,
        };
    }
}
