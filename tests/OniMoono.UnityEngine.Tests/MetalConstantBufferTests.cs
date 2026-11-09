using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OniMoono.Assets.Files;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;
using OniMoono.Desktop.Gl;
using OniMoono.Desktop.Metal;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Tests
{
    /// The Metal constant-buffer packer: members at the offsets the parameter blob records, which
    /// must be where the MSL reads them, with the values the GL device would give the same draw.
    public class MetalConstantBufferTests
    {
        static readonly int ColorId = Shader.PropertyToID("_PackTestColor");
        static readonly int CutoffId = Shader.PropertyToID("_PackTestCutoff");
        static readonly int WeightsId = Shader.PropertyToID("_PackTestWeights");
        static readonly int CountId = Shader.PropertyToID("_PackTestCount");
        static readonly int OffsetsId = Shader.PropertyToID("_PackTestOffsets");

        static int Word(byte[] b, int offset) => BitConverter.ToInt32(b, offset);
        static float Float(byte[] b, int offset) => BitConverter.ToSingle(b, offset);

        [Fact]
        public void MembersGoToTheirOffsetsWithMetalStrides()
        {
            // As HLSLcc lays out FGlobals for Metal: a float array packed tight, a float3 array with
            // 16-byte elements, an int, and a matrix in UnityPerDraw.
            var p = new ShaderParameters();
            p.ConstantBuffers.Add(new ShaderConstantBuffer("FGlobals", 96,
            [
                new ShaderConstant("_PackTestColor", 0, 1, 4, false, 0, 0),
                new ShaderConstant("_PackTestCutoff", 0, 1, 1, false, 0, 16),
                new ShaderConstant("_PackTestWeights", 0, 1, 1, false, 3, 20),
                new ShaderConstant("_PackTestCount", 1, 1, 1, false, 0, 32),
                new ShaderConstant("_PackTestOffsets", 0, 1, 3, false, 2, 48),
                new ShaderConstant("_ScreenParams", 0, 1, 4, false, 0, 80),
            ]));
            p.ConstantBuffers.Add(new ShaderConstantBuffer("UnityPerDraw", 64, [new ShaderConstant("unity_ObjectToWorld", 0, 4, 4, true, 0, 0)]));
            p.ConstantBufferBindings.Add(new ShaderBufferParameter("FGlobals", 0, 1));
            p.ConstantBufferBindings.Add(new ShaderBufferParameter("UnityPerDraw", 1, 1));
            var layout = MetalConstantBuffers.Layout(p);
            Assert.Equal(["FGlobals", "UnityPerDraw"], layout.Select(b => b.Name));
            Assert.Equal([0, 1], layout.Select(b => b.Slot));

            var material = new MaterialNative();
            material.Properties.SetVector(ColorId, new Vector4(1f, 2f, 3f, 4f));
            material.Properties.SetFloat(CutoffId, 0.5f);
            material.Properties.SetFloatArray(WeightsId, [7f, 8f, 9f], 3);
            material.Properties.SetInt(CountId, 5);
            material.Properties.SetVectorArray(OffsetsId, [new Vector4(10f, 11f, 12f, 99f), new Vector4(13f, 14f, 15f, 99f)], 2);
            var d = new DrawInputs
            {
                Material = material,
                ObjectToWorld = Matrix4x4.Translate(new Vector3(3f, 4f, 5f)),
                Camera = new CameraConstants(Matrix4x4.identity, Matrix4x4.identity, Vector3.zero, 0.3f, 1000f, true, new Vector2(1920f, 1080f)),
            };
            var globals = new byte[96];
            Array.Fill(globals, (byte)0xEE);
            MetalConstantBuffers.Pack(layout[0], d, globals);
            Assert.Equal([1f, 2f, 3f, 4f], Enumerable.Range(0, 4).Select(i => Float(globals, 4 * i)));
            Assert.Equal(0.5f, Float(globals, 16));
            Assert.Equal([7f, 8f, 9f], Enumerable.Range(0, 3).Select(i => Float(globals, 20 + 4 * i)));
            Assert.Equal(5, Word(globals, 32));
            Assert.Equal([10f, 11f, 12f, 0f, 13f, 14f, 15f], Enumerable.Range(0, 7).Select(i => Float(globals, 48 + 4 * i)));
            Assert.Equal(1920f, Float(globals, 80));
            Assert.Equal(0, Word(globals, 36)); // padding is cleared

            var perDraw = new byte[64];
            MetalConstantBuffers.Pack(layout[1], d, perDraw);
            Assert.Equal([3f, 4f, 5f, 1f], Enumerable.Range(12, 4).Select(i => Float(perDraw, 4 * i))); // column 3: translation
            Assert.Equal(1f, Float(perDraw, 0));
        }

        // The macOS build, when present: every Metal program.

        sealed class MacShaders : IDisposable
        {
            public readonly List<ShaderNative> Shaders = new();

            public MacShaders()
            {
                var dataDir = MetalShaderProgramTests.MacDataDir;
                if (dataDir == null) Assert.Skip("macOS game data not found (set ONI_MAC_APP to OxygenNotIncluded.app).");
                foreach (var name in new[] { "level0", "level1", "level2", "resources.assets", "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets", "Resources/unity default resources", "Resources/unity_builtin_extra" })
                {
                    var file = SerializedFile.Open(Path.Combine(dataDir, name));
                    foreach (var info in file.Objects)
                    {
                        var type = file.TypeOf(info);
                        if (type.ClassId != 48) continue;
                        var shader = new ShaderNative();
                        shader.Bind(type.Tree != null ? TypeTreeReader.ReadObject(type.Tree, file.OpenObject(info), TypeTreeDatabase.TrailingBytes(48)) : TypeTreeReader.ReadNative(48, file.OpenObject(info)), null);
                        Shaders.Add(shader);
                    }
                }
            }

            public void Dispose()
            {
                foreach (var s in Shaders) ShaderRegistry.Unregister(s);
            }
        }

        /// Every stage program of every pass, with its parameters.
        static IEnumerable<(ShaderNative Shader, ShaderPass Pass, string Stage, ProgramVariant Variant, MetalProgram Program, ShaderParameters Parameters)> MetalPrograms(MacShaders mac)
        {
            foreach (var shader in mac.Shaders)
            {
                var programs = shader.ProgramsFor(ShaderNative.MetalPlatform);
                if (programs == null) continue;
                foreach (var sub in shader.SubShaders)
                    foreach (var pass in sub.Passes)
                        foreach (var (stage, variants) in new[] { ("progVertex", pass.MetalVertexVariants), ("progFragment", pass.MetalFragmentVariants) })
                            foreach (var v in variants)
                                yield return (shader, pass, stage, v, programs.Metal(v.BlobIndex), programs.Parameters(pass.Data, stage, v.ParameterBlobIndex));
            }
        }

        /// A draw whose every input has a value of its own: the renderer's block gives each name
        /// a float, an int, a vector, a matrix and arrays derived from the name.
        static DrawInputs Draw(IEnumerable<string> names)
        {
            var block = new PropertySheet();
            foreach (var name in names)
            {
                int id = Shader.PropertyToID(name);
                float h = (uint)name.GetHashCode() % 1000 / 7f + 1f;
                block.SetFloat(id, h);
                block.SetVector(id, new Vector4(h, h + 0.25f, h + 0.5f, h + 0.75f));
                var m = Matrix4x4.TRS(new Vector3(h, -h, 2 * h), Quaternion.Euler(h, 2 * h, 3 * h), new Vector3(1f, 2f, h));
                block.SetMatrix(id, m);
                block.SetFloatArray(id, Enumerable.Range(0, 64).Select(i => h + i).ToArray(), 64);
                block.SetVectorArray(id, Enumerable.Range(0, 64).Select(i => new Vector4(h, i, -i, h * i)).ToArray(), 64);
                block.SetMatrixArray(id, Enumerable.Range(0, 8).Select(i => m * Matrix4x4.Translate(new Vector3(i, 0, 0))).ToArray(), 8);
            }
            var view = Matrix4x4.TRS(new Vector3(-30f, -20f, 10f), Quaternion.identity, Vector3.one);
            var projection = Matrix4x4.Ortho(-16f, 16f, -9f, 9f, 0.3f, 1000f);
            return new DrawInputs
            {
                Block = block,
                ObjectToWorld = Matrix4x4.TRS(new Vector3(5f, 6f, 7f), Quaternion.Euler(0f, 0f, 30f), new Vector3(2f, 2f, 1f)),
                Camera = new CameraConstants(view, projection, new Vector3(30f, 20f, -10f), 0.3f, 1000f, true, new Vector2(1600f, 900f)),
            };
        }

        static string MslType(MetalConstant m) => m.Constant.IsMatrix ? "float4"
            : (m.Kind switch { ConstantKind.Int => "int", ConstantKind.UInt => "uint", _ => "float" }) + (m.Components > 1 ? m.Components.ToString() : "");

        [Fact]
        public void EveryMetalBuffersLayoutIsTheOneItsMslDeclares()
        {
            using var mac = new MacShaders();
            int programs = 0, buffers = 0, members = 0;
            var failures = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in MetalPrograms(mac))
                foreach (var cb in x.Parameters.ConstantBuffers)
                    foreach (var m in cb.Members) names.Add(m.Name);
            var d = Draw(names);
            foreach (var x in MetalPrograms(mac))
            {
                programs++;
                string where = $"{x.Shader.ShaderName} {x.Stage} [{string.Join(" ", x.Variant.Keywords)}]";
                var structs = MslStructs.Layouts(x.Program.Source);
                foreach (var cb in MetalConstantBuffers.Layout(x.Parameters))
                {
                    buffers++;
                    if (!structs.TryGetValue(cb.Name + "_Type", out var msl)) { failures.Add($"{where}: no MSL struct {cb.Name}_Type"); continue; }
                    var byName = msl.ToDictionary(m => m.Name, StringComparer.Ordinal);
                    int end = 0;
                    var covered = new bool[cb.Size];
                    foreach (var m in cb.Members)
                    {
                        members++;
                        string mslName = m.Constant.IsMatrix ? "hlslcc_mtx4x4" + m.Constant.Name : m.Constant.Name;
                        if (!byName.TryGetValue(mslName, out var s)) { failures.Add($"{where}: {cb.Name}.{mslName} not in the MSL struct"); continue; }
                        int size = MetalConstantBuffers.Size(m);
                        if (s.Offset != m.Constant.Offset) failures.Add($"{where}: {cb.Name}.{mslName} at {m.Constant.Offset} in the blob, {s.Offset} in the MSL");
                        if (s.Type != MslType(m)) failures.Add($"{where}: {cb.Name}.{mslName} is {s.Type} in the MSL, {MslType(m)} for the packer");
                        if (Math.Max(1, s.ArraySize) != m.Elements) failures.Add($"{where}: {cb.Name}.{mslName} has {s.ArraySize} elements in the MSL, {m.Elements} for the packer");
                        if (m.Elements > 1 && s.Stride != m.Stride) failures.Add($"{where}: {cb.Name}.{mslName} stride {s.Stride} in the MSL, {m.Stride} for the packer");
                        for (int b = m.Constant.Offset; b < m.Constant.Offset + size && b < covered.Length; b++)
                        {
                            if (covered[b]) { failures.Add($"{where}: {cb.Name}.{mslName} overlaps another member"); break; }
                            covered[b] = true;
                        }
                        end = Math.Max(end, m.Constant.Offset + size);
                    }
                    if (end > cb.Size) failures.Add($"{where}: {cb.Name} members end at {end}, past its size {cb.Size}");
                    if (cb.Size > MslStructs.SizeOf(msl)) failures.Add($"{where}: {cb.Name} size {cb.Size}, its MSL struct {MslStructs.SizeOf(msl)}");

                    // Packed, every member reads back as the value resolved for it.
                    var bytes = new byte[cb.Size];
                    MetalConstantBuffers.Pack(cb, d, bytes);
                    var o = new float[4 * 1024];
                    foreach (var m in cb.Members)
                    {
                        int n = GlUniforms.Resolve(m.Binding, d, o);
                        bool slotted = m.Elements > 1 || m.Constant.IsMatrix;
                        for (int e = 0; e < m.Elements && (slotted ? e * 4 : 0) < n; e++)
                            for (int c = 0; c < m.Components; c++)
                            {
                                float v = o[(slotted ? e * 4 : 0) + c];
                                int expected = m.Kind == ConstantKind.Int ? (int)v : m.Kind == ConstantKind.UInt ? (int)(uint)v : BitConverter.SingleToInt32Bits(v);
                                int actual = Word(bytes, m.Constant.Offset + e * m.Stride + 4 * c);
                                if (actual != expected) { failures.Add($"{where}: {cb.Name}.{m.Constant.Name}[{e}].{c} packed {actual:x8}, resolved {expected:x8}"); e = m.Elements; break; }
                            }
                    }
                }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join("\n", failures.Take(40)));
            TestContext.Current.TestOutputHelper?.WriteLine($"{programs} programs, {buffers} buffers, {members} members");
            Assert.Equal(1057, programs);
            Assert.True(buffers > 1500 && members > 5000, $"{buffers} buffers, {members} members");
        }

        /// GL reflection's type for a GLSL type.
        static uint GlType(string type) => type switch
        {
            #pragma warning disable format // a hand-laid table
            "float" => FloatType, "vec2" => FloatVec2, "vec3" => FloatVec3, "vec4" => FloatVec4,
            "int" => IntType, "ivec2" => IntVec2, "ivec3" => IntVec3, "ivec4" => IntVec4,
            "uint" => UnsignedIntType, "uvec2" => UintVec2, "uvec3" => UintVec3, "uvec4" => UintVec4,
            #pragma warning restore format
            "mat4" => FloatMat4,
            _ => throw new NotSupportedException(type),
        };

        [Fact]
        public void MetalBuffersHoldWhatTheGlDeviceSetsForTheSameDraw()
        {
            using var mac = new MacShaders();
            var failures = new List<string>();
            int variants = 0, compared = 0, values = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in MetalPrograms(mac))
                foreach (var cb in x.Parameters.ConstantBuffers)
                    foreach (var m in cb.Members) names.Add(m.Name);
            var d = Draw(names);
            foreach (var shader in mac.Shaders)
            {
                var metal = shader.ProgramsFor(ShaderNative.MetalPlatform);
                if (metal == null || shader.Programs == null) continue;
                foreach (var sub in shader.SubShaders)
                    foreach (var pass in sub.Passes)
                        foreach (var gl in pass.Variants)
                        {
                            var enabled = new HashSet<string>(gl.Keywords, StringComparer.Ordinal);
                            var stages = ShaderPrograms.SelectStages(pass, ShaderNative.MetalPlatform, enabled.Contains);
                            if (stages.Vertex < 0 || stages.Fragment < 0) continue;
                            var keywords = metal.Metal(stages.Vertex).Keywords.Concat(metal.Metal(stages.Fragment).Keywords).ToHashSet(StringComparer.Ordinal);
                            // Only where Metal has programs for exactly these keywords (and not
                            // GL_OVR_multiview's, which Metal cannot have): others are other programs.
                            if (!keywords.SetEquals(enabled) || enabled.Contains("STEREO_MULTIVIEW_ON")) continue;
                            variants++;
                            string where = $"{shader.ShaderName} [{string.Join(" ", gl.Keywords)}]";

                            // The GL device's program: its default-block uniforms as reflection gives them.
                            var variant = shader.Programs.Variant(gl.BlobIndex);
                            var program = new DeviceProgram();
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var source in new[] { variant.VertexSource, variant.FragmentSource })
                                foreach (var u in GlslDeclarations.Uniforms(source))
                                {
                                    if (u.IsSampler || u.Block != null || !seen.Add(u.Name)) continue;
                                    var b = new UniformBinding { Name = u.Name, Type = GlType(u.Type), Size = Math.Max(1, u.ArraySize) };
                                    GlPrograms.Classify(b, u.Name);
                                    program.Uniforms.Add(b);
                                    program.LooseLength += GlUniforms.Length(b);
                                }
                            var loose = new float[program.LooseLength];
                            GlUniforms.Record(program, d, loose, Span<byte>.Empty);

                            // Metal's buffers for the same draw, by member.
                            var packed = new Dictionary<(string Name, bool Matrix), (MetalConstant Member, byte[] Bytes)>();
                            foreach (var (stage, parameters) in new[] { ("progVertex", stages.VertexParameters), ("progFragment", stages.FragmentParameters) })
                                foreach (var cb in MetalConstantBuffers.Layout(metal.Parameters(pass.Data, stage, parameters)))
                                {
                                    var bytes = new byte[cb.Size];
                                    MetalConstantBuffers.Pack(cb, d, bytes);
                                    foreach (var m in cb.Members) packed.TryAdd((m.Binding.Name, m.Binding.IsMatrix), (m, bytes));
                                }

                            int at = 0;
                            foreach (var u in program.Uniforms)
                            {
                                int length = GlUniforms.Length(u);
                                var glValues = loose.AsSpan(at, length);
                                at += length;
                                if (!packed.TryGetValue((u.Name, u.IsMatrix), out var p))
                                {
                                    failures.Add($"{where}: {u.Name} is in no Metal buffer");
                                    continue;
                                }
                                compared++;
                                var m = p.Member;
                                bool glInt = u.Type is IntType or IntVec2 or IntVec3 or IntVec4;
                                bool glUint = u.Type is UnsignedIntType or UintVec2 or UintVec3 or UintVec4;
                                if ((m.Kind == ConstantKind.Int) != glInt || (m.Kind == ConstantKind.UInt) != glUint)
                                {
                                    failures.Add($"{where}: {u.Name} GL type {u.Type:x}, Metal {m.Kind}");
                                    continue;
                                }
                                int glElements = u.IsMatrix ? u.Size : u.Size;
                                if (glElements != m.Elements) { failures.Add($"{where}: {u.Name} {glElements} elements on GL, {m.Elements} on Metal"); continue; }
                                bool slotted = m.Elements > 1 || u.IsMatrix;
                                for (int e = 0; e < m.Elements; e++)
                                    for (int c = 0; c < m.Components; c++)
                                    {
                                        int src = (slotted ? e * 4 : 0) + c;
                                        if (src >= glValues.Length) break;
                                        float v = glValues[src];
                                        // What glUniform*i/ui or *f receives.
                                        int expected = glInt ? (int)v : glUint ? (int)(uint)v : BitConverter.SingleToInt32Bits(v);
                                        int actual = Word(p.Bytes, m.Constant.Offset + e * m.Stride + 4 * c);
                                        values++;
                                        if (actual != expected) { failures.Add($"{where}: {u.Name}[{e}].{c} GL {expected:x8}, Metal {actual:x8}"); e = m.Elements; break; }
                                    }
                            }
                        }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join("\n", failures.Take(40)));
            TestContext.Current.TestOutputHelper?.WriteLine($"{variants} variants, {compared} uniforms, {values} values");
            Assert.True(variants > 500 && compared > 5000 && values > 50000, $"{variants} variants, {compared} uniforms, {values} values");
        }
    }
}
