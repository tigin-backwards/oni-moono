using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;

namespace UnityEngine
{
    public partial class Shader
    {
        ShaderNative Data => (ShaderNative)NativeOrThrow;

        public static int PropertyToID(string name) => ShaderPropertyIds.Get(name);

        /// Looks the shader up by its ShaderLab name; null when there is none.
        public static Shader Find(string name) => ShaderRegistry.Find(name);

        /// Whether the device can draw the shader (it has programs for the device's graphics API).
        public bool isSupported => GraphicsDevice.Current.Supports(Data);

        public int renderQueue => Data.RenderQueue;

        public int passCount => Data.PassCount;

        /// The ShaderLab properties in their declared order, as Unity lists them (measured in the
        /// original player; Morphs Not Included looks for a material's texture properties).
        public int GetPropertyCount() => Data.Properties.Length;

        public string GetPropertyName(int propertyIndex) => Property(propertyIndex).Name;

        public UnityEngine.Rendering.ShaderPropertyType GetPropertyType(int propertyIndex) => (UnityEngine.Rendering.ShaderPropertyType)Property(propertyIndex).Kind;

        /// An index out of range throws as Unity's does (the exception's default message).
        ShaderProperty Property(int index)
        {
            var properties = Data.Properties;
            if (index < 0 || index >= properties.Length) throw new ArgumentOutOfRangeException();
            return properties[index];
        }

        public int maximumLOD
        {
            get => Data.MaximumLOD;
            set => Data.MaximumLOD = value;
        }

        public static int globalMaximumLOD
        {
            get => ShaderGlobals.MaximumLOD;
            set => ShaderGlobals.MaximumLOD = value;
        }

        public static void WarmupAllShaders() => GraphicsDevice.Current.WarmupShaders();

        public static void EnableKeyword(string keyword) => ShaderGlobals.Keywords.Enable(keyword);
        public static void DisableKeyword(string keyword) => ShaderGlobals.Keywords.Disable(keyword);
        public static bool IsKeywordEnabled(string keyword) => ShaderGlobals.Keywords.Contains(keyword);

        // Unity's SetGlobalInt writes a float property.
        public static void SetGlobalInt(string name, int value) => ShaderGlobals.Properties.SetFloat(PropertyToID(name), value);
        public static void SetGlobalInt(int nameID, int value) => ShaderGlobals.Properties.SetFloat(nameID, value);
        public static void SetGlobalFloat(string name, float value) => ShaderGlobals.Properties.SetFloat(PropertyToID(name), value);
        public static void SetGlobalFloat(int nameID, float value) => ShaderGlobals.Properties.SetFloat(nameID, value);
        public static void SetGlobalInteger(string name, int value) => ShaderGlobals.Properties.SetInt(PropertyToID(name), value);
        public static void SetGlobalInteger(int nameID, int value) => ShaderGlobals.Properties.SetInt(nameID, value);
        public static void SetGlobalColor(string name, Color value) => ShaderGlobals.Properties.SetVector(PropertyToID(name), value);
        public static void SetGlobalColor(int nameID, Color value) => ShaderGlobals.Properties.SetVector(nameID, value);
        public static void SetGlobalVector(string name, Vector4 value) => ShaderGlobals.Properties.SetVector(PropertyToID(name), value);
        public static void SetGlobalVector(int nameID, Vector4 value) => ShaderGlobals.Properties.SetVector(nameID, value);
        public static void SetGlobalMatrix(string name, Matrix4x4 value) => ShaderGlobals.Properties.SetMatrix(PropertyToID(name), value);
        public static void SetGlobalMatrix(int nameID, Matrix4x4 value) => ShaderGlobals.Properties.SetMatrix(nameID, value);
        public static void SetGlobalTexture(string name, Texture value) => ShaderGlobals.Properties.SetTexture(PropertyToID(name), value);
        public static void SetGlobalTexture(int nameID, Texture value) => ShaderGlobals.Properties.SetTexture(nameID, value);

