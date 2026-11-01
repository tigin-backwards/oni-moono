using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using static OniMoono.Desktop.Gl.GlConst;

namespace OniMoono.Tests
{
    /// A draw queue recorded in parallel posts the same commands, in the same order and with
    /// the same values, as the serial loop: draws whose inputs are all prepared are recorded on
    /// worker threads, the others (a changed mesh, texture, material or keyword set, an input
    /// seen for the first time) serially at their place.
    public class ParallelDrawRecordingTests : EngineTest
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int UndeclaredId = Shader.PropertyToID("_ParallelTestUndeclared");
        static readonly int BlockValueId = Shader.PropertyToID("_ParallelTestBlockValue");
        static readonly int StencilId = Shader.PropertyToID("_Stencil");
        static readonly int GlobalId = Shader.PropertyToID("_ParallelTestGlobal");

        /// A program as reflection would give one (no GL): property, texture and per-draw
        /// uniforms, a uniform block member, a declared and an undeclared sampler.
        static DeviceProgram FakeProgram()
        {
            var p = new DeviceProgram { Id = 1 };
            p.Uniforms.Add(new UniformBinding { Name = "_Color", Type = FloatVec4, Size = 1, Source = UniformSource.Property, PropertyId = ColorId });
            p.Uniforms.Add(new UniformBinding { Name = "_ParallelTestGlobal", Type = FloatVec4, Size = 1, Source = UniformSource.Property, PropertyId = GlobalId });
            p.Uniforms.Add(new UniformBinding { Name = "_MainTex_ST", Type = FloatVec4, Size = 1, Source = UniformSource.TextureScaleOffset, PropertyId = MainTexId });
            p.Uniforms.Add(new UniformBinding { Name = "_MainTex_TexelSize", Type = FloatVec4, Size = 1, Source = UniformSource.TextureTexelSize, PropertyId = MainTexId });
            p.Uniforms.Add(new UniformBinding { Name = "unity_ObjectToWorld", Type = FloatMat4, Size = 4, IsMatrix = true, Source = UniformSource.ObjectToWorld });
            p.Uniforms.Add(new UniformBinding { Name = "unity_MatrixVP", Type = FloatMat4, Size = 4, IsMatrix = true, Source = UniformSource.MatrixVP });
            p.Uniforms.Add(new UniformBinding { Name = "_ParallelTestBlockValue", Type = FloatType, Size = 1, Source = UniformSource.Property, PropertyId = BlockValueId, Block = 0, Offset = 0 });
            p.BlockSizes = [16];
            p.Samplers.Add(new SamplerBinding { Location = 0, Unit = 0, Target = TexTarget2D, PropertyId = MainTexId });
            p.Samplers.Add(new SamplerBinding { Location = 1, Unit = 1, Target = TexTarget2D, PropertyId = UndeclaredId });
            foreach (var u in p.Uniforms)
                if (u.Block < 0) p.LooseLength += GlUniforms.Length(u);
            p.BlocksLength = 16;
            return p;
        }

        /// The objects a queue draws; built the same way each time.
        sealed class Objects
        {
            public Material[] Materials;
            public Texture2D[] Textures;
            public RenderTexture Target;
            public Mesh[] Meshes;
            public PropertySheet[] Blocks;
            public List<DrawItem> Queue = new();
            public readonly List<UnityEngine.Object> Owned = new();

