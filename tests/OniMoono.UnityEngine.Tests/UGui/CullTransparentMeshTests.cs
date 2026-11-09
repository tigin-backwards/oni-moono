using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests.UGui
{
    /// CanvasRenderer.cullTransparentMesh, as measured in the original player (UnityProbe
    /// cullalpha; docs/cleanroom/ugui.md, "CanvasRenderer.cullTransparentMesh").
    public class CullTransparentMeshTests : UGuiTest, IDisposable
    {
        readonly RecordingDevice device = new();
        readonly RenderDevice previous = GraphicsDevice.Current;
        readonly Canvas canvas;

        public CullTransparentMeshTests()
        {
            UseGameAssets();
            FrameRenderer.Install();
            GraphicsDevice.Install(device);
            canvas = NewCanvas("CullCanvas");
        }

        public new void Dispose()
        {
            GraphicsDevice.Install(previous);
            base.Dispose();
        }

        /// A bare renderer under the canvas drawing a triangle whose vertices have these alphas,
        /// with a material of its own named after it.
        CanvasRenderer Element(string name, bool cull, params float[] alphas)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var r = go.AddComponent<CanvasRenderer>();
            r.cullTransparentMesh = cull;
            SetAlphas(r, alphas);
            r.materialCount = 1;
            r.SetMaterial(new Material(Shader.Find("UI/Default")) { name = name }, 0);
            return r;
        }

        static void SetAlphas(CanvasRenderer r, params float[] alphas)
        {
            var mesh = new Mesh { vertices = [Vector3.zero, new Vector3(0f, 100f), new Vector3(100f, 0f)] };
            mesh.colors = alphas.Select(a => new Color(1f, 0f, 0f, a)).ToArray();
            mesh.triangles = [0, 1, 2];
            r.SetMesh(mesh);
        }

        /// The names of the materials drawn in the next frame.
        HashSet<string> DrawnNextFrame()
        {
            device.MeshDraws.Clear();
            Frame();
            return device.MeshDraws.Select(d => d.Material.name).ToHashSet();
        }

        [Fact]
        public void ARendererAddedAtRunTimeHasTheFlagOnAndACopyKeepsItsSources()
        {
            var plain = new GameObject("Plain").AddComponent<CanvasRenderer>();
            var image = new GameObject("Image").AddComponent<Image>();
            Assert.True(plain.cullTransparentMesh);
            Assert.True(image.canvasRenderer.cullTransparentMesh);
            plain.cullTransparentMesh = false;
            var copy = Object.Instantiate(plain.gameObject);
            Assert.False(copy.GetComponent<CanvasRenderer>().cullTransparentMesh);
        }

        [Fact]
        public void WithTheFlagAMeshWhoseEveryVertexAlphaIsZeroIsNotDrawn()
        {
            Element("Clear", true, 0f, 0f, 0f);
            Element("ClearNoFlag", false, 0f, 0f, 0f);
            Element("OneVertex", true, 0f, 0f, 1f);
            Element("Byte1", true, 1f / 255f, 1f / 255f, 1f / 255f);
            Element("Negative", true, -0.5f, -0.5f, -0.5f);
            // Close to zero is below 0.001 as a float.
            float below = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(0.001f) - 1);
            Element("JustBelow", true, below, 0.0005f, 0f);
            Element("AtThreshold", true, 0.001f, 0f, 0f);
            var drawn = DrawnNextFrame();
            Assert.DoesNotContain("JustBelow", drawn);
            Assert.Contains("AtThreshold", drawn);
            Assert.DoesNotContain("Clear", drawn);
            Assert.Contains("ClearNoFlag", drawn);
            Assert.Contains("OneVertex", drawn);
            Assert.Contains("Byte1", drawn);
            Assert.DoesNotContain("Negative", drawn);
        }

        [Fact]
        public void TheRenderersAlphaAndTheGroupsDoNotEnterTheTest()
        {
            // A small vertex alpha stays drawn under a small renderer or group alpha; a zero
            // renderer alpha hides an element with or without the flag.
            Element("TinyRenderer", true, 1f / 255f, 1f / 255f, 1f / 255f).SetAlpha(0.1f);
            var grouped = Element("TinyGroup", true, 1f, 1f, 1f);
            grouped.gameObject.AddComponent<CanvasGroup>().alpha = 1e-5f;
            Element("ZeroRendererNoFlag", false, 1f, 1f, 1f).SetAlpha(0f);
            var drawn = DrawnNextFrame();
            Assert.Contains("TinyRenderer", drawn);
            Assert.Contains("TinyGroup", drawn);
            Assert.DoesNotContain("ZeroRendererNoFlag", drawn);
        }

        [Fact]
        public void ChangesToTheFlagOrTheVerticesShowInTheNextFrame()
        {
            var flipOff = Element("FlipOff", true, 0f, 0f, 0f);
            var flipOn = Element("FlipOn", false, 0f, 0f, 0f);
            var up = Element("Up", true, 0f, 0f, 0f);
            var down = Element("Down", true, 1f, 1f, 1f);
            var drawn = DrawnNextFrame();
            Assert.Equal(new[] { "Down", "FlipOn" }, drawn.OrderBy(n => n));
            flipOff.cullTransparentMesh = false;
            flipOn.cullTransparentMesh = true;
            SetAlphas(up, 1f, 1f, 1f);
            SetAlphas(down, 0f, 0f, 0f);
            drawn = DrawnNextFrame();
            Assert.Equal(new[] { "FlipOff", "Up" }, drawn.OrderBy(n => n));
        }

        [Fact]
        public void ACulledGraphicKeepsItsDepthAndTakesRaycasts()
        {
            var system = new GameObject("EventSystem").AddComponent<EventSystem>();
            canvas.gameObject.AddComponent<GraphicRaycaster>();
            var clear = NewRect("Clear", canvas.transform, 100, 100, typeof(Image)).GetComponent<Image>();
            clear.color = Color.clear;
            var after = NewRect("After", canvas.transform, 10, 10, typeof(Image)).GetComponent<Image>();
            after.rectTransform.anchoredPosition = new Vector2(300, 300);
            var drawn = DrawnNextFrame();
            Assert.Single(device.MeshDraws); // "After" alone
            Assert.Equal(0, clear.canvasRenderer.absoluteDepth);
            Assert.Equal(1, after.canvasRenderer.absoluteDepth);
            var hits = new List<RaycastResult>();
            system.RaycastAll(new PointerEventData(system) { position = new Vector2(512, 384) }, hits);
            Assert.Equal("Clear", Assert.Single(hits).gameObject.name);
        }

        [Fact]
        public void ARendererWithoutATextureUnderAShaderWithoutMainTexLogsNothing()
        {
            // The original's canvas logs nothing here; Material.mainTexture would log an error,
            // which the game turns into its crash dialog.
            var r = Element("Colored", false, 1f, 1f, 1f);
            r.SetMaterial(new Material(Shader.Find("Hidden/Internal-Colored")) { name = "Colored" }, 0);
            int errors = 0;
            Application.LogCallback count = (_, _, type) => { if (type is LogType.Error or LogType.Exception) errors++; };
            Application.logMessageReceived += count;
            try
            {
                Assert.Contains("Colored", DrawnNextFrame());
            }
            finally
            {
                Application.logMessageReceived -= count;
            }
            Assert.Equal(0, errors);
        }

        [Fact]
        public void AMaskWhoseGraphicIsCulledDrawsNeitherItsPushNorItsPop()
        {
            int Draws(bool cull)
            {
                var mask = NewRect("Mask" + cull, canvas.transform, 100, 100, typeof(Image)).GetComponent<Image>();
                mask.color = Color.clear;
                mask.canvasRenderer.cullTransparentMesh = cull;
                mask.gameObject.AddComponent<Mask>();
                NewRect("Child", mask.transform, 50, 50, typeof(Image));
                DrawnNextFrame();
                Object.DestroyImmediate(mask.gameObject);
                return device.MeshDraws.Count;
            }
            Assert.Equal(3, Draws(cull: false));
            Assert.Equal(1, Draws(cull: true));
        }
    }
}
