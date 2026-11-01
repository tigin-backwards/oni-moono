using System.Collections.Generic;
using UnityEngine;

namespace TMPro
{
    // The units of a rich-text measure: pixels (the default), em (relative to the font size in
    // effect) or a percentage (of the font size, or of the width for horizontal placement).
    internal enum MeasureUnit : byte { Pixels, Em, Percent }

    internal readonly struct Measure
    {
        public readonly float Value;
        public readonly MeasureUnit Unit;

        public Measure(float value, MeasureUnit unit)
        {
            Value = value;
            Unit = unit;
        }

        public bool IsZero => Value == 0f;

        // Pixels, with em and percent relative to the given size.
        public float Resolve(float emSize, float percentOf) => Unit switch
        {
            MeasureUnit.Em => Value * emSize,
            MeasureUnit.Percent => Value * 0.01f * percentOf,
            _ => Value,
        };
    }

    // The formatting in effect for a run of characters, as rich text left it. States are
    // immutable: a tag that changes something makes a new state.
    internal sealed class TextState
    {
        // Font size = SizeScale × the text's font size + SizeOffset (tags nest).
        public float SizeScale = 1f;
        public float SizeOffset;
        public FontStyles Style;
        public FontWeight Weight = FontWeight.Regular;
        public Color32 Color;
        public bool ColorFromTag;
        public byte AlphaOverride = 255;
        public bool HasAlphaOverride;
        public Color32 UnderlineColor;
        public Color32 StrikethroughColor;
        public HighlightState Highlight;
        public TMP_ColorGradient Gradient;
        public Measure CharacterSpacing;
        public Measure Monospace;
        public Measure VerticalOffset;
        public Measure Indent;
        public Measure LineIndent;
        public Measure MarginLeft;
        public Measure MarginRight;
        public Measure LineHeight;
        public bool HasLineHeight;
        public Measure Width;
        public bool HasWidth;
        public HorizontalAlignmentOptions Align;
        public int Link = -1;
        public float Rotation;
        public bool NoBreak;
        public TMP_FontAsset Font;
        public Material Material;
        public int MaterialIndex;
        public float Scale = 1f;

        public TextState Clone() => (TextState)MemberwiseClone();
    }

    internal enum ElementKind : byte
    {
        Character,
        Sprite,
        // A zero-width element: horizontal space (<space>), a new pen position (<pos>), an
        // opened indent, which also moves the pen (<indent>), or a page break (<page>).
        Space,
        Position,
        Indent,
        PageBreak,
    }

    // One element of the parsed text: a character or sprite with its source range and state,
    // or a placement instruction.
    internal struct ParsedElement
    {
        public ElementKind Kind;
        public uint Unicode;
        // The character as written (before case conversion).
        public uint SourceUnicode;
        public int SourceIndex;
        public int SourceLength;
        public TextState State;
        public TMP_TextElement Element;
        public TMP_Asset Asset;
        public int MaterialIndex;
        // Font size factor of small caps (lowercase letters drawn as capitals).
        public float CaseScale;
        public bool AlternateTypeface;
        // <sprite tint=1> or the tag's colour.
        public bool SpriteTint;
        public bool HasSpriteColor;
        public Color32 SpriteColor;
        public Measure Amount;
    }

    // A material used by the text, in the order of first use; the first is the text's own.
    internal struct MaterialSlot
    {
        public Material Material;
        public TMP_Asset Asset;
        public bool IsSprite;
        public bool IsFallback;
        public int Count;
    }

    internal sealed class LinkRecord
    {
        public string Id;
        public int IdSourceIndex;
        public int IdLength;
    }

    // The parsed text: elements and the materials they use.
    internal sealed class ParsedText
    {
        public ParsedElement[] Elements = new ParsedElement[16];
        public int Count;
        public readonly List<MaterialSlot> Materials = new();
        public readonly List<LinkRecord> Links = new();
        // The ellipsis character (Element null when the fonts have none).
        public ParsedElement Ellipsis;

        public void Clear()
        {
            System.Array.Clear(Elements, 0, Count);
            Count = 0;
            Ellipsis = default;
            Materials.Clear();
            Links.Clear();
        }

        public ref ParsedElement Add()
        {
            if (Count == Elements.Length) System.Array.Resize(ref Elements, Elements.Length * 2);
            ref var e = ref Elements[Count++];
            e = default;
            e.CaseScale = 1f;
            return ref e;
        }
    }
}
