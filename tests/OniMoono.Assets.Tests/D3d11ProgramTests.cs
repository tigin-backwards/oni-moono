using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Shaders;
using OniMoono.Assets.TypeTrees;
using Xunit;

namespace OniMoono.Assets.Tests
{
    /// The D3D11 program entry reader, the DXBC container reader and the binding check, on real
    /// entries of U60's Windows data (Data/d3d11: each program entry with its platform-4
    /// parameter blob) and on damaged copies of them: every failure is an InvalidDataException.
    public class D3d11ProgramTests
    {
        static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Data", "d3d11");

        static byte[] Entry(string name) => File.ReadAllBytes(Path.Combine(Dir, name + ".entry.bin"));

        static ShaderParameters Params(string name) => ShaderParameters.Read(File.ReadAllBytes(Path.Combine(Dir, name + ".params.bin")));

        static readonly string[] Fixtures = ["fullscreen-vs", "instancing-vs", "uirdefault-ps", "debugpattern-ps", "vrblit-gs", "tmp-sdf-ps"];

        /// Where the DXBC container starts in an entry (after the header, keywords, payload length
        /// and Unity prefix).
        static int DxbcOffset(byte[] entry)
        {
            int pos = 24, keywords = BitConverter.ToInt32(entry, pos);
            pos += 4;
            if (keywords is < 0 or > 64) throw new ArgumentException("not a fixture's keyword count");
            for (int i = 0; i < keywords; i++)
            {
                int len = BitConverter.ToInt32(entry, pos);
                if (len is < 0 or > 256) throw new ArgumentException("not a fixture's keyword");
                pos += 4 + ((len + 3) & ~3);
            }
            return pos + 4 + D3d11Program.PrefixBytes;
        }

        /// The entry with its DXBC checksum recomputed, so that a change inside the container
        /// reaches the parts after the checksum check.
        static byte[] Resealed(byte[] entry)
        {
            int at = DxbcOffset(entry);
            int size = BitConverter.ToInt32(entry, at + 24);
            DxbcChecksum.Compute(entry.AsSpan(at, size), entry.AsSpan(at + 4, 16));
            return entry;
        }

