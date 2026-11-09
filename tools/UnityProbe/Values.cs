// Value-level probes: results of engine functions game code consumes (formatting, maths,
// random sequences, serialization, hierarchy queries), printed one per line ("PROBE ... VAL").
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SemanticsProbe
{
    [Serializable]
    public class JsonSample
    {
        public int number = 7;
        public float ratio = 0.1f;
        public string text = "a \"quoted\" é";
        public Vector3 position = new Vector3(1.5f, -2f, 0.333f);
        public List<int> list = new List<int> { 1, 2 };
        public Color color = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        public List<float> floats = new List<float> { 1f, 0.1f, -2.5f, 100000f, 1e21f, 1.5e-7f, 0.000001f, 123.456f, 3.4028235e38f, -0f, 1e-45f, 16777216f, 0.3f, 2.71828f, 0.030318595f, 3.9166574f, 1.8230687e-05f, -62.81875f, 0.00154345f, -2.1329275e-07f };
        public List<double> doubles = new List<double> { 0.1, 1e21, 1.5e-7, 123.456, 5e-324, 1.7976931348623157e308, 0.3, 1d / 3d, 1e22, 9007199254740993d, -7.3127151177519755e-06, 0.030318594544552595, 78663.40851152701, 3.9166573353688707, 1.8230687000260787e-05, -9553557779573.523, 0.30000000000000004, -0.0 };
    }

    public class Msg : MonoBehaviour
    {
        void Ping(string arg) => Rec.Log("VAL SendMessage Ping(" + arg + ") on " + name);
    }

    public class OrderRec : MonoBehaviour
    {
        public static string Order = "";
        void Awake() => Order += name + ".Awake ";
        void OnEnable() => Order += name + ".OnEnable ";
        void Start() => Order += name + ".Start ";
    }

    /// Destroys a target's component and child in one phase of a frame (Update, a coroutine's
    /// `yield return null`, LateUpdate or the end of the frame) and logs, at every phase after
    /// it for two frames, whether they are gone: where the player carries out the destruction
    /// Destroy asked for.
    public class DestroyPhase : MonoBehaviour
    {
        public string Phase;
        public GameObject Target;
        public bool Armed;
        int frames;
        BoxCollider box;
        Transform child;

        public void Arm()
        {
            box = Target.GetComponent<BoxCollider>();
            child = Target.transform.GetChild(0);
            Armed = true;
            StartCoroutine(Co());
            StartCoroutine(Eof());
        }

        void Check(string at)
        {
            if (frames == 0 || frames > 2) return;
            Rec.Log("VAL destroy in " + Phase + ": at " + at + " component " + (box == null ? "gone" : "there") + " child " + (child == null ? "gone" : "there") + " childCount=" + Target.transform.childCount);
        }

        void Fire(string at)
        {
            if (!Armed || Phase != at) return;
            Armed = false;
            frames = 1;
            Destroy(box);
            Destroy(child.gameObject);
            Rec.Log("VAL destroy in " + Phase + ": destroyed");
        }

        void FixedUpdate() { Check("FixedUpdate"); Fire("FixedUpdate"); }
        void Update() { Check("Update"); Fire("Update"); }
        void OnGUI() { if (Event.current.type != EventType.Repaint) return; Check("OnGUI"); Fire("OnGUI"); }
        public void FromStart() { Check("Start"); Fire("Start"); }
        void LateUpdate() { Check("LateUpdate"); Fire("LateUpdate"); }
        IEnumerator Co()
        {
            for (int i = 0; i < 4; i++) { yield return null; Check("co"); Fire("co"); }
        }
        IEnumerator Eof()
        {
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForEndOfFrame();
                Check("EndOfFrame"); Fire("EndOfFrame");
                // Added at the end of the frame, it starts in the next frame's first Start pass.
                if (Armed && Phase == "Start" && i == 0) gameObject.AddComponent<DestroyFromStart>();
                if (frames > 0) frames++;
            }
        }
    }

    public class DestroyFromStart : MonoBehaviour
    {
        void Start() => GetComponent<DestroyPhase>().FromStart();
    }

    /// Logs whether DestroyPhase's target lost its parts, from another behaviour's Update.
    public class DestroyWatch : MonoBehaviour
    {
        public DestroyPhase Of;
        int n;
        void Update() { if (n++ < 3) Rec.Log("VAL destroy in " + Of.Phase + ": watcher Update childCount=" + Of.Target.transform.childCount + " component " + (Of.Target.GetComponent<BoxCollider>() == null ? "gone" : "there")); }
    }

    // Classes for AddComponent's refusals: [DisallowMultipleComponent] on a base, on a middle
    // class and on a leaf; Dm0 has none of its own.
    [DisallowMultipleComponent] public class DmBase : MonoBehaviour { }
    public class DmA : DmBase { }
    public class DmB : DmBase { }
    public class Dm0 : MonoBehaviour { }
    [DisallowMultipleComponent] public class DmMid : Dm0 { }
    public class DmLeaf : DmMid { }
    [DisallowMultipleComponent] public class DmOwn : DmA { }
    public class Plain : MonoBehaviour { }

    /// What AddComponent does when it refuses a component (or does not): what it returns, the
    /// components left, and what reaches the log callbacks (type, message, stack trace), between
    /// two markers in Player.log ("VAL addcomp begin/end LABEL").
    ///
    /// Measured in U57's player (AddComponentRefusalTests holds it): every refusal returns C#
    /// null and raises one LogType.Log, with an empty stack trace, on both logMessageReceived
    /// and logMessageReceivedThreaded; Player.log gets the bare message. A refusal by
    /// [DisallowMultipleComponent] reads "Can't add 'ADDED' to GO because a 'EXISTING' is
    /// already added to the game object!", and a second line "A GameObject can only contain one
    /// 'X' component." when EXISTING is not X itself, X being the most basic class declaring the
    /// attribute. The same Transform, RectTransform or single engine component (Canvas, Camera,
    /// CanvasGroup, CanvasRenderer, MeshFilter, MeshRenderer, SpriteRenderer, LineRenderer,
    /// Light, Rigidbody(2D), Animator, Animation, AudioListener, ParticleSystem(Renderer)) again
    /// reads "Can't add component 'T' to GO because such a component is already added to the
    /// game object!". A Transform over a RectTransform replaces it, as the reverse does, even
    /// beside an Image. Duplicates of other types are added silently (MonoBehaviours without the
    /// attribute, BoxCollider(2D), AudioSource, ContentSizeFitter, LayoutElement,
    /// GraphicRaycaster). A SpriteRenderer over a MeshRenderer, a Rigidbody2D over a Rigidbody and
    /// a BoxCollider2D over a BoxCollider read "Can't add component 'T' to GO because it
    /// conflicts with the existing 'E' derived component!".
    public static class AddComponentRefusals
    {
        static string Esc(string s) => s == null ? "null" : s.Replace("\r", "\\r").Replace("\n", "\\n");

        static void V(string what, object value) => Rec.Log("VAL " + what + " = " + Convert.ToString(value, CultureInfo.InvariantCulture));

        static void Try(string label, Type[] existing, Type added, bool rect = false)
        {
            var go = rect ? new GameObject("AC_" + label, typeof(RectTransform)) : new GameObject("AC_" + label);
            var before = new List<Component>();
            before.Add(go.transform);
            foreach (var t in existing) before.Add(go.AddComponent(t));
            var main = new List<string>();
            var threaded = new List<string>();
            Application.LogCallback onMain = (c, st, ty) => main.Add(ty + " \"" + Esc(c) + "\" stack \"" + Esc(st) + "\"");
            Application.LogCallback onThreaded = (c, st, ty) => threaded.Add(ty + " \"" + Esc(c) + "\" stack \"" + Esc(st) + "\"");
            Rec.Log("VAL addcomp begin " + label);
            Application.logMessageReceived += onMain;
            Application.logMessageReceivedThreaded += onThreaded;
            Component got;
            try { got = go.AddComponent(added); }
            finally
            {
                Application.logMessageReceived -= onMain;
                Application.logMessageReceivedThreaded -= onThreaded;
            }
            Rec.Log("VAL addcomp end " + label);
            string result = (object)got == null ? "C# null" : got == null ? "fake null" : before.Contains(got) ? "the existing " + got.GetType().Name : "new " + got.GetType().Name;
            V("addcomp " + label + " returns", result);
            V("addcomp " + label + " components", string.Join(",", Array.ConvertAll(go.GetComponents<Component>(), c => c.GetType().Name)));
            V("addcomp " + label + " logMessageReceived", main.Count + (main.Count > 0 ? " " + string.Join(" | ", main) : ""));
            V("addcomp " + label + " logMessageReceivedThreaded", threaded.Count + (threaded.Count > 0 ? " " + string.Join(" | ", threaded) : ""));
            UnityEngine.Object.Destroy(go);
        }

        public static void Run()
        {
            Rec.Log("=== values: add component refusals");
            foreach (LogType t in Enum.GetValues(typeof(LogType)))
                V("stack trace type " + t, Application.GetStackTraceLogType(t));
            var vlg = typeof(UnityEngine.UI.VerticalLayoutGroup);
            var hlg = typeof(UnityEngine.UI.HorizontalLayoutGroup);
            Try("vlg-over-vlg", new[] { vlg }, vlg);
            Try("vlg-over-hlg", new[] { hlg }, vlg);
            Try("grid-over-vlg", new[] { vlg }, typeof(UnityEngine.UI.GridLayoutGroup));
            Try("A-over-A", new[] { typeof(DmA) }, typeof(DmA));
            Try("B-over-A", new[] { typeof(DmA) }, typeof(DmB));
            Try("Base-over-A", new[] { typeof(DmA) }, typeof(DmBase));
            Try("A-over-Base", new[] { typeof(DmBase) }, typeof(DmA));
            Try("Base-over-Base", new[] { typeof(DmBase) }, typeof(DmBase));
            Try("Leaf-over-Mid", new[] { typeof(DmMid) }, typeof(DmLeaf));
            Try("Mid-over-Leaf", new[] { typeof(DmLeaf) }, typeof(DmMid));
            Try("Mid-over-Dm0", new[] { typeof(Dm0) }, typeof(DmMid));
            Try("Dm0-over-Mid", new[] { typeof(DmMid) }, typeof(Dm0));
            Try("Own-over-A", new[] { typeof(DmA) }, typeof(DmOwn));
            Try("Own-over-Own", new[] { typeof(DmOwn) }, typeof(DmOwn));
            Try("B-over-Own", new[] { typeof(DmOwn) }, typeof(DmB));
            Try("Plain-over-Plain", new[] { typeof(Plain) }, typeof(Plain));
            Try("BoxCollider-over-BoxCollider", new[] { typeof(BoxCollider) }, typeof(BoxCollider));
            Try("Transform-over-Transform", new Type[0], typeof(Transform));
            Try("Transform-over-RectTransform", new Type[0], typeof(Transform), rect: true);
            Try("Transform-over-RectTransform+Image", new[] { typeof(UnityEngine.UI.Image) }, typeof(Transform), rect: true);
            Try("Transform-over-RectTransform+Plain", new[] { typeof(Plain) }, typeof(Transform), rect: true);
            var back = new GameObject("AC_back", typeof(RectTransform));
            var backChild = new GameObject("AC_backChild"); backChild.transform.SetParent(back.transform, false);
            back.transform.localPosition = new Vector3(1, 2, 3);
            var plain = back.AddComponent<Plain>();
            var oldRect = back.transform;
            var turned = back.AddComponent<Transform>();
            V("addcomp Transform-over-RectTransform keeps", "localPosition " + back.transform.localPosition + " children " + back.transform.childCount + " child's parent is it " + (backChild.transform.parent == back.transform) + " old RectTransform null " + (oldRect == null) + " returned is go.transform " + (turned == back.transform) + " Plain.transform is it " + (plain.transform == back.transform));
            UnityEngine.Object.Destroy(back);
            Try("RectTransform-over-RectTransform", new Type[0], typeof(RectTransform), rect: true);
            Try("RectTransform-over-Transform", new Type[0], typeof(RectTransform));
            Try("Canvas-over-Canvas", new[] { typeof(Canvas) }, typeof(Canvas), rect: true);
            // Engine types, each over itself, then pairs that may conflict.
            foreach (var t in new[] { typeof(Camera), typeof(CanvasGroup), typeof(CanvasRenderer), typeof(MeshFilter), typeof(MeshRenderer),
                typeof(SpriteRenderer), typeof(LineRenderer), typeof(Light), typeof(Rigidbody), typeof(Rigidbody2D), typeof(BoxCollider2D),
                typeof(Animator), typeof(Animation), typeof(AudioSource), typeof(AudioListener), typeof(ParticleSystem),
                typeof(ParticleSystemRenderer), typeof(UnityEngine.UI.Image), typeof(TMPro.TextMeshProUGUI), typeof(UnityEngine.UI.ContentSizeFitter),
                typeof(UnityEngine.UI.LayoutElement), typeof(UnityEngine.UI.Mask), typeof(UnityEngine.UI.RectMask2D), typeof(UnityEngine.UI.ScrollRect),
                typeof(UnityEngine.UI.Button), typeof(UnityEngine.UI.GraphicRaycaster), typeof(UnityEngine.UI.CanvasScaler) })
                Try(t.Name + "-over-" + t.Name, new[] { t }, t, rect: true);
            Try("Text-over-Image", new[] { typeof(UnityEngine.UI.Image) }, typeof(UnityEngine.UI.Text), rect: true);
            Try("SpriteRenderer-over-MeshRenderer", new[] { typeof(MeshRenderer) }, typeof(SpriteRenderer));
            Try("Rigidbody2D-over-Rigidbody", new[] { typeof(Rigidbody) }, typeof(Rigidbody2D));
            Try("BoxCollider2D-over-BoxCollider", new[] { typeof(BoxCollider) }, typeof(BoxCollider2D));
            Rec.Log("VAL addcomp begin ctor-vlg-vlg");
            var made = new GameObject("AC_ctor", vlg, vlg);
            Rec.Log("VAL addcomp end ctor-vlg-vlg");
            V("addcomp ctor-vlg-vlg components", string.Join(",", Array.ConvertAll(made.GetComponents<Component>(), c => c.GetType().Name)));
            UnityEngine.Object.Destroy(made);
        }
    }

    public static class Values
    {
        static void V(string what, object value) => Rec.Log("VAL " + what + " = " + Convert.ToString(value, CultureInfo.InvariantCulture));

        public static IEnumerator Run()
        {
            Rec.Log("=== values: screen");
            V("Screen", Screen.width + "x" + Screen.height + " current " + Screen.currentResolution.width + "x" + Screen.currentResolution.height + " fullScreenMode " + Screen.fullScreenMode);
            V("Display.main", Display.displays[0].systemWidth);

            // Mono's string hash (persisted by KMod.Local as a local mod's version stamp).
            foreach (var text in new[] { "", "a", "ab", "abc", "abcd", "SemanticsProbe", "onimaxxing-sim", "Ünïcødé 漢字", "nul\0after", "a\0", "\0" })
                V("strhash " + text.Replace("\0", "\\0"), text.GetHashCode());

            Rec.Log("=== values: formatting");
            V("Vector3", new Vector3(1.23456f, -2f, 1e-5f));
            V("Vector2", new Vector2(0.5f, 100.25f));
            V("Vector4", new Vector4(1, 2, 3, 4));
            V("Quaternion", Quaternion.Euler(10, 20, 30));
            V("Color", new Color(0.1f, 0.25f, 1f, 0.5f));
            V("Color32", new Color32(1, 2, 3, 4));
            V("Rect", new Rect(1, 2, 3.5f, 4));
            V("Bounds", new Bounds(Vector3.one, new Vector3(2, 4, 6)));
            V("Matrix4x4 TRS", Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.Euler(0, 90, 0), Vector3.one * 2));
            V("Vector2Int", new Vector2Int(3, -4));
            V("Color.HSVToRGB", Color.HSVToRGB(0.3f, 0.5f, 0.7f));
            V("Color.Lerp", Color.Lerp(Color.red, Color.blue, 0.3f));
            V("Color32 implicit", (Color32)new Color(0.5f, 0.501f, 0.999f, 0.002f));

            Rec.Log("=== values: maths");
            V("Mathf.Repeat/PingPong", Mathf.Repeat(-1.25f, 1f) + " " + Mathf.PingPong(3.5f, 2f));
            V("Mathf.SmoothStep/SmoothDamp", Mathf.SmoothStep(0f, 10f, 0.3f) + " " + SmoothDamp());
            V("ColorUtility.ToHtmlStringRGB", ColorUtility.ToHtmlStringRGB(new Color(0.5f, 0.25f, 1f, 0.75f)));
            V("Vector2.SignedAngle", Vector2.SignedAngle(Vector2.right, new Vector2(1, 1)));
            V("Mathf.Round/RoundToInt", Mathf.Round(2.5f) + " " + Mathf.Round(3.5f) + " " + Mathf.RoundToInt(-2.5f) + " " + Mathf.RoundToInt(1.5f));
            V("Mathf.Approximately", Mathf.Approximately(1f, 1.0000001f) + " " + Mathf.Approximately(0f, 1e-40f));
            V("Vector3.Slerp", Vector3.Slerp(Vector3.right, Vector3.up, 0.3f));
            V("Vector3.Normalize tiny", new Vector3(1e-6f, 0, 0).normalized);
            V("Quaternion.LookRotation", Quaternion.LookRotation(new Vector3(1, 2, 3), Vector3.up));
            V("Quaternion.eulerAngles", new Quaternion(0.1f, 0.2f, 0.3f, 0.9273618f).eulerAngles);
            V("Quaternion.Slerp", Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(0, 170, 0), 0.4f));
            V("Quaternion.FromToRotation", Quaternion.FromToRotation(Vector3.up, Vector3.left));
            V("Matrix4x4.Ortho", Matrix4x4.Ortho(-1, 1, -2, 2, 0.1f, 10f));

            Rec.Log("=== values: random");
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.value", UnityEngine.Random.value.ToString("R"));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.Range(0f, 1f)", UnityEngine.Random.Range(0f, 1f).ToString("R"));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.Range(2f, 10f)", UnityEngine.Random.Range(2f, 10f).ToString("R"));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.Range(10f, 2f)", UnityEngine.Random.Range(10f, 2f).ToString("R"));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.Range(0, 1000)", UnityEngine.Random.Range(0, 1000));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.insideUnitCircle", UnityEngine.Random.insideUnitCircle.ToString("R"));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.rotation", UnityEngine.Random.rotation.ToString("R"));
            UnityEngine.Random.InitState(42); V("UnityEngine.Random.ColorHSV", UnityEngine.Random.ColorHSV().ToString("R"));

            Rec.Log("=== values: json");
            string json = JsonUtility.ToJson(new JsonSample());
            V("JsonUtility.ToJson", json);

            Rec.Log("=== values: hierarchy");
            var root = new GameObject("H0");
            var c1 = new GameObject("H1"); c1.transform.SetParent(root.transform);
            var c2 = new GameObject("H2"); c2.transform.SetParent(root.transform);
            var c11 = new GameObject("H11"); c11.transform.SetParent(c1.transform);
            c11.SetActive(false);
            foreach (var go in new[] { root, c1, c2, c11 }) go.AddComponent<BoxCollider>();
            V("GetComponentsInChildren", string.Join(",", Array.ConvertAll(root.GetComponentsInChildren<BoxCollider>(), x => x.name)));
            V("GetComponentsInChildren(true)", string.Join(",", Array.ConvertAll(root.GetComponentsInChildren<BoxCollider>(true), x => x.name)));
            V("GetComponentsInParent", string.Join(",", Array.ConvertAll(c11.GetComponentsInParent<BoxCollider>(true), x => x.name)));
            V("Transform.Find", (root.transform.Find("H1/H11") != null) + " " + (GameObject.Find("H11") != null));
            // Paths with empty segments; an empty-named child under H2 to tell "itself" from "a child named ''".
            new GameObject("").transform.SetParent(c2.transform);
            foreach (var path in new[] { "", "/", "H1/", "/H1", "H1//H11", "H1/H11/", ".", "..", "H2/", "H2//" })
            {
                var found = root.transform.Find(path);
                V("Transform.Find \"" + path + "\"", found == null ? "null" : found == root.transform ? "self" : "'" + found.name + "' under " + (found.parent == null ? "none" : found.parent.name));
            }
            root.transform.position = new Vector3(10, 0, 0);
            root.transform.localScale = new Vector3(2, 2, 2);
            c2.transform.localPosition = new Vector3(1, 1, 0);
            var loose = new GameObject("L");
            loose.transform.position = new Vector3(5, 5, 5);
            loose.transform.SetParent(c2.transform, true);
            V("SetParent(worldPositionStays)", loose.transform.localPosition.ToString("R") + " " + loose.transform.localScale.ToString("R"));
            loose.transform.SetParent(null, false);
            V("SetParent(null, false)", loose.transform.position.ToString("R"));
            V("sibling index", c2.transform.GetSiblingIndex() + " " + root.transform.childCount);
            c2.transform.SetAsFirstSibling();
            V("after SetAsFirstSibling", root.transform.GetChild(0).name);

            var ra = new GameObject("RA"); var rb = new GameObject("RB"); var rc = new GameObject("RC");
            V("root sibling indices", ra.transform.GetSiblingIndex() + " " + rb.transform.GetSiblingIndex() + " " + rc.transform.GetSiblingIndex());
            rc.transform.SetAsFirstSibling();
            V("root after SetAsFirstSibling", ra.transform.GetSiblingIndex() + " " + rb.transform.GetSiblingIndex() + " " + rc.transform.GetSiblingIndex());
            UnityEngine.Object.DontDestroyOnLoad(rb);
            V("root after DontDestroyOnLoad", ra.transform.GetSiblingIndex() + " " + rb.transform.GetSiblingIndex() + " " + rc.transform.GetSiblingIndex());

            Rec.Log("=== values: root canvas sizing");
            var cgo = new GameObject("ProbeCanvas", typeof(RectTransform));
            var crt = (RectTransform)cgo.transform;
            V("before AddComponent<Canvas>", crt.rect);
            var cv = cgo.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            V("after AddComponent<Canvas>", crt.rect + " " + crt.position);
            var child = new GameObject("Child", typeof(RectTransform));
            child.transform.SetParent(cgo.transform, false);
            ((RectTransform)child.transform).anchorMin = Vector2.zero;
            ((RectTransform)child.transform).anchorMax = Vector2.one;
            V("child right away", ((RectTransform)child.transform).rect);
            var offCanvas = new GameObject("InactiveCanvas", typeof(RectTransform));
            offCanvas.SetActive(false);
            offCanvas.AddComponent<Canvas>();
            V("inactive canvas", ((RectTransform)offCanvas.transform).rect);
            offCanvas.SetActive(true);
            V("after activation", ((RectTransform)offCanvas.transform).rect);
            yield return null;
            V("canvas next frame", crt.rect + " " + ((RectTransform)child.transform).rect);

            Rec.Log("=== values: instantiate order");
            var proto = new GameObject("P0");
            proto.AddComponent<OrderRec>();
            var pc = new GameObject("P1"); pc.transform.SetParent(proto.transform); pc.AddComponent<OrderRec>();
            OrderRec.Order = "";
            var clone = UnityEngine.Object.Instantiate(proto);
            V("Instantiate callbacks", OrderRec.Order);
            V("clone names", clone.name + " " + clone.transform.GetChild(0).name);
            OrderRec.Order = "";
            proto.SetActive(false);
            var inactive = UnityEngine.Object.Instantiate(proto);
            V("Instantiate inactive", OrderRec.Order + "| activeSelf=" + inactive.activeSelf);
            OrderRec.Order = "";
            yield return null;
            V("next frame", OrderRec.Order);

            yield return ClonePendingDestroy();

            Rec.Log("=== values: messages");
            var m = new GameObject("M").AddComponent<Msg>();
            new GameObject("Mc").AddComponent<Msg>().transform.SetParent(m.transform);
            m.gameObject.SendMessage("Ping", "a", SendMessageOptions.RequireReceiver);
            m.gameObject.SendMessage("Missing", SendMessageOptions.DontRequireReceiver);
            for (int i = 0; i < 10; i++) yield return null;

            AddComponentRefusals.Run();
            Rec.Log("=== values: float precision");
            FloatPrecision.Run();
            Rec.Log("=== values: done");
        }

        /// What a source's parts tell, and what Instantiate copies of them, while they await the
        /// end-of-frame destruction Destroy asked for (Better Automation Overlay strips a label
        /// this way and clones it).
        static string Parts(GameObject go)
        {
            if (go == null) return "null";
            var parts = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                parts.Add(c == null ? "null" : c.GetType().Name + (c is Behaviour b ? (b.enabled ? "+" : "-") : ""));
            string children = "";
            for (int i = 0; i < go.transform.childCount; i++)
                children += (i == 0 ? "" : ",") + go.transform.GetChild(i).name;
            return go.name + " active=" + go.activeSelf + " [" + string.Join(" ", parts) + "] children=" + go.transform.childCount + "(" + children + ")"
                + " Get<Beh>=" + (go.GetComponent<OrderRec>() != null) + " Get<BoxCollider>=" + (go.GetComponent<BoxCollider>() != null)
                + " Get<ContentSizeFitter>=" + (go.GetComponent<UnityEngine.UI.ContentSizeFitter>() != null)
                + " InChildren<Beh>=" + go.GetComponentsInChildren<OrderRec>(true).Length;
        }

        static GameObject MakeStripped(string name, bool active)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(active);
            go.AddComponent<OrderRec>();
            go.AddComponent<BoxCollider>();
            go.AddComponent<UnityEngine.UI.ContentSizeFitter>();
            go.AddComponent<UnityEngine.UI.LayoutElement>();
            var keep = new GameObject("Keep", typeof(RectTransform)); keep.transform.SetParent(go.transform, false);
            var gone = new GameObject("Gone", typeof(RectTransform)); gone.transform.SetParent(go.transform, false);
            gone.AddComponent<OrderRec>();
            new GameObject("GoneChild").transform.SetParent(gone.transform, false);
            return go;
        }

        static void Strip(GameObject go)
        {
            UnityEngine.Object.Destroy(go.GetComponent<OrderRec>());
            UnityEngine.Object.Destroy(go.GetComponent<BoxCollider>());
            UnityEngine.Object.Destroy(go.GetComponent<UnityEngine.UI.ContentSizeFitter>());
            UnityEngine.Object.Destroy(go.transform.Find("Gone").gameObject);
        }

        // Measured in the original player (U59):
        // - Until the destruction, the source's GetComponent(s), childCount, GetChild and Find
        //   still return the parts Destroy was called on; a Behaviour among them reports
        //   enabled = false, other components are unchanged.
        // - Instantiate(GameObject), Instantiate(Component), Instantiate under a parent and a
        //   clone of that clone, made before the destruction, copy those parts, active or
        //   inactive source alike; the copied behaviours are disabled (enabled = false), and
        //   they stay in the clone, still disabled, after the source loses them. A clone made
        //   after the destruction has none of them. Destroy(go) then Instantiate(go): the clone
        //   copies every behaviour disabled and lives on.
        // - Destruction points ("destroy phases"): Destroy called in Start or Update is carried
        //   out when the Update pass ends (another behaviour's Update later in the pass still
        //   finds the parts; a `yield return null` coroutine, LateUpdate and the end of the
        //   frame do not); called in FixedUpdate, before Update; in a coroutine resumed after
        //   Update or in LateUpdate, after LateUpdate (LateUpdate still finds a coroutine's
        //   parts, the end of the frame does not); in OnGUI or at WaitForEndOfFrame, after the
        //   next frame's Update pass (that Update still finds them).
        static IEnumerator ClonePendingDestroy()
        {
            Rec.Log("=== values: clone pending destroy");
            foreach (var active in new[] { true, false })
            {
                string a = active ? "active " : "inactive ";
                var src = MakeStripped("Src", active);
                Strip(src);
                V(a + "source after Destroy", Parts(src));
                var clone = UnityEngine.Object.Instantiate(src);
                V(a + "clone same frame", Parts(clone));
                var byComponent = UnityEngine.Object.Instantiate(src.GetComponent<UnityEngine.UI.LayoutElement>());
                V(a + "clone by component same frame", Parts(byComponent.gameObject));
                var parent = new GameObject("Parent", typeof(RectTransform));
                var underParent = UnityEngine.Object.Instantiate(src, parent.transform, false);
                V(a + "clone under a parent same frame", Parts(underParent));
                var ofClone = UnityEngine.Object.Instantiate(clone);
                V(a + "clone of clone same frame", Parts(ofClone));
                // A child destroyed on its own, then the source cloned: the child's own parts.
                var gone = src.transform.Find("Gone");
                V(a + "Find(Gone) on source", gone == null ? "null" : gone.name + " children=" + gone.childCount);
                yield return null;
                V(a + "source next frame", Parts(src));
                V(a + "clone next frame", Parts(clone));
                V(a + "clone of clone next frame", Parts(ofClone));
                V(a + "clone made next frame", Parts(UnityEngine.Object.Instantiate(src)));
                V(a + "clone of clone made next frame", Parts(UnityEngine.Object.Instantiate(clone)));
            }
            // DestroyImmediate for comparison.
            var imm = MakeStripped("Imm", true);
            UnityEngine.Object.DestroyImmediate(imm.GetComponent<OrderRec>());
            UnityEngine.Object.DestroyImmediate(imm.transform.Find("Gone").gameObject);
            V("DestroyImmediate clone", Parts(UnityEngine.Object.Instantiate(imm)));
            // A destroyed source's clone: Destroy(go) and then Instantiate(go) the same frame.
            var whole = MakeStripped("Whole", true);
            UnityEngine.Object.Destroy(whole);
            V("Destroy(go) source", Parts(whole));
            GameObject wholeClone = null;
            try { wholeClone = UnityEngine.Object.Instantiate(whole); V("Destroy(go) clone", Parts(wholeClone)); }
            catch (Exception e) { V("Destroy(go) clone", "EXCEPTION " + e.GetType().Name + ": " + e.Message); }
            yield return null;
            V("Destroy(go) clone next frame", Parts(wholeClone));

            Rec.Log("=== values: destroy phases");
            foreach (var phase in new[] { "Update", "co", "LateUpdate", "EndOfFrame", "OnGUI", "Start", "FixedUpdate" })
            {
                var target = new GameObject("Target");
                target.AddComponent<BoxCollider>();
                new GameObject("Child").transform.SetParent(target.transform, false);
                var d = new GameObject("Destroyer").AddComponent<DestroyPhase>();
                d.Phase = phase; d.Target = target;
                yield return null;
                d.Arm();
                new GameObject("Watcher").AddComponent<DestroyWatch>().Of = d;
                for (int i = 0; i < 4; i++) yield return null;
            }
        }

        static string SmoothDamp()
        {
            var velocity = Vector3.zero;
            var x = Vector3.SmoothDamp(Vector3.zero, new Vector3(10f, 0f, 0f), ref velocity, 0.3f);
            return x.x.ToString("R", CultureInfo.InvariantCulture) + " " + velocity.x.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
