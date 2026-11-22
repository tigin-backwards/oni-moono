using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// Shader program data: blob tables, parameter blobs and Metal program entries, on hand-written
    /// fixtures in the shipped layout and, where the macOS data is present, on all of it.
    public class ShaderDataTests
    {
        static byte[] ParameterBlob() => new ShaderBlobWriter()
            .UInt(ShaderParameters.Magic)
            .Int(3)
            .Buffer("", 0, 0).Int(0)
            .Buffer("VGlobals", 108, 3)
            .Member("_ClusterWorldSizeInfo", 0, 1, 4, false, 0, 0)
            .Member("ANIM_TEXTURE_START_OFFSET", 0, 1, 1, false, 0, 32)
            .Member("_Offsets", 1, 1, 4, false, 2, 48)
            .Int(0)
            .Buffer("UnityPerFrame", 368, 1)
            .Member("unity_MatrixVP", 0, 4, 4, true, 0, 272)
            .Int(0)
            .Int(5)
            .Texture("buildAndAnimTex", 0, 0, 2)
            .Texture("instanceTex", 3, -1, 2)
            .Texture("_CameraDepthTexture", 4, 1, 5, multisampled: true)
            .ConstantBufferBinding("VGlobals", 0)
            .StructuredBuffer("unity_ParticleInstanceData", 3)
            .ToArray();

        [Fact]
        public void ParameterBlobGivesBuffersMembersAndBindings()
        {
            var p = ShaderParameters.Read(ParameterBlob());
            Assert.Equal(["", "VGlobals", "UnityPerFrame"], p.ConstantBuffers.Select(b => b.Name));
            var globals = p.Buffer("VGlobals")!;
            Assert.Equal(108, globals.Size);
            Assert.Equal(new ShaderConstant("ANIM_TEXTURE_START_OFFSET", 0, 1, 1, false, 0, 32), globals.Members[1]);
            Assert.Equal(2, globals.Members[2].Elements);
            Assert.Equal(1, globals.Members[0].Elements);
            var vp = p.Buffer("UnityPerFrame")!.Members.Single();
            Assert.True(vp.IsMatrix);
            Assert.Equal((4, 4, 272), (vp.Rows, vp.Columns, vp.Offset));

            Assert.Equal(new ShaderTextureParameter("buildAndAnimTex", 0, 0, 2, false), p.Textures[0]);
            Assert.Equal(-1, p.Textures[1].SamplerIndex);
            Assert.Equal(new ShaderTextureParameter("_CameraDepthTexture", 4, 1, 5, true), p.Textures[2]);
            Assert.Equal(new ShaderBufferParameter("VGlobals", 0, 0), p.ConstantBufferBindings.Single());
            Assert.Equal(new ShaderBufferParameter("unity_ParticleInstanceData", 3, 1), p.StructuredBuffers.Single());
        }

        [Fact]
        public void DamagedParameterBlobsAreRefused()
        {
            var good = ParameterBlob();
            Assert.Throws<InvalidDataException>(() => ShaderParameters.Read([.. good, 0, 0, 0, 0]));
            Assert.Throws<InvalidDataException>(() => ShaderParameters.Read(good.AsSpan(0, good.Length - 4)));
            var badMagic = (byte[])good.Clone();
            badMagic[0] ^= 1;
            Assert.Throws<InvalidDataException>(() => ShaderParameters.Read(badMagic));
            // A buffer with struct members, and an unknown binding kind.
            Assert.Throws<InvalidDataException>(() => ShaderParameters.Read(new ShaderBlobWriter().UInt(ShaderParameters.Magic).Int(1).Buffer("S", 16, 0).Int(1).Int(0).ToArray()));
            Assert.Throws<InvalidDataException>(() => ShaderParameters.Read(new ShaderBlobWriter().UInt(ShaderParameters.Magic).Int(0).Int(1).String("x").Int(7).Int(0).Int(0).ToArray()));
        }

        [Fact]
        public void CommonParametersMergeWithTheBlob()
        {
            // The pass stage's common parameters hold FGlobals' members; the variant's blob names
            // the buffer without them, and adds its own texture.
            var common = new ShaderParameters();
            common.ConstantBuffers.Add(new ShaderConstantBuffer("FGlobals", 32, [new ShaderConstant("_Color", 0, 1, 4, false, 0, 0)]));
            common.Textures.Add(new ShaderTextureParameter("_MainTex", 0, 0, 2, false));
            common.ConstantBufferBindings.Add(new ShaderBufferParameter("FGlobals", 0, 1));
            var blob = ShaderParameters.Read(new ShaderBlobWriter().UInt(ShaderParameters.Magic).Int(2).Buffer("", 0, 0).Int(0)
                .Buffer("FGlobals", 20, 1).Member("_Cutoff", 0, 1, 1, false, 0, 16).Int(0)
                .Int(1).Texture("_AlphaTex", 1, 1, 2).ToArray());
            var p = ShaderParameters.Merge(common, blob);
            Assert.Equal(["FGlobals", ""], p.ConstantBuffers.Select(b => b.Name));
            Assert.Equal(["_Color", "_Cutoff"], p.Buffer("FGlobals")!.Members.Select(m => m.Name));
            // The variant's size: the common one is the pass's largest variant's.
            Assert.Equal(20, p.Buffer("FGlobals")!.Size);
            Assert.Equal(["_MainTex", "_AlphaTex"], p.Textures.Select(t => t.Name));
            Assert.Single(p.ConstantBufferBindings);
        }

        const string FragmentSource = "#include <metal_stdlib>\nusing namespace metal;\nfragment float4 xlatMtlMain() { return float4(1.0); }\n";

        [Fact]
        public void MetalEntryGivesSourceKeywordsAndVertexInputs()
        {
            var vertex = MetalProgram.Read(ShaderBlobWriter.MetalEntry(GpuProgramTypes.MetalVertex, ["VERTEXLIGHT_ON"], "vertex source\n", 0x11, [(0, 0), (4, 1)]));
            Assert.True(vertex.IsVertex);
            Assert.Equal(["VERTEXLIGHT_ON"], vertex.Keywords);
            Assert.Equal("xlatMtlMain", vertex.EntryPoint);
            Assert.Equal("vertex source\n", vertex.Source);
            Assert.Equal(0x11u, vertex.ChannelMask);
            Assert.Equal([new MetalVertexInput(0, 0), new MetalVertexInput(4, 1)], vertex.Inputs);
            Assert.Equal(unchecked((int)MetalProgram.PayloadMagic), vertex.PayloadHeader[0]);

            var fragment = MetalProgram.Read(ShaderBlobWriter.MetalEntry(GpuProgramTypes.MetalFragment, [], FragmentSource, 0, []));
            Assert.False(fragment.IsVertex);
            Assert.Equal(FragmentSource, fragment.Source);
            Assert.Empty(fragment.Inputs);
        }

        [Fact]
        public void NonMetalOrDamagedEntriesAreRefused()
        {
            var good = ShaderBlobWriter.MetalEntry(GpuProgramTypes.MetalFragment, [], FragmentSource, 0, []);
            Assert.Throws<InvalidDataException>(() => MetalProgram.Read([.. good, 1, 2, 3, 4]));
            var gl = (byte[])good.Clone();
            gl[4] = GpuProgramTypes.GLCore32;
            Assert.Throws<InvalidDataException>(() => MetalProgram.Read(gl));
            var noPayloadMagic = (byte[])good.Clone();
            noPayloadMagic[32] ^= 1;
            Assert.Throws<InvalidDataException>(() => MetalProgram.Read(noPayloadMagic));
        }

        [Fact]
        public void ProgramTableGivesEntriesInsideTheirSegment()
        {
            var table = new ShaderProgramTable([ShaderBlobWriter.Table([1, 2, 3], [4, 5])]);
            Assert.Equal(2, table.Count);
            Assert.Equal([4, 5], table.Get(1).ToArray());
            Assert.False(table.Contains(2));
            Assert.Throws<InvalidDataException>(() => table.Get(2));
            Assert.Equal(ShaderCompilerPlatform.Metal, GpuProgramTypes.PlatformOf(GpuProgramTypes.MetalVertex));
            Assert.Equal(ShaderCompilerPlatform.OpenGLCore, GpuProgramTypes.PlatformOf(GpuProgramTypes.GLCore43));
            Assert.Equal(ShaderCompilerPlatform.Vulkan, GpuProgramTypes.PlatformOf(GpuProgramTypes.SpirV));
            foreach (int type in new[] { 15, 16, 17, 18, 19, 20, 21, 22 }) Assert.Equal(ShaderCompilerPlatform.D3D11, GpuProgramTypes.PlatformOf(type));
            Assert.Equal(0u, GpuProgramTypes.PlatformOf(14)); // DX10Level9Pixel: not read
        }

        [Fact]
        public void MslStructsAreLaidOutByMetalsRules()
        {
            const string source = "struct FGlobals_Type\n{\n    float4 _Color;\n    float _Cutoff;\n    float _Weights[3];\n    float3 _Dir;\n    float _After;\n"
                + "    packed_float3 _Packed;\n    int _Count;\n    float2 _Uv;\n    float3 _Offsets[2];\n    float4 hlslcc_mtx4x4_M[4];\n};\n"
                + "struct Mtl_FragmentIn\n{\n    float2 TEXCOORD0 [[ user(TEXCOORD0) ]] ;\n};\n";
            var layouts = MslStructs.Layouts(source);
            var s = layouts["FGlobals_Type"];
            Assert.Equal([0, 16, 20, 32, 48, 52, 64, 72, 80, 112], s.Select(m => m.Offset));
            Assert.Equal(4, s[2].Stride);
            Assert.Equal(16, s[8].Stride);
            Assert.Equal(176, MslStructs.SizeOf(s));
            // A struct of other members (stage inputs) is not laid out.
            Assert.False(layouts.ContainsKey("Mtl_FragmentIn"));
        }

        /// Every Metal program and parameter blob of the macOS build decodes (the counts are the
        /// build's: docs/research/macos-feasibility.md §1.1).
        [Fact]
        public void EveryMetalProgramOfTheMacBuildDecodes()
        {
            var dataDir = GameData.MacDataDir;
            if (dataDir == null) Assert.Skip(NoMacData);
            int vertex = 0, fragment = 0, blobs = 0, shaders = 0;
            var failures = new List<string>();
            foreach (var shader in Shaders(dataDir))
            {
                var table = ShaderProgramTable.Load(shader, ShaderCompilerPlatform.Metal);
                if (table == null) continue;
                shaders++;
                var parsed = shader.Struct("m_ParsedForm");
                var keywords = parsed.Array("m_KeywordNames").Cast<string>().ToArray();
                foreach (var sub in parsed.Array("m_SubShaders").Cast<Fields>())
                    foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
                        foreach (var stage in PlayerSubprograms.Stages)
                            foreach (var x in PlayerSubprograms.Of(pass, stage, keywords).Where(x => x.Platform == ShaderCompilerPlatform.Metal))
                                try
                                {
                                    var program = MetalProgram.Read(table.Get(x.BlobIndex));
                                    if (program.IsVertex) vertex++;
                                    else fragment++;
                                    Assert.Equal(x.GpuProgramType, program.GpuProgramType);
                                    Assert.Equal(x.Keywords.Order(StringComparer.Ordinal), program.Keywords.Order(StringComparer.Ordinal));
                                    Assert.Contains("#include <metal_stdlib>", program.Source);
                                    if (x.ParameterBlobIndex >= 0)
                                    {
                                        ShaderParameters.Read(table.Get(x.ParameterBlobIndex));
                                        blobs++;
                                    }
                                }
                                catch (Exception e)
                                {
                                    failures.Add($"{parsed.String("m_Name")} {stage} [{string.Join(" ", x.Keywords)}]: {e.Message}");
                                }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
            Assert.Equal(120, shaders);
            Assert.Equal((522, 535), (vertex, fragment));
            Assert.Equal(1057, blobs);
        }

        /// Every Vulkan program of the build reads: its stages decode from SMOL-V to the sizes
        /// their headers record (a whole SPIR-V module each), one vertex and one fragment stage
        /// each, and its parameter blob reads.
        [Fact]
        public void EveryVulkanProgramDecodes()
        {
            int programs = 0, stages = 0, shaders = 0;
            var failures = new List<string>();
            foreach (var shader in Shaders(GameData.DataDir))
            {
                var table = ShaderProgramTable.Load(shader, ShaderCompilerPlatform.Vulkan);
                if (table == null) continue;
                shaders++;
                var parsed = shader.Struct("m_ParsedForm");
                var keywords = parsed.Array("m_KeywordNames").Cast<string>().ToArray();
                var seen = new HashSet<int>();
                foreach (var sub in parsed.Array("m_SubShaders").Cast<Fields>())
                    foreach (var pass in sub.Array("m_Passes").Cast<Fields>())
                        foreach (var x in PlayerSubprograms.Of(pass, "progVertex", keywords).Where(x => x.Platform == ShaderCompilerPlatform.Vulkan))
                        {
                            if (!seen.Add(x.BlobIndex)) continue;
                            try
                            {
                                var program = VulkanProgram.Read(table.Get(x.BlobIndex));
                                programs++;
                                stages += program.Stages.Length;
                                Assert.Equal(x.Keywords.Order(StringComparer.Ordinal), program.Keywords.Order(StringComparer.Ordinal));
                                Assert.Contains(program.Stages, s => s.Index == 0);
                                Assert.Contains(program.Stages, s => s.Index == 1);
                                foreach (var s in program.Stages)
                                    Assert.Equal(0x07230203u, BitConverter.ToUInt32(s.Spirv));
                                if (x.ParameterBlobIndex >= 0) ShaderParameters.Read(table.Get(x.ParameterBlobIndex));
                            }
                            catch (Exception e)
                            {
                                failures.Add($"{parsed.String("m_Name")} [{string.Join(" ", x.Keywords)}]: {e.Message}");
                            }
                        }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
            Assert.Equal(120, shaders);
            Assert.Equal(539, programs);
            Assert.Equal(1079, stages);
        }

        internal const string NoMacData = "macOS game data not found (set ONI_MAC_APP to OxygenNotIncluded.app; default " + GameData.DefaultMacApp + ").";

        internal static IEnumerable<Fields> Shaders(string dataDir)
        {
            foreach (var name in GameData.ShaderFiles)
            {
                var file = SerializedFile.Open(Path.Combine(dataDir, name));
                foreach (var info in file.Objects)
                {
                    var type = file.TypeOf(info);
                    if (type.ClassId != 48) continue;
                    yield return type.Tree != null ? TypeTreeReader.ReadObject(type.Tree, file.OpenObject(info), TypeTreeDatabase.TrailingBytes(48)) : TypeTreeReader.ReadNative(48, file.OpenObject(info));
                }
            }
        }
    }
}
