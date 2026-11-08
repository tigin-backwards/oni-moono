using System;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using OniMoono.Engine.Text;

namespace UnityEngine
{
    public partial class TextMesh
    {
        TextMeshNative Data => (TextMeshNative)NativeOrThrow;

        internal override void EngineOnDestroy() => Data.ReleaseResources();

        public string text
        {
            get => Data.Text;
            set => Data.Text = value ?? "";
        }

        public Font font
        {
            get => Data.Font;
            set => Data.Font = value;
        }

        /// 0 uses the font's own size.
        public int fontSize
        {
            get => Data.FontSize;
            set => Data.FontSize = value;
        }

        public FontStyle fontStyle
        {
            get => Data.FontStyle;
            set => Data.FontStyle = value;
        }

        public float offsetZ
        {
            get => Data.OffsetZ;
            set => Data.OffsetZ = value;
        }

        public TextAnchor anchor
        {
            get => Data.Anchor;
            set => Data.Anchor = value;
        }

        /// The serialized m_Alignment: how lines sit within the block (Geometry).
        public TextAlignment alignment
        {
            get => (TextAlignment)Data.Alignment;
            set => Data.Alignment = (int)value;
        }

        public float characterSize
        {
            get => Data.CharacterSize;
            set => Data.CharacterSize = value;
        }

        public float lineSpacing
        {
            get => Data.LineSpacing;
            set => Data.LineSpacing = value;
        }

        public float tabSize
        {
            get => Data.TabSize;
            set => Data.TabSize = value;
        }

        public bool richText
        {
            get => Data.RichText;
            set => Data.RichText = value;
        }

        public Color color
        {
            get => Data.Color;
            set => Data.Color = value;
        }
    }
}

namespace OniMoono.Engine.Text
{
    using UnityEngine;

    /// Engine side of TextMesh (native class 102). Defaults are those of a TextMesh added at
    /// runtime.
    internal sealed class TextMeshNative : ComponentNative
    {
        public string Text = "Hello World";
        public float OffsetZ;
        public float CharacterSize = 1f;
        public float LineSpacing = 1f;
        public TextAnchor Anchor = TextAnchor.UpperLeft;
        /// TextAlignment (Left, Center, Right) of lines within the block.
        public int Alignment;
        public float TabSize = 4f;
        public int FontSize;
        public FontStyle FontStyle;
        public bool RichText = true;
        public Font Font;
        public Color Color = Color.white;

        public override void CopyStateFrom(NativeObject source)
        {
            var t = (TextMeshNative)source;
            Text = t.Text;
            OffsetZ = t.OffsetZ;
            CharacterSize = t.CharacterSize;
            LineSpacing = t.LineSpacing;
            Anchor = t.Anchor;
            Alignment = t.Alignment;
            TabSize = t.TabSize;
            FontSize = t.FontSize;
            FontStyle = t.FontStyle;
            RichText = t.RichText;
            Font = t.Font;
            Color = t.Color;
        }

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Text = f.String("m_Text");
            OffsetZ = f.Float("m_OffsetZ");
            CharacterSize = f.Float("m_CharacterSize");
            LineSpacing = f.Float("m_LineSpacing");
            Anchor = (TextAnchor)f.Get<short>("m_Anchor");
            Alignment = f.Get<short>("m_Alignment");
            TabSize = f.Float("m_TabSize");
            FontSize = f.Int("m_FontSize");
            FontStyle = (FontStyle)f.Int("m_FontStyle");
            RichText = f.Bool("m_RichText");
            Font = resolver.Resolve(f.PPtr("m_Font")) as Font;
            // ColorRGBA: packed bytes, red in the lowest.
            uint rgba = f.Struct("m_Color").Get<uint>("rgba");
            Color = new Color32((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));
        }

        /// World units per font pixel at characterSize 1.
        const float UnitsPerPixel = 0.1f;

        readonly MeshNative mesh = new() { Dynamic = true };
        readonly TextGenerator generator = new();
        bool built;
        string builtText;
        TextGenerationSettings builtSettings;
        float builtTabSize, builtCharacterSize, builtOffsetZ;
        TextAnchor builtAnchor;
        int builtAtlasVersion;

