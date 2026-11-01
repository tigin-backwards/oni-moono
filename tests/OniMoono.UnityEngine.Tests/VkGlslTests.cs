using System;
using System.IO;
using System.Linq;
using OniMoono.Desktop.Vulkan;
using Xunit;

namespace OniMoono.Tests
{
    /// The Vulkan device's rewrite of Unity's GLCore program text for shaders without a usable
    /// Vulkan program (VkGlsl), its compilation (shaderc) and the reflection of the result's
    /// uniform blocks (SpirvReflection).
    public class VkGlslTests
    {
        const string Header = "#version 150\n#extension GL_ARB_explicit_attrib_location : require\n"
            + "#define HLSLCC_ENABLE_UNIFORM_BUFFERS 1\n#if HLSLCC_ENABLE_UNIFORM_BUFFERS\n#define UNITY_UNIFORM\n#else\n#define UNITY_UNIFORM uniform\n#endif\n"
            + "#define UNITY_SUPPORTS_UNIFORM_LOCATION 1\n#if UNITY_SUPPORTS_UNIFORM_LOCATION\n#define UNITY_LOCATION(x) layout(location = x)\n#define UNITY_BINDING(x) layout(binding = x, std140)\n"
            + "#else\n#define UNITY_LOCATION(x)\n#define UNITY_BINDING(x) layout(std140)\n#endif\n";

        // As HLSLcc writes them: loose uniforms, a block wrapped in #if, a vertex texture read,
        // varyings declared in different orders in the two stages, one with a location.
        const string Vertex = Header + "uniform \tvec4 hlslcc_mtx4x4unity_ObjectToWorld[4];\nuniform \tfloat _Scale;\n"
            + "#if HLSLCC_ENABLE_UNIFORM_BUFFERS\nUNITY_BINDING(0) uniform UnityPerCamera {\n#endif\n\tUNITY_UNIFORM vec4 _Time;\n\tUNITY_UNIFORM vec4 _ScreenParams;\n#if HLSLCC_ENABLE_UNIFORM_BUFFERS\n};\n#endif\n"
            + "UNITY_LOCATION(0) uniform  sampler2D _Data;\nin  vec4 in_POSITION0;\nin  vec4 in_COLOR0;\nin  vec2 in_TEXCOORD1;\n"
            + "out vec2 vs_TEXCOORD0;\nflat out vec4 vs_COLOR0;\nlayout(location = 0) out float vs_FOG0;\nvec4 u_xlat0;\n"
            + "void main()\n{\n    u_xlat0 = textureLod(_Data, in_TEXCOORD1, 0.0) * _Scale + _Time;\n    gl_Position = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + u_xlat0 + float(gl_InstanceID);\n"
            + "    vs_TEXCOORD0 = in_TEXCOORD1; vs_COLOR0 = in_COLOR0; vs_FOG0 = _ScreenParams.x;\n    return;\n}\n";

        const string Fragment = Header + "uniform \tvec4 _Color;\n"
            + "#if HLSLCC_ENABLE_UNIFORM_BUFFERS\nUNITY_BINDING(0) uniform UnityPerCamera {\n#endif\n\tUNITY_UNIFORM vec4 _Time;\n\tUNITY_UNIFORM vec4 _ScreenParams;\n#if HLSLCC_ENABLE_UNIFORM_BUFFERS\n};\n#endif\n"
            + "UNITY_LOCATION(0) uniform  sampler2D _MainTex;\nUNITY_LOCATION(1) uniform  sampler2D _Data;\nlayout(location = 0) in  float vs_FOG0;\nflat in  vec4 vs_COLOR0;\nin  vec2 vs_TEXCOORD0;\n"
            + "layout(location = 0) out vec4 SV_Target0;\nvoid main()\n{\n    SV_Target0 = texture(_MainTex, vs_TEXCOORD0) * _Color * vs_COLOR0 + texture(_Data, vs_TEXCOORD0) * vs_FOG0 + _Time;\n    return;\n}\n";

