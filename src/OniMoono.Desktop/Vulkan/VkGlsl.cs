using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OniMoono.Desktop.Vulkan
{
    /// Unity's GLCore program text made Vulkan GLSL, for shaders that carry no usable Vulkan
    /// program of U60's (mods' bundles built with other Unity versions; ARCHITECTURE §3.5,
    /// Vulkan). Vulkan GLSL has no default uniform block, no name-based linking and no implicit
    /// bindings, so a program's two stages are rewritten together, after preprocessing (HLSLcc
    /// wraps its uniform blocks in #if), laid out as Unity's Vulkan programs are (textures in
    /// set 0, constant buffers in set 1):
    ///
    /// - loose uniforms become one std140 block per stage (vertex at binding 0, fragment at 1,
    ///   set 1), members keeping their names, so the code reads them unchanged;
    /// - named uniform blocks (set 1), storage buffers and samplers (set 0) get descriptor
    ///   bindings by name, the same in both stages, in order of first declaration (vertex
    ///   stage first);
    /// - vertex inputs get the locations of their mesh channels (in_POSITION0 0 … in_TEXCOORD7
    ///   11; others from 12), and the varyings one location map by name for both stages;
    /// - gl_InstanceID reads gl_InstanceIndex (the device draws from instance 0, where they agree);
    /// - the program runs in OpenGL's clip space: it reads OpenGL's projection and
    ///   _ProjectionParams (UniformBinding.OpenGlClip), and after its own main the vertex stage
    ///   maps OpenGL's clip space onto the target as Unity's Vulkan programs lay it out: depth
    ///   from [-w, w] to the reversed [w, 0], and y turned over into the back buffer (stored top
    ///   row first) but not into a render texture (`oni_ClipY`, UniformSource.ClipY). Render
    ///   textures then hold OpenGL's rows for everything drawn, as on OpenGL: what the camera's
    ///   projection places, as Unity's Vulkan programs leave it too, and what a program writes
    ///   to clip space directly (Klei's TexturePage pages of the world's property textures),
    ///   which Unity's Vulkan programs leave turned over and read back with their
    ///   UNITY_UV_STARTS_AT_TOP branches. The GLCore text reads such textures turned over
    ///   (1 - v), as OpenGL needs. ARCHITECTURE §3.5.
    internal static class VkGlsl
    {
        public const int VertexGlobalsBinding = 0, FragmentGlobalsBinding = 1;
        public const string VertexGlobalsBlock = "oni_VertexGlobals", FragmentGlobalsBlock = "oni_FragmentGlobals";
        /// The vertex globals' member holding the epilogue's factor for y (UniformSource.ClipY).
        public const string ClipYUniform = "oni_ClipY";

        /// Mesh channels by vertex input name, in GlMeshes' Channel order.
        public static readonly string[] AttributeNames =
        [
            "in_POSITION0", "in_NORMAL0", "in_TANGENT0", "in_COLOR0",
            "in_TEXCOORD0", "in_TEXCOORD1", "in_TEXCOORD2", "in_TEXCOORD3", "in_TEXCOORD4", "in_TEXCOORD5", "in_TEXCOORD6", "in_TEXCOORD7",
        ];

        public enum ResourceKind { UniformBlock, StorageBuffer, Sampler }

        /// A descriptor the program reads. `Type`: the GLSL sampler type for samplers.
        public sealed record Resource(string Name, ResourceKind Kind, int Binding, string Type);

        public sealed class Result
        {
            public string Vertex, Fragment;
            /// Descriptors in binding order (the two globals blocks are not listed).
            public readonly List<Resource> Resources = new();
            /// Vertex inputs by location, with the input's GLSL type.
            public readonly SortedDictionary<int, (string Name, string Type)> Inputs = new();
            /// Whether each stage has a globals block (any loose uniform).
            public bool VertexGlobals, FragmentGlobals;
        }

        /// Preprocessed text of one stage (shaderc's preprocessor, which keeps #version and
        /// #extension lines). `macroSource` is the program text as Unity stores it.
        public delegate string Preprocess(string source, bool vertex);

        /// Makes the text one stage gives the preprocessor: no explicit uniform locations or
        /// block bindings (they are assigned after) and GLSL 4.50, the version Vulkan GLSL
        /// starts from. Unity's text is GLSL 1.50 or 4.20 core.
        public static string BeforePreprocessing(string source)
        {
            int versionEnd = source.IndexOf('\n') + 1;
            var first = source[..versionEnd].Trim();
            var body = source[versionEnd..];
            if (!first.StartsWith("#version", StringComparison.Ordinal)) throw new FormatException("program text without a #version line");
            body = body.Replace("#define UNITY_SUPPORTS_UNIFORM_LOCATION 1", "#define UNITY_SUPPORTS_UNIFORM_LOCATION 0", StringComparison.Ordinal);
            return "#version 450\n" + body;
        }

        /// Rewrites a program's preprocessed stages. Throws NotSupportedException for programs
        /// this device cannot run (XR stereo variants, which need multiview).
        public static Result Translate(string vertex, string fragment)
        {
            if (vertex.Contains("gl_ViewID", StringComparison.Ordinal) || vertex.Contains("gl_Layer", StringComparison.Ordinal)
                || fragment.Contains("gl_ViewID", StringComparison.Ordinal))
                throw new NotSupportedException("XR stereo variant (multiview)");
            var v = Parse(vertex);
            var f = Parse(fragment);
            var result = new Result();
            // Descriptors by name, in order of first declaration.
            var bindings = new Dictionary<string, Resource>(StringComparer.Ordinal);
            int next = 2;
            void Add(Stage s)
            {
                foreach (var d in s.Statements)
                {
                    var kind = d.Kind switch
                    {
                        StatementKind.UniformBlock => ResourceKind.UniformBlock,
                        StatementKind.StorageBuffer => ResourceKind.StorageBuffer,
                        StatementKind.Sampler => ResourceKind.Sampler,
                        _ => (ResourceKind)(-1),
                    };
                    if ((int)kind < 0) continue;
                    if (bindings.TryGetValue(d.Name, out var known))
                    {
                        if (known.Kind != kind || (kind == ResourceKind.Sampler && known.Type != d.Type))
                            throw new NotSupportedException($"'{d.Name}' is declared differently in the two stages");
                        continue;
                    }
                    var r = new Resource(d.Name, kind, next++, d.Type);
                    bindings[d.Name] = r;
                    result.Resources.Add(r);
                }
            }
            Add(v);
            Add(f);
            // Varyings: one location map for both stages, by name.
            var varyings = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in v.Statements)
                if (d.Kind == StatementKind.Out) varyings[d.Name] = Locations(d.Type, d.ArraySize);
            foreach (var d in f.Statements)
                if (d.Kind == StatementKind.In && !varyings.ContainsKey(d.Name)) varyings[d.Name] = Locations(d.Type, d.ArraySize);
            var varyingLocation = new Dictionary<string, int>(StringComparer.Ordinal);
            int at = 0;
            foreach (var (name, count) in varyings)
            {
                varyingLocation[name] = at;
                at += count;
            }
            int extraInput = AttributeNames.Length;
            var inputs = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in v.Statements)
            {
                if (d.Kind != StatementKind.In) continue;
                int channel = Array.IndexOf(AttributeNames, d.Name);
                int location = channel >= 0 ? channel : extraInput;
                if (channel < 0) extraInput += Locations(d.Type, d.ArraySize);
                inputs[d.Name] = location;
                result.Inputs[location] = (d.Name, d.Type);
            }
            result.Vertex = Emit(v, true, bindings, varyingLocation, inputs, out result.VertexGlobals);
            result.Fragment = Emit(f, false, bindings, varyingLocation, null, out result.FragmentGlobals);
            return result;
        }

        /// Locations a varying or input takes: one per vector, a column each for matrices.
        static int Locations(string type, int arraySize)
        {
            int columns = type switch
            {
                "mat2" or "mat2x2" or "mat2x3" or "mat2x4" or "dmat2" => 2,
                "mat3" or "mat3x3" or "mat3x2" or "mat3x4" => 3,
                "mat4" or "mat4x4" or "mat4x2" or "mat4x3" => 4,
                _ => 1,
            };
            return columns * Math.Max(1, arraySize);
        }

        enum StatementKind { Other, LooseUniform, Sampler, UniformBlock, StorageBuffer, In, Out, Main }

        /// A global statement of interest: its text range and what it declares.
        sealed class Statement
        {
            public StatementKind Kind;
            public int Start, End; // [Start, End): the declaration up to its ';' or '{' (excluded)
            public string Name, Type, Text;
            public int ArraySize;
            /// Qualifiers kept (interpolation) for varyings.
            public string Interpolation = "";
        }

        sealed class Stage
        {
            public string Source;
            public readonly List<Statement> Statements = new();
        }

        static readonly Regex LayoutPrefix = new(@"^layout\s*\(([^)]*)\)\s*", RegexOptions.Compiled);
        static readonly Regex Declarator = new(@"^(\w+)\s+(\w+)\s*(?:\[\s*(\d+)\s*\])?$", RegexOptions.Compiled);
        static readonly HashSet<string> Interpolations = new(StringComparer.Ordinal) { "flat", "noperspective", "smooth", "centroid", "invariant", "precise", "highp", "mediump", "lowp" };

        /// Splits the global scope into statements and classifies the declarations.
        static Stage Parse(string source)
        {
            var stage = new Stage { Source = source };
            int depth = 0, start = 0, i = 0;
            while (i < source.Length)
            {
                char c = source[i];
                if (c == '#' && (i == 0 || source[i - 1] == '\n') && depth == 0)
                {
                    // A preprocessor line (#version, #extension, #line).
                    int end = source.IndexOf('\n', i);
                    i = end < 0 ? source.Length : end + 1;
                    start = i;
                    continue;
                }
                if (c == '{')
                {
                    if (depth == 0) Classify(stage, start, i, opensBlock: true);
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) start = i + 1;
                }
                else if (c == ';' && depth == 0)
                {
                    Classify(stage, start, i, opensBlock: false);
                    start = i + 1;
                }
                i++;
            }
            return stage;
        }

        static void Classify(Stage stage, int start, int end, bool opensBlock)
        {
            var raw = stage.Source[start..end];
            var text = Regex.Replace(raw, @"\s+", " ").Trim();
            if (text.Length == 0) return;
            var s = new Statement { Start = start, End = end, Text = text };
            string layout = "";
            var rest = text;
            var m = LayoutPrefix.Match(rest);
            if (m.Success)
            {
                layout = m.Groups[1].Value;
                rest = rest[m.Length..];
            }
            if (opensBlock)
            {
                // `uniform Name`, `readonly buffer Name`, a struct, or a function.
                var words = rest.Split(' ');
                if (words.Length == 2 && words[0] == "uniform") { s.Kind = StatementKind.UniformBlock; s.Name = words[1]; }
                else if (words.Length >= 2 && words[^2] == "buffer" && Array.TrueForAll(words[..^2], w => w is "readonly" or "writeonly" or "coherent" or "restrict" or "volatile"))
                { s.Kind = StatementKind.StorageBuffer; s.Name = words[^1]; }
                else if (Regex.IsMatch(rest, @"^void main\s*\(\s*(void)?\s*\)$")) s.Kind = StatementKind.Main;
                else return;
                stage.Statements.Add(s);
                return;
            }
            var interpolation = new StringBuilder();
            while (true)
            {
                int space = rest.IndexOf(' ');
                if (space < 0 || !Interpolations.Contains(rest[..space])) break;
                interpolation.Append(rest[..space]).Append(' ');
                rest = rest[(space + 1)..];
            }
            string storage;
            if (rest.StartsWith("uniform ", StringComparison.Ordinal)) storage = "uniform";
            else if (rest.StartsWith("in ", StringComparison.Ordinal)) storage = "in";
            else if (rest.StartsWith("out ", StringComparison.Ordinal)) storage = "out";
            else return;
            rest = rest[(storage.Length + 1)..].Trim();
            while (true)
            {
                int space = rest.IndexOf(' ');
                if (space < 0 || !Interpolations.Contains(rest[..space])) break;
                rest = rest[(space + 1)..];
            }
            var d = Declarator.Match(rest);
            if (!d.Success) throw new NotSupportedException($"declaration '{text}' is not understood");
            s.Type = d.Groups[1].Value;
            s.Name = d.Groups[2].Value;
            s.ArraySize = d.Groups[3].Success ? int.Parse(d.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            s.Interpolation = interpolation.ToString();
            if (storage == "uniform") s.Kind = s.Type.Contains("sampler", StringComparison.Ordinal) ? StatementKind.Sampler : StatementKind.LooseUniform;
            else s.Kind = storage == "in" ? StatementKind.In : StatementKind.Out;
            // A fragment output keeps its location (SV_Target0 at 0).
            if (s.Kind == StatementKind.Out && layout.Contains("location", StringComparison.Ordinal) && s.Name.StartsWith("SV_", StringComparison.Ordinal)) return;
            if (s.Kind == StatementKind.Sampler && s.ArraySize > 0) throw new NotSupportedException($"sampler array '{s.Name}'");
            stage.Statements.Add(s);
        }

        static string Emit(Stage stage, bool vertex, Dictionary<string, Resource> bindings, Dictionary<string, int> varyings, Dictionary<string, int> inputs, out bool hasGlobals)
        {
            var src = stage.Source;
            var o = new StringBuilder(src.Length + 512);
            var globals = new StringBuilder();
            int globalsAt = -1;
            int copied = 0;
            foreach (var s in stage.Statements)
            {
                o.Append(src, copied, s.Start - copied);
                copied = s.End;
                string declarator = s.Type + " " + s.Name + (s.ArraySize > 0 ? $"[{s.ArraySize}]" : "");
                switch (s.Kind)
                {
                    case StatementKind.LooseUniform:
                        globals.Append("    ").Append(declarator).Append(";\n");
                        if (globalsAt < 0) globalsAt = o.Length;
                        copied = s.End + 1; // with its ';'
                        break;
                    case StatementKind.Sampler:
                        o.Append($"\nlayout(set = 0, binding = {bindings[s.Name].Binding}) uniform {declarator}");
                        break;
                    case StatementKind.UniformBlock:
                        o.Append($"\nlayout(std140, set = 1, binding = {bindings[s.Name].Binding}) uniform {s.Name} ");
                        break;
                    case StatementKind.StorageBuffer:
                        o.Append($"\nlayout(std430, set = 0, binding = {bindings[s.Name].Binding}) readonly buffer {s.Name} ");
                        break;
                    case StatementKind.In when vertex:
                        o.Append($"\nlayout(location = {inputs[s.Name]}) in {declarator}");
                        break;
                    case StatementKind.In:
                    case StatementKind.Out:
                        o.Append($"\nlayout(location = {varyings[s.Name]}) {s.Interpolation}{(s.Kind == StatementKind.In ? "in" : "out")} {declarator}");
                        break;
                    case StatementKind.Main:
                        o.Append(vertex ? "\nvoid oni_main() " : "\nvoid main() ");
                        break;
                }
            }
            o.Append(src, copied, src.Length - copied);
            if (vertex)
            {
                // Read only by the epilogue below: without other loose uniforms, the block goes
                // at the end.
                globals.Append($"    vec4 {ClipYUniform};\n");
                if (globalsAt < 0) globalsAt = o.Length;
            }
            hasGlobals = globals.Length > 0;
            if (hasGlobals)
            {
                string block = vertex ? VertexGlobalsBlock : FragmentGlobalsBlock;
                int binding = vertex ? VertexGlobalsBinding : FragmentGlobalsBinding;
                o.Insert(globalsAt, $"\nlayout(std140, set = 1, binding = {binding}) uniform {block} {{\n{globals}}};\n");
            }
            var text = Regex.Replace(o.ToString(), @"\bgl_InstanceID\b", "gl_InstanceIndex");
            if (vertex)
                text += "\nvoid main()\n{\n    oni_main();\n"
                    + "    gl_Position.z = 0.5 * (gl_Position.w - gl_Position.z);\n"
                    + $"    gl_Position.y *= {ClipYUniform}.x;\n}}\n";
            return text;
        }
    }
}