        public static float GetGlobalFloat(string name) => GetGlobalFloat(PropertyToID(name));
        public static float GetGlobalFloat(int nameID) => ShaderGlobals.Properties.Floats.GetValueOrDefault(nameID);
        public static Vector4 GetGlobalVector(string name) => GetGlobalVector(PropertyToID(name));
        public static Vector4 GetGlobalVector(int nameID) => ShaderGlobals.Properties.Vectors.GetValueOrDefault(nameID);
        public static Color GetGlobalColor(string name) => GetGlobalVector(PropertyToID(name));
        public static Color GetGlobalColor(int nameID) => GetGlobalVector(nameID);
        public static Texture GetGlobalTexture(string name) => GetGlobalTexture(PropertyToID(name));
        /// Null for a texture since destroyed, and for a render texture never created
        /// (measured: tools/UnityProbe, texfallback).
        public static Texture GetGlobalTexture(int nameID)
            => ShaderGlobals.Properties.Textures.TryGetValue(nameID, out var t) && t.Texture != null
               && t.Texture.Native is not RenderTextureNative { EverCreated: false } ? t.Texture : null;

        internal override void EngineOnDestroy()
        {
            ShaderRegistry.Unregister(Data);
            GraphicsDevice.Release(ref Data.Gpu);
        }
    }

    public partial class Material
    {
        static readonly int k_ColorId = Shader.PropertyToID("_Color");
        static readonly int k_MainTexId = Shader.PropertyToID("_MainTex");

        public Material(Shader shader)
        {
            if ((object)shader == null) throw new ArgumentNullException(nameof(shader));
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            // Named after the shader, as in the original (tools/UnityProbe, texfallback).
            var m = new MaterialNative { Name = shader.name };
            m.AssignShader(shader);
            Bind(m);
        }

        /// A copy of the source material, its name included.
        public Material(Material source)
        {
            if ((object)source == null) throw new ArgumentNullException(nameof(source));
            if (source == null) throw new ArgumentNullException(nameof(source));
            var m = new MaterialNative();
            m.CopyFrom((MaterialNative)source.NativeOrThrow, includeShader: true);
            m.Name = source.name;
            Bind(m);
        }

        internal MaterialNative Data => (MaterialNative)NativeOrThrow;

        internal override Object EngineCloneAsset() => new Material(this);

        /// Assigning a shader keeps property values the new shader also has and adds the new
        /// shader's defaults for the rest.
        public Shader shader
        {
            get => Data.Shader;
            set => Data.AssignShader(value);
        }

        public Color color
        {
            get => GetColor(MainProperty(ShaderPropertyAttributes.MainColor, k_ColorId));
            set => SetColor(MainProperty(ShaderPropertyAttributes.MainColor, k_ColorId), value);
        }

        public Texture mainTexture
        {
            get => GetTexture(MainProperty(ShaderPropertyAttributes.MainTexture, k_MainTexId));
            set => SetTexture(MainProperty(ShaderPropertyAttributes.MainTexture, k_MainTexId), value);
        }

        public Vector2 mainTextureOffset
        {
            get => GetTextureOffset(MainProperty(ShaderPropertyAttributes.MainTexture, k_MainTexId));
            set => SetTextureOffset(MainProperty(ShaderPropertyAttributes.MainTexture, k_MainTexId), value);
        }

        public Vector2 mainTextureScale
        {
            get => GetTextureScale(MainProperty(ShaderPropertyAttributes.MainTexture, k_MainTexId));
            set => SetTextureScale(MainProperty(ShaderPropertyAttributes.MainTexture, k_MainTexId), value);
        }

        /// The first property flagged [MainColor]/[MainTexture], else the conventional name.
        int MainProperty(ShaderPropertyAttributes attribute, int fallback)
        {
            var s = Data.Shader;
            var p = s != null ? ((ShaderNative)s.NativeOrThrow).FirstWith(attribute) : null;
            return p?.Id ?? fallback;
        }

        /// The custom queue when set (≥ 0), otherwise the shader's.
        public int renderQueue
        {
            get => Data.RenderQueue;
            set
            {
                var m = Data;
                m.CustomRenderQueue = Math.Clamp(value, -1, 5000);
                m.Changed();
            }
        }

        public int rawRenderQueue => Data.CustomRenderQueue;

        public MaterialGlobalIlluminationFlags globalIlluminationFlags
        {
            get => Data.GlobalIlluminationFlags;
            set { var m = Data; m.GlobalIlluminationFlags = value; m.Changed(); }
        }

        public bool enableInstancing
        {
            get => Data.EnableInstancing;
            set { var m = Data; m.EnableInstancing = value; m.Changed(); }
        }

        public bool doubleSidedGI
        {
            get => Data.DoubleSidedGI;
            set { var m = Data; m.DoubleSidedGI = value; m.Changed(); }
        }

