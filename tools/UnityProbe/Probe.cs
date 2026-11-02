// A mod for the ORIGINAL game (Unity player) that records Unity's lifecycle semantics in
// Player.log (lines starting with "PROBE"): when OnDisable/OnDestroy run around Destroy, whether
// objects awaiting destruction are enabled again, the order of OnApplicationQuit. Its results
// are what the engine's Lifecycle reproduces (LifecycleTests). See README.md.
using System.Collections;
using UnityEngine;

namespace SemanticsProbe
{
    public class Mod : KMod.UserMod2
    {
        public override void OnLoad(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Game), "OnApplicationQuit"), prefix: new HarmonyLib.HarmonyMethod(typeof(Mod), nameof(GameQuitPrefix)), postfix: new HarmonyLib.HarmonyMethod(typeof(Mod), nameof(GameQuitPostfix)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(MinionIdentity), "OnSpawn"), prefix: new HarmonyLib.HarmonyMethod(typeof(Mod), nameof(MinionSpawnPrefix)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(KMonoBehaviour), "Start"), prefix: new HarmonyLib.HarmonyMethod(typeof(Mod), nameof(KStartPrefix)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(MinionIdentity), "OnDied"), prefix: new HarmonyLib.HarmonyMethod(typeof(Mod), nameof(MinionRemovedPrefix)));
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(DetailsScreen), "OnCmpDisable"), prefix: new HarmonyLib.HarmonyMethod(typeof(Mod), nameof(DetailsDisablePrefix)));
            var go = new GameObject("SemanticsProbe");
            Object.DontDestroyOnLoad(go);            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "canvas")
            {
                go.AddComponent<CanvasProbe>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "drawmeshnow")
            {
                go.AddComponent<DrawMeshNow>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "activetarget")
            {
                go.AddComponent<ActiveTarget>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "canvasorder")
            {
                go.AddComponent<CanvasOrder>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "draworder")
            {
                go.AddComponent<DrawOrder>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "textwidth")
            {
                go.AddComponent<TextWidth>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "autosizepref")
            {
                go.AddComponent<AutoSizePreferred>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "textheight")
            {
                go.AddComponent<TextHeight>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "textoverflow")
            {
                go.AddComponent<TextOverflow>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "inputlayout")
            {
                go.AddComponent<InputLayout>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "inputtext")
            {
                go.AddComponent<InputText>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "inputcaret")
            {
                InputCaret.Install(harmony);
                go.AddComponent<InputCaret>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "tmpgaps")
            {
                TmpGaps.Install(harmony);
                go.AddComponent<TmpGaps>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "fontasset")
            {
                go.AddComponent<FontAsset>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "pointermove")
            {
                go.AddComponent<PointerMove>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "scrollbar")
            {
                go.AddComponent<ScrollbarPress>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "layoutcull")
            {
                go.AddComponent<LayoutCull>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "childactive")
            {
                go.AddComponent<ChildActivation>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "childdims")
            {
                go.AddComponent<ChildDimensions>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "focusdelivery")
            {
                Rec.Log("=== values: focus delivery");
                FocusDelivery.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "deactorder")
            {
                Rec.Log("=== values: deactivation order");
                DeactivateOrder.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "floatprec")
            {
                Rec.Log("=== values: float precision");
                FloatPrecision.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "jpg")
            {
                Rec.Log("=== values: jpg encode");
                JpgEncode.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "convert")
            {
                Rec.Log("=== values: conversions");
                Conversions.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "dirorder")
            {
                Rec.Log("=== values: directory order");
                DirectoryOrder.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "stackdepth")
            {
                Rec.Log("=== values: stack depth");
                StackDepth.Run(harmony);
                harmony.Patch(HarmonyLib.AccessTools.Method(typeof(MainMenu), "OnSpawn"), postfix: new HarmonyLib.HarmonyMethod(typeof(StackDepth), nameof(StackDepth.Summary)));
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "callingasm")
            {
                Rec.Log("=== values: calling assembly");
                CallingAssembly.Run(harmony);
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "asmbind")
            {
                Rec.Log("=== values: assembly binding");
                AssemblyBinding.Run(mod, path);
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "memberorder")
            {
                Rec.Log("=== values: member order");
                MemberOrder.Run();
                MemberOrder.HookDump(harmony);
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "smorder")
            {
                MemberOrder.Hook(harmony);
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "delegatejson")
            {
                Rec.Log("=== values: delegate json");
                DelegateJson.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "nativerange")
            {
                NativeRanges.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "statics")
            {
                Rec.Log("=== values: statics");
                StaticValues.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "submesh")
            {
                go.AddComponent<SubMesh>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "modtext")
            {
                go.AddComponent<ModText>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "tallline")
            {
                go.AddComponent<TallLine>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "indent")
            {
                go.AddComponent<IndentWidth>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "emptytext")
            {
                go.AddComponent<EmptyText>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "wordcount")
            {
                go.AddComponent<WordCount>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "missingtypes")
            {
                go.AddComponent<MissingTypes>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "kindchange")
            {
                go.AddComponent<KindChange>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "missingmembers")
            {
                go.AddComponent<MissingMembers>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "atcall")
            {
                go.AddComponent<AtCall>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "quitevents")
            {
                go.AddComponent<QuitEvents>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "emptycomposite")
            {
                go.AddComponent<EmptyComposite>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "newobject")
            {
                go.AddComponent<NewObject>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "findorder")
            {
                go.AddComponent<FindOrder>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "alignment")
            {
                go.AddComponent<Alignment>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "logging")
            {
                go.AddComponent<Logging>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "logthreads")
            {
                go.AddComponent<LogThreads>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "tooltip")
            {
                go.AddComponent<Tooltip>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "invscale")
            {
                InverseScale.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "transform")
            {
                TransformMath.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "matmul")
            {
                MatrixMath.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "projection")
            {
                ProjectionMath.Run();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "cardcollapse")
            {
                CardCollapse.Install(harmony);
                go.AddComponent<CardCollapse>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "researchpan")
            {
                ResearchPan.Install(harmony);
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "scrollgeo")
            {
                ScrollGeo.Install(harmony);
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "vitalsopen")
            {
                VitalsOpen.Install(harmony);
                go.AddComponent<VitalsOpen>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "scrollvis")
            {
                go.AddComponent<ScrollVis>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "zeroscale")
            {
                go.AddComponent<ZeroScale>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "updorder")
            {
                UpdateOrder.Install(harmony);
                go.AddComponent<UpdateOrder>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "meshshrink")
            {
                go.AddComponent<MeshShrink>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "guilayout")
            {
                go.AddComponent<GuiLayout>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "legacytext")
            {
                go.AddComponent<LegacyText>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "destroyflags")
            {
                go.AddComponent<DestroyFlags>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "particleemit")
            {
                go.AddComponent<ParticleEmit>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "frametiming")
            {
                go.AddComponent<FrameTimings>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "execorder")
            {
                go.AddComponent<ExecOrder>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "awakedestroy")
            {
                go.AddComponent<AwakeDestroy>();
                return;
            }
            if (System.Environment.GetEnvironmentVariable("PROBE_SCENARIO") == "startdestroy")
            {
                go.AddComponent<SDScenario>();
                return;
            }
            go.AddComponent<Probe>();
            go.AddComponent<QuitLog>().Tag = "ddol-at-load";
            go.AddComponent<FrameLog>();
            go.AddComponent<UiDump>();
            go.AddComponent<EventQueueLog>();
            go.AddComponent<Triggers>();
            go.AddComponent<UiSemantics>();
            UiSemantics.Install(harmony);
            Rec.Log("OnLoad renderedFrameCount=" + Time.renderedFrameCount + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        }

        public static void GameQuitPrefix()
        {
            var d = DetailsScreen.Instance;
            Rec.Log("Game.OnApplicationQuit prefix: DetailsScreen " + (d == null ? "null" : "activeInHierarchy=" + d.gameObject.activeInHierarchy + " isActiveAndEnabled=" + d.isActiveAndEnabled) + " App.IsExiting=" + App.IsExiting);
        }

        public static void GameQuitPostfix(Game __instance)
        {
            var d = DetailsScreen.Instance;
            var mgr = HarmonyLib.Traverse.Create(__instance).Field("screenMgr").GetValue<UnityEngine.Object>();
            var mgrGo = mgr is Component c ? c.gameObject : mgr as GameObject;
            Rec.Log("Game.OnApplicationQuit postfix: screenMgr=" + (mgrGo == null ? "null" : mgrGo.name) + " details under it=" + (d != null && mgrGo != null && d.transform.IsChildOf(mgrGo.transform)) + " details isActiveAndEnabled=" + (d != null && d.isActiveAndEnabled) + " details path=" + (d == null ? "" : Path(d.transform)) + " mgr comp enabled=" + (mgr is Behaviour b && b.isActiveAndEnabled));
        }

        /// The order Duplicants spawn in after a load (Components.LiveMinionIdentities follows it).
        public static void MinionSpawnPrefix(MinionIdentity __instance) => Rec.Log("MINION OnSpawn " + __instance.name + " kpid=" + __instance.GetComponent<KPrefabID>().InstanceID + " dead=" + __instance.gameObject.HasTag(GameTags.Dead) + " live=" + string.Join(",", Components.LiveMinionIdentities.Items.ConvertAll(m => m.name)));

        public static void KStartPrefix(KMonoBehaviour __instance)
        {
            var id = __instance.GetComponent<MinionIdentity>();
            if (id != null) Rec.Log("KSTART " + __instance.GetType().Name + " on " + id.name + " kpid=" + __instance.GetComponent<KPrefabID>().InstanceID + " spawned=" + __instance.isSpawned);
        }

        public static void MinionRemovedPrefix(MinionIdentity __instance) => Rec.Log("MINION removed " + __instance.name + " live=" + string.Join(",", Components.LiveMinionIdentities.Items.ConvertAll(m => m.name)));

        static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        public static void DetailsDisablePrefix() => Rec.Log("DetailsScreen.OnCmpDisable called, App.IsExiting=" + App.IsExiting + " AudioMixer.instance null=" + (AudioMixer.instance == null));
    }

    public class Rec : MonoBehaviour
    {
        public string Tag;
        public Rec Other;
        public bool DestroyOnDisable;
        public static void Log(string s) => Debug.Log("PROBE f" + Time.frameCount + " " + s);
        void Awake() => Log(Tag + " Awake");
        void OnEnable() => Log(Tag + " OnEnable");
        void OnDisable()
        {
            Log(Tag + " OnDisable other=" + (Other == null ? "null" : "alive"));
            if (!DestroyOnDisable) return;
            try { Destroy(Other.gameObject); Log(Tag + " destroyed other ok"); }
            catch (System.Exception e) { Log(Tag + " EXCEPTION " + e.GetType().Name); }
            Destroy(gameObject);
        }
        void OnDestroy() => Log(Tag + " OnDestroy other=" + (Other == null ? "null" : "alive"));
    }

    /// Logs its Start, for the order Starts run in.
    public class StartLog : MonoBehaviour
    {
        public string Tag;
        void Start() => Rec.Log("START " + Tag);
    }

    /// Start-order probes: components with Start and none, one or another of the update
    /// messages, on objects made in one batch (StartSets), from a coroutine or from a Start.
    public class StartBase : MonoBehaviour { public string Tag; }
    public class SN : StartBase { void Start() => Rec.Log("START " + Tag + "/N"); }
    public class SU : StartBase { bool once; void Start() => Rec.Log("START " + Tag + "/U"); void Update() { if (!once) Rec.Log("FIRST Update " + Tag); once = true; } }
    public class SL : StartBase { bool once; void Start() => Rec.Log("START " + Tag + "/L"); void LateUpdate() { if (!once) Rec.Log("FIRST LateUpdate " + Tag); once = true; } }
    public class SF : StartBase { bool once; void Start() => Rec.Log("START " + Tag + "/F"); void FixedUpdate() { if (!once) Rec.Log("FIRST FixedUpdate " + Tag); once = true; } }
    public class Spawner : StartBase { void Start() { Rec.Log("START " + Tag + " spawner"); StartSets.Make(Tag); } }
    public class SpawnerU : StartBase { void Start() { Rec.Log("START " + Tag + " spawnerU"); StartSets.Make(Tag); } void Update() { } }

    /// A Start that makes a set and a further Spawner (whose own Start then runs a frame later,
    /// in the frame's first pass of pending Starts, as the game's SaveLoader does).
    public class ChainSpawner : StartBase
    {
        void Start()
        {
            Rec.Log("START " + Tag + " chain");
            if (Tag.EndsWith("2-")) { StartSets.Make(Tag); return; }
            new GameObject("chain2").AddComponent<ChainSpawner>().Tag = Tag + "2-";
        }
    }

    /// Marks the frame's phases while armed (Frames > 0), and makes a set from inside its own
    /// Update, LateUpdate or FixedUpdate when asked.
    public class PhaseMarker : MonoBehaviour
    {
        public static int Frames;
        public static string InUpdate, InLate, InFixed;
        void FixedUpdate()
        {
            if (Frames <= 0) return;
            Rec.Log("MARK FixedUpdate");
            if (InFixed != null) { var t = InFixed; InFixed = null; StartSets.Make(t); }
        }
        void Update()
        {
            if (Frames <= 0) return;
            Rec.Log("MARK Update");
            if (InUpdate != null) { var t = InUpdate; InUpdate = null; StartSets.Make(t); }
        }
        void LateUpdate()
        {
            if (Frames <= 0) return;
            Rec.Log("MARK LateUpdate");
            if (InLate != null) { var t = InLate; InLate = null; StartSets.Make(t); }
            Frames--;
        }
        void OnEnable() => StartCoroutine(EndOfFrames());
        IEnumerator EndOfFrames()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (Frames > 0) Rec.Log("MARK EndOfFrame");
            }
        }
    }

    /// Lifecycle of a component added by another one's Awake while its object activates (as
    /// the game's MyCmpAdd does): A adds C in its Awake, B follows A.
    public class AwakeLog : MonoBehaviour
    {
        public virtual string Name => "?";
        protected virtual void Awake() => Rec.Log("ADDED " + Name + " Awake");
        void OnEnable() => Rec.Log("ADDED " + Name + " OnEnable");
        void Start() => Rec.Log("ADDED " + Name + " Start");
        bool once;
        void Update() { if (!once) Rec.Log("ADDED " + Name + " Update"); once = true; }
    }
    public class AwakeAdderA : AwakeLog
    {
        public override string Name => "A";
        protected override void Awake() { base.Awake(); if (GetComponent<AwakeAddedC>() == null) gameObject.AddComponent<AwakeAddedC>(); Rec.Log("ADDED A Awake done, components=" + GetComponents<AwakeLog>().Length); }
    }
    public class AwakeB : AwakeLog { public override string Name => "B"; }
    public class AwakeAddedC : AwakeLog { public override string Name => "C"; }

    public static class StartSets
    {
        static GameObject template;
        static void AddAll(GameObject go)
        {
            go.AddComponent<SN>(); go.AddComponent<SU>(); go.AddComponent<SN>(); go.AddComponent<SL>(); go.AddComponent<SF>(); go.AddComponent<SN>();
        }
        static void Tag(GameObject go, string tag)
        {
            var all = go.GetComponents<StartBase>();
            for (int i = 0; i < all.Length; i++) all[i].Tag = tag + "#" + i;
        }
        /// Two new objects, then two clones of an inactive template activated one by one (as
        /// the game's save loader does).
        public static void Make(string prefix)
        {
            if (template == null) { template = new GameObject("ST"); template.SetActive(false); AddAll(template); Object.DontDestroyOnLoad(template); }
            for (int i = 0; i < 2; i++) { var go = new GameObject(prefix + "new" + i); go.SetActive(false); AddAll(go); Tag(go, prefix + "new" + i); go.SetActive(true); }
            for (int i = 0; i < 2; i++) { var go = Object.Instantiate(template); Tag(go, prefix + "clone" + i); go.SetActive(true); }
        }
    }

    /// Scenario 20: Destroy of objects whose Start is still pending. The game's SaveLoader, in
    /// a Start of the frame's first pass, makes Game (Start and Update) and then
    /// MinionSelectScreen, whose Awake makes CharacterContainers (Start, no update messages);
    /// Game's Start, run just before its first Update, destroys the screen.
    public class SDPart : StartBase
    {
        public bool Started;
        void Start() { Started = true; Rec.Log("SD " + Tag + " Start"); }
        void OnDisable() => Rec.Log("SD " + Tag + " OnDisable started=" + Started);
        void OnDestroy() => Rec.Log("SD " + Tag + " OnDestroy started=" + Started);
    }
    public class SDPartU : SDPart { bool once; void Update() { if (!once) Rec.Log("SD " + Tag + " first Update"); once = true; } }
    public class SDPartL : SDPart { bool once; void LateUpdate() { if (!once) Rec.Log("SD " + Tag + " first LateUpdate"); once = true; } }

    /// The screen: its Awake makes children with Start only, with Update and with LateUpdate,
    /// and a grandchild with Start only.
    public class SDScreen : SDPart
    {
        public static string Prefix;
        void Awake()
        {
            Tag = Prefix + "screen";
            Child<SDPart>("N", transform);
            Child<SDPartU>("U", transform);
            Child<SDPartL>("L", transform);
            Child<SDPart>("deepN", Child<SDPart>("mid", transform).transform);
        }
        T Child<T>(string name, Transform parent) where T : SDPart
        {
            var go = new GameObject(Prefix + name);
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<T>();
            c.Tag = Prefix + name;
            return c;
        }
        public static GameObject Make(string prefix)
        {
            Prefix = prefix;
            return new GameObject(prefix + "screen", typeof(SDScreen));
        }
    }

    /// Game's part: its Start destroys the screen.
    public class SDGame : StartBase
    {
        public GameObject Target;
        bool once;
        void Start() { Rec.Log("SD " + Tag + "game Start, destroying the screen"); Destroy(Target); }
        void Update() { if (!once) Rec.Log("SD " + Tag + "game first Update"); once = true; }
    }

    /// SaveLoader's part: a Start that makes the game and then the screen.
    public class SDLoader : StartBase
    {
        void Start()
        {
            Rec.Log("SD " + Tag + "loader Start");
            var game = new GameObject(Tag + "game").AddComponent<SDGame>();
            game.Tag = Tag;
            game.Target = SDScreen.Make(Tag);
            Rec.Log("SD " + Tag + "loader Start done");
        }
    }

    /// Makes the loader from a Start, so that the loader starts in the next frame's first pass.
    public class SDChain : StartBase { void Start() => new GameObject("SDLoader").AddComponent<SDLoader>().Tag = Tag; }

    /// Makes a screen and destroys it at once, from Update or LateUpdate (Mode, once).
    public class SDNow : MonoBehaviour
    {
        public static string Mode;
        void Update() { if (Mode == "upd-") { Mode = null; Destroy(SDScreen.Make("upd-")); Rec.Log("SD upd- made and destroyed in Update"); } }
        void LateUpdate() { if (Mode == "late-") { Mode = null; Destroy(SDScreen.Make("late-")); Rec.Log("SD late- made and destroyed in LateUpdate"); } }
    }

    /// Made in Update before and after a Destroy there: whether its Start, in the pass after
    /// Update, still finds what was destroyed.
    public class SDWitness : StartBase
    {
        public SDPart Watched;
        void Start() => Rec.Log("SD " + Tag + " Start, watched " + (Watched == null ? "gone" : "there"));
    }

    /// In Update (Armed, once): a witness, Destroy of a screen made frames earlier (all of it
    /// started), a second witness.
    public class SDOld : MonoBehaviour
    {
        public static bool Armed;
        public static GameObject Old;
        void Update()
        {
            if (!Armed) return;
            Armed = false;
            var deep = Old.transform.Find("old-mid/old-deepN").GetComponent<SDPart>();
            var w1 = new GameObject("w1").AddComponent<SDWitness>(); w1.Tag = "old-witness1"; w1.Watched = deep;
            Destroy(Old);
            var w2 = new GameObject("w2").AddComponent<SDWitness>(); w2.Tag = "old-witness2"; w2.Watched = deep;
            Rec.Log("SD old- destroyed in Update");
        }
    }

    public class SDScenario : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return null;
            yield return Run();
            Rec.Log("=== done");
        }

        public static IEnumerator Run()
        {
            Rec.Log("=== scenario 20: Destroy of objects whose Start is pending");
            new GameObject("marker20").AddComponent<PhaseMarker>();
            new GameObject("SDNow").AddComponent<SDNow>();
            yield return null;
            PhaseMarker.Frames = 4;
            Rec.Log("coroutine: making the loader's chain");
            new GameObject("SDChain").AddComponent<SDChain>().Tag = "load-";
            for (int i = 0; i < 4; i++) yield return null;
            // The same with no fixed step in the loader's frame.
            float fixedStep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 5f;
            yield return null;
            PhaseMarker.Frames = 4;
            new GameObject("SDChain").AddComponent<SDChain>().Tag = "loadnf-";
            for (int i = 0; i < 4; i++) yield return null;
            Time.fixedDeltaTime = fixedStep;
            PhaseMarker.Frames = 2;
            SDNow.Mode = "upd-";
            for (int i = 0; i < 3; i++) yield return null;
            PhaseMarker.Frames = 2;
            SDNow.Mode = "late-";
            for (int i = 0; i < 3; i++) yield return null;
            PhaseMarker.Frames = 2;
            yield return null;
            Destroy(SDScreen.Make("co-"));
            Rec.Log("SD co- made and destroyed in a coroutine");
            for (int i = 0; i < 3; i++) yield return null;
            SDOld.Old = SDScreen.Make("old-");
            new GameObject("SDOld").AddComponent<SDOld>();
            yield return null;
            yield return null;
            PhaseMarker.Frames = 2;
            SDOld.Armed = true;
            for (int i = 0; i < 3; i++) yield return null;
        }
    }

    public class Upd : MonoBehaviour
    {
        public static bool Armed;
        void Update()
        {
            Rec.Log("U Update");
            if (Armed) { Armed = false; Destroy(gameObject); Rec.Log("U destroyed self in Update, enabled=" + enabled); }
        }
        void LateUpdate() => Rec.Log("U LateUpdate");
        void OnDisable() => Rec.Log("U OnDisable");
        void OnDestroy() => Rec.Log("U OnDestroy");
    }

    /// When `yield return null` resumes, for coroutines started from Start, Update and LateUpdate.
    public class CoTiming : MonoBehaviour
    {
        int updates, lates;
        IEnumerator Co(string from)
        {
            Rec.Log("co[" + from + "] started");
            yield return null;
            Rec.Log("co[" + from + "] after 1st null");
            yield return null;
            Rec.Log("co[" + from + "] after 2nd null");
            yield return new WaitForEndOfFrame();
            Rec.Log("co[" + from + "] after WaitForEndOfFrame");
            yield return null;
            Rec.Log("co[" + from + "] after null following end of frame");
        }
        void Start() { Rec.Log("CoTiming Start"); StartCoroutine(Co("Start")); }
        void Update() { Rec.Log("CoTiming Update"); if (updates++ == 1) StartCoroutine(Co("Update")); }
        void LateUpdate() { Rec.Log("CoTiming LateUpdate"); if (lates++ == 3) StartCoroutine(Co("LateUpdate")); if (lates > 8) enabled = false; }
    }

    /// The length of the input event queue (Event.PopEvent) at Update and LateUpdate, when
    /// it is not empty (at most 40 lines).
    public class EventQueueLog : MonoBehaviour
    {
        int lines;
        void Update() { int n = Event.GetEventCount(); if (n > 0 && lines++ < 40) Rec.Log("EVQ Update " + n); }
        void LateUpdate() { int n = Event.GetEventCount(); if (n > 0 && lines++ < 40) Rec.Log("EVQ LateUpdate " + n); }
    }

    /// The first frames: frame counters and the active scene at Update and LateUpdate.
    public class FrameLog : MonoBehaviour
    {
        int n;
        void Update() { if (n < 12) Rec.Log("Update rendered=" + Time.renderedFrameCount + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name); }
        void LateUpdate() { if (n++ < 12) Rec.Log("LateUpdate rendered=" + Time.renderedFrameCount + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name); }
    }

    public class QuitLog : MonoBehaviour
    {
        public string Tag;
        void OnApplicationQuit() => Rec.Log("QUIT " + Tag + " App.IsExiting=" + App.IsExiting);
    }

    public class Probe : MonoBehaviour
    {
        Rec quitVictim;
        void OnApplicationQuit()
        {
            Rec.Log("Probe OnApplicationQuit: destroying QV");
            if (quitVictim != null) Destroy(quitVictim.gameObject);
            Rec.Log("Probe OnApplicationQuit: after Destroy, QV enabled=" + (quitVictim != null && quitVictim.enabled));
        }

        static Rec Make(string tag, Transform parent)
        {
            var go = new GameObject(tag);
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<Rec>();
            r.Tag = tag;
            return r;
        }

        IEnumerator Start()
        {
            yield return null;
            Rec.Log("=== scenario 1: disable (destroys sibling + self), reactivate, same frame");
            var p = new GameObject("P").transform;
            var x = Make("X", p);
            var a = Make("A", p);
            a.Other = x; a.DestroyOnDisable = true;
            yield return null;
            p.gameObject.SetActive(false);
            Rec.Log("after SetActive(false)");
            p.gameObject.SetActive(true);
            Rec.Log("after SetActive(true)");
            yield return null;
            Rec.Log("scenario 1 next frame: A=" + (a == null ? "null" : "alive") + " X=" + (x == null ? "null" : "alive"));

            Rec.Log("=== scenario 2: Destroy(B) then Destroy(C), order of callbacks");
            var b = Make("B", null); var c = Make("C", null); var d = Make("D", null);
            b.Other = c; c.Other = b; d.Other = b;
            yield return null;
            Destroy(b.gameObject); Destroy(c.gameObject); Destroy(d.gameObject);
            Rec.Log("requested B, C, D");
            yield return null;
            Rec.Log("=== scenario 3: Destroy(X2) then Destroy(A2) (A2 active, OnDisable reads X2)");
            var x2 = Make("X2", null); var a2 = Make("A2", null); a2.Other = x2;
            yield return null;
            Destroy(x2.gameObject); Destroy(a2.gameObject);
            yield return null;
            Rec.Log("=== scenario 4: SetActive(true) on an object pending destroy");
            var e = Make("E", null);
            yield return null;
            e.gameObject.SetActive(false);
            Destroy(e.gameObject);
            e.gameObject.SetActive(true);
            Rec.Log("E after reactivation enabled=" + e.isActiveAndEnabled);
            yield return null;
            Rec.Log("=== scenario 5: flags after Destroy(go), child included");
            var f = Make("F", null); var fc = Make("FC", f.transform);
            yield return null;
            Destroy(f.gameObject);
            Rec.Log("F activeSelf=" + f.gameObject.activeSelf + " activeInHierarchy=" + f.gameObject.activeInHierarchy + " enabled=" + f.enabled + " isActiveAndEnabled=" + f.isActiveAndEnabled + " FC activeInHierarchy=" + fc.gameObject.activeInHierarchy + " FC isActiveAndEnabled=" + fc.isActiveAndEnabled + " F==null " + (f == null));
            yield return null;
            Rec.Log("=== scenario 6: Destroy(component)");
            var g = Make("G", null);
            yield return null;
            Destroy(g);
            Rec.Log("G after Destroy(component): enabled=" + g.enabled + " isActiveAndEnabled=" + g.isActiveAndEnabled + " null=" + (g == null));
            yield return null;
            Rec.Log("=== scenario 7: Destroy(go, 0.3)");
            var h = Make("H", null);
            yield return null;
            Destroy(h.gameObject, 0.3f);
            Rec.Log("H requested delayed");
            yield return new WaitForSeconds(0.6f);
            Rec.Log("=== scenario 8: Update on a pending-destroy object this frame");
            var u = new GameObject("U").AddComponent<Upd>();
            yield return null; yield return null;
            Upd.Armed = true;
            yield return null; yield return null;
            Rec.Log("=== scenario 9: SetActive(false) then Destroy, then parent toggled");
            var q = Make("Q", null); var qc = Make("QC", q.transform);
            yield return null;
            qc.gameObject.SetActive(false);
            Destroy(qc.gameObject);
            Rec.Log("QC activeSelf after destroy=" + qc.gameObject.activeSelf);
            q.gameObject.SetActive(false); q.gameObject.SetActive(true);
            Rec.Log("QC after parent toggle activeInHierarchy=" + qc.gameObject.activeInHierarchy);
            yield return null;
            Rec.Log("=== scenario 10: deep hierarchy destroyed from the root");
            var d0 = Make("D0", null); var d1 = Make("D1", d0.transform); var d2 = Make("D2", d1.transform); var d3 = Make("D3", d2.transform); var d4 = Make("D4", d3.transform);
            yield return null;
            Destroy(d0.gameObject);
            Rec.Log("after Destroy(D0): D4 isActiveAndEnabled=" + d4.isActiveAndEnabled);
            yield return null;
            quitVictim = Make("QV", null);
            new GameObject("late").AddComponent<QuitLog>().Tag = "scene-late";
            Rec.Log("=== scenario 11: coroutine timing");
            new GameObject("CoTiming").AddComponent<CoTiming>();
            for (int i = 0; i < 10; i++) yield return null;
            Rec.Log("=== scenario 12: Start order of objects made in one frame");
            for (int i = 0; i < 4; i++) new GameObject("S" + i).AddComponent<StartLog>().Tag = "new" + i;
            var template = new GameObject("T"); template.SetActive(false); template.AddComponent<StartLog>();
            for (int i = 0; i < 4; i++) { var clone = Instantiate(template); clone.GetComponent<StartLog>().Tag = "clone" + i; clone.SetActive(true); }
            var inactive = new GameObject[4];
            for (int i = 0; i < 4; i++) { inactive[i] = Instantiate(template); inactive[i].GetComponent<StartLog>().Tag = "activated" + i; }
            for (int i = 0; i < 4; i++) inactive[i].SetActive(true);
            yield return null;
            yield return null;
            Rec.Log("=== scenario 13: Start order of objects with and without update messages, made in a coroutine");
            StartSets.Make("co-");
            yield return null;
            yield return null;
            Rec.Log("=== scenario 14: the same, made inside a Start without Update");
            new GameObject("SP").AddComponent<Spawner>().Tag = "inStart-";
            yield return null;
            yield return null;
            Rec.Log("=== scenario 15: the same, made inside a Start with Update");
            new GameObject("SPU").AddComponent<SpawnerU>().Tag = "inStartU-";
            yield return null;
            yield return null;
            Rec.Log("=== scenario 16: the same, made inside a Start with Update, after objects of their own");
            new GameObject("pre").AddComponent<SN>().Tag = "pre";
            new GameObject("preU").AddComponent<SU>().Tag = "preU";
            new GameObject("SPU2").AddComponent<SpawnerU>().Tag = "inStartU2-";
            new GameObject("post").AddComponent<SN>().Tag = "post";
            new GameObject("postU").AddComponent<SU>().Tag = "postU";
            yield return null;
            yield return null;
            Rec.Log("=== scenario 17: phases; a Start made a frame earlier makes a set; sets made in Update, LateUpdate, FixedUpdate");
            new GameObject("marker").AddComponent<PhaseMarker>();
            yield return null;
            PhaseMarker.Frames = 3;
            Rec.Log("coroutine: making chain");
            new GameObject("chain").AddComponent<ChainSpawner>().Tag = "chain-";
            yield return null;
            yield return null;
            yield return null;
            PhaseMarker.Frames = 2;
            PhaseMarker.InUpdate = "inUpdate-";
            yield return null;
            yield return null;
            yield return null;
            PhaseMarker.Frames = 2;
            PhaseMarker.InLate = "inLate-";
            yield return null;
            yield return null;
            yield return null;
            PhaseMarker.Frames = 3;
            PhaseMarker.InFixed = "inFixed-";
            for (int i = 0; i < 8; i++) yield return null;
            Rec.Log("=== scenario 18: a Start made a frame earlier makes a set, with fixed steps in every frame");
            float fixedStep = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 0.0005f;
            yield return null;
            PhaseMarker.Frames = 3;
            new GameObject("chainF").AddComponent<ChainSpawner>().Tag = "chainF-";
            for (int i = 0; i < 3; i++) { yield return null; Rec.Log("coroutine after Update"); }
            Time.fixedDeltaTime = fixedStep;
            Rec.Log("=== scenario 19: a component added by an Awake while its object activates");
            var adder = new GameObject("adder");
            adder.SetActive(false);
            adder.AddComponent<AwakeAdderA>();
            adder.AddComponent<AwakeB>();
            adder.SetActive(true);
            var order = adder.GetComponents<AwakeLog>();
            Rec.Log("ADDED component order " + string.Join(",", System.Array.ConvertAll(order, c => c.Name)));
            yield return null;
            yield return null;
            yield return SDScenario.Run();
            yield return Values.Run();
            Rec.Log("=== done");
        }
    }
}
