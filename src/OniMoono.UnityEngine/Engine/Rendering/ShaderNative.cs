using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Serialization;
using UnityEngine;

namespace OniMoono.Engine.Rendering
{
    /// SerializedPropertyType.
    internal enum ShaderPropertyKind
    {
        Color = 0,
        Vector = 1,
        Float = 2,
        Range = 3,
        Texture = 4,
        Int = 5,
    }

    /// SerializedPropertyFlag.
    [Flags]
    internal enum ShaderPropertyAttributes
    {
        HideInInspector = 1,
        PerRendererData = 2,
        NoScaleOffset = 4,
        Normal = 8,
        HDR = 16,
        Gamma = 32,
        NonModifiableTextureData = 64,
        MainTexture = 128,
        MainColor = 256,
    }

    internal sealed class ShaderProperty
    {
        public string Name;
        public int Id;
        public ShaderPropertyKind Kind;
        public ShaderPropertyAttributes Attributes;
        public Vector4 Default;
        /// Default texture of a texture property: "white", "black", "gray", "bump", "red" ("" is gray).
        public string DefaultTexture;
        /// TextureDimension of a texture property.
        public int TextureDimension;
    }

    internal sealed class ShaderPass
    {
        public string Name;
        /// 0 normal pass, 1 UsePass, 2 GrabPass.
        public int Type;
        public string UseName;
        /// The pass's tags. A normal pass keeps them in its state (`m_State.m_Tags`, with the
        /// subshader's merged in: LightMode, RenderType...); a GrabPass keeps its own in `m_Tags`.
        public Dictionary<string, string> Tags;
        /// The pass's SerializedPass data (render state and programs) for the device.
        public Fields Data;
        /// Its OpenGLCore program variants: blob index and keywords (ShaderPrograms.SelectVariant).
        public ProgramVariant[] Variants = Array.Empty<ProgramVariant>();
        /// Its Metal programs, one list per stage, each keyed by that stage's own keywords
        /// (ShaderPrograms.SelectStages); empty for shaders without Metal programs.
        public ProgramVariant[] MetalVertexVariants = Array.Empty<ProgramVariant>();
        public ProgramVariant[] MetalFragmentVariants = Array.Empty<ProgramVariant>();
        /// Its Vulkan programs (every stage in one, keyed by the union of their keywords, with
        /// their parameter blobs); empty for shaders without Vulkan programs. Read on first use
        /// (only the Vulkan device asks).
        public ProgramVariant[] VulkanVariants => vulkanVariants ??= Legacy || KeywordNames == null
            ? Array.Empty<ProgramVariant>() : ShaderPrograms.StageVariants(Data, "progVertex", KeywordNames, ShaderNative.VulkanPlatform);
        ProgramVariant[] vulkanVariants;
        /// The shader's keyword names, and whether it is a legacy (pre-parsed-form) shader.
        public string[] KeywordNames;
        public bool Legacy;
    }

    /// A program variant: its entry in the platform's blob table, its keywords and (Metal) its
    /// parameter blob (-1: none recorded).
    internal readonly record struct ProgramVariant(int BlobIndex, string[] Keywords, int ParameterBlobIndex = -1);

    internal sealed class SubShader
    {
        public ShaderPass[] Passes;
        public Dictionary<string, string> Tags;
        public int Lod;

        /// The "Queue" tag parsed (ShaderNative.ParseQueue), Geometry (2000) without one; tags do
        /// not change after load, and renderers ask for every draw.
        public int Queue => queue ??= Tags != null && Tags.TryGetValue("Queue", out var q) ? ShaderNative.ParseQueue(q) : 2000;
        int? queue;
    }

    /// A shader: its parsed ShaderLab form plus the compiled program data.
    internal sealed class ShaderNative : NativeObject
    {
        /// GLCore in ShaderCompilerPlatform.
        public const uint OpenGLCorePlatform = ShaderCompilerPlatform.OpenGLCore;
        /// Metal in ShaderCompilerPlatform.
        public const uint MetalPlatform = ShaderCompilerPlatform.Metal;
        /// Vulkan in ShaderCompilerPlatform.
        public const uint VulkanPlatform = ShaderCompilerPlatform.Vulkan;

        public string ShaderName = "";
        /// The device's compiled programs (see MeshNative.Gpu).
        public object Gpu;
        /// The ShaderCompilerPlatform whose programs the device draws this shader with, once the
        /// device has chosen (0: not yet). The Vulkan device runs U60's Vulkan programs, or the
        /// GLCore ones for a shader without usable Vulkan programs.
        public volatile uint DevicePlatform;
        public ShaderProperty[] Properties = Array.Empty<ShaderProperty>();
        public Dictionary<int, ShaderProperty> PropertiesById = new();
        public SubShader[] SubShaders = Array.Empty<SubShader>();
        public string[] KeywordNames = Array.Empty<string>();
        public string FallbackName = "";
        public bool HasOpenGLCorePrograms;
        public int MaximumLOD = -1;
        /// The whole serialized shader, for the device (program blobs and their tables).
        public Fields Data;

