using System;
using OniMoono.Engine;
using OniMoono.Engine.Playables;

namespace UnityEngine.Playables
{
    /// Plays a PlayableAsset (in the game: Timeline assets) through a PlayableGraph.
    /// OniMoono.Engine.Playables.PlayableDirectorNative documents graph creation, the director
    /// update and the wrap modes. timeUpdateMode is absent (DirectorUpdateMode is outside the
    /// game's API surface); the serialized mode is used.
    public partial class PlayableDirector
    {
        public event Action<PlayableDirector> played;

        public event Action<PlayableDirector> paused;

        public event Action<PlayableDirector> stopped;

        PlayableDirectorNative Data => (PlayableDirectorNative)EngineStateOrThrow;

        public PlayState state => Data.State;

        public DirectorWrapMode extrapolationMode
        {
            get => Data.WrapMode;
            set => Data.WrapMode = value;
        }

        public PlayableAsset playableAsset
        {
            get => Data.Asset as PlayableAsset;
            set
            {
                Threads.AssertMainThread("set_playableAsset");
                Data.SetAsset(value);
            }
        }

        /// The graph being played; an invalid graph while the director has none.
        public PlayableGraph playableGraph => Data.Graph;

        public bool playOnAwake
        {
            get => Data.PlayOnAwake;
            set => Data.PlayOnAwake = value;
        }

        /// The director's time in seconds (the root playable's local time). Setting it moves
        /// playback there; the graph is evaluated at the new time on the next update or Evaluate.
        public double time
        {
            get => Data.Time;
            set
            {
                Threads.AssertMainThread("set_time");
                Data.Time = value;
            }
        }

        /// Where playback starts when the director has no graph (first Play, after Stop).
        public double initialTime
        {
            get => Data.InitialTime;
            set => Data.SetInitialTime(value);
        }

        /// The asset's duration (0 without an asset).
        public double duration => Data.Duration;

        /// Evaluates the graph at the next director update instead of now.
        public void DeferredEvaluate() => Data.DeferredEvaluate();

        /// Plays with the time advancing in whole frames of `frameRate`.
        internal void Play(FrameRate frameRate)
        {
            Threads.AssertMainThread("Play");
            Data.Play(frameRate);
        }

        public void Play(PlayableAsset asset)
        {
            if (asset == null) throw new ArgumentNullException("asset");
            Play(asset, extrapolationMode);
        }

        public void Play(PlayableAsset asset, DirectorWrapMode mode)
        {
            if (asset == null) throw new ArgumentNullException("asset");
            playableAsset = asset;
            extrapolationMode = mode;
            Play();
        }

        public void SetGenericBinding(Object key, Object value) => Data.SetGenericBinding(key, value);

        /// Evaluates the graph at the current time (building it first if needed).
        public void Evaluate()
        {
            Threads.AssertMainThread("Evaluate");
            Data.Evaluate();
        }

        /// Builds the graph if needed and starts playing it; `played` is invoked.
        public void Play()
        {
            Threads.AssertMainThread("Play");
            Data.Play();
        }

        /// Destroys the graph and resets the time to initialTime; `stopped` is invoked.
        public void Stop()
        {
            Threads.AssertMainThread("Stop");
            Data.Stop();
        }

        /// Stops the graph's evaluation, keeping the time; `paused` is invoked.
        public void Pause()
        {
            Threads.AssertMainThread("Pause");
            Data.Pause();
        }

        /// Plays a paused director again.
        public void Resume()
        {
            Threads.AssertMainThread("Resume");
            Data.Resume();
        }

        public void RebuildGraph()
        {
            Threads.AssertMainThread("RebuildGraph");
            Data.RebuildGraph();
        }

        public void SetReferenceValue(PropertyName id, Object value) => Data.ExposedReferences[id] = value;

        /// The value stored for `id`; idValid is false (and the result null) if none is stored.
        public Object GetReferenceValue(PropertyName id, out bool idValid)
        {
            idValid = Data.ExposedReferences.TryGetValue(id, out var value);
            return value;
        }

        public void ClearReferenceValue(PropertyName id) => Data.ExposedReferences.Remove(id);

        /// The object bound to `key` (a track), or null.
        public Object GetGenericBinding(Object key)
        {
            var d = Data;
            if ((object)key == null) return null;
            return d.SceneBindings.TryGetValue(key, out var value) ? value : null;
        }

        public void ClearGenericBinding(Object key) => Data.ClearGenericBinding(key);

        /// Re-applies the generic bindings to the graph's outputs.
        public void RebindPlayableGraphOutputs() => Data.BindOutputs();

        internal bool HasGenericBinding(Object key) => (object)key != null && Data.SceneBindings.ContainsKey(key);

        // ---- engine side ----

        internal void SendPlayed() => played?.Invoke(this);

        internal void SendPaused() => paused?.Invoke(this);

        internal void SendStopped() => stopped?.Invoke(this);

        internal override void EngineOnEnable() => Data.OnEnable();

        internal override void EngineOnDisable() => Data.OnDisable();

        internal override void EngineOnDestroy()
        {
            if (EngineState is PlayableDirectorNative d) d.OnDestroy();
        }
    }
}
