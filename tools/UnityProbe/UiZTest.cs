// How an overlay canvas's draws meet the depth buffer ("PROBE ... UZ ..."), at the main menu
// (10 s after the intro video has gone). Found with the Colony Summary's charts (#57): their lines draw with the game's
// "Lines/Colored Blended" shader, whose pass tests depth (ZTest LEqual, ZWrite Off) instead of
// reading unity_GUIZTestMode as the UI shaders do.
//
// An overlay canvas (sorting order 30000, one canvas unit a pixel) holds 100 x 100 quads drawn
// with that shader in red, from the left: at z 0, at z +500 and -500 over the menu, and at z 0
// over a quad a probe camera (depth 50, clearing nothing) drew just past its near plane with a
// depth-writing shader; then a quad with the default UI material over that same depth, as the
// control. A second row draws a depth-writing quad in the canvas at z 0, then the red quad at
// z +500, -500 and 0 over it, and the writer alone; a third, the writer, then the red quad at
// z -500 in a canvas sorted above. Each case's centre pixel is read back (ReadPixels) at the end of the frame.
// With PROBE_SCENARIO=uiztest only this probe runs; it ends with a UiZTestProbeDone canvas.
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class UiZTest : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;

        static void L(string s) => Rec.Log("UZ " + s);

        /// A quad over its rect in its colour, with the material it is given.
        public class Quad : MaskableGraphic
        {
            protected override void OnPopulateMesh(VertexHelper vh)
            {
                var r = rectTransform.rect;
                Color32 c = color;
                vh.Clear();
                vh.AddVert(new Vector3(r.xMin, r.yMin), c, Vector2.zero);
                vh.AddVert(new Vector3(r.xMin, r.yMax), c, Vector2.up);
                vh.AddVert(new Vector3(r.xMax, r.yMax), c, Vector2.one);
                vh.AddVert(new Vector3(r.xMax, r.yMin), c, Vector2.right);
                vh.AddTriangle(0, 1, 2);
                vh.AddTriangle(2, 3, 0);
            }
        }

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            // After the intro video, whose overlay covers the screen.
            if (GameObject.Find("VideoScreen") != null) { sceneStart = -1f; return; }
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static Shader Find(string name)
        {
            var s = Shader.Find(name);
            if (s != null) return s;
            foreach (var x in Resources.FindObjectsOfTypeAll<Shader>())
                if (x.name == name) return x;
            return null;
        }

        static void AddQuad(Transform canvas, string name, float x, float z, Material material, Color color, float y = 100f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.sizeDelta = new Vector2(100f, 100f);
            rt.anchoredPosition3D = new Vector3(x, y, z);
            var q = go.AddComponent<Quad>();
            q.material = material;
            q.color = color;
        }

        IEnumerator Run()
        {
            var lines = Find("Lines/Colored Blended");
            var lit = Find("Legacy Shaders/VertexLit") ?? Find("Standard");
            L("shaders lines=" + (lines != null) + " depthwriter=" + (lit == null ? "none" : lit.name));
            if (lines == null || lit == null) yield break;
            var lineMat = new Material(lines) { name = "UzLines" };

            // The probe camera: world units are screen pixels, and its quad lies 0.5 past the
            // camera (near plane 0.3), over screen x 700-800 and 900-1000, y 100-200.
            var camGo = new GameObject("UzCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = Screen.height / 2f;
            cam.transform.position = new Vector3(Screen.width / 2f, Screen.height / 2f, -10f);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.Nothing;
            cam.depth = 50f;
            cam.cullingMask = 1 << 31;
            var quadGo = new GameObject("UzDepthQuad");
            quadGo.layer = 31;
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(700f, 100f, -9.5f), new Vector3(700f, 200f, -9.5f), new Vector3(1000f, 200f, -9.5f), new Vector3(1000f, 100f, -9.5f) };
            mesh.triangles = new[] { 0, 1, 2, 2, 3, 0, 0, 2, 1, 2, 0, 3 };
            mesh.RecalculateBounds();
            quadGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            quadGo.AddComponent<MeshRenderer>().sharedMaterial = new Material(lit) { name = "UzDepthWriter", color = Color.blue };

            var canvasGo = new GameObject("UiZTestCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            AddQuad(canvasGo.transform, "Z0", 100f, 0f, lineMat, Color.red);
            AddQuad(canvasGo.transform, "ZPlus500", 300f, 500f, lineMat, Color.red);
            AddQuad(canvasGo.transform, "ZMinus500", 500f, -500f, lineMat, Color.red);
            AddQuad(canvasGo.transform, "Z0OverDepth", 700f, 0f, lineMat, Color.red);
            AddQuad(canvasGo.transform, "DefaultOverDepth", 900f, 0f, null, Color.green);
            // Over a depth-writing quad drawn in the canvas itself at z 0: the red quad behind
            // it (+500), in front (-500), at the same depth, and the writer alone.
            var writer = new Material(lit) { name = "UzUiDepthWriter", color = Color.blue };
            foreach (var (x, z) in new[] { (100f, 500f), (300f, -500f), (500f, 0f) })
            {
                AddQuad(canvasGo.transform, "Writer", x, 0f, writer, Color.white, 300f);
                AddQuad(canvasGo.transform, "LinesAfterWriter" + z, x, z, lineMat, Color.red, 300f);
            }
            AddQuad(canvasGo.transform, "WriterAlone", 700f, 0f, writer, Color.white, 300f);
            // A third row across two canvases: the writer in this one, the red quad at z -500 in
            // a canvas sorted above it.
            AddQuad(canvasGo.transform, "Writer", 100f, 0f, writer, Color.white, 500f);
            var aboveGo = new GameObject("UiZTestCanvasAbove", typeof(RectTransform));
            var above = aboveGo.AddComponent<Canvas>();
            above.renderMode = RenderMode.ScreenSpaceOverlay;
            above.sortingOrder = 30001;
            AddQuad(aboveGo.transform, "LinesInCanvasAbove", 100f, -500f, lineMat, Color.red, 500f);

            for (int i = 0; i < 10; i++) yield return null;
            yield return new WaitForEndOfFrame();
            var shot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
            shot.Apply();
            L("screen " + Screen.width + "x" + Screen.height + " shot " + shot.width + "x" + shot.height);
            foreach (var (name, x) in new[] { ("Z0", 150), ("ZPlus500", 350), ("ZMinus500", 550), ("Z0OverDepth", 750), ("DefaultOverDepth", 950), ("DepthQuadAlone", 850),
                         ("LinesBehindUiWriter", 150 + 10000), ("LinesBeforeUiWriter", 350 + 10000), ("LinesAtUiWriter", 550 + 10000), ("UiWriterAlone", 750 + 10000),
                         ("LinesInCanvasAboveWriter", 150 + 20000) })
            {
                Color32 c = shot.GetPixel(x % 10000, 150 + 200 * (x / 10000));
                L(name + " " + c.r + "," + c.g + "," + c.b);
            }
            var dir = System.Environment.GetEnvironmentVariable("PROBE_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, "uiztest.png"), shot.EncodeToPNG());
            Object.Destroy(shot);
            Object.Destroy(canvasGo);
            Object.Destroy(aboveGo);
            Object.Destroy(camGo);
            Object.Destroy(quadGo);
            var done = new GameObject("UiZTestProbeDone", typeof(RectTransform));
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("=== done");
        }
    }
}
