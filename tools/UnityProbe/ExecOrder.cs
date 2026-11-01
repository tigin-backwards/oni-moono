// [DefaultExecutionOrder] on MonoBehaviours of an assembly loaded at runtime (a mod, so no
// MonoScript asset carries an m_ExecutionOrder): whether the player orders their messages by it.
// Found with Radbolt Demolisher (Workshop 3768016876), whose tracker is marked
// [DefaultExecutionOrder(-1000)].
//
// Types: EoNeg [DefaultExecutionOrder(-1000)], EoPos [DefaultExecutionOrder(1000)], EoPlain
// (none), EoNegChild (derives from EoNeg, no attribute of its own). Cases, in one frame each:
//   objs-  separate active objects made in the order pos1, plain1, neg1, child1, then neg2,
//          plain2, pos2, child2;
//   one-   one inactive object with pos, plain, neg and child added in that order, then
//          SetActive(true).
// Each logs its Awake, OnEnable and Start as they happen, and per frame (end of frame to end of
// frame) the order of every Update, LateUpdate and FixedUpdate ("order" lines). Ordered by the
// attribute, neg and child come first and pos last in each. With PROBE_SCENARIO=execorder only
// this probe runs (lines "EO ..."); it ends with an ExecOrderProbeDone canvas.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SemanticsProbe
{
    public class EoBase : MonoBehaviour
    {
        public string Tag = "?";
        void Awake() { if (ExecOrder.Armed) ExecOrder.Note("Awake " + Tag); }
        void OnEnable() { if (ExecOrder.Armed) ExecOrder.Note("OnEnable " + Tag); }
        void Start() { if (ExecOrder.Armed) ExecOrder.Note("Start " + Tag); }
        void Update() { if (ExecOrder.Armed) ExecOrder.Note("U " + Tag); }
        void LateUpdate() { if (ExecOrder.Armed) ExecOrder.Note("L " + Tag); }
        void FixedUpdate() { if (ExecOrder.Armed) ExecOrder.Note("F " + Tag); }
    }

    [DefaultExecutionOrder(-1000)]
    public class EoNeg : EoBase { }

    public class EoNegChild : EoNeg { }

    [DefaultExecutionOrder(1000)]
    public class EoPos : EoBase { }

    public class EoPlain : EoBase { }

    public class ExecOrder : MonoBehaviour
    {
        public static bool Armed;
        static readonly List<string> calls = new List<string>();
        public static void Note(string s) => calls.Add(s);
        static void L(string s) => Rec.Log("EO " + s);

        static void Flush(string label)
        {
            L(label + " order " + string.Join(", ", calls));
            calls.Clear();
        }

        static readonly List<GameObject> made = new List<GameObject>();

        static T Make<T>(string tag) where T : EoBase
        {
            var go = new GameObject("eo-" + tag);
            made.Add(go);
            DontDestroyOnLoad(go);
            go.SetActive(false);
            var c = go.AddComponent<T>();
            c.Tag = tag;
            go.SetActive(true);
            return c;
        }

        static void Add<T>(GameObject go, string tag) where T : EoBase => go.AddComponent<T>().Tag = tag;

        IEnumerator Frames(string label, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForEndOfFrame();
                Flush(label + " frame +" + (i + 1));
            }
        }

        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2f);
            L("begin");
            foreach (var t in new[] { typeof(EoNeg), typeof(EoNegChild), typeof(EoPos), typeof(EoPlain) })
            {
                var a = (DefaultExecutionOrder)System.Attribute.GetCustomAttribute(t, typeof(DefaultExecutionOrder));
                L("attr " + t.Name + " " + (a == null ? "none" : a.order.ToString()));
            }

            yield return new WaitForEndOfFrame();
            calls.Clear();
            Armed = true;
            Make<EoPos>("pos1"); Make<EoPlain>("plain1"); Make<EoNeg>("neg1"); Make<EoNegChild>("child1");
            Make<EoNeg>("neg2"); Make<EoPlain>("plain2"); Make<EoPos>("pos2"); Make<EoNegChild>("child2");
            Flush("objs made");
            yield return Frames("objs", 3);
            Armed = false;
            foreach (var go in made) Destroy(go);
            yield return null;
            yield return new WaitForEndOfFrame();

            calls.Clear();
            Armed = true;
            var one = new GameObject("eo-one");
            DontDestroyOnLoad(one);
            one.SetActive(false);
            Add<EoPos>(one, "pos"); Add<EoPlain>(one, "plain"); Add<EoNeg>(one, "neg"); Add<EoNegChild>(one, "child");
            one.SetActive(true);
            Flush("one made");
            yield return Frames("one", 3);
            Armed = false;

            var done = new GameObject("ExecOrderProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("done");
        }
    }
}
