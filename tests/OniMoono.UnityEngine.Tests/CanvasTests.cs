using System;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    public class CanvasTests : EngineTest
    {
        /// SetMesh copies the mesh as it is then: a later change shows only with the next
        /// SetMesh, and setting the same mesh again unchanged (the game's UI animations, every
        /// frame) takes the same copy.
        [Fact]
        public void SetMeshCopiesTheMeshAsItIsThen()
        {
            var go = new GameObject("Element", typeof(RectTransform));
            try
            {
                var renderer = go.AddComponent<CanvasRenderer>();
                var data = (OniMoono.Engine.UI.CanvasRendererNative)renderer.Native;
                var mesh = new Mesh { vertices = [Vector3.zero, new Vector3(0f, 100f), new Vector3(100f, 0f)], triangles = [0, 1, 2] };
                renderer.SetMesh(mesh);
                var first = data.Geometry;
                renderer.Clear();
                Assert.Null(data.Geometry);
                renderer.SetMesh(mesh);
                Assert.Same(first, data.Geometry);

                mesh.vertices = [Vector3.zero, new Vector3(0f, 50f), new Vector3(50f, 0f)];
                Assert.Same(first, data.Geometry);
                Assert.Equal(new Vector3(0f, 100f), data.Geometry.Positions[1]);
                renderer.SetMesh(mesh);
                Assert.NotSame(first, data.Geometry);
                Assert.Equal(new Vector3(0f, 50f), data.Geometry.Positions[1]);

                var other = new Mesh { vertices = [Vector3.zero, new Vector3(0f, 50f), new Vector3(50f, 0f)], triangles = [0, 1, 2] };
                var second = data.Geometry;
                renderer.SetMesh(other);
                Assert.NotSame(second, data.Geometry);
                renderer.SetMesh(null);
                Assert.Null(data.Geometry);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// Unity keeps the low 16 bits of a canvas's sorting order: the Resource Calculator mod's
        /// 100000 reads back as -31072 in the original player, which draws that canvas under
        /// the game's HUD.
        [Fact]
        public void SortingOrderKeepsTheLow16Bits()
        {
            var canvas = new GameObject("Root", typeof(RectTransform)).AddComponent<Canvas>();
            try
            {
                canvas.sortingOrder = 100000;
                Assert.Equal(-31072, canvas.sortingOrder);
                canvas.sortingOrder = 32767;
                Assert.Equal(32767, canvas.sortingOrder);
                canvas.sortingOrder = -32769;
                Assert.Equal(32767, canvas.sortingOrder);
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Fact]
        public void NestedCanvasSortsWithTheNearestOverridingAncestor()
        {
            var root = new GameObject("Root", typeof(RectTransform)).AddComponent<Canvas>();
            root.sortingOrder = 3;

            var overriding = new GameObject("Overriding", typeof(RectTransform));
            overriding.transform.SetParent(root.transform, false);
            var overridingCanvas = overriding.AddComponent<Canvas>();
            overridingCanvas.overrideSorting = true;
            overridingCanvas.sortingOrder = 20;

            // A plain object between the canvases does not interrupt inheritance.
            var plain = new GameObject("Plain", typeof(RectTransform));
            plain.transform.SetParent(overriding.transform, false);
            var nested = new GameObject("Nested", typeof(RectTransform));
            nested.transform.SetParent(plain.transform, false);
            var nestedCanvas = nested.AddComponent<Canvas>();
            nestedCanvas.sortingOrder = 7;

            var direct = new GameObject("Direct", typeof(RectTransform));
            direct.transform.SetParent(root.transform, false);
            var directCanvas = direct.AddComponent<Canvas>();
            directCanvas.sortingOrder = 9;

            Assert.Equal(20, overridingCanvas.sortingOrder);
            Assert.Equal(20, nestedCanvas.sortingOrder);
            Assert.Equal(3, directCanvas.sortingOrder);
            Assert.Equal(3, root.sortingOrder);

            // Turning the override on makes a canvas use its own value.
            nestedCanvas.overrideSorting = true;
            Assert.Equal(7, nestedCanvas.sortingOrder);
        }

        sealed class HierarchyListener : MonoBehaviour
        {
            public int Changes;
            void OnCanvasHierarchyChanged() => Changes++;
        }

        /// A nested canvas reports its root's scale factor, as in the original.
        [Fact]
        public void ANestedCanvasReportsItsRootsScaleFactor()
        {
            var root = new GameObject("Root", typeof(RectTransform)).AddComponent<Canvas>();
            root.renderMode = RenderMode.ScreenSpaceOverlay;
            var nestedGo = new GameObject("Nested", typeof(RectTransform));
            nestedGo.transform.SetParent(root.transform, false);
            var nested = nestedGo.AddComponent<Canvas>();
            root.scaleFactor = 0.86f;
            Assert.Equal(0.86f, nested.scaleFactor);
            Assert.Equal(0.86f, root.scaleFactor);
        }

        [Fact]
        public void EnablingOrDisablingACanvasTellsTheScriptsBelowIt()
        {
            var root = new GameObject("Root", typeof(RectTransform)).AddComponent<Canvas>();
            var nestedGo = new GameObject("Nested", typeof(RectTransform));
            nestedGo.transform.SetParent(root.transform, false);
            var nested = nestedGo.AddComponent<Canvas>();
            var child = new GameObject("Child", typeof(RectTransform));
            child.transform.SetParent(nestedGo.transform, false);
            var onCanvas = nestedGo.AddComponent<HierarchyListener>();
            var below = child.AddComponent<HierarchyListener>();
            var outside = root.gameObject.AddComponent<HierarchyListener>();

            nested.enabled = false;
            Assert.Equal(1, onCanvas.Changes);
            Assert.Equal(1, below.Changes);
            nested.enabled = true;
            Assert.Equal(2, below.Changes);
            // Scripts above the canvas are not told.
            Assert.Equal(0, outside.Changes);
        }

        // A root canvas in a screen-space mode is laid out (anchors, pivot, size, position, scale)
        // the moment it becomes enabled, as measured in the original player (PerfProbe measure.txt):
        // the game's hover card is built in the same frame its canvas, saved at scale zero, comes on.
        static GameObject ZeroScaledOverlay(string name, bool active)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(active);
            go.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            go.transform.localScale = Vector3.zero;
            return go;
        }

        static void AssertLaidOut(GameObject go)
        {
            var rt = (RectTransform)go.transform;
            var screen = new Vector2(Screen.width, Screen.height);
            Assert.Equal(Vector3.one, rt.localScale);
            Assert.Equal(screen, rt.sizeDelta);
            Assert.Equal(Vector2.zero, rt.anchorMin);
            Assert.Equal(Vector2.zero, rt.anchorMax);
            Assert.Equal(new Vector2(0.5f, 0.5f), rt.pivot);
            Assert.Equal(screen * 0.5f, rt.anchoredPosition);
        }

        [Fact]
        public void ARootScreenSpaceCanvasIsLaidOutWhenActivated()
        {
            var go = ZeroScaledOverlay("Activated", active: false);
            go.SetActive(true);
            AssertLaidOut(go);

            var again = ZeroScaledOverlay("Reactivated", active: true);
            again.SetActive(false);
            again.SetActive(true);
            AssertLaidOut(again);
        }

        [Fact]
        public void ARootScreenSpaceCanvasIsLaidOutWhenItsComponentIsEnabled()
        {
            var go = ZeroScaledOverlay("Toggled", active: true);
            var canvas = go.GetComponent<Canvas>();
            canvas.enabled = false;
            canvas.enabled = true;
            AssertLaidOut(go);
        }

        [Fact]
        public void AnInstantiatedRootScreenSpaceCanvasIsLaidOut()
        {
            AssertLaidOut(Object.Instantiate(ZeroScaledOverlay("ActiveSource", active: true)));
            var fromInactive = Object.Instantiate(ZeroScaledOverlay("InactiveSource", active: false));
            Assert.Equal(Vector3.zero, fromInactive.transform.localScale);
            fromInactive.SetActive(true);
            AssertLaidOut(fromInactive);
        }

        [Fact]
        public void AChildParentedKeepingItsWorldScaleUnderAJustActivatedCanvasKeepsScaleOne()
        {
            // HoverTextDrawer's pools: new GameObject, a RectTransform, SetParent(screen).
            var canvas = ZeroScaledOverlay("HoverCanvas", active: false);
            canvas.SetActive(true);
            var root = new GameObject("PoolRoot");
            root.AddComponent<RectTransform>().SetParent(canvas.transform);
            Assert.Equal(Vector3.one, root.transform.localScale);
        }

        [Fact]
        public void NestedWorldSpaceOrMovedCanvasesAreNotLaidOutOnEnable()
        {
            var host = new GameObject("Host", typeof(RectTransform));
            host.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var nested = ZeroScaledOverlay("Nested", active: false);
            nested.transform.SetParent(host.transform, false);
            nested.transform.localScale = Vector3.zero;
            nested.SetActive(true);
            Assert.Equal(Vector3.zero, nested.transform.localScale);

            // AddComponent<Canvas> starts in world space: a Dropdown's template canvas, cloned at
            // the root and nested again, keeps its anchoring.
            var worldSpace = new GameObject("WorldSpace", typeof(RectTransform));
            worldSpace.SetActive(false);
            worldSpace.AddComponent<Canvas>();
            worldSpace.transform.localScale = Vector3.zero;
            worldSpace.SetActive(true);
            Assert.Equal(Vector3.zero, worldSpace.transform.localScale);

            // Moving an enabled canvas to the root does not lay it out.
            var moved = ZeroScaledOverlay("Moved", active: true);
            moved.transform.SetParent(host.transform, false);
            moved.transform.localScale = Vector3.zero;
            moved.transform.SetParent(null, false);
            Assert.Equal(Vector3.zero, moved.transform.localScale);
        }

        sealed class DimensionsListener : MonoBehaviour
        {
            public static bool InUpdate;
            public readonly System.Collections.Generic.List<bool> Received = new();
            void OnRectTransformDimensionsChange() => Received.Add(InUpdate);
        }

        /// The messages a rect gets when its handler of Canvas.willRenderCanvases changes it
        /// once, through change, as (inUpdate) flags: in that frame and in the next.
        static (bool[] InFrame, bool[] Next) ChangedInTheCanvasUpdate(Action<RectTransform> change)
        {
            OniMoono.Engine.UI.CanvasSystem.Install();
            var canvas = new GameObject("Canvas", typeof(RectTransform));
            canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var child = new GameObject("Child", typeof(RectTransform));
            child.transform.SetParent(canvas.transform, false);
            var listener = child.AddComponent<DimensionsListener>();
            Frame();
            listener.Received.Clear();
            Canvas.WillRenderCanvases handler = () =>
            {
                DimensionsListener.InUpdate = true;
                change((RectTransform)child.transform);
                DimensionsListener.InUpdate = false;
            };
            Canvas.willRenderCanvases += handler;
            try { Frame(); }
            finally { Canvas.willRenderCanvases -= handler; }
            var inFrame = listener.Received.ToArray();
            listener.Received.Clear();
            Frame();
            return (inFrame, listener.Received.ToArray());
        }

        // Measured in the original player (docs/cleanroom/ugui.md, "Moved children of layout
        // groups", rule 4): a rect resized or re-pivoted from a willRenderCanvases handler hears
        // of it once, from the setter, and nothing after the canvas update.
        [Fact]
        public void ARectResizedDuringTheCanvasUpdateHearsOfItOnceFromTheSetter()
        {
            var (inFrame, next) = ChangedInTheCanvasUpdate(rt => rt.sizeDelta += new Vector2(10f, 0f));
            Assert.Equal([true], inFrame);
            Assert.Empty(next);
        }

        [Fact]
        public void ARectRepivotedDuringTheCanvasUpdateHearsOfItOnceFromTheSetter()
        {
            var (inFrame, next) = ChangedInTheCanvasUpdate(rt => rt.pivot = new Vector2(0f, 1f));
            Assert.Equal([true], inFrame);
            Assert.Empty(next);
        }

        // The same section: a rect moved there (anchoredPosition, localPosition, localScale)
        // gets no message at all.
        [Theory]
        [InlineData("anchoredPosition")]
        [InlineData("localPosition")]
        [InlineData("localScale")]
        public void ARectMovedDuringTheCanvasUpdateHearsNothing(string property)
        {
            var (inFrame, next) = ChangedInTheCanvasUpdate(rt =>
            {
                switch (property)
                {
                    case "anchoredPosition": rt.anchoredPosition += new Vector2(25f, -5f); break;
                    case "localPosition": rt.localPosition += new Vector3(25f, -5f, 0f); break;
                    default: rt.localScale = new Vector3(2f, 0.5f, 1f); break;
                }
            });
            Assert.Empty(inFrame);
            Assert.Empty(next);
        }

        [Fact]
        public void InstantiateRemapsTheCanvasCameraIntoTheClone()
        {
            // The game's virtual cursor: a camera-space canvas whose camera is in the same prefab.
            var root = new GameObject("Controller");
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.SetParent(root.transform, false);
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(root.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            var outside = new GameObject("Outside").AddComponent<Camera>();

            var clone = Object.Instantiate(root);
            var cloneCanvas = clone.GetComponentInChildren<Canvas>();
            var cloneCamera = clone.GetComponentInChildren<Camera>();
            Assert.Same(cloneCamera, cloneCanvas.worldCamera);
            Assert.NotSame(camera, cloneCamera);

            // A reference to an object outside the cloned hierarchy is kept.
            canvas.worldCamera = outside;
            Assert.Same(outside, Object.Instantiate(root).GetComponentInChildren<Canvas>().worldCamera);
        }

        [Fact]
        public void AnElementDestroyedOrClearedAfterTheCanvasBuildIsNotDrawn()
        {
            // The game's video screen, ending on its own, destroys its elements at once after the
            // canvases were built for the frame (both in PostLateUpdate); the frame still renders.
            var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            OniMoono.Engine.UI.CanvasSystem.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            var canvasGo = new GameObject("Overlay", typeof(RectTransform));
            Action destroy = null;
            int exceptions = 0;
            Application.LogCallback count = (_, _, type) => { if (type == LogType.Exception) exceptions++; };
            Application.logMessageReceived += count;
            try
            {
                canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var element = new GameObject("Element", typeof(RectTransform));
                element.transform.SetParent(canvasGo.transform, false);
                var renderer = element.AddComponent<CanvasRenderer>();
                renderer.SetMesh(new Mesh { vertices = [Vector3.zero, new Vector3(0f, 100f), new Vector3(100f, 0f)], triangles = [0, 1, 2] });
                renderer.materialCount = 1;
                renderer.SetMaterial(new Material(Shader.Find("UI/Default")), 0);
                Frame();
                Assert.NotEmpty(device.MeshDraws);

                // Cleared after the build (VideoScreen.Stop clears its RawImage's texture).
                device.MeshDraws.Clear();
                destroy = () => renderer.Clear();
                PlayerLoop.PostLateUpdate += destroy;
                Frame();
                Assert.Empty(device.MeshDraws);
                PlayerLoop.PostLateUpdate -= destroy;
                renderer.SetMesh(new Mesh { vertices = [Vector3.zero, new Vector3(0f, 100f), new Vector3(100f, 0f)], triangles = [0, 1, 2] });
                renderer.materialCount = 1;
                renderer.SetMaterial(new Material(Shader.Find("UI/Default")), 0);
                Frame();
                Assert.NotEmpty(device.MeshDraws);

                // Destroyed after the build.
                device.MeshDraws.Clear();
                destroy = () => Object.DestroyImmediate(element);
                PlayerLoop.PostLateUpdate += destroy;
                Frame();
                Assert.Empty(device.MeshDraws);
                Assert.Equal(0, exceptions);
            }
            finally
            {
                Application.logMessageReceived -= count;
                if (destroy != null) PlayerLoop.PostLateUpdate -= destroy;
                Object.DestroyImmediate(canvasGo);
                GraphicsDevice.Install(previous);
            }
        }

        /// Elements of a nested canvas are drawn from one mesh in the nested canvas's space, a
        /// submesh each, through the canvas's transform: moving or scaling the canvas (the
        /// research screen's zoom, observed in Unity's player at the frame time of a still
        /// screen) leaves that mesh as it is. Elements of the root canvas keep their own meshes.
        [Fact]
        public void ANestedCanvasMovedOrScaledKeepsItsElementsGeometry()
        {
            var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            OniMoono.Engine.UI.CanvasSystem.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            var root = new GameObject("Overlay", typeof(RectTransform));
            try
            {
                root.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var tree = new GameObject("Tree", typeof(RectTransform));
                tree.transform.SetParent(root.transform, false);
                tree.AddComponent<Canvas>();
                var row = new GameObject("Row", typeof(RectTransform));
                row.transform.SetParent(tree.transform, false);
                row.transform.localPosition = new Vector3(10f, 20f, 0f);
                // Two materials, so the elements are two draws rather than one merged run.
                var a = Triangle(row.transform, "A", new Vector3(0f, 0f, 0f));
                var b = Triangle(row.transform, "B", new Vector3(200f, 0f, 0f));
                var outside = Triangle(root.transform, "Outside", new Vector3(5f, 5f, 0f));

                Frame();
                var first = device.MeshDrawRequests.ToArray();
                Assert.Equal(3, first.Length);
                var (drawA, drawB) = (Find(first, a), Find(first, b));
                Assert.Same(drawA.Mesh, drawB.Mesh);
                Assert.NotEqual(drawA.SubMesh, drawB.SubMesh);
                Assert.Equal(tree.transform.localToWorldMatrix, drawA.ObjectToWorld);
                Assert.NotSame(drawA.Mesh, Find(first, outside).Mesh);
                var shared = drawA.Mesh;
                int version = shared.Version;

                device.MeshDrawRequests.Clear();
                tree.transform.localScale = new Vector3(0.6f, 0.6f, 1f);
                tree.transform.localPosition = new Vector3(-347f, 278f, 0f);
                Frame();
                var moved = device.MeshDrawRequests.ToArray();
                drawA = Find(moved, a);
                drawB = Find(moved, b);
                Assert.Same(shared, drawA.Mesh);
                Assert.Equal(version, shared.Version);
                Assert.Equal(tree.transform.localToWorldMatrix, drawA.ObjectToWorld);
                // Drawn where the element is: its vertices through the draw's transform.
                foreach (var (draw, element) in new[] { (drawA, a), (drawB, b) })
                {
                    var indices = draw.Mesh.SubMeshes[draw.SubMesh].Indices;
                    var world = draw.ObjectToWorld.MultiplyPoint3x4(draw.Mesh.Positions[indices[1]]);
                    var expected = element.transform.localToWorldMatrix.MultiplyPoint3x4(new Vector3(0f, 100f, 0f));
                    Assert.True((world - expected).magnitude < 1e-3f, $"{element.name}: {world} != {expected}");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                GraphicsDevice.Install(previous);
            }

            static DrawRequest Find(DrawRequest[] draws, CanvasRenderer element)
            {
                foreach (var d in draws)
                    if (ReferenceEquals(d.Material, element.GetMaterial(0))) return d;
                throw new InvalidOperationException(element.name + " was not drawn");
            }
        }

        /// A shader whose vertex program reads no position places its geometry from
        /// unity_ObjectToWorld alone, so an element drawn with one is given the root canvas's
        /// transform, also inside a nested canvas: the Consumables table's Duplicant portraits
        /// (Klei/BatchedAnimationUI, in a nested canvas pivoted at its top right) were drawn off
        /// that corner while the original draws them in their rows. Elements whose shader reads
        /// positions keep the nested canvas's space.
        [Fact]
        public void AnElementWhoseShaderReadsNoPositionIsDrawnInTheRootCanvassSpace()
        {
            var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            OniMoono.Engine.UI.CanvasSystem.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            var root = new GameObject("Overlay", typeof(RectTransform));
            try
            {
                var anim = Shader.Find("Klei/BatchedAnimationUI");
                Assert.NotNull(anim);
                Assert.False(((ShaderNative)anim.Native).ReadsVertexPosition);
                Assert.True(((ShaderNative)Shader.Find("UI/Default").Native).ReadsVertexPosition);

                root.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                root.transform.localPosition = new Vector3(640f, 360f, 0f);
                root.transform.localScale = new Vector3(0.86f, 0.86f, 1f);
                var screen = new GameObject("Screen", typeof(RectTransform));
                screen.transform.SetParent(root.transform, false);
                screen.transform.localPosition = new Vector3(744f, 418f, 0f);
                screen.AddComponent<Canvas>();
                var portrait = Triangle(screen.transform, "Portrait", new Vector3(-1000f, -300f, 0f));
                portrait.SetMaterial(new Material(anim), 0);
                var label = Triangle(screen.transform, "Label", new Vector3(-900f, -300f, 0f));

                Frame();
                var draws = device.MeshDrawRequests.ToArray();
                Assert.Equal(root.transform.localToWorldMatrix, Find(draws, portrait).ObjectToWorld);
                Assert.Equal(screen.transform.localToWorldMatrix, Find(draws, label).ObjectToWorld);
            }
            finally
            {
                Object.DestroyImmediate(root);
                GraphicsDevice.Install(previous);
            }

            static DrawRequest Find(DrawRequest[] draws, CanvasRenderer element)
            {
                foreach (var d in draws)
                    if (ReferenceEquals(d.Material, element.GetMaterial(0))) return d;
                throw new InvalidOperationException(element.name + " was not drawn");
            }
        }

        static CanvasRenderer Triangle(Transform parent, string name, Vector3 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            var renderer = go.AddComponent<CanvasRenderer>();
            renderer.SetMesh(new Mesh { vertices = [Vector3.zero, new Vector3(0f, 100f), new Vector3(100f, 0f)], triangles = [0, 1, 2] });
            renderer.materialCount = 1;
            renderer.SetMaterial(new Material(Shader.Find("UI/Default")), 0);
            return renderer;
        }

        // ----- Disabled canvases (measured in the original with UnityProbe's CanvasProbe) -----

        /// The probe's layout: an overlay root (scale factor 1.5, order 32000) holding a nested
        /// override-sorting canvas, a plain nested canvas with an override-sorting canvas inside
        /// it, and an element directly under the root. Each canvas holds one element.
        sealed class Nest
        {
            public Canvas Root, Override, Plain, Inner;
            public CanvasRenderer RootElement, OverrideElement, PlainElement, InnerElement;
            public CanvasRenderer[] Elements => [RootElement, OverrideElement, PlainElement, InnerElement];
        }

        static CanvasRenderer Element(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.AddComponent<CanvasRenderer>();
        }

        static Canvas NestedCanvas(Transform parent, string name, Vector2 position, bool overrideSorting)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
            rt.anchoredPosition = position;
            rt.sizeDelta = new Vector2(60f, 60f);
            var canvas = go.AddComponent<Canvas>();
            if (overrideSorting)
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32001;
            }
            return canvas;
        }

        static Nest BuildNest()
        {
            OniMoono.Engine.UI.CanvasSystem.Install();
            var n = new Nest();
            n.Root = new GameObject("Root", typeof(RectTransform)).AddComponent<Canvas>();
            n.Root.renderMode = RenderMode.ScreenSpaceOverlay;
            n.Root.sortingOrder = 32000;
            n.Root.scaleFactor = 1.5f;
            n.Override = NestedCanvas(n.Root.transform, "Override", new Vector2(20f, 20f), overrideSorting: true);
            n.Plain = NestedCanvas(n.Root.transform, "Plain", new Vector2(120f, 20f), overrideSorting: false);
            n.Inner = NestedCanvas(n.Plain.transform, "Inner", new Vector2(0f, 70f), overrideSorting: true);
            n.RootElement = Element(n.Root.transform, "RootElement");
            n.OverrideElement = Element(n.Override.transform, "OverrideElement");
            n.PlainElement = Element(n.Plain.transform, "PlainElement");
            n.InnerElement = Element(n.Inner.transform, "InnerElement");
            return n;
        }

        static Vector3 Corner(Canvas canvas)
        {
            var corners = new Vector3[4];
            ((RectTransform)canvas.transform).GetWorldCorners(corners);
            return corners[0];
        }

        /// A nested canvas under a root whose Canvas component is disabled still has that root:
        /// isRootCanvas false, rootCanvas the disabled root, and the root's render mode, scale
        /// factor and (without its own override) sorting order; it keeps its place on screen.
        [Fact]
        public void ADisabledRootCanvasIsStillTheRootOfTheCanvasesBelowIt()
        {
            var n = BuildNest();
            Frame();
            var placed = Corner(n.Override);
            // 20 from the root's corner at scale 1.5, to within a float's rounding: the root's
            // rect is 1024 / 1.5 wide, so the corner is not exact.
            Assert.True((placed - new Vector3(30f, 30f, 0f)).sqrMagnitude < 1e-8f, placed.ToString("R"));
            n.Root.enabled = false;
            Frame();
            foreach (var nested in new[] { n.Override, n.Plain, n.Inner })
            {
                Assert.False(nested.isRootCanvas);
                Assert.Same(n.Root, nested.rootCanvas);
                Assert.Equal(1.5f, nested.scaleFactor);
                Assert.Equal(RenderMode.ScreenSpaceOverlay, nested.renderMode);
            }
            Assert.Equal(32001, n.Override.sortingOrder);
            Assert.Equal(32000, n.Plain.sortingOrder);
            Assert.Equal(placed, Corner(n.Override));
            Assert.True(n.Root.isRootCanvas);
        }

        /// Under a disabled root canvas only the nested override-sorting canvases draw (their
        /// elements are numbered, so uGUI raycasts hit them); the root's own elements and those
        /// of nested canvases that sort with it read depth -1. Enabling the root brings them back.
        [Fact]
        public void UnderADisabledRootCanvasOnlyOverrideSortingCanvasesDraw()
        {
            var n = BuildNest();
            Frame();
            Assert.All(n.Elements, r => Assert.NotEqual(-1, r.absoluteDepth));

            n.Root.enabled = false;
            Frame();
            Assert.NotEqual(-1, n.OverrideElement.absoluteDepth);
            Assert.NotEqual(-1, n.InnerElement.absoluteDepth);
            Assert.Equal(-1, n.RootElement.absoluteDepth);
            Assert.Equal(-1, n.PlainElement.absoluteDepth);

            n.Root.enabled = true;
            Frame();
            Assert.All(n.Elements, r => Assert.NotEqual(-1, r.absoluteDepth));
        }

        /// A disabled canvas reports itself as a root with its own settings (here a nested
        /// canvas's defaults: World Space, scale factor 1, order 0), as one on an inactive object
        /// does; the canvases below it take it as their root. It draws nothing, override-sorting
        /// or not, while the rest of its root's canvas still draws.
        [Fact]
        public void ADisabledNestedCanvasIsARootOfItsOwnAndDrawsNothing()
        {
            var n = BuildNest();
            n.Override.enabled = false;
            n.Plain.enabled = false;
            Frame();
            foreach (var disabled in new[] { n.Override, n.Plain })
            {
                Assert.True(disabled.isRootCanvas);
                Assert.Same(disabled, disabled.rootCanvas);
                Assert.Equal(1f, disabled.scaleFactor);
                Assert.Equal(RenderMode.WorldSpace, disabled.renderMode);
            }
            Assert.Equal(0, n.Plain.sortingOrder);
            Assert.False(n.Inner.isRootCanvas);
            Assert.Same(n.Plain, n.Inner.rootCanvas);
            Assert.Equal(1f, n.Inner.scaleFactor);
            Assert.Equal(RenderMode.WorldSpace, n.Inner.renderMode);
            Assert.Equal(-1, n.OverrideElement.absoluteDepth);
            Assert.Equal(-1, n.PlainElement.absoluteDepth);
            Assert.NotEqual(-1, n.RootElement.absoluteDepth);
        }

        /// A root canvas moved under a disabled canvas stops drawing, and its elements read -1
        /// as when it is disabled; moved back to the top level, it numbers them again.
        [Fact]
        public void ARootCanvasMovedUnderADisabledCanvasLosesItsDepths()
        {
            var n = BuildNest();
            var other = new GameObject("Other", typeof(RectTransform)).AddComponent<Canvas>();
            other.renderMode = RenderMode.ScreenSpaceOverlay;
            var element = Element(other.transform, "OtherElement");
            Frame();
            Assert.Equal(0, element.absoluteDepth);
            n.Root.enabled = false;
            other.transform.SetParent(n.Root.transform, false);
            Frame();
            Assert.Equal(-1, element.absoluteDepth);
            other.transform.SetParent(null, false);
            Frame();
            Assert.Equal(0, element.absoluteDepth);
        }

        static Canvas TiedRoot(string name)
        {
            var c = new GameObject(name, typeof(RectTransform)).AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 31000;
            Element(c.transform, name + "Element");
            return c;
        }

        static string BottomToTop(params Canvas[] canvases)
        {
            Frame();
            var sorted = (Canvas[])canvases.Clone();
            Array.Sort(sorted, (a, b) => a.renderOrder.CompareTo(b.renderOrder));
            return string.Join(",", Array.ConvertAll(sorted, c => c.name));
        }

        /// Root canvases that tie on sorting order draw in list order, as measured in the original
        /// (UnityProbe canvasorder): an enabled canvas goes on top, and setting a sortingOrder or
        /// moving a live canvas to another parent puts them all back in creation order, the last
        /// made at the bottom. The Skills screen and the HUD tie at 0: the first tooltip moves its
        /// canvas under the tooltip anchor, and the HUD, made first, ends up over the screen.
        [Fact]
        public void CanvasesTiedOnSortingOrderDrawInListOrder()
        {
            OniMoono.Engine.UI.CanvasSystem.Install();
            var a = TiedRoot("A");
            var b = TiedRoot("B");
            var c = TiedRoot("C");
            Assert.Equal("C,B,A", BottomToTop(a, b, c));
            // Enabled again: on top of its ties.
            b.gameObject.SetActive(false);
            b.gameObject.SetActive(true);
            Assert.Equal("C,A,B", BottomToTop(a, b, c));
            c.enabled = false;
            c.enabled = true;
            Assert.Equal("A,B,C", BottomToTop(a, b, c));
            Assert.Equal("A,B,C", BottomToTop(a, b, c));
            // Setting a root's order, even to the same value, sorts by creation again.
            a.sortingOrder = 31000;
            Assert.Equal("C,B,A", BottomToTop(a, b, c));

            // A canvas at another order, created and left at its default order: no re-sort; its
            // nested canvas made, given an order while inactive and enabled, overrideSorting
            // changed, sibling order: none either.
            c.gameObject.SetActive(false);
            c.gameObject.SetActive(true);
            var other = new GameObject("Other", typeof(RectTransform)).AddComponent<Canvas>();
            other.renderMode = RenderMode.ScreenSpaceOverlay;
            Element(other.transform, "OtherElement");
            var anchor = new GameObject("Anchor", typeof(RectTransform));
            anchor.transform.SetParent(other.transform, false);
            var tip = new GameObject("Tip", typeof(RectTransform));
            tip.SetActive(false);
            tip.transform.SetParent(other.transform, false);
            tip.AddComponent<Canvas>();
            Element(tip.transform, "TipElement");
            // Its order set while its object is inactive: not a root (a canvas is above it).
            tip.GetComponent<Canvas>().sortingOrder = 4;
            tip.SetActive(true);
            tip.GetComponent<Canvas>().overrideSorting = true;
            a.transform.SetAsLastSibling();
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            // The nested canvas moved under another parent: a re-sort. Moved to the parent it
            // already has: none.
            tip.transform.SetParent(anchor.transform);
            Assert.Equal("C,B,A", BottomToTop(a, b, c));
            c.gameObject.SetActive(false);
            c.gameObject.SetActive(true);
            tip.transform.SetParent(anchor.transform);
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            // A disabled canvas moved: none.
            tip.SetActive(false);
            tip.transform.SetParent(other.transform);
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            // An object moved with a live canvas below it: a re-sort.
            tip.SetActive(true);
            anchor.transform.SetParent(null);
            anchor.transform.SetParent(other.transform);
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            tip.transform.SetParent(anchor.transform);
            c.gameObject.SetActive(false);
            c.gameObject.SetActive(true);
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            anchor.transform.SetParent(null);
            Assert.Equal("C,B,A", BottomToTop(a, b, c));
            anchor.transform.SetParent(other.transform);

            // The order set on a nested canvas that overrides sorting: a re-sort when the canvas
            // was added to an active object, none when it was added to an inactive one (tip).
            c.gameObject.SetActive(false);
            c.gameObject.SetActive(true);
            tip.GetComponent<Canvas>().sortingOrder = 5;
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            var live = new GameObject("Live", typeof(RectTransform));
            live.transform.SetParent(other.transform, false);
            var liveCanvas = live.AddComponent<Canvas>();
            liveCanvas.overrideSorting = true;
            Assert.Equal("B,A,C", BottomToTop(a, b, c));
            liveCanvas.sortingOrder = 6;
            Assert.Equal("C,B,A", BottomToTop(a, b, c));
        }

        /// For contrast: with the root's GameObject inactive, every nested canvas reports itself
        /// as its own root with its own settings, and nothing draws.
        [Fact]
        public void UnderAnInactiveRootEveryCanvasIsItsOwnRoot()
        {
            var n = BuildNest();
            Frame();
            n.Root.gameObject.SetActive(false);
            Frame();
            foreach (var nested in new[] { n.Override, n.Plain, n.Inner })
            {
                Assert.True(nested.isRootCanvas);
                Assert.Same(nested, nested.rootCanvas);
                Assert.Equal(1f, nested.scaleFactor);
                Assert.Equal(RenderMode.WorldSpace, nested.renderMode);
            }
            Assert.Equal(0, n.Plain.sortingOrder);
            Assert.Equal(32001, n.Override.sortingOrder);
            Assert.All(n.Elements, r => Assert.Equal(-1, r.absoluteDepth));
        }

        /// Overlay canvases draw over a depth buffer cleared once, before the first of them
        /// (UnityProbe UiZTest, #57): the Colony Summary's chart lines use a shader that tests
        /// depth itself (ZTest LEqual), and drew nothing over the depth the cameras had left.
        [Fact]
        public void OverlayCanvasesDrawAfterOneDepthClear()
        {
            var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                       ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
            AssetSystem.Initialize(System.IO.Path.Combine(game, "OxygenNotIncluded_Data"));
            ProjectSettings.Load();
            FrameRenderer.Install();
            OniMoono.Engine.UI.CanvasSystem.Install();
            var device = new RecordingDevice();
            var previous = GraphicsDevice.Current;
            GraphicsDevice.Install(device);
            var canvases = new System.Collections.Generic.List<GameObject>();
            try
            {
                Frame();
                Assert.Empty(device.DepthClears);

                for (int i = 0; i < 2; i++)
                {
                    var canvasGo = new GameObject("Overlay" + i, typeof(RectTransform));
                    canvases.Add(canvasGo);
                    var canvas = canvasGo.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = i;
                    var element = new GameObject("Element", typeof(RectTransform));
                    element.transform.SetParent(canvasGo.transform, false);
                    var renderer = element.AddComponent<CanvasRenderer>();
                    renderer.SetMesh(new Mesh { vertices = [Vector3.zero, new Vector3(0f, 100f), new Vector3(100f, 0f)], triangles = [0, 1, 2] });
                    renderer.materialCount = 1;
                    renderer.SetMaterial(new Material(Shader.Find("UI/Default")), 0);
                }
                device.MeshDraws.Clear();
                device.DepthClears.Clear();
                Frame();
                Assert.Equal(2, device.MeshDraws.Count);
                var clear = Assert.Single(device.DepthClears);
                Assert.Null(clear.Target);
                Assert.Equal(1f, clear.Value);
                Assert.Equal(0, clear.DrawsBefore);
            }
            finally
            {
                foreach (var c in canvases) Object.DestroyImmediate(c);
                GraphicsDevice.Install(previous);
            }
        }
    }
}
