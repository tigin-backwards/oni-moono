using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OniMoono.Assets.IO;
using OniMoono.Assets.TypeTrees;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Serialization
{
    /// Resolves serialized object references while reading (file-relative PPtrs).
    internal interface IReferenceResolver
    {
        Object Resolve(PPtr pptr);
    }

    /// Binary form of script data (MonoBehaviour / ScriptableObject payloads and the composite
    /// values inside them), as Unity writes it when type trees are stripped:
    ///
    /// - fields in SerializedLayout order, each followed by alignment to 4 bytes;
    /// - strings: int32 byte length, UTF-8 bytes;
    /// - arrays and lists: int32 count, then the elements (no per-element alignment besides the
    ///   elements' own fields and strings);
    /// - object references: PPtr (int32 file id, int64 path id);
    /// - Unity's built-in types in their native layouts (Vector3 = 3 floats, Color32 = 4 bytes,
    ///   AnimationCurve, Gradient, RectOffset …).
    internal static class ScriptData
    {
        // ---- Skipping (validation without constructing anything) ----

        /// Depth of a script object's own fields; each nested composite adds one. A composite
        /// at depth SerializedLayout.MaxDepth or deeper is not serialized at all (Unity's limit for
        /// composition cycles).
        public const int TopLevelDepth = 1;

        /// Diagnostics: when set, SkipFields reports each field's offset and shape.
        internal static System.IO.TextWriter Trace;

        public static void SkipFields(FieldShape[] fields, DataReader r, int depth = TopLevelDepth)
        {
            foreach (var f in fields)
            {
                if (IsBeyondDepth(f.Shape, depth)) continue;
                Trace?.WriteLine($"{new string(' ', depth * 2)}{r.Position,6} {f.Name} : {f.Shape.Kind} {f.Shape.Type?.Name}");
                SkipValue(f.Shape, r, depth);
                r.Align(4);
            }
        }

        /// Whether a field of this shape is omitted at this depth: composites (also as array or
        /// list elements) past the limit carry no data.
        public static bool IsBeyondDepth(ValueShape shape, int depth)
        {
            var s = shape.Kind is ValueKind.Array or ValueKind.List ? shape.Element : shape;
            return s.Kind == ValueKind.Composite && depth >= SerializedLayout.MaxDepth;
        }

        static void SkipValue(ValueShape shape, DataReader r, int depth)
        {
            switch (shape.Kind)
            {
                case ValueKind.Bool: case ValueKind.SByte: case ValueKind.Byte: r.Skip(1); break;
                case ValueKind.Short: case ValueKind.UShort: case ValueKind.Char: r.Skip(2); break;
                case ValueKind.Int: case ValueKind.UInt: case ValueKind.Float: r.Skip(4); break;
                case ValueKind.Long: case ValueKind.ULong: case ValueKind.Double: r.Skip(8); break;
                case ValueKind.Enum: SkipValue(new ValueShape { Kind = shape.EnumUnderlying }, r, depth); break;
                case ValueKind.String: r.Skip(CheckedCount(r)); r.Align(4); break;
                case ValueKind.ObjectReference: r.Skip(12); break;
                case ValueKind.BuiltinStruct: r.Skip(BuiltinSize(shape.Type)); break;
                case ValueKind.BuiltinClass: SkipBuiltinClass(shape.Type, r); break;
                case ValueKind.Composite: SkipFields(SerializedLayout.CompositeFields(shape), r, depth + 1); break;
                case ValueKind.Array:
                case ValueKind.List:
                {
                    int count = CheckedCount(r);
                    for (int i = 0; i < count; i++) SkipValue(shape.Element, r, depth);
                    break;
                }
            }
        }

        static int CheckedCount(DataReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > r.Remaining) throw new InvalidDataException($"Bad count {count} at {r.Position - 4} ({r.Remaining} bytes left).");
            return count;
        }

        /// Native sizes of the built-in value types, from their release type trees.
        static int BuiltinSize(Type t)
        {
            if (t == typeof(Vector2)) return 8;
            if (t == typeof(Vector3)) return 12;
            if (t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Rect)) return 16;
            if (t == typeof(Color32) || t == typeof(LayerMask)) return 4;
            if (t == typeof(Vector2Int)) return 8;
            if (t == typeof(Vector3Int)) return 12;
            if (t == typeof(RectInt)) return 16;
            if (t == typeof(Bounds)) return 24;
            if (t == typeof(Matrix4x4)) return 64;
            throw new InvalidOperationException("No built-in layout for " + t);
        }

        static void SkipBuiltinClass(Type t, DataReader r)
        {
            if (t == typeof(AnimationCurve))
            {
                int keys = CheckedCount(r);
                r.Skip(keys * 28);
                r.Align(4);
                r.Skip(12); // pre/post infinity, rotation order
            }
            else if (t == typeof(Gradient))
            {
                r.Skip(164);
            }
            else if (t == typeof(RectOffset))
            {
                r.Skip(16);
            }
            else if (t == typeof(GUIStyle))
            {
                ReadGUIStyle(r, null);
            }
            else throw new InvalidOperationException("No built-in layout for " + t);
        }

        // ---- Reading ----

        /// Reads the fields of a script object into `target`.
        public static void ReadFields(object target, FieldShape[] fields, DataReader r, IReferenceResolver resolver, int depth = TopLevelDepth)
        {
            foreach (var f in fields)
            {
                if (IsBeyondDepth(f.Shape, depth)) continue; // omitted: the field keeps its constructed value
                f.Field.SetValue(target, ReadValue(f.Shape, r, resolver, depth));
                r.Align(4);
            }
        }

        public static object ReadValue(ValueShape shape, DataReader r, IReferenceResolver resolver, int depth)
        {
            switch (shape.Kind)
            {
                case ValueKind.Bool: return r.ReadBool();
                case ValueKind.SByte: return r.ReadSByte();
                case ValueKind.Byte: return r.ReadByte();
                case ValueKind.Short: return r.ReadInt16();
                case ValueKind.UShort: return r.ReadUInt16();
                case ValueKind.Char: return (char)r.ReadUInt16();
                case ValueKind.Int: return r.ReadInt32();
                case ValueKind.UInt: return r.ReadUInt32();
                case ValueKind.Long: return r.ReadInt64();
                case ValueKind.ULong: return r.ReadUInt64();
                case ValueKind.Float: return r.ReadSingle();
                case ValueKind.Double: return r.ReadDouble();
                case ValueKind.Enum:
                    return Enum.ToObject(shape.Type, ReadValue(new ValueShape { Kind = shape.EnumUnderlying }, r, resolver, depth));
                case ValueKind.String:
                {
                    var s = Encoding.UTF8.GetString(r.ReadSpan(CheckedCount(r)));
                    r.Align(4);
                    return s;
                }
                case ValueKind.ObjectReference:
                {
                    var target = resolver.Resolve(new PPtr(r.ReadInt32(), r.ReadInt64()));
                    // A reference whose target is not of the field's type deserializes as null.
                    return target && shape.Type.IsInstanceOfType(target) ? target : null;
                }
                case ValueKind.BuiltinStruct: return ReadBuiltinStruct(shape.Type, r);
                case ValueKind.BuiltinClass: return ReadBuiltinClass(shape.Type, r, resolver);
                case ValueKind.Composite:
                {
                    var value = Cloner.NewInstance(shape.Type);
                    ReadFields(value, SerializedLayout.CompositeFields(shape), r, resolver, depth + 1);
                    SerializedLayout.AfterDeserialize(value);
                    return value;
                }
                case ValueKind.Array:
                {
                    int count = CheckedCount(r);
                    var array = Array.CreateInstance(shape.Element.Type, count);
                    for (int i = 0; i < count; i++) array.SetValue(ReadValue(shape.Element, r, resolver, depth), i);
                    return array;
                }
                case ValueKind.List:
                {
                    int count = CheckedCount(r);
                    var list = (IList)Activator.CreateInstance(shape.Type);
                    for (int i = 0; i < count; i++) list.Add(ReadValue(shape.Element, r, resolver, depth));
                    return list;
                }
            }
            throw new InvalidOperationException("Unhandled value kind " + shape.Kind);
        }

        // ---- Reading by a file's type tree ----
        //
        // A file that carries type trees (an asset bundle) whose layout of a script differs from
        // the class's (a mod's bundle built against another version of a package, or of the mod)
        // is read as Unity reads it: field by field as the tree lists them, each matched to the
        // class's field by name; fields the class lacks are read and dropped, fields the data lacks
        // keep their constructed values.

        /// Whether data laid out by these nodes reads with these fields as they stand.
        public static bool Matches(FieldShape[] fields, IReadOnlyList<TypeTreeNode> nodes, int depth = TopLevelDepth)
        {
            int i = 0;
            foreach (var f in fields)
            {
                if (IsBeyondDepth(f.Shape, depth)) continue;
                if (i >= nodes.Count || nodes[i].Name != f.Name || !Matches(f.Shape, nodes[i], depth)) return false;
                i++;
            }
            return i == nodes.Count;
        }

        static bool Matches(ValueShape shape, TypeTreeNode node, int depth) => shape.Kind switch
        {
            ValueKind.Composite => Matches(SerializedLayout.CompositeFields(shape), node.Children, depth + 1),
            ValueKind.Array or ValueKind.List => ArrayData(node) is { } data
                && (shape.Element.Kind != ValueKind.Composite || Matches(SerializedLayout.CompositeFields(shape.Element), data.Children, depth + 1)),
            _ => LeafMatches(shape, node),
        };

        /// The element node of an array field (a container wrapping one "Array" node), or null.
        static TypeTreeNode ArrayData(TypeTreeNode node)
        {
            var array = node.IsArray ? node : node.Children.Count == 1 && node.Children[0].IsArray ? node.Children[0] : null;
            return array != null && array.Children.Count == 2 ? array.Children[1] : null;
        }

        /// Whether a leaf node holds the binary form this shape reads (the tree's type names).
        static bool LeafMatches(ValueShape shape, TypeTreeNode node)
        {
            var kind = shape.Kind == ValueKind.Enum ? shape.EnumUnderlying : shape.Kind;
            return kind switch
            {
                // Unity 6 bundles (6000.3) name a script's bool fields UInt8: one byte all the same.
                ValueKind.Bool => node.TypeName is "bool" or "UInt8",
                ValueKind.SByte => node.TypeName is "SInt8",
                ValueKind.Byte => node.TypeName is "UInt8" or "char",
                ValueKind.Short => node.TypeName is "SInt16" or "short",
                ValueKind.UShort or ValueKind.Char => node.TypeName is "UInt16" or "unsigned short",
                ValueKind.Int => node.TypeName is "int" or "SInt32",
                ValueKind.UInt => node.TypeName is "unsigned int" or "UInt32",
                ValueKind.Long => node.TypeName is "SInt64" or "long long",
                ValueKind.ULong => node.TypeName is "UInt64" or "unsigned long long",
                ValueKind.Float => node.TypeName == "float",
                ValueKind.Double => node.TypeName == "double",
                ValueKind.String => node.TypeName == "string",
                ValueKind.ObjectReference => node.TypeName.StartsWith("PPtr<", StringComparison.Ordinal),
                // Built-in values keep their native layouts across versions.
                _ => true,
            };
        }

        /// Reads a script object's fields as these nodes lay them out (see above).
        public static void ReadFieldsByTree(object target, FieldShape[] fields, IReadOnlyList<TypeTreeNode> nodes, DataReader r, IReferenceResolver resolver, int depth = TopLevelDepth)
        {
            foreach (var node in nodes)
            {
                FieldShape field = null;
                foreach (var f in fields)
                    if (f.Name == node.Name) { field = f; break; }
                if (field == null || IsBeyondDepth(field.Shape, depth))
                {
                    TypeTreeReader.ReadValue(node, r);
                    continue;
                }
                if (TryReadByTree(field.Shape, node, r, resolver, depth, out var value)) field.Field.SetValue(target, value);
                if (node.AlignAfter) r.Align(4);
            }
        }

        /// One value as its node lays it out; false (the data read and dropped) when the node holds
        /// a different kind of value than the field.
        static bool TryReadByTree(ValueShape shape, TypeTreeNode node, DataReader r, IReferenceResolver resolver, int depth, out object value)
        {
            switch (shape.Kind)
            {
                case ValueKind.Composite when node.Children.Count > 0 && !LeafLike(node):
                {
                    var composite = Cloner.NewInstance(shape.Type);
                    ReadFieldsByTree(composite, SerializedLayout.CompositeFields(shape), node.Children, r, resolver, depth + 1);
                    SerializedLayout.AfterDeserialize(composite);
                    value = composite;
                    return true;
                }
                case ValueKind.Array or ValueKind.List when ArrayData(node) is { } data:
                {
                    var array = node.IsArray ? node : node.Children[0];
                    int count = CheckedCount(r);
                    var items = new object[count];
                    bool ok = true;
                    for (int i = 0; i < count; i++)
                    {
                        ok &= TryReadByTree(shape.Element, data, r, resolver, depth, out items[i]);
                        if (data.AlignAfter) r.Align(4);
                    }
                    if (array.AlignAfter) r.Align(4);
                    if (!ok) { value = null; return false; }
                    if (shape.Kind == ValueKind.Array)
                    {
                        var typed = Array.CreateInstance(shape.Element.Type, count);
                        for (int i = 0; i < count; i++) typed.SetValue(items[i], i);
                        value = typed;
                    }
                    else
                    {
                        var list = (IList)Activator.CreateInstance(shape.Type);
                        foreach (var item in items) list.Add(item);
                        value = list;
                    }
                    return true;
                }
                default:
                    if (shape.Kind is ValueKind.Composite or ValueKind.Array or ValueKind.List || !LeafMatches(shape, node))
                    {
                        TypeTreeReader.ReadValue(node, r);
                        value = null;
                        return false;
                    }
                    // The same binary form: read as the class reads it, without the alignment
                    // ReadFields adds after a field (the node's own flag says).
                    value = ReadValue(shape, r, resolver, depth);
                    return true;
            }
        }

        /// A node read as a whole value though it has children (strings, references).
        static bool LeafLike(TypeTreeNode node) => node.TypeName == "string" || node.TypeName.StartsWith("PPtr<", StringComparison.Ordinal);

        static Vector3 ReadVector3(DataReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        static object ReadBuiltinStruct(Type t, DataReader r)
        {
            if (t == typeof(Vector2)) return new Vector2(r.ReadSingle(), r.ReadSingle());
            if (t == typeof(Vector3)) return ReadVector3(r);
            if (t == typeof(Vector4)) return new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (t == typeof(Quaternion)) return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (t == typeof(Color)) return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (t == typeof(Color32)) return new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
            if (t == typeof(Rect)) return new Rect(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (t == typeof(LayerMask)) return (LayerMask)(int)r.ReadUInt32();
            if (t == typeof(Vector2Int)) return new Vector2Int(r.ReadInt32(), r.ReadInt32());
            if (t == typeof(Vector3Int)) return new Vector3Int(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (t == typeof(RectInt)) return new RectInt(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (t == typeof(Bounds))
            {
                var center = ReadVector3(r);
                var extent = ReadVector3(r);
                return new Bounds(center, extent * 2f);
            }
            if (t == typeof(Matrix4x4))
            {
                // Native storage is column-major, like the managed field order.
                var m = new Matrix4x4();
                for (int i = 0; i < 16; i++) m[i] = r.ReadSingle();
                return m;
            }
            throw new InvalidOperationException("No built-in layout for " + t);
        }

        static object ReadBuiltinClass(Type t, DataReader r, IReferenceResolver resolver)
        {
            if (t == typeof(AnimationCurve))
            {
                int count = CheckedCount(r);
                var keys = new Keyframe[count];
                for (int i = 0; i < count; i++)
                    keys[i] = Keyframe.FromSerialized(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle());
                r.Align(4);
                var curve = new AnimationCurve(keys)
                {
                    preWrapMode = InfinityToWrapMode(r.ReadInt32()),
                    postWrapMode = InfinityToWrapMode(r.ReadInt32()),
                };
                r.ReadInt32(); // rotation order (rotation curves only)
                return curve;
            }
            if (t == typeof(Gradient))
            {
                var colors = new Color[8];
                for (int i = 0; i < 8; i++) colors[i] = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var ctime = new ushort[8];
                var atime = new ushort[8];
                for (int i = 0; i < 8; i++) ctime[i] = r.ReadUInt16();
                for (int i = 0; i < 8; i++) atime[i] = r.ReadUInt16();
                int mode = r.ReadByte();
                r.ReadSByte(); // color space
                int numColor = r.ReadByte();
                int numAlpha = r.ReadByte();
                var gradient = new Gradient();
                gradient.SetFromSerialized(colors, ctime, atime, mode, numColor, numAlpha);
                return gradient;
            }
            if (t == typeof(RectOffset)) return new RectOffset(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (t == typeof(GUIStyle)) return ReadGUIStyle(r, resolver);
            throw new InvalidOperationException("No built-in layout for " + t);
        }

        /// The native GUIStyle layout: name, the eight states, border/margin/padding/overflow,
        /// font, then the text settings. A null resolver only advances the reader.
        static GUIStyle ReadGUIStyle(DataReader r, IReferenceResolver resolver)
        {
            var s = resolver == null ? null : new GUIStyle();
            var name = Encoding.UTF8.GetString(r.ReadSpan(CheckedCount(r)));
            r.Align(4);
            var states = new GUIStyleState[8];
            for (int i = 0; i < 8; i++)
            {
                var state = new GUIStyleState();
                // A serialized state is its background and text color (scaled backgrounds are
                // runtime-only).
                var background = new PPtr(r.ReadInt32(), r.ReadInt64());
                state.m_TextColor = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (resolver != null) state.m_Background = resolver.Resolve(background) as Texture2D;
                states[i] = state;
            }
            var offsets = new RectOffset[4];
            for (int i = 0; i < 4; i++) offsets[i] = new RectOffset(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            var font = new PPtr(r.ReadInt32(), r.ReadInt64());
            int fontSize = r.ReadInt32();
            int fontStyle = r.ReadInt32();
            int alignment = r.ReadInt32();
            bool wordWrap = r.ReadBool();
            bool richText = r.ReadBool();
            r.Align(4);
            int clipping = r.ReadInt32();
            int imagePosition = r.ReadInt32();
            var contentOffset = new Vector2(r.ReadSingle(), r.ReadSingle());
            float fixedWidth = r.ReadSingle(), fixedHeight = r.ReadSingle();
            bool stretchWidth = r.ReadBool(), stretchHeight = r.ReadBool();
            r.Align(4);
            if (s == null) return null;
            s.m_Name = name;
            (s.m_Normal, s.m_Hover, s.m_Active, s.m_Focused, s.m_OnNormal, s.m_OnHover, s.m_OnActive, s.m_OnFocused)
                = (states[0], states[1], states[2], states[3], states[4], states[5], states[6], states[7]);
            (s.m_Border, s.m_Margin, s.m_Padding, s.m_Overflow) = (offsets[0], offsets[1], offsets[2], offsets[3]);
            s.m_Font = resolver.Resolve(font) as Font;
            s.m_FontSize = fontSize;
            s.m_FontStyle = (FontStyle)fontStyle;
            s.m_Alignment = (TextAnchor)alignment;
            s.m_WordWrap = wordWrap;
            s.m_RichText = richText;
            s.m_TextClipping = (TextClipping)clipping;
            s.m_ImagePosition = (ImagePosition)imagePosition;
            s.m_ContentOffset = contentOffset;
            s.m_FixedWidth = fixedWidth;
            s.m_FixedHeight = fixedHeight;
            s.m_StretchWidth = stretchWidth;
            s.m_StretchHeight = stretchHeight;
            return s;
        }

        /// Unity stores curve wrap modes as its internal infinity enum: 0 PingPong, 1 Loop,
        /// 2 ClampForever (the editor's "Clamp").
        static WrapMode InfinityToWrapMode(int infinity) => infinity switch
        {
            0 => WrapMode.PingPong,
            1 => WrapMode.Loop,
            _ => WrapMode.ClampForever,
        };
    }
}