        public int passCount
        {
            get
            {
                var s = Data.Shader;
                return s != null ? ((ShaderNative)s.NativeOrThrow).PassCount : 0;
            }
        }

        public string[] shaderKeywords
        {
            get => Data.Keywords.ToArray();
            set { var m = Data; m.Keywords.Set(value); m.Changed(); }
        }

        public void EnableKeyword(string keyword) { var m = Data; m.Keywords.Enable(keyword); m.Changed(); }
        public void DisableKeyword(string keyword) { var m = Data; m.Keywords.Disable(keyword); m.Changed(); }
        public bool IsKeywordEnabled(string keyword) => Data.Keywords.Contains(keyword);

        /// Whether the material's shader declares the property, or the material holds a
        /// texture for it (true after SetTexture of an undeclared property, as in the original;
        /// tools/UnityProbe, texfallback).
        public bool HasProperty(int nameID)
        {
            var s = Data.Shader;
            return s != null && ((ShaderNative)s.NativeOrThrow).PropertiesById.ContainsKey(nameID) || Sheet.Textures.ContainsKey(nameID);
        }

        public bool HasProperty(string name) => HasProperty(Shader.PropertyToID(name));

        public string GetTag(string tag, bool searchFallbacks) => GetTag(tag, searchFallbacks, "");

        /// Material override tags first, then the active subshader's tags.
        public string GetTag(string tag, bool searchFallbacks, string defaultValue)
        {
            var m = Data;
            if (m.Tags.TryGetValue(tag, out var v)) return v;
            var s = m.Shader;
            var sub = s != null ? ((ShaderNative)s.NativeOrThrow).ActiveSubShader : null;
            return sub != null && sub.Tags.TryGetValue(tag, out v) ? v : defaultValue;
        }

        public void SetOverrideTag(string tag, string val)
        {
            var m = Data;
            if (string.IsNullOrEmpty(val)) m.Tags.Remove(tag);
            else m.Tags[tag] = val;
            m.Changed();
        }

        /// Activates a pass for immediate drawing; false (with Unity's error) for an invalid pass.
        public bool SetPass(int pass)
        {
            Threads.AssertMainThread("SetPass");
            int count = passCount;
            if (pass < 0 || pass >= count)
            {
                Debug.LogError($"Trying to access pass {pass}, but material '{name}' subshader (0) has only {count} valid passes.", this);
                return false;
            }
            return GraphicsDevice.Current.SetPass(this, pass);
        }

        /// Property values, keywords, render queue and GI flags of the other material.
        public void CopyPropertiesFromMaterial(Material mat)
        {
            if (mat == null) throw new ArgumentNullException(nameof(mat));
            Data.CopyFrom((MaterialNative)mat.NativeOrThrow, includeShader: false);
        }

        /// A content hash: shader, property values, keywords and render queue.
        public int ComputeCRC() => (int)Data.ComputeCrc();

        // ----- Property access -----

        PropertySheet Sheet => Data.Properties;

        public void SetFloat(string name, float value) => SetFloat(Shader.PropertyToID(name), value);
        public void SetFloat(int nameID, float value) { var m = Data; m.Properties.SetFloat(nameID, value); m.Changed(); }

        // SetInt/GetInt operate on float properties in Unity.
        public void SetInt(string name, int value) => SetFloat(Shader.PropertyToID(name), value);
        public void SetInt(int nameID, int value) => SetFloat(nameID, value);
        public int GetInt(string name) => (int)GetFloat(Shader.PropertyToID(name));
        public int GetInt(int nameID) => (int)GetFloat(nameID);

        public void SetInteger(string name, int value) => SetInteger(Shader.PropertyToID(name), value);
        public void SetInteger(int nameID, int value) { var m = Data; m.Properties.SetInt(nameID, value); m.Changed(); }
        public int GetInteger(string name) => GetInteger(Shader.PropertyToID(name));
        public int GetInteger(int nameID) => Sheet.Ints.GetValueOrDefault(nameID);

        public float GetFloat(string name) => GetFloat(Shader.PropertyToID(name));
        public float GetFloat(int nameID) => Sheet.Floats.GetValueOrDefault(nameID);

        public void SetColor(string name, Color value) => SetColor(Shader.PropertyToID(name), value);
        public void SetColor(int nameID, Color value) { var m = Data; m.Properties.SetVector(nameID, value); m.Changed(); }
        public Color GetColor(string name) => GetColor(Shader.PropertyToID(name));
        public Color GetColor(int nameID) => Sheet.Vectors.GetValueOrDefault(nameID);

