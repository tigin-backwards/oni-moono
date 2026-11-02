// Culling of MeshRenderers that are disabled or on inactive GameObjects ("PROBE ... RC ..."), at
// the main menu (10 s in). Found with Fast Track's mesh renderers: tens of thousands registered,
// a tenth of them enabled on active objects.
//
// A disabled orthographic camera (size 8) at (5000, 5000, -10) renders layer 31 alone into a
// 16 x 16 texture through Camera.Render(); quads in "Lines/Colored Blended" (vertex colours)
// at z 0 are read back as a character map (rows top to bottom, R / G / B: that channel above
// 0.5 alone; '.': black; '?': other).
//
//   cost-*: the mean milliseconds of one Render() (50 renders after 5) with 30,000 more
//     MeshRenderers: none; disabled; on inactive GameObjects; under 300 inactive parents;
//     enabled out of the camera's view; enabled in view on layer 30 (not in the camera's mask);
//     the same enabled on layer 31 off-view again after being disabled once; and the cost of
//     30,000 enabled toggles and SetActive toggles (milliseconds for all of them).
//   when-*: a quad filling the texture changed during a Render(): from the camera's OnPreCull
//     (enabled, disabled, its object activated, its parent activated, created), from its
//     OnPreRender (the same), and from OnBecameVisible of another renderer in view (enabled,
//     disabled). The map shows whether the change took effect in that render.
//   tie-*: two quads A (red, (0,0)-(10,10)) and B (green, (6,6)-(16,16)) at the same depth, in
//     queue 3000 and 2000, one material or two; the overlap shows which drew last after each
//     change: created A then B, B then A, A disabled and enabled, A's object deactivated and
//     activated, A's parent deactivated and activated, both created inactive and activated B
//     first, B disabled and enabled, A's or B's material swapped for one made after it, A's set
//     again, and A and B made with an older and a newer material each way.
// With PROBE_SCENARIO=renderercull only this probe runs; it ends with a RendererCullProbeDone
// canvas. Compare the players' "RC when" and "RC tie" lines (the cost lines are timings).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class RcCameraHooks : MonoBehaviour
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

    public class RcVisibleHook : MonoBehaviour
    {
        public System.Action Visible;

        void OnBecameVisible()
        {
            var a = Visible;
            Visible = null;
            a?.Invoke();
        }
    }

    public class RendererCull : MonoBehaviour
    {
        static void L(string s) => Rec.Log("RC " + s);

        const int Layer = 31, OtherLayer = 30, N = 30000;
        static readonly Vector3 Origin = new Vector3(5000f, 5000f, 0f);

        bool started;
        float sceneStart = -1f;
        Camera cam;
        RcCameraHooks hooks;
        RenderTexture rt;
        Texture2D read;
        Material mat;
        Mesh full, tiny;

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static Mesh Quad(Rect r, Color c)
        {
            var m = new Mesh();
            m.vertices = new[] { new Vector3(r.xMin, r.yMin, 0f), new Vector3(r.xMax, r.yMin, 0f), new Vector3(r.xMax, r.yMax, 0f), new Vector3(r.xMin, r.yMax, 0f) };
            m.colors = new[] { c, c, c, c };
            // Both windings, so face culling plays no part.
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        string Map()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0);
            read.Apply();
            RenderTexture.active = prev;
            var sb = new StringBuilder();
            for (int y = 15; y >= 0; y--)
            {
                if (y < 15) sb.Append('/');
                for (int x = 0; x < 16; x++)
                {
                    var p = read.GetPixel(x, y);
                    bool r = p.r > 0.5f, g = p.g > 0.5f, b = p.b > 0.5f;
                    sb.Append(!r && !g && !b && p.r + p.g + p.b < 0.05f ? '.' : r && !g && !b ? 'R' : g && !r && !b ? 'G' : b && !r && !g ? 'B' : '?');
                }
            }
            return sb.ToString();
        }

        /// The colour at a pixel of the last map: R, G, B or '.'.
        char At(string map, int x, int y) => map[(15 - y) * 17 + x];

        string Render()
        {
            cam.Render();
            return Map();
        }

        GameObject Make(string name, Mesh mesh, Material material, Vector3 at, Transform parent = null, int layer = Layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = Origin + at;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        double TimeRenders()
        {
            for (int i = 0; i < 5; i++) cam.Render();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) cam.Render();
            return sw.Elapsed.TotalMilliseconds / 50.0;
        }

        static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

        IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            rt = new RenderTexture(16, 16, 24, RenderTextureFormat.ARGB32) { name = "RcTarget" };
            rt.Create();
            read = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            mat = new Material(Shader.Find("Lines/Colored Blended")) { renderQueue = 3000 };
            var camGo = new GameObject("RcCamera");
            camGo.transform.position = Origin + new Vector3(8f, 8f, -10f);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = 8f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.cullingMask = 1 << Layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = rt;
            hooks = camGo.AddComponent<RcCameraHooks>();
            full = Quad(new Rect(0, 0, 16, 16), Color.red);
            tiny = Quad(new Rect(0, 0, 0.01f, 0.01f), Color.blue);
            L("=== start shader=" + (mat.shader != null ? mat.shader.name : "null") + " layer31=" + LayerMask.LayerToName(Layer) + " layer30=" + LayerMask.LayerToName(OtherLayer)
              + " empty=" + Render());

            Cost();
            yield return null;
            When();
            yield return null;
            foreach (int queue in new[] { 3000, 2000 })
            {
                Ties(queue);
                yield return null;
            }

            var done = new GameObject("RendererCullProbeDone", typeof(RectTransform));
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("=== done");
        }

        void Cost()
        {
            double baseMs = TimeRenders();
            L("cost-none ms=" + F(baseMs));
            void Set(string name, Func<int, GameObject> make, System.Action<List<GameObject>> after = null)
            {
                var all = new List<GameObject>(N);
                for (int i = 0; i < N; i++) all.Add(make(i));
                after?.Invoke(all);
                double ms = TimeRenders();
                L("cost-" + name + " ms=" + F(ms) + " over-none=" + F(ms - baseMs) + " map-center=" + At(Map(), 8, 8));
                var parents = new HashSet<GameObject>();
                foreach (var go in all)
                {
                    if (go.transform.parent != null) parents.Add(go.transform.parent.gameObject);
                    UnityEngine.Object.DestroyImmediate(go);
                }
                foreach (var p in parents) UnityEngine.Object.DestroyImmediate(p);
            }
            Set("disabled", i =>
            {
                var go = Make("RcDisabled", tiny, mat, new Vector3(1f + (i % 100) * 0.1f, 1f + (i / 100) * 0.03f, 0f));
                go.GetComponent<MeshRenderer>().enabled = false;
                return go;
            });
            Set("inactive", i =>
            {
                var go = Make("RcInactive", tiny, mat, new Vector3(1f + (i % 100) * 0.1f, 1f + (i / 100) * 0.03f, 0f));
                go.SetActive(false);
                return go;
            });
            var inactiveParents = new List<GameObject>();
            for (int p = 0; p < 300; p++)
            {
                var parent = new GameObject("RcParent");
                parent.SetActive(false);
                inactiveParents.Add(parent);
            }
            Set("inactive-parent", i => Make("RcChild", tiny, mat, new Vector3(1f + (i % 100) * 0.1f, 1f + (i / 100) * 0.03f, 0f), inactiveParents[i % 300].transform));
            Set("enabled-offview", i => Make("RcOffview", tiny, mat, new Vector3(1000f + (i % 100) * 0.1f, 1000f + (i / 100) * 0.03f, 0f)));
            Set("enabled-otherlayer", i => Make("RcOtherLayer", tiny, mat, new Vector3(1f + (i % 100) * 0.1f, 1f + (i / 100) * 0.03f, 0f), null, OtherLayer));
            Set("reenabled-offview", i => Make("RcReenabled", tiny, mat, new Vector3(1000f + (i % 100) * 0.1f, 1000f + (i / 100) * 0.03f, 0f)), all =>
            {
                foreach (var go in all) go.GetComponent<MeshRenderer>().enabled = false;
                cam.Render();
                foreach (var go in all) go.GetComponent<MeshRenderer>().enabled = true;
            });
            {
                var all = new List<MeshRenderer>(N);
                for (int i = 0; i < N; i++) all.Add(Make("RcToggle", tiny, mat, new Vector3(1000f + (i % 100) * 0.1f, 1000f, 0f)).GetComponent<MeshRenderer>());
                cam.Render();
                var sw = Stopwatch.StartNew();
                foreach (var r in all) r.enabled = false;
                double off = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                foreach (var r in all) r.enabled = true;
                double on = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                foreach (var r in all) r.gameObject.SetActive(false);
                double deact = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                foreach (var r in all) r.gameObject.SetActive(true);
                double act = sw.Elapsed.TotalMilliseconds;
                L("cost-toggles n=" + N + " disable-ms=" + F(off) + " enable-ms=" + F(on) + " deactivate-ms=" + F(deact) + " activate-ms=" + F(act));
                foreach (var r in all) UnityEngine.Object.DestroyImmediate(r.gameObject);
            }
        }

        void When()
        {
            void Case(string name, Func<GameObject> setUp, System.Action<GameObject> hook, bool preRender, bool viaVisible = false)
            {
                var target = setUp();
                GameObject trigger = null;
                if (viaVisible)
                {
                    // A blue quad in view whose OnBecameVisible makes the change, during culling.
                    trigger = Make("RcTrigger", tiny, mat, new Vector3(15f, 15f, 0f));
                    trigger.AddComponent<RcVisibleHook>().Visible = () => hook(target);
                }
                else if (preRender) hooks.PreRender = () => hook(target);
                else hooks.PreCull = () => hook(target);
                string first = Render();
                string second = Render();
                L("when-" + name + " render=" + first + " next=" + second);
                hooks.PreCull = hooks.PreRender = null;
                if (trigger != null) UnityEngine.Object.DestroyImmediate(trigger);
                if (target != null)
                {
                    var parent = target.transform.parent;
                    UnityEngine.Object.DestroyImmediate(target);
                    if (parent != null) UnityEngine.Object.DestroyImmediate(parent.gameObject);
                }
            }
            GameObject Disabled() { var go = Make("RcWhen", full, mat, Vector3.zero); go.GetComponent<MeshRenderer>().enabled = false; return go; }
            GameObject Enabled() => Make("RcWhen", full, mat, Vector3.zero);
            GameObject Inactive() { var go = Make("RcWhen", full, mat, Vector3.zero); go.SetActive(false); return go; }
            GameObject InactiveParent()
            {
                var parent = new GameObject("RcWhenParent");
                parent.SetActive(false);
                return Make("RcWhen", full, mat, Vector3.zero, parent.transform);
            }
            GameObject created = null;
            foreach (bool pre in new[] { false, true })
            {
                string at = pre ? "prerender" : "precull";
                Case(at + "-enable", Disabled, go => go.GetComponent<MeshRenderer>().enabled = true, pre);
                Case(at + "-disable", Enabled, go => go.GetComponent<MeshRenderer>().enabled = false, pre);
                Case(at + "-activate", Inactive, go => go.SetActive(true), pre);
                Case(at + "-deactivate", Enabled, go => go.SetActive(false), pre);
                Case(at + "-activate-parent", InactiveParent, go => go.transform.parent.gameObject.SetActive(true), pre);
                Case(at + "-create", () => null, _ => created = Make("RcWhenCreated", full, mat, Vector3.zero), pre);
                if (created != null) { UnityEngine.Object.DestroyImmediate(created); created = null; }
            }
            Case("visible-enable", Disabled, go => go.GetComponent<MeshRenderer>().enabled = true, false, true);
            Case("visible-disable", Enabled, go => go.GetComponent<MeshRenderer>().enabled = false, false, true);
            Case("visible-create", () => null, _ => created = Make("RcWhenCreated", full, mat, Vector3.zero), false, true);
            if (created != null) { UnityEngine.Object.DestroyImmediate(created); created = null; }
        }

        void Ties(int queue)
        {
            var m1 = new Material(mat) { renderQueue = queue };
            var a = Quad(new Rect(0, 0, 10, 10), Color.red);
            var b = Quad(new Rect(6, 6, 10, 10), Color.green);
            string q = "q" + queue;
            void Case(string name, Func<(GameObject A, GameObject B)> setUp, System.Action<GameObject, GameObject> change)
            {
                var (ga, gb) = setUp();
                string before = Render();
                string top = "" + At(before, 8, 8);
                if (change != null)
                {
                    change(ga, gb);
                    top += ">" + At(Render(), 8, 8);
                }
                L("tie-" + q + "-" + name + " top=" + top + " map=" + Map());
                foreach (var go in new[] { ga, gb })
                {
                    var parent = go.transform.parent;
                    UnityEngine.Object.DestroyImmediate(go);
                    if (parent != null) UnityEngine.Object.DestroyImmediate(parent.gameObject);
                }
            }
            (GameObject, GameObject) AB() { var x = Make("RcA", a, m1, Vector3.zero); var y = Make("RcB", b, m1, Vector3.zero); return (x, y); }
            (GameObject, GameObject) BA() { var y = Make("RcB", b, m1, Vector3.zero); var x = Make("RcA", a, m1, Vector3.zero); return (x, y); }
            Case("created-AB", AB, null);
            Case("created-BA", BA, null);
            Case("A-disable-enable", AB, (x, y) => { var r = x.GetComponent<MeshRenderer>(); r.enabled = false; cam.Render(); r.enabled = true; });
            Case("A-disable-enable-same-render", AB, (x, y) => { var r = x.GetComponent<MeshRenderer>(); r.enabled = false; r.enabled = true; });
            Case("A-deactivate-activate", AB, (x, y) => { x.SetActive(false); cam.Render(); x.SetActive(true); });
            Case("A-parent-deactivate-activate", () =>
            {
                var p = new GameObject("RcParentA");
                var x = Make("RcA", a, m1, Vector3.zero, p.transform);
                var y = Make("RcB", b, m1, Vector3.zero);
                return (x, y);
            }, (x, y) => { x.transform.parent.gameObject.SetActive(false); cam.Render(); x.transform.parent.gameObject.SetActive(true); });
            Case("inactive-AB-activate-BA", () =>
            {
                var x = Make("RcA", a, m1, Vector3.zero); x.SetActive(false);
                var y = Make("RcB", b, m1, Vector3.zero); y.SetActive(false);
                y.SetActive(true); x.SetActive(true);
                return (x, y);
            }, null);
            Case("disabled-AB-enable-BA", () =>
            {
                var x = Make("RcA", a, m1, Vector3.zero); x.GetComponent<MeshRenderer>().enabled = false;
                var y = Make("RcB", b, m1, Vector3.zero); y.GetComponent<MeshRenderer>().enabled = false;
                y.GetComponent<MeshRenderer>().enabled = true; x.GetComponent<MeshRenderer>().enabled = true;
                return (x, y);
            }, null);
            Case("B-disable-enable", AB, (x, y) => { var r = y.GetComponent<MeshRenderer>(); r.enabled = false; cam.Render(); r.enabled = true; });
            Case("A-material-made-after", AB, (x, y) => x.GetComponent<MeshRenderer>().sharedMaterial = new Material(m1));
            Case("B-material-made-after", AB, (x, y) => y.GetComponent<MeshRenderer>().sharedMaterial = new Material(m1));
            Case("A-material-set-again", AB, (x, y) => x.GetComponent<MeshRenderer>().sharedMaterial = m1);
            var m2 = new Material(m1);
            Case("created-AB-A-older-material", () => { var x = Make("RcA", a, m1, Vector3.zero); var y = Make("RcB", b, m2, Vector3.zero); return (x, y); }, null);
            Case("created-AB-A-newer-material", () => { var x = Make("RcA", a, m2, Vector3.zero); var y = Make("RcB", b, m1, Vector3.zero); return (x, y); }, null);
        }
    }
}
