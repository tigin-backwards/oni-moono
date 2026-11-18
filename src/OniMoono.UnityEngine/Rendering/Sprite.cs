using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine.U2D;

namespace UnityEngine
{
    /// Sprite.packingMode; the game's assemblies do not use it, so SurfaceGen does not emit it.
    public enum SpritePackingMode
    {
        Tight = 0,
        Rectangle = 1,
    }

    /// Sprite.packingRotation; emitted by hand for the same reason.
    public enum SpritePackingRotation
    {
        None = 0,
        FlipHorizontal = 1,
        FlipVertical = 2,
        Rotate180 = 3,
        Any = 15,
    }

    public partial class Sprite
    {
        SpriteNative Data => (SpriteNative)NativeOrThrow;

        public Rect rect => Data.Rect;

        public Vector4 border => Data.Border;

        public float pixelsPerUnit => Data.PixelsPerUnit;

        internal uint extrude => Data.Extrude;

        /// The texture the sprite renders from: its atlas page when packed.
        public Texture2D texture => Data.Render.Texture;

        public Texture2D associatedAlphaSplitTexture => Data.Render.AlphaTexture;

        /// Pivot in pixels from the rect's bottom-left corner.
        public Vector2 pivot
        {
            get
            {
                var d = Data;
                return Vector2.Scale(d.Pivot, d.Rect.size);
            }
        }

        public bool packed => (Data.Render.Settings & 1) != 0;

        /// How the sprite was packed: tight or rectangle (mods read it; the game does not).
        public SpritePackingMode packingMode => (SpritePackingMode)((Data.Render.Settings >> 1) & 1);

        /// How the sprite's pixels were flipped or rotated in its texture when packed.
        public SpritePackingRotation packingRotation => (SpritePackingRotation)((Data.Render.Settings >> 2) & 0xF);

        /// Bounds of the sprite's geometry in units around the pivot.
        public Bounds bounds => Data.Bounds;

        /// Location of the sprite's pixels in its texture.
        public Rect textureRect
        {
            get
            {
                var r = Data.Render;
                if ((r.Settings & 1) != 0 && ((r.Settings >> 1) & 1) == 0)
                    throw new UnityException("Could not access textureRect since the sprite is packed in tight mode.");
                return r.TextureRect;
            }
        }

        public Vector2 textureRectOffset => Data.Render.TextureRectOffset;

        /// Geometry in units relative to the pivot.
        public Vector2[] vertices => (Vector2[])Data.Vertices.Clone();

        public ushort[] triangles => (ushort[])Data.Triangles.Clone();

        public Vector2[] uv => Data.ComputeUvs();

        public int GetSecondaryTextureCount() => Data.Render.SecondaryTextures.Length;

        public int GetSecondaryTextures(SecondarySpriteTexture[] secondaryTexture)
        {
            if (secondaryTexture == null) throw new ArgumentNullException(nameof(secondaryTexture));
            var source = Data.Render.SecondaryTextures;
            int count = Math.Min(source.Length, secondaryTexture.Length);
            Array.Copy(source, secondaryTexture, count);
            return count;
        }

        internal Vector4 GetOuterUVs() => Data.OuterUvs();
        internal Vector4 GetInnerUVs() => Data.InnerUvs();
        internal Vector4 GetPadding() => Data.Padding();

        internal override Object EngineCloneAsset()
        {
            var clone = new SpriteNative();
            clone.CopyFrom(Data);
            return ObjectLoader.CreateNative<Sprite>(typeof(Sprite), clone);
        }

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot) => Create(texture, rect, pivot, 100f);

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit) => Create(texture, rect, pivot, pixelsPerUnit, 0u);

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, uint extrude)
            => Create(texture, rect, pivot, pixelsPerUnit, extrude, SpriteMeshType.Tight);

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, uint extrude, SpriteMeshType meshType)
            => Create(texture, rect, pivot, pixelsPerUnit, extrude, meshType, Vector4.zero);

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, uint extrude, SpriteMeshType meshType, Vector4 border)
            => Create(texture, rect, pivot, pixelsPerUnit, extrude, meshType, border, false);

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, uint extrude, SpriteMeshType meshType, Vector4 border, bool generateFallbackPhysicsShape)
            => Create(texture, rect, pivot, pixelsPerUnit, extrude, meshType, border, generateFallbackPhysicsShape, null);

        /// A sprite over `rect` of the texture, pivot normalized. The geometry is the full rect
        /// for every mesh type (deviation D6: Unity traces alpha outlines for Tight).
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, uint extrude, SpriteMeshType meshType, Vector4 border, bool generateFallbackPhysicsShape, SecondarySpriteTexture[] secondaryTextures)
        {
            if (texture == null) return null;
            if (rect.xMax > texture.width || rect.yMax > texture.height)
                throw new ArgumentException($"Could not create sprite ({rect.x}, {rect.y}, {rect.width}, {rect.height}) from a {texture.width}x{texture.height} texture.");
            if (pixelsPerUnit <= 0f) throw new ArgumentException("pixelsPerUnit must be set to a positive non-zero value.");
            if (secondaryTextures != null)
                foreach (var s in secondaryTextures)
                    if (s.texture == texture) throw new ArgumentException($"{s.name} is using source Texture as Secondary Texture.");
            var native = SpriteNative.CreateFullRect(texture, rect, pivot, pixelsPerUnit, extrude, meshType, border, secondaryTextures);
            return ObjectLoader.CreateNative<Sprite>(typeof(Sprite), native);
        }
    }
}