        public void SetVector(string name, Vector4 value) => SetVector(Shader.PropertyToID(name), value);
        public void SetVector(int nameID, Vector4 value) { var m = Data; m.Properties.SetVector(nameID, value); m.Changed(); }
        public Vector4 GetVector(string name) => GetVector(Shader.PropertyToID(name));
        public Vector4 GetVector(int nameID) => Sheet.Vectors.GetValueOrDefault(nameID);

        public void SetMatrix(string name, Matrix4x4 value) => SetMatrix(Shader.PropertyToID(name), value);
        public void SetMatrix(int nameID, Matrix4x4 value) { var m = Data; m.Properties.SetMatrix(nameID, value); m.Changed(); }

        public void SetTexture(string name, Texture value) => SetTexture(Shader.PropertyToID(name), value);
        public void SetTexture(int nameID, Texture value) { var m = Data; m.Properties.SetTexture(nameID, value); m.Changed(); }
        public Texture GetTexture(string name) => GetTexture(Shader.PropertyToID(name));
        /// Null, with an error logged, for a property the shader does not declare as a texture,
        /// though the material holds a value its draws use (measured: tools/UnityProbe,
        /// texfallback).
        public Texture GetTexture(int nameID)
        {
            var shader = Data.Shader;
            if (shader != null && !(((ShaderNative)shader.NativeOrThrow).PropertiesById.TryGetValue(nameID, out var p) && p.Kind == ShaderPropertyKind.Texture))
            {
                Debug.LogError($"Material '{name}' with Shader '{shader.name}' doesn't have a texture property '{ShaderPropertyIds.NameOf(nameID)}'");
                return null;
            }
            return Sheet.Textures.TryGetValue(nameID, out var t) && t.Texture != null ? t.Texture : null;
        }

        /// The material holds a texture for the property, or its shader declares one.
        public bool HasTexture(int nameID)
            => Sheet.Textures.ContainsKey(nameID)
               || Data.Shader is { } shader && ((ShaderNative)shader.NativeOrThrow).PropertiesById.TryGetValue(nameID, out var p) && p.Kind == ShaderPropertyKind.Texture;

        public bool HasTexture(string name) => HasTexture(Shader.PropertyToID(name));

        /// The material's texture properties (its saved ones and its shader's), assigned or not.
        public string[] GetTexturePropertyNames()
        {
            var names = new List<string>();
            GetTexturePropertyNames(names);
            return names.ToArray();
        }

        public void GetTexturePropertyNames(List<string> outNames)
        {
            outNames.Clear();
            foreach (var id in Sheet.Textures.Keys) outNames.Add(ShaderPropertyIds.NameOf(id));
        }

        public int[] GetTexturePropertyNameIDs()
        {
            var ids = new List<int>();
            GetTexturePropertyNameIDs(ids);
            return ids.ToArray();
        }

        public void GetTexturePropertyNameIDs(List<int> outNames)
        {
            outNames.Clear();
            outNames.AddRange(Sheet.Textures.Keys);
        }

        public Vector2 GetTextureOffset(string name) => GetTextureOffset(Shader.PropertyToID(name));
        public Vector2 GetTextureOffset(int nameID) => Sheet.Textures.TryGetValue(nameID, out var t) ? new Vector2(t.ScaleOffset.z, t.ScaleOffset.w) : Vector2.zero;
        public Vector2 GetTextureScale(string name) => GetTextureScale(Shader.PropertyToID(name));
        public Vector2 GetTextureScale(int nameID) => Sheet.Textures.TryGetValue(nameID, out var t) ? new Vector2(t.ScaleOffset.x, t.ScaleOffset.y) : Vector2.one;

        public void SetTextureOffset(string name, Vector2 value) => SetTextureOffset(Shader.PropertyToID(name), value);
        public void SetTextureOffset(int nameID, Vector2 value)
        {
            var m = Data;
            var st = m.Properties.Textures.TryGetValue(nameID, out var t) ? t.ScaleOffset : TextureBinding.IdentityScaleOffset;
            m.Properties.SetTextureScaleOffset(nameID, new Vector4(st.x, st.y, value.x, value.y));
            m.Changed();
        }

