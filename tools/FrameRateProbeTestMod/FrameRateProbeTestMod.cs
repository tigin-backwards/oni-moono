using System.Text;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace FrameRateProbeTestMod
{
    /// Counts what the game's clocks give against the sim, by game speed (Time.timeScale), for the
    /// low-fps-timing tweak (tests/e2e/frame-rate-probe.yaml,
    /// docs/research/frame-rate-dependence.md). It patches no method that tweak or sim-step-pace
    /// guard (SimEveryTick, UnsafeSim200ms, LogicCircuitManager's Refresh and RenderEveryTick), so
    /// they stay on. Logs a line every 10 game seconds: `FrameRateProbe: ...`.
    ///
    /// - Frames, their real time (unscaledDeltaTime), their game time (deltaTime) and the sim's
    ///   (GameClock).
    /// - Sim sub-ticks (StateMachineUpdater.AdvanceOneSimSubTick), automation ticks
    ///   (LogicCircuitManager.onLogicTick) and the ticks with no sub-tick before the next one:
    ///   a one-tick signal the sim never sees.
    /// - An animation-gated step: an effect playing one animation once, played again when it
    ///   completes (onAnimComplete); the sim's seconds per play against the animation's length.
    /// - Overheat and scald damage on their game clocks: Overheatable's rule (a SIM_4000ms check,
    ///   a hit when Time.time is 7.5 s past the last, which adds 7.5) and ScaldingMonitor's (a
    ///   SIM_1000ms check, a hit when Time.time is over 5 s past the last), on Sim4000ms and
    ///   Sim1000ms of the game's own scheduler, as if always overheated or scalding.
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            harmony.Patch(AccessTools.Method(typeof(StateMachineUpdater), "AdvanceOneSimSubTick"), postfix: new HarmonyMethod(typeof(Mod), nameof(SubTickPost)));
            harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(Mod), nameof(SpawnPost)));
            Debug.Log("FrameRateProbe: installed");
        }

        /// One speed's counts.
        sealed class Counts
        {
            public int Frames, Capped, SubTicks, Ticks, Hidden, Plays, OverheatHits, ScaldHits, Checks4, Checks1;
            public double Real, Scaled, Sim, PlaySim;
        }

        static readonly Counts[] bySpeed = { new Counts(), new Counts(), new Counts(), new Counts() };
        static Counts Now => bySpeed[Mathf.Clamp(Mathf.RoundToInt(Time.timeScale), 0, 3)];

        /// For specs (`type:` state): the counts at speed 3.
        public static int Ticks3 => bySpeed[3].Ticks;
        public static int Plays3 => bySpeed[3].Plays;

        static int subTicksSinceTick = -1;
        static float lastGameTime = -1f, nextLog = -1f, lastOverheat, lastScald;
        static float playStarted;
        static string playName;
        static float playLength;
        static KBatchedAnimController fx;

        static void SubTickPost()
        {
            Now.SubTicks++;
            if (subTicksSinceTick >= 0) subTicksSinceTick++;
        }

        static void SpawnPost(Game __instance)
        {
            __instance.gameObject.AddComponent<Probe>();
            SimAndRenderScheduler.instance.Add(new Damage(), false);
            lastOverheat = lastScald = Time.time;
        }

        static void OnLogicTick()
        {
            var c = Now;
            c.Ticks++;
            if (subTicksSinceTick == 0) c.Hidden++;
            subTicksSinceTick = 0;
        }

        static float SimTime => GameClock.Instance == null ? 0f : GameClock.Instance.GetTime();

        static void Play()
        {
            playStarted = SimTime;
            fx.Play(playName, KAnim.PlayMode.Once);
        }

        static void Completed(HashedString name)
        {
            if (fx == null) return;
            var c = Now;
            c.Plays++;
            c.PlaySim += SimTime - playStarted;
            Play();
        }

        sealed class Damage : ISim1000ms, ISim4000ms
        {
            public void Sim1000ms(float dt)
            {
                Now.Checks1++;
                if (Time.time - lastScald > 5f)
                {
                    lastScald = Time.time;
                    Now.ScaldHits++;
                }
            }

            public void Sim4000ms(float dt)
            {
                Now.Checks4++;
                if (!(Time.time - lastOverheat < 7.5f))
                {
                    lastOverheat += 7.5f;
                    Now.OverheatHits++;
                }
            }
        }

        static System.Reflection.FieldInfo moved;

        /// low-fps-timing's automation ticks run between sub-ticks (OniMoono.GameTweaks.LogicTicks.Moved), or -1.
        static long Moved()
        {
            if (moved == null)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetType("OniMoono.GameTweaks.LogicTicks", false) is { } type) moved = type.GetField("Moved");
                if (moved == null) return -1;
            }
            return (long)moved.GetValue(null);
        }

        sealed class Probe : MonoBehaviour
        {
            void Start()
            {
                Game.Instance.logicCircuitManager.onLogicTick += OnLogicTick;
                Vector3 at = Components.Telepads.Count > 0 ? Components.Telepads[0].transform.GetPosition() : Vector3.zero;
                // The animation closest to 1 s long of all the game's: one step that waits on it.
                string file = null;
                KAnim.Anim anim = null;
                foreach (var kanim in Assets.Anims)
                {
                    if (kanim == null || !kanim.name.EndsWith("_fx_kanim")) continue;
                    var data = kanim.GetData();
                    if (data == null) continue;
                    for (int i = 0; i < data.animCount; i++)
                    {
                        var a = data.GetAnim(i);
                        if (a == null || a.frameRate <= 0f || a.numFrames < 2) continue;
                        if (anim == null || Mathf.Abs(a.numFrames / a.frameRate - 1f) < Mathf.Abs(anim.numFrames / anim.frameRate - 1f))
                        {
                            anim = a;
                            file = kanim.name;
                        }
                    }
                }
                fx = FXHelpers.CreateEffect(file, at + new Vector3(0f, 1f, 0f), null, false, Grid.SceneLayer.FXFront);
                fx.destroyOnAnimComplete = false;
                playName = anim.name;
                playLength = anim.numFrames / anim.frameRate;
                fx.onAnimComplete += Completed;
                Play();
                Debug.Log($"FrameRateProbe: playing {playName} of {file}, {anim.numFrames} frames at {anim.frameRate} fps ({playLength:F3} s)");
            }

            void Update()
            {
                var c = Now;
                c.Frames++;
                c.Real += Time.unscaledDeltaTime;
                c.Scaled += Time.deltaTime;
                // A frame whose game time is under its real time × speed: low-fps-timing's game time cap.
                if (Mathf.Min(Time.unscaledDeltaTime, Time.maximumDeltaTime) * Time.timeScale > Time.deltaTime * 1.0001f) c.Capped++;
                float now = SimTime;
                if (lastGameTime >= 0f) c.Sim += now - lastGameTime;
                lastGameTime = now;
                if (nextLog < 0f) nextLog = now + 10f;
                if (now < nextLog) return;
                nextLog = now + 10f;
                var line = new StringBuilder($"FrameRateProbe: t={now:F1} speed={Time.timeScale} anim={playLength:F3}");
                for (int s = 1; s <= 3; s++)
                {
                    var k = bySpeed[s];
                    if (k.Frames == 0) continue;
                    line.Append($" | s{s} frames={k.Frames} capped={k.Capped} real={k.Real:F2} scaled={k.Scaled:F2} sim={k.Sim:F2} subTicks={k.SubTicks} ticks={k.Ticks} hidden={k.Hidden}" +
                                $" plays={k.Plays} playSim={k.PlaySim:F2} checks4={k.Checks4} overheat={k.OverheatHits} checks1={k.Checks1} scald={k.ScaldHits}");
                }
                line.Append($" | moved={Moved()}");
                Debug.Log(line.ToString());
            }
        }
    }
}
