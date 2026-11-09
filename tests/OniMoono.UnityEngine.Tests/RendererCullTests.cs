using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The renderers a camera culls: those enabled on an active GameObject, in the order they last
    /// became so, as measured in Unity's player (tools/UnityProbe, renderercull).
    public class RendererCullTests : EngineTest
    {
        static MeshRenderer Renderer(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go.AddComponent<MeshRenderer>();
        }

        static RendererNative N(Renderer r) => r.RendererData;

        /// The cull candidates among the given renderers, in their order.
        static string Order(params Renderer[] rs)
        {
            var mine = rs.Select(N).ToHashSet();
            return string.Join(",", FrameRenderer.Candidates.Where(mine.Contains).Select(r => ((Object)r.GameObject.Managed).name));
        }

        [Fact]
        public void CandidatesAreEnabledRenderersOnActiveObjectsInTheOrderTheyBecameSo()
        {
            var a = Renderer("A");
            var b = Renderer("B");
            Assert.Equal("A,B", Order(a, b));

            a.enabled = false;
            Assert.Equal("B", Order(a, b));
            a.enabled = true;
            Assert.Equal("B,A", Order(a, b));
            a.enabled = true; // no change: keeps its place
            Assert.Equal("B,A", Order(a, b));

            b.gameObject.SetActive(false);
            Assert.Equal("A", Order(a, b));
            b.enabled = false;
            b.gameObject.SetActive(true);
            Assert.Equal("A", Order(a, b));
            b.enabled = true;
            Assert.Equal("A,B", Order(a, b));
        }

        [Fact]
        public void AnAncestorsActivationAddsAndRemovesItsRenderers()
        {
            var root = new GameObject("root");
            var mid = new GameObject("mid");
            mid.transform.SetParent(root.transform, false);
            var c = Renderer("C", mid.transform);
            var d = Renderer("D");
            Assert.Equal("C,D", Order(c, d));

            root.SetActive(false);
            Assert.Equal("D", Order(c, d));
            c.enabled = false;
            c.enabled = true; // enabled while its object is inactive: not a candidate yet
            Assert.Equal("D", Order(c, d));
            root.SetActive(true);
            Assert.Equal("D,C", Order(c, d));

            mid.SetActive(false);
            root.SetActive(false);
            root.SetActive(true);
            Assert.Equal("D", Order(c, d));
            mid.SetActive(true);
            Assert.Equal("D,C", Order(c, d));
        }

        [Fact]
        public void RenderersMadeOnInactiveObjectsJoinWhenActivatedAndLeaveWhenDestroyed()
        {
            var parent = new GameObject("parent");
            parent.SetActive(false);
            var e = Renderer("E", parent.transform);
            var f = Renderer("F");
            Assert.Equal("F", Order(e, f));
            parent.SetActive(true);
            Assert.Equal("F,E", Order(e, f));

            Object.DestroyImmediate(e);
            Assert.Equal("F", Order(f));
            Assert.True(N(f).CandidateSlot >= 0);

            var native = N(f);
            Object.Destroy(f.gameObject);
            // Until its destruction a renderer awaiting Destroy stays (as before: Destroy does not
            // disable components that are not Behaviours).
            Assert.Contains(native, FrameRenderer.Candidates);
            Lifecycle.ProcessDestroyQueue();
            Assert.DoesNotContain(native, FrameRenderer.Candidates);
            Assert.Equal(-1, native.CandidateSlot);
        }

        [Fact]
        public void TogglingARendererManyTimesLeavesOneSlotAfterCulling()
        {
            using var scope = new RenderScope();
            var r = Renderer("T");
            // Earlier tests' destroyed renderers may still hold slots (holes) in other layers' lists.
            int slots = FrameRenderer.CandidateSlots;
            for (int i = 0; i < 10000; i++)
            {
                r.enabled = false;
                r.enabled = true;
            }
            // Bounded without a camera too.
            Assert.InRange(FrameRenderer.CandidateSlots, 1, slots + 2100);
            scope.SetUpCamera();
            Frame();
            Assert.Equal(FrameRenderer.Candidates.Count(), FrameRenderer.CandidateSlots);
            Assert.Equal("T", Order(r));
        }

        /// The regression check for Fast Track's renderers: thousands disabled or on inactive
        /// objects (themselves or an ancestor) cost the cameras nothing. Each camera's cull
        /// visits the enabled renderers on active objects alone (counted, not timed).
        [Fact]
        public void CamerasVisitOnlyEnabledRenderersOnActiveObjects()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            var cameras = new List<Camera> { s.Camera };
            for (int i = 0; i < 4; i++)
            {
                var c = Object.Instantiate(s.Camera.gameObject).GetComponent<Camera>();
                Object.DestroyImmediate(c.GetComponent<CameraHook>());
                cameras.Add(c);
            }
            var inactiveParent = new GameObject("inactive parent");
            inactiveParent.SetActive(false);
            var all = new List<MeshRenderer>();
            for (int i = 0; i < 3000; i++)
            {
                var r = s.Quadr("A", s.MaterialA, i % 3 == 2 ? inactiveParent.transform : null);
                if (i % 3 == 0) r.enabled = false;
                if (i % 3 == 1) r.gameObject.SetActive(false);
                all.Add(r);
            }
            var shown = new List<MeshRenderer>();
            for (int i = 0; i < 20; i++) shown.Add(s.Quadr("B", s.MaterialB));
            // Fast Track toggles its renderers often: churn leaves no extra visits.
            foreach (var r in all.Where((_, i) => i % 3 == 0)) { r.enabled = true; r.enabled = false; }
            Frame();
            int candidates = FrameRenderer.Candidates.Count();
            Assert.Equal(shown.Count, candidates);
            long before = FrameRenderer.CullVisits;
            Assert.Equal(string.Join(",", Enumerable.Repeat("B", shown.Count * cameras.Count)), s.Drawn());
            Assert.Equal(candidates * cameras.Count, FrameRenderer.CullVisits - before);
        }

        /// Each camera walks only the layers its culling mask holds: 15 cameras over Fast Track's
        /// renderers each walked all of them, though about 7% are in the layers each culls.
        [Fact]
        public void CamerasVisitOnlyTheLayersTheyCull()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            s.Camera.cullingMask = 1 << 5;
            var other = Object.Instantiate(s.Camera.gameObject).GetComponent<Camera>();
            other.cullingMask = (1 << 5) | (1 << 6);
            for (int i = 0; i < 2000; i++) s.Quadr("A", s.MaterialA).gameObject.layer = i % 2 == 0 ? 0 : 7;
            var b5 = new List<MeshRenderer>();
            for (int i = 0; i < 10; i++)
            {
                var r = s.Quadr("B", s.MaterialB);
                r.gameObject.layer = 5;
                b5.Add(r);
            }
            var b6 = s.Quadr("B", s.MaterialB);
            b6.gameObject.layer = 6;
            Frame();
            long before = FrameRenderer.CullVisits;
            Assert.Equal(21, s.Drawn().Split(',').Length);
            Assert.Equal(10 + 11, FrameRenderer.CullVisits - before);

            // Moved out of a camera's layers and back.
            b5[0].gameObject.layer = 0;
            before = FrameRenderer.CullVisits;
            Assert.Equal(19, s.Drawn().Split(',').Length);
            Assert.Equal(9 + 10, FrameRenderer.CullVisits - before);
            b5[0].gameObject.layer = 5;
            Assert.Equal(21, s.Drawn().Split(',').Length);
        }

        /// A renderer moved to another layer and back keeps its place among ties (measured:
        /// UnityProbe renderercull, A-layer-out-and-back).
        [Fact]
        public void ALayerChangeKeepsTheDrawOrder()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            s.Camera.cullingMask = 1 << 5;
            var a = s.Quadr("A", s.MaterialA);
            a.gameObject.layer = 5;
            var b = s.Quadr("B", s.MaterialB);
            b.gameObject.layer = 5;
            Assert.Equal("A,B", s.Drawn());
            a.gameObject.layer = 6;
            Assert.Equal("B", s.Drawn());
            a.gameObject.layer = 5;
            Assert.Equal("A,B", s.Drawn());
            // Earlier tests' destroyed renderers may still hold slots (holes) in other layers' lists.
            int slots = FrameRenderer.CandidateSlots;
            for (int i = 0; i < 100000; i++) a.gameObject.layer = i % 2 == 0 ? 6 : 5;
            Assert.InRange(FrameRenderer.CandidateSlots, 2, slots + 2100);
            Assert.Equal("A,B", s.Drawn());
        }

        /// A device and a camera over two overlapping quads, A and B, at the same depth.
        sealed class RenderScope : System.IDisposable
        {
            readonly RenderDevice previous = GraphicsDevice.Current;
            public readonly RecordingDevice Device = new();
            public Camera Camera;
            public Material MaterialA, MaterialB;
            public Mesh Quad;

            public RenderScope()
            {
                AssetSystem.Initialize(GameInstall.DataDir);
                ProjectSettings.Load();
                FrameRenderer.Install();
                GraphicsDevice.Install(Device);
            }

            public void SetUpCamera()
            {
                Camera = new GameObject("camera", typeof(Camera)).GetComponent<Camera>();
                Camera.orthographic = true;
                Camera.orthographicSize = 8f;
                Camera.transform.position = new Vector3(8f, 8f, -10f);
                Quad = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
                var shader = Shader.Find("Lines/Colored Blended");
                MaterialA = new Material(shader) { name = "A", renderQueue = 3000 };
                MaterialB = new Material(shader) { name = "B", renderQueue = 3000 };
            }

            public MeshRenderer Quadr(string name, Material material, Transform parent = null)
            {
                var r = Renderer(name, parent);
                r.transform.position = new Vector3(4f, 4f, 0f);
                r.gameObject.AddComponent<MeshFilter>().sharedMesh = Quad;
                r.sharedMaterial = material;
                return r;
            }

            /// The materials drawn in the next frame, by name.
            public string Drawn()
            {
                Device.MeshDraws.Clear();
                Frame();
                return string.Join(",", Device.MeshDraws.Select(d => d.Material.name));
            }

            public void Dispose() => GraphicsDevice.Install(previous);
        }

        [Fact]
        public void RenderersTiedOnDepthDrawInTheOrderTheyWereEnabled()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            var a = s.Quadr("A", s.MaterialA);
            var b = s.Quadr("B", s.MaterialB);
            Assert.Equal("A,B", s.Drawn());
            a.enabled = false;
            Assert.Equal("B", s.Drawn());
            a.enabled = true;
            Assert.Equal("B,A", s.Drawn());
            b.enabled = false;
            b.enabled = true; // between two renders: B still moves after A
            Assert.Equal("A,B", s.Drawn());
            a.gameObject.SetActive(false);
            a.gameObject.SetActive(true);
            Assert.Equal("B,A", s.Drawn());
        }

        public sealed class CameraHook : MonoBehaviour
        {
            public System.Action PreCull, PreRender;

            void OnPreCull()
            {
                var a = PreCull;
                PreCull = null;
                a?.Invoke();
            }

            void OnPreRender()
            {
                var a = PreRender;
                PreRender = null;
                a?.Invoke();
            }
        }

        public sealed class VisibleHook : MonoBehaviour
        {
            public System.Action Visible;

            void OnBecameVisible()
            {
                var a = Visible;
                Visible = null;
                a?.Invoke();
            }
        }

        /// Changes from OnPreCull take effect in that render; from OnPreRender and OnBecameVisible
        /// (culling done), enabling, activating and creating take effect in the next render, and
        /// disabling, deactivating and destroying in that render.
        [Fact]
        public void ChangesDuringARenderTakeEffectWhenUnitysDo()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            var hook = s.Camera.gameObject.AddComponent<CameraHook>();
            var a = s.Quadr("A", s.MaterialA);
            var b = s.Quadr("B", s.MaterialB);
            Assert.Equal("A,B", s.Drawn());

            a.enabled = false;
            hook.PreCull = () => a.enabled = true;
            Assert.Equal("B,A", s.Drawn());
            hook.PreCull = () => b.gameObject.SetActive(false);
            Assert.Equal("A", s.Drawn());
            b.gameObject.SetActive(true);

            hook.PreRender = () => a.enabled = false;
            Assert.Equal("B", s.Drawn());
            hook.PreRender = () => a.enabled = true;
            Assert.Equal("B", s.Drawn());
            Assert.Equal("B,A", s.Drawn());
            hook.PreRender = () => b.gameObject.SetActive(false);
            Assert.Equal("A", s.Drawn());
            b.gameObject.SetActive(true);
            hook.PreRender = () => Object.DestroyImmediate(a.gameObject);
            Assert.Equal("B", s.Drawn());
            Assert.Equal("B", s.Drawn());
        }

        /// Renders a frame, then asserts that every kept culling bound matches a fresh one.
        static string DrawnKept(RenderScope s)
        {
            var drawn = s.Drawn();
            var (wrong, kept) = FrameRenderer.KeptBounds();
            Assert.Equal(0, wrong);
            Assert.True(kept > 0);
            return drawn;
        }

        static void AssertKeptMatch()
        {
            Assert.Equal(0, FrameRenderer.KeptBounds().Wrong);
        }

        /// The culling bounds kept for a mesh renderer follow every change they are made from:
        /// moves of it and of its parent, its filter's mesh, the mesh's vertices, bounds and
        /// destruction, filters added and removed, and bounds assigned to the renderer.
        [Fact]
        public void KeptCullingBoundsFollowWhatTheyAreMadeFrom()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            var parent = new GameObject("parent");
            var a = s.Quadr("A", s.MaterialA, parent.transform);
            var b = s.Quadr("B", s.MaterialB);
            Assert.Equal("A,B", DrawnKept(s));
            long made = CullCache.Made;
            Assert.Equal("A,B", DrawnKept(s));
            Assert.Equal(made, CullCache.Made); // nothing changed: nothing made again

            // Its own move, and its parent's.
            a.transform.position = new Vector3(100f, 100f, 0f);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            a.transform.position = new Vector3(4f, 4f, 0f);
            Assert.Equal("A,B", DrawnKept(s));
            parent.transform.position = new Vector3(-100f, 0f, 0f);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            parent.transform.position = Vector3.zero;
            Assert.Equal("A,B", DrawnKept(s));
            parent.transform.localScale = new Vector3(100f, 100f, 1f);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s)); // A now at (400, 400)
            parent.transform.localScale = Vector3.one;
            Assert.Equal("A,B", DrawnKept(s));
            // Reparented.
            var far = new GameObject("far");
            far.transform.position = new Vector3(200f, 0f, 0f);
            a.transform.SetParent(far.transform, false);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            a.transform.SetParent(parent.transform, false);
            Assert.Equal("A,B", DrawnKept(s));

            // Its filter's mesh: another one, the mesh's vertices and bounds, its destruction.
            var filter = a.GetComponent<MeshFilter>();
            var farMesh = new Mesh { vertices = [new Vector3(100f, 0f, 0f), new Vector3(100f, 1f, 0f), new Vector3(101f, 0f, 0f)], triangles = [0, 1, 2] };
            filter.sharedMesh = farMesh;
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            // (The bounds follow new vertices when their number changes.)
            farMesh.vertices = [Vector3.zero, Vector3.up, Vector3.right, Vector3.one];
            AssertKeptMatch();
            Assert.Equal("A,B", DrawnKept(s));
            farMesh.bounds = new Bounds(new Vector3(300f, 0f, 0f), Vector3.one);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            farMesh.RecalculateBounds();
            Assert.Equal("A,B", DrawnKept(s));
            farMesh.SetVertices(new List<Vector3> { new(100f, 0f, 0f), new(100f, 1f, 0f), new(101f, 0f, 0f) });
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            filter.sharedMesh = s.Quad;
            Assert.Equal("A,B", DrawnKept(s));
            // A second filter's mesh is read when the first's is destroyed.
            var lone = new GameObject("lone");
            var mine = new Mesh { vertices = [Vector3.zero, Vector3.up, Vector3.right], triangles = [0, 1, 2] };
            filter.sharedMesh = mine;
            Assert.Equal("A,B", DrawnKept(s));
            Object.DestroyImmediate(mine);
            AssertKeptMatch();
            // No mesh now: empty bounds at A's position, still in view.
            Assert.Equal(0, FrameRenderer.KeptBounds().Wrong);
            filter.sharedMesh = s.Quad;
            Assert.Equal("A,B", DrawnKept(s));
            // The filter's mesh instance (MeshFilter.mesh) and a filter removed.
            filter.mesh.vertices = [new Vector3(100f, 0f, 0f), new Vector3(100f, 1f, 0f), new Vector3(101f, 0f, 0f), new Vector3(101f, 1f, 0f)];
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            Object.DestroyImmediate(filter);
            AssertKeptMatch();
            s.Drawn();
            AssertKeptMatch();
            a.gameObject.AddComponent<MeshFilter>().sharedMesh = s.Quad;
            AssertKeptMatch();
            Assert.Equal("A,B", DrawnKept(s));

            // Bounds assigned to the renderer, and reset.
            a.bounds = new Bounds(new Vector3(500f, 0f, 0f), Vector3.one);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            a.ResetBounds();
            Assert.Equal("A,B", DrawnKept(s));
            a.localBounds = new Bounds(new Vector3(500f, 0f, 0f), Vector3.one);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            a.transform.position = new Vector3(-496f, 4f, 0f);
            AssertKeptMatch();
            Assert.Equal("A,B", DrawnKept(s));
            a.ResetLocalBounds();
            a.transform.position = new Vector3(4f, 4f, 0f);
            Assert.Equal("A,B", DrawnKept(s));

            // Disabled, moved, enabled; deactivated, moved, activated; another layer and back.
            a.enabled = false;
            a.transform.position = new Vector3(100f, 100f, 0f);
            a.enabled = true;
            Assert.Equal("B", DrawnKept(s));
            a.gameObject.SetActive(false);
            a.transform.position = new Vector3(4f, 4f, 0f);
            a.gameObject.SetActive(true);
            Assert.Equal("B,A", DrawnKept(s));
            a.gameObject.layer = 3;
            a.transform.position = new Vector3(100f, 100f, 0f);
            a.gameObject.layer = 0;
            Assert.Equal("B", DrawnKept(s));
            a.transform.position = new Vector3(4f, 4f, 0f);
            Assert.Equal("B,A", DrawnKept(s));
            // A transform made a RectTransform keeps the watch.
            a.gameObject.AddComponent<RectTransform>();
            AssertKeptMatch();
            Assert.Equal("B,A", DrawnKept(s));
            a.transform.position = new Vector3(100f, 100f, 0f);
            AssertKeptMatch();
            Assert.Equal("B", DrawnKept(s));
            Object.DestroyImmediate(lone);
        }

        /// A move from OnBecameVisible reaches the renderers culled later in that pass.
        [Fact]
        public void KeptCullingBoundsSeeMovesMadeDuringThePass()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            var trigger = s.Quadr("A", s.MaterialA);
            var b = s.Quadr("B", s.MaterialB);
            Assert.Equal("A,B", DrawnKept(s));
            b.transform.position = new Vector3(100f, 100f, 0f);
            Assert.Equal("A", DrawnKept(s));
            trigger.transform.position = new Vector3(100f, 100f, 0f);
            Assert.Equal("", s.Drawn());
            trigger.transform.position = new Vector3(4f, 4f, 0f);
            trigger.gameObject.AddComponent<VisibleHook>().Visible = () => b.transform.position = new Vector3(4f, 4f, 0f);
            Assert.Equal("A,B", DrawnKept(s));
        }

        [Fact]
        public void RenderersChangedFromOnBecameVisibleWaitForTheNextRender()
        {
            using var s = new RenderScope();
            s.SetUpCamera();
            var b = s.Quadr("B", s.MaterialB);
            Assert.Equal("B", s.Drawn());

            // Created and enabled during culling: drawn from the next render on.
            var trigger = s.Quadr("A", s.MaterialA);
            MeshRenderer made = null;
            var other = s.Quadr("A", s.MaterialA);
            other.enabled = false;
            trigger.gameObject.AddComponent<VisibleHook>().Visible = () =>
            {
                made = s.Quadr("A", s.MaterialA);
                other.enabled = true;
                b.enabled = false;
            };
            Assert.Equal("A", s.Drawn());
            Assert.NotNull(made);
            Assert.Equal("A,A,A", s.Drawn());
        }
    }
}