        [Fact]
        public void Bindings_and_locations_are_shared_by_name()
        {
            using var compiler = new VkShaderCompiler();
            var compiled = compiler.Compile(Vertex, Fragment, "test");
            var p = compiled.Program;
            Assert.True(p.VertexGlobals);
            Assert.True(p.FragmentGlobals);
            // The block first, the samplers after, in order of first declaration (vertex first).
            Assert.Equal(["UnityPerCamera:UniformBlock:2", "_Data:Sampler:3", "_MainTex:Sampler:4"],
                p.Resources.Select(r => $"{r.Name}:{r.Kind}:{r.Binding}").ToArray());
            // Inputs at their channels' locations.
            Assert.Equal([(0, "in_POSITION0"), (3, "in_COLOR0"), (5, "in_TEXCOORD1")], p.Inputs.Select(i => (i.Key, i.Value.Name)).ToArray());
            // Varyings by name, sorted: vs_COLOR0 0, vs_FOG0 1, vs_TEXCOORD0 2, in both stages.
            Assert.Contains("layout(location = 0) flat out vec4 vs_COLOR0", p.Vertex);
            Assert.Contains("layout(location = 1) out float vs_FOG0", p.Vertex);
            Assert.Contains("layout(location = 2) out vec2 vs_TEXCOORD0", p.Vertex);
            Assert.Contains("layout(location = 0) flat in vec4 vs_COLOR0", p.Fragment);
            Assert.Contains("layout(location = 1) in float vs_FOG0", p.Fragment);
            Assert.Contains("layout(location = 2) in vec2 vs_TEXCOORD0", p.Fragment);
            Assert.Contains("layout(location = 0) out vec4 SV_Target0", p.Fragment);
            Assert.Contains("gl_InstanceIndex", p.Vertex);
            // The vertex epilogue maps OpenGL's clip space onto the target: reversed depth, y by
            // the target; blocks in set 1, samplers in set 0.
            Assert.Contains("gl_Position.z = 0.5 * (gl_Position.w - gl_Position.z);", p.Vertex);
            Assert.Contains("gl_Position.y *= oni_ClipY.x;", p.Vertex);
            Assert.Contains("layout(std140, set = 1, binding = 2) uniform UnityPerCamera", p.Vertex);
            Assert.Contains("layout(set = 0, binding = 3) uniform sampler2D _Data", p.Vertex);

            var vertexBlocks = SpirvReflection.UniformBlocks(compiled.Vertex).OrderBy(b => b.Binding).ToArray();
            Assert.Equal([(0, VkGlsl.VertexGlobalsBlock), (2, "UnityPerCamera")], vertexBlocks.Select(b => (b.Binding, b.Name)).ToArray());
            var globals = vertexBlocks[0];
            Assert.Equal(96, globals.Size);
            var matrix = globals.Members.Single(m => m.Name == "hlslcc_mtx4x4unity_ObjectToWorld");
            Assert.Equal((0, 4, 4, 16), (matrix.Offset, matrix.Components, matrix.ArrayLength, matrix.ArrayStride));
            var scale = globals.Members.Single(m => m.Name == "_Scale");
            Assert.Equal((64, 1, SpirvReflection.Scalar.Float), (scale.Offset, scale.Components, scale.Type));
            var clipY = globals.Members.Single(m => m.Name == VkGlsl.ClipYUniform);
            Assert.Equal((80, 4), (clipY.Offset, clipY.Components));
            Assert.Equal(["_Time@0", "_ScreenParams@16"], vertexBlocks[1].Members.Select(m => $"{m.Name}@{m.Offset}").ToArray());
            var fragmentBlocks = SpirvReflection.UniformBlocks(compiled.Fragment).OrderBy(b => b.Binding).ToArray();
            Assert.Equal([(1, VkGlsl.FragmentGlobalsBlock), (2, "UnityPerCamera")], fragmentBlocks.Select(b => (b.Binding, b.Name)).ToArray());
        }

        /// A vertex stage without loose uniforms (one writing clip space directly, as Klei's
        /// TexturePage) still gets the globals block for the epilogue's factor, and the factor
        /// reads as the built-in UniformSource.ClipY.
        [Fact]
        public void A_vertex_stage_without_loose_uniforms_gets_the_clip_factor()
        {
            const string vertex = Header + "in  vec2 in_TEXCOORD0;\nout vec2 vs_TEXCOORD0;\n"
                + "void main()\n{\n    gl_Position.xy = in_TEXCOORD0.xy * vec2(2.0, -2.0) + vec2(-1.0, 1.0);\n    gl_Position.zw = vec2(0.0, 1.0);\n"
                + "    vs_TEXCOORD0.xy = in_TEXCOORD0.xy;\n    return;\n}\n";
            const string fragment = Header + "UNITY_LOCATION(0) uniform  sampler2D _MainTex;\nin  vec2 vs_TEXCOORD0;\n"
                + "layout(location = 0) out vec4 SV_Target0;\nvoid main()\n{\n    SV_Target0 = texture(_MainTex, vs_TEXCOORD0.xy);\n    return;\n}\n";
            using var compiler = new VkShaderCompiler();
            var compiled = compiler.Compile(vertex, fragment, "page");
            Assert.True(compiled.Program.VertexGlobals);
            Assert.False(compiled.Program.FragmentGlobals);
            var globals = SpirvReflection.UniformBlocks(compiled.Vertex).Single();
            Assert.Equal((VkGlsl.VertexGlobalsBinding, 16), (globals.Binding, globals.Size));
            var u = new OniMoono.Desktop.Gl.UniformBinding();
            OniMoono.Desktop.Gl.GlPrograms.Classify(u, globals.Members.Single().Name);
            Assert.Equal(OniMoono.Desktop.Gl.UniformSource.ClipY, u.Source);
        }

        [Fact]
        public void Stereo_variants_are_not_supported()
        {
            var vertex = Vertex.Replace("float(gl_InstanceID)", "float(gl_ViewID_OVR)");
            using var compiler = new VkShaderCompiler();
            Assert.Throws<NotSupportedException>(() => compiler.Compile(vertex, Fragment, "stereo"));
        }

        /// Every variant of a `--dump-shader '*:DIR'` dump compiles (ONI_GLSL_DUMP=DIR; skipped
        /// without one).
        [Fact]
        public void Dumped_programs_compile()
        {
            var dir = Environment.GetEnvironmentVariable("ONI_GLSL_DUMP");
            if (string.IsNullOrEmpty(dir)) return;
            using var compiler = new VkShaderCompiler();
            int compiled = 0, unsupported = 0;
            var failures = new System.Collections.Generic.List<string>();
            foreach (var vertexFile in Directory.EnumerateFiles(dir, "*.vert", SearchOption.AllDirectories).Order())
            {
                var fragmentFile = Path.ChangeExtension(vertexFile, ".frag");
                if (!File.Exists(fragmentFile)) continue;
                try
                {
                    var c = compiler.Compile(File.ReadAllText(vertexFile), File.ReadAllText(fragmentFile), Path.GetFileName(vertexFile));
                    SpirvReflection.UniformBlocks(c.Vertex);
                    SpirvReflection.UniformBlocks(c.Fragment);
                    compiled++;
                }
                catch (NotSupportedException) { unsupported++; }
                catch (Exception e) { failures.Add($"{vertexFile}: {e.Message.Split('\n')[0]}"); }
            }
            Console.WriteLine($"{compiled} compiled, {unsupported} unsupported, {failures.Count} failed");
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(30)));
        }
    }
}