        /// The ShaderCompilerPlatforms the shader carries programs for.
        public uint[] Platforms = Array.Empty<uint>();

        ShaderPrograms programs;
        bool programsLoaded;
        ShaderPrograms metalPrograms;
        bool metalProgramsLoaded;
        ShaderPrograms vulkanPrograms;
        bool vulkanProgramsLoaded;

        /// The OpenGLCore programs (null when the shader has none).
        public ShaderPrograms Programs
        {
            get
            {
                if (!programsLoaded)
                {
                    programs = Data == null ? null : ShaderPrograms.Load(Data);
                    programsLoaded = true;
                }
                return programs;
            }
        }

        /// Whether the shader carries programs for a ShaderCompilerPlatform.
        public bool HasProgramsFor(uint platform) => platform == OpenGLCorePlatform ? HasOpenGLCorePrograms : Array.IndexOf(Platforms, platform) >= 0;

        /// The programs for a ShaderCompilerPlatform (OpenGLCore, Metal or Vulkan; null when the
        /// shader has none for it).
        public ShaderPrograms ProgramsFor(uint platform)
        {
            if (platform == OpenGLCorePlatform) return Programs;
            if (platform == VulkanPlatform)
                lock (this)
                {
                    if (!vulkanProgramsLoaded)
                    {
                        vulkanPrograms = Data == null ? null : ShaderPrograms.Load(Data, VulkanPlatform);
                        vulkanProgramsLoaded = true;
                    }
                    return vulkanPrograms;
                }
            if (platform != MetalPlatform) throw new ArgumentOutOfRangeException(nameof(platform), platform, "Programs are read for OpenGLCore (15), Metal (14) and Vulkan (18).");
            lock (this)
            {
                if (!metalProgramsLoaded)
                {
                    metalPrograms = Data == null ? null : ShaderPrograms.Load(Data, MetalPlatform);
                    metalProgramsLoaded = true;
                }
                return metalPrograms;
            }
        }

        bool? readsVertexPosition;

        /// Whether any of its OpenGLCore vertex programs reads the vertex position (its channel
        /// mask's position bit); true when that is not known (no OpenGLCore programs). A program
        /// that reads none (Klei's BatchedAnimationUI places its quads from instance data and
        /// unity_ObjectToWorld alone) draws the same whatever space the mesh is baked in, but not
        /// whatever object-to-world transform it is given.
        public bool ReadsVertexPosition => readsVertexPosition ??= FindReadsVertexPosition();

        bool FindReadsVertexPosition()
        {
            var programs = Programs;
            if (programs == null) return true;
            bool any = false;
            foreach (var sub in SubShaders)
                foreach (var pass in sub.Passes)
                    foreach (var variant in pass.Variants)
                    {
                        any = true;
                        if ((programs.Variant(variant.BlobIndex).ChannelMask & 1u) != 0) return true;
                    }
            return !any;
        }

        /// Shader (class 48).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Data = f;
            readsVertexPosition = null;
            var parsed = f.Struct("m_ParsedForm");
            ShaderName = parsed.String("m_Name");
            if (Name.Length == 0) Name = ShaderName;
            FallbackName = parsed.String("m_FallbackName");
            // Shaders built before Unity 2021.2 (a mod's bundle) name their keywords per pass.
            bool legacy = ShaderPrograms.IsLegacy(f);
            KeywordNames = legacy ? Array.Empty<string>() : parsed.Array("m_KeywordNames").Cast<string>().ToArray();

            var props = parsed.Struct("m_PropInfo").Array("m_Props").Cast<Fields>().Select(p =>
            {
                var tex = p.Struct("m_DefTexture");
                var name = p.String("m_Name");
                return new ShaderProperty
                {
                    Name = name,
                    Id = ShaderPropertyIds.Get(name),
                    Kind = (ShaderPropertyKind)p.Int("m_Type"),
                    Attributes = (ShaderPropertyAttributes)p.Get<uint>("m_Flags"),
                    Default = new Vector4(p.Float("m_DefValue[0]"), p.Float("m_DefValue[1]"), p.Float("m_DefValue[2]"), p.Float("m_DefValue[3]")),
                    DefaultTexture = tex.String("m_DefaultName"),
                    TextureDimension = tex.Int("m_TexDim"),
                };
            }).ToArray();
            Properties = props;
            PropertiesById = new Dictionary<int, ShaderProperty>();
            foreach (var p in props) PropertiesById.TryAdd(p.Id, p);

