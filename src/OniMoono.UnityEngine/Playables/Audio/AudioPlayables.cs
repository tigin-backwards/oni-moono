using System;
using OniMoono.Engine.Playables;
using UnityEngine.Playables;

namespace OniMoono.Engine.Playables
{
    /// Settings of an AudioClipPlayable. Playing them is the job of Unity's audio mixer, which this
    /// engine does not have: evaluating a clip playable with a clip and a non-zero weight for an
    /// audio output throws instead of staying silent.
    internal sealed class AudioClipPayload : PlayablePayload
    {
        public UnityEngine.AudioClip Clip;
        public bool Looped;
        public float Volume = 1f;
        public float StereoPan;
        public float SpatialBlend;
        public double StartDelay;
        public double PauseDelay;

        public override void CheckEvaluable(PlayableNode node, OutputNode output, float effectiveWeight)
        {
            if (Clip && effectiveWeight > 0f && output.Kind == typeof(UnityEngine.Audio.AudioPlayableOutput))
                throw new NotSupportedException($"Playing AudioClip '{Clip.name}' through a PlayableGraph needs Unity's audio mixer, which is not implemented.");
        }
    }

    internal sealed class AudioMixerPayload : PlayablePayload
    {
        public bool NormalizeInputVolumes;
    }
}

namespace UnityEngine.Audio
{
    public partial struct AudioClipPlayable
    {
        private PlayableHandle m_Handle;

        /// A playable for `clip` whose duration is the clip's length.
        public static AudioClipPlayable Create(PlayableGraph graph, AudioClip clip, bool looping)
        {
            var playable = new AudioClipPlayable(PlayableGraphState.Get(graph)
                .CreatePlayable(typeof(AudioClipPlayable), new AudioClipPayload { Clip = clip, Looped = looping }).Handle);
            if (clip != null) playable.SetDuration(clip.length);
            return playable;
        }

        internal AudioClipPlayable(PlayableHandle handle)
        {
            if (handle.IsValid() && !handle.IsPlayableOfType<AudioClipPlayable>())
                throw new InvalidCastException("Can't set handle: the playable is not an AudioClipPlayable.");
            m_Handle = handle;
        }

        public PlayableHandle GetHandle() => m_Handle;

        public static implicit operator Playable(AudioClipPlayable playable) => new Playable(playable.GetHandle());

        public static explicit operator AudioClipPlayable(Playable playable) => new AudioClipPlayable(playable.GetHandle());

        public bool Equals(AudioClipPlayable other) => m_Handle == other.m_Handle;

        AudioClipPayload Payload => (AudioClipPayload)PlayableGraphState.Get(m_Handle).Payload;

        public AudioClip GetClip() => Payload.Clip;

        public void SetClip(AudioClip value) => Payload.Clip = value;

        public bool GetLooped() => Payload.Looped;

        public void SetLooped(bool value) => Payload.Looped = value;

        internal float GetVolume() => Payload.Volume;

        internal void SetVolume(float value)
        {
            if (value < 0f || value > 1f)
                throw new ArgumentException("Trying to set AudioClipPlayable volume outside of range (0.0 - 1.0): " + value);
            Payload.Volume = value;
        }

        internal float GetStereoPan() => Payload.StereoPan;

        internal void SetStereoPan(float value)
        {
            if (value < -1f || value > 1f)
                throw new ArgumentException("Trying to set AudioClipPlayable stereo pan outside of range (-1.0 - 1.0): " + value);
            Payload.StereoPan = value;
        }

        internal float GetSpatialBlend() => Payload.SpatialBlend;

        internal void SetSpatialBlend(float value)
        {
            if (value < 0f || value > 1f)
                throw new ArgumentException("Trying to set AudioClipPlayable spatial blend outside of range (0.0 - 1.0): " + value);
            Payload.SpatialBlend = value;
        }

        public double GetStartDelay() => Payload.StartDelay;

        internal void SetStartDelay(double value) => Payload.StartDelay = value;

        public double GetPauseDelay() => Payload.PauseDelay;

        public void Seek(double startTime, double startDelay) => Seek(startTime, startDelay, 0.0);