namespace UnityEngine.Sprites
{
    public partial class DataUtility
    {
        public static Vector4 GetInnerUV(Sprite sprite) => sprite.GetInnerUVs();
        public static Vector4 GetOuterUV(Sprite sprite) => sprite.GetOuterUVs();
        public static Vector4 GetPadding(Sprite sprite) => sprite.GetPadding();

        public static Vector2 GetMinSize(Sprite sprite)
        {
            var b = sprite.border;
            return new Vector2(b.x + b.z, b.y + b.w);
        }
    }
}

namespace UnityEngine.U2D
{
    public partial class SpriteAtlas
    {
        SpriteAtlasNative Data => (SpriteAtlasNative)NativeOrThrow;

        public int spriteCount => Data.PackedSprites.Length;

        public string tag => Data.Tag;

        public bool isVariant => Data.IsVariant;

        /// Whether the sprite was packed into this atlas.
        public bool CanBindTo(Sprite sprite)
        {
            if (sprite == null) return false;
            return Data.RenderData.ContainsKey(((SpriteNative)sprite.NativeOrThrow).RenderDataKey);
        }

        /// Fills the array with copies of the packed sprites; returns how many were written.
        public int GetSprites(Sprite[] sprites)
        {
            if (sprites == null) throw new ArgumentNullException(nameof(sprites));
            var d = Data;
            int count = Math.Min(sprites.Length, d.PackedSprites.Length);
            for (int i = 0; i < count; i++) sprites[i] = d.CloneSprite(i);
            return count;
        }

        public Sprite GetSprite(string name)
        {
            var d = Data;
            int index = Array.IndexOf(d.PackedSpriteNames, name);
            return index < 0 ? null : d.CloneSprite(index);
        }
    }

    public partial class SpriteAtlasManager
    {
        /// Raised when an atlas is registered (loaded) with the engine.
        public static event Action<SpriteAtlas> atlasRegistered;