        static void Put(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

        [Fact]
        public void AVertexProgramReads()
        {
            // Klei/PostFX/FullScreen's vertex program (programs.md §2's example).
            var p = D3d11Program.Read(Entry("fullscreen-vs"));
            Assert.Equal(GpuProgramTypes.D3D11VertexSM40, p.GpuProgramType);
            Assert.Empty(p.Keywords);
            Assert.Equal(new byte[] { 2, 0, 0, 0 }, p.Prefix[..4]);
            Assert.Equal(348, p.Dxbc.Bytes.Length);
            Assert.Equal(["ISGN", "OSGN", "SHDR"], p.Dxbc.Chunks.Select(c => c.Name));
            Assert.Equal((DxbcShader.VertexProgram, 4, 0), (p.Dxbc.Shader.ProgramType, p.Dxbc.Shader.Major, p.Dxbc.Shader.Minor));
            Assert.Equal(["POSITION", "TEXCOORD"], p.Dxbc.Inputs.Select(e => e.SemanticName));
            Assert.Equal(new DxbcSignatureElement("TEXCOORD", 0, 0, 3, 1, 0x3, 0x3), p.Dxbc.Inputs[1]);
            Assert.Equal(1u, p.Dxbc.Outputs[0].SystemValue); // D3D_NAME_POSITION
            Assert.Equal(0x11u, p.ChannelMask);
            Assert.Equal([(0, 0), (4, 5)], p.Inputs);
            Assert.Empty(D3d11Bindings.From(Params("fullscreen-vs")).Check(p));
            // The container is kept byte for byte.
            var entry = Entry("fullscreen-vs");
            Assert.Equal(entry.AsSpan(DxbcOffset(entry), 348).ToArray(), p.Dxbc.Bytes);
        }

        [Fact]
        public void DeclarationsAndBindingsAgree()
        {
            // An SM5 vertex program reading a structured buffer (Klei/Standard Unlit, PROCEDURAL_INSTANCING_ON).
            var p = D3d11Program.Read(Entry("instancing-vs"));
            Assert.Equal((DxbcShader.VertexProgram, 5), (p.Dxbc.Shader.ProgramType, p.Dxbc.Shader.Major));
            Assert.Equal("SHEX", p.Dxbc.Chunks[2].Name);
            Assert.Equal([new DxbcResourceDeclaration(0, 12, 0, 0, 56)], p.Dxbc.Shader.Resources);
            Assert.Equal([0, 1, 2], p.Dxbc.Shader.ConstantBuffers.Select(c => c.Slot));
            Assert.Equal(8u, p.Dxbc.Inputs[4].SystemValue); // SV_InstanceID: D3D_NAME_INSTANCE_ID
            var b = D3d11Bindings.From(Params("instancing-vs"));
            Assert.Equal([("$Globals", 0, 208), ("UnityPerFrame", 1, 368), ("UnityDrawCallInfo", 2, 16)], b.ConstantBuffers.Select(c => (c.Name, c.Slot, c.Size)));
            Assert.Equal("unity_ParticleInstanceData", b.StructuredBuffers.Single().Name);
            Assert.Empty(b.Check(p));

            // Minimum-precision signatures (ISG1/OSG1) and SFI0 (Hidden/Internal-UIRDefault).
            var ui = D3d11Program.Read(Entry("uirdefault-ps"));
            Assert.Equal(DxbcContainer.ISG1, ui.Dxbc.InputSignatureTag);
            Assert.Equal(DxbcContainer.OSG1, ui.Dxbc.OutputSignatureTag);
            Assert.Equal(0x10ul, ui.Dxbc.Features); // D3D_SHADER_FEATURE_MINIMUM_PRECISION
            Assert.True(ui.Dxbc.Shader.ConstantBuffers.Single().DynamicIndexed);
            Assert.Equal(["_RENDER_TYPE_TEXT", "_TEXTURE_SLOT_COUNT_1"], ui.Keywords.Select(k => k["_UIE".Length..]).Order());
            var uib = D3d11Bindings.From(Params("uirdefault-ps"));
            Assert.Equal([("_ShaderInfoTex", 0, -1), ("_Texture0", 1, 0)], uib.Textures.Select(t => (t.Name, t.Slot, t.Sampler)));
            Assert.Empty(uib.Check(ui));
        }

        [Fact]
        public void OtherStagesAndChunksRead()
        {
            // The one geometry program (Hidden/VR/BlitFromTex2DToTexArraySlice): prefix byte 5 is 3,
            // kept raw.
            var gs = D3d11Program.Read(Entry("vrblit-gs"));
            Assert.Equal((GpuProgramTypes.D3D11GeometrySM40, DxbcShader.GeometryProgram), (gs.GpuProgramType, gs.Dxbc.Shader.ProgramType));
            Assert.Equal(3, gs.Prefix[5]);
            Assert.Equal(4u, gs.Dxbc.Outputs[2].SystemValue); // D3D_NAME_RENDER_TARGET_ARRAY_INDEX
            Assert.Equal(0u, gs.ChannelMask);
            Assert.Empty(gs.Inputs);
            // SV_Depth: an output without a register (Hidden/Internal-DebugPattern).
            var depth = D3d11Program.Read(Entry("debugpattern-ps")).Dxbc.Outputs.Single(o => o.SemanticName == "SV_Depth");
            Assert.Equal(0xFFFFFFFFu, depth.Register);
            // RDEF and STAT kept as chunks (TextMeshPro/Mobile/Distance Field).
            var tmp = D3d11Program.Read(Entry("tmp-sdf-ps"));
            Assert.True(tmp.Dxbc.Has(DxbcContainer.RDEF) && tmp.Dxbc.Has(DxbcContainer.STAT));
            Assert.Equal(DxbcShader.PixelProgram, tmp.Dxbc.Shader.ProgramType);
        }

        [Fact]
        public void TheCheckFindsDisagreements()
        {
            var p = D3d11Program.Read(Entry("uirdefault-ps"));
            ShaderParameters Changed(Action<ShaderParameters> change)
            {
                var x = Params("uirdefault-ps");
                change(x);
                return x;
            }
            Assert.Equal(["texture _Texture0 t5 is not declared", "declared t1 has no binding"], D3d11Bindings.From(Changed(x => x.Textures[1] = x.Textures[1] with { Index = 5 })).Check(p));
            Assert.Contains("declared t1 has no binding", D3d11Bindings.From(Changed(x => x.Textures.RemoveAt(1))).Check(p));
            Assert.Contains("declared s0 has no texture or inline sampler using it", D3d11Bindings.From(Changed(x => x.Textures[1] = x.Textures[1] with { SamplerIndex = -1 })).Check(p));
            Assert.Contains("texture _Texture0 t1 is dimension 4, declared as resource dimension 3", D3d11Bindings.From(Changed(x => x.Textures[1] = x.Textures[1] with { Dimension = 4 })).Check(p));
            Assert.Contains("declared b0 has no binding", D3d11Bindings.From(Changed(x => x.ConstantBufferBindings.Clear())).Check(p));
            Assert.Contains("constant buffer $Globals b0 is declared as 4 vectors, Unity's size is 48 bytes",
                D3d11Bindings.From(Changed(x => { int i = x.ConstantBuffers.FindIndex(c => c.Name == "$Globals"); x.ConstantBuffers[i] = x.ConstantBuffers[i] with { Size = 48 }; })).Check(p));
            // An inline sampler accounts for a declared sampler.
            Assert.Empty(D3d11Bindings.From(Changed(x =>
            {
                x.Textures[1] = x.Textures[1] with { SamplerIndex = -1 };
                x.Samplers.Add(new ShaderSamplerParameter(84, 0));
            })).Check(p));
            Assert.Throws<InvalidDataException>(() => D3d11Bindings.From(Changed(x => x.ConstantBuffers.RemoveAll(c => c.Name == "$Globals"))));
            // The prefix's counts.
            var entry = Entry("uirdefault-ps");
            entry[DxbcOffset(entry) - D3d11Program.PrefixBytes + 1] = 3;
            Assert.Contains("prefix SRV count 3, 2 declared", D3d11Bindings.From(Params("uirdefault-ps")).Check(D3d11Program.Read(entry)));
        }

        [Fact]
        public void DamagedEntriesAreRefused()
        {
            var good = Entry("fullscreen-vs");
            byte[] With(Action<byte[]> change)
            {
                var e = (byte[])good.Clone();
                change(e);
                return e;
            }
            int dxbc = DxbcOffset(good);
            void Refused(byte[] e, string message) => Assert.Contains(message, Assert.Throws<InvalidDataException>(() => D3d11Program.Read(e)).Message);
            Refused(With(e => Put(e, 0, 0x0C07513C)), "Not a shader program entry");
            Refused(With(e => Put(e, 4, (uint)GpuProgramTypes.SpirV)), "is not a D3D11 program");
            Refused(With(e => Put(e, 4, (uint)GpuProgramTypes.D3D11PixelSM40)), "GPU program type 17, but the DXBC is program type 1");
            Refused(With(e => Put(e, 24, 0x7FFFFFFF)), "bad keyword count");
            Refused(With(e => Put(e, dxbc - 42, 0x7FFFFFF0)), "bad payload length");
            Refused(With(e => Put(e, dxbc - 42, 40)), "bad payload length");
            Refused(With(e => Put(e, dxbc + 24, 340)), "DXBC of 340 bytes");
            Refused(good.Concat(new byte[4]).ToArray(), "bytes after the vertex inputs");
            Refused(With(e => Put(e, good.Length - 20, 1000)), "bad vertex input count");
            // The checksum, and each byte the checksum covers.
            Refused(With(e => e[dxbc + 4] ^= 1), "bad checksum");
            Refused(With(e => e[dxbc + 100] ^= 1), "bad checksum");
            Refused(With(e => e[dxbc + 347] ^= 0x80), "bad checksum");
        }

        [Fact]
        public void DamagedContainersAreRefused()
        {
            var good = Entry("fullscreen-vs");
            int dxbc = DxbcOffset(good);
            byte[] Sealed(Action<byte[]> change)
            {
                var e = (byte[])good.Clone();
                change(e);
                return Resealed(e);
            }
            void Refused(byte[] e, string message) => Assert.Contains(message, Assert.Throws<InvalidDataException>(() => D3d11Program.Read(e)).Message);
            Assert.NotNull(D3d11Program.Read(Sealed(_ => { })));
            Refused(Sealed(e => e[dxbc] = (byte)'X'), "bad magic");
            Refused(Sealed(e => Put(e, dxbc + 20, 2)), "version 2");
            // Chunk table: count, offsets and sizes past the container.
            Refused(Sealed(e => Put(e, dxbc + 28, 0x40000000)), "chunks do not fit");
            Refused(Sealed(e => Put(e, dxbc + 32, 348)), "outside the container");
            Refused(Sealed(e => Put(e, dxbc + 32, 0xFFFFFFFF)), "outside the container");
            Refused(Sealed(e => Put(e, dxbc + 32, 344)), "outside the container");
            int isgn = BitConverter.ToInt32(good, dxbc + 32);
            Refused(Sealed(e => Put(e, dxbc + isgn + 4, 0x7FFFFFFF)), "ends past the container");
            // Required chunks: a second code chunk, a missing input signature.
            int osgn = BitConverter.ToInt32(good, dxbc + 36), shdr = BitConverter.ToInt32(good, dxbc + 40);
            Refused(Sealed(e => Put(e, dxbc + 36, (uint)shdr)), "both present");
            Refused(Sealed(e => Put(e, dxbc + isgn, DxbcContainer.Tag("XXXX"))), "no input signature");
            Refused(Sealed(e => Put(e, dxbc + shdr, DxbcContainer.Tag("XXXX"))), "no SHDR or SHEX");
            // Signature elements and names.
            Refused(Sealed(e => Put(e, dxbc + isgn + 8, 1000)), "signature elements do not fit");
            Refused(Sealed(e => Put(e, dxbc + isgn + 12, 6)), "signature elements at 6");
            Refused(Sealed(e => Put(e, dxbc + isgn + 16, 5000)), "name at 5000 is outside its chunk");
            Refused(Sealed(e => e.AsSpan(dxbc + osgn + 8 + 56, 24).Fill((byte)'A')), "not terminated");
            // The token stream: program length, an instruction running past it, length 0, a shader model 5.1.
            int code = dxbc + shdr + 8;
            Refused(Sealed(e => Put(e, code + 4, 1000)), "program length 1000");
            Refused(Sealed(e => Put(e, code + 8, BitConverter.ToUInt32(good, code + 8) | 0x1F000000)), "runs past the program");
            Refused(Sealed(e => Put(e, code + 8, BitConverter.ToUInt32(good, code + 8) & 0x00FFFFFF)), "of length 0");
            Refused(Sealed(e => Put(e, code, 0x00010051)), "shader model 5.1 is not supported");
        }

        [Fact]
        public void DeclarationsInTheWrongFormAreRefused()
        {
            // uirdefault-ps declares cb0, s0, t0 and t1 first: dcl_globalFlags, dcl_constantbuffer,
            // dcl_sampler, dcl_resource × 2.
            var good = Entry("uirdefault-ps");
            int dxbc = DxbcOffset(good);
            var chunks = D3d11Program.Read(good).Dxbc.Chunks;
            int code = dxbc + chunks.Single(c => c.Name == "SHEX").Offset;
            var tokens = Enumerable.Range(0, 24).Select(i => BitConverter.ToUInt32(good, code + 4 * i)).ToArray();
            int At(int opcode)
            {
                for (int i = 2; ; i += (int)((tokens[i] >> 24) & 0x7F))
                    if ((tokens[i] & 0x7FF) == opcode) return code + 4 * i;
            }
            byte[] Sealed(Action<byte[]> change)
            {
                var e = (byte[])good.Clone();
                change(e);
                return Resealed(e);
            }
            void Refused(byte[] e, string message) => Assert.Contains(message, Assert.Throws<InvalidDataException>(() => D3d11Program.Read(e)).Message);
            int cb = At(89), sampler = At(90), resource = At(88);
            Refused(Sealed(e => Put(e, cb + 4, BitConverter.ToUInt32(good, cb + 4) & ~0xFF000u | (7u << 12))), "dcl_constantbuffer operand of type 7");
            Refused(Sealed(e => Put(e, cb + 4, BitConverter.ToUInt32(good, cb + 4) & ~0x300000u | (1u << 20))), "index dimension 1");
            Refused(Sealed(e => Put(e, cb + 4, BitConverter.ToUInt32(good, cb + 4) | (2u << 22))), "non-immediate indices");
            Refused(Sealed(e => Put(e, sampler + 4, BitConverter.ToUInt32(good, sampler + 4) | 0x80000000)), "non-immediate indices or an extension");
            Refused(Sealed(e => Put(e, resource, BitConverter.ToUInt32(good, resource) & ~0xF800u)), "dcl_resource t0 of dimension 0");
            Refused(Sealed(e => Put(e, resource + 8, 1)), "resource slot 1 declared twice");
            Refused(Sealed(e => Put(e, resource + 8, 0x80000000)), "dcl_resource index 2147483648");
        }

        /// What no program of U60's data or mods has is refused, each with what it is.
        [Fact]
        public void UnsupportedFormsAreRefused()
        {
            void Refused(byte[] e, string message) => Assert.Contains(message, Assert.Throws<InvalidDataException>(() => D3d11Program.Read(e)).Message);
            byte[] Sealed(byte[] good, Action<byte[]> change)
            {
                var e = (byte[])good.Clone();
                change(e);
                return Resealed(e);
            }
            // GPU program types 20-22, shader model 4.1, OSG5, a component type past FLOAT32.
            var vs = Entry("fullscreen-vs");
            int dxbc = DxbcOffset(vs);
            foreach (uint type in new uint[] { 20, 21, 22 }) Refused(Sealed(vs, e => Put(e, 4, type)), $"GPU program type {type} is not supported");
            int isgn = BitConverter.ToInt32(vs, dxbc + 32), osgn = BitConverter.ToInt32(vs, dxbc + 36), shdr = BitConverter.ToInt32(vs, dxbc + 40);
            Refused(Sealed(vs, e => Put(e, dxbc + shdr + 8, 0x00010041)), "shader model 4.1 is not supported");
            Refused(Sealed(vs, e => Put(e, dxbc + osgn, DxbcContainer.OSG5)), "OSG5 output signatures are not supported");
            Refused(Sealed(vs, e => Put(e, dxbc + isgn + 8 + 8 + 24 + 12, 7)), "signature element 1 has component type 7");
            // ISG1's stream and minimum precision (uirdefault-ps's first input element).
            var ps = Entry("uirdefault-ps");
            int pdx = DxbcOffset(ps);
            var chunks = D3d11Program.Read(ps).Dxbc.Chunks;
            int isg1 = pdx + chunks.Single(c => c.Name == "ISG1").Offset;
            Refused(Sealed(ps, e => Put(e, isg1 + 8, 1)), "has stream 1 and minimum precision 0");
            Refused(Sealed(ps, e => Put(e, isg1 + 8 + 28, 1)), "has stream 0 and minimum precision 1");
            // Its declarations: dcl_globalFlags (one DWORD) first, then cb0, s0, t0, t1.
            int code = pdx + chunks.Single(c => c.Name == "SHEX").Offset;
            var tokens = Enumerable.Range(0, 24).Select(i => BitConverter.ToUInt32(ps, code + 4 * i)).ToArray();
            Assert.Equal(106u | (1u << 24), tokens[2] & 0xFF0007FF);
            int At(int opcode)
            {
                for (int i = 2; ; i += (int)((tokens[i] >> 24) & 0x7F))
                    if ((tokens[i] & 0x7FF) == opcode) return code + 4 * i;
            }
            int flags = code + 8, sampler = At(90), resource = At(88);
            int[] declarations = [99, 102, 113, .. Enumerable.Range(143, 19), 206];
            foreach (int opcode in declarations) Refused(Sealed(ps, e => Put(e, flags, (uint)opcode | (1u << 24))), $"({opcode}) is not supported");
            Refused(Sealed(ps, e => Put(e, flags, 156u | (1u << 24))), "D3D11_SB_OPCODE_DCL_UNORDERED_ACCESS_VIEW_TYPED");
            Refused(Sealed(ps, e => Put(e, flags, 161u | (1u << 24))), "D3D11_SB_OPCODE_DCL_RESOURCE_RAW");
            Refused(Sealed(ps, e => Put(e, flags, 53u)), "custom-data block of class 0 is not supported");
            Refused(Sealed(ps, e => Put(e, flags, tokens[2] | 0x20000000)), "length bits 29-30");
            Refused(Sealed(ps, e => Put(e, flags, tokens[2] | 0x40000000)), "length bits 29-30");
            Refused(Sealed(ps, e => Put(e, sampler, BitConverter.ToUInt32(ps, sampler) | 0x80000000)), "extended opcode token is not supported");
            Refused(Sealed(ps, e => Put(e, sampler, BitConverter.ToUInt32(ps, sampler) | (2u << 11))), "dcl_sampler s0 of mode 2 is not supported");
            foreach (uint dimension in new uint[] { 1, 2, 7, 10, 11, 12 })
                Refused(Sealed(ps, e => Put(e, resource, BitConverter.ToUInt32(ps, resource) & ~0xF800u | (dimension << 11))), $"of dimension {dimension} is not supported");
        }

        /// Every prefix of each fixture, and many single-byte changes of each (with the checksum
        /// recomputed, so they reach the parsing behind it), either read or fail with
        /// InvalidDataException.
        [Fact]
        public void TruncationAndBitFlipsFailCleanly()
        {
            foreach (var name in Fixtures)
            {
                var entry = Entry(name);
                for (int n = 0; n < entry.Length; n++)
                    Assert.Throws<InvalidDataException>(() => D3d11Program.Read(entry.AsSpan(0, n)));
                for (int i = 0; i < entry.Length; i++)
                    foreach (byte v in new byte[] { 0x00, 0xFF, 0x80, (byte)(entry[i] ^ 0x01) })
                    {
                        var copy = (byte[])entry.Clone();
                        copy[i] = v;
                        ReadsOrRefuses(copy);
                        try { Resealed(copy); }
                        catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException) { continue; } // the change broke the container's own size
                        ReadsOrRefuses(copy);
                    }
            }
        }

