using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class RenderingDataTests : EngineTest
    {
        [Theory]
        [InlineData(TextureFormat.RGBA32)]
        [InlineData(TextureFormat.ARGB32)]
        [InlineData(TextureFormat.BGRA32)]
        [InlineData(TextureFormat.RGBAHalf)]
        [InlineData(TextureFormat.RGBAFloat)]
        public void RgbaFormatsRoundTrip(TextureFormat format)
        {
            TextureFormats.TryGetBlock(format, out _, out _, out int bpp);
            var texel = new byte[bpp];
            var c = new Color(0.2f, 0.4f, 0.6f, 0.8f);
            TextureFormats.Encode(format, texel, 1, 0, 0, c);
            var back = TextureFormats.Decode(format, texel, 1, 0, 0);
            Assert.Equal(c.r, back.r, 2);
            Assert.Equal(c.g, back.g, 2);
            Assert.Equal(c.b, back.b, 2);
            Assert.Equal(c.a, back.a, 2);
        }

        [Fact]
        public void SingleChannelFormatsReadLikeAShader()
        {
            var texel = new byte[1];
            TextureFormats.Encode(TextureFormat.Alpha8, texel, 1, 0, 0, new Color(0f, 0f, 0f, 0.5f));
            Assert.Equal(new Color(1f, 1f, 1f, 128 / 255f), TextureFormats.Decode(TextureFormat.Alpha8, texel, 1, 0, 0));
            TextureFormats.Encode(TextureFormat.R8, texel, 1, 0, 0, new Color(1f, 0.3f, 0.3f, 0.3f));
            Assert.Equal(new Color(1f, 0f, 0f, 1f), TextureFormats.Decode(TextureFormat.R8, texel, 1, 0, 0));
        }

        [Fact]
        public void Dxt1BlockDecodes()
        {
            // color0 = pure red (0xF800) > color1 = pure blue (0x001F): four-colour mode.
            // Texel indices (2 bits each, row-major from the low bits): 0, 1, 2, 3, then zeros.
            byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0b11_10_01_00, 0, 0, 0];
            Assert.Equal(new Color(1f, 0f, 0f, 1f), TextureFormats.Decode(TextureFormat.DXT1, block, 4, 0, 0));
            Assert.Equal(new Color(0f, 0f, 1f, 1f), TextureFormats.Decode(TextureFormat.DXT1, block, 4, 1, 0));
            var twoThirdsRed = TextureFormats.Decode(TextureFormat.DXT1, block, 4, 2, 0);
            Assert.Equal(2f / 3f, twoThirdsRed.r, 4);
            Assert.Equal(1f / 3f, twoThirdsRed.b, 4);
        }

        [Fact]
        public void Texture2DLayoutPixelsAndMips()
        {
            var t = new Texture2D(4, 4);
            Assert.Equal(3, t.mipmapCount);
            Assert.Equal(16 * 4 + 4 * 4 + 4, t.GetRawTextureData().Length);

            var pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32((byte)(i * 16), 0, 0, 255);
            t.SetPixels32(pixels);
            // Repeat wrapping by default.
            Assert.Equal(t.GetPixel(3, 0), t.GetPixel(-1, 0));
            t.wrapMode = TextureWrapMode.Clamp;
            Assert.Equal(t.GetPixel(0, 0), t.GetPixel(-5, 0));

            t.Apply();
            // Level 1 texel (0, 0) averages texels (0..1, 0..1) of level 0: (0 + 16 + 64 + 80) / 4.
            Assert.Equal(40, ((Color32)t.GetPixel(0, 0, 1)).r);
        }

        [Fact]
        public void RawTextureDataAliasesTexels()
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var raw = t.GetRawTextureData<byte>();
            raw[0] = 255;
            raw[3] = 255;
            Assert.Equal(1f, t.GetPixel(0, 0).r);
            Assert.Equal(4, t.GetPixelData<int>(0).Length);
        }

        [Fact]
        public void BilinearSamplesBetweenTexelCentres()
        {
            var t = new Texture2D(2, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32([new Color32(0, 0, 0, 255), new Color32(255, 0, 0, 255)]);
            Assert.Equal(0.5f, t.GetPixelBilinear(0.5f, 0.5f).r, 3);
            Assert.Equal(0f, t.GetPixelBilinear(0.25f, 0.5f).r, 3);
        }

        [Fact]
        public void NonReadableTexturesRejectPixelAccess()
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.Apply(false, true);
            Assert.False(t.isReadable);
            Assert.Throws<UnityException>(() => t.GetPixel(0, 0));
        }

        [Fact]
        public void MeshValidatesIndicesAgainstVertices()
        {
            var mesh = new Mesh();
            mesh.vertices = [Vector3.zero, Vector3.right, Vector3.up];
            mesh.triangles = [0, 1, 2];
            Assert.Equal(new Bounds(new Vector3(0.5f, 0.5f, 0f), new Vector3(1f, 1f, 0f)), mesh.bounds);

            // Indices beyond the vertices are rejected and the mesh keeps its triangles.
            mesh.triangles = [0, 1, 3];
            Assert.Equal(new[] { 0, 1, 2 }, mesh.triangles);

            // So is shrinking the vertices below what the triangles reference.
            mesh.vertices = [Vector3.zero];
            Assert.Equal(3, mesh.vertexCount);

            // Other channels must match the vertex count.
            mesh.colors32 = [new Color32(1, 2, 3, 4)];
            Assert.Empty(mesh.colors32);
            mesh.colors32 = [new Color32(1, 2, 3, 4), new Color32(5, 6, 7, 8), new Color32(9, 10, 11, 12)];
            Assert.Equal(new Color32(5, 6, 7, 8), mesh.colors32[1]);
            // Colours given in bytes read back as floats too, and float colours as bytes.
            Assert.Equal((Color)new Color32(9, 10, 11, 12), mesh.colors[2]);
            mesh.colors = [Color.red, Color.green, Color.blue];
            Assert.Equal(new Color32(0, 0, 255, 255), mesh.colors32[2]);

            mesh.Clear();
            Assert.Equal(0, mesh.vertexCount);
            Assert.Equal(1, mesh.subMeshCount);
        }

        // Measured in the original player (UnityProbe meshshrink: clear.vertices4,
        // clearKeep.vertices4, clearNoKeep.vertices4, clear.vertices12, clear).
        static Mesh TwoQuads()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[8];
            mesh.normals = Enumerable.Repeat(new Vector3(0f, 0f, -1f), 8).ToArray();
            mesh.uv = Enumerable.Repeat(new Vector2(0.25f, 0.75f), 8).ToArray();
            mesh.colors32 = Enumerable.Repeat(new Color32(255, 255, 255, 255), 8).ToArray();
            mesh.triangles = [0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4];
            return mesh;
        }

        [Fact]
        public void MeshClearKeepsTheVertexLayoutUnlessToldNot()
        {
            // Clear() and Clear(true): the channels come back with the next vertices, which a
            // mesh that still held the old triangles would have refused.
            foreach (var keep in new bool?[] { null, true })
            {
                var mesh = TwoQuads();
                if (keep == null) mesh.Clear(); else mesh.Clear(true);
                Assert.Equal(0, mesh.vertexCount);
                Assert.Empty(mesh.normals);
                Assert.Empty(mesh.uv);
                Assert.Empty(mesh.colors32);
                Assert.Empty(mesh.triangles);
                mesh.vertices = new Vector3[4];
                Assert.Equal(4, mesh.vertexCount);
                Assert.Equal(4, mesh.normals.Length);
                Assert.Equal(4, mesh.uv.Length);
                Assert.Equal(4, mesh.colors32.Length);
                Assert.Empty(mesh.tangents);
                mesh.vertices = new Vector3[12];
                Assert.Equal(12, mesh.normals.Length);
                Assert.Equal(12, mesh.colors32.Length);
            }

            // Kept channels left unread are not made: a cloned mesh keeps them pending too.
            var pending = TwoQuads();
            pending.Clear();
            pending.vertices = new Vector3[4];
            var copy = Object.Instantiate(pending);
            Assert.Equal(4, copy.uv.Length);

            // Clear(false) drops the channels.
            var bare = TwoQuads();
            bare.Clear(false);
            bare.vertices = new Vector3[4];
            Assert.Equal(4, bare.vertexCount);
            Assert.Empty(bare.normals);
            Assert.Empty(bare.uv);
            Assert.Empty(bare.colors32);
        }

        // uGUI rebuilds a dirty graphic's mesh as Clear, SetVertices, then every channel: the
        // channels a Clear keeps must not be made at the new count only to be replaced.
        [Fact]
        public void MeshRebuiltAfterClearAllocatesNoDroppedChannels()
        {
            var positions = new List<Vector3>(new Vector3[12]);
            var colors = new List<Color32>(new Color32[12]);
            var uvs = new List<Vector4>(new Vector4[12]);
            var normals = new List<Vector3>(new Vector3[12]);
            var tangents = new List<Vector4>(new Vector4[12]);
            var indices = new List<int> { 0, 1, 2, 2, 3, 0 };
            long Rebuild(Mesh mesh)
            {
                long before = System.GC.GetAllocatedBytesForCurrentThread();
                mesh.Clear();
                mesh.SetVertices(positions);
                mesh.SetColors(colors);
                mesh.SetUVs(0, uvs);
                mesh.SetUVs(1, uvs);
                mesh.SetNormals(normals);
                mesh.SetTangents(tangents);
                mesh.SetTriangles(indices, 0);
                return System.GC.GetAllocatedBytesForCurrentThread() - before;
            }
            var fresh = new Mesh();
            var reused = new Mesh();
            Rebuild(fresh);
            Rebuild(reused);
            Rebuild(new Mesh());
            // Each path run once more before it is measured: what the JIT allocates on this thread
            // when it compiles a method counts too (a handler of MonoMod's compile event).
            Rebuild(reused);
            Rebuild(new Mesh());
            // A mesh that held all those channels allocates no more than a new one does: at most
            // the new channel arrays themselves (with no upload pending it reuses its own).
            long expected = Rebuild(new Mesh());
            Assert.True(Rebuild(reused) <= expected);
            Assert.Equal(12, reused.uv2.Length);
        }

        [Fact]
        public void PropertySheetHashIgnoresInsertionOrder()
        {
            var a = new PropertySheet();
            a.SetFloat(Shader.PropertyToID("_A"), 1f);
            a.SetVector(Shader.PropertyToID("_B"), Vector4.one);
            var b = new PropertySheet();
            b.SetVector(Shader.PropertyToID("_B"), Vector4.one);
            b.SetFloat(Shader.PropertyToID("_A"), 1f);
            var ca = new Crc32();
            a.Hash(ca);
            var cb = new Crc32();
            b.Hash(cb);
            Assert.Equal(ca.Current, cb.Current);
            b.SetFloat(Shader.PropertyToID("_A"), 2f);
            var cc = new Crc32();
            b.Hash(cc);
            Assert.NotEqual(ca.Current, cc.Current);
        }

        [Fact]
        public void Crc32MatchesTheStandardCheckValue()
            => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

        [Fact]
        public void PropertyBlockStoresColoursAsVectors()
        {
            var block = new MaterialPropertyBlock();
            Assert.True(block.isEmpty);
            int id = Shader.PropertyToID("_Tint");
            block.SetColor(id, new Color(1f, 0.5f, 0.25f, 1f));
            Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 1f), block.GetVector(id));
            block.SetInt("_Count", 3);
            Assert.Equal(3f, block.GetFloat("_Count"));
        }


        [Fact]
        public void PngRoundTripsThroughLoadImage()
        {
            var source = new Texture2D(3, 2, TextureFormat.RGBA32, false);
            source.SetPixels32([
                new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 128), new Color32(0, 0, 255, 0),
                new Color32(10, 20, 30, 40), new Color32(50, 60, 70, 80), new Color32(90, 100, 110, 120)]);
            var png = source.EncodeToPNG();
            var loaded = new Texture2D(2, 2);
            Assert.True(loaded.LoadImage(png));
            Assert.Equal(TextureFormat.ARGB32, loaded.format);
            Assert.Equal(3, loaded.width);
            Assert.Equal(2, loaded.height);
            Assert.Equal(source.GetPixels32(), loaded.GetPixels32(0));
            Assert.False(loaded.LoadImage([1, 2, 3]));
        }

        [Fact]
        public void DescriptorWithMipMapsAndNoMipCountHasTheFullChain()
        {
            // mipCount -1 (the default) with useMipMap is the full chain; without it, one level.
            var desc = new RenderTextureDescriptor(64, 32, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, 0) { useMipMap = true };
            Assert.Equal(7, desc.ToNative().MipCount);
            desc.mipCount = 3;
            Assert.Equal(3, desc.ToNative().MipCount);
            desc.useMipMap = false;
            Assert.Equal(1, desc.ToNative().MipCount);
        }

        [Fact]
        public void RenderTextureMipCountsAreTheOnesAskedFor()
        {
            // As Unity's player (UnityProbe vram): a descriptor's mipCount is the texture's
            // mipmapCount and its device levels; useMipMap without one is the full chain at the
            // size the texture has, set before or after.
            var d = new RenderTextureDescriptor(1024, 1024, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, 0) { useMipMap = true, mipCount = 3 };
            var three = d.ToNative();
            Assert.Equal(3, three.MipCount);
            Assert.Equal(3, three.DeviceLevels);
            Assert.Equal(3, RenderTextureDescriptor.Of(three).mipCount);
            // What RenderTexture's useMipMap, width and height setters do.
            var rt = new RenderTextureNative { Width = 256, Height = 256 };
            Assert.Equal(1, rt.MipCount);
            rt.UseMipMap = true;
            rt.MipRequest = -1;
            rt.MipsChanged();
            Assert.Equal(9, rt.MipCount);
            rt.Width = rt.Height = 1024;
            rt.MipsChanged();
            Assert.Equal(11, rt.MipCount);
            Assert.Equal(11, rt.DeviceLevels);
            Assert.Equal(-1, RenderTextureDescriptor.Of(rt).mipCount);
        }

        [Fact]
        public void RenderTextureDescriptorDescribesTemporaryTextures()
        {
            // The dev tools' (ImGui) target: an 8-bit linear RGBA texture without depth.
            var desc = new RenderTextureDescriptor(64, 32, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, 0);
            Assert.Equal(1, desc.msaaSamples);
            Assert.Equal(1, desc.volumeDepth);
            Assert.False(desc.sRGB);
            desc.sRGB = true;
            Assert.Equal(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, desc.graphicsFormat);
            desc.sRGB = false;
            Assert.Equal(RenderTextureFormat.ARGB32, desc.colorFormat);
            desc.depthBufferBits = 20;
            Assert.Equal(24, desc.depthBufferBits);

            var rt = RenderTexture.GetTemporary(desc);
            Assert.Equal(64, rt.width);
            Assert.Equal(32, rt.height);
            Assert.Equal(24, rt.depth);
            Assert.Equal(desc.graphicsFormat, rt.descriptor.graphicsFormat);
            RenderTexture.ReleaseTemporary(rt);
            // The same description is served from the pool.
            Assert.Same(rt, RenderTexture.GetTemporary(desc));
            RenderTexture.ReleaseTemporary(rt);

            var own = new RenderTexture(desc);
            Assert.Equal(64, own.width);
            Object.DestroyImmediate(own);
        }

        /// A shader from Unity's built-in resources in the game install (ONI_GAME_DIR).
        static Shader BuiltinShader(string name)
        {
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            var file = OniMoono.Engine.Assets.AssetSystem.Get("unity_builtin_extra");
            foreach (var info in file.Objects)
                if (file.TypeOf(info).ClassId == 48 && OniMoono.Engine.Assets.ObjectLoader.Load(file, info.PathId) is Shader shader && shader.name == name)
                    return shader;
            throw new System.InvalidOperationException($"Built-in shader {name} not found.");
        }

        [Fact]
        public void CommandBufferRecordsMeshDrawsAndScissoring()
        {
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            Assert.Equal(UnityEngine.Rendering.IndexFormat.UInt32, mesh.indexFormat);
            mesh.vertices = [Vector3.zero, Vector3.right, Vector3.up];
            mesh.triangles = [0, 1, 2];
            var material = new Material(BuiltinShader("Hidden/BlitCopy"));
            var cb = new UnityEngine.Rendering.CommandBuffer { name = "test" };
            cb.SetViewport(new Rect(0, 0, 10, 10));
            cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0, 10, 10, 0, -10, 10));
            cb.EnableScissorRect(new Rect(1, 1, 5, 5));
            cb.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0, new MaterialPropertyBlock());
            cb.DisableScissorRect();
            Assert.Equal(5, cb.sizeInBytes);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => cb.DrawMesh(mesh, Matrix4x4.identity, material, 3, 0, null));
            Graphics.ExecuteCommandBuffer(cb);
        }
    }
}