            public void Destroy()
            {
                foreach (var o in Owned) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        static Objects Build(Shader shader)
        {
            var s = new Objects();
            s.Textures = new Texture2D[5];
            for (int i = 0; i < s.Textures.Length; i++)
            {
                var t = new Texture2D(4 + i, 4, TextureFormat.RGBA32, false);
                t.SetPixel(0, 0, new Color(i / 5f, 0f, 0f, 1f));
                t.Apply();
                s.Textures[i] = t;
                s.Owned.Add(t);
            }
            s.Target = new RenderTexture(8, 8, 0);
            s.Owned.Add(s.Target);
            s.Materials = new Material[6];
            for (int i = 0; i < s.Materials.Length; i++)
            {
                var m = new Material(shader);
                m.SetColor(ColorId, new Color(i, 1f, 0f, 1f));
                m.SetFloat(BlockValueId, i * 0.5f);
                m.SetTexture(MainTexId, i == 5 ? s.Target : s.Textures[i % 5]);
                if (i == 2) m.SetTextureScale(MainTexId, new Vector2(2f, 3f));
                s.Materials[i] = m;
                s.Owned.Add(m);
            }
            s.Meshes = new Mesh[4];
            for (int i = 0; i < s.Meshes.Length; i++)
            {
                var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up * (i + 1), Vector3.right], triangles = [0, 1, 2] };
                s.Meshes[i] = mesh;
                s.Owned.Add(mesh);
            }
            s.Blocks = new PropertySheet[4];
            for (int i = 0; i < s.Blocks.Length; i++)
            {
                var b = new PropertySheet();
                b.SetVector(ColorId, new Vector4(0f, 0f, i, 1f));
                if (i == 1) b.SetTexture(MainTexId, s.Textures[4]);
                if (i == 2) b.SetFloat(BlockValueId, 9f);
                // A block setting a property the pass state reads resolves the state itself.
                if (i == 3) b.SetFloat(StencilId, 3f);
                s.Blocks[i] = b;
            }
            string[] clip = ["UNITY_UI_CLIP_RECT"];
            for (int i = 0; i < 400; i++)
            {
                s.Queue.Add(new DrawItem
                {
                    Mesh = (MeshNative)s.Meshes[i % 4].NativeOrThrow,
                    SubMesh = 0,
                    ObjectToWorld = Matrix4x4.Translate(new Vector3(i, i % 7, 0f)),
                    Material = s.Materials[i * 7 % 6],
                    Properties = i % 3 == 0 ? s.Blocks[i / 3 % 4] : null,
                    Keywords = i % 11 == 0 ? clip : null,
                });
            }
            return s;
        }

        /// What a recorder posted, in order: each draw's recorded inputs (devices objects by
        /// order of first appearance), each other command by its method.
        static List<string> Stream(GlWorker worker, List<ProgramInputs> executed)
        {
            var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            int Id(object o) => o == null ? -1 : ids.TryGetValue(o, out var id) ? id : ids[o] = ids.Count;
            var stream = new List<string>();
            foreach (var command in worker.Recording.Commands)
            {
                if (command.Method.DeclaringType?.DeclaringType != typeof(GlDrawRecorder))
                {
                    stream.Add("post " + command.Method.DeclaringType?.DeclaringType?.Name);
                    continue;
                }
                executed.Clear();
                command();
                var r = Assert.Single(executed);
                var values = string.Join(",", r.Values.AsSpan(r.ValuesOffset, r.Program.LooseLength).ToArray().Select(BitConverter.SingleToInt32Bits));
                var blocks = Convert.ToHexString(r.Blocks, r.BlocksOffset, r.Program.BlocksLength);
                var textures = string.Join(",", Enumerable.Range(0, r.TextureCount).Select(i => $"{Id(r.Textures[i].Texture)}/{r.Textures[i].RenderTexture}/{r.Textures[i].AutoGenerateMips}"));
                stream.Add($"draw {Id(r.Program)} {r.State.Src},{r.State.Dst},{r.State.ZTest},{r.State.ZWrite},{r.State.Cull},{r.State.StencilRef},{r.State.FComp},{r.State.Mask} [{values}] {blocks} [{textures}] mesh {Id(r.Mesh)} layout {Id(r.Layout)} {r.SubMesh}");
            }
            return stream;
        }

        static readonly ConcurrentDictionary<(string, uint), DeviceTexture> defaults = new();

        static DeviceTexture DefaultTexture(string name, uint target) => defaults.GetOrAdd((name, target), _ => new DeviceTexture { Target = target, RecordedTarget = target });

        /// Prepares the first half of the queue, then changes what a serial recording would
        /// have to prepare or resolve again, then records the whole queue.
        static int frame;

