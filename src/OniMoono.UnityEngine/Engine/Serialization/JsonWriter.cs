using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Serialization
{
    /// JsonUtility.ToJson: Unity's serializer driven by SerializedLayout, written the way Unity's
    /// RapidJSON-based writer does.
    ///
    /// - Fields in serialization order; a null string is "", a null array/list is [], a null
    ///   serializable class or built-in class is written as a default-constructed instance.
    /// - Object references are {"instanceID":N} (0 for null or destroyed).
    /// - Enums and chars are numbers; bools are true/false.
    /// - Floats are widened to double; doubles get RapidJSON's Grisu2 digits (observed in Unity's
    ///   output: 0.2f is written 0.20000000298023225), laid out by RapidJSON's Prettify: integral
    ///   values get ".0", decimal notation for exponents -6 &lt; e &lt;= 21, otherwise "1.5e-7";
    ///   zero (either sign) is "0.0". Unity's RapidJSON (1.1.0) reads past its power-of-ten table
    ///   when rounding the last digits of long outputs, so its last digit can exceed ours there
    ///   (ARCHITECTURE D10); the values round-trip identically.
    /// - Built-in types are written with their native transfer names, including
    ///   "serializedVersion" (as a string) for versioned types: Rect, LayerMask, Color32,
    ///   AnimationCurve and its keys, Gradient.
    /// - Pretty printing is RapidJSON's PrettyWriter with four-space indentation.
    internal sealed class JsonWriter
    {
        readonly StringBuilder sb = new();
        readonly bool pretty;
        int indent;
        // Per open container: whether a value has been written in it yet.
        readonly System.Collections.Generic.Stack<bool> hasItems = new();

        JsonWriter(bool pretty) => this.pretty = pretty;

        public static string Write(object obj, bool prettyPrint)
        {
            var w = new JsonWriter(prettyPrint);
            var type = obj.GetType();
            if (obj is Object)
            {
                SerializedLayout.BeforeSerialize(obj);
                w.WriteFields(obj, SerializedLayout.ScriptFields(type), ScriptData.TopLevelDepth);
            }
            else
            {
                var shape = SerializedLayout.ShapeOf(type);
                switch (shape?.Kind)
                {
                    case ValueKind.BuiltinStruct:
                    case ValueKind.BuiltinClass:
                        w.WriteValue(obj, shape, ScriptData.TopLevelDepth);
                        break;
                    case null:
                    case ValueKind.Composite:
                        // The root needs no [Serializable]; primitives, strings and collections
                        // have no fields of their own and come out as an empty object.
                        bool hasFields = !type.IsPrimitive && !type.IsEnum && type != typeof(string) && !type.IsArray
                                         && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>));
                        var fields = hasFields ? SerializedLayout.CompositeFields(new ValueShape { Type = type, Kind = ValueKind.Composite, IsValueType = type.IsValueType }) : Array.Empty<FieldShape>();
                        SerializedLayout.BeforeSerialize(obj);
                        w.WriteFields(obj, fields, ScriptData.TopLevelDepth);
                        break;
                    default:
                        w.WriteFields(obj, Array.Empty<FieldShape>(), ScriptData.TopLevelDepth);
                        break;
                }
            }
            return w.sb.ToString();
        }

        // ---- Structure ----

        void StartObject() { BeforeValue(); sb.Append('{'); Open(); }
        void EndObject() => Close('}');
        void StartArray() { BeforeValue(); sb.Append('['); Open(); }
        void EndArray() => Close(']');

        void Open()
        {
            hasItems.Push(false);
            indent++;
        }

        void Close(char c)
        {
            indent--;
            bool any = hasItems.Pop();
            if (pretty && any) NewLine();
            sb.Append(c);
        }

        void NewLine()
        {
            sb.Append('\n');
            sb.Append(' ', indent * 4);
        }

        /// Separator and indentation before an array element or an object member's name.
        void BeforeItem()
        {
            if (hasItems.Count == 0) return;
            if (hasItems.Peek()) sb.Append(',');
            hasItems.Pop();
            hasItems.Push(true);
            if (pretty) NewLine();
        }

        bool afterName;

        void BeforeValue()
        {
            if (afterName)
            {
                afterName = false;
                return;
            }
            BeforeItem();
        }

        void Name(string name)
        {
            BeforeItem();
            WriteString(name);
            sb.Append(pretty ? ": " : ":");
            afterName = true;
        }

        // ---- Values ----

        void WriteFields(object target, FieldShape[] fields, int depth)
        {
            StartObject();
            foreach (var f in fields)
            {
                if (ScriptData.IsBeyondDepth(f.Shape, depth) || !JsonLayout.Known(f.Shape)) continue;
                Name(f.Name);
                WriteValue(f.Field.GetValue(target), f.Shape, depth);
            }
            EndObject();
        }

        void WriteValue(object value, ValueShape shape, int depth)
        {
            switch (shape.Kind)
            {
                case ValueKind.Bool: Raw((bool)value ? "true" : "false"); break;
                case ValueKind.SByte: Raw(((sbyte)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Byte: Raw(((byte)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Short: Raw(((short)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.UShort: Raw(((ushort)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Char: Raw(((int)(char)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Int: Raw(((int)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.UInt: Raw(((uint)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Long: Raw(((long)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.ULong: Raw(((ulong)value).ToString(CultureInfo.InvariantCulture)); break;
                case ValueKind.Float: Raw(FormatFloat((float)value)); break;
                case ValueKind.Double: Raw(FormatDouble((double)value)); break;
                case ValueKind.Enum:
                    Raw(shape.EnumUnderlying == ValueKind.ULong
                        ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                        : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    break;
                case ValueKind.String:
                    BeforeValue();
                    WriteString((string)value ?? "");
                    break;
                case ValueKind.ObjectReference:
                    StartObject();
                    Name("instanceID");
                    Raw(value is Object o && o ? o.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "0");
                    EndObject();
                    break;
                case ValueKind.BuiltinStruct: WriteBuiltinStruct(value); break;
                case ValueKind.BuiltinClass: WriteBuiltinClass(value ?? Activator.CreateInstance(shape.Type)); break;
                case ValueKind.Composite:
                    var composite = value ?? Cloner.NewInstance(shape.Type);
                    SerializedLayout.BeforeSerialize(composite);
                    WriteFields(composite, SerializedLayout.CompositeFields(shape), depth + 1);
                    break;
                case ValueKind.Array:
                case ValueKind.List:
                    StartArray();
                    if (value is IList list)
                        foreach (var item in list) WriteValue(item, shape.Element, depth);
                    EndArray();
                    break;
                default:
                    throw new InvalidOperationException("Unhandled value kind " + shape.Kind);
            }
        }

        void Raw(string token)
        {
            BeforeValue();
            sb.Append(token);
        }

        void Member(string name, string token)
        {
            Name(name);
            Raw(token);
        }

        void Member(string name, float value) => Member(name, FormatFloat(value));
        void Member(string name, int value) => Member(name, value.ToString(CultureInfo.InvariantCulture));
        void Member(string name, uint value) => Member(name, value.ToString(CultureInfo.InvariantCulture));
        void Member(string name, bool value) => Member(name, value ? "true" : "false");

        void Version(int version)
        {
            Name("serializedVersion");
            BeforeValue();
            WriteString(version.ToString(CultureInfo.InvariantCulture));
        }

        void Vector(params (string Name, float Value)[] components)
        {
            StartObject();
            foreach (var (n, v) in components) Member(n, v);
            EndObject();
        }

        void WriteBuiltinStruct(object value)
        {
            switch (value)
            {
                case Vector2 v: Vector(("x", v.x), ("y", v.y)); break;
                case Vector3 v: Vector(("x", v.x), ("y", v.y), ("z", v.z)); break;
                case Vector4 v: Vector(("x", v.x), ("y", v.y), ("z", v.z), ("w", v.w)); break;
                case Quaternion q: Vector(("x", q.x), ("y", q.y), ("z", q.z), ("w", q.w)); break;
                case Color c: Vector(("r", c.r), ("g", c.g), ("b", c.b), ("a", c.a)); break;
                case Color32 c:
                    // ColorRGBA32: one packed value, red in the low byte.
                    StartObject();
                    Version(2);
                    Member("rgba", c.r | (uint)c.g << 8 | (uint)c.b << 16 | (uint)c.a << 24);
                    EndObject();
                    break;
                case Rect r:
                    StartObject();
                    Version(2);
                    Member("x", r.x);
                    Member("y", r.y);
                    Member("width", r.width);
                    Member("height", r.height);
                    EndObject();
                    break;
                case LayerMask m:
                    StartObject();
                    Version(2);
                    Member("m_Bits", (uint)m.value);
                    EndObject();
                    break;
                case Vector2Int v:
                    StartObject();
                    Member("x", v.x);
                    Member("y", v.y);
                    EndObject();
                    break;
                case Vector3Int v:
                    StartObject();
                    Member("x", v.x);
                    Member("y", v.y);
                    Member("z", v.z);
                    EndObject();
                    break;
                case RectInt r:
                    StartObject();
                    Version(2);
                    Member("x", r.x);
                    Member("y", r.y);
                    Member("width", r.width);
                    Member("height", r.height);
                    EndObject();
                    break;
                case Bounds b:
                    StartObject();
                    Name("m_Center");
                    Vector(("x", b.center.x), ("y", b.center.y), ("z", b.center.z));
                    Name("m_Extent");
                    Vector(("x", b.extents.x), ("y", b.extents.y), ("z", b.extents.z));
                    EndObject();
                    break;
                case Matrix4x4 m:
                    StartObject();
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++)
                            Member("e" + row + col, m[row, col]);
                    EndObject();
                    break;
                default:
                    throw new InvalidOperationException("No built-in layout for " + value.GetType());
            }
        }

        void WriteBuiltinClass(object value)
        {
            switch (value)
            {
                case AnimationCurve curve:
                    StartObject();
                    Version(2);
                    Name("m_Curve");
                    StartArray();
                    foreach (var k in curve.keys)
                    {
                        StartObject();
                        Version(3);
                        Member("time", k.time);
                        Member("value", k.value);
                        Member("inSlope", k.inTangent);
                        Member("outSlope", k.outTangent);
                        Member("weightedMode", k.WeightedModeBits);
                        Member("inWeight", k.inWeight);
                        Member("outWeight", k.outWeight);
                        EndObject();
                    }
                    EndArray();
                    Member("m_PreInfinity", WrapModeToInfinity(curve.preWrapMode));
                    Member("m_PostInfinity", WrapModeToInfinity(curve.postWrapMode));
                    Member("m_RotationOrder", 4); // Unity's default rotation order
                    EndObject();
                    break;
                case Gradient g:
                    WriteGradient(g);
                    break;
                case RectOffset r:
                    WriteRectOffset(r);
                    break;
                case GUIStyle s:
                    WriteGUIStyle(s);
                    break;
                default:
                    throw new InvalidOperationException("No built-in layout for " + value.GetType());
            }
        }

        void WriteRectOffset(RectOffset r)
        {
            StartObject();
            Member("m_Left", r.left);
            Member("m_Right", r.right);
            Member("m_Top", r.top);
            Member("m_Bottom", r.bottom);
            EndObject();
        }

        /// Native layout: eight packed keys (color keys' rgb, alpha keys' a; unused slots zero),
        /// times in 1/65535 units, mode, color space (-1: unspecified), key counts.
        void WriteGradient(Gradient g)
        {
            var colors = g.colorKeys;
            var alphas = g.alphaKeys;
            StartObject();
            Version(2);
            for (int i = 0; i < 8; i++)
            {
                var c = i < colors.Length ? colors[i].color : default;
                float a = i < alphas.Length ? alphas[i].alpha : 0f;
                Name("key" + i);
                Vector(("r", c.r), ("g", c.g), ("b", c.b), ("a", a));
            }
            for (int i = 0; i < 8; i++) Member("ctime" + i, i < colors.Length ? (int)MathF.Round(colors[i].time * 65535f) : 0);
            for (int i = 0; i < 8; i++) Member("atime" + i, i < alphas.Length ? (int)MathF.Round(alphas[i].time * 65535f) : 0);
            Member("m_Mode", g.Mode);
            Member("m_ColorSpace", -1);
            Member("m_NumColorKeys", colors.Length);
            Member("m_NumAlphaKeys", alphas.Length);
            EndObject();
        }

        /// The player's GUIStyle transfer (ScriptData.ReadGUIStyle reads the same layout).
        void WriteGUIStyle(GUIStyle s)
        {
            StartObject();
            Name("m_Name");
            BeforeValue();
            WriteString(s.m_Name ?? "");
            foreach (var (n, state) in new[]
                     {
                         ("m_Normal", s.m_Normal), ("m_Hover", s.m_Hover), ("m_Active", s.m_Active), ("m_Focused", s.m_Focused),
                         ("m_OnNormal", s.m_OnNormal), ("m_OnHover", s.m_OnHover), ("m_OnActive", s.m_OnActive), ("m_OnFocused", s.m_OnFocused),
                     })
            {
                Name(n);
                StartObject();
                Name("m_Background");
                WriteValue(state?.m_Background, SerializedLayout.ShapeOf(typeof(Texture2D)), 0);
                var color = state?.m_TextColor ?? default;
                Name("m_TextColor");
                Vector(("r", color.r), ("g", color.g), ("b", color.b), ("a", color.a));
                EndObject();
            }
            foreach (var (n, offset) in new[] { ("m_Border", s.m_Border), ("m_Margin", s.m_Margin), ("m_Padding", s.m_Padding), ("m_Overflow", s.m_Overflow) })
            {
                Name(n);
                WriteRectOffset(offset ?? new RectOffset());
            }
            Name("m_Font");
            WriteValue(s.m_Font, SerializedLayout.ShapeOf(typeof(Font)), 0);
            Member("m_FontSize", s.m_FontSize);
            Member("m_FontStyle", (int)s.m_FontStyle);
            Member("m_Alignment", (int)s.m_Alignment);
            Member("m_WordWrap", s.m_WordWrap);
            Member("m_RichText", s.m_RichText);
            Member("m_TextClipping", (int)s.m_TextClipping);
            Member("m_ImagePosition", (int)s.m_ImagePosition);
            Name("m_ContentOffset");
            Vector(("x", s.m_ContentOffset.x), ("y", s.m_ContentOffset.y));
            Member("m_FixedWidth", s.m_FixedWidth);
            Member("m_FixedHeight", s.m_FixedHeight);
            Member("m_StretchWidth", s.m_StretchWidth);
            Member("m_StretchHeight", s.m_StretchHeight);
            EndObject();
        }

        static int WrapModeToInfinity(WrapMode mode) => mode switch
        {
            WrapMode.PingPong => 0,
            WrapMode.Loop => 1,
            _ => 2,
        };

        // ---- Scalars ----

        /// RapidJSON's escaping: quote, backslash and control characters (\b \t \n \f \r, others
        /// as \u00XX); everything else, including '/' and non-ASCII text, is written as is.
        void WriteString(string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\r': sb.Append("\\r"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u00").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        internal static string FormatFloat(float v) => FormatDouble(v);

        internal static string FormatDouble(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return NonFinite(v);
            if (v == 0d) return "0.0"; // negative zero too (observed)
            var (digits, k) = Grisu2.Digits(Math.Abs(v));
            return Prettify((v < 0 ? "-" : "") + digits + "E" + k.ToString(CultureInfo.InvariantCulture));
        }

        /// RapidJSON's spelling for non-finite numbers (kWriteNanAndInfFlag).
        static string NonFinite(double v) => double.IsNaN(v) ? "NaN" : v > 0 ? "Infinity" : "-Infinity";

        /// Lays out digits × 10^exponent ("123E-5") the way RapidJSON's Prettify does.
        static string Prettify(string roundTrip)
        {
            bool negative = roundTrip[0] == '-';
            var s = negative ? roundTrip.Substring(1) : roundTrip;
            int exp = 0;
            int e = s.IndexOfAny(['E', 'e']);
            if (e >= 0)
            {
                exp = int.Parse(s.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                s = s.Substring(0, e);
            }
            int dot = s.IndexOf('.');
            string intPart = dot >= 0 ? s.Substring(0, dot) : s;
            string all = dot >= 0 ? intPart + s.Substring(dot + 1) : s;
            int point = intPart.Length + exp; // value = 0.<all> * 10^point
            int lead = 0;
            while (lead < all.Length - 1 && all[lead] == '0') lead++;
            string digits = all.Substring(lead).TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            int kk = point - lead;          // value = 0.<digits> * 10^kk
            int length = digits.Length;
            int k = kk - length;            // value = <digits> * 10^k

            var sb = new StringBuilder();
            if (negative) sb.Append('-');
            if (k >= 0 && kk <= 21)
            {
                sb.Append(digits).Append('0', k).Append(".0");
            }
            else if (kk > 0 && kk <= 21)
            {
                sb.Append(digits, 0, kk).Append('.').Append(digits, kk, length - kk);
            }
            else if (kk > -6 && kk <= 0)
            {
                sb.Append("0.").Append('0', -kk).Append(digits);
            }
            else if (length == 1)
            {
                sb.Append(digits).Append('e').Append((kk - 1).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(digits[0]).Append('.').Append(digits, 1, length - 1).Append('e').Append((kk - 1).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