        public void SetTextureScale(string name, Vector2 value) => SetTextureScale(Shader.PropertyToID(name), value);
        public void SetTextureScale(int nameID, Vector2 value)
        {
            var m = Data;
            var st = m.Properties.Textures.TryGetValue(nameID, out var t) ? t.ScaleOffset : TextureBinding.IdentityScaleOffset;
            m.Properties.SetTextureScaleOffset(nameID, new Vector4(value.x, value.y, st.z, st.w));
            m.Changed();
        }

        public void SetFloatArray(string name, float[] values) => SetFloatArray(Shader.PropertyToID(name), values, values?.Length ?? 0);
        public void SetFloatArray(int nameID, float[] values) => SetFloatArray(nameID, values, values?.Length ?? 0);
        public void SetFloatArray(string name, List<float> values) => SetFloatArray(Shader.PropertyToID(name), values?.ToArray(), values?.Count ?? 0);
        public void SetFloatArray(int nameID, List<float> values) => SetFloatArray(nameID, values?.ToArray(), values?.Count ?? 0);

        void SetFloatArray(int nameID, float[] values, int count)
        {
            CheckArray(values, count);
            var m = Data;
            m.Properties.SetFloatArray(nameID, values, count);
            m.Changed();
        }

        public void SetVectorArray(string name, Vector4[] values) => SetVectorArray(Shader.PropertyToID(name), values, values?.Length ?? 0);
        public void SetVectorArray(int nameID, Vector4[] values) => SetVectorArray(nameID, values, values?.Length ?? 0);
        public void SetVectorArray(string name, List<Vector4> values) => SetVectorArray(Shader.PropertyToID(name), values?.ToArray(), values?.Count ?? 0);
        public void SetVectorArray(int nameID, List<Vector4> values) => SetVectorArray(nameID, values?.ToArray(), values?.Count ?? 0);

        void SetVectorArray(int nameID, Vector4[] values, int count)
        {
            CheckArray(values, count);
            var m = Data;
            m.Properties.SetVectorArray(nameID, values, count);
            m.Changed();
        }

        internal static void CheckArray<T>(T[] values, int count)
        {
            if (values == null) throw new ArgumentNullException("values");
            if (values.Length == 0) throw new ArgumentException("Zero-sized array is not allowed.");
            if (values.Length < count) throw new ArgumentException("array has less elements than passed count.");
        }
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;

    internal sealed class MaterialNative : NativeObject
    {
        public Shader Shader;
        public readonly PropertySheet Properties = new();
        public readonly KeywordSet Keywords = new();
        /// The device's program variant per pass and draw keyword list, with the material and
        /// global keyword versions it was selected for.
        public Dictionary<(ShaderPass Pass, string[] DrawKeywords), (int MaterialVersion, int GlobalVersion, int Blob)> Variants;
        /// The device's values resolved from this material per program (GlUniforms).
        public object DeviceCache;
        public int CustomRenderQueue = -1;
        public MaterialGlobalIlluminationFlags GlobalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        public bool EnableInstancing;
        public bool DoubleSidedGI;
        public readonly Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase);
        /// LightMode values of the passes not drawn; compared without case, as pass tags are.
        public readonly HashSet<string> DisabledPasses = new(StringComparer.OrdinalIgnoreCase);

        /// Bumped on every change that affects rendering.
        public int Version;

        public void Changed() => Version++;

        public int RenderQueue
        {
            get
            {
                if (CustomRenderQueue >= 0) return CustomRenderQueue;
                return Shader != null ? ((ShaderNative)Shader.NativeOrThrow).RenderQueue : 2000;
            }
        }

        public void AssignShader(Shader shader)
        {
            Shader = shader;
            if (shader != null) AddShaderDefaults((ShaderNative)shader.NativeOrThrow);
            Changed();
        }

        /// Shader properties the material has no value for get the shader's defaults (Unity
        /// builds a material's property sheet this way when its shader is set or loaded).
        void AddShaderDefaults(ShaderNative shader)
        {
            foreach (var p in shader.Properties)
            {
                switch (p.Kind)
                {
                    case ShaderPropertyKind.Float or ShaderPropertyKind.Range:
                        Properties.Floats.TryAdd(p.Id, p.Default.x);
                        break;
                    // An Integer property's value is the material's integer (m_Ints, SetInteger).
                    case ShaderPropertyKind.Int:
                        Properties.Ints.TryAdd(p.Id, (int)p.Default.x);
                        break;
                    case ShaderPropertyKind.Color or ShaderPropertyKind.Vector:
                        Properties.Vectors.TryAdd(p.Id, p.Default);
                        break;
                    case ShaderPropertyKind.Texture:
                        Properties.Textures.TryAdd(p.Id, new TextureBinding { ScaleOffset = TextureBinding.IdentityScaleOffset });
                        break;
                }
            }
            Properties.Version++;
        }

