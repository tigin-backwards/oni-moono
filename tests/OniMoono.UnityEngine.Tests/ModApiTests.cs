using System;
using System.IO;
using System.Linq;
using OniMoono.Engine.Assets;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    // Unity API that mods call although the game itself does not (tools/ApiCheck over the mod
    // assemblies of a real profile): BlueprintsV2's shared UtilLibs looks a bundle up among the
    // loaded ones and makes temporary render textures with a read/write mode.
    public class ModApiTests : EngineTest
    {
        static string DataDir => Path.Combine(Environment.GetEnvironmentVariable("ONI_GAME_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211"), "OxygenNotIncluded_Data");

        [Fact]
        public void LoadedAssetBundlesAreListedUntilUnloaded()
        {
            AssetSystem.Initialize(DataDir);
            var bundle = AssetBundle.LoadFromFile(Path.Combine(DataDir, "StreamingAssets", "hires_base_bundle"));
            Assert.NotNull(bundle);
            try
            {
                Assert.Contains(bundle, AssetBundle.GetAllLoadedAssetBundles());
            }
            finally
            {
                bundle.Unload(true);
            }
            Assert.DoesNotContain(AssetBundle.GetAllLoadedAssetBundles(), b => ReferenceEquals(b, bundle));
        }

        // In the player, DestroyImmediate removes assets without allowDestroyingAssets (measured in
        // the original player on a mod's bundle prefab and a prefab of the game's own data; the
        // refusal is the editor's). BlueprintsV2 swaps its bundle prefabs' Text for LocText so.
        [Fact]
        public void DestroyImmediateRemovesAnAssetWithoutTheFlag()
        {
            AssetSystem.Initialize(DataDir);
            var file = AssetSystem.Get("sharedassets0.assets");
            // The file's last texture (Texture2D, class 28): an asset of the game's own data.
            var info = file.Objects.Last(o => file.TypeOf(o).ClassId == 28);
            var texture = ObjectLoader.Load(file, info.PathId);
            Assert.True(texture != null);
            Object.DestroyImmediate(texture);
            Assert.True(texture == null);
        }

        [Fact]
        public void TemporaryRenderTexturesTakeTheirReadWriteMode()
        {
            var linear = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var srgb = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Assert.False(linear.sRGB);
            Assert.True(srgb.sRGB);
            RenderTexture.ReleaseTemporary(linear);
            RenderTexture.ReleaseTemporary(srgb);
        }

        // Fast Track sets these on the renderers it creates (defaults and round trip measured in the
        // original player).
        [Fact]
        public void RenderersAllowOcclusionAndGenerateObjectMotionVectorsByDefault()
        {
            var go = new GameObject("Renderer");
            var renderer = go.AddComponent<MeshRenderer>();
            Assert.True(renderer.allowOcclusionWhenDynamic);
            Assert.Equal(MotionVectorGenerationMode.Object, renderer.motionVectorGenerationMode);
            renderer.allowOcclusionWhenDynamic = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            Assert.False(renderer.allowOcclusionWhenDynamic);
            Assert.Equal(MotionVectorGenerationMode.ForceNoMotion, renderer.motionVectorGenerationMode);
            Object.DestroyImmediate(go);
        }

        // Fast Track's tile meshes: the update flags (measured in the original player).
        // DontValidateIndices does not let fewer vertices than the triangles reference through;
        // DontRecalculateBounds keeps the bounds while the vertices change.
        [Fact]
        public void MeshUpdateFlagsKeepTheIndexCheckAndCanKeepTheBounds()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new System.Collections.Generic.List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(5, 5, 0) });
            mesh.SetTriangles(new[] { 0, 1, 3 }, 0);
            var bounds = mesh.bounds;
            mesh.SetVertices(new System.Collections.Generic.List<Vector3> { new(0, 0, 0), new(2, 0, 0), new(0, 2, 0) }, 0, 3, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            Assert.Equal(4, mesh.vertexCount);
            mesh.SetVertices(new System.Collections.Generic.List<Vector3> { new(0, 0, 0), new(3, 0, 0), new(0, 3, 0), new(9, 9, 0) }, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            Assert.Equal(new Vector3(9, 9, 0), mesh.vertices[3]);
            Assert.Equal(bounds, mesh.bounds);
            mesh.SetUVs(0, new System.Collections.Generic.List<Vector2> { Vector2.zero, Vector2.one, Vector2.up, Vector2.right }, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices);
            mesh.SetColors(new System.Collections.Generic.List<Color> { Color.red, Color.green, Color.blue, Color.white }, 0, 4, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
            Assert.Equal(Vector2.one, mesh.uv[1]);
            Assert.Equal(Color.blue, mesh.colors[2]);
            Object.DestroyImmediate(mesh);
        }

        // When a mesh's bounds change (measured in the original player): with the number of vertices,
        // and by RecalculateBounds; not with new positions for as many vertices, triangles (their
        // submesh's bounds only) or Clear.
        [Fact]
        public void MeshBoundsFollowTheVertexCountOnly()
        {
            var far = new System.Collections.Generic.List<Vector3> { new(0, 0, 0), new(4, 0, 0), new(0, 4, 0), new(20, 20, 0) };
            var wide = new Vector3[] { new(0, 0, 0), new(3, 0, 0), new(0, 3, 0), new(9, 9, 0) };
            var tenTen = new Bounds(new Vector3(10, 10, 0), new Vector3(20, 20, 0));
            var mesh = new Mesh();
            mesh.SetVertices(far);
            Assert.Equal(tenTen, mesh.bounds);
            mesh.vertices = wide;
            Assert.Equal(tenTen, mesh.bounds);
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.triangles = new[] { 0, 1, 3 };
            Assert.Equal(tenTen, mesh.bounds);
            mesh.RecalculateBounds();
            Assert.Equal(new Bounds(new Vector3(4.5f, 4.5f, 0), new Vector3(9, 9, 0)), mesh.bounds);
            mesh.Clear();
            Assert.Equal(new Bounds(new Vector3(4.5f, 4.5f, 0), new Vector3(9, 9, 0)), mesh.bounds);
            mesh.SetVertices(far);
            mesh.SetVertices(new System.Collections.Generic.List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) });
            Assert.Equal(new Bounds(new Vector3(0.5f, 0.5f, 0), new Vector3(1, 1, 0)), mesh.bounds);
            mesh.bounds = new Bounds(new Vector3(100, 100, 0), Vector3.one);
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, true);
            Assert.Equal(new Vector3(100, 100, 0), mesh.bounds.center);
            Object.DestroyImmediate(mesh);
        }

        // Rocketry Expanded builds gradients with SetKeys: both key sets, sorted by time, evaluated
        // between them.
        // Measured in the original player (PerfProbe's measure mode); mods' shared UI code parses
        // its colours so.
        [Theory]
        [InlineData("#FF0000", 255, 0, 0, 255)]
        [InlineData("#f00", 255, 0, 0, 255)]
        [InlineData("#F008", 255, 0, 0, 136)]
        [InlineData("#FF000080", 255, 0, 0, 128)]
        [InlineData(" #FF0000", 255, 0, 0, 255)]
        [InlineData("#FF0000 ", 255, 0, 0, 255)]
        [InlineData("RED", 255, 0, 0, 255)]
        [InlineData("grey", 128, 128, 128, 255)]
        [InlineData("lightblue", 173, 216, 230, 255)]
        [InlineData("#abc", 170, 187, 204, 255)]
        [InlineData("#ABCDEF12", 171, 205, 239, 18)]
        public void HtmlColorsParseAsInUnity(string text, byte r, byte g, byte b, byte a)
        {
            Assert.True(ColorUtility.TryParseHtmlString(text, out var color));
            Assert.Equal(new Color32(r, g, b, a), (Color32)color);
        }

        [Theory]
        [InlineData("#12345")]
        [InlineData("#1234567")]
        [InlineData("FF0000")]
        [InlineData("#GG0000")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("gray")]
        [InlineData("pink")]
        [InlineData("clear")]
        public void OtherHtmlColorsAreWhiteAndRefused(string text)
        {
            Assert.False(ColorUtility.TryParseHtmlString(text, out var color));
            Assert.Equal(Color.white, color);
        }

        // LineRenderer geometry as measured in the original player (BakeMesh with an orthographic
        // camera at z -10 looking along +Z), start colour red, end colour half-transparent blue.
        static (Vector3[] Positions, Vector2[] Uvs, Color32[] Colors, int[] Indices) Line(Action<LineRenderer> setup)
        {
            var line = new GameObject("line").AddComponent<LineRenderer>();
            line.startColor = new Color(1, 0, 0, 1);
            line.endColor = new Color(0, 0, 1, 0.5f);
            setup(line);
            var mesh = new OniMoono.Engine.Rendering.MeshNative();
            var native = (OniMoono.Engine.Rendering.LineRendererNative)line.NativeOrThrow;
            if (!native.BuildGeometry(new Vector3(0, 0, -10), Vector3.forward, Vector3.right, Vector3.up, false, mesh)) return (Array.Empty<Vector3>(), Array.Empty<Vector2>(), Array.Empty<Color32>(), Array.Empty<int>());
            return (mesh.Positions, mesh.Uvs[0].Select(u => new Vector2(u.x, u.y)).ToArray(), mesh.Colors.Select(c => (Color32)c).ToArray(), mesh.SubMeshes[0].Indices);
        }

        static void Near(Vector3 expected, Vector3 actual) => Assert.True((expected - actual).magnitude < 1e-3f, $"expected {expected}, got {actual}");

        [Fact]
        public void LineRenderersDefaultToTwoPointsOfUnitWidth()
        {
            var line = new GameObject("line").AddComponent<LineRenderer>();
            Assert.Equal(2, line.positionCount);
            Assert.Equal(new Vector3(0, 0, 1), line.GetPosition(1));
            Assert.Equal(1f, line.startWidth);
            Assert.Equal(1f, line.endWidth);
            Assert.True(line.useWorldSpace);
            Assert.Equal(LineAlignment.View, line.alignment);
            Assert.Equal(Vector2.one, line.textureScale);
            Assert.Equal((Color32)Color.white, (Color32)line.startColor);
        }

        [Fact]
        public void LineCornersAreMiteredAndClamped()
        {
            var two = Line(l => l.SetPositions(new[] { Vector3.zero, new Vector3(10, 0, 0) }));
            Near(new Vector3(0, -0.5f, 0), two.Positions[0]);
            Near(new Vector3(0, 0.5f, 0), two.Positions[1]);
            Assert.Equal(new[] { 0, 1, 2, 1, 3, 2 }, two.Indices);
            Assert.Equal(new Vector2(0, 1), two.Uvs[0]);
            Assert.Equal(new Color32(0, 0, 255, 128), two.Colors[3]);

            var corner = Line(l => { l.positionCount = 3; l.SetPositions(new[] { Vector3.zero, new Vector3(10, 0, 0), new Vector3(10, 10, 0) }); });
            Near(new Vector3(10.5f, -0.5f, 0), corner.Positions[2]);
            Near(new Vector3(9.5f, 0.5f, 0), corner.Positions[3]);
            Assert.Equal(new Color32(128, 0, 128, 192), corner.Colors[2]);

            var acute = Line(l => { l.positionCount = 3; l.SetPositions(new[] { Vector3.zero, new Vector3(10, 0, 0), new Vector3(0, 2, 0) }); });
            Near(new Vector3(11.4927f, -0.1478f, 0), acute.Positions[2]);
            Near(new Vector3(8.5073f, 0.1478f, 0), acute.Positions[3]);

            var loop = Line(l => { l.positionCount = 4; l.SetPositions(new[] { Vector3.zero, new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 10, 0) }); l.loop = true; });
            Assert.Equal(10, loop.Positions.Length);
            Near(new Vector3(-0.5f, -0.5f, 0), loop.Positions[0]);
            Near(new Vector3(0.5f, 0.5f, 0), loop.Positions[9]);
            Assert.Equal(new Vector2(0.25f, 1), loop.Uvs[2]);

            Assert.Empty(Line(l => { l.positionCount = 1; l.SetPosition(0, Vector3.one); }).Positions);
            var depth = Line(l => l.SetPositions(new[] { Vector3.zero, new Vector3(0, 0, 10) }));
            Near(new Vector3(-0.3536f, 0.3536f, 0), depth.Positions[0]);
        }

        [Fact]
        public void LineWidthsColoursAndUvsFollowTheLength()
        {
            var curve = Line(l => { l.positionCount = 3; l.SetPositions(new[] { Vector3.zero, new Vector3(4, 0, 0), new Vector3(10, 0, 0) }); l.widthMultiplier = 2; l.widthCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0.2f)); });
            Near(new Vector3(4, 0.7184f, 0), curve.Positions[3]);
            Near(new Vector3(10, 0.2f, 0), curve.Positions[5]);
            Assert.Equal(new Color32(153, 0, 102, 204), curve.Colors[2]);

            var widths = Line(l => { l.startWidth = 2; l.endWidth = 0.5f; l.SetPositions(new[] { Vector3.zero, new Vector3(10, 0, 0) }); });
            Near(new Vector3(0, 1, 0), widths.Positions[1]);
            Near(new Vector3(10, 0.25f, 0), widths.Positions[3]);

            Vector3[] three = { Vector3.zero, new Vector3(4, 0, 0), new Vector3(10, 0, 0) };
            var tile = Line(l => { l.positionCount = 3; l.SetPositions(three); l.widthMultiplier = 0.5f; l.textureMode = LineTextureMode.Tile; l.textureScale = new Vector2(2, 1); });
            Assert.Equal(new[] { 0f, 0f, 8f, 8f, 20f, 20f }, tile.Uvs.Select(u => u.x));
            var repeat = Line(l => { l.positionCount = 3; l.SetPositions(three); l.textureMode = LineTextureMode.RepeatPerSegment; });
            Assert.Equal(new[] { 0f, 0f, 1f, 1f, 2f, 2f }, repeat.Uvs.Select(u => u.x));
            var distribute = Line(l => { l.positionCount = 3; l.SetPositions(three); l.textureMode = LineTextureMode.DistributePerSegment; });
            Assert.Equal(new[] { 0f, 0f, 0.5f, 0.5f, 1f, 1f }, distribute.Uvs.Select(u => u.x));

            var gradient = Line(l =>
            {
                l.positionCount = 3;
                l.SetPositions(new[] { Vector3.zero, new Vector3(2, 0, 0), new Vector3(10, 0, 0) });
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.green, 0.5f), new GradientColorKey(Color.blue, 1) }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.2f, 1) });
                l.colorGradient = g;
            });
            Assert.Equal(new Color32(153, 102, 0, 214), gradient.Colors[2]);
            Assert.Equal(new Color32(0, 0, 255, 51), gradient.Colors[4]);

            var local = Line(l => { l.useWorldSpace = false; l.transform.position = new Vector3(5, 5, 0); l.SetPositions(new[] { Vector3.zero, new Vector3(10, 0, 0) }); });
            Near(new Vector3(5, 4.5f, 0), local.Positions[0]);
        }

        // Measured in the original player with Random.InitState(1234): ColorHSV takes four draws
        // (hue, saturation, value, alpha) whatever the overload.
        [Fact]
        public void ColorHsvDrawsHueSaturationValueAndAlpha()
        {
            UnityEngine.Random.InitState(1234);
            var c = UnityEngine.Random.ColorHSV(0f, 1f, 0f, 1f, 0f, 1f);
            Assert.Equal(0.0035922532f, UnityEngine.Random.value, 6);
            Assert.Equal(0.623438537f, c.r, 5);
            Assert.Equal(0.137176186f, c.g, 5);
            Assert.Equal(0.5177638f, c.b, 5);
            Assert.Equal(1f, c.a);
            UnityEngine.Random.InitState(1234);
            var ranged = UnityEngine.Random.ColorHSV(0.2f, 0.4f, 0.5f, 0.6f, 0.7f, 0.9f);
            var alpha = UnityEngine.Random.ColorHSV(0.2f, 0.4f, 0.5f, 0.6f, 0.7f, 0.9f, 0.1f, 0.3f);
            var plain = UnityEngine.Random.ColorHSV();
            Assert.Equal(0.82468766f, ranged.g, 5);
            Assert.Equal(0.229389668f, alpha.a, 5);
            Assert.Equal(0.2551408f, plain.r, 5);
        }

        // Measured in the original player: a bundle's bytes load as its file does, not twice, and
        // null or junk give null.
        [Fact]
        public void BundlesLoadFromMemory()
        {
            AssetSystem.Initialize(DataDir);
            var bytes = File.ReadAllBytes(Path.Combine(DataDir, "StreamingAssets", "hires_base_bundle"));
            Assert.Null(AssetBundle.LoadFromMemory(null));
            Assert.Null(AssetBundle.LoadFromMemory(new byte[] { 1, 2, 3, 4 }));
            var bundle = AssetBundle.LoadFromMemory(bytes);
            Assert.NotNull(bundle);
            try
            {
                Assert.Null(AssetBundle.LoadFromMemory(bytes));
                Assert.Empty(bundle.GetAllScenePaths());
                var all = bundle.LoadAllAssets();
                Assert.NotEmpty(all);
                Assert.All(bundle.LoadAllAssets<Texture2D>(), t => Assert.IsType<Texture2D>(t));
                Assert.Equal(all.OfType<Texture2D>().Count(), bundle.LoadAllAssets<Texture2D>().Length);
            }
            finally
            {
                bundle.Unload(true);
            }
        }

        // Measured in the original player: a stream of a bundle's bytes loads it (from the
        // stream's position), not twice; junk gives null; a null stream throws, its parameter
        // name Unity's sentence.
        [Fact]
        public void BundlesLoadFromStream()
        {
            AssetSystem.Initialize(DataDir);
            var bytes = File.ReadAllBytes(Path.Combine(DataDir, "StreamingAssets", "hires_base_bundle"));
            var thrown = Assert.Throws<ArgumentNullException>(() => AssetBundle.LoadFromStream(null));
            Assert.Equal("ManagedStream object must be non-null", thrown.ParamName);
            Assert.Null(AssetBundle.LoadFromStream(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
            var stream = new MemoryStream(new byte[] { 9, 9, 9 }.Concat(bytes).ToArray());
            stream.Position = 3;
            var bundle = AssetBundle.LoadFromStream(stream);
            Assert.NotNull(bundle);
            try
            {
                Assert.Equal(stream.Length, stream.Position);
                Assert.Null(AssetBundle.LoadFromStream(new MemoryStream(bytes), 0));
                Assert.NotEmpty(bundle.GetAllAssetNames());
                Assert.NotEmpty(bundle.LoadAllAssets());
            }
            finally
            {
                bundle.Unload(true);
            }
        }

        // The prefs file as the original player writes it (measured): entries it does not know are
        // kept, strings are base64 UTF-8, floats have six significant digits, a key set with another
        // type is replaced and reads as the default for the old type.
        [Fact]
        public void PlayerPrefsKeepUnitysFile()
        {
            var dir = Directory.CreateTempSubdirectory("oni-moono-prefs-").FullName;
            var saved = OniMoono.Engine.EngineConfig.PersistentDataPath;
            try
            {
                OniMoono.Engine.EngineConfig.PersistentDataPath = dir;
                File.WriteAllText(Path.Combine(dir, "prefs"), "<unity_prefs version_major=\"1\" version_minor=\"1\">\n\t<pref name=\"Screenmanager Resolution Width\" type=\"int\">1280</pref>\n</unity_prefs>");
                PlayerPrefs.Reload();
                Assert.Equal(1280, PlayerPrefs.GetInt("Screenmanager Resolution Width"));
                Assert.Equal(0, PlayerPrefs.GetInt("Missing"));
                Assert.Equal(7, PlayerPrefs.GetInt("Missing", 7));
                Assert.Equal("", PlayerPrefs.GetString("Missing"));
                PlayerPrefs.SetInt("PerfProbeInt", -42);
                PlayerPrefs.SetFloat("PerfProbeFloatThird", 1f / 3f);
                PlayerPrefs.SetString("PerfProbeString", "héllo, <x> & \"y\"\nline2");
                PlayerPrefs.SetInt("PerfProbeRetyped", 5);
                PlayerPrefs.SetString("PerfProbeRetyped", "now a string");
                Assert.Equal(0, PlayerPrefs.GetInt("PerfProbeRetyped"));
                Assert.Equal(0f, PlayerPrefs.GetFloat("PerfProbeInt"));
                PlayerPrefs.Save();
                var text = File.ReadAllText(Path.Combine(dir, "prefs"));
                Assert.StartsWith("<unity_prefs version_major=\"1\" version_minor=\"1\">\n\t<pref name=\"Screenmanager Resolution Width\" type=\"int\">1280</pref>", text);
                Assert.Contains("\t<pref name=\"PerfProbeString\" type=\"string\">aMOpbGxvLCA8eD4gJiAieSIKbGluZTI=</pref>", text);
                Assert.Contains("\t<pref name=\"PerfProbeFloatThird\" type=\"float\">0.333333</pref>", text);
                Assert.Contains("\t<pref name=\"PerfProbeRetyped\" type=\"string\">bm93IGEgc3RyaW5n</pref>", text);
                Assert.EndsWith("</unity_prefs>", text);
                PlayerPrefs.Reload();
                Assert.Equal("héllo, <x> & \"y\"\nline2", PlayerPrefs.GetString("PerfProbeString"));
                Assert.Equal(-42, PlayerPrefs.GetInt("PerfProbeInt"));
            }
            finally
            {
                OniMoono.Engine.EngineConfig.PersistentDataPath = saved;
                PlayerPrefs.Reload();
                Directory.Delete(dir, true);
            }
        }

        // Measured in the original player: a zero scale component inverts to zero on that axis, per
        // level, instead of the singular matrix's all-zero inverse (Rocketry Expanded scales a UI
        // image by (1, 1), z 0, which then caught every click).
        [Fact]
        public void ZeroScaleAxesInvertToZero()
        {
            var a = new GameObject("a").transform;
            a.position = new Vector3(100, 50, 0);
            a.localScale = new Vector3(1, 1, 0);
            Near(new Vector3(50, 10, 0), a.InverseTransformPoint(new Vector3(150, 60, 7)));
            Near(new Vector3(3, 4, 0), a.InverseTransformVector(new Vector3(3, 4, 5)));
            Near(new Vector3(3, 4, 5), a.InverseTransformDirection(new Vector3(3, 4, 5)));
            Assert.Equal(0f, a.worldToLocalMatrix.m22);
            Assert.Equal(-100f, a.worldToLocalMatrix.m03);
            a.localScale = new Vector3(2, 0, 3);
            Near(new Vector3(25, 0, 2.3333f), a.InverseTransformPoint(new Vector3(150, 60, 7)));
            a.localScale = Vector3.zero;
            Near(Vector3.zero, a.InverseTransformPoint(new Vector3(150, 60, 7)));
            var parent = new GameObject("parent").transform;
            parent.localScale = new Vector3(2, 2, 0);
            var child = new GameObject("child").transform;
            child.SetParent(parent, false);
            child.localPosition = new Vector3(10, 20, 30);
            Near(new Vector3(20, 40, 0), child.position);
            Near(new Vector3(10, 10, -30), child.InverseTransformPoint(new Vector3(40, 60, 9)));

            var canvas = new GameObject("canvas").AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var image = new GameObject("image", typeof(RectTransform)).GetComponent<RectTransform>();
            image.SetParent(canvas.transform, false);
            image.sizeDelta = new Vector2(24, 24);
            image.localScale = new Vector3(1, 1, 0);
            var centre = new Vector2(image.position.x, image.position.y);
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(image, centre + new Vector2(10, 10)));
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(image, centre + new Vector2(-300, -200)));

            // A menu popping in from scale 0 (measured): its collapsed buttons contain only the point
            // they collapsed to; at half scale, their halved rects.
            var content = new GameObject("content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(canvas.transform, false);
            var below = new GameObject("below", typeof(RectTransform)).GetComponent<RectTransform>();
            below.SetParent(content, false);
            below.sizeDelta = new Vector2(200, 30);
            below.anchoredPosition = new Vector2(0, -50);
            var at = new Vector2(content.position.x, content.position.y);
            content.localScale = Vector3.zero;
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at));
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(0, 50)));
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(90, 0)));
            content.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at));
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(0, -25)));
            // Collapsed on one axis: the line it collapsed to.
            content.localScale = new Vector3(0, 1, 1);
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(0, -50)));
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(10, -50)));
            content.localScale = new Vector3(1, 0, 1);
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(90, 0)));
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(90, -5)));
            content.localScale = Vector3.one;
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(0, -50)));
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at));
            // The offset shrinks the rect (left, bottom, right, top), as a graphic's raycastPadding.
            var shrunk = new Vector4(10, 10, 10, 10);
            Assert.False(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(95, -50), null, shrunk));
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(85, -50), null, shrunk));
            Assert.True(RectTransformUtility.RectangleContainsScreenPoint(below, at + new Vector2(105, -50), null, -shrunk));
        }

        [Fact]
        public void GradientsTakeColorAndAlphaKeys()
        {
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.blue, 1f), new GradientColorKey(Color.red, 0f) },
                new[] { new GradientAlphaKey(0f, 1f), new GradientAlphaKey(1f, 0f) });
            Assert.Equal(0f, gradient.colorKeys[0].time);
            Assert.Equal(1f, gradient.alphaKeys[0].alpha);
            var middle = gradient.Evaluate(0.5f);
            Assert.Equal(0.5f, middle.r, 3);
            Assert.Equal(0.5f, middle.b, 3);
            Assert.Equal(0.5f, middle.a, 3);
        }

        // The third hundred Workshop mods' API (measured in the original player): MinMaxCurve's
        // slots and Evaluate, the modules' defaults and setters, blackTexture, safeArea.
        [Fact]
        public void MinMaxCurvesUseTheMaxSlotAndLerpOnlyBetweenTwoValues()
        {
            var constant = new ParticleSystem.MinMaxCurve(3f);
            Assert.Equal((ParticleSystemCurveMode.Constant, 3f, 0f, 3f, 0f), (constant.mode, constant.constant, constant.constantMin, constant.constantMax, constant.curveMultiplier));
            Assert.Equal(3f, constant.Evaluate(0.5f, 0.25f));
            var two = new ParticleSystem.MinMaxCurve(1f, 2f);
            Assert.Equal((ParticleSystemCurveMode.TwoConstants, 2f), (two.mode, two.constant));
            Assert.Equal(2f, two.Evaluate(0.5f));
            Assert.Equal(1.25f, two.Evaluate(0.5f, 0.25f));
            var ramp = AnimationCurve.Linear(0, 0, 1, 4);
            var curve = new ParticleSystem.MinMaxCurve(2f, ramp);
            Assert.Same(ramp, curve.curveMax);
            Assert.Null(curve.curveMin);
            Assert.Equal(4f, curve.Evaluate(0.5f, 0.25f));
            var curves = new ParticleSystem.MinMaxCurve(1f, ramp, AnimationCurve.Constant(0, 1, 1));
            Assert.Equal(1f, curves.Evaluate(0.5f));
            Assert.Equal(1.75f, curves.Evaluate(0.5f, 0.25f), 4);
            ParticleSystem.MinMaxCurve seven = 7f;
            Assert.Equal(7f, seven.constantMax);

            var ps = new GameObject("particles").AddComponent<ParticleSystem>();
            var main = ps.main;
            var emission = ps.emission;
            var noise = ps.noise;
            Assert.Equal((5f, 0f, 1f), (main.startLifetime.constant, main.startLifetime.constantMin, main.startLifetime.curveMultiplier));
            Assert.Equal(10f, emission.rateOverTime.constant);
            Assert.Equal((1f, false, 1000), (main.simulationSpeed, main.prewarm, main.maxParticles));
            Assert.Equal((false, 1f, 1f), (noise.enabled, noise.strength.constant, noise.strengthMultiplier));
            main.startLifetime = 2.5f;
            emission.rateOverTime = 40f;
            main.simulationSpeed = 0.5f;
            main.prewarm = true;
            main.maxParticles = -3;
            noise.strength = 0.75f;
            Assert.Equal((ParticleSystemCurveMode.Constant, 2.5f, 1f), (main.startLifetime.mode, main.startLifetime.constant, main.startLifetime.curveMultiplier));
            Assert.Equal(40f, emission.rateOverTime.constant);
            Assert.Equal((0.5f, true, 0), (main.simulationSpeed, main.prewarm, main.maxParticles));
            Assert.Equal((false, 0.75f, 0.75f), (noise.enabled, noise.strength.constant, noise.strengthMultiplier));
            Object.DestroyImmediate(ps.gameObject);

            var black = Texture2D.blackTexture;
            Assert.Same(black, Texture2D.blackTexture);
            Assert.Equal(("UnityBlack", 4, 4, TextureFormat.RGBA32), (black.name, black.width, black.height, black.format));
            Assert.Equal(new Color32(0, 0, 0, 0), black.GetPixels32()[0]);
            Assert.Equal(new Rect(0, 0, Screen.width, Screen.height), Screen.safeArea);
        }

        // RecalculateNormals (measured in the original player): area-weighted face normals summed
        // per index, no merging by position, zero where no proper triangle uses a vertex.
        [Fact]
        public void RecalculatedNormalsAreAreaWeightedPerIndex()
        {
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 3), new Vector3(5, 5, 5), new Vector3(6, 5, 5), new Vector3(5, 6, 5) },
                triangles = new[] { 0, 1, 2, 0, 3, 1, 4, 6, 5 },
            };
            mesh.RecalculateNormals();
            var n = mesh.normals;
            var shared = new Vector3(0, 0.94868f, 0.31623f);
            Assert.True((n[0] - shared).magnitude < 1e-4f && (n[1] - shared).magnitude < 1e-4f);
            Assert.Equal(new Vector3(0, 0, 1), n[2]);
            Assert.Equal(new Vector3(0, 1, 0), n[3]);
            Assert.Equal(new Vector3(0, 0, -1), n[4]);
            var split = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.zero, new Vector3(0, 0, 3), Vector3.right }, triangles = new[] { 0, 1, 2, 3, 4, 5 } };
            split.RecalculateNormals();
            Assert.Equal(new Vector3(0, 0, 1), split.normals[0]);
            Assert.Equal(new Vector3(0, 1, 0), split.normals[3]);
            var degenerate = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, new Vector3(2, 0, 0), new Vector3(9, 9, 9) }, triangles = new[] { 0, 1, 2 } };
            degenerate.RecalculateNormals();
            Assert.All(degenerate.normals, v => Assert.Equal(Vector3.zero, v));
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(split);
            Object.DestroyImmediate(degenerate);
        }

        // Fast Save's timelapse images (measured in the original player): RGBA32's graphics format
        // whatever isSRGB says, ARGB32's unnamed 88; PNGs from raw rows (bottom row first) with a
        // row stride, BGRA swizzled and R8 as grey.
        [Fact]
        public void PngsOfRawTexelsDecodeTheirFormat()
        {
            Assert.Equal(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, true));
            Assert.Equal((UnityEngine.Experimental.Rendering.GraphicsFormat)88, UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.ARGB32, false));
            Assert.Equal(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8_UNorm, UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGB24, false));
            var check = new Texture2D(1, 1);
            var rgb = ImageConversion.EncodeArrayToPNG(new byte[] { 255, 0, 0, 0, 255, 0, 9, 9, 0, 0, 255, 10, 20, 30, 9, 9 }, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8_UNorm, 2, 2, 8);
            Assert.True(check.LoadImage(rgb));
            Assert.Equal(new Color32[] { new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255), new(10, 20, 30, 255) }, check.GetPixels32());
            Assert.True(check.LoadImage(ImageConversion.EncodeArrayToPNG(new byte[] { 255, 0, 0, 255 }, UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_UNorm, 1, 1)));
            Assert.Equal(new Color32(0, 0, 255, 255), check.GetPixels32()[0]);
            Assert.True(check.LoadImage(ImageConversion.EncodeArrayToPNG(new byte[] { 0, 100, 200, 255 }, UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm, 2, 2)));
            Assert.Equal(new Color32(100, 100, 100, 255), check.GetPixels32()[1]);
            Object.DestroyImmediate(check);
        }

        // A read-back is done three frames after the request (callback then) and its data is gone
        // the frame after (measured in the original player).
        [Fact]
        public void ReadbacksAreDoneThreeFramesLaterAndReadableForThatFrame()
        {
            var texture = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            texture.SetPixels32(new Color32[] { new(10, 20, 30, 40), new(50, 60, 70, 80) });
            texture.Apply();
            int calls = 0;
            var request = UnityEngine.Rendering.AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, _ => calls++);
            Assert.False(request.done);
            Assert.False(request.hasError);
            Assert.Equal(2, request.width);
            Frame();
            Frame();
            Assert.False(request.done);
            Frame();
            Assert.True(request.done);
            Assert.Equal(1, calls);
            Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }, request.GetData<byte>().ToArray());
            Frame();
            Assert.Throws<InvalidOperationException>(() => request.GetData<byte>());
            Object.DestroyImmediate(texture);
        }

        // Color's named colours as the original player gives them (tools/UnityProbe,
        // PROBE_SCENARIO=statics): 157 of them, with float bits that are not always n / 255f.
        // A reported SkillsInfoScreen build calls Color.darkGreen (its Workshop build does not).
        [Fact]
        public void NamedColoursHaveTheMeasuredBits()
        {
            var names = typeof(Color).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.Equal(157, names.Count(p => p.PropertyType == typeof(Color)));
            static uint[] Bits(Color c) => [Bits1(c.r), Bits1(c.g), Bits1(c.b), Bits1(c.a)];
            static uint Bits1(float f) => BitConverter.SingleToUInt32Bits(f);
            Assert.Equal(new uint[] { 0, 0x3EC8C8CA, 0, 0x3F800000 }, Bits(Color.darkGreen));
            Assert.Equal(new uint[] { 0, 0, 0x3F008081, 0x3F800000 }, Bits(Color.navyBlue));
            Assert.Equal(new uint[] { 0x3ECCCCCD, 0x3E4CCCCD, 0x3F19999A, 0x3F800000 }, Bits(Color.rebeccaPurple));
            Assert.Equal(new uint[] { 0x3F5CDCDF, 0x3E44C4C7, 0x3E48C8C7, 0x3F800000 }, Bits(Color.softRed));
            Assert.Equal(Bits(Color.yellow), Bits(Color.yellowNice));
            Assert.Equal(new uint[] { 0, 0, 0, 0 }, Bits(Color.clear));
        }

        // Measured: False in the original player.
        [Fact]
        public void GarbageCollectorIsNotIncremental() => Assert.False(UnityEngine.Scripting.GarbageCollector.isIncremental);

        // Mods throw Unity's exceptions although the engine does not: Advanced Mopping's filter
        // panel names MissingComponentException, and without the type the panel's method failed to
        // compile, so the mod blocked every new mop order. Every public exception of Unity's
        // CoreModule is ours too, with the same base type.
        [Fact]
        public void EveryCoreModuleExceptionTypeExists()
        {
            using var stream = File.OpenRead(Path.Combine(DataDir, "Managed", "UnityEngine.CoreModule.dll"));
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var ours = typeof(Object).Assembly;
            int seen = 0;
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                var name = md.GetString(type.Name);
                if ((type.Attributes & System.Reflection.TypeAttributes.VisibilityMask) != System.Reflection.TypeAttributes.Public
                    || !name.EndsWith("Exception", StringComparison.Ordinal)) continue;
                var fullName = md.GetString(type.Namespace) + "." + name;
                var mine = ours.GetType(fullName);
                Assert.True(mine != null, $"{fullName} is missing");
                Assert.True(typeof(Exception).IsAssignableFrom(mine), $"{fullName} is not an exception");
                seen++;
            }
            Assert.True(seen >= 6, $"only {seen} exception types found");
            var e = new MissingComponentException("Liquid filter requires KScrollRect.");
            Assert.IsAssignableFrom<SystemException>(e);
            Assert.Equal(unchecked((int)0x80004003), e.HResult);
            Assert.Equal("A Unity Runtime error occurred!", new UnassignedReferenceException().Message);
            Assert.Equal("user\nfailed", new UnityEngine.Assertions.AssertionException("failed", "user").Message);
        }
    }
}