        static void ReadsOrRefuses(byte[] entry)
        {
            try
            {
                var p = D3d11Program.Read(entry);
                D3d11Bindings.From(new ShaderParameters()).Check(p);
            }
            catch (InvalidDataException)
            {
            }
        }

        [Fact]
        public void TheChecksumIsDxbcsMd5Variant()
        {
            // Every fixture's stored checksum is what the algorithm computes (1,612 containers of
            // the game and mods agree in the survey).
            foreach (var name in Fixtures)
            {
                var entry = Entry(name);
                int at = DxbcOffset(entry), size = BitConverter.ToInt32(entry, at + 24);
                var digest = new byte[16];
                DxbcChecksum.Compute(entry.AsSpan(at, size), digest);
                Assert.Equal(entry.AsSpan(at + 4, 16).ToArray(), digest);
            }
            // Known answers: a container of 20 + n bytes, byte i = (i * 31 + 7) & 0xff, digested
            // from byte 20. The digests are vkd3d-shader's own (checksum.c at 698cbfbd, built
            // with gcc and a stub vkd3d_shader_private.h, vkd3d_compute_md5(VKD3D_MD5_DXBC)), and
            // equal an independent Python translation of it (the M1 review's scan.py). n mod 64
            // covers one final block (1, 55, 64, 119), the extra block (56, 57, 59, 60, 63, 120,
            // 124, 127) and both DWORD-aligned extra-block shapes, 56 and 60.
            foreach (var (n, hex) in KnownDigests)
            {
                var container = Enumerable.Range(0, 20 + n).Select(i => (byte)((i * 31 + 7) & 0xff)).ToArray();
                var digest = new byte[16];
                DxbcChecksum.Compute(container, digest);
                Assert.Equal(hex, Convert.ToHexStringLower(digest));
            }
        }

