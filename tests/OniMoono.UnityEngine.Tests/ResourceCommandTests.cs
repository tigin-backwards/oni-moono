using System.Collections.Generic;
using OniMoono.Desktop.Gl;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Texture uploads, sampler changes and mesh uploads are recorded as ResourceCommands from
    /// the frame's arena: each runs the work it was recorded with, in the order posted, and the
    /// arena hands the same objects out again once the frame has been reset.
    public class ResourceCommandTests
    {
        sealed class Work : IResourceWork
        {
            public readonly List<string> Calls = new();
            readonly Dictionary<object, int> ids = new(ReferenceEqualityComparer.Instance);
            int Id(object o) => ids.TryGetValue(o, out var id) ? id : ids[o] = ids.Count;

            public void CreateRenderTexture(DeviceTexture texture, RenderShape shape, string name) => Calls.Add($"create {Id(texture)}");
            public void UploadTexture(DeviceTexture texture, TexelUpload upload) => Calls.Add($"upload {Id(texture)} {Id(upload)}");
            public void ApplySampler(DeviceTexture texture, GlTextures.SamplerSettings sampler) => Calls.Add($"sampler {Id(texture)} {sampler.FilterMode} {sampler.Aniso}");
            public void UploadMesh(DeviceMesh mesh, MeshUpload upload) => Calls.Add($"mesh {Id(mesh)} {Id(upload)}");
            public DeviceProgram Program(ShaderNative shader, int blobIndex) => null;
        }

        [Fact]
        public void CommandsRunTheirOwnWorkInOrderAndAreReused()
        {
            var work = new Work();
            var arena = new FrameArena();
            DeviceTexture a = new(), b = new();
            TexelUpload ua = new(), ub = new();
            var mesh = new DeviceMesh();
            var meshUpload = new MeshUpload();
            var posted = new List<System.Action>
            {
                arena.Command().UploadTexture(work, a, ua),
                arena.Command().ApplySampler(work, a, new GlTextures.SamplerSettings(FilterMode.Point, TextureWrapMode.Clamp, TextureWrapMode.Clamp, TextureWrapMode.Clamp, 0f, 1)),
                arena.Command().UploadMesh(work, mesh, meshUpload),
                arena.Command().UploadTexture(work, b, ub),
                arena.Command().ApplySampler(work, a, new GlTextures.SamplerSettings(FilterMode.Trilinear, TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureWrapMode.Repeat, 0f, 4)),
            };
            foreach (var p in posted) p();
            Assert.Equal(new[] { "upload 0 1", "sampler 0 Point 1", "mesh 2 3", "upload 4 5", "sampler 0 Trilinear 4" }, work.Calls);
            // Five objects, each with its delegate made once.
            Assert.Equal(5, new HashSet<object>(posted.ConvertAll(p => p.Target!), ReferenceEqualityComparer.Instance).Count);

            arena.Reset();
            var again = arena.Command().ApplySampler(work, b, new GlTextures.SamplerSettings(FilterMode.Bilinear, TextureWrapMode.Clamp, TextureWrapMode.Clamp, TextureWrapMode.Clamp, 0f, 2));
            Assert.Same(posted[0], again);
            work.Calls.Clear();
            again();
            Assert.Equal(new[] { "sampler 4 Bilinear 2" }, work.Calls);
        }

        /// A device's state commands (targets, viewports, clears) run their work with the state
        /// each was recorded with, a list per state type, reused once the frame is reset.
        [Fact]
        public void StateCommandsRunWithTheirOwnState()
        {
            var arena = new FrameArena();
            var calls = new List<string>();
            System.Action<Rect> viewport = r => calls.Add($"viewport {r.width}");
            System.Action<(bool Color, float Depth)> clear = c => calls.Add($"clear {c.Color} {c.Depth}");
            var posted = new List<System.Action>
            {
                arena.Command(viewport, new Rect(0f, 0f, 10f, 10f)),
                arena.Command(clear, (true, 1f)),
                arena.Command(viewport, new Rect(0f, 0f, 20f, 10f)),
                arena.Command(clear, (false, 0.5f)),
            };
            foreach (var p in posted) p();
            Assert.Equal(new[] { "viewport 10", "clear True 1", "viewport 20", "clear False 0.5" }, calls);

            arena.Reset();
            calls.Clear();
            var again = arena.Command(clear, (true, 0.25f));
            Assert.Same(posted[1], again);
            again();
            Assert.Equal(new[] { "clear True 0.25" }, calls);
        }
    }
}
