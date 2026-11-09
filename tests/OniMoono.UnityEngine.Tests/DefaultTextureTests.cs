using System;
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
    /// What a sampler reads when its texture is missing: the original's readings
    /// (tools/UnityProbe/TextureFallback.cs, GLCore), each "ROUTE STATE DECLARATION = R,G,B,A".
    /// The route is the material's _MainTex or the global; the declaration is _MainTex's
    /// default ("empty": "", "nonsense": a name Unity does not know) or none; a live texture
    /// reads 51,102,153,204, and one made again at another size, or created after it was set,
    /// 204,153,102,51 ("rt-new": a render texture never created).
    public class DefaultTextureTests : EngineTest
    {
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        static readonly string[] Measured =
        [
            "global never empty = 128,128,128,128",
            "global never white = 255,255,255,255",
            "global never black = 0,0,0,0",
            "global never grey = 127,127,127,127",
            "global never gray = 127,127,127,127",
            "global never bump = 127,127,255,255",
            "global never red = 255,0,0,0",
            "global never nonsense = 128,128,128,128",
            "global never undeclared = 128,128,128,128",
            "material never empty = 128,128,128,128",
            "material null empty = 128,128,128,128",
            "material live empty = 51,102,153,204",
            "material rt-live empty = 51,102,153,204",
            "material destroyed empty = 0,0,0,255",
            "material rt-destroyed empty = 0,0,0,255",
            "material rt-released empty = 0,0,0,255",
            "material rt-resized empty = 204,153,102,51",
            "material rt-resized-uncreated empty = 0,0,0,255",
            "material never white = 255,255,255,255",
            "material null white = 255,255,255,255",
            "material live white = 51,102,153,204",
            "material rt-live white = 51,102,153,204",
            "material destroyed white = 0,0,0,255",
            "material rt-destroyed white = 0,0,0,255",
            "material rt-released white = 0,0,0,255",
            "material rt-resized white = 204,153,102,51",
            "material rt-resized-uncreated white = 0,0,0,255",
            "material never black = 0,0,0,0",
            "material null black = 0,0,0,0",
            "material live black = 51,102,153,204",
            "material rt-live black = 51,102,153,204",
            "material destroyed black = 0,0,0,255",
            "material rt-destroyed black = 0,0,0,255",
            "material rt-released black = 0,0,0,255",
            "material rt-resized black = 204,153,102,51",
            "material rt-resized-uncreated black = 0,0,0,255",
            "material never grey = 127,127,127,127",
            "material null grey = 127,127,127,127",
            "material live grey = 51,102,153,204",
            "material rt-live grey = 51,102,153,204",
            "material destroyed grey = 0,0,0,255",
            "material rt-destroyed grey = 0,0,0,255",
            "material rt-released grey = 0,0,0,255",
            "material rt-resized grey = 204,153,102,51",
            "material rt-resized-uncreated grey = 0,0,0,255",
            "material never gray = 127,127,127,127",
            "material null gray = 127,127,127,127",
            "material live gray = 51,102,153,204",
            "material rt-live gray = 51,102,153,204",
            "material destroyed gray = 0,0,0,255",
            "material rt-destroyed gray = 0,0,0,255",
            "material rt-released gray = 0,0,0,255",
            "material rt-resized gray = 204,153,102,51",
            "material rt-resized-uncreated gray = 0,0,0,255",
            "material never bump = 127,127,255,255",
            "material null bump = 127,127,255,255",
            "material live bump = 51,102,153,204",
            "material rt-live bump = 51,102,153,204",
            "material destroyed bump = 0,0,0,255",
            "material rt-destroyed bump = 0,0,0,255",
            "material rt-released bump = 0,0,0,255",
            "material rt-resized bump = 204,153,102,51",
            "material rt-resized-uncreated bump = 0,0,0,255",
            "material never red = 255,0,0,0",
            "material null red = 255,0,0,0",
            "material live red = 51,102,153,204",
            "material rt-live red = 51,102,153,204",
            "material destroyed red = 0,0,0,255",
            "material rt-destroyed red = 0,0,0,255",
            "material rt-released red = 0,0,0,255",
            "material rt-resized red = 204,153,102,51",
            "material rt-resized-uncreated red = 0,0,0,255",
            "material never nonsense = 128,128,128,128",
            "material null nonsense = 128,128,128,128",
            "material live nonsense = 51,102,153,204",
            "material rt-live nonsense = 51,102,153,204",
            "material destroyed nonsense = 0,0,0,255",
            "material rt-destroyed nonsense = 0,0,0,255",
            "material rt-released nonsense = 0,0,0,255",
            "material rt-resized nonsense = 204,153,102,51",
            "material rt-resized-uncreated nonsense = 0,0,0,255",
            "material never undeclared = 128,128,128,128",
            "material null undeclared = 128,128,128,128",
            "material live undeclared = 51,102,153,204",
            "material rt-live undeclared = 51,102,153,204",
            "material destroyed undeclared = 0,0,0,255",
            "material rt-destroyed undeclared = 0,0,0,255",
            "material rt-released undeclared = 0,0,0,255",
            "material rt-resized undeclared = 204,153,102,51",
            "material rt-resized-uncreated undeclared = 0,0,0,255",
            "global null empty = 128,128,128,128",
            "global live empty = 51,102,153,204",
            "global rt-live empty = 51,102,153,204",
            "global destroyed empty = 128,128,128,128",
            "global rt-destroyed empty = 128,128,128,128",
            "global rt-released empty = 0,0,0,255",
            "global rt-resized empty = 204,153,102,51",
            "global rt-resized-uncreated empty = 0,0,0,255",
            "global null white = 255,255,255,255",
            "global live white = 255,255,255,255",
            "global rt-live white = 255,255,255,255",
            "global destroyed white = 255,255,255,255",
            "global rt-destroyed white = 255,255,255,255",
            "global rt-released white = 255,255,255,255",
            "global rt-resized white = 255,255,255,255",
            "global rt-resized-uncreated white = 255,255,255,255",
            "global null black = 0,0,0,0",
            "global live black = 0,0,0,0",
            "global rt-live black = 0,0,0,0",
            "global destroyed black = 0,0,0,0",
            "global rt-destroyed black = 0,0,0,0",
            "global rt-released black = 0,0,0,0",
            "global rt-resized black = 0,0,0,0",
            "global rt-resized-uncreated black = 0,0,0,0",
            "global null grey = 127,127,127,127",
            "global live grey = 127,127,127,127",
            "global rt-live grey = 127,127,127,127",
            "global destroyed grey = 127,127,127,127",
            "global rt-destroyed grey = 127,127,127,127",
            "global rt-released grey = 127,127,127,127",
            "global rt-resized grey = 127,127,127,127",
            "global rt-resized-uncreated grey = 127,127,127,127",
            "global null gray = 127,127,127,127",
            "global live gray = 127,127,127,127",
            "global rt-live gray = 127,127,127,127",
            "global destroyed gray = 127,127,127,127",
            "global rt-destroyed gray = 127,127,127,127",
            "global rt-released gray = 127,127,127,127",
            "global rt-resized gray = 127,127,127,127",
            "global rt-resized-uncreated gray = 127,127,127,127",
            "global null bump = 127,127,255,255",
            "global live bump = 127,127,255,255",
            "global rt-live bump = 127,127,255,255",
            "global destroyed bump = 127,127,255,255",
            "global rt-destroyed bump = 127,127,255,255",
            "global rt-released bump = 127,127,255,255",
            "global rt-resized bump = 127,127,255,255",
            "global rt-resized-uncreated bump = 127,127,255,255",
            "global null red = 255,0,0,0",
            "global live red = 255,0,0,0",
            "global rt-live red = 255,0,0,0",
            "global destroyed red = 255,0,0,0",
            "global rt-destroyed red = 255,0,0,0",
            "global rt-released red = 255,0,0,0",
            "global rt-resized red = 255,0,0,0",
            "global rt-resized-uncreated red = 255,0,0,0",
            "global null nonsense = 128,128,128,128",
            "global live nonsense = 51,102,153,204",
            "global rt-live nonsense = 51,102,153,204",
            "global destroyed nonsense = 128,128,128,128",
            "global rt-destroyed nonsense = 128,128,128,128",
            "global rt-released nonsense = 0,0,0,255",
            "global rt-resized nonsense = 204,153,102,51",
            "global rt-resized-uncreated nonsense = 0,0,0,255",
            "global null undeclared = 128,128,128,128",
            "global live undeclared = 51,102,153,204",
            "global rt-live undeclared = 51,102,153,204",
            "global destroyed undeclared = 128,128,128,128",
            "global rt-destroyed undeclared = 128,128,128,128",
            "global rt-released undeclared = 0,0,0,255",
            "global rt-resized undeclared = 204,153,102,51",
            "global rt-resized-uncreated undeclared = 0,0,0,255",
            "material rt-new empty = 0,0,0,255",
            "material rt-new-created empty = 204,153,102,51",
            "material rt-new white = 0,0,0,255",
            "material rt-new-created white = 204,153,102,51",
            "material rt-new black = 0,0,0,255",
            "material rt-new-created black = 204,153,102,51",
            "material rt-new grey = 0,0,0,255",
            "material rt-new-created grey = 204,153,102,51",
            "material rt-new gray = 0,0,0,255",
            "material rt-new-created gray = 204,153,102,51",
            "material rt-new bump = 0,0,0,255",
            "material rt-new-created bump = 204,153,102,51",
            "material rt-new red = 0,0,0,255",
            "material rt-new-created red = 204,153,102,51",
            "material rt-new nonsense = 0,0,0,255",
            "material rt-new-created nonsense = 204,153,102,51",
            "material rt-new undeclared = 0,0,0,255",
            "material rt-new-created undeclared = 204,153,102,51",
            "global rt-new empty = 0,0,0,255",
            "global rt-new-created empty = 204,153,102,51",
            "global rt-new white = 255,255,255,255",
            "global rt-new-created white = 255,255,255,255",
            "global rt-new black = 0,0,0,0",
            "global rt-new-created black = 0,0,0,0",
            "global rt-new grey = 127,127,127,127",
            "global rt-new-created grey = 127,127,127,127",
            "global rt-new gray = 127,127,127,127",
            "global rt-new-created gray = 127,127,127,127",
            "global rt-new bump = 127,127,255,255",
            "global rt-new-created bump = 127,127,255,255",
            "global rt-new red = 255,0,0,0",
            "global rt-new-created red = 255,0,0,0",
            "global rt-new nonsense = 0,0,0,255",
            "global rt-new-created nonsense = 204,153,102,51",
            "global rt-new undeclared = 0,0,0,255",
            "global rt-new-created undeclared = 204,153,102,51"
        ];

        static readonly Color32 Live = new(51, 102, 153, 204), Again = new(204, 153, 102, 51);

        [Fact]
        public void EveryCaseReadsWhatTheOriginalReads()
        {
            LoadGame();
            var shader = Shader.Find("UI/Default");
            Assert.NotNull(shader);
            var native = (ShaderNative)shader.NativeOrThrow;
            var previousGpu = native.Gpu;
            var declared = native.PropertiesById[MainTexId];
            string previousDefault = declared.DefaultTexture;
            var set = new DeviceProgramSet();
            var program = new DeviceProgram { Id = 1 };
            program.Samplers.Add(new SamplerBinding { Location = 0, Unit = 0, Target = TexTarget2D, PropertyId = MainTexId });
            foreach (var sub in native.SubShaders)
                foreach (var pass in sub.Passes)
                    foreach (var variant in pass.Variants)
                        set.Programs[variant.BlobIndex] = program;
            native.Gpu = set;
            var wrong = new List<string>();
            try
            {
                foreach (var line in Measured)
                {
                    var (route, state, declaration) = (line.Split(' ')[0], line.Split(' ')[1], line.Split(' ')[2]);
                    string expected = line[(line.IndexOf('=') + 2)..];
                    if (declaration == "undeclared") native.PropertiesById.Remove(MainTexId);
                    else
                    {
                        native.PropertiesById[MainTexId] = declared;
                        declared.DefaultTexture = declaration == "empty" ? "" : declaration;
                    }
                    string read = Read(shader, route, state);
                    if (read != expected) wrong.Add(line + " | ours " + read);
                }
            }
            finally
            {
                native.PropertiesById[MainTexId] = declared;
                declared.DefaultTexture = previousDefault;
                native.Gpu = previousGpu;
                ShaderGlobals.Properties.Textures.Remove(MainTexId);
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        /// What the sampler reads: a default's colour, or the texture's (Live, or Again for one
        /// made again at another size).
        static string Read(Shader shader, string route, string state)
        {
            ShaderGlobals.Properties.Textures.Remove(MainTexId);
            var material = new Material(shader);
            var owned = new List<UnityEngine.Object> { material };
            Texture texture = null;
            bool again = false;
            switch (state)
            {
                case "never":
                    break;
                case "null":
                    Set(material, route, null);
                    break;
                case "rt-new":
                case "rt-new-created":
                    texture = new RenderTexture(4, 4, 0);
                    break;
                case "live":
                case "destroyed":
                    texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    break;
                default:
                    var rt = new RenderTexture(4, 4, 0);
                    rt.Create();
                    texture = rt;
                    break;
            }
            if (texture != null)
            {
                Set(material, route, texture);
                var rt = texture as RenderTexture;
                switch (state)
                {
                    case "destroyed":
                        UnityEngine.Object.DestroyImmediate(texture);
                        break;
                    case "rt-destroyed":
                        rt.Release();
                        UnityEngine.Object.DestroyImmediate(rt);
                        break;
                    case "rt-released":
                        rt.Release();
                        break;
                    case "rt-resized":
                        rt.Release();
                        rt.width = rt.height = 8;
                        rt.Create();
                        again = true;
                        break;
                    case "rt-new-created":
                        rt.Create();
                        again = true;
                        break;
                    case "rt-resized-uncreated":
                        rt.Release();
                        rt.width = rt.height = 8;
                        break;
                }
                if (texture != null) owned.Add(texture);
            }
            try
            {
                var worker = new GlWorker();
                var executed = new List<ProgramInputs>();
                var names = new Dictionary<DeviceTexture, string>(ReferenceEqualityComparer.Instance);
                DeviceTexture Default(string name, uint target)
                {
                    var t = new DeviceTexture { Target = target, RecordedTarget = target, Width = 1, Height = 1 };
                    names[t] = name;
                    return t;
                }
                var recorder = new GlDrawRecorder(worker, Default, executed.Add) { ParallelQueues = false };
                recorder.Camera = new CameraConstants(Matrix4x4.identity, Matrix4x4.identity, Vector3.zero, 0.1f, 100f, true, new Vector2(4f, 4f));
                var mesh = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
                owned.Add(mesh);
                recorder.Draw(new DrawRequest((MeshNative)mesh.NativeOrThrow, 0, Matrix4x4.identity, material, 0, null, null));
                foreach (var command in worker.Recording.Commands)
                    if (command.Method.DeclaringType?.DeclaringType == typeof(GlDrawRecorder) && command.Target is not ResourceCommand) command();
                var r = Assert.Single(executed);
                var sampled = r.Textures[0].Texture;
                if (names.TryGetValue(sampled, out var name))
                {
                    var (_, cr, cg, cb, ca) = DefaultTextures.Colors.First(c => c.Name == name);
                    return $"{cr},{cg},{cb},{ca}";
                }
                var c32 = again ? Again : Live;
                return $"{c32.r},{c32.g},{c32.b},{c32.a}";
            }
            finally
            {
                foreach (var o in owned)
                    if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        /// Shader.GetGlobalTexture returns null for a texture since destroyed and for a render
        /// texture never created, and the texture once it was created, released or not;
        /// Material.GetTexture returns it all along.
        [Fact]
        public void GetGlobalTextureHidesRenderTexturesNeverCreated()
        {
            LoadGame();
            var id = Shader.PropertyToID("_DefaultTextureTestGlobal");
            var rt = new RenderTexture(4, 4, 0);
            try
            {
                Shader.SetGlobalTexture(id, rt);
                Assert.Null(Shader.GetGlobalTexture(id));
                rt.Create();
                Assert.Same(rt, Shader.GetGlobalTexture(id));
                rt.Release();
                Assert.Same(rt, Shader.GetGlobalTexture(id));
                UnityEngine.Object.DestroyImmediate(rt);
                Assert.Null(Shader.GetGlobalTexture(id));
            }
            finally
            {
                ShaderGlobals.Properties.Textures.Remove(id);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// A material made from a shader takes its name. Of a property the shader does not
        /// declare, it holds a texture its draws read, but GetTexture returns null with an error;
        /// HasProperty and HasTexture are true (as in the original).
        [Fact]
        public void UndeclaredTexturesAreHeldButNotReturned()
        {
            LoadGame();
            var shader = Shader.Find("UI/Default");
            var material = new Material(shader);
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var logged = new List<(string, LogType)>();
            void Log(string message, string stack, LogType type) => logged.Add((message, type));
            Application.logMessageReceived += Log;
            try
            {
                Assert.Equal("UI/Default", material.name);
                Assert.Null(material.GetTexture(MainTexId));
                Assert.Empty(logged);
                material.SetTexture("_DefaultTextureTestUndeclared", texture);
                Assert.True(material.HasProperty("_DefaultTextureTestUndeclared"));
                Assert.True(material.HasTexture("_DefaultTextureTestUndeclared"));
                Assert.Null(material.GetTexture("_DefaultTextureTestUndeclared"));
                var (message, type) = Assert.Single(logged);
                Assert.Equal(LogType.Error, type);
                Assert.Equal("Material 'UI/Default' with Shader 'UI/Default' doesn't have a texture property '_DefaultTextureTestUndeclared'", message);
                Assert.False(material.HasTexture("_DefaultTextureTestNeverSet"));
            }
            finally
            {
                Application.logMessageReceived -= Log;
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static void LoadGame()
        {
            AssetSystem.Initialize(GameInstall.DataDir);
            ProjectSettings.Load();
        }

        static void Set(Material material, string route, Texture texture)
        {
            if (route == "material") material.SetTexture(MainTexId, texture);
            else Shader.SetGlobalTexture(MainTexId, texture);
        }
    }
}