        static readonly (int, string)[] KnownDigests =
        [
            (1, "70e46b0e5cad2cfd8a57bc8c8e6b92c5"), (55, "9c6e2c79bbdd25cb54ad5791b2cd3626"), (56, "dbc73c7aa1dccf708d027aaf6a9689bd"),
            (57, "53ac048fb8c2d4acba6e1c3d33251852"), (59, "9aedc5c4952ed33e2835d192ecb2941d"), (60, "4f74a3fdb50658e9154b54148b6c408a"),
            (63, "fbf50823a1cd6527357dbf2d1683afda"), (64, "639827252b1d77a80212c2fba239bbb9"), (119, "5be3cc1ca4839ca22ddf5ea459a0bb21"),
            (120, "7c0d69ced000d16a1e54d24a4eb78144"), (124, "7531e5707d054c189a821cb753495dcd"), (127, "8be7106d490093afb8a24ae49d49b60a"),
            (128, "55800099ae6e679e56a3c6548ce8a3e4"),
        ];

        /// A pass with D3D11 programs keyed per stage: VERTEXLIGHT_ON only splits the vertex
        /// programs and _ALPHABLEND_ON only the fragment ones.
        static Fields Pass()
        {
            Fields Sub(int type, uint blob, params ushort[] keywords)
            {
                var f = new Fields();
                f.Add("m_BlobIndex", blob);
                f.Add("m_GpuProgramType", (sbyte)type);
                f.Add("m_KeywordIndices", keywords);
                return f;
            }
            Fields Stage(params Fields[] subs)
            {
                var f = new Fields();
                f.Add("m_PlayerSubPrograms", new object?[] { Array.Empty<object?>(), subs.Cast<object?>().ToArray() });
                f.Add("m_ParameterBlobIndices", new object?[] { Array.Empty<uint>(), subs.Select((_, i) => (uint)(100 + i)).ToArray() });
                return f;
            }
            var pass = new Fields();
            // Keywords: 0 VERTEXLIGHT_ON, 1 _ALPHABLEND_ON. Vulkan's one program per union is listed too.
            pass.Add("progVertex", Stage(Sub(15, 1), Sub(15, 2, 0), Sub(25, 7), Sub(25, 8, 0), Sub(25, 9, 1), Sub(25, 10, 0, 1)));
            pass.Add("progFragment", Stage(Sub(17, 3), Sub(18, 4, 1)));
            return pass;
        }