        static List<string> Record(Shader shader, bool parallel, bool verify = false)
        {
            Shader.SetGlobalVector(GlobalId, Vector4.one);
            var scene = Build(shader);
            try
            {
                var executed = new List<ProgramInputs>();
                var worker = new GlWorker();
                var recorder = new GlDrawRecorder(worker, DefaultTexture, executed.Add) { ParallelQueues = false };
                recorder.Camera = new CameraConstants(Matrix4x4.Translate(new Vector3(0f, 0f, -10f)), Matrix4x4.Ortho(-10f, 10f, -10f, 10f, 0.1f, 100f), Vector3.zero, 0.1f, 100f, true, new Vector2(640f, 480f));
                foreach (var d in scene.Queue.Take(200)) recorder.Draw(new DrawRequest(d.Mesh, d.SubMesh, d.ObjectToWorld, d.Material, d.Pass, d.Properties, d.Keywords));
                worker.Recording.Commands.Clear();

                scene.Textures[1].SetPixel(1, 1, Color.green);
                scene.Textures[1].Apply();
                scene.Textures[2].filterMode = FilterMode.Point;
                scene.Meshes[2].vertices = [Vector3.zero, Vector3.up, Vector3.left];
                scene.Materials[3].SetColor(ColorId, Color.blue);
                scene.Materials[4].EnableKeyword("UNITY_UI_ALPHACLIP");
                // As the game does every frame: every material's values are out of date.
                Shader.SetGlobalVector(GlobalId, new Vector4(frame, 2f, 3f, 4f));

                recorder.ParallelQueues = parallel;
                bool previous = GlDrawRecorder.Verify;
                GlDrawRecorder.Verify = verify;
                try
                {
                    recorder.DrawQueue(scene.Queue);
                }
                finally
                {
                    GlDrawRecorder.Verify = previous;
                }
                return Stream(worker, executed);
            }
            finally
            {
                scene.Destroy();
            }
        }

        static Shader TestShader(out ShaderNative native, out object previousGpu)
        {
            var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            var shader = Shader.Find("UI/Default");
            Assert.NotNull(shader);
            native = (ShaderNative)shader.NativeOrThrow;
            Assert.True(native.HasOpenGLCorePrograms);
            previousGpu = native.Gpu;
            // Every variant has a program already, as after its first draw.
            var set = new DeviceProgramSet();
            var program = FakeProgram();
            foreach (var sub in native.SubShaders)
                foreach (var pass in sub.Passes)
                    foreach (var variant in pass.Variants)
                        set.Programs[variant.BlobIndex] = program;
            native.Gpu = set;
            return shader;
        }

        [Fact]
        public void AParallelQueuePostsTheSerialCommandsInOrder()
        {
            var shader = TestShader(out var native, out var previousGpu);
            try
            {
                frame = 7;
                var serial = Record(shader, parallel: false);
                long ahead = GlDrawRecorder.AheadDrawCount;
                var parallel = Record(shader, parallel: true);
                ahead = GlDrawRecorder.AheadDrawCount - ahead;

                Assert.True(serial.Count >= 400, $"{serial.Count} commands");
                Assert.Contains(serial, c => c.StartsWith("post GlTextures", StringComparison.Ordinal));
                Assert.Contains(serial, c => c.StartsWith("post GlMeshes", StringComparison.Ordinal));
                for (int i = 0; i < Math.Min(serial.Count, parallel.Count); i++)
                    Assert.True(serial[i] == parallel[i], $"command {i}:\n  serial   {serial[i]}\n  parallel {parallel[i]}");
                Assert.Equal(serial.Count, parallel.Count);
                // Most of the queue was recorded ahead; what was changed or new was not.
                Assert.InRange(ahead, 300, 399);
            }
            finally
            {
                native.Gpu = previousGpu;
            }
        }

        [Fact]
        public void VerifyModeFindsTheDrawsRecordedAheadEqual()
        {
            var shader = TestShader(out var native, out var previousGpu);
            try
            {
                frame = 9;
                var before = GlDrawRecorder.VerifyCounts;
                var verified = Record(shader, parallel: true, verify: true);
                var after = GlDrawRecorder.VerifyCounts;
                Assert.True(after.Verified - before.Verified >= 100, $"{after.Verified - before.Verified} draws verified");
                Assert.Equal(before.Wrong, after.Wrong);
                // Checking posts nothing more.
                Assert.Equal(Record(shader, parallel: false), verified);
            }
            finally
            {
                native.Gpu = previousGpu;
            }
        }

        [Fact]
        public void ChildArenasAreResetWithTheFrame()
        {
            var arena = new FrameArena();
            var child = arena.Child(3);
            Assert.Same(child, arena.Child(3));
            var (first, offset) = child.Floats(10);
            Assert.Equal(0, offset);
            Assert.Equal(10, child.Floats(5).Offset);
            arena.Reset();
            var (again, at) = child.Floats(4);
            Assert.Same(first, again);
            Assert.Equal(0, at);
        }
    }
}