            SubShaders = parsed.Array("m_SubShaders").Cast<Fields>().Select(s => new SubShader
            {
                Tags = TagMap(s.Struct("m_Tags")),
                Lod = s.Int("m_LOD"),
                Passes = s.Array("m_Passes").Cast<Fields>().Select(p => new ShaderPass
                {
                    Name = p.Struct("m_State").String("m_Name"),
                    Type = p.Int("m_Type"),
                    UseName = p.String("m_UseName"),
                    Tags = PassTags(p),
                    Data = p,
                    Variants = legacy ? ShaderPrograms.LegacyVariants(p) : ShaderPrograms.Variants(p, KeywordNames),
                    MetalVertexVariants = legacy ? Array.Empty<ProgramVariant>() : ShaderPrograms.StageVariants(p, "progVertex", KeywordNames, MetalPlatform),
                    MetalFragmentVariants = legacy ? Array.Empty<ProgramVariant>() : ShaderPrograms.StageVariants(p, "progFragment", KeywordNames, MetalPlatform),
                    KeywordNames = KeywordNames,
                    Legacy = legacy,
                }).ToArray(),
            }).ToArray();

            Platforms = f.Get<uint[]>("platforms");
            lock (this)
            {
                metalPrograms = null;
                metalProgramsLoaded = false;
                vulkanPrograms = null;
                vulkanProgramsLoaded = false;
            }
            HasOpenGLCorePrograms = Platforms.Contains(OpenGLCorePlatform);
            ShaderRegistry.Register(this);
        }

        /// ShaderLab tag names are case-insensitive (the serialized data stores some upper-case,
        /// e.g. "QUEUE").
        static Dictionary<string, string> TagMap(Fields tagMap)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Fields pair in tagMap.Array("tags")) map[pair.String("first")] = pair.String("second");
            return map;
        }

        /// A pass's tags: those of its state over its own (ShaderPass.Tags).
        static Dictionary<string, string> PassTags(Fields pass)
        {
            var map = TagMap(pass.Struct("m_Tags"));
            foreach (var kv in TagMap(pass.Struct("m_State").Struct("m_Tags"))) map[kv.Key] = kv.Value;
            return map;
        }

        /// The subshader used for rendering: the first whose LOD is within the shader's and the
        /// global maximum LOD.
        public SubShader ActiveSubShader
        {
            get
            {
                int max = Math.Min(MaximumLOD < 0 ? int.MaxValue : MaximumLOD, ShaderGlobals.MaximumLOD);
                foreach (var s in SubShaders)
                    if (s.Lod <= max) return s;
                return SubShaders.Length > 0 ? SubShaders[^1] : null;
            }
        }

        /// From the active subshader's "Queue" tag ("Geometry+1"…); Geometry (2000) otherwise.
        public int RenderQueue
        {
            get
            {
                return ActiveSubShader?.Queue ?? 2000;
            }
        }

        public static int ParseQueue(string queue)
        {
            int offset = 0;
            int sign = queue.IndexOfAny(['+', '-']);
            string name = queue;
            if (sign > 0)
            {
                name = queue[..sign];
                offset = int.Parse(queue[sign..].TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture);
            }
            int baseQueue = name.Trim().ToLowerInvariant() switch
            {
                "background" => 1000,
                "geometry" => 2000,
                "alphatest" => 2450,
                "geometrylast" => 2500,
                "transparent" => 3000,
                "overlay" => 4000,
                var n => int.TryParse(n, out int v) ? v : 2000,
            };
            return baseQueue + offset;
        }

        public int PassCount => ActiveSubShader?.Passes.Length ?? 0;

        public ShaderProperty FirstWith(ShaderPropertyAttributes attribute)
        {
            foreach (var p in Properties)
                if ((p.Attributes & attribute) != 0) return p;
            return null;
        }
    }

    /// Shader.Find's name table: shaders already loaded, then the project's ShaderNameRegistry.
    /// Entries are weak: the table does not keep unused shaders loaded.
    internal static class ShaderRegistry
    {
        static readonly Dictionary<string, WeakReference<ShaderNative>> loaded = new(StringComparer.Ordinal);

        public static void Register(ShaderNative shader)
        {
            lock (loaded) loaded[shader.ShaderName] = new WeakReference<ShaderNative>(shader);
        }

        public static void Unregister(ShaderNative shader)
        {
            lock (loaded)
                if (loaded.TryGetValue(shader.ShaderName, out var w) && w.TryGetTarget(out var s) && s == shader) loaded.Remove(shader.ShaderName);
        }

        public static Shader Find(string name)
        {
            if (name == null) return null;
            lock (loaded)
                if (loaded.TryGetValue(name, out var w) && w.TryGetTarget(out var s) && s.Managed is Shader live && live != null) return live;
            var pptr = ProjectSettings.ShaderNames.GetValueOrDefault(name);
            if (pptr.IsNull) return null;
            return ObjectLoader.Resolve(ProjectSettings.Managers, pptr) as Shader;
        }
    }
}
