using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    public class Recorder : MonoBehaviour
    {
        public static readonly List<string> Log = new();
        public string label = "";
        void Awake() => Log.Add(label + ":Awake");
        void OnEnable() => Log.Add(label + ":OnEnable");
        void Start() => Log.Add(label + ":Start");
        void Update() => Log.Add(label + ":Update");
        void OnDisable() => Log.Add(label + ":OnDisable");
        void OnDestroy() => Log.Add(label + ":OnDestroy");
    }

    /// Looks up a sibling from OnDestroy, like the game's SaveLoadRoot (KPrefabID).
    public class SiblingChecker : MonoBehaviour
    {
        public static bool SawSibling;
        void OnDestroy() => SawSibling = GetComponent<Recorder>() != null && GetComponentInChildren<Base>(true) != null;
    }

    /// Records whether another object is still alive in its own OnDisable and OnDestroy.
    public class PeerWatcher : MonoBehaviour
    {
        public string label = "";
        public Object peer;
        void OnDisable() => Recorder.Log.Add(label + ":OnDisable peer " + (peer == null ? "gone" : "alive"));
        void OnDestroy() => Recorder.Log.Add(label + ":OnDestroy peer " + (peer == null ? "gone" : "alive"));
    }

    /// Records its callbacks as Recorder does, and in Awake whether it is disabled.
    public class AwakeRecorder : MonoBehaviour
    {
        public string label = "";
        protected virtual void Awake() => Recorder.Log.Add(label + ":Awake" + (enabled ? "" : " enabled=False"));
        void OnEnable() => Recorder.Log.Add(label + ":OnEnable");
        void Start() => Recorder.Log.Add(label + ":Start");
        void Update() => Recorder.Log.Add(label + ":Update");
        void OnDisable() => Recorder.Log.Add(label + ":OnDisable");
        void OnDestroy() => Recorder.Log.Add(label + ":OnDestroy");
    }

    /// Destroys its GameObject from its Awake while Armed, as the game's PlantBranch does (from
    /// KPrefabID's Awake) for a branch whose cell is taken.
    public class AwakeDestroyer : AwakeRecorder
    {
        public static bool Armed;
        protected override void Awake()
        {
            base.Awake();
            if (!Armed) return;
            Destroy(gameObject);
            Recorder.Log.Add(label + ":after Destroy enabled=" + enabled);
        }
    }

    public class LateRecorder : MonoBehaviour
    {
        void Start() => Recorder.Log.Add("late:Start");
        void LateUpdate() => Recorder.Log.Add("late:LateUpdate");
    }

    /// Instantiates a LateRecorder from its Update, once.
    public class Spawner : MonoBehaviour
    {
        bool done;
        void Update()
        {
            if (done) return;
            done = true;
            new GameObject("spawned").AddComponent<LateRecorder>();
            Recorder.Log.Add("spawned");
        }
    }

    /// A Klei-style tag: only the name is serialized; the hash is rebuilt after deserialization.
    [System.Serializable]
    public struct HashedName : ISerializationCallbackReceiver
    {
        [SerializeField] string name;
        int hash;
        public HashedName(string n) { name = n; hash = n.GetHashCode(); }
        public int Hash => hash;
        public void OnBeforeSerialize() { }
        public void OnAfterDeserialize() => hash = name?.GetHashCode() ?? 0;
    }

    public class HashedNameHolder : MonoBehaviour
    {
        public HashedName single;
        public System.Collections.Generic.List<HashedName> more = new();
    }

    /// Awakes before default-order scripts (script execution order -5, set by the test).
    public class EarlyRecorder : MonoBehaviour
    {
        void Awake() => Recorder.Log.Add("early:Awake");
    }

    /// Creates another object from its Awake, like the game's Game (GameScreenManager) and
    /// SpawnScreen (the new-colony screen).
    public class AwakeCreator : MonoBehaviour
    {
        void Awake()
        {
            Recorder.Log.Add("outer:Awake");
            new GameObject("inner", typeof(InnerStarter));
        }

        void OnEnable() => Recorder.Log.Add("outer:OnEnable");
        void Start() => Recorder.Log.Add("outer:Start");
    }

    public class InnerStarter : MonoBehaviour
    {
        void Awake() => Recorder.Log.Add("inner:Awake");
        void OnEnable() => Recorder.Log.Add("inner:OnEnable");
        void Start() => Recorder.Log.Add("inner:Start");
    }

    public class LateStarter : MonoBehaviour
    {
        void Start() => Recorder.Log.Add("late:Start");
    }

    public class EarlyStarter : MonoBehaviour
    {
        void Start() => Recorder.Log.Add("early:Start");
    }

    public class Base : MonoBehaviour
    {
        public static readonly List<string> Log = new();
        protected void Awake() => Log.Add("Base.Awake");
    }

    public class Derived : Base
    {
        new void Awake() => Log.Add("Derived.Awake");
    }

    public class LifecycleTests : EngineTest
    {
        public LifecycleTests() => Recorder.Log.Clear();

        [Fact]
        public void AddComponentAwakensThenStartsBeforeFirstUpdate()
        {
            var go = new GameObject("a");
            var r = go.AddComponent<Recorder>();
            Assert.Equal(["Awake", "OnEnable"], Recorder.Log.ConvertAll(s => s.Split(':')[1]));
            Frame();
            Assert.Equal(["Awake", "OnEnable", "Start", "Update"], Recorder.Log.ConvertAll(s => s.Split(':')[1]));
        }

        [Fact]
        public void OnlyTheMostDerivedMessageRuns()
        {
            Base.Log.Clear();
            new GameObject("d").AddComponent<Derived>();
            Assert.Equal(["Derived.Awake"], Base.Log);
        }

        [Fact]
        public void InactiveObjectsAreNotAwakenedUntilActivated()
        {
            var go = new GameObject("a");
            go.SetActive(false);
            go.AddComponent<Recorder>();
            Assert.Empty(Recorder.Log);
            go.SetActive(true);
            Assert.Equal([":Awake", ":OnEnable"], Recorder.Log);
            go.SetActive(false);
            Assert.Equal(":OnDisable", Recorder.Log[^1]);
        }

        [Fact]
        public void DisabledBehaviourIsAwakenedButNotEnabled()
        {
            var go = new GameObject("a");
            go.SetActive(false);
            var r = go.AddComponent<Recorder>();
            r.enabled = false;
            go.SetActive(true);
            Assert.Equal([":Awake"], Recorder.Log);
            Frame();
            Assert.Equal([":Awake"], Recorder.Log); // no Start or Update while disabled
            r.enabled = true;
            Frame();
            Assert.Equal([":Awake", ":OnEnable", ":Start", ":Update"], Recorder.Log);
        }

        [Fact]
        public void ChildActivationFollowsParent()
        {
            var parent = new GameObject("p");
            var child = new GameObject("c");
            child.transform.SetParent(parent.transform);
            child.AddComponent<Recorder>().label = "c";
            parent.SetActive(false);
            Assert.False(child.activeInHierarchy);
            Assert.True(child.activeSelf);
            Assert.Equal("c:OnDisable", Recorder.Log[^1]);
            parent.SetActive(true);
            Assert.Equal("c:OnEnable", Recorder.Log[^1]);
        }

        [Fact]
        public void DestroyIsDeferredAndFakeNull()
        {
            var go = new GameObject("a");
            go.AddComponent<Recorder>();
            Object.Destroy(go);
            Assert.True(go != null); // still alive until the next destruction point
            Frame();
            Assert.True(go == null);
            Assert.False(go);
            Assert.Equal(0, go.GetInstanceID());
            Assert.Contains(":OnDestroy", Recorder.Log);
        }

        // Destroy's timing as observed in Unity's player (a probe mod in the original game):
        // behaviours are disabled at the call, destroyed at the next destruction point in request
        // order.

        [Fact]
        public void DestroyDisablesTheObjectAndItsChildrenAtOnceAndDeeperOnesAtDestruction()
        {
            Recorder Make(string label, Transform parent)
            {
                var go = new GameObject(label);
                go.transform.SetParent(parent);
                var r = go.AddComponent<Recorder>();
                r.label = label;
                return r;
            }
            var d0 = Make("d0", null);
            var d1 = Make("d1", d0.transform);
            var d2 = Make("d2", d1.transform);
            var d3 = Make("d3", d2.transform);
            Recorder.Log.Clear();
            Object.Destroy(d0.gameObject);
            Assert.Equal(["d0:OnDisable", "d1:OnDisable"], Recorder.Log);
            Assert.True(d0.gameObject.activeSelf);
            Assert.True(d1.gameObject.activeInHierarchy);
            Assert.False(d0.enabled);
            Assert.False(d1.isActiveAndEnabled);
            Assert.True(d3.isActiveAndEnabled);
            Frame();
            // d2 and d3 stay enabled for the rest of the frame (Start, Update).
            Assert.Equal(["d0:OnDisable", "d1:OnDisable", "d3:OnDisable", "d2:OnDisable", "d0:OnDestroy", "d1:OnDestroy", "d2:OnDestroy", "d3:OnDestroy"],
                Recorder.Log.FindAll(e => e.EndsWith(":OnDisable") || e.EndsWith(":OnDestroy")));
        }

        /// Logs the active flags its OnDisable and OnDestroy see.
        public class FlagRecorder : MonoBehaviour
        {
            public string label = "";
            string Flags() => (gameObject.activeSelf ? "self" : "-") + "/" + (gameObject.activeInHierarchy ? "hier" : "-");
            void OnDisable() => Recorder.Log.Add(label + ":OnDisable " + Flags());
            void OnDestroy() => Recorder.Log.Add(label + ":OnDestroy " + Flags());
        }

        // UnityProbe's destroyflags scenario in the original player: the GameObject Destroy or
        // DestroyImmediate names turns inactive itself before its subtree's OnDisable and
        // OnDestroy at destruction; descendants keep activeSelf; a component destroyed alone
        // leaves its object active. KMonoBehaviour.OnDestroy releases a KObject's event system on
        // `!gameObject.activeSelf`: left alive, a destroyed RocketControlStation's tag handler
        // threw at a rocket's launch.

        static FlagRecorder MakeFlags(string label, Transform parent)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent);
            var r = go.AddComponent<FlagRecorder>();
            r.label = label;
            return r;
        }

        [Fact]
        public void DestroyedObjectIsInactiveInItsOwnOnDestroy()
        {
            var a = MakeFlags("A", null);
            var ac = MakeFlags("AC", a.transform);
            MakeFlags("ACC", ac.transform);
            Frame();
            Recorder.Log.Clear();
            Object.Destroy(a.gameObject);
            Assert.True(a.gameObject.activeSelf); // until destruction
            Frame();
            Assert.Equal(["A:OnDisable self/hier", "AC:OnDisable self/hier", "ACC:OnDisable self/-",
                          "A:OnDestroy -/-", "AC:OnDestroy self/-", "ACC:OnDestroy self/-"], Recorder.Log);
        }

        [Fact]
        public void DestroyImmediateDeactivatesTheObjectBeforeItsOnDisable()
        {
            var c = MakeFlags("C", null);
            MakeFlags("CC", c.transform);
            Frame();
            Recorder.Log.Clear();
            Object.DestroyImmediate(c.gameObject);
            Assert.Equal(["CC:OnDisable self/-", "C:OnDisable -/-", "C:OnDestroy -/-", "CC:OnDestroy self/-"], Recorder.Log);
        }

        [Fact]
        public void DestroyedChildUnderAnInactiveParentIsInactiveInOnDestroy()
        {
            var p = MakeFlags("P", null);
            var e = MakeFlags("E", p.transform);
            var q = MakeFlags("Q", null);
            var qc = MakeFlags("QC", q.transform);
            Frame();
            p.gameObject.SetActive(false);
            Recorder.Log.Clear();
            Object.Destroy(e.gameObject);
            Object.Destroy(qc.gameObject);
            Frame();
            Assert.Equal(["QC:OnDisable self/hier", "E:OnDestroy -/-", "QC:OnDestroy -/-"], Recorder.Log);
            Assert.True(q.gameObject.activeSelf);
        }

        [Fact]
        public void DestroyedComponentLeavesItsObjectActive()
        {
            var b = MakeFlags("B", null);
            var go = b.gameObject;
            Frame();
            Recorder.Log.Clear();
            Object.Destroy(b);
            Frame();
            Assert.Equal(["B:OnDisable self/hier", "B:OnDestroy self/hier"], Recorder.Log);
            Assert.True(go.activeSelf);
        }

        [Fact]
        public void ObjectsAwaitingDestructionAreNotEnabledAgain()
        {
            var parent = new GameObject("p");
            var child = new GameObject("x");
            child.transform.SetParent(parent.transform);
            var x = child.AddComponent<Recorder>(); x.label = "x";
            Recorder.Log.Clear();
            Object.Destroy(child);
            parent.SetActive(false);
            parent.SetActive(true);
            x.enabled = true;
            Assert.Equal(["x:OnDisable"], Recorder.Log);
            Assert.False(x.isActiveAndEnabled);
            Frame();
            Assert.Equal(["x:OnDisable", "x:OnDestroy"], Recorder.Log);
        }

        /// Destroy(gameObject) from an Awake while the object activates, as the game's PlantBranch
        /// does from KPrefabID's Awake for a branch whose cell is taken (issue #45). Measured in
        /// the original player (UnityProbe, PROBE_SCENARIO=awakedestroy): the destroying behaviour
        /// and those awake before it get OnDisable at once, the destroying one without an
        /// OnEnable; the behaviours after it and the child's still get Awake, disabled; nothing
        /// gets OnEnable after that or Start, and every one of them gets OnDestroy.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DestroyFromAnAwakeDuringActivationStartsNothing(bool clone)
        {
            var go = new GameObject("o");
            go.SetActive(false);
            go.AddComponent<AwakeRecorder>().label = "P";
            go.AddComponent<AwakeDestroyer>().label = "D";
            go.AddComponent<AwakeRecorder>().label = "Q";
            var child = new GameObject("c");
            child.transform.SetParent(go.transform, false);
            child.AddComponent<AwakeRecorder>().label = "C";
            if (clone) go = Object.Instantiate(go);
            Recorder.Log.Clear();
            AwakeDestroyer.Armed = true;
            try
            {
                go.SetActive(true);
            }
            finally
            {
                AwakeDestroyer.Armed = false;
            }
            Assert.Equal(["P:Awake", "P:OnEnable", "D:Awake", "P:OnDisable", "D:OnDisable", "D:after Destroy enabled=False",
                          "Q:Awake enabled=False", "C:Awake enabled=False"], Recorder.Log);
            Frame();
            Frame();
            Assert.Equal(["P:OnDestroy", "D:OnDestroy", "Q:OnDestroy", "C:OnDestroy"], Recorder.Log.GetRange(8, Recorder.Log.Count - 8));
            Assert.True(go == null);
        }

        [Fact]
        public void DestroyFromTheFirstAwakeStillAwakensTheRest()
        {
            var go = new GameObject("o");
            go.SetActive(false);
            go.AddComponent<AwakeDestroyer>().label = "D";
            go.AddComponent<AwakeRecorder>().label = "P";
            Recorder.Log.Clear();
            AwakeDestroyer.Armed = true;
            try
            {
                go.SetActive(true);
            }
            finally
            {
                AwakeDestroyer.Armed = false;
            }
            Frame();
            Frame();
            Assert.Equal(["D:Awake", "D:OnDisable", "D:after Destroy enabled=False", "P:Awake enabled=False", "D:OnDestroy", "P:OnDestroy"], Recorder.Log);
        }

        /// An object never active, destroyed while inactive and activated before its destruction:
        /// its behaviours get Awake, disabled, and OnDestroy, nothing else (the same probe).
        [Fact]
        public void ActivatingAnObjectDestroyedWhileInactiveOnlyAwakensIt()
        {
            var go = new GameObject("o");
            go.SetActive(false);
            go.AddComponent<AwakeRecorder>().label = "P";
            var child = new GameObject("c");
            child.transform.SetParent(go.transform, false);
            child.AddComponent<AwakeRecorder>().label = "C";
            Recorder.Log.Clear();
            Object.Destroy(go);
            go.SetActive(true);
            Frame();
            Frame();
            Assert.Equal(["P:Awake enabled=False", "C:Awake enabled=False", "P:OnDestroy", "C:OnDestroy"], Recorder.Log);
            Assert.True(go == null);
        }

        [Fact]
        public void DestroyingAComponentDisablesItAtOnce()
        {
            var go = new GameObject("a");
            var r = go.AddComponent<Recorder>();
            Recorder.Log.Clear();
            Object.Destroy(r);
            Assert.Equal([":OnDisable"], Recorder.Log);
            Assert.False(r.enabled);
            Frame();
            Assert.Equal([":OnDisable", ":OnDestroy"], Recorder.Log);
            Assert.True(r == null);
            Assert.True(go != null);
        }

        [Fact]
        public void PendingDestructionsRunInRequestOrderOneAfterAnother()
        {
            var b = new GameObject("b").AddComponent<PeerWatcher>(); b.label = "b";
            var c = new GameObject("c").AddComponent<PeerWatcher>(); c.label = "c";
            b.peer = c; c.peer = b;
            Object.Destroy(b.gameObject);
            Object.Destroy(c.gameObject);
            Frame();
            Assert.Equal(["b:OnDisable peer alive", "c:OnDisable peer alive", "b:OnDestroy peer alive", "c:OnDestroy peer gone"], Recorder.Log);
        }

        [Fact]
        public void DestroyImmediateRunsDisableThenDestroy()
        {
            var go = new GameObject("a");
            go.AddComponent<Recorder>();
            Object.DestroyImmediate(go);
            Assert.Equal([":Awake", ":OnEnable", ":OnDisable", ":OnDestroy"], Recorder.Log);
            Assert.True(go == null);
        }

        [Fact]
        public void GetComponentMatchesInterfacesAndSkipsInactiveChildren()
        {
            var parent = new GameObject("p");
            var child = new GameObject("c", typeof(Recorder));
            child.transform.SetParent(parent.transform);
            Assert.NotNull(parent.GetComponentInChildren<Recorder>());
            child.SetActive(false);
            Assert.Null(parent.GetComponentInChildren<Recorder>());
            Assert.NotNull(parent.GetComponentInChildren<Recorder>(true));
            Assert.NotNull(child.GetComponent<System.IDisposable>() == null ? child.transform : null);
        }

        [Fact]
        public void OnDestroySeesTheWholeSubtreeAlive()
        {
            var go = new GameObject("root", typeof(Recorder), typeof(SiblingChecker));
            var child = new GameObject("child", typeof(Base));
            child.transform.SetParent(go.transform);
            SiblingChecker.SawSibling = false;
            Object.DestroyImmediate(go);
            Assert.True(SiblingChecker.SawSibling);
            Assert.True(go == null);
            Assert.True(child == null);
        }

        [Fact]
        public void BehaviourCreatedDuringUpdateStartsBeforeLateUpdate()
        {
            Recorder.Log.Clear();
            var go = new GameObject("spawner", typeof(Spawner));
            Frame();
            // Unity's delayed-start pass after Update: Start, then the same frame's LateUpdate.
            Assert.Equal(["spawned", "late:Start", "late:LateUpdate"], Recorder.Log);
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(GameObject.Find("spawned"));
        }

        [Fact]
        public void InstantiateCallsCallbacksOnNestedSerializedValues()
        {
            var go = new GameObject("holder");
            var h = go.AddComponent<HashedNameHolder>();
            h.single = new HashedName("Minion");
            h.more.Add(new HashedName("BaseMinion"));
            var copy = Object.Instantiate(go).GetComponent<HashedNameHolder>();
            Assert.Equal(h.single.Hash, copy.single.Hash);
            Assert.Equal(h.more[0].Hash, copy.more[0].Hash);
            Object.DestroyImmediate(copy.gameObject);
            Object.DestroyImmediate(go);
        }

        [Fact]
        public void ActivationAwakesScriptsInExecutionOrder()
        {
            OniMoono.Engine.ScriptInfo.SetExecutionOrder(typeof(EarlyRecorder).FullName!, -5);
            Recorder.Log.Clear();
            var root = new GameObject("root");
            root.SetActive(false);
            var child = new GameObject("child");
            child.transform.SetParent(root.transform);
            child.AddComponent<Recorder>().label = "a";
            child.AddComponent<EarlyRecorder>();
            root.SetActive(true);
            // The later component with the lower order awakes first, across the whole batch.
            Assert.Equal(["early:Awake", "a:Awake", "a:OnEnable"], Recorder.Log);
            Object.DestroyImmediate(root);
        }

        [Fact]
        public void PendingStartsRunInTheOrderTheyWereScheduled()
        {
            // As in the original player loading a save: World (order 6) is enabled before Game
            // (order -10) and starts first; execution order does not reorder pending Starts.
            OniMoono.Engine.ScriptInfo.SetExecutionOrder(typeof(LateStarter).FullName!, 6);
            OniMoono.Engine.ScriptInfo.SetExecutionOrder(typeof(EarlyStarter).FullName!, -10);
            Recorder.Log.Clear();
            var late = new GameObject("late", typeof(LateStarter));
            var early = new GameObject("early", typeof(EarlyStarter));
            OniMoono.Engine.BehaviourManager.RunStarts();
            Assert.Equal(["late:Start", "early:Start"], Recorder.Log);
            Object.DestroyImmediate(late);
            Object.DestroyImmediate(early);
        }

        [Fact]
        public void AnObjectCreatedInAwakeStartsAfterItsCreator()
        {
            // Measured in the original player: Start is queued when activation begins, before
            // Awake, so the outer object starts first although the inner one is enabled first.
            // (Loading a save, Game starts before the new-colony screen its Awake creates and
            // destroys that screen before the screen's Start renames the colony.)
            Recorder.Log.Clear();
            var outer = new GameObject("outer", typeof(AwakeCreator));
            Assert.Equal(["outer:Awake", "inner:Awake", "inner:OnEnable", "outer:OnEnable"], Recorder.Log);
            OniMoono.Engine.BehaviourManager.RunStarts();
            Assert.Equal(["outer:Awake", "inner:Awake", "inner:OnEnable", "outer:OnEnable", "outer:Start", "inner:Start"], Recorder.Log);
            Object.DestroyImmediate(outer);
            Object.DestroyImmediate(GameObject.Find("inner"));
        }

        [Fact]
        public void GameObjectSceneIsTheSceneItBelongsTo()
        {
            var go = new GameObject("in scene");
            var child = new GameObject("child");
            child.transform.SetParent(go.transform);
            Assert.Equal(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), go.scene);
            Assert.Equal(go.scene, child.scene);
            Object.DontDestroyOnLoad(go);
            Assert.Equal("DontDestroyOnLoad", go.scene.name);
            Assert.Equal(go.scene, child.scene);
            Object.DestroyImmediate(go);
        }

        [Fact]
        public void FindObjectOfTypeFindsActiveObjectsUnlessAskedForInactive()
        {
            var active = new GameObject("active", typeof(Recorder));
            var inactive = new GameObject("inactive", typeof(Recorder));
            inactive.SetActive(false);
            Assert.Same(active.GetComponent<Recorder>(), Object.FindObjectOfType<Recorder>());
            Assert.Single(Object.FindObjectsOfType<Recorder>());
            Assert.Equal(2, Object.FindObjectsOfType<Recorder>(includeInactive: true).Length);
            Assert.Same(active.GetComponent<Recorder>(), Object.FindObjectOfType(typeof(Recorder)));
            Object.DestroyImmediate(active);
            Assert.Same(inactive.GetComponent<Recorder>(), Object.FindObjectOfType<Recorder>(includeInactive: true));
            Assert.Null(Object.FindObjectOfType<Recorder>());
            Object.DestroyImmediate(inactive);
        }

        [Fact]
        public void ChildSearchJudgesActivityRelativeToTheStartingObject()
        {
            // root (inactive) → a (active) → b (Recorder); root → c (inactive) → d (Recorder).
            var root = new GameObject("root");
            var a = new GameObject("a");
            a.transform.SetParent(root.transform);
            var b = new GameObject("b", typeof(Recorder));
            b.transform.SetParent(a.transform);
            var c = new GameObject("c");
            c.transform.SetParent(root.transform);
            var d = new GameObject("d", typeof(Recorder));
            d.transform.SetParent(c.transform);
            c.SetActive(false);
            root.SetActive(false);
            Assert.Same(b.GetComponent<Recorder>(), root.GetComponentInChildren<Recorder>());
            Assert.Single(root.GetComponentsInChildren<Recorder>());
            Assert.Equal(2, root.GetComponentsInChildren<Recorder>(true).Length);
            // The starting object is searched even when it is inactive itself.
            Assert.NotNull(d.GetComponentInChildren<Recorder>());
            Object.DestroyImmediate(root);
        }

        [Fact]
        public void SetParentKeepsWorldPosition()
        {
            var parent = new GameObject("p");
            parent.transform.position = new Vector3(10, 0, 0);
            parent.transform.localScale = new Vector3(2, 2, 2);
            var child = new GameObject("c");
            child.transform.position = new Vector3(4, 0, 0);
            child.transform.SetParent(parent.transform, true);
            Assert.Equal(new Vector3(-3, 0, 0), child.transform.localPosition);
            Assert.True(child.transform.position == new Vector3(4, 0, 0));
            Assert.Equal(child.transform, parent.transform.Find("c"));
        }
    }

    public class CoroutineTests : EngineTest
    {
        public class Runner : MonoBehaviour
        {
            public readonly List<string> Log = new();

            public IEnumerator Steps()
            {
                Log.Add("start");
                yield return null;
                Log.Add("after null");
                yield return Nested();
                Log.Add("after nested");
            }

            IEnumerator Nested()
            {
                Log.Add("nested");
                yield return null;
                Log.Add("nested resumed");
            }
        }

        [Fact]
        public void CoroutinesRunSynchronouslyToFirstYieldAndNest()
        {
            var r = new GameObject("r").AddComponent<Runner>();
            r.StartCoroutine(r.Steps());
            Assert.Equal(["start"], r.Log);
            Frame();
            Assert.Equal(["start", "after null", "nested"], r.Log);
            Frame();
            Assert.Equal(["start", "after null", "nested", "nested resumed", "after nested"], r.Log);
        }

        public class UpdateStarter : MonoBehaviour
        {
            public readonly List<string> Log = new();
            bool started;

            void Update()
            {
                Log.Add("update " + Time.frameCount);
                if (started) return;
                started = true;
                StartCoroutine(Co());
            }

            IEnumerator Co()
            {
                Log.Add("co start " + Time.frameCount);
                yield return null;
                Log.Add("co resumed " + Time.frameCount);
            }
        }

        [Fact]
        public void YieldNullFromACoroutineStartedInUpdateResumesNextFrame()
        {
            // Observed in Unity's player (tools/UnityProbe): the coroutine pass after Update does
            // not resume a wait that began earlier in the same frame.
            var r = new GameObject("r").AddComponent<UpdateStarter>();
            Frame();
            int f = Time.frameCount;
            Frame();
            Assert.Equal([$"update {f}", $"co start {f}", $"update {f + 1}", $"co resumed {f + 1}"], r.Log);
        }

        public class CoroutineSpawner : MonoBehaviour
        {
            public IEnumerator Spawn()
            {
                yield return null;
                new GameObject("late").AddComponent<LateRecorder>();
                Recorder.Log.Add("spawned " + Time.frameCount);
            }
        }

        [Fact]
        public void BehavioursCreatedByCoroutinesStartAndLateUpdateThatFrame()
        {
            Recorder.Log.Clear();
            var s = new GameObject("s").AddComponent<CoroutineSpawner>();
            s.StartCoroutine(s.Spawn());
            Frame();
            Assert.Equal([$"spawned {Time.frameCount}", "late:Start", "late:LateUpdate"], Recorder.Log);
        }

        [Fact]
        public void DeactivationStopsCoroutines()
        {
            var r = new GameObject("r").AddComponent<Runner>();
            r.StartCoroutine(r.Steps());
            r.gameObject.SetActive(false);
            r.gameObject.SetActive(true);
            Frame();
            Assert.Equal(["start"], r.Log);
        }

        [Fact]
        public void SharedWaitForSecondsRealtimeInstanceWaits()
        {
            var wait = new WaitForSecondsRealtime(10f);
            Assert.True(wait.keepWaiting);
            Assert.True(wait.MoveNext());
        }

        [Fact]
        public void WaitForSecondsRealtimeRearmsAfterItsDeadline()
        {
            var wait = new WaitForSecondsRealtime(0.05f);
            float start = Time.realtimeSinceStartup;
            Assert.True(wait.keepWaiting);
            while (wait.keepWaiting)
                System.Threading.Thread.Sleep(5);
            Assert.True(Time.realtimeSinceStartup - start >= 0.05f);
            // Passing the deadline reset the instance: the next query starts a new wait.
            Assert.True(wait.keepWaiting);
            wait.waitTime = 0f;
            wait.Reset();
            Assert.False(wait.keepWaiting);
            Assert.Equal(0f, wait.waitTime);
        }

        [Fact]
        public void WaitUntilWaitsForItsPredicate()
        {
            bool done = false;
            var wait = new WaitUntil(() => done);
            Assert.True(wait.keepWaiting);
            done = true;
            Assert.False(wait.keepWaiting);
        }
    }

    /// Destroys a target's BoxCollider and first child in one phase of a frame and records,
    /// phase by phase, whether they are gone (UnityProbe's DestroyPhase).
    public class DestroyPhaseRecorder : MonoBehaviour
    {
        public static readonly List<string> Log = new();
        public string phase;
        public GameObject target;
        bool fired;

        void Note(string at)
        {
            if (fired) Log.Add(at + " " + (target.GetComponent<BoxCollider>() == null ? "gone" : "there") + " " + target.transform.childCount);
            if (fired || at != phase) return;
            fired = true;
            Object.Destroy(target.GetComponent<BoxCollider>());
            Object.Destroy(target.transform.GetChild(0).gameObject);
            Log.Add(at + " destroyed");
        }

        void Start() => Note("Start");
        void Update() => Note("Update");
        void LateUpdate() => Note("LateUpdate");
        void OnEnable()
        {
            StartCoroutine(Resumed());
            StartCoroutine(EndOfFrame());
        }
        IEnumerator Resumed()
        {
            while (true) { yield return null; Note("co"); }
        }
        IEnumerator EndOfFrame()
        {
            while (true) { yield return new WaitForEndOfFrame(); Note("EndOfFrame"); }
        }
    }

    public class DestroyTimingTests : EngineTest
    {
        public DestroyTimingTests() => DestroyPhaseRecorder.Log.Clear();

        static List<string> Run(string phase)
        {
            var target = new GameObject("Target");
            target.AddComponent<BoxCollider>();
            new GameObject("Child").transform.SetParent(target.transform, false);
            var d = new GameObject("Destroyer");
            d.SetActive(false);
            var r = d.AddComponent<DestroyPhaseRecorder>();
            r.phase = phase; r.target = target;
            d.SetActive(true);
            // No fixed step, which would carry the requests out before Update.
            float fixedStep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 100f;
            try
            {
                for (int i = 0; i < 3; i++) Frame();
            }
            finally
            {
                Time.fixedDeltaTime = fixedStep;
            }
            return DestroyPhaseRecorder.Log.GetRange(0, 5);
        }

        // Where the player carries out Destroy, as measured in the original player (UnityProbe,
        // "values: destroy phases"): after the Update pass, after LateUpdate, and for calls made
        // later in the frame, after the next frame's Update pass.

        [Fact]
        public void DestroyInStartIsCarriedOutAfterTheUpdatePass()
            => Assert.Equal(["Start destroyed", "Update there 1", "co gone 0", "LateUpdate gone 0", "EndOfFrame gone 0"], Run("Start"));

        [Fact]
        public void DestroyInUpdateIsCarriedOutBeforeCoroutinesAndLateUpdate()
            => Assert.Equal(["Update destroyed", "co gone 0", "LateUpdate gone 0", "EndOfFrame gone 0", "Update gone 0"], Run("Update"));

        [Fact]
        public void DestroyInACoroutineIsCarriedOutAfterLateUpdate()
            => Assert.Equal(["co destroyed", "LateUpdate there 1", "EndOfFrame gone 0", "Update gone 0", "co gone 0"], Run("co"));

        [Fact]
        public void DestroyInLateUpdateIsCarriedOutBeforeTheEndOfTheFrame()
            => Assert.Equal(["LateUpdate destroyed", "EndOfFrame gone 0", "Update gone 0", "co gone 0", "LateUpdate gone 0"], Run("LateUpdate"));

        [Fact]
        public void DestroyAtTheEndOfTheFrameWaitsForTheNextUpdatePass()
            => Assert.Equal(["EndOfFrame destroyed", "Update there 1", "co gone 0", "LateUpdate gone 0", "EndOfFrame gone 0"], Run("EndOfFrame"));

        /// Better Automation Overlay strips a label template in Update (the overlay toggle's click)
        /// and the pool clones it in LateUpdate (the overlay's update): the clone lacks the parts.
        public class StripInUpdateCloneInLateUpdate : MonoBehaviour
        {
            public GameObject template, clone;
            void Update()
            {
                if (template != null) return;
                template = new GameObject("Template");
                template.AddComponent<BoxCollider>();
                template.AddComponent<Recorder>();
                new GameObject("Unit").transform.SetParent(template.transform, false);
                Object.Destroy(template.GetComponent<BoxCollider>());
                Object.Destroy(template.GetComponent<Recorder>());
                Object.Destroy(template.transform.GetChild(0).gameObject);
            }
            void LateUpdate()
            {
                if (clone == null) clone = Object.Instantiate(template);
            }
        }

        [Fact]
        public void ATemplateStrippedInUpdateIsClonedInLateUpdateWithoutTheParts()
        {
            var s = new GameObject("S").AddComponent<StripInUpdateCloneInLateUpdate>();
            Frame();
            Assert.NotNull(s.clone);
            Assert.Null(s.clone.GetComponent<BoxCollider>());
            Assert.Null(s.clone.GetComponent<Recorder>());
            Assert.Equal(0, s.clone.transform.childCount);
        }
    }

    /// An owner whose OnDestroy destroys another object (with a child), as
    /// DiseaseSourceVisualizer.OnCleanUp destroys its ResourceVisualizer. Logs each phase's view
    /// of the other object and the OnDestroy calls (UnityProbe's destroycascade).
    public class CascadeRecorder : MonoBehaviour
    {
        public static readonly List<string> Log = new();
        public static GameObject Other;
        public string Tag;
        public GameObject DestroyOnDestroy;
        public string DestroyOwnerIn;
        public GameObject Owner;
        int frames;

        void Watch(string at)
        {
            if (DestroyOwnerIn == null) return;
            if (frames > 0 && frames < 3) Log.Add($"{at} other={(Other == null ? "null" : "alive")}");
            if (at == DestroyOwnerIn && frames == 0)
            {
                Object.Destroy(Owner);
                Log.Add(at + " destroyed the owner");
                frames = 1;
            }
        }

        void FixedUpdate() => Watch("FixedUpdate");
        void Update() { Watch("Update"); }
        void LateUpdate() { Watch("LateUpdate"); if (frames > 0) frames++; }

        void OnDestroy()
        {
            if (Tag == null) return;
            Log.Add(Tag + " OnDestroy");
            if (DestroyOnDestroy != null) Object.Destroy(DestroyOnDestroy);
        }
    }

    public class DestroyCascadeTests : EngineTest
    {
        public DestroyCascadeTests() => CascadeRecorder.Log.Clear();

        /// The owner destroyed in PHASE of the first frame (without fixed steps); the second
        /// frame has fixed steps if FIXED_NEXT.
        static List<string> Run(string phase, bool fixedNext)
        {
            var other = new GameObject("Other");
            other.AddComponent<CascadeRecorder>().Tag = "other";
            var child = new GameObject("Child");
            child.transform.SetParent(other.transform, false);
            child.AddComponent<CascadeRecorder>().Tag = "child";
            CascadeRecorder.Other = other;
            var owner = new GameObject("Owner");
            var o = owner.AddComponent<CascadeRecorder>();
            o.Tag = "owner"; o.DestroyOnDestroy = other;
            var w = new GameObject("Watcher").AddComponent<CascadeRecorder>();
            w.DestroyOwnerIn = phase; w.Owner = owner;
            float step = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 100f;
            try
            {
                Frame();
                if (fixedNext) Time.fixedDeltaTime = 1f / 120f;
                Frame();
                Time.fixedDeltaTime = 100f;
                Frame();
            }
            finally
            {
                Time.fixedDeltaTime = step;
            }
            return CascadeRecorder.Log;
        }

        // As measured in the original player (UnityProbe destroycascade): a Destroy made by an
        // OnDestroy that a destruction point runs waits for the next destruction point.

        [Fact]
        public void ADestroyFromOnDestroyAfterTheUpdatePassIsCarriedOutAfterLateUpdate()
            => Assert.Equal(["Update destroyed the owner", "owner OnDestroy", "LateUpdate other=alive", "other OnDestroy", "child OnDestroy", "Update other=null"],
                Run("Update", false).GetRange(0, 6));

        [Fact]
        public void ADestroyFromOnDestroyAfterLateUpdateWaitsForTheNextUpdatePass()
            => Assert.Equal(["LateUpdate destroyed the owner", "owner OnDestroy", "Update other=alive", "other OnDestroy", "child OnDestroy", "LateUpdate other=null"],
                Run("LateUpdate", false).GetRange(0, 6));

        [Fact]
        public void ADestroyFromOnDestroyAfterLateUpdateIsCarriedOutAfterTheNextFixedStep()
            => Assert.Equal(["LateUpdate destroyed the owner", "owner OnDestroy", "FixedUpdate other=alive", "other OnDestroy", "child OnDestroy", "FixedUpdate other=null"],
                Run("LateUpdate", true).GetRange(0, 6));
    }

    public class CloneTests : EngineTest
    {
        [System.Serializable]
        public class Settings
        {
            public int value = 1;
            public List<string> names;
        }

        public class Holder : MonoBehaviour
        {
            public int number;
            [SerializeField] private string secret;
            public Settings settings;
            public Settings nullSettings;
            public GameObject child;
            public GameObject outside;
            [System.NonSerialized] public int transientValue = 7;
            public int notSerializedProperty { get; set; }
            public string Secret { get => secret; set => secret = value; }
        }

        [Fact]
        public void InstantiateCopiesSerializedStateAndRemapsInternalReferences()
        {
            var outside = new GameObject("outside");
            var root = new GameObject("root");
            var child = new GameObject("child");
            child.transform.SetParent(root.transform);
            var h = root.AddComponent<Holder>();
            h.number = 5;
            h.Secret = "s";
            h.settings = new Settings { value = 9, names = new List<string> { "x" } };
            h.child = child;
            h.outside = outside;
            h.transientValue = 99;
            h.notSerializedProperty = 3;

            var clone = Object.Instantiate(h);
            Assert.NotSame(h, clone);
            Assert.Equal("root(Clone)", clone.name);
            Assert.Equal(5, clone.number);
            Assert.Equal("s", clone.Secret);
            Assert.NotSame(h.settings, clone.settings);
            Assert.Equal(9, clone.settings.value);
            Assert.Equal(["x"], clone.settings.names);
            Assert.NotNull(clone.nullSettings);            // Unity serialization has no null for classes
            Assert.Equal(1, clone.nullSettings.value);
            Assert.NotSame(child, clone.child);
            Assert.Equal(clone.transform.GetChild(0).gameObject, clone.child);
            Assert.Same(outside, clone.outside);
            Assert.Equal(7, clone.transientValue);         // initializer value, not copied
            Assert.Equal(0, clone.notSerializedProperty);
        }

        public class ValueHolder : MonoBehaviour
        {
            public int[] numbers;
            public int[] nullNumbers;
            public List<float> weights;
            public List<string> nullNames;
            public KeyCode key;
            public Vector3 offset;
            public List<GameObject> objects;
        }

        [Fact]
        public void InstantiateCopiesCollectionsOfValuesAndRemapsReferencesInCollections()
        {
            var root = new GameObject("root");
            var child = new GameObject("child");
            child.transform.SetParent(root.transform);
            var outside = new GameObject("outside");
            var h = root.AddComponent<ValueHolder>();
            h.numbers = [1, 2, 3];
            h.weights = new List<float> { 0.5f };
            h.key = KeyCode.Space;
            h.offset = new Vector3(1, 2, 3);
            h.objects = new List<GameObject> { child, outside };

            var clone = Object.Instantiate(h);
            Assert.Equal([1, 2, 3], clone.numbers);
            Assert.NotSame(h.numbers, clone.numbers);
            Assert.Empty(clone.nullNumbers);                // no null collections after serialization
            Assert.Equal([0.5f], clone.weights);
            Assert.NotSame(h.weights, clone.weights);
            Assert.Empty(clone.nullNames);
            Assert.Equal(KeyCode.Space, clone.key);
            Assert.Equal(new Vector3(1, 2, 3), clone.offset);
            Assert.Equal(clone.transform.GetChild(0).gameObject, clone.objects[0]);
            Assert.Same(outside, clone.objects[1]);
        }

        [Fact]
        public void InstantiateUnderParentWithLocalValues()
        {
            var parent = new GameObject("p");
            parent.transform.position = new Vector3(100, 0, 0);
            var prefab = new GameObject("prefab");
            prefab.transform.localPosition = new Vector3(1, 2, 3);
            var clone = Object.Instantiate(prefab, parent.transform, false);
            Assert.Equal(parent.transform, clone.transform.parent);
            Assert.Equal(new Vector3(1, 2, 3), clone.transform.localPosition);
        }
    
        [Fact]
        public void ADestroyedComponentThrowsNullReferenceAsInAPlayer()
        {
            // Measured in the original player: GetComponent on a destroyed component (the
            // game's KCanvasScaler after a resize) throws NullReferenceException.
            var go = new GameObject("Destroyed");
            var c = go.AddComponent<BoxCollider>();
            Object.DestroyImmediate(go);
            Assert.Throws<System.NullReferenceException>(() => c.GetComponent<Transform>());
        }

        public class ChildWatcher : MonoBehaviour
        {
            public int Count;
            void OnTransformChildrenChanged() => Count++;
        }

        [Fact]
        public void EveryChangeOfTheChildListIsSent()
        {
            // Measured in the original player (PerfProbe measure.txt): Instantiate under a parent,
            // Destroy (when the child goes, by the next frame), DestroyImmediate and SetParent each
            // send OnTransformChildrenChanged to the parent once.
            var parent = new GameObject("Parent");
            var watcher = parent.AddComponent<ChildWatcher>();
            var prefab = new GameObject("Prefab");
            var a = Object.Instantiate(prefab, parent.transform);
            Assert.Equal(1, watcher.Count);
            Object.Instantiate(prefab, Vector3.zero, Quaternion.identity, parent.transform);
            Assert.Equal(2, watcher.Count);
            Object.Destroy(a);
            Assert.Equal(2, watcher.Count);
            Frame();
            Assert.Equal(3, watcher.Count);
            var b = new GameObject("B");
            b.transform.SetParent(parent.transform);
            Assert.Equal(4, watcher.Count);
            Object.DestroyImmediate(b);
            Assert.Equal(5, watcher.Count);
            Object.Instantiate(prefab);
            Assert.Equal(5, watcher.Count);
        }

        // Instantiate of a source whose parts await Destroy, as measured in the original player
        // (UnityProbe, "values: clone pending destroy").

        static GameObject StrippedSource(bool active)
        {
            var go = new GameObject("Src");
            go.SetActive(active);
            go.AddComponent<Recorder>();
            go.AddComponent<BoxCollider>();
            go.AddComponent<LateRecorder>();
            new GameObject("Keep").transform.SetParent(go.transform, false);
            var gone = new GameObject("Gone");
            gone.transform.SetParent(go.transform, false);
            gone.AddComponent<Recorder>();
            Object.Destroy(go.GetComponent<Recorder>());
            Object.Destroy(go.GetComponent<BoxCollider>());
            Object.Destroy(gone);
            return go;
        }

        static string Parts(GameObject go)
        {
            var parts = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                parts.Add(c.GetType().Name + (c is Behaviour b ? (b.enabled ? "+" : "-") : ""));
            for (int i = 0; i < go.transform.childCount; i++) parts.Add("/" + go.transform.GetChild(i).name);
            return string.Join(" ", parts);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AClonePendingDestroyPartsKeepsThemDisabled(bool active)
        {
            var src = StrippedSource(active);
            // The source still shows its pending parts until they are destroyed.
            Assert.Equal("Transform Recorder- BoxCollider LateRecorder+ /Keep /Gone", Parts(src));
            Assert.NotNull(src.GetComponent<Recorder>());
            Assert.Equal(2, src.transform.childCount);
            // A clone made now copies them, the behaviours disabled; Instantiate(Component) and a
            // clone of that clone alike.
            var clone = Object.Instantiate(src);
            var byComponent = Object.Instantiate(src.GetComponent<LateRecorder>()).gameObject;
            var ofClone = Object.Instantiate(clone);
            foreach (var go in new[] { clone, byComponent, ofClone })
            {
                Assert.Equal("Transform Recorder- BoxCollider LateRecorder+ /Keep /Gone", Parts(go));
                Assert.Equal(active, go.activeSelf);
            }
            Assert.False(clone.transform.Find("Gone").GetComponent<Recorder>().enabled);
            Frame();
            // The source has lost them; the clones keep theirs, still disabled.
            Assert.Equal("Transform LateRecorder+ /Keep", Parts(src));
            Assert.Equal("Transform Recorder- BoxCollider LateRecorder+ /Keep /Gone", Parts(clone));
            Assert.Equal("Transform Recorder- BoxCollider LateRecorder+ /Keep /Gone", Parts(ofClone));
            // A clone made after the destruction has none of them.
            Assert.Equal("Transform LateRecorder+ /Keep", Parts(Object.Instantiate(src)));
            Assert.Equal("Transform Recorder- BoxCollider LateRecorder+ /Keep /Gone", Parts(Object.Instantiate(clone)));
        }

        [Fact]
        public void ACloneOfAnObjectAwaitingDestroyLivesOnDisabled()
        {
            var whole = new GameObject("Whole");
            whole.AddComponent<Recorder>();
            whole.AddComponent<BoxCollider>();
            Object.Destroy(whole);
            var clone = Object.Instantiate(whole);
            Assert.Equal("Transform Recorder- BoxCollider", Parts(clone));
            Frame();
            Assert.True(whole == null);
            Assert.True(clone != null);
            Assert.Equal("Transform Recorder- BoxCollider", Parts(clone));
        }

        /// Logs, in its OnDisable, the activeInHierarchy of every object of the probe's hierarchy,
        /// and runs `onDisable` then.
        public class HierarchySpy : MonoBehaviour
        {
            public static readonly Dictionary<string, GameObject> Objects = new();
            public System.Action onDisable;
            void OnDisable()
            {
                if (!Objects.ContainsKey("C")) return;
                var flags = new List<string>();
                foreach (var kv in Objects) flags.Add(kv.Key + (kv.Value.activeInHierarchy ? "+" : "-"));
                Recorder.Log.Add(name + ":OnDisable " + string.Join(" ", flags));
                onDisable?.Invoke();
            }
        }

        // UnityProbe's deactorder scenario in the original player: P > A, B (> B1, B2), C. An
        // object turns inactive when the walk reaches it, then its children are walked, then its
        // own OnDisables run.
        static GameObject DeactivationTree()
        {
            HierarchySpy.Objects.Clear();
            Recorder.Log.Clear();
            GameObject Make(string n, GameObject parent)
            {
                var go = new GameObject(n);
                if (parent != null) go.transform.SetParent(parent.transform, false);
                HierarchySpy.Objects[n] = go;
                go.AddComponent<HierarchySpy>();
                return go;
            }
            var p = Make("P", null);
            Make("A", p);
            var b = Make("B", p);
            Make("B1", b);
            Make("B2", b);
            Make("C", p);
            return p;
        }

        static readonly string[] DeactivationLog = [
            "A:OnDisable P- A- B+ B1+ B2+ C+",
            "B1:OnDisable P- A- B- B1- B2+ C+",
            "B2:OnDisable P- A- B- B1- B2- C+",
            "B:OnDisable P- A- B- B1- B2- C+",
            "C:OnDisable P- A- B- B1- B2- C-",
            "P:OnDisable P- A- B- B1- B2- C-"];

        [Fact]
        public void DeactivationTurnsEachObjectInactiveWhenItsTurnComesChildrenFirst()
        {
            DeactivationTree().SetActive(false);
            Assert.Equal(DeactivationLog, Recorder.Log);
        }

        [Fact]
        public void AnOnDisableThatDeactivatesALaterSiblingDisablesItThere()
        {
            // DetailCollapsableLabel.OnDisable hides its content rows, later siblings, only while
            // they still read active: when the details screen closes too (issue #23).
            var p = DeactivationTree();
            var c = HierarchySpy.Objects["C"];
            HierarchySpy.Objects["A"].GetComponent<HierarchySpy>().onDisable = () => c.SetActive(false);
            p.SetActive(false);
            Assert.Equal(["A", "C", "B1", "B2", "B", "P"], Recorder.Log.ConvertAll(e => e.Split(':')[0]));
            p.SetActive(true);
            Assert.False(c.activeSelf);
            Assert.False(c.activeInHierarchy);
        }

        [Fact]
        public void DestructionDisablesInTheSameOrderWithTheSameFlags()
        {
            Object.DestroyImmediate(DeactivationTree());
            Assert.Equal(DeactivationLog, Recorder.Log);
        }
}
}