        /// Schedules playback from `startTime` after `startDelay` seconds, for `duration` seconds
        /// (0: unbounded), and plays the playable. Unity marks the playable done when the scheduled
        /// end reaches its current duration, and always for an unbounded seek.
        public void Seek(double startTime, double startDelay, double duration)
        {
            var payload = Payload;
            payload.StartDelay = startDelay;
            if (duration > 0.0)
            {
                if (startDelay + duration >= m_Handle.GetDuration()) m_Handle.SetDone(true);
                m_Handle.SetDuration(duration + startTime);
                payload.PauseDelay = startDelay + duration;
            }
            else
            {
                m_Handle.SetDone(true);
                m_Handle.SetDuration(double.MaxValue);
                payload.PauseDelay = 0.0;
            }
            m_Handle.SetTime(startTime);
            m_Handle.Play();
        }
    }

    public partial struct AudioMixerPlayable
    {
        private PlayableHandle m_Handle;

        public static AudioMixerPlayable Create(PlayableGraph graph, int inputCount = 0, bool normalizeInputVolumes = false)
        {
            var handle = PlayableGraphState.Get(graph).CreatePlayable(typeof(AudioMixerPlayable),
                new AudioMixerPayload { NormalizeInputVolumes = normalizeInputVolumes }).Handle;
            handle.SetInputCount(inputCount);
            return new AudioMixerPlayable(handle);
        }

        internal AudioMixerPlayable(PlayableHandle handle)
        {
            if (handle.IsValid() && !handle.IsPlayableOfType<AudioMixerPlayable>())
                throw new InvalidCastException("Can't set handle: the playable is not an AudioMixerPlayable.");
            m_Handle = handle;
        }

        public PlayableHandle GetHandle() => m_Handle;

        public static implicit operator Playable(AudioMixerPlayable playable) => new Playable(playable.GetHandle());

        public static explicit operator AudioMixerPlayable(Playable playable) => new AudioMixerPlayable(playable.GetHandle());

        public bool Equals(AudioMixerPlayable other) => m_Handle == other.m_Handle;
    }

    public partial struct AudioPlayableOutput
    {
        private PlayableOutputHandle m_Handle;

        public static AudioPlayableOutput Null => new AudioPlayableOutput(PlayableOutputHandle.Null);

        public static AudioPlayableOutput Create(PlayableGraph graph, string name, AudioSource target)
        {
            var output = new AudioPlayableOutput(PlayableGraphState.Get(graph).CreateOutput(typeof(AudioPlayableOutput), name).Handle);
            output.SetTarget(target);
            return output;
        }

        internal AudioPlayableOutput(PlayableOutputHandle handle)
        {
            if (handle.IsValid() && !handle.IsPlayableOutputOfType<AudioPlayableOutput>())
                throw new InvalidCastException("Can't set handle: the playable is not an AudioPlayableOutput.");
            m_Handle = handle;
        }

        public PlayableOutputHandle GetHandle() => m_Handle;

        public static implicit operator PlayableOutput(AudioPlayableOutput output) => new PlayableOutput(output.GetHandle());

        public static explicit operator AudioPlayableOutput(PlayableOutput output) => new AudioPlayableOutput(output.GetHandle());

        public AudioSource GetTarget() => (AudioSource)PlayableGraphState.Get(m_Handle).Target;

        public void SetTarget(AudioSource value) => PlayableGraphState.Get(m_Handle).Target = value;

        public bool GetEvaluateOnSeek() => PlayableGraphState.Get(m_Handle).EvaluateOnSeek;

        /// Whether the output plays audio when the graph is evaluated after a seek (scrubbing).
        public void SetEvaluateOnSeek(bool value) => PlayableGraphState.Get(m_Handle).EvaluateOnSeek = value;
    }

    public static partial class AudioPlayableBinding
    {
        /// A binding whose output is an AudioPlayableOutput targeting an AudioSource.
        public static PlayableBinding Create(string name, Object key)
            => PlayableBinding.CreateInternal(name, key, typeof(AudioSource), CreateAudioOutput);

        private static PlayableOutput CreateAudioOutput(PlayableGraph graph, string name)
            => AudioPlayableOutput.Create(graph, name, null);
    }
}
