using System.Collections;
using OniMoono.Engine;
using OniMoono.Engine.Mecanim;

namespace UnityEngine
{
    public enum PlayMode
    {
        StopSameLayer = 0,
        StopAll = 4,
    }

    public enum AnimationCullingType
    {
        AlwaysAnimate = 0,
        BasedOnRenderers = 1,
    }

    /// The legacy Animation component: plays legacy clips on its GameObject's hierarchy, one
    /// AnimationState per clip (named after the clip). With playAutomatically, the default clip
    /// plays each time the component becomes enabled.
    public sealed class Animation : Behaviour, IEnumerable
    {
        public Animation() { }

        internal AnimationNative Data => (AnimationNative)EngineStateOrThrow;

        /// The default clip.
        public AnimationClip clip
        {
            get => Data.DefaultClip;
            set => Data.DefaultClip = value;
        }

        public bool playAutomatically
        {
            get => Data.PlayAutomatically;
            set => Data.PlayAutomatically = value;
        }

        /// The wrap mode of clips whose own wrap mode is Default.
        public WrapMode wrapMode
        {
            get => Data.WrapMode;
            set => Data.WrapMode = value;
        }

        public bool animatePhysics
        {
            get => Data.AnimatePhysics;
            set => Data.AnimatePhysics = value;
        }

        public AnimationCullingType cullingType
        {
            get => (AnimationCullingType)Data.CullingType;
            set => Data.CullingType = (int)value;
        }

        public bool isPlaying => Data.IsPlaying(null);

        public bool IsPlaying(string name) => Data.IsPlaying(name);

        /// The state of the clip with this name; null if there is none.
        public AnimationState this[string name] => Data.State(name);

        public bool Play() => Play(PlayMode.StopSameLayer);

        public bool Play(PlayMode mode) => Data.DefaultClip != null && Data.Play(Data.DefaultClip.name, mode);

        public bool Play(string animation) => Data.Play(animation, PlayMode.StopSameLayer);

        public bool Play(string animation, PlayMode mode) => Data.Play(animation, mode);

        public void Stop() => Data.Stop(null);

        public void Stop(string name) => Data.Stop(name);

        public void Rewind() => Data.Rewind(null);

        public void Rewind(string name) => Data.Rewind(name);

        /// Samples the enabled states at their current times now.
        public void Sample() => Data.Sample();

        public void AddClip(AnimationClip clip, string newName) => Data.AddClip(clip, newName);

        public void RemoveClip(AnimationClip clip) => Data.RemoveClip(clip);

        public void RemoveClip(string clipName) => Data.RemoveClip(clipName);

        public int GetClipCount() => Data.States.Count;

        public AnimationClip GetClip(string name) => Data.State(name)?.clip;

        /// The AnimationStates, in the order the clips were added.
        public IEnumerator GetEnumerator() => Data.States.ToArray().GetEnumerator();

        internal override void EngineOnEnable() => Data.OnEnable();
        internal override void EngineOnDisable() => Data.OnDisable();
        internal override void EngineOnDestroy() => LegacyAnimationSystem.Remove(Data);
    }

    /// One clip of an Animation: its playback time, speed, weight and wrap mode.
    public sealed class AnimationState
    {
        internal AnimationState(string name, AnimationClip clip, WrapMode wrapMode)
        {
            this.name = name;
            this.clip = clip;
            this.wrapMode = wrapMode;
        }

        public string name { get; set; }

        public AnimationClip clip { get; }

        /// Playing: the state advances and is sampled.
        public bool enabled { get; set; }

        public float weight { get; set; }

        public WrapMode wrapMode { get; set; }

        /// Seconds since the state started; not wrapped.
        public float time { get; set; }

        public float speed { get; set; } = 1f;

        public int layer { get; set; }

        /// The clip's length in seconds.
        public float length => clip.length;

        public float normalizedTime
        {
            get => length > 0f ? time / length : 0f;
            set => time = value * length;
        }

        public float normalizedSpeed
        {
            get => length > 0f ? speed / length : 0f;
            set => speed = value * length;
        }
    }
}
