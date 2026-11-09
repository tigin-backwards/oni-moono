// CanvasRenderer.cullTransparentMesh ("PROBE ... CA ..."), at the main menu (10 s after the intro
// video has gone). Unity documents the flag as: geometry "can be ignored when the vertex color
// alpha is close to zero for every vertex of the mesh". This measures what "close to zero" is,
// which alpha counts (the vertices', the renderer's SetColor/SetAlpha, CanvasGroups), whether a
// culled element keeps its depth and takes raycasts, when changes take effect, what a Mask whose
// graphic is culled does, and the flag's default on renderers made at run time and in the game's
// loaded objects.
//
// An overlay canvas (sorting order 30000, a canvas unit a pixel) holds a black quad under a grid
// of 20 x 20 quads, each drawn red with Hidden/Internal-Colored set to blend One Zero: the quad's
// pixels are red when it is drawn, whatever its alpha, and black when it is not. Each case's
// centre pixel is read back at the end of the frame (ReadPixels), with its renderer's
// absoluteDepth and whether the canvas's GraphicRaycaster hits it there. A second shot follows
// changes made after the first (flag flipped, alpha raised or lowered, a sibling added first so
// the canvas renumbers). With PROBE_SCENARIO=cullalpha only this probe runs; it ends with a
// CullAlphaProbeDone canvas.
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class CullAlpha : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;

        static void L(string s) => Rec.Log("CA " + s);

        /// A quad over its rect in its colour, each vertex's alpha byte given (or the colour's).
        public class AlphaQuad : MaskableGraphic
        {
            public byte[] Alphas;

            protected override void OnPopulateMesh(VertexHelper vh)
            {
                var r = rectTransform.rect;
                vh.Clear();
                Color32 C(int i)
                {
                    Color32 c = color;
                    if (Alphas != null) c.a = Alphas[i];
                    return c;
                }
                vh.AddVert(new Vector3(r.xMin, r.yMin), C(0), Vector2.zero);
                vh.AddVert(new Vector3(r.xMin, r.yMax), C(1), Vector2.up);
                vh.AddVert(new Vector3(r.xMax, r.yMax), C(2), Vector2.one);
                vh.AddVert(new Vector3(r.xMax, r.yMin), C(3), Vector2.right);
                vh.AddTriangle(0, 1, 2);
                vh.AddTriangle(2, 3, 0);
            }
        }

        class Case
        {
            public string Name;
            public GameObject Holder;
            public Graphic Graphic;
            public CanvasRenderer Renderer;
            public Vector2Int Centre;
        }

        readonly List<Case> cases = new();
        Transform canvasTransform;
        Material opaque;
        GraphicRaycaster raycaster;

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (GameObject.Find("VideoScreen") != null) { sceneStart = -1f; return; }
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);
            return rt;
        }

        Vector2 NextSlot(out Vector2Int centre)
        {
            int i = cases.Count;
            float x = 30 + (i % 30) * 40, y = 60 + (i / 30) * 40;
            centre = new Vector2Int((int)x + 10, (int)y + 10);
            return new Vector2(x, y);
        }

        /// A case: a holder at its slot (with a CanvasGroup when group is given) and a red quad
        /// filling it.
        Case Add(string name, bool cull, byte[] alphas = null, float? rendererAlpha = null, Color? rendererColor = null, float? group = null,
                 float? outerGroup = null)
        {
            var at = NextSlot(out var centre);
            var holder = Rect(canvasTransform, "Case_" + name, at.x, at.y, 20, 20).gameObject;
            var parent = holder.transform;
            if (outerGroup is { } og) holder.AddComponent<CanvasGroup>().alpha = og;
            if (group is { } g)
            {
                var inner = Rect(holder.transform, "Group", 0, 0, 20, 20);
                inner.gameObject.AddComponent<CanvasGroup>().alpha = g;
                parent = inner;
            }
            var quadRt = Rect(parent, "Quad", 0, 0, 20, 20);
            var q = quadRt.gameObject.AddComponent<AlphaQuad>();
            q.Alphas = alphas;
            q.material = opaque;
            q.color = Color.red;
            q.canvasRenderer.cullTransparentMesh = cull;
            if (rendererAlpha is { } ra) q.canvasRenderer.SetAlpha(ra);
            if (rendererColor is { } rc) q.canvasRenderer.SetColor(rc);
            var c = new Case { Name = name, Holder = holder, Graphic = q, Centre = centre };
            cases.Add(c);
            return c;
        }

        /// A case drawn by a bare CanvasRenderer from a mesh whose colours are red with alpha a.
        void AddMesh(string name, float a)
        {
            var at = NextSlot(out var centre);
            var holder = Rect(canvasTransform, "Case_" + name, at.x, at.y, 20, 20).gameObject;
            var cr = holder.AddComponent<CanvasRenderer>();
            cr.cullTransparentMesh = true;
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(0, 0), new Vector3(0, 20), new Vector3(20, 20), new Vector3(20, 0) };
            var c = new Color(1f, 0f, 0f, a);
            mesh.colors = new[] { c, c, c, c };
            mesh.triangles = new[] { 0, 1, 2, 2, 3, 0 };
            cr.SetMesh(mesh);
            cr.materialCount = 1;
            cr.SetMaterial(opaque, 0);
            cases.Add(new Case { Name = name, Holder = holder, Centre = centre, Renderer = cr });
        }

        static byte[] All(byte a) => new[] { a, a, a, a };

        IEnumerator Run()
        {
            var colored = Shader.Find("Hidden/Internal-Colored");
            L("shader " + (colored != null));
            if (colored == null) yield break;
            opaque = new Material(colored) { name = "CaOpaque" };
            opaque.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            opaque.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            opaque.SetInt("_ZWrite", 0);
            opaque.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            opaque.SetInt("_Cull", 0);

            Defaults();

            var canvasGo = new GameObject("CullAlphaCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            raycaster = canvasGo.AddComponent<GraphicRaycaster>();
            canvasTransform = canvasGo.transform;
            var back = Rect(canvasTransform, "Back", 0, 40, Screen.width, 300).gameObject.AddComponent<AlphaQuad>();
            back.material = opaque;
            back.color = Color.black;
            back.raycastTarget = false;

            // Vertex alpha alone (bytes), the flag on and off.
            foreach (bool cull in new[] { true, false })
                foreach (byte a in new byte[] { 0, 1, 2, 3, 255 })
                    Add("vertex" + a + (cull ? "_on" : "_off"), cull, All(a));
            // The renderer's alpha (SetAlpha), vertices opaque.
            foreach (bool cull in new[] { true, false })
                foreach (float f in new[] { 0f, 1e-7f, 1e-5f, 1e-4f, 5e-4f, 9e-4f, 0.001f, 0.0011f, 0.0015f, 0.00195f, 0.002f, 0.0025f, 0.003f, 0.0039f, 0.004f, 0.005f, 0.01f })
                    Add("ralpha" + f.ToString("R") + (cull ? "_on" : "_off"), cull, rendererAlpha: f);
            // SetColor's alpha, and its rgb.
            foreach (bool cull in new[] { true, false })
            {
                Add("rcolor_a0" + (cull ? "_on" : "_off"), cull, rendererColor: new Color(1f, 1f, 1f, 0f));
                Add("rcolor_rgb0" + (cull ? "_on" : "_off"), cull, rendererColor: new Color(0f, 0f, 0f, 1f));
            }
            // CanvasGroup alpha.
            foreach (bool cull in new[] { true, false })
                foreach (float f in new[] { 0f, 1e-5f, 0.001f, 0.002f, 0.004f, 0.01f })
                    Add("group" + f.ToString("R") + (cull ? "_on" : "_off"), cull, group: f);
            // Products: vertex byte x renderer alpha, renderer x group, nested groups.
            Add("v1_r0.5_on", true, All(1), rendererAlpha: 0.5f);
            Add("v1_r0.25_on", true, All(1), rendererAlpha: 0.25f);
            Add("v1_r0.1_on", true, All(1), rendererAlpha: 0.1f);
            Add("v2_r0.25_on", true, All(2), rendererAlpha: 0.25f);
            Add("v4_r0.25_on", true, All(4), rendererAlpha: 0.25f);
            Add("v1_g0.25_on", true, All(1), group: 0.25f);
            Add("r0.03_g0.03_on", true, rendererAlpha: 0.03f, group: 0.03f);
            Add("g0.03_g0.03_on", true, group: 0.03f, outerGroup: 0.03f);
            Add("g0.1_g0.01_on", true, group: 0.1f, outerGroup: 0.01f);
            // Mixed vertices.
            Add("mixed_0_0_0_255_on", true, new byte[] { 0, 0, 0, 255 });
            Add("mixed_0_0_0_1_on", true, new byte[] { 0, 0, 0, 1 });
            Add("mixed_255_0_0_0_on", true, new byte[] { 255, 0, 0, 0 });
            // Float vertex colours (Mesh.colors) set straight on a renderer: which alpha counts as zero.
            var below = System.BitConverter.Int32BitsToSingle(System.BitConverter.SingleToInt32Bits(0.001f) - 1);
            foreach (float f in new[] { -0.01f, 0f, 1e-6f, 0.0005f, 0.0006f, 0.0007f, 0.0008f, 0.0009f, 0.00095f, 0.00099f, 0.000999f, 0.0009999f, below, 0.001f, 0.0015f, 0.002f })
                AddMesh("fmesh" + f.ToString("R") + "_on", f);
            // Cases changed after the first shot.
            var flipOff = Add("p2_flipoff", true, All(0));
            var flipOn = Add("p2_flipon", false, All(0));
            var alphaUp = Add("p2_ralpha_up", true, rendererAlpha: 0f);
            var alphaDown = Add("p2_ralpha_down", true);
            var groupDown = Add("p2_group_down", true, group: 1f);
            var groupUp = Add("p2_group_up", true, group: 0f);
            var vertexUp = Add("p2_vertex_up", true, All(0));
            var vertexDown = Add("p2_vertex_down", true, All(255));
            var colorDown = Add("p2_graphic_color_down", true);
            // Masks: a Mask whose image's vertices are clear, the flag on and off (and an opaque
            // one), each with a red child (default UI material) and a red sibling after it.
            var masks = new List<(string Name, GameObject Child, GameObject Sibling, Image Mask)>();
            foreach (var (name, cull, a) in new[] { ("mask_clear_on", true, 0f), ("mask_clear_off", false, 0f), ("mask_opaque_on", true, 1f) })
            {
                var at = NextSlot(out var centre);
                cases.Add(new Case { Name = name + "_child", Centre = centre });
                var holder = Rect(canvasTransform, "Mask_" + name, at.x, at.y, 20, 20).gameObject;
                var maskRt = Rect(holder.transform, "Mask", 0, 0, 20, 20);
                var img = maskRt.gameObject.AddComponent<Image>();
                img.color = new Color(1f, 1f, 1f, a);
                img.canvasRenderer.cullTransparentMesh = cull;
                maskRt.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                var child = Rect(maskRt, "Child", 0, 0, 20, 20).gameObject;
                child.AddComponent<Image>().color = Color.red;
                var sat = NextSlot(out var scentre);
                cases.Add(new Case { Name = name + "_sibling", Centre = scentre });
                var sibling = Rect(canvasTransform, "MaskSibling_" + name, sat.x, sat.y, 20, 20).gameObject;
                sibling.AddComponent<Image>().color = Color.red;
                masks.Add((name, child, sibling, img));
            }

            for (int i = 0; i < 10; i++) yield return null;
            yield return new WaitForEndOfFrame();
            Shot("1");

            // Second shot: changes without rebuilding the graphic (flag, renderer alpha, groups)
            // and with it (vertex colours through the graphic's colour).
            flipOff.Graphic.canvasRenderer.cullTransparentMesh = false;
            flipOn.Graphic.canvasRenderer.cullTransparentMesh = true;
            alphaUp.Graphic.canvasRenderer.SetAlpha(1f);
            alphaDown.Graphic.canvasRenderer.SetAlpha(0f);
            groupDown.Holder.GetComponentInChildren<CanvasGroup>().alpha = 0f;
            groupUp.Holder.GetComponentInChildren<CanvasGroup>().alpha = 1f;
            ((AlphaQuad)vertexUp.Graphic).Alphas = All(255);
            vertexUp.Graphic.SetVerticesDirty();
            ((AlphaQuad)vertexDown.Graphic).Alphas = All(0);
            vertexDown.Graphic.SetVerticesDirty();
            colorDown.Graphic.color = Color.clear;
            yield return null;
            yield return new WaitForEndOfFrame();
            Shot("2a");
            // Renumbering: a sibling added before everything.
            Rect(canvasTransform, "Renumber", -100, -100, 1, 1).gameObject.AddComponent<CanvasRenderer>().transform.SetAsFirstSibling();
            for (int i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
            Shot("2");

            Object.Destroy(canvasGo);
            var done = new GameObject("CullAlphaProbeDone", typeof(RectTransform));
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("=== done");
        }

        void Shot(string tag)
        {
            var shot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            shot.ReadPixels(new UnityEngine.Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
            shot.Apply();
            L("shot " + tag + " screen " + Screen.width + "x" + Screen.height);
            var results = new List<RaycastResult>();
            foreach (var c in cases)
            {
                Color32 px = shot.GetPixel(c.Centre.x, c.Centre.y);
                string drawn = px.r > 128 && px.g < 64 ? "drawn" : px.r < 32 && px.g < 32 && px.b < 32 ? "culled" : "other";
                string extra = "";
                if (c.Graphic != null)
                {
                    results.Clear();
                    var ped = new PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = c.Centre };
                    raycaster.Raycast(ped, results);
                    bool hit = results.Any(r => r.gameObject == c.Graphic.gameObject);
                    var cr = c.Graphic.canvasRenderer;
                    extra = " depth " + cr.absoluteDepth + " hit " + hit + " flag " + cr.cullTransparentMesh + " ralpha " + cr.GetAlpha().ToString("R")
                            + " inherited " + cr.GetInheritedAlpha().ToString("R");
                }
                if (c.Renderer != null) extra = " depth " + c.Renderer.absoluteDepth;
                L("shot " + tag + " " + c.Name + " " + drawn + " (" + px.r + "," + px.g + "," + px.b + ")" + extra);
            }
            var dir = System.Environment.GetEnvironmentVariable("PROBE_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, "cullalpha-" + tag + ".png"), shot.EncodeToPNG());
            Object.Destroy(shot);
        }

        /// The flag on renderers made at run time, and on every loaded one.
        void Defaults()
        {
            var a = new GameObject("CaPlain");
            L("default AddComponent<CanvasRenderer> " + a.AddComponent<CanvasRenderer>().cullTransparentMesh);
            L("default AddComponent<Image> " + new GameObject("CaImage").AddComponent<Image>().canvasRenderer.cullTransparentMesh);
            L("default AddComponent<RawImage> " + new GameObject("CaRaw").AddComponent<RawImage>().canvasRenderer.cullTransparentMesh);
            L("default AddComponent<Text> " + new GameObject("CaText").AddComponent<Text>().canvasRenderer.cullTransparentMesh);
            L("default AddComponent<TextMeshProUGUI> " + new GameObject("CaTmp").AddComponent<TextMeshProUGUI>().canvasRenderer.cullTransparentMesh);
            L("default AddComponent<KImage> " + new GameObject("CaKImage").AddComponent<KImage>().canvasRenderer.cullTransparentMesh);
            var inactive = new GameObject("CaInactive");
            inactive.SetActive(false);
            L("default AddComponent<Image> inactive " + inactive.AddComponent<Image>().canvasRenderer.cullTransparentMesh);
            var src = new GameObject("CaSource");
            src.AddComponent<CanvasRenderer>().cullTransparentMesh = true;
            L("default Instantiate(flag true) " + Object.Instantiate(src).GetComponent<CanvasRenderer>().cullTransparentMesh);
            src.GetComponent<CanvasRenderer>().cullTransparentMesh = false;
            L("default Instantiate(flag false) " + Object.Instantiate(src).GetComponent<CanvasRenderer>().cullTransparentMesh);

            var all = Resources.FindObjectsOfTypeAll<CanvasRenderer>();
            int sceneOn = 0, sceneOff = 0, assetOn = 0, assetOff = 0;
            var byRoot = new SortedDictionary<string, int[]>();
            foreach (var r in all)
            {
                if (r.gameObject.name.StartsWith("Ca")) continue;
                bool inScene = r.gameObject.scene.IsValid();
                if (inScene) { if (r.cullTransparentMesh) sceneOn++; else sceneOff++; }
                else { if (r.cullTransparentMesh) assetOn++; else assetOff++; }
                var root = (inScene ? "scene:" : "asset:") + r.transform.root.name;
                if (!byRoot.TryGetValue(root, out var n)) byRoot[root] = n = new int[2];
                n[r.cullTransparentMesh ? 0 : 1]++;
            }
            L("census scene on " + sceneOn + " off " + sceneOff + " asset on " + assetOn + " off " + assetOff);
            foreach (var (root, n) in byRoot.Select(kv => (kv.Key, kv.Value)))
                if (n[0] > 0) L("census on " + root + " on " + n[0] + " off " + n[1]);
        }
    }
}
