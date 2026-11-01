// Destroy(gameObject) from an Awake while the object activates (issue #45): the game's
// PlantBranch destroys a branch from its state machine's constructor, which KPrefabID's Awake
// runs (OnPrefabInit, CreateSMIS), while the save's objects are made in SaveLoader's Start.
// Which of the object's behaviours then get Awake, OnEnable, Start, Update, OnDisable and
// OnDestroy: the one whose Awake destroys, those before it and those after it, and a child's.
//
// Cases, each an object with components P, D (its Awake destroys the object) and Q, and a child
// with C:
//   new-   a new GameObject made inactive, the components added, then SetActive(true), in a
//          coroutine;
//   clone- Instantiate of an inactive template (as Util.KInstantiate of a prefab), then
//          SetActive(true), in a coroutine;
//   start- the same clone made in a Start (as SaveLoader loads a save);
//   first- D first, then P and Q, a clone in a Start;
//   late-  P and Q (no D) on an inactive clone that is destroyed, then activated before its
//          destruction, in a coroutine.
// With PROBE_SCENARIO=awakedestroy only this probe runs (lines "AD ..."); it ends with an
// AwakeDestroyProbeDone canvas.
using System.Collections;
using UnityEngine;

namespace SemanticsProbe
{
    public class ADPart : MonoBehaviour
    {
        public string Tag;
        bool updated, started;
        public static bool Armed;
        protected void L(string s) => Rec.Log("AD " + Tag + " " + s);
        protected virtual void Awake() { if (Armed) L("Awake enabled=" + enabled); }
        void OnEnable() { if (Armed) L("OnEnable"); }
        void Start() { started = true; L("Start"); }
        void Update() { if (!updated) L("first Update"); updated = true; }
        void OnDisable() { if (Armed) L("OnDisable started=" + started); }
        void OnDestroy() { if (Armed) L("OnDestroy started=" + started); }
    }

    public class ADDestroyer : ADPart
    {
        protected override void Awake()
        {
            base.Awake();
            if (!Armed) return;
            L("Awake: Destroy(gameObject)");
            Destroy(gameObject);
            L("Awake: after Destroy enabled=" + enabled);
        }
    }

    public class ADLoader : MonoBehaviour
    {
        public GameObject Template;
        public string Prefix;
        void Start()
        {
            Rec.Log("AD " + Prefix + " loader Start");
            AwakeDestroy.Activate(Instantiate(Template), Prefix);
            Rec.Log("AD " + Prefix + " loader Start done");
        }
    }

    public class AwakeDestroy : MonoBehaviour
    {
        static GameObject Template(bool destroyerFirst)
        {
            var go = new GameObject("ad-template");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            if (destroyerFirst) go.AddComponent<ADDestroyer>();
            go.AddComponent<ADPart>();
            if (!destroyerFirst) go.AddComponent<ADDestroyer>();
            go.AddComponent<ADPart>();
            var child = new GameObject("ad-child");
            child.transform.SetParent(go.transform, false);
            child.AddComponent<ADPart>();
            return go;
        }

        /// Names the parts of an inactive object, then activates it.
        public static GameObject Activate(GameObject go, string prefix)
        {
            go.name = prefix + "object";
            int i = 0;
            foreach (var p in go.GetComponents<ADPart>())
                p.Tag = prefix + (p is ADDestroyer ? "D" : i++ == 0 ? "P" : "Q");
            go.transform.GetChild(0).GetComponent<ADPart>().Tag = prefix + "C";
            ADPart.Armed = true;
            Rec.Log("AD " + prefix + " SetActive(true)");
            go.SetActive(true);
            Rec.Log("AD " + prefix + " SetActive done, object " + (go == null ? "null" : "alive"));
            return go;
        }

        static IEnumerator Watch(GameObject go, string prefix)
        {
            for (int f = 0; f < 3; f++)
            {
                yield return null;
                Rec.Log("AD " + prefix + " frame +" + (f + 1) + ": object " + (go == null ? "null" : "alive"));
            }
        }

        IEnumerator Start()
        {
            yield return null;
            Rec.Log("=== awakedestroy");

            var go = new GameObject("ad-new");
            go.SetActive(false);
            go.AddComponent<ADPart>();
            go.AddComponent<ADDestroyer>();
            go.AddComponent<ADPart>();
            var child = new GameObject("ad-child");
            child.transform.SetParent(go.transform, false);
            child.AddComponent<ADPart>();
            yield return Watch(Activate(go, "new-"), "new-");

            ADPart.Armed = false;
            var template = Template(false);
            yield return Watch(Activate(Instantiate(template), "clone-"), "clone-");

            ADPart.Armed = false;
            var loader = new GameObject("ad-loader").AddComponent<ADLoader>();
            loader.Template = template;
            loader.Prefix = "start-";
            for (int f = 0; f < 4; f++) yield return null;

            ADPart.Armed = false;
            var first = new GameObject("ad-loader-first").AddComponent<ADLoader>();
            first.Template = Template(true);
            first.Prefix = "first-";
            for (int f = 0; f < 4; f++) yield return null;

            // An inactive object, never active, destroyed and then activated before its
            // destruction: whether its behaviours get Awake.
            ADPart.Armed = false;
            var late = Instantiate(Template(false));
            foreach (var d in late.GetComponents<ADDestroyer>()) DestroyImmediate(d);
            Rec.Log("AD late- Destroy while inactive");
            Destroy(late);
            Activate(late, "late-");
            yield return Watch(late, "late-");

            var done = new GameObject("AwakeDestroyProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            Rec.Log("=== done");
        }
    }
}
