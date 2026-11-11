using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.UI;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The layered order of an overlay canvas's draws (CanvasLayering): draws that share a pixel
    /// keep their order, barriers keep their place, and draws of one state that share no pixel
    /// with what lies between them come together. Plain data, no game files.
    public class CanvasLayeringTests
    {
        const int W = 1280, H = 720;

        /// A draw: its state (or Barrier) and its pixel-centre ranges (x0 > x1: none).
        readonly record struct D(int State, int X0, int Y0, int X1, int Y1)
        {
            public static D Rect(int state, int x, int y, int w, int h) => new(state, x, y, x + w - 1, y + h - 1);
            public static D None(int state) => new(state, 0, 0, -1, -1);
            public static D Barrier(int x, int y, int w, int h) => Rect(CanvasLayering.Barrier, x, y, w, h);
        }

        static int[] Order(CanvasLayering l, params D[] draws)
        {
            int states = 0;
            foreach (var d in draws) states = Math.Max(states, d.State + 1);
            l.Reset(draws.Length, states);
            for (int i = 0; i < draws.Length; i++)
            {
                l.State[i] = draws[i].State;
                l.X0[i] = draws[i].X0;
                l.Y0[i] = draws[i].Y0;
                l.X1[i] = draws[i].X1;
                l.Y1[i] = draws[i].Y1;
            }
            l.Run(W, H);
            return l.Order[..draws.Length];
        }

        static int[] Order(params D[] draws) => Order(new CanvasLayering(), draws);

        /// Runs of one state in the order (the draws a merge leaves).
        static int Runs(D[] draws, int[] order)
        {
            int runs = 0;
            for (int k = 0; k < order.Length; k++)
                if (k == 0 || draws[order[k]].State == CanvasLayering.Barrier || draws[order[k]].State != draws[order[k - 1]].State) runs++;
            return runs;
        }

        static bool Share(D a, D b) => a.X0 <= a.X1 && b.X0 <= b.X1 && a.Y0 <= a.Y1 && b.Y0 <= b.Y1
                                       && a.X0 <= b.X1 && b.X0 <= a.X1 && a.Y0 <= b.Y1 && b.Y0 <= a.Y1;

        /// Every pair that shares a pixel, or has a barrier, keeps its order; the order is a
        /// permutation.
        static void AssertExact(D[] draws, int[] order)
        {
            var at = new int[draws.Length];
            Array.Fill(at, -1);
            for (int k = 0; k < order.Length; k++)
            {
                Assert.Equal(-1, at[order[k]]);
                at[order[k]] = k;
            }
            for (int i = 0; i < draws.Length; i++)
                for (int j = i + 1; j < draws.Length; j++)
                    if (draws[i].State == CanvasLayering.Barrier || draws[j].State == CanvasLayering.Barrier || Share(draws[i], draws[j]))
                        Assert.True(at[i] < at[j], $"draw {j} moved before draw {i}, which it overlaps");
        }

        [Fact]
        public void RowsOfTheSameElementsDrawTogether()
        {
            // The Load screen's colony view: each row a background, a text over it and an icon
            // over that, rows one under the other (sharing an edge, not a pixel centre).
            var draws = new List<D>();
            for (int row = 0; row < 10; row++)
            {
                draws.Add(D.Rect(0, 0, row * 60, 400, 60));
                draws.Add(D.Rect(1, 10, row * 60 + 10, 200, 40));
                draws.Add(D.Rect(2, 300, row * 60 + 10, 40, 40));
            }
            var d = draws.ToArray();
            var order = Order(d);
            AssertExact(d, order);
            Assert.Equal(3, Runs(d, order));
            Assert.Equal([0, 3, 6, 9, 12, 15, 18, 21, 24, 27], order[..10]);
        }

        [Fact]
        public void DrawsThatOverlapKeepTheirOrder()
        {
            // B over A, then A again over B: the second A cannot join the first.
            var d = new[] { D.Rect(0, 0, 0, 100, 100), D.Rect(1, 50, 50, 100, 100), D.Rect(0, 120, 120, 50, 50) };
            var order = Order(d);
            Assert.Equal([0, 1, 2], order);
            AssertExact(d, order);
        }

        [Fact]
        public void DrawsOfOneStateThatOverlapJoinEachOther()
        {
            // Two of A overlapping (one draw keeps their triangles in order), B elsewhere between.
            var d = new[] { D.Rect(0, 0, 0, 100, 100), D.Rect(1, 500, 500, 10, 10), D.Rect(0, 50, 50, 100, 100) };
            var order = Order(d);
            Assert.Equal([0, 2, 1], order);
            Assert.Equal(2, Runs(d, order));
        }

        [Fact]
        public void ASameStateDrawAboveAnotherStateGoesAboveIt()
        {
            // A, B over A, A over B and over the first A: the last A is above B's layer.
            var l = new CanvasLayering();
            var d = new[] { D.Rect(0, 0, 0, 100, 100), D.Rect(1, 0, 0, 100, 100), D.Rect(0, 0, 0, 100, 100) };
            var order = Order(l, d);
            Assert.Equal([0, 1, 2], order);
            Assert.Equal([0, 1, 2], l.Layer[..3]);
        }

        [Fact]
        public void BarriersKeepTheirPlace()
        {
            // A stencil write between draws that share no pixel with it or each other: nothing
            // crosses it, but each side is still ordered among itself.
            var d = new[]
            {
                D.Rect(0, 0, 0, 10, 10), D.Rect(1, 100, 0, 10, 10), D.Rect(0, 200, 0, 10, 10),
                D.Barrier(600, 600, 10, 10),
                D.Rect(0, 300, 0, 10, 10), D.Rect(1, 400, 0, 10, 10), D.Rect(0, 500, 0, 10, 10),
            };
            var order = Order(d);
            Assert.Equal([0, 2, 1, 3, 4, 6, 5], order);
            AssertExact(d, order);
        }

        [Fact]
        public void ADrawThatReachesNoPixelGoesWithTheFirstOfItsState()
        {
            // Off the screen (a scrolled list's rows under a stencil Mask, not culled): it can be
            // drawn anywhere, so it joins the first draw of its state.
            var d = new[] { D.Rect(0, 0, 0, 100, 100), D.Rect(1, 0, 0, 100, 100), D.None(0) };
            var order = Order(d);
            Assert.Equal([0, 2, 1], order);
        }

        [Fact]
        public void PixelCentresDecideWhatABoundsReaches()
        {
            // Sharing an edge: no pixel centre in both.
            Assert.True(CanvasLayering.Covers(0, 0, 70, 10, W, H, out _, out _, out int a1, out _));
            Assert.True(CanvasLayering.Covers(70, 0, 140, 10, W, H, out int b0, out _, out _, out _));
            Assert.Equal(69, a1);
            Assert.Equal(70, b0);
            // An edge on a pixel centre: both may reach it.
            Assert.True(CanvasLayering.Covers(0, 0, 69.5, 10, W, H, out _, out _, out a1, out _));
            Assert.True(CanvasLayering.Covers(69.5, 0, 140, 10, W, H, out b0, out _, out _, out _));
            Assert.True(a1 >= b0);
            // Within an eighth of a pixel of a centre counts too (the device's rounding).
            Assert.True(CanvasLayering.Covers(10.6, 0, 20.4, 10, W, H, out int c0, out _, out int c1, out _));
            Assert.Equal(10, c0);
            Assert.Equal(20, c1);
            // Between two centres: none.
            Assert.False(CanvasLayering.Covers(10.65, 0, 11.35, 10, W, H, out _, out _, out _, out _));
            // Off the target, and clamped to it.
            Assert.False(CanvasLayering.Covers(0, -5000, 400, -4000, W, H, out _, out _, out _, out _));
            Assert.False(CanvasLayering.Covers(W + 1, 0, W + 100, 10, W, H, out _, out _, out _, out _));
            Assert.True(CanvasLayering.Covers(-100, -100, 5000, 5000, W, H, out c0, out int y0, out c1, out int y1));
            Assert.Equal((0, 0, W - 1, H - 1), (c0, y0, c1, y1));
            // Not a number: none (the caller makes such a draw a barrier).
            Assert.False(CanvasLayering.Covers(double.NaN, 0, 10, 10, W, H, out _, out _, out _, out _));
        }

        /// The layers the grid finds are the rule's own, tested against every earlier draw.
        static int[] BruteLayers(D[] d)
        {
            var layers = new int[d.Length];
            int from = 0;
            for (int i = 0; i < d.Length; i++)
            {
                if (d[i].State == CanvasLayering.Barrier)
                {
                    from = i + 1;
                    continue;
                }
                int layer = 0;
                for (int j = from; j < i; j++)
                    if (Share(d[i], d[j])) layer = Math.Max(layer, d[j].State == d[i].State ? layers[j] : layers[j] + 1);
                layers[i] = layer;
            }
            return layers;
        }

        [Fact]
        public void RandomDrawsKeepEveryOverlapInOrder()
        {
            var random = new System.Random(7);
            var l = new CanvasLayering();
            for (int round = 0; round < 300; round++)
            {
                int n = random.Next(1, round % 10 == 0 ? 2000 : 120);
                int states = random.Next(1, 8);
                var d = new D[n];
                for (int i = 0; i < n; i++)
                {
                    int kind = random.Next(100);
                    if (kind < 3) d[i] = D.Barrier(random.Next(W), random.Next(H), 10, 10);
                    else if (kind < 8) d[i] = D.None(random.Next(states));
                    else
                    {
                        // Mostly small, some screen-sized, some across cell edges.
                        int w = kind < 12 ? random.Next(300, W) : random.Next(1, 90), h = kind < 12 ? random.Next(200, H) : random.Next(1, 90);
                        d[i] = D.Rect(random.Next(states), random.Next(W - Math.Min(w, W - 1)), random.Next(H - Math.Min(h, H - 1)), w, h);
                    }
                }
                // One layering object for every round: its arrays grow and shrink with n.
                var order = Order(l, d);
                AssertExact(d, order);
                var brute = BruteLayers(d);
                for (int i = 0; i < n; i++)
                    if (d[i].State != CanvasLayering.Barrier) Assert.Equal(brute[i], l.Layer[i]);
            }
        }

        [Fact]
        public void ALayeringOfThousandsOfRowsAllocatesNothingOnceGrown()
        {
            var draws = new List<D>();
            for (int row = 0; row < 700; row++)
            {
                int y = row * 60 % (H - 60);
                int x = row * 60 / (H - 60) * 420 % (W - 420);
                draws.Add(D.Rect(0, x, y, 400, 60));
                draws.Add(D.Rect(1, x + 10, y + 10, 200, 40));
                for (int icon = 0; icon < 5; icon++) draws.Add(D.Rect(2 + icon, x + 220 + icon * 30, y + 10, 25, 25));
            }
            var d = draws.ToArray();
            var l = new CanvasLayering();
            var order = Order(l, d);
            AssertExact(d, order);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5; i++) l.Run(W, H);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    /// An overlay canvas's draws as the device gets them, reordered (CanvasSystem.OrderFor) and
    /// in hierarchy order: the game's UI shaders, so these need the game's files.
    public class CanvasLayeredDrawTests : EngineTest
    {
        sealed class Overlay : IDisposable
        {
            public readonly RecordingDevice Device = new();
            readonly RenderDevice previous;
            public readonly GameObject Root;
            readonly bool reorder = CanvasSystem.Reorder;

            public Overlay()
            {
                AssetSystem.Initialize(GameInstall.DataDir);
                ProjectSettings.Load();
                FrameRenderer.Install();
                CanvasSystem.Install();
                previous = GraphicsDevice.Current;
                GraphicsDevice.Install(Device);
                Root = new GameObject("Overlay", typeof(RectTransform));
                Root.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }

            /// The materials of a frame's draws, in order, with the batcher on or off.
            public Material[] Draws(bool reorder)
            {
                CanvasSystem.Reorder = reorder;
                Device.MeshDrawRequests.Clear();
                PlayerLoop.RunFrame(1f / 60f);
                return Device.MeshDrawRequests.Select(d => d.Material).ToArray();
            }

            public void Dispose()
            {
                CanvasSystem.Reorder = reorder;
                Object.DestroyImmediate(Root);
                GraphicsDevice.Install(previous);
            }
        }

        static Material Ui() => new(Shader.Find("UI/Default"));

        /// A w × h quad at (x, y) of the parent's space (the root canvas's: 0 at the screen's
        /// centre, 1024 × 768).
        static CanvasRenderer Quad(Transform parent, string name, float x, float y, float w, float h, Material material)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<CanvasRenderer>();
            renderer.SetMesh(new Mesh
            {
                vertices = [new Vector3(x, y), new Vector3(x, y + h), new Vector3(x + w, y + h), new Vector3(x + w, y)],
                triangles = [0, 1, 2, 2, 3, 0],
            });
            renderer.materialCount = 1;
            renderer.SetMaterial(material, 0);
            return renderer;
        }

        [Fact]
        public void RowsOfABackgroundAndATextDrawInTwoDraws()
        {
            using var scene = new Overlay();
            Material background = Ui(), text = Ui();
            for (int row = 0; row < 20; row++)
            {
                Quad(scene.Root.transform, "Row", -300f, row * 30f - 300f, 300f, 30f, background);
                Quad(scene.Root.transform, "Text", -290f, row * 30f - 295f, 200f, 20f, text);
            }
            Assert.Equal(40, scene.Draws(reorder: false).Length);
            Assert.Equal([background, text], scene.Draws(reorder: true));
        }

        [Fact]
        public void OverlappingDrawsOfOtherMaterialsKeepTheirOrder()
        {
            using var scene = new Overlay();
            Material a = Ui(), b = Ui();
            // A, B over it, A over B: three draws either way.
            Quad(scene.Root.transform, "A1", -100f, -100f, 100f, 100f, a);
            Quad(scene.Root.transform, "B", -50f, -50f, 100f, 100f, b);
            Quad(scene.Root.transform, "A2", 20f, 20f, 50f, 50f, a);
            Assert.Equal([a, b, a], scene.Draws(reorder: true));
            Assert.Equal([a, b, a], scene.Draws(reorder: false));
        }

        [Fact]
        public void AStencilWriteIsABarrier()
        {
            using var scene = new Overlay();
            Material a = Ui(), b = Ui(), mask = Ui();
            mask.SetFloat("_StencilOp", 2f); // Replace, as a Mask's graphic writes
            Quad(scene.Root.transform, "A1", -300f, 0f, 10f, 10f, a);
            Quad(scene.Root.transform, "B1", -200f, 0f, 10f, 10f, b);
            Quad(scene.Root.transform, "Mask", 0f, 300f, 10f, 10f, mask);
            Quad(scene.Root.transform, "A2", 100f, 0f, 10f, 10f, a);
            Quad(scene.Root.transform, "B2", 200f, 0f, 10f, 10f, b);
            // Each side is ordered on its own; nothing joins across the mask.
            Assert.Equal([a, b, mask, a, b], scene.Draws(reorder: true));
        }

        [Fact]
        public void ANestedCanvasDrawsApartFromItsParentsElements()
        {
            using var scene = new Overlay();
            Material a = Ui();
            var nested = new GameObject("Nested", typeof(RectTransform));
            nested.transform.SetParent(scene.Root.transform, false);
            nested.AddComponent<Canvas>();
            Quad(scene.Root.transform, "Outside1", 0f, -200f, 10f, 10f, a);
            Quad(nested.transform, "Inside", 0f, 0f, 10f, 10f, a);
            Quad(scene.Root.transform, "Outside2", 0f, 200f, 10f, 10f, a);
            // The nested canvas's element is baked in its own space: one material, two spaces.
            var draws = scene.Device;
            scene.Draws(reorder: true);
            var requests = draws.MeshDrawRequests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal(nested.transform.localToWorldMatrix, requests[1].ObjectToWorld);
        }

        [Fact]
        public void AnElementMovedOverAnotherIsDrawnAfterIt()
        {
            // The order is kept while nothing changes, and made again when something moves.
            using var scene = new Overlay();
            Material a = Ui(), b = Ui();
            Quad(scene.Root.transform, "A1", -300f, 0f, 100f, 100f, a);
            var cover = Quad(scene.Root.transform, "B", -100f, 0f, 100f, 100f, b);
            Quad(scene.Root.transform, "A2", 100f, 0f, 100f, 100f, a);
            Assert.Equal([a, b], scene.Draws(reorder: true));
            Assert.Equal([a, b], scene.Draws(reorder: true));
            cover.transform.localPosition = new Vector3(50f, 0f, 0f); // still clear of A2
            Assert.Equal([a, b], scene.Draws(reorder: true));
            cover.transform.localPosition = new Vector3(150f, 0f, 0f);
            Assert.Equal([a, b, a], scene.Draws(reorder: true));
        }
    }
}