        [Fact]
        public void EachStageIsSelectedByItsOwnKeywords()
        {
            string[] names = ["VERTEXLIGHT_ON", "_ALPHABLEND_ON"];
            var v = D3d11Variants.Of(Pass(), names);
            Assert.Equal([1, 2], v.Vertex.Select(x => x.BlobIndex));
            Assert.Equal([3, 4], v.Fragment.Select(x => x.BlobIndex));
            Assert.Empty(v.Geometry);
            Assert.Equal([100, 101], v.Vertex.Select(x => x.ParameterBlobIndex));
            (int?, int?) Selected(params string[] enabled)
            {
                var s = v.Select(enabled.Contains);
                return (s[0]?.BlobIndex, s[1]?.BlobIndex);
            }
            Assert.Equal((2, 4), Selected("VERTEXLIGHT_ON", "_ALPHABLEND_ON"));
            Assert.Equal((1, 4), Selected("_ALPHABLEND_ON"));
            Assert.Equal((2, 3), Selected("VERTEXLIGHT_ON"));
            Assert.Equal((1, 3), Selected());
            Assert.Equal((1, 3), Selected("UNKNOWN"));
            Assert.Null(v.Select(_ => true)[2]);
            Assert.Equal(2, VariantSelection.SelectIndex<string[]>([["A", "X"], ["A"], ["A", "B"], ["A", "B"]], static k => k, k => k is "A" or "B"));
        }
    }
}
