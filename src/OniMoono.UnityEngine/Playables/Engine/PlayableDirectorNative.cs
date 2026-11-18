using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Playables
{
    /// Unity's DirectorUpdateMode (the enum is not part of the game's API surface).
    internal enum DirectorClock
    {
        DSPClock = 0,
        GameTime = 1,
        UnscaledGameTime = 2,
        Manual = 3,
    }

    /// Engine side of a PlayableDirector (class 320): its serialized settings and bindings, and
    /// the graph it plays.
    ///
    /// The graph is created on demand (Play, Evaluate, RebuildGraph): a new graph resolving
    /// exposed references through the director, the asset's root playable from
    /// `PlayableAsset.CreatePlayable(graph, gameObject)` (the asset creates its own outputs, as
    /// Timeline does), then every output whose reference object has a generic binding is bound
    /// to it (script outputs get it as user data, animation outputs an Animator, audio outputs an
    /// AudioSource). The director's time is the root playable's local time.
    ///
    /// While playing, the director update (Update.DirectorUpdate, after scripts and coroutines)
    /// advances the time by the update mode's delta × the root's speed and evaluates the graph;
    /// the first update after the graph is built evaluates the start time itself (the graph
    /// sees it as a seek).
    /// At the asset's duration the wrap mode applies: Loop wraps the time, Hold keeps it at the
    /// duration and keeps evaluating, None stops the director (graph destroyed, `stopped`).
    /// Pause stops the graph (behaviours get OnBehaviourPause and OnGraphStop) and keeps the
    /// time; Stop destroys the graph and resets the time to initialTime.
    internal sealed class PlayableDirectorNative : BehaviourNative
    {
        public ScriptableObject Asset;
        public DirectorWrapMode WrapMode = DirectorWrapMode.Hold;
        public DirectorClock UpdateMode = DirectorClock.GameTime;
        public bool PlayOnAwake = true;
        public double InitialTime;
        public readonly Dictionary<PropertyName, Object> ExposedReferences = new();
        /// Generic bindings keyed by the binding key object (a track), compared by identity like
        /// Unity's instance-ID keyed table.
        public readonly Dictionary<Object, Object> SceneBindings = new(ReferenceEqualityComparer.Instance);

        PlayableGraph graph;
        PlayableNode root;
        bool playing;
        /// The time while there is no graph.
        double time;
        bool deferredEvaluate;
        /// Play(FrameRate): time advances in whole frames of this rate.
        FrameRate frameRate;
        double unquantizedTime;

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Asset = resolver.Resolve(f.PPtr("m_PlayableAsset")) as ScriptableObject;
            PlayOnAwake = f.Int("m_InitialState") == (int)PlayState.Playing;
            WrapMode = (DirectorWrapMode)f.Int("m_WrapMode");
            UpdateMode = (DirectorClock)f.Int("m_DirectorUpdateMode");
            InitialTime = f.Get<double>("m_InitialTime");
            time = InitialTime;
            foreach (Fields pair in f.Array("m_SceneBindings"))
            {
                var key = resolver.Resolve(pair.PPtr("key"));
                if ((object)key != null) SceneBindings[key] = resolver.Resolve(pair.PPtr("value"));
            }
            foreach (Fields pair in f.Struct("m_ExposedReferences").Array("m_References"))
            {
                var name = pair["first"] is Fields id ? id.String("id") : (string)pair["first"];
                ExposedReferences[new PropertyName(name)] = resolver.Resolve(pair.PPtr("second"));
            }
        }

        public override void RemapReferences(Func<Object, Object> map)
        {
            var bindings = new List<KeyValuePair<Object, Object>>(SceneBindings);
            SceneBindings.Clear();
            foreach (var (key, value) in bindings) SceneBindings[map(key)] = map(value);
            foreach (var name in new List<PropertyName>(ExposedReferences.Keys)) ExposedReferences[name] = map(ExposedReferences[name]);
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var d = (PlayableDirectorNative)source;
            Asset = d.Asset;
            WrapMode = d.WrapMode;
            UpdateMode = d.UpdateMode;
            PlayOnAwake = d.PlayOnAwake;
            InitialTime = d.InitialTime;
            time = d.InitialTime;
            foreach (var kv in d.ExposedReferences) ExposedReferences[kv.Key] = kv.Value;
            foreach (var kv in d.SceneBindings) SceneBindings[kv.Key] = kv.Value;
        }

        PlayableDirector Director => (PlayableDirector)Managed;

        public PlayableAsset PlayableAsset => Asset as PlayableAsset;

        public PlayableGraph Graph => graph;

        bool HasGraph => graph.IsValid() && root != null && root.Alive;

        public PlayState State => playing ? PlayState.Playing : PlayState.Paused;

        /// The asset's duration (0 without an asset).
        public double Duration
        {
            get
            {
                var asset = PlayableAsset;
                return !asset ? 0 : asset.duration;
            }
        }

        public double Time
        {
            get => HasGraph ? root.Time : time;
            set
            {
                time = value;
                unquantizedTime = value;
                if (HasGraph) root.SetTime(value);
            }
        }

        // ---- graph ----

        /// Builds the graph if there is an asset and no graph. Returns whether a graph exists.
        bool EnsureGraph()
        {
            if (HasGraph) return true;
            var asset = PlayableAsset;
            if (!asset) return false;
            var g = PlayableGraph.Create(Director.gameObject.name);
            g.SetResolver(Director);
            Playable playable;
            try
            {
                playable = asset.CreatePlayable(g, Director.gameObject);
            }
            catch
            {
                g.Destroy();
                throw;
            }
            var node = PlayableGraphState.Resolve(playable.GetHandle());
            if (node == null)
            {
                g.Destroy();
                return false;
            }
            graph = g;
            root = node;
            root.SetTime(time);
            unquantizedTime = time;
            BindOutputs();
            DirectorSystem.Add(this);
            return true;
        }

        void DestroyGraph()
        {
            DirectorSystem.Remove(this);
            if (graph.IsValid())
            {
                if (root != null && root.Alive) time = root.Time;
                graph.Destroy();
            }
            graph = default;
            root = null;
        }

        /// Applies the generic bindings to the graph's outputs.
        public void BindOutputs()
        {
            if (!graph.IsValid()) return;
            var state = PlayableGraphState.Get(graph);
            for (int i = 0; i < state.OutputCount; i++)
            {
                var output = state.GetOutput(i);
                if ((object)output.ReferenceObject == null || !SceneBindings.TryGetValue(output.ReferenceObject, out var value)) continue;
                if (output.Kind == typeof(UnityEngine.Animations.AnimationPlayableOutput))
                    output.Target = value as Animator ?? (value is GameObject go ? go.GetComponent<Animator>() : null);
                else if (output.Kind == typeof(AudioPlayableOutput))
                    output.Target = value as AudioSource;
                else
                    output.UserData = value;
            }
        }

        // ---- control ----

        public void Play()
        {
            if (!EnsureGraph()) return;
            if (playing) return;
            playing = true;
            PlayableGraphState.Get(graph).Play();
            Director.SendPlayed();
        }

        public void Play(FrameRate rate)
        {
            frameRate = rate;
            Play();
            if (HasGraph) PlayableGraphState.Get(graph).MatchFrameRate = rate.IsValid();
            if (HasGraph) PlayableGraphState.Get(graph).FrameRate = rate;
        }

        public void Pause()
        {
            if (!playing) return;
            playing = false;
            if (graph.IsValid()) PlayableGraphState.Get(graph).Stop();
            Director.SendPaused();
        }

        public void Resume()
        {
            if (playing || !HasGraph) return;
            Play();
        }

        public void Stop()
        {
            bool hadGraph = graph.IsValid();
            playing = false;
            frameRate = default;
            deferredEvaluate = false;
            DestroyGraph();
            time = InitialTime;
            unquantizedTime = InitialTime;
            if (hadGraph) Director.SendStopped();
        }

        /// Evaluates the graph at the current time without advancing it.
        public void Evaluate()
        {
            if (!EnsureGraph()) return;
            PlayableGraphState.Get(graph).Evaluate(0, FrameData.EvaluationType.Evaluate);
        }

        public void DeferredEvaluate() => deferredEvaluate = true;

        /// Destroys and recreates the graph, keeping the time and play state.
        public void RebuildGraph()
        {
            bool wasPlaying = playing;
            double t = Time;
            if (wasPlaying && graph.IsValid()) PlayableGraphState.Get(graph).Stop();
            playing = false;
            DestroyGraph();
            time = t;
            if (!EnsureGraph()) return;
            if (wasPlaying)
            {
                playing = true;
                PlayableGraphState.Get(graph).Play();
            }
        }

        public void SetAsset(ScriptableObject asset)
        {
            if (ReferenceEquals(Asset, asset)) return;
            Asset = asset;
            if (graph.IsValid()) RebuildGraph();
        }

        /// Without a graph, the director's time moves to the new initial time.
        public void SetInitialTime(double value)
        {
            InitialTime = value;
            if (!graph.IsValid())
            {
                time = value;
                unquantizedTime = value;
            }
        }

        public void SetGenericBinding(Object key, Object value)
        {
            if ((object)key == null) return;
            SceneBindings[key] = value;
            BindOutputs();
        }

        public void ClearGenericBinding(Object key)
        {
            if ((object)key == null) return;
            SceneBindings.Remove(key);
        }

        // ---- lifecycle ----

        public void OnEnable()
        {
            if (PlayOnAwake) Play();
        }

        /// Disabling the component (or deactivating its GameObject) stops the director.
        public void OnDisable() => Stop();

        public void OnDestroy()
        {
            playing = false;
            DestroyGraph();
        }

        // ---- the director update ----

        public void Update()
        {
            if (!HasGraph) return;
            var state = PlayableGraphState.Get(graph);
            // While a parent timeline evaluates this graph (it synchronized it this frame or the
            // last, whichever director updates first), the parent controls time and evaluation.
            if (state.SynchronizedFrame >= UnityEngine.Time.frameCount - 1 && state.SynchronizedFrame >= 0) return;
            if (playing && UpdateMode != DirectorClock.Manual)
            {
                double dt = UpdateMode == DirectorClock.GameTime ? UnityEngine.Time.deltaTime
                    // DSPClock follows the audio output clock, which advances with real time.
                    : UnityEngine.Time.unscaledDeltaTime;
                double step = dt * root.Speed;
                double next = root.Time + step;
                if (frameRate.IsValid())
                {
                    // Frame-locked playback: the evaluated time is the last whole frame reached.
                    unquantizedTime += step;
                    next = Math.Floor(unquantizedTime * frameRate.rate + 1e-9) / frameRate.rate;
                    step = next - root.Time;
                    dt = root.Speed != 0 ? step / root.Speed : 0;
                }
                double duration = Duration;
                if (duration > 0 && !double.IsInfinity(duration) && next >= duration)
                {
                    switch (WrapMode)
                    {
                        case DirectorWrapMode.None:
                            Stop();
                            return;
                        case DirectorWrapMode.Loop:
                            root.SetTime(next % duration);
                            unquantizedTime %= duration;
                            break;
                        default: // Hold
                            root.SetTime(duration);
                            unquantizedTime = duration;
                            break;
                    }
                }
                deferredEvaluate = false;
                state.Tick(dt);
                return;
            }
            if (deferredEvaluate)
            {
                deferredEvaluate = false;
                state.Evaluate(0, FrameData.EvaluationType.Evaluate);
            }
        }
    }

    /// The directors that have a graph, updated in Update.DirectorUpdate in the order their
    /// graphs were created.
    internal static class DirectorSystem
    {
        static readonly List<PlayableDirectorNative> directors = new();

        static DirectorSystem() => PlayerLoop.DirectorUpdate += Update;

        public static void Add(PlayableDirectorNative d)
        {
            if (!ByReference.Contains(directors, d)) directors.Add(d);
        }

        public static void Remove(PlayableDirectorNative d) => ByReference.Remove(directors, d);

        static void Update()
        {
            if (directors.Count == 0) return;
            foreach (var d in directors.ToArray())
            {
                if (!ByReference.Contains(directors, d) || d.Destroyed) continue;
                try
                {
                    d.Update();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, d.Managed);
                }
            }
        }
    }
}