        /// The mesh the MeshRenderer draws: the text laid out by the legacy text generator
        /// (lines aligned by Alignment, the block's Anchor point at the origin), scaled by
        /// CharacterSize, at OffsetZ. Regenerated when the text, its settings or the font
        /// texture change.
        public MeshNative Geometry()
        {
            if (Font == null)
            {
                if (built || mesh.VertexCount != 0) Reset();
                return mesh;
            }
            var settings = new TextGenerationSettings
            {
                font = Font,
                color = Color,
                fontSize = FontSize,
                fontStyle = FontStyle,
                lineSpacing = LineSpacing,
                richText = RichText,
                scaleFactor = 1f,
                textAnchor = (TextAnchor)((int)Anchor / 3 * 3 + Math.Clamp(Alignment, 0, 2)),
                horizontalOverflow = HorizontalWrapMode.Overflow,
                verticalOverflow = VerticalWrapMode.Overflow,
                generationExtents = Vector2.zero,
                pivot = new Vector2(0.5f, 0.5f),
                generateOutOfBounds = true,
                updateBounds = true,
            };
            var font = (FontNative)Font.NativeOrThrow;
            if (built && builtText == Text && settings.Equals(builtSettings) && builtTabSize == TabSize && builtCharacterSize == CharacterSize
                && builtOffsetZ == OffsetZ && builtAnchor == Anchor && builtAtlasVersion == font.AtlasVersion)
                return mesh;

            generator.TabSize = TabSize;
            generator.Invalidate();
            generator.Populate(Text, settings);
            // The generator places lines in a zero-size rect; move the block so its anchor point
            // (horizontal from Anchor, which may differ from the line alignment) is the origin.
            // rectExtents holds the block's size (its x and y read 0); the generator centred the
            // block on the origin by the pivot (0.5, 0.5).
            var extents = generator.rectExtents;
            int anchor = (int)Anchor;
            float ax = -0.5f * extents.width + anchor % 3 * 0.5f * extents.width;
            float ay = 0.5f * extents.height - anchor / 3 * 0.5f * extents.height;
            float scale = CharacterSize * UnitsPerPixel;

            var verts = generator.verts;
            int quads = verts.Count / 4;
            var positions = new Vector3[quads * 4];
            var colors = new Color[positions.Length];
            var uvs = new Vector4[positions.Length];
            var indices = new int[quads * 6];
            for (int i = 0; i < positions.Length; i++)
            {
                var v = verts[i];
                positions[i] = new Vector3((v.position.x - ax) * scale, (v.position.y - ay) * scale, OffsetZ);
                colors[i] = v.color;
                uvs[i] = new Vector4(v.uv0.x, v.uv0.y, 0f, 0f);
            }
            for (int q = 0; q < quads; q++)
            {
                int b = q * 4;
                indices[q * 6] = b;
                indices[q * 6 + 1] = b + 1;
                indices[q * 6 + 2] = b + 2;
                indices[q * 6 + 3] = b + 2;
                indices[q * 6 + 4] = b + 3;
                indices[q * 6 + 5] = b;
            }
            mesh.Clear();
            mesh.VertexCount = positions.Length;
            mesh.Positions = positions;
            mesh.Colors = colors;
            mesh.ColorsAreBytes = true;
            mesh.Uvs[0] = uvs;
            mesh.UvDimensions[0] = 2;
            mesh.SubMeshes[0].SetIndices(indices);
            mesh.RecalculateBounds();
            mesh.Changed();

            built = true;
            builtText = Text;
            builtSettings = settings;
            builtTabSize = TabSize;
            builtCharacterSize = CharacterSize;
            builtOffsetZ = OffsetZ;
            builtAnchor = Anchor;
            // Populate may have rebuilt the atlas itself.
            builtAtlasVersion = font.AtlasVersion;
            return mesh;
        }

        void Reset()
        {
            mesh.Clear();
            mesh.Changed();
            built = false;
        }

        public void ReleaseResources() => mesh.ReleaseResources();
    }
}
