// Destroy called from a Start that a pass of pending Starts runs after the Update pass, lines
// "PROBE ... SP ..." (issue #130). A host whose Start runs in the frame's first pass of pending
// Starts (it is created at the end of the frame before) creates objects there; those Starts then
// run in a later pass of the same frame. As in a save load, where SaveLoader's Start loads the
// colony and the loaded starmap inventories (no Update) destroy themselves in their OnSpawn:
//   A: no Update, Destroy(gameObject) in Start
//   B: no Update, Destroy(another object) in Start
//   C: with Update (started by the Update pass itself), Destroy(gameObject) in Start
// The probe logs the phase each Start ran in, and whether the objects are alive in the probe's
// LateUpdate that frame and in its Update and LateUpdate the next frame. With
// PROBE_SCENARIO=startpassdestroy only this probe runs; it ends with a StartPassDestroyProbeDone
// canvas.
//
// Observed in the original (U60 764961, Linux player), host created at the end of frame 7:
//   f8 host Start (first pass of the frame); Update A=alive B=alive B-other=alive C=alive
//   f8 C Start, A Start, B Start (all after the probe's Update); C OnDestroy (before LateUpdate)
//   f8 LateUpdate A=alive B=alive B-other=alive C=null
//   f8 A OnDestroy (after LateUpdate)
//   f9 Update A=null B=alive B-other=null C=null
// A Destroy from a Start of the pass after Update is carried out after LateUpdate; one from a
// Start the Update pass ran itself (C, which has Update) when the Update pass ends.
using System.Collections;
using UnityEngine;

namespace SemanticsProbe
{
    public class SpVictim : MonoBehaviour
    {
        public string Tag;
        public GameObject Target;
        void Start()
        {
            Rec.Log("SP " + Tag + " Start (" + StartPassDestroy.Phase + ")");
            Destroy(Target != null ? Target : gameObject);
        }
        void OnDestroy() => Rec.Log("SP " + Tag + " OnDestroy (" + StartPassDestroy.Phase + ")");
    }

    public class SpVictimWithUpdate : SpVictim
    {
        void Update() { }
    }

    public class SpHost : MonoBehaviour
    {
        void Start()
        {
            Rec.Log("SP host Start (" + StartPassDestroy.Phase + ")");
            var p = StartPassDestroy.Instance;
            p.A = new GameObject("SP-A");
            p.A.AddComponent<SpVictim>().Tag = "A";
            p.BOther = new GameObject("SP-B-other");
            p.B = new GameObject("SP-B");
            var b = p.B.AddComponent<SpVictim>();
            b.Tag = "B";
            b.Target = p.BOther;
            p.C = new GameObject("SP-C");
            p.C.AddComponent<SpVictimWithUpdate>().Tag = "C";
        }
    }

    public class StartPassDestroy : MonoBehaviour
    {
        public static StartPassDestroy Instance;
        public static string Phase = "?";
        public GameObject A, B, BOther, C;
        int watch;

        static void L(string s) => Rec.Log("SP " + s);
        static string Alive(GameObject g) => g == null ? "null" : "alive";
        string State() => "A=" + Alive(A) + " B=" + Alive(B) + " B-other=" + Alive(BOther) + " C=" + Alive(C);

        void Awake() => Instance = this;

        void Update()
        {
            Phase = "after Update f" + Time.frameCount;
            if (watch > 0) L("Update " + State());
        }

        void LateUpdate()
        {
            Phase = "after LateUpdate f" + Time.frameCount;
            if (watch > 0) { L("LateUpdate " + State()); watch--; }
        }

        IEnumerator Start()
        {
            for (int i = 0; i < 5; i++) yield return null;
            // No fixed steps meanwhile: a pass of pending Starts follows each, and would start
            // the objects before the Update pass.
            float step = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 10f;
            yield return new WaitForEndOfFrame();
            Phase = "end of frame f" + Time.frameCount;
            L("=== host created at the end of frame " + Time.frameCount);
            new GameObject("SP-host").AddComponent<SpHost>();
            watch = 3;
            for (int i = 0; i < 6; i++) yield return null;
            Time.fixedDeltaTime = step;
            yield return new WaitForEndOfFrame();
            Phase = "start of frame";
            var done = new GameObject("StartPassDestroyProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("done");
        }
    }
}
