using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.OpenGlApi;
using OniMoono.Engine.Rendering;
using UnityEngine;
using N = Brutal.OpenGlApi.OpenGl.Native;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Desktop.Gl
{
    /// Where a uniform's value comes from.
    internal enum UniformSource
    {
        Property,
        TextureScaleOffset,
        TextureTexelSize,
        TextureHdr,
        ObjectToWorld, WorldToObject, MatrixV, MatrixInvV, MatrixP, MatrixVP, MatrixInvVP, MatrixMVP, CameraProjection, CameraInvProjection,
        CameraToWorld, WorldToCamera,
        Time, SinTime, CosTime, DeltaTime, ScreenParams, ProjectionParams, ZBufferParams, OrthoParams, WorldSpaceCameraPos, WorldTransformParams,
    }

    internal sealed class UniformBinding
    {
        public string Name;
        public int Location = -1;
        public uint Type;
        /// Array length in GLSL elements (vec4 elements for HLSLcc matrices).
        public int Size;
        public bool IsMatrix;
        public UniformSource Source;
        public int PropertyId;
        /// TextureScaleOffset: the id of the texture's "<name>_ST" property, found on first use.
        public int ScaleOffsetId = int.MinValue;
        // Members of uniform blocks.
        public int Block = -1;
        public int Offset;
        public int ArrayStride;
    }

    internal sealed class SamplerBinding
    {
        public int Location;
        public int Unit;
        public uint Target;
        public int PropertyId;
    }

    internal sealed class DeviceProgram
    {
        public uint Id;
        public int[] Attributes = new int[(int)Channel.Count];
        public readonly List<UniformBinding> Uniforms = new();
        public readonly List<SamplerBinding> Samplers = new();
        public int[] BlockSizes = Array.Empty<int>();
        /// Floats of the default-block uniforms' values, recorded per draw in uniform order.
        public int LooseLength;
        /// Bytes of the uniform blocks recorded per draw, one after another (each at least 16).
        public int BlocksLength;
        /// Programs with the same attribute locations share a layout id (vertex arrays are
        /// built per mesh and layout).
        public int LayoutId;
        /// The values last given to each default-block uniform (indexed like Uniforms): a
        /// program keeps its uniform state, so an unchanged value is not sent again.
        public float[][] Sent;
        /// The bindless handle each sampler was last given (GlBindless).
        public ulong[] SamplerHandles;
    }

    /// What a material and the global properties give one program: its default-block values
    /// and uniform-block bytes, and its samplers' textures, as of the versions they were
    /// resolved at (main thread).
    internal sealed class MaterialValues
    {
        /// Written last when the values are resolved again (with release semantics), so a
        /// thread that reads them current (with acquire semantics) sees the values too.
        public int MaterialVersion = -1, GlobalsVersion = -1;
        public readonly float[] Loose;
        public readonly byte[] Blocks;
        public readonly TextureBinding[] Textures;
        public readonly bool[] Found;
        /// Each found texture is the global's (not the material's).
        public readonly bool[] Global;
        /// The pass state resolved from the same values, for one pass (ResolvedState.Of); one
        /// object, so that threads recording a queue replace it whole.
        public ResolvedFor Resolved;

        public MaterialValues(DeviceProgram p)
        {
            Loose = new float[p.LooseLength];
            Blocks = new byte[p.BlocksLength];
            Textures = new TextureBinding[p.Samplers.Count];
            Found = new bool[p.Samplers.Count];
            Global = new bool[p.Samplers.Count];
        }
    }

    /// A shader's compiled programs by variant blob index.
    internal sealed unsafe class DeviceProgramSet
    {
        public readonly Dictionary<int, DeviceProgram> Programs = new();

        public void Delete()
        {
            foreach (var p in Programs.Values)
                if (p.Id != 0)
                {
                    GlBindings.ForgetProgram(p.Id);
                    N.glDeleteProgram(new GlProgram(p.Id));
                }
            Programs.Clear();
        }
    }

    /// Compiles Unity's GLSL variants and binds their inputs by name, as the program text
    /// declares them (HLSLcc names: `hlslcc_mtx4x4X` for matrix X as four column vectors,
    /// `in_POSITION0`… for vertex inputs).
    internal static unsafe class GlPrograms
    {
        static readonly string[] attributeNames =
        [
            "in_POSITION0", "in_NORMAL0", "in_TANGENT0", "in_COLOR0",
            "in_TEXCOORD0", "in_TEXCOORD1", "in_TEXCOORD2", "in_TEXCOORD3", "in_TEXCOORD4", "in_TEXCOORD5", "in_TEXCOORD6", "in_TEXCOORD7",
        ];

        /// The shader's program for a variant blob, built on first use and kept in the shader's
        /// Gpu slot.
        public static DeviceProgram Get(ShaderNative shader, int blobIndex)
        {
            if (shader.Gpu is not DeviceProgramSet set)
            {
                set = new DeviceProgramSet();
                shader.Gpu = set;
                GraphicsDevice.Adopt(shader, set);
            }
            if (set.Programs.TryGetValue(blobIndex, out var p)) return p;
            long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            p = Build(shader, shader.Programs.Variant(blobIndex));
            System.Threading.Interlocked.Add(ref OniMoono.Engine.StartupStats.ProgramTicks, System.Diagnostics.Stopwatch.GetTimestamp() - buildStart);
            System.Threading.Interlocked.Increment(ref OniMoono.Engine.StartupStats.Programs);
            set.Programs[blobIndex] = p;
            return p;
        }

        /// GLSL 1.50 needs extensions for the explicit uniform locations and block bindings
        /// Unity's program text uses (UNITY_LOCATION / UNITY_BINDING).
        static string Prepare(string section)
        {
            int versionEnd = section.IndexOf('\n') + 1;
            var extensions = "";
            if (section.Contains("UNITY_LOCATION(", StringComparison.Ordinal)) extensions += "#extension GL_ARB_explicit_uniform_location : require\n";
            if (section.Contains("UNITY_BINDING(", StringComparison.Ordinal)) extensions += "#extension GL_ARB_shading_language_420pack : require\n";
            var body = section[versionEnd..];
            if (GlBindless.Enabled)
            {
                body = GlBindless.Prepare(body, out var bindless);
                extensions += bindless;
            }
            return section[..versionEnd] + extensions + body;
        }

        /// Extensions the context offers (GL_EXTENSIONS).
        public static HashSet<string> Extensions = new(StringComparer.Ordinal);

        /// A variant is supported when every extension its stages require is available (VR
        /// multiview variants need GL_OVR_multiview2, for instance).
        public static bool Supported(ShaderVariant variant)
        {
            foreach (var source in new[] { variant.VertexSource, variant.FragmentSource, variant.GeometrySource })
            {
                if (source == null) continue;
                foreach (var line in source.Split('\n'))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("#extension ", StringComparison.Ordinal) || !t.EndsWith(": require", StringComparison.Ordinal)) continue;
                    var name = t["#extension ".Length..t.IndexOf(':')].Trim();
                    if (!Extensions.Contains(name)) return false;
                }
            }
            return true;
        }

        static DeviceProgram Build(ShaderNative shader, ShaderVariant variant)
        {
            var program = new DeviceProgram();
            if (!Supported(variant)) return program;
            uint vs = GlApi.CompileShader(VertexShader, Prepare(variant.VertexSource), out var vlog);
            uint fs = GlApi.CompileShader(FragmentShader, Prepare(variant.FragmentSource), out var flog);
            uint gs = 0;
            string glog = "";
            if (variant.GeometrySource != null) gs = GlApi.CompileShader(GeometryShader, Prepare(variant.GeometrySource), out glog);
            if (vs == 0 || fs == 0 || (variant.GeometrySource != null && gs == 0))
            {
                Debug.LogError($"Shader '{shader.ShaderName}' variant {variant.BlobIndex} [{string.Join(" ", variant.Keywords)}] failed to compile:\n{vlog}\n{flog}\n{glog}");
                foreach (var id in new[] { vs, fs, gs })
                    if (id != 0) N.glDeleteShader(new GlShader(id));
                return program;
            }
            program.Id = GlApi.LinkProgram(vs, fs, gs, out var llog);
            foreach (var id in new[] { vs, fs, gs })
                if (id != 0) N.glDeleteShader(new GlShader(id));
            if (program.Id == 0)
            {
                Debug.LogError($"Shader '{shader.ShaderName}' variant {variant.BlobIndex} failed to link:\n{llog}");
                return program;
            }
            for (int i = 0; i < attributeNames.Length; i++) program.Attributes[i] = GlApi.AttribLocation(program.Id, attributeNames[i]);
            var layout = string.Join(",", program.Attributes);
            if (!layouts.TryGetValue(layout, out program.LayoutId)) layouts[layout] = program.LayoutId = layouts.Count;
            Reflect(program);
            foreach (var u in program.Uniforms)
                if (u.Block < 0) program.LooseLength += GlUniforms.Length(u);
            foreach (var size in program.BlockSizes) program.BlocksLength += Math.Max(size, 16);
            return program;
        }

        static readonly Dictionary<string, int> layouts = new();

        static readonly HashSet<string> unsupportedSamplers = new();

        static void Reflect(DeviceProgram p)
        {
            GlBindings.UseProgram(p.Id);
            int blocks = GlApi.GetProgram(p.Id, ActiveUniformBlocks);
            p.BlockSizes = new int[blocks];
            for (uint b = 0; b < blocks; b++)
            {
                p.BlockSizes[b] = GlApi.UniformBlockParameter(p.Id, b, UniformBlockDataSize);
                N.glUniformBlockBinding(new GlProgram(p.Id), b, b);
            }
            int count = GlApi.GetProgram(p.Id, ActiveUniforms);
            int unit = 0;
            for (uint i = 0; i < count; i++)
            {
                var (rawName, type, size) = GlApi.ActiveUniform(p.Id, i);
                var name = rawName.EndsWith("[0]", StringComparison.Ordinal) ? rawName[..^3] : rawName;
                if (IsSampler(type))
                {
                    // This device has no textures of the other targets (multisample, cube array,
                    // 1D, rectangle, buffer). Such a sampler gets its own unit, where nothing is
                    // bound to its target (bindless: no handle is set, so it reads its unit), and
                    // reads texture object 0, which is not complete: a lookup returns (0, 0, 0, 1)
                    // (OpenGL 4.6 section 11.1.3.5), a texel fetch (all a multisample sampler
                    // does) is undefined without robust access (section 11.1.3.2). Reported once
                    // a name.
                    uint target = SamplerTarget(type);
                    if (!HasTextures(target) && unsupportedSamplers.Add(name))
                        OniMoono.Engine.EngineLog.Engine($"shader sampler '{name}' (type 0x{type:X}) reads a kind of texture this device has none of; nothing is bound to it");
                    int location = GlApi.UniformLocation(p.Id, rawName);
                    var s = new SamplerBinding { Location = location, Unit = unit++, Target = target, PropertyId = ShaderPropertyIds.Get(name) };
                    if (!GlBindless.Enabled) N.glUniform1i(location, s.Unit);
                    p.Samplers.Add(s);
                    continue;
                }
                var u = new UniformBinding { Name = name, Type = type, Size = size };
                u.Block = GlApi.ActiveUniformParameter(p.Id, i, UniformBlockIndex);
                if (u.Block >= 0)
                {
                    u.Offset = GlApi.ActiveUniformParameter(p.Id, i, UniformOffset);
                    u.ArrayStride = GlApi.ActiveUniformParameter(p.Id, i, UniformArrayStride);
                }
                else u.Location = GlApi.UniformLocation(p.Id, rawName);
                Classify(u, name);
                p.Uniforms.Add(u);
            }
        }

        internal static void Classify(UniformBinding u, string name)
        {
            const string matrixPrefix = "hlslcc_mtx4x4";
            if (name.StartsWith(matrixPrefix, StringComparison.Ordinal))
            {
                u.IsMatrix = true;
                name = name[matrixPrefix.Length..];
            }
            u.Source = name switch
            {
                "unity_ObjectToWorld" => UniformSource.ObjectToWorld,
                "unity_WorldToObject" => UniformSource.WorldToObject,
                "unity_MatrixV" => UniformSource.MatrixV,
                "unity_MatrixInvV" => UniformSource.MatrixInvV,
                "glstate_matrix_projection" => UniformSource.MatrixP,
                "unity_MatrixVP" => UniformSource.MatrixVP,
                "unity_MatrixInvVP" => UniformSource.MatrixInvVP,
                "glstate_matrix_mvp" or "unity_MatrixMVP" => UniformSource.MatrixMVP,
                "unity_CameraProjection" => UniformSource.CameraProjection,
                "unity_CameraInvProjection" => UniformSource.CameraInvProjection,
                "unity_CameraToWorld" => UniformSource.CameraToWorld,
                "unity_WorldToCamera" => UniformSource.WorldToCamera,
                "_Time" => UniformSource.Time,
                "_SinTime" => UniformSource.SinTime,
                "_CosTime" => UniformSource.CosTime,
                "unity_DeltaTime" => UniformSource.DeltaTime,
                "_ScreenParams" => UniformSource.ScreenParams,
                "_ProjectionParams" => UniformSource.ProjectionParams,
                "_ZBufferParams" => UniformSource.ZBufferParams,
                "unity_OrthoParams" => UniformSource.OrthoParams,
                "_WorldSpaceCameraPos" => UniformSource.WorldSpaceCameraPos,
                "unity_WorldTransformParams" => UniformSource.WorldTransformParams,
                _ => UniformSource.Property,
            };
            if (u.Source != UniformSource.Property) return;
            // Texture-derived vectors: X_ST, X_TexelSize, X_HDR (an explicit X_ST property wins).
            if (name.EndsWith("_ST", StringComparison.Ordinal)) { u.Source = UniformSource.TextureScaleOffset; u.PropertyId = ShaderPropertyIds.Get(name[..^3]); }
            else if (name.EndsWith("_TexelSize", StringComparison.Ordinal)) { u.Source = UniformSource.TextureTexelSize; u.PropertyId = ShaderPropertyIds.Get(name[..^10]); }
            else if (name.EndsWith("_HDR", StringComparison.Ordinal)) { u.Source = UniformSource.TextureHdr; u.PropertyId = ShaderPropertyIds.Get(name[..^4]); }
            else u.PropertyId = ShaderPropertyIds.Get(name);
            u.Name = name;
        }

        /// Every sampler type of GLSL 4.50 (section 4.1.7), as its GL type token (OpenGL 4.6,
        /// table 7.3): float, int and uint samplers of each dimensionality, and the shadow ones.
        /// A sampler missed here became a loose uniform, left pointing at unit 0 alongside a
        /// sampler of another type, which makes the draw INVALID_OPERATION (section 7.11).
        internal static bool IsSampler(uint type) => SamplerTarget(type) != 0;

        /// The texture target a sampler type reads (0: not a sampler type).
        /// Shadow samplers read the same textures as the others, and none has a compare mode:
        /// depth-only render textures (the Depth and Shadowmap formats) are DEPTH_COMPONENT32F
        /// with TEXTURE_COMPARE_MODE left at NONE, and the default textures are RGBA8. A lookup
        /// through a shadow sampler is undefined for both, a depth texture whose compare mode is
        /// NONE and a texture that has no depth format (OpenGL 4.6 section 11.1.3.5). No program
        /// the engine selects has one: the shipped shaders declare them only in the
        /// shadow-receiving variants of Standard, Legacy Shaders/Diffuse and Particles/Standard
        /// Surface (SHADOWS_SCREEN, SHADOWS_DEPTH, SHADOWS_CUBE), and the engine renders no
        /// real-time shadows and sets no SHADOWS_* keyword. Sampling a Shadowmap render texture
        /// that way (Unity: a format "the GPU can automatically do shadowmap comparisons for")
        /// would need COMPARE_REF_TO_TEXTURE on the texture, or on the bindless sampler object.
        internal static uint SamplerTarget(uint type) => type switch
        {
            Sampler2D or Sampler2DShadow or IntSampler2D or UnsignedIntSampler2D => TexTarget2D,
            Sampler3D or IntSampler3D or UnsignedIntSampler3D => TexTarget3D,
            SamplerCube or SamplerCubeShadow or IntSamplerCube or UnsignedIntSamplerCube => TexTargetCubeMap,
            Sampler2DArray or Sampler2DArrayShadow or IntSampler2DArray or UnsignedIntSampler2DArray => TexTarget2DArray,
            Sampler1D or Sampler1DShadow or IntSampler1D or UnsignedIntSampler1D => TexTarget1D,
            Sampler1DArray or Sampler1DArrayShadow or IntSampler1DArray or UnsignedIntSampler1DArray => TexTarget1DArray,
            Sampler2DRect or Sampler2DRectShadow or IntSampler2DRect or UnsignedIntSampler2DRect => TexTargetRectangle,
            SamplerBuffer or IntSamplerBuffer or UnsignedIntSamplerBuffer => TexTargetBuffer,
            SamplerCubeMapArray or SamplerCubeMapArrayShadow or IntSamplerCubeMapArray or UnsignedIntSamplerCubeMapArray => TexTargetCubeMapArray,
            Sampler2DMultisample or IntSampler2DMultisample or UnsignedIntSampler2DMultisample => TexTarget2DMultisample,
            Sampler2DMultisampleArray or IntSampler2DMultisampleArray or UnsignedIntSampler2DMultisampleArray => TexTarget2DMultisampleArray,
            _ => 0,
        };

        /// Targets a texture or a default texture of the device can have: 2D, 2D array, 3D, cube.
        internal static bool HasTextures(uint target) => target is TexTarget2D or TexTarget2DArray or TexTarget3D or TexTargetCubeMap;
    }

    /// Everything a draw's uniforms can depend on.
    internal sealed class DrawInputs
    {
        public Matrix4x4 ObjectToWorld = Matrix4x4.identity;
        public CameraConstants Camera;
        public PropertySheet Block;
        public MaterialNative Material;
        public ShaderNative Shader;

        /// Property lookup order: the renderer's block, the material, the globals; at each, a
        /// float (SetFloat, SetInt) or an integer (SetInteger).
        public bool TryFloat(int id, out float v)
        {
            if (Block != null && FloatOrInt(Block, id, out v)) return true;
            if (Material != null && FloatOrInt(Material.Properties, id, out v)) return true;
            return FloatOrInt(ShaderGlobals.Properties, id, out v);
        }

        static bool FloatOrInt(PropertySheet sheet, int id, out float v)
        {
            if (sheet.Floats.TryGetValue(id, out v)) return true;
            if (sheet.Ints.TryGetValue(id, out var i)) { v = i; return true; }
            return false;
        }

        /// A texture's tiling and offset when no X_ST vector is set: the material's or the
        /// globals'. A block's SetTexture sets only the texture (as Unity's does).
        public bool TryTextureScaleOffset(int id, out Vector4 scaleOffset)
        {
            if (Material != null && Material.Properties.Textures.TryGetValue(id, out var t)) { scaleOffset = t.ScaleOffset; return true; }
            if (ShaderGlobals.Properties.Textures.TryGetValue(id, out t)) { scaleOffset = t.ScaleOffset; return true; }
            scaleOffset = default;
            return false;
        }

        public bool TryVector(int id, out Vector4 v)
        {
            if (Block != null && Block.Vectors.TryGetValue(id, out v)) return true;
            if (Material != null && Material.Properties.Vectors.TryGetValue(id, out v)) return true;
            if (ShaderGlobals.Properties.Vectors.TryGetValue(id, out v)) return true;
            v = default;
            return false;
        }

        public bool TryMatrix(int id, out Matrix4x4 m)
        {
            if (Block != null && Block.Matrices.TryGetValue(id, out m)) return true;
            if (Material != null && Material.Properties.Matrices.TryGetValue(id, out m)) return true;
            if (ShaderGlobals.Properties.Matrices.TryGetValue(id, out m)) return true;
            m = Matrix4x4.zero;
            return false;
        }

        public bool TryTexture(int id, out TextureBinding t) => TryTexture(id, out t, out _);

        /// `global`: the value is the global's. A material's empty value stands only for a
        /// property declared with a default Unity knows; otherwise the global is read
        /// (DefaultTextures).
        public bool TryTexture(int id, out TextureBinding t, out bool global)
        {
            global = false;
            if (Block != null && Block.Textures.TryGetValue(id, out t)) return true;
            if (Material != null && Material.Properties.Textures.TryGetValue(id, out t)
                && ((object)t.Texture != null || DefaultTextures.KeepsGlobalsOut(Shader, id))) return true;
            if (ShaderGlobals.Properties.Textures.TryGetValue(id, out t))
            {
                global = true;
                return true;
            }
            t = default;
            return false;
        }

        public T[] Array<T>(int id, Func<PropertySheet, Dictionary<int, T[]>> select)
        {
            if (Block != null && select(Block).TryGetValue(id, out var a)) return a;
            if (Material != null && select(Material.Properties).TryGetValue(id, out a)) return a;
            return select(ShaderGlobals.Properties).TryGetValue(id, out a) ? a : null;
        }
    }

    /// Writes uniform values: loose uniforms through glUniform*, block members into std140
    /// buffers.
    internal static unsafe class GlUniforms
    {
        /// Resolve's output (per thread: draw queues are recorded on worker threads).
        [ThreadStatic] static float[] scratch;
        /// Array uniforms tightly packed for glUniform*fv.
        static readonly float[] packedScratch = new float[4 * 1024];
        /// Integer uniforms tightly packed for glUniform*iv / *uiv.
        static readonly int[] intScratch = new int[4 * 1024];

        static int Components(uint type) => type switch
        {
            FloatType or IntType or UnsignedIntType or BoolType => 1,
            FloatVec2 or IntVec2 or UintVec2 or BoolVec2 => 2,
            FloatVec3 or IntVec3 or UintVec3 or BoolVec3 => 3,
            FloatMat4 => 16,
            _ => 4,
        };

        /// Signed integer and bool uniforms (bools are set with glUniform*iv).
        static bool IsInt(uint type) => type is IntType or IntVec2 or IntVec3 or IntVec4 or BoolType or BoolVec2 or BoolVec3 or BoolVec4;
        static bool IsUint(uint type) => type is UnsignedIntType or UintVec2 or UintVec3 or UintVec4;

        /// Values as floats, `count` elements of `stride` floats (vec4 slots).
        internal static int Resolve(UniformBinding u, DrawInputs d, Span<float> o)
        {
            var cam = d.Camera;
            if (u.IsMatrix)
            {
                int matrices = Math.Max(1, u.Size / 4);
                if (u.Source == UniformSource.Property && matrices > 1)
                {
                    var arr = d.Array(u.PropertyId, s => s.MatrixArrays);
                    for (int i = 0; i < matrices; i++) WriteMatrix(arr != null && i < arr.Length ? arr[i] : Matrix4x4.zero, o[(16 * i)..]);
                    return matrices * 16;
                }
                Matrix4x4 m = u.Source switch
                {
                    UniformSource.ObjectToWorld => d.ObjectToWorld,
                    UniformSource.WorldToObject => d.ObjectToWorld.inverse,
                    UniformSource.MatrixV => cam.View,
                    UniformSource.MatrixInvV => cam.View.inverse,
                    UniformSource.MatrixP or UniformSource.CameraProjection => cam.Projection,
                    UniformSource.CameraInvProjection => cam.Projection.inverse,
                    UniformSource.MatrixVP => cam.Projection * cam.View,
                    UniformSource.MatrixInvVP => (cam.Projection * cam.View).inverse,
                    UniformSource.MatrixMVP => cam.Projection * cam.View * d.ObjectToWorld,
                    UniformSource.CameraToWorld => cam.View.inverse * Matrix4x4.Scale(new Vector3(1f, 1f, -1f)),
                    UniformSource.WorldToCamera => Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cam.View,
                    _ => d.TryMatrix(u.PropertyId, out var pm) ? pm : Matrix4x4.zero,
                };
                WriteMatrix(m, o);
                return 16;
            }
            Vector4 v;
            switch (u.Source)
            {
                case UniformSource.Time:
                {
                    float t = Time.timeSinceLevelLoad;
                    v = new Vector4(t / 20f, t, t * 2f, t * 3f);
                    break;
                }
                case UniformSource.SinTime:
                {
                    float t = Time.timeSinceLevelLoad;
                    v = new Vector4(Mathf.Sin(t / 8f), Mathf.Sin(t / 4f), Mathf.Sin(t / 2f), Mathf.Sin(t));
                    break;
                }
                case UniformSource.CosTime:
                {
                    float t = Time.timeSinceLevelLoad;
                    v = new Vector4(Mathf.Cos(t / 8f), Mathf.Cos(t / 4f), Mathf.Cos(t / 2f), Mathf.Cos(t));
                    break;
                }
                case UniformSource.DeltaTime:
                {
                    float dt = Time.deltaTime, sdt = Time.smoothDeltaTime;
                    v = new Vector4(dt, dt == 0f ? 0f : 1f / dt, sdt, sdt == 0f ? 0f : 1f / sdt);
                    break;
                }
                case UniformSource.ScreenParams:
                    v = new Vector4(cam.TargetSize.x, cam.TargetSize.y, 1f + 1f / cam.TargetSize.x, 1f + 1f / cam.TargetSize.y);
                    break;
                case UniformSource.ProjectionParams:
                    // OpenGL renders textures upright: no projection flip.
                    v = new Vector4(1f, cam.Near, cam.Far, 1f / cam.Far);
                    break;
                case UniformSource.ZBufferParams:
                {
                    float x = 1f - cam.Far / cam.Near, y = cam.Far / cam.Near;
                    v = new Vector4(x, y, x / cam.Far, y / cam.Far);
                    break;
                }
                case UniformSource.OrthoParams:
                {
                    float h = 1f / cam.Projection.m11, w = 1f / cam.Projection.m00;
                    v = new Vector4(w, h, 0f, cam.Orthographic ? 1f : 0f);
                    break;
                }
                case UniformSource.WorldSpaceCameraPos:
                    v = new Vector4(cam.Position.x, cam.Position.y, cam.Position.z, 0f);
                    break;
                case UniformSource.WorldTransformParams:
                    v = new Vector4(0f, 0f, 0f, d.ObjectToWorld.determinant < 0f ? -1f : 1f);
                    break;
                case UniformSource.TextureScaleOffset:
                    // Found on first use; threads recording at once store the same id.
                    if (u.ScaleOffsetId == int.MinValue) u.ScaleOffsetId = ShaderPropertyIds.Get(ShaderPropertyIds.NameOf(u.PropertyId) + "_ST");
                    v = d.TryVector(u.ScaleOffsetId, out var st) ? st
                        : d.TryTextureScaleOffset(u.PropertyId, out var so) ? so : TextureBinding.IdentityScaleOffset;
                    break;
                case UniformSource.TextureTexelSize:
                {
                    var tex = d.TryTexture(u.PropertyId, out var tb2) ? tb2.Texture : null;
                    v = tex != null ? new Vector4(1f / tex.width, 1f / tex.height, tex.width, tex.height) : new Vector4(1f / 4f, 1f / 4f, 4f, 4f);
                    break;
                }
                case UniformSource.TextureHdr:
                    v = new Vector4(1f, 1f, 0f, 0f);
                    break;
                default:
                    return ResolveProperty(u, d, o);
            }
            o[0] = v.x;
            o[1] = v.y;
            o[2] = v.z;
            o[3] = v.w;
            return 4;
        }

        static int ResolveProperty(UniformBinding u, DrawInputs d, Span<float> o)
        {
            int components = Components(u.Type);
            if (u.Size > 1)
            {
                if (components == 1)
                {
                    var arr = d.Array(u.PropertyId, s => s.FloatArrays);
                    for (int i = 0; i < u.Size; i++) o[i * 4] = arr != null && i < arr.Length ? arr[i] : 0f;
                }
                else
                {
                    var arr = d.Array(u.PropertyId, s => s.VectorArrays);
                    for (int i = 0; i < u.Size; i++)
                    {
                        var e = arr != null && i < arr.Length ? arr[i] : default;
                        o[i * 4] = e.x;
                        o[i * 4 + 1] = e.y;
                        o[i * 4 + 2] = e.z;
                        o[i * 4 + 3] = e.w;
                    }
                }
                return u.Size * 4;
            }
            if (components == 1)
            {
                d.TryFloat(u.PropertyId, out var f);
                o[0] = f;
                return 1;
            }
            d.TryVector(u.PropertyId, out var vec);
            o[0] = vec.x;
            o[1] = vec.y;
            o[2] = vec.z;
            o[3] = vec.w;
            return 4;
        }

        static void WriteMatrix(Matrix4x4 m, Span<float> o)
        {
            MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(in m)).CopyTo(o);
        }

        /// The number of floats Resolve gives for a uniform.
        public static int Length(UniformBinding u)
        {
            if (u.IsMatrix)
            {
                int matrices = Math.Max(1, u.Size / 4);
                return u.Source == UniformSource.Property && matrices > 1 ? matrices * 16 : 16;
            }
            if (u.Source != UniformSource.Property) return 4;
            if (u.Size > 1) return u.Size * 4;
            return Components(u.Type) == 1 ? 1 : 4;
        }

        /// Default-block uniforms sent and skipped since the last frame counters (--frame-stats).
        public static int UniformsSent, UniformsSkipped;

        /// Resolves a draw's uniforms (main thread): default-block values into `values` in
        /// uniform order, block members into `blocks` (laid out as DeviceProgram.BlocksLength).
        /// What the material and the globals give is resolved once per program and material
        /// version (MaterialValues); a draw resolves its per-draw values (matrices, time, texture
        /// sizes) and the properties its own block sets. Returns the material's values (null
        /// without a material).
        public static MaterialValues Record(DeviceProgram p, DrawInputs d, Span<float> values, Span<byte> blocks)
        {
            if (d.Material == null)
            {
                blocks.Clear();
                RecordAll(p, d, values, blocks, perDraw: false, null);
                return null;
            }
            var cached = ValuesOf(p, d);
            RecordFrom(cached, p, d, values, blocks);
            return cached;
        }

        /// A draw's values from the material's (current) values: those copied, then its per-draw
        /// values and its block's properties resolved over them. Reads only: any thread, while
        /// nothing changes the inputs.
        public static void RecordFrom(MaterialValues cached, DeviceProgram p, DrawInputs d, Span<float> values, Span<byte> blocks)
        {
            cached.Loose.AsSpan().CopyTo(values);
            cached.Blocks.AsSpan().CopyTo(blocks);
            var block = d.Block is { IsEmpty: false } b ? b : null;
            RecordAll(p, d, values, blocks, perDraw: true, block);
        }


        /// The material's and the globals' values for a program, resolved again when either has
        /// changed since.
        static MaterialValues ValuesOf(DeviceProgram p, DrawInputs d)
        {
            var material = d.Material;
            var entries = (Dictionary<DeviceProgram, MaterialValues>)(material.DeviceCache ??= new Dictionary<DeviceProgram, MaterialValues>());
            int materialVersion = material.Properties.Version, globalsVersion = ShaderGlobals.Properties.Version;
            if (entries.TryGetValue(p, out var e) && e.MaterialVersion == materialVersion && e.GlobalsVersion == globalsVersion) return e;
            if (e == null) entries[p] = e = new MaterialValues(p);
            Resolve(e, p, d, materialVersion, globalsVersion);
            return e;
        }

        /// Resolves the material's and globals' values into an entry. What it resolves for
        /// per-draw uniforms (from `d`'s matrices and camera) every draw resolves again over it.
        static void Resolve(MaterialValues e, DeviceProgram p, DrawInputs d, int materialVersion, int globalsVersion)
        {
            var block = d.Block;
            d.Block = null;
            try
            {
                Array.Clear(e.Blocks);
                RecordAll(p, d, e.Loose, e.Blocks, perDraw: false, null);
                for (int i = 0; i < p.Samplers.Count; i++)
                    e.Found[i] = d.TryTexture(p.Samplers[i].PropertyId, out e.Textures[i], out e.Global[i]);
            }
            finally
            {
                d.Block = block;
            }
            e.Resolved = null;
            System.Threading.Volatile.Write(ref e.MaterialVersion, materialVersion);
            System.Threading.Volatile.Write(ref e.GlobalsVersion, globalsVersion);
        }

        /// ValuesOf for threads recording one queue at once (while nothing changes the material
        /// or the globals): an entry found out of date is resolved again by one of them, under
        /// its lock, the others reading it once it is current. Null when the material has no
        /// entry for the program yet (adding one changes the material's table: main thread).
        public static MaterialValues SharedValuesOf(DeviceProgram p, DrawInputs d)
        {
            var material = d.Material;
            if (material?.DeviceCache is not Dictionary<DeviceProgram, MaterialValues> entries || !entries.TryGetValue(p, out var e)) return null;
            int materialVersion = material.Properties.Version, globalsVersion = ShaderGlobals.Properties.Version;
            if (Current(e, materialVersion, globalsVersion)) return e;
            lock (e)
            {
                if (!Current(e, materialVersion, globalsVersion)) Resolve(e, p, d, materialVersion, globalsVersion);
            }
            return e;
        }

        static bool Current(MaterialValues e, int materialVersion, int globalsVersion)
            => System.Threading.Volatile.Read(ref e.GlobalsVersion) == globalsVersion && System.Threading.Volatile.Read(ref e.MaterialVersion) == materialVersion;

        static bool OwnValue(UniformBinding u, PropertySheet block) => u.Source != UniformSource.Property || (block != null && Sets(block, u));

        /// Whether a draw's block sets the property a uniform reads.
        static bool Sets(PropertySheet block, UniformBinding u)
        {
            int id = u.PropertyId;
            if (u.IsMatrix) return u.Size / 4 > 1 ? block.MatrixArrays.ContainsKey(id) : block.Matrices.ContainsKey(id);
            if (Components(u.Type) == 1) return u.Size > 1 ? block.FloatArrays.ContainsKey(id) : block.Floats.ContainsKey(id) || block.Ints.ContainsKey(id);
            return u.Size > 1 ? block.VectorArrays.ContainsKey(id) : block.Vectors.ContainsKey(id);
        }

        /// Resolves uniforms into their places: all of them, or with perDraw those a draw resolves
        /// itself (not read from properties, or set by its block).
        static void RecordAll(DeviceProgram p, DrawInputs d, Span<float> values, Span<byte> blocks, bool perDraw, PropertySheet block)
        {
            Span<float> o = scratch ??= new float[4 * 1024];
            Span<int> blockStarts = stackalloc int[p.BlockSizes.Length];
            for (int b = 0, at = 0; b < blockStarts.Length; b++)
            {
                blockStarts[b] = at;
                at += Math.Max(p.BlockSizes[b], 16);
            }
            int next = 0;
            foreach (var u in p.Uniforms)
            {
                int length = Length(u);
                if (u.Block >= 0)
                {
                    if (perDraw && !OwnValue(u, block)) continue;
                    int n = Resolve(u, d, o);
                    if (n != length) throw new InvalidOperationException($"Uniform '{u.Name}': {n} values resolved, {length} expected.");
                    WriteBlock(blocks.Slice(blockStarts[u.Block], p.BlockSizes[u.Block]), u, o[..n]);
                    continue;
                }
                var target = values.Slice(next, length);
                next += length;
                if (perDraw && !OwnValue(u, block)) continue;
                // Slots a value leaves unwritten (padding of float arrays) compare equal from draw to draw.
                target.Clear();
                int written = Resolve(u, d, target);
                if (written != length) throw new InvalidOperationException($"Uniform '{u.Name}': {written} values resolved, {length} expected.");
            }
        }

        /// Sets the default-block uniforms recorded for a draw (GL thread), skipping values the
        /// program already holds.
        public static void Send(DeviceProgram p, ReadOnlySpan<float> values)
        {
            var sent = p.Sent ??= new float[p.Uniforms.Count][];
            int next = 0;
            for (int index = 0; index < p.Uniforms.Count; index++)
            {
                var u = p.Uniforms[index];
                if (u.Block >= 0) continue;
                int n = Length(u);
                var o = values.Slice(next, n);
                next += n;
                if (u.Location < 0) continue;
                var last = sent[index];
                if (last != null && MemoryMarshal.Cast<float, int>(last.AsSpan()).SequenceEqual(MemoryMarshal.Cast<float, int>(o))) // bitwise: -0 and NaNs are values too
                {
                    UniformsSkipped++;
                    continue;
                }
                UniformsSent++;
                if (last == null || last.Length != n) sent[index] = last = new float[n];
                o.CopyTo(last);
                fixed (float* f = o)
                {
                    if (IsInt(u.Type) || IsUint(u.Type))
                    {
                        // Scalars, vectors and arrays of them, with the call of their own type
                        // and size (any other is GL_INVALID_OPERATION), elements tightly packed.
                        bool unsigned = IsUint(u.Type);
                        int comps = Components(u.Type), elements = Math.Max(1, u.Size);
                        for (int e = 0; e < elements; e++)
                            for (int c = 0; c < comps; c++)
                                intScratch[e * comps + c] = unsigned ? (int)(uint)o[e * 4 + c] : (int)o[e * 4 + c];
                        fixed (int* ip = intScratch)
                        {
                            switch (comps, unsigned)
                            {
                                case (1, false): N.glUniform1iv(u.Location, elements, ip); break;
                                case (2, false): N.glUniform2iv(u.Location, elements, ip); break;
                                case (3, false): N.glUniform3iv(u.Location, elements, ip); break;
                                case (_, false): N.glUniform4iv(u.Location, elements, ip); break;
                                case (1, true): N.glUniform1uiv(u.Location, elements, (uint*)ip); break;
                                case (2, true): N.glUniform2uiv(u.Location, elements, (uint*)ip); break;
                                case (3, true): N.glUniform3uiv(u.Location, elements, (uint*)ip); break;
                                default: N.glUniform4uiv(u.Location, elements, (uint*)ip); break;
                            }
                        }
                    }
                    else if (u.IsMatrix || u.Size > 1 && Components(u.Type) == 4) N.glUniform4fv(u.Location, n / 4, f);
                    else if (u.Size > 1)
                    {
                        // An array in one call from its base location, elements tightly packed:
                        // GL gives no other element a location that is safe to assume.
                        int comps = Components(u.Type);
                        var packed = packedScratch.AsSpan(0, u.Size * comps);
                        for (int i = 0; i < u.Size; i++)
                            for (int c = 0; c < comps; c++) packed[i * comps + c] = o[i * 4 + c];
                        fixed (float* pp = packed)
                        {
                            switch (comps)
                            {
                                case 1: N.glUniform1fv(u.Location, u.Size, pp); break;
                                case 2: N.glUniform2fv(u.Location, u.Size, pp); break;
                                default: N.glUniform3fv(u.Location, u.Size, pp); break;
                            }
                        }
                    }
                    else switch (Components(u.Type))
                    {
                        case 1: N.glUniform1fv(u.Location, 1, f); break;
                        case 2: N.glUniform2fv(u.Location, 1, f); break;
                        case 3: N.glUniform3fv(u.Location, 1, f); break;
                        default: N.glUniform4fv(u.Location, 1, f); break;
                    }
                }
            }
        }

        /// std140: element i of an array at offset + i * stride; matrices are four columns.
        static void WriteBlock(Span<byte> block, UniformBinding u, ReadOnlySpan<float> values)
        {
            var target = MemoryMarshal.Cast<byte, float>(block);
            int components = u.IsMatrix ? 4 : Math.Min(4, Components(u.Type));
            int elements = u.IsMatrix ? values.Length / 4 : Math.Max(1, u.Size);
            int stride = u.ArrayStride > 0 ? u.ArrayStride / 4 : 4;
            int start = u.Offset / 4;
            for (int e = 0; e < elements; e++)
            {
                int src = (u.IsMatrix || u.Size > 1) ? e * 4 : 0;
                for (int c = 0; c < components && src + c < values.Length; c++)
                {
                    int dst = start + e * stride + c;
                    if (dst >= target.Length) return;
                    if (IsInt(u.Type)) target[dst] = BitConverter.Int32BitsToSingle((int)values[src + c]);
                    else if (IsUint(u.Type)) target[dst] = BitConverter.Int32BitsToSingle((int)(uint)values[src + c]);
                    else target[dst] = values[src + c];
                }
            }
        }
    }
}
