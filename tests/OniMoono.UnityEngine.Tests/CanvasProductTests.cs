using System;
using System.Collections.Generic;
using System.Text;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.UI;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// The canvas update keeps its matrix products while what they are computed from stands
    /// (CanvasSystem, "Products kept"). After each kind of change, the products a frame used
    /// (each element's matrix to its canvas, to its batch's space and the vertices baked with
    /// it, and canvasRenderer.hasMoved's) have the bits of a frame that computes every one of
    /// them again.
    public class CanvasProductTests : EngineTest
    {
        sealed class Ui
        {
            public Canvas Root, Nested, Override;
            public RectTransform Panel, Row, Deep;
            public readonly List<CanvasRenderer> Elements = new();
        }

        static readonly Mesh Triangle = new() { vertices = [new Vector3(0.1f, 0.3f), new Vector3(0.7f, 100.3f), new Vector3(100.9f, 0.1f)], triangles = [0, 1, 2] };

        static RectTransform Rect(Transform parent, string name, Vector2 position, float angle = 0f, float scale = 1f)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.3f, 0.7f);
            rt.pivot = new Vector2(0.25f, 0.5f);
            rt.sizeDelta = new Vector2(77.7f, 33.3f);
            rt.anchoredPosition = position;
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
            rt.localScale = new Vector3(scale, scale, 1f);
            return rt;
        }

        static CanvasRenderer Element(Ui s, Transform parent, string name, Vector2 position, float angle = 0f, float scale = 1f)
        {
            var rt = Rect(parent, name, position, angle, scale);
            var r = rt.gameObject.AddComponent<CanvasRenderer>();
            r.SetMesh(Triangle);
            r.materialCount = 1;
            r.SetMaterial(new Material(Shader.Find("UI/Default")), 0);
            s.Elements.Add(r);
            return r;
        }

        /// A root canvas at scale 1.37 with elements of its own, a nested canvas that sorts with
        /// it (its elements baked in its space) three levels deep, rotated and scaled, and one
        /// that sorts on its own.
        static Ui Build()
        {
            var s = new Ui();
            s.Root = new GameObject("Root", typeof(RectTransform)).AddComponent<Canvas>();
            s.Root.renderMode = RenderMode.ScreenSpaceOverlay;
            s.Root.scaleFactor = 1.37f;
            Element(s, s.Root.transform, "RootElement", new Vector2(13.3f, 17.1f));
            s.Panel = Rect(s.Root.transform, "Panel", new Vector2(-40.1f, 22.9f), 7.5f, 0.93f);
            Element(s, s.Panel, "PanelElement", new Vector2(3.1f, -9.7f), -3f);
            var nestedRect = Rect(s.Panel, "Nested", new Vector2(120.7f, 20.3f), 11f, 1.1f);
            s.Nested = nestedRect.gameObject.AddComponent<Canvas>();
            s.Row = Rect(nestedRect, "Row", new Vector2(5.5f, 70.1f), -21f, 0.87f);
            Element(s, s.Row, "RowElement", new Vector2(1.7f, 2.9f), 33f, 1.21f);
            s.Deep = Rect(s.Row, "Deep", new Vector2(-8.3f, 4.4f), 2f, 0.77f);
            Element(s, s.Deep, "DeepElement", new Vector2(0.37f, -0.71f), -13f, 1.03f);
            Element(s, nestedRect, "NestedElement", new Vector2(9.9f, 9.1f));
            var overrideRect = Rect(s.Root.transform, "Override", new Vector2(60.6f, -30.3f), 4f, 1.05f);
            s.Override = overrideRect.gameObject.AddComponent<Canvas>();
            s.Override.overrideSorting = true;
            s.Override.sortingOrder = 5;
            Element(s, overrideRect, "OverrideElement", new Vector2(2.2f, 3.3f), 17f, 0.9f);
            return s;
        }

        static string Bits(Matrix4x4 m)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(BitConverter.SingleToInt32Bits(m[i]).ToString("x8")).Append(i == 15 ? "" : ",");
            return sb.ToString();
        }

        static string Products(Ui s)
        {
            var sb = new StringBuilder();
            foreach (var e in s.Elements)
            {
                if (e == null) continue;
                var r = (CanvasRendererNative)e.Native;
                sb.Append(e.name).Append(" built ").Append(r.BuiltToCanvas is { } b ? Bits(b) : "none");
                sb.Append(" moved ").Append(e.hasMoved).Append(" to canvas ").Append(Bits(CanvasSystem.ToCanvas(r)));
                sb.Append(" baked ").Append(r.Baked == null ? "none" : Bits(r.BakedToSpace));
                if (r.Baked != null)
                    foreach (var p in r.Baked.Positions)
                        sb.Append(' ').Append(BitConverter.SingleToInt32Bits(p.x).ToString("x8")).Append(BitConverter.SingleToInt32Bits(p.y).ToString("x8"))
                          .Append(BitConverter.SingleToInt32Bits(p.z).ToString("x8"));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// A frame with the kept products, then one computing them all again: the same bits.
        static void SameAsComputedAgain(Ui s, string after)
        {
            Frame();
            var kept = Products(s);
            CanvasSystem.keepProducts = false;
            try
            {
                Frame();
            }
            finally
            {
                CanvasSystem.keepProducts = true;
            }
            Assert.True(kept == Products(s), $"after {after}:\nkept\n{kept}computed again\n{Products(s)}");
        }

        [Fact]
        public void KeptProductsAreTheProductsComputedAgain()
        {
            AssetSystem.Initialize(System.IO.Path.Combine(TmpTestSupport.GameDir, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            CanvasSystem.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            try
            {
                var s = Build();
                SameAsComputedAgain(s, "the first frame");
                Frame();
                Assert.NotEmpty(device.MeshDraws);

                var deepElement = s.Elements.Find(e => e.name == "DeepElement");
                var rowElement = s.Elements.Find(e => e.name == "RowElement");
                var rootElement = s.Elements.Find(e => e.name == "RootElement");
                var nestedElement = s.Elements.Find(e => e.name == "NestedElement");
                var steps = new (string What, Action Change)[]
                {
                    ("nothing", () => { }),
                    ("a leaf moved", () => ((RectTransform)deepElement.transform).anchoredPosition += new Vector2(0.31f, -0.17f)),
                    ("a leaf rotated", () => deepElement.transform.localRotation = Quaternion.Euler(0f, 0f, 41.3f)),
                    ("a leaf scaled", () => deepElement.transform.localScale = new Vector3(1.13f, 0.97f, 1f)),
                    ("a parent in the nested canvas moved", () => s.Row.anchoredPosition += new Vector2(1.9f, 0.7f)),
                    ("a parent in the nested canvas rotated", () => s.Row.localRotation = Quaternion.Euler(0f, 0f, -7.7f)),
                    ("a parent in the nested canvas scaled", () => s.Row.localScale = new Vector3(0.79f, 0.79f, 1f)),
                    ("the nested canvas moved", () => ((RectTransform)s.Nested.transform).anchoredPosition += new Vector2(-3.3f, 2.1f)),
                    ("the nested canvas scaled", () => s.Nested.transform.localScale = new Vector3(1.3f, 1.3f, 1f)),
                    ("an ancestor of the nested canvas rotated", () => s.Panel.localRotation = Quaternion.Euler(0f, 0f, 19f)),
                    ("an ancestor of the nested canvas scaled", () => s.Panel.localScale = new Vector3(1.07f, 0.91f, 1f)),
                    ("the root canvas's scale", () => s.Root.scaleFactor = 0.83f),
                    ("a position set to -0", () => ((RectTransform)deepElement.transform).anchoredPosition = new Vector2(-0f, -0f)),
                    ("a position set to 0", () => ((RectTransform)deepElement.transform).anchoredPosition = new Vector2(0f, 0f)),
                    // Closer than Vector3's == tells apart (1e-5), and still a different product.
                    ("a position moved by 9.9e-6", () => nestedElement.transform.localPosition += new Vector3(9.9e-6f, 0f, 0f)),
                    ("a position moved by an ulp", () => deepElement.transform.localPosition = new Vector3(
                        BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(deepElement.transform.localPosition.x) + 1),
                        deepElement.transform.localPosition.y, deepElement.transform.localPosition.z)),
                    ("an element reparented into the nested canvas", () => rootElement.transform.SetParent(s.Deep, false)),
                    ("an element reparented keeping its world place", () => rootElement.transform.SetParent(s.Root.transform, true)),
                    ("a nested element reparented out of it", () => rowElement.transform.SetParent(s.Panel, true)),
                    ("and back", () => rowElement.transform.SetParent(s.Row, false)),
                    ("a parent deactivated", () => s.Row.gameObject.SetActive(false)),
                    ("a moved parent activated", () => { s.Row.anchoredPosition += new Vector2(0.5f, 0.5f); s.Row.gameObject.SetActive(true); }),
                    ("the nested canvas disabled", () => s.Nested.enabled = false),
                    ("the nested canvas enabled", () => s.Nested.enabled = true),
                    ("the nested canvas sorting on its own", () => s.Nested.overrideSorting = true),
                    ("and with its parent again", () => s.Nested.overrideSorting = false),
                    ("a canvas added in the nested canvas", () => s.Row.gameObject.AddComponent<Canvas>()),
                    ("the canvas added removed", () => { Object.DestroyImmediate(s.Row.GetComponent<Canvas>()); }),
                    ("the override canvas moved", () => ((RectTransform)s.Override.transform).anchoredPosition += new Vector2(7f, 7f)),
                    ("the root canvas's scale back", () => s.Root.scaleFactor = 1.37f),
                    ("a sibling order change", () => s.Row.SetAsFirstSibling()),
                };
                foreach (var (what, change) in steps)
                {
                    change();
                    SameAsComputedAgain(s, what);
                }
            }
            finally
            {
                CanvasSystem.keepProducts = true;
                GraphicsDevice.Install(previous);
            }
        }

        /// The world matrix's number changes with each computation of it, and only then.
        [Fact]
        public void TheWorldStampChangesWithTheWorldMatrix()
        {
            var parent = new GameObject("Parent").transform;
            var child = new GameObject("Child").transform;
            child.SetParent(parent, false);
            var t = child.TransformNative;
            long first = t.WorldStamp;
            Assert.NotEqual(0, first);
            Assert.Equal(first, t.WorldStamp);
            parent.localPosition = new Vector3(1f, 2f, 3f);
            long moved = t.WorldStamp;
            Assert.NotEqual(first, moved);
            Assert.Equal(moved, t.WorldStamp);
            _ = child.localToWorldMatrix;
            Assert.Equal(moved, t.WorldStamp);
        }
    }
}