        internal static void Register(SpriteAtlas atlas)
        {
            try
            {
                atlasRegistered?.Invoke(atlas);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}

namespace OniMoono.Engine.Rendering
{
    using UnityEngine;
    using UnityEngine.U2D;

    /// Where a sprite's pixels live and how its vertices map to them.
    internal sealed class SpriteRenderData
    {
        public Texture2D Texture;
        public Texture2D AlphaTexture;
        public SecondarySpriteTexture[] SecondaryTextures = Array.Empty<SecondarySpriteTexture>();
        public Rect TextureRect;
        public Vector2 TextureRectOffset;
        public Vector2 AtlasRectOffset;
        /// SpriteSettings: packed (bit 0), packingMode (bit 1), packingRotation (bits 2-5),
        /// meshType (bit 6).
        public uint Settings;
        /// Pixel of a vertex = vertex · (x, z) + (y, w).
        public Vector4 UvTransform;
        public float DownscaleMultiplier = 1f;

        public static SpriteRenderData Read(Fields f, IReferenceResolver resolver) => new()
        {
            Texture = resolver.Resolve(f.PPtr("texture")) as Texture2D,
            AlphaTexture = resolver.Resolve(f.PPtr("alphaTexture")) as Texture2D,
            SecondaryTextures = f.Array("secondaryTextures").Cast<Fields>()
                .Select(s => new SecondarySpriteTexture { name = s.String("name"), texture = resolver.Resolve(s.PPtr("texture")) as Texture2D }).ToArray(),
            TextureRect = ReadRect(f.Struct("textureRect")),
            TextureRectOffset = ReadVector2(f.Struct("textureRectOffset")),
            AtlasRectOffset = ReadVector2(f.Struct("atlasRectOffset")),
            Settings = f.Get<uint>("settingsRaw"),
            UvTransform = ReadVector4(f.Struct("uvTransform")),
            DownscaleMultiplier = f.Float("downscaleMultiplier"),
        };

        internal static Rect ReadRect(Fields r) => new(r.Float("x"), r.Float("y"), r.Float("width"), r.Float("height"));
        internal static Vector2 ReadVector2(Fields v) => new(v.Float("x"), v.Float("y"));
        internal static Vector4 ReadVector4(Fields v) => new(v.Float("x"), v.Float("y"), v.Float("z"), v.Float("w"));
    }

    /// SpriteAtlas render-data key: the sprite's GUID and file id.
    internal readonly record struct SpriteKey(uint A, uint B, uint C, uint D, long Id)
    {
        public static SpriteKey Read(Fields pair)
        {
            var guid = pair.Struct("first");
            return new SpriteKey(guid.Get<uint>("data[0]"), guid.Get<uint>("data[1]"), guid.Get<uint>("data[2]"), guid.Get<uint>("data[3]"), pair.Get<long>("second"));
        }
    }

    internal sealed class SpriteNative : NativeObject
    {
        public Rect Rect;
        public Vector2 Offset;
        public Vector4 Border;
        public float PixelsPerUnit = 100f;
        /// Normalized pivot.
        public Vector2 Pivot;
        public uint Extrude;
        public SpriteKey RenderDataKey;
        public SpriteAtlas Atlas;
        public SpriteRenderData Own = new();
        public Vector2[] Vertices = Array.Empty<Vector2>();
        public ushort[] Triangles = Array.Empty<ushort>();
        public Bounds Bounds;

        /// The atlas page's render data when the sprite is packed into a loaded atlas.
        public SpriteRenderData Render
        {
            get
            {
                if (Atlas && Atlas.Native is SpriteAtlasNative atlas && atlas.RenderData.TryGetValue(RenderDataKey, out var packed)) return packed;
                return Own;
            }
        }

        public Vector2[] ComputeUvs()
        {
            var r = Render;
            var t = r.Texture;
            var uvs = new Vector2[Vertices.Length];
            if (!t) return uvs;
            float w = t.width, h = t.height;
            for (int i = 0; i < uvs.Length; i++)
            {
                var v = Vertices[i];
                uvs[i] = new Vector2((v.x * r.UvTransform.x + r.UvTransform.y) / w, (v.y * r.UvTransform.z + r.UvTransform.w) / h);
            }
            return uvs;
        }

        /// textureRect in normalized texture coordinates (xMin, yMin, xMax, yMax).
        public Vector4 OuterUvs()
        {
            var r = Render;
            if (!r.Texture) return Vector4.zero;
            float w = r.Texture.width, h = r.Texture.height;
            var tr = r.TextureRect;
            return new Vector4(tr.xMin / w, tr.yMin / h, tr.xMax / w, tr.yMax / h);
        }

        /// The border-inset rect in normalized texture coordinates. Borders are measured from
        /// the sprite rect; trimmed pixels (padding) count towards them.
        public Vector4 InnerUvs()
        {
            var r = Render;
            if (!r.Texture) return Vector4.zero;
            float w = r.Texture.width, h = r.Texture.height;
            var tr = r.TextureRect;
            var pad = Padding();
            float s = r.DownscaleMultiplier;
            float xMin = Mathf.Clamp(tr.xMin + (Border.x - pad.x) * s, tr.xMin, tr.xMax);
            float yMin = Mathf.Clamp(tr.yMin + (Border.y - pad.y) * s, tr.yMin, tr.yMax);
            float xMax = Mathf.Clamp(tr.xMax - (Border.z - pad.z) * s, tr.xMin, tr.xMax);
            float yMax = Mathf.Clamp(tr.yMax - (Border.w - pad.w) * s, tr.yMin, tr.yMax);
            return new Vector4(xMin / w, yMin / h, xMax / w, yMax / h);
        }

        /// Pixels trimmed from each side of the rect (left, bottom, right, top).
        public Vector4 Padding()
        {
            var r = Render;
            float s = r.DownscaleMultiplier == 0f ? 1f : r.DownscaleMultiplier;
            var tr = r.TextureRect;
            var off = r.TextureRectOffset;
            return new Vector4(off.x, off.y, Rect.width - tr.width / s - off.x, Rect.height - tr.height / s - off.y);
        }

        public void CopyFrom(SpriteNative o)
        {
            Name = o.Name;
            Rect = o.Rect;
            Offset = o.Offset;
            Border = o.Border;
            PixelsPerUnit = o.PixelsPerUnit;
            Pivot = o.Pivot;
            Extrude = o.Extrude;
            RenderDataKey = o.RenderDataKey;
            Atlas = o.Atlas;
            Own = o.Own;
            Vertices = (Vector2[])o.Vertices.Clone();
            Triangles = (ushort[])o.Triangles.Clone();
            Bounds = o.Bounds;
        }

        /// Sprite (class 213).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Rect = SpriteRenderData.ReadRect(f.Struct("m_Rect"));
            Offset = SpriteRenderData.ReadVector2(f.Struct("m_Offset"));
            Border = SpriteRenderData.ReadVector4(f.Struct("m_Border"));
            PixelsPerUnit = f.Float("m_PixelsToUnits");
            Pivot = SpriteRenderData.ReadVector2(f.Struct("m_Pivot"));
            Extrude = f.Get<uint>("m_Extrude");
            RenderDataKey = SpriteKey.Read(f.Struct("m_RenderDataKey"));
            var rd = f.Struct("m_RD");
            Own = SpriteRenderData.Read(rd, resolver);
            if (((Own.Settings >> 2) & 0xF) != 0) throw new System.IO.InvalidDataException($"Sprite {Name}: packing rotation is not supported.");
            var vertices = VertexData.Decode(rd.Struct("m_VertexData"));
            Vertices = vertices.Positions?.Select(p => (Vector2)p).ToArray() ?? Array.Empty<Vector2>();
            var indexBytes = rd.Get<byte[]>("m_IndexBuffer");
            Triangles = new ushort[indexBytes.Length / 2];
            for (int i = 0; i < Triangles.Length; i++) Triangles[i] = BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(i * 2));
            Bounds = BoundsOf(Vertices);
            Atlas = resolver.Resolve(f.PPtr("m_SpriteAtlas")) as SpriteAtlas;
        }

        static Bounds BoundsOf(Vector2[] vertices)
        {
            if (vertices.Length == 0) return default;
            Vector2 min = vertices[0], max = vertices[0];
            foreach (var v in vertices)
            {
                min = Vector2.Min(min, v);
                max = Vector2.Max(max, v);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        public static SpriteNative CreateFullRect(Texture2D texture, Rect rect, Vector2 pivot, float ppu, uint extrude, SpriteMeshType meshType, Vector4 border, SecondarySpriteTexture[] secondary)
        {
            var px = Vector2.Scale(pivot, rect.size);
            float left = -px.x / ppu, right = (rect.width - px.x) / ppu, bottom = -px.y / ppu, top = (rect.height - px.y) / ppu;
            var native = new SpriteNative
            {
                Name = "",
                Rect = rect,
                Border = border,
                PixelsPerUnit = ppu,
                Pivot = pivot,
                Extrude = extrude,
                Own = new SpriteRenderData
                {
                    Texture = texture,
                    SecondaryTextures = secondary != null ? (SecondarySpriteTexture[])secondary.Clone() : Array.Empty<SecondarySpriteTexture>(),
                    TextureRect = rect,
                    Settings = (uint)meshType << 6,
                    UvTransform = new Vector4(ppu, rect.x + px.x, ppu, rect.y + px.y),
                },
                // Unity's full-rect order: top-left, top-right, bottom-left, bottom-right.
                Vertices = [new(left, top), new(right, top), new(left, bottom), new(right, bottom)],
                Triangles = [0, 1, 2, 2, 1, 3],
            };
            native.Bounds = BoundsOf(native.Vertices);
            return native;
        }
    }

    internal sealed class SpriteAtlasNative : NativeObject
    {
        public PPtrRef[] PackedSprites = Array.Empty<PPtrRef>();
        public string[] PackedSpriteNames = Array.Empty<string>();
        public Dictionary<SpriteKey, SpriteRenderData> RenderData = new();
        public string Tag = "";
        public bool IsVariant;

        /// A packed sprite reference, resolved on demand.
        internal readonly record struct PPtrRef(IReferenceResolver Resolver, OniMoono.Assets.TypeTrees.PPtr Pointer);

        public Sprite CloneSprite(int index)
        {
            var p = PackedSprites[index];
            var sprite = p.Resolver.Resolve(p.Pointer) as Sprite;
            if (!sprite) return null;
            var clone = (Sprite)Object.Instantiate(sprite);
            return clone;
        }

        /// SpriteAtlas (class 687078895).
        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            PackedSprites = f.Array("m_PackedSprites").Cast<OniMoono.Assets.TypeTrees.PPtr>().Select(p => new PPtrRef(resolver, p)).ToArray();
            PackedSpriteNames = f.Array("m_PackedSpriteNamesToIndex").Cast<string>().ToArray();
            foreach (Fields pair in f.Array("m_RenderDataMap"))
                RenderData[SpriteKey.Read(pair.Struct("first"))] = SpriteRenderData.Read(pair.Struct("second"), resolver);
            Tag = f.String("m_Tag");
            IsVariant = f.Bool("m_IsVariant");
            var atlas = (SpriteAtlas)Managed;
            ObjectLoader.AfterLoad(() => SpriteAtlasManager.Register(atlas));
        }
    }
}
