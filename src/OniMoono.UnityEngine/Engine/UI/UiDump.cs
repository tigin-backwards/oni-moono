using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace OniMoono.Engine.UI
{
    /// Diagnostics (`--dump-ui FRAME:PATH`): every active UI element of the loaded scenes, one line
    /// each, in hierarchy order. It is the differential oracle for replacing Unity's UI packages
    /// (PLAN M9): the same screens dumped with the shipped packages and with this project's must
    /// agree.
    ///
    /// A line holds the element's path (then a tab), its rect and world corners, its canvas renderer's output
    /// (vertex and index counts, a hash of the geometry in vertex order and one independent of
    /// triangle order, materials, texture, colour, clipping, culling), and the state of the package
    /// components on it. That state is read through public members by name, so the dump does not
    /// depend on which implementation is loaded. Positions are rounded to 0.01, colours to 1/255
    /// and texture coordinates to 1e-4 before hashing: float rounding is not a difference.
    internal static class UiDump
    {
        /// Public members recorded per package component type (by full name, base types included).
        static readonly (string Type, string[] Members)[] recorded =
        [
            ("UnityEngine.Canvas", ["renderMode", "scaleFactor", "sortingOrder", "overrideSorting", "pixelPerfect"]),
            ("UnityEngine.CanvasGroup", ["alpha", "interactable", "blocksRaycasts"]),
            ("UnityEngine.UI.Graphic", ["color", "raycastTarget", "depth"]),
            ("UnityEngine.UI.Image", ["type", "fillAmount", "preserveAspect"]),
            ("UnityEngine.UI.Text", ["text", "fontSize", "alignment"]),
            ("UnityEngine.UI.Selectable", ["interactable"]),
            ("UnityEngine.UI.Toggle", ["isOn"]),
            ("UnityEngine.UI.Slider", ["value", "minValue", "maxValue"]),
            ("UnityEngine.UI.Scrollbar", ["value", "size", "numberOfSteps"]),
            ("UnityEngine.UI.ScrollRect", ["normalizedPosition", "velocity"]),
            ("UnityEngine.UI.InputField", ["text", "caretPosition"]),
            ("UnityEngine.UI.Dropdown", ["value"]),
            ("UnityEngine.UI.LayoutElement", ["minWidth", "minHeight", "preferredWidth", "preferredHeight", "flexibleWidth", "flexibleHeight", "ignoreLayout"]),
            ("UnityEngine.UI.LayoutGroup", ["preferredWidth", "preferredHeight", "minWidth", "minHeight"]),
            ("UnityEngine.UI.CanvasScaler", ["scaleFactor", "uiScaleMode"]),
            ("TMPro.TMP_Text", ["text", "fontSize", "alignment", "isTextOverflowing", "firstOverflowCharacterIndex", "textInfo"]),
            ("TMPro.TMP_InputField", ["text", "caretPosition", "isFocused"]),
            ("TMPro.TMP_Dropdown", ["value"]),
        ];

        static readonly Dictionary<Type, List<(string Label, Func<object, object> Get)>> readers = new();

        /// Elements whose path contains this text also get every vertex written
        /// (`--dump-ui-vertices`).
        public static string VerticesFilter;

        public static void Write(string path)
        {
            var text = new StringBuilder();
            var paths = new Dictionary<GameObjectNative, string>(ReferenceEqualityComparer.Instance);
            int elements = 0;
            foreach (var go in Finder.ActiveGameObjects())
            {
                var parent = go.Transform.Parent?.GameObject;
                var name = go.Name;
                var key = parent is not null && paths.TryGetValue(parent, out var p) ? p + "/" + name : "/" + name;
                // Siblings with the same name are told apart by their sibling index.
                key += "#" + ((GameObject)go.Managed).transform.GetSiblingIndex();
                paths[go] = key;
                if (((GameObject)go.Managed).transform is not RectTransform rt) continue;
                elements++;
                text.Append(key).Append('\t');
                var r = rt.rect;
                text.Append("rect ").Append(F(r.x)).Append(',').Append(F(r.y)).Append(',').Append(F(r.width)).Append(',').Append(F(r.height));
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                text.Append(" world ").Append(F(corners[0].x)).Append(',').Append(F(corners[0].y)).Append(',').Append(F(corners[2].x)).Append(',').Append(F(corners[2].y));
                foreach (var c in go.Components)
                {
                    if (c is null || c is Transform) continue;
                    text.Append(" | ").Append(c.GetType().Name);
                    if (c is Behaviour b && !b.enabled) text.Append("(off)");
                    if (c is CanvasRenderer cr) AppendRenderer(text, (CanvasRendererNative)cr.EngineState);
                    AppendMembers(text, c);
                }
                if (VerticesFilter != null && key.Contains(VerticesFilter, StringComparison.Ordinal)
                    && go.Managed is GameObject managed && managed.GetComponent<CanvasRenderer>() is { } renderer
                    && ((CanvasRendererNative)renderer.EngineState).Geometry is { } geometry)
                    AppendVertices(text, geometry);
                text.Append('\n');
            }
            File.WriteAllText(path, text.ToString());
            EngineLog.Engine($"UI dump: {elements} elements to {path}");
        }

        static void AppendRenderer(StringBuilder text, CanvasRendererNative r)
        {
            var g = r.Geometry;
            int vertices = g?.Positions.Length ?? 0, indices = g?.Indices.Length ?? 0;
            text.Append($" v={vertices} i={indices}");
            if (g != null && vertices > 0) text.Append($" h={OrderedHash(g):x8} t={TriangleHash(g):x8}");
            var materials = new List<string>();
            foreach (var m in r.Materials) materials.Add(!m ? "null" : m.name + "(" + m.shader?.name + ")");
            text.Append(" mat=").Append(string.Join(";", materials));
            if (r.Texture) text.Append(" tex=").Append(r.Texture.name);
            text.Append(" col=").Append(C(r.Color));
            if (r.ClipRect is { } clip) text.Append(" clip=").Append(F(clip.x)).Append(',').Append(F(clip.y)).Append(',').Append(F(clip.width)).Append(',').Append(F(clip.height));
            if (r.Cull) text.Append(" cull");
        }

        static void AppendMembers(StringBuilder text, Component c)
        {
            var type = c.GetType();
            if (!readers.TryGetValue(type, out var list))
            {
                list = new();
                for (var t = type; t != null; t = t.BaseType)
                    foreach (var (name, members) in recorded)
                        if (t.FullName == name)
                            foreach (var member in members)
                            {
                                var property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                                if (property?.GetMethod != null && property.GetIndexParameters().Length == 0) list.Add((member, o => property.GetValue(o)));
                                else if (type.GetField(member, BindingFlags.Public | BindingFlags.Instance) is { } field) list.Add((member, o => field.GetValue(o)));
                            }
                readers[type] = list;
            }
            foreach (var (label, get) in list)
            {
                object value;
                try { value = get(c); }
                catch (TargetInvocationException e) { value = "!" + e.InnerException?.GetType().Name; }
                text.Append(' ').Append(label).Append('=').Append(Value(value));
            }
        }

        /// The counts of a TextMeshPro text info (public fields, read by name).
        static readonly string[] textInfoCounts = ["characterCount", "spaceCount", "wordCount", "linkCount", "lineCount", "pageCount", "materialCount", "spriteCount"];

        static string TextInfoCounts(object info)
        {
            var type = info.GetType();
            var parts = new List<string>(textInfoCounts.Length);
            foreach (var name in textInfoCounts)
                if (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } f) parts.Add(Convert.ToString(f.GetValue(info), CultureInfo.InvariantCulture));
            var text = string.Join(",", parts);
            // Per material: its vertex count, and how many characters use it.
            if (type.GetField("materialCount")?.GetValue(info) is int materials && materials > 1
                && type.GetField("meshInfo")?.GetValue(info) is Array meshes
                && type.GetField("characterInfo")?.GetValue(info) is Array chars && type.GetField("characterCount")?.GetValue(info) is int count)
            {
                var perMaterial = new int[materials];
                for (int i = 0; i < count && i < chars.Length; i++)
                {
                    var c = chars.GetValue(i);
                    if (c.GetType().GetField("materialReferenceIndex")?.GetValue(c) is int m && m >= 0 && m < materials) perMaterial[m]++;
                }
                var list = new List<string>();
                var owner = type.GetField("textComponent")?.GetValue(info);
                var shared = owner?.GetType().GetProperty("fontSharedMaterial")?.GetValue(owner);
                var font = owner?.GetType().GetProperty("font")?.GetValue(owner);
                var fontMaterial = font?.GetType().GetProperty("material")?.GetValue(font);
                for (int m = 0; m < materials && m < meshes.Length; m++)
                {
                    var mi = meshes.GetValue(m);
                    int vertices = mi.GetType().GetField("vertexCount")?.GetValue(mi) is int v ? v : -1;
                    // The material: the text's shared one (s), its font's (f), or another (o).
                    var material = mi.GetType().GetField("material")?.GetValue(mi);
                    string which = material == null ? "-" : (ReferenceEquals(material, shared) ? "s" : "") + (ReferenceEquals(material, fontMaterial) ? "f" : "");
                    if (which == "") which = "o";
                    list.Add(vertices + "/" + perMaterial[m] + which);
                }
                text += " meshes=" + string.Join(";", list);
            }
            return text;
        }

        static string Value(object v) => v switch
        {
            null => "null",
            _ when v.GetType().FullName == "TMPro.TMP_TextInfo" => TextInfoCounts(v),
            float f => F(f),
            Vector2 v2 => F(v2.x) + "," + F(v2.y),
            Color c => C(c),
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"") + "\"",
            UnityEngine.Object o => !o ? "null" : o.name,
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };

        static string F(float v)
        {
            var s = Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        static string C(Color c) => $"{Math.Round(c.r * 255)},{Math.Round(c.g * 255)},{Math.Round(c.b * 255)},{Math.Round(c.a * 255)}";

        /// One vertex as rounded integers: position (0.01), colour (1/255), the four texture
        /// coordinate channels (1e-4), normal and tangent (1e-3). TextMeshPro's shaders read data
        /// from the extra channels, so all of them count.
        static long[] Vertex(CanvasGeometry g, int i)
        {
            var values = new List<long>(24);
            var p = g.Positions[i];
            values.Add((long)Math.Round(p.x * 100)); values.Add((long)Math.Round(p.y * 100)); values.Add((long)Math.Round(p.z * 100));
            var c = g.Colors != null && i < g.Colors.Length ? g.Colors[i] : Color.white;
            values.Add((long)Math.Round(c.r * 255) << 24 | (long)Math.Round(c.g * 255) << 16 | (long)Math.Round(c.b * 255) << 8 | (long)Math.Round(c.a * 255));
            foreach (var channel in g.Uvs)
            {
                var uv = channel != null && i < channel.Length ? channel[i] : Vector4.zero;
                values.Add((long)Math.Round(uv.x * 10000)); values.Add((long)Math.Round(uv.y * 10000));
                values.Add((long)Math.Round(uv.z * 10000)); values.Add((long)Math.Round(uv.w * 10000));
            }
            var n = g.Normals != null && i < g.Normals.Length ? g.Normals[i] : Vector3.zero;
            values.Add((long)Math.Round(n.x * 1000)); values.Add((long)Math.Round(n.y * 1000)); values.Add((long)Math.Round(n.z * 1000));
            var t = g.Tangents != null && i < g.Tangents.Length ? g.Tangents[i] : Vector4.zero;
            values.Add((long)Math.Round(t.x * 1000)); values.Add((long)Math.Round(t.y * 1000)); values.Add((long)Math.Round(t.z * 1000)); values.Add((long)Math.Round(t.w * 1000));
            return values.ToArray();
        }

        /// Every vertex and the indices of an element, for `--dump-ui-vertices`: what a package
        /// generated, channel by channel.
        static void AppendVertices(StringBuilder text, CanvasGeometry g)
        {
            for (int i = 0; i < g.Positions.Length; i++)
            {
                var c = g.Colors != null && i < g.Colors.Length ? g.Colors[i] : Color.white;
                text.Append("\n  v").Append(i).Append(" pos ").Append(V(g.Positions[i])).Append(" col ").Append(C(c));
                for (int k = 0; k < 4; k++)
                    if (g.Uvs[k] != null && i < g.Uvs[k].Length) text.Append(" uv").Append(k).Append(' ').Append(V(g.Uvs[k][i]));
                if (g.Normals != null && i < g.Normals.Length) text.Append(" n ").Append(V(g.Normals[i]));
                if (g.Tangents != null && i < g.Tangents.Length) text.Append(" t ").Append(V(g.Tangents[i]));
            }
            text.Append("\n  indices ").Append(string.Join(",", g.Indices));
        }

        // Round-trip precision (R): the listing is for measuring a package's arithmetic exactly.
        static string V(Vector4 v) => string.Create(CultureInfo.InvariantCulture, $"{v.x:R},{v.y:R},{v.z:R},{v.w:R}");

        static uint OrderedHash(CanvasGeometry g)
        {
            uint h = 2166136261;
            for (int i = 0; i < g.Positions.Length; i++)
            {
                // Field by field: tuple hash codes are randomized per process.
                foreach (long x in Vertex(g, i)) h = Mix(h, (int)x ^ (int)(x >> 32));
            }
            foreach (int i in g.Indices) h = Mix(h, i);
            return h;
        }

        /// The geometry as a set of triangles, each the sorted triple of its vertices: equal for
        /// meshes that draw the same triangles in another order or with other vertex numbering.
        static uint TriangleHash(CanvasGeometry g)
        {
            var triangles = new List<string>();
            for (int i = 0; i + 2 < g.Indices.Length; i += 3)
            {
                var corners = new[] { string.Join(",", Vertex(g, g.Indices[i])), string.Join(",", Vertex(g, g.Indices[i + 1])), string.Join(",", Vertex(g, g.Indices[i + 2])) };
                Array.Sort(corners, StringComparer.Ordinal);
                triangles.Add(string.Join("|", corners));
            }
            triangles.Sort(StringComparer.Ordinal);
            uint h = 2166136261;
            foreach (var t in triangles) foreach (char ch in t) h = Mix(h, ch);
            return h;
        }

        static uint Mix(uint h, int value) => (h ^ (uint)value) * 16777619;
    }
}