        public void CopyFrom(MaterialNative other, bool includeShader)
        {
            if (includeShader)
            {
                Shader = other.Shader;
                EnableInstancing = other.EnableInstancing;
                DoubleSidedGI = other.DoubleSidedGI;
                Tags.Clear();
                foreach (var kv in other.Tags) Tags[kv.Key] = kv.Value;
                DisabledPasses.Clear();
                DisabledPasses.UnionWith(other.DisabledPasses);
            }
            Properties.CopyFrom(other.Properties);
            Keywords.Set(other.Keywords.Items);
            CustomRenderQueue = other.CustomRenderQueue;
            GlobalIlluminationFlags = other.GlobalIlluminationFlags;
            if (!includeShader && Shader != null) AddShaderDefaults((ShaderNative)Shader.NativeOrThrow);
            Changed();
        }

        public uint ComputeCrc()
        {
            var crc = new Crc32();
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, Shader != null ? Shader.GetInstanceID() : 0);
            crc.Append(buffer);
            BitConverter.TryWriteBytes(buffer, RenderQueue);
            crc.Append(buffer);
            Properties.Hash(crc);
            foreach (var k in Keywords.Items) crc.Append(System.Text.Encoding.UTF8.GetBytes(k));
            return crc.Current;
        }

        /// Material (class 21).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            // A shader stripped from the build leaves a null reference; Unity substitutes its
            // error shader.
            Shader = resolver.Resolve(f.PPtr("m_Shader")) as Shader ?? ShaderRegistry.Find("Hidden/InternalErrorShader");
            // Unity 2021.2 split the keywords into valid and invalid lists; a material from an
            // earlier version (a mod's bundle) has them space-separated in one string.
            if (f.Contains("m_ValidKeywords"))
                Keywords.Set(f.Array("m_ValidKeywords").Cast<string>().Concat(f.Array("m_InvalidKeywords").Cast<string>()));
            else
                Keywords.Set(f.String("m_ShaderKeywords").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            GlobalIlluminationFlags = (MaterialGlobalIlluminationFlags)f.Get<uint>("m_LightmapFlags");
            EnableInstancing = f.Bool("m_EnableInstancingVariants");
            DoubleSidedGI = f.Bool("m_DoubleSidedGI");
            CustomRenderQueue = f.Int("m_CustomRenderQueue");
            foreach (Fields pair in f.Array("stringTagMap")) Tags[pair.String("first")] = pair.String("second");
            foreach (string pass in f.Array("disabledShaderPasses")) DisabledPasses.Add(pass);

            var saved = f.Struct("m_SavedProperties");
            foreach (Fields pair in saved.Array("m_TexEnvs"))
            {
                var env = pair.Struct("second");
                var scale = env.Struct("m_Scale");
                var offset = env.Struct("m_Offset");
                Properties.Textures[ShaderPropertyIds.Get(pair.String("first"))] = new TextureBinding
                {
                    Texture = resolver.Resolve(env.PPtr("m_Texture")) as Texture,
                    ScaleOffset = new Vector4(scale.Float("x"), scale.Float("y"), offset.Float("x"), offset.Float("y")),
                };
            }
            // Integer properties came with Unity 2021.1.
            if (saved.Contains("m_Ints"))
                foreach (Fields pair in saved.Array("m_Ints")) Properties.Ints[ShaderPropertyIds.Get(pair.String("first"))] = pair.Int("second");
            foreach (Fields pair in saved.Array("m_Floats")) Properties.Floats[ShaderPropertyIds.Get(pair.String("first"))] = pair.Float("second");
            foreach (Fields pair in saved.Array("m_Colors"))
            {
                var c = pair.Struct("second");
                Properties.Vectors[ShaderPropertyIds.Get(pair.String("first"))] = new Vector4(c.Float("r"), c.Float("g"), c.Float("b"), c.Float("a"));
            }
            if (Shader != null) AddShaderDefaults((ShaderNative)Shader.NativeOrThrow);
            Changed();
        }
    }
}
