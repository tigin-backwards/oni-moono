using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;

namespace OniMoono.Engine.Mecanim
{
    /// Engine side of the legacy Animation component. Each clip has an AnimationState; enabled
    /// states advance by the frame's delta time (fixed delta with animatePhysics) and are sampled
    /// onto the hierarchy in the legacy animation update. A Once/Clamp state that reaches its end
    /// is sampled there, then stops and rewinds; Loop and PingPong wrap; ClampForever holds the
    /// last frame and keeps playing.
    internal sealed class AnimationNative : BehaviourNative
    {
        public AnimationClip DefaultClip;
        WrapMode wrapMode;
        public bool PlayAutomatically = true;
        public bool AnimatePhysics;
        /// AnimationCullingType. BasedOnRenderers culling (skipping updates while the object's
        /// renderers are invisible) is not implemented: such Animations always animate.
        public int CullingType;

        public readonly List<AnimationState> States = new();
        AnimationBinder binder;

        public WrapMode WrapMode
        {
            get => wrapMode;
            set
            {
                wrapMode = value;
                foreach (var s in States) s.wrapMode = value;
            }
        }

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            DefaultClip = resolver.Resolve(data.PPtr("m_Animation")) as AnimationClip;
            wrapMode = (WrapMode)data.Int("m_WrapMode");
            PlayAutomatically = data.Bool("m_PlayAutomatically");
            AnimatePhysics = data.Bool("m_AnimatePhysics");
            CullingType = data.Int("m_CullingType");
            foreach (var p in data.Array("m_Animations"))
                if (resolver.Resolve((PPtr)p) is AnimationClip clip) AddClip(clip, clip.name);
            if (DefaultClip && State(DefaultClip.name) == null) AddClip(DefaultClip, DefaultClip.name);
            Serialized = null;
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var a = (AnimationNative)source;
            DefaultClip = a.DefaultClip;
            wrapMode = a.wrapMode;
            PlayAutomatically = a.PlayAutomatically;
            AnimatePhysics = a.AnimatePhysics;
            CullingType = a.CullingType;
            foreach (var s in a.States) AddClip(s.clip, s.name);
        }

        public AnimationState State(string name)
        {
            foreach (var s in States)
                if (s.name == name) return s;
            return null;
        }

        public void AddClip(AnimationClip clip, string name)
        {
            if (!clip) throw new NullReferenceException("AddClip: the clip is null.");
            var existing = State(name);
            if (existing != null) States.Remove(existing);
            var clipWrap = clip.wrapMode;
            States.Add(new AnimationState(name, clip, clipWrap == WrapMode.Default ? wrapMode : clipWrap));
        }

        // Animation.RemoveClip(clip): Unity's equality, as the facade's caller compares (a destroyed
        // clip matches the states whose clip was destroyed).
#pragma warning disable MOONO002
        public void RemoveClip(AnimationClip clip) => States.RemoveAll(s => s.clip == clip);
#pragma warning restore MOONO002

        public void RemoveClip(string name) => States.RemoveAll(s => s.name == name);

        public bool IsPlaying(string name)
        {
            foreach (var s in States)
                if (s.enabled && (name == null || s.name == name)) return true;
            return false;
        }

        public bool Play(string name, PlayMode mode)
        {
            var state = State(name);
            if (state == null)
            {
                Debug.LogWarning($"The animation state {name} could not be played because it couldn't be found!\nPlease attach an animation clip with the name '{name}' or call this function only for existing animations.", Managed);
                return false;
            }
            if (!state.clip.legacy)
            {
                Debug.LogError($"The AnimationClip '{state.clip.name}' used by the Animation component '{((Component)Managed).gameObject.name}' must be marked as Legacy.", Managed);
                return false;
            }
            foreach (var s in States)
                if (s != state && (mode == PlayMode.StopAll || s.layer == state.layer)) StopState(s);
            state.enabled = true;
            state.weight = 1f;
            return true;
        }

        static void StopState(AnimationState s)
        {
            s.enabled = false;
            s.time = 0f;
        }

        public void Stop(string name)
        {
            foreach (var s in States)
                if (name == null || s.name == name) StopState(s);
        }

        public void Rewind(string name)
        {
            foreach (var s in States)
                if (name == null || s.name == name) s.time = 0f;
        }

        public void OnEnable()
        {
            binder = null; // the hierarchy may have changed while disabled
            LegacyAnimationSystem.Add(this);
            if (PlayAutomatically && DefaultClip) Play(DefaultClip.name, PlayMode.StopSameLayer);
        }

        public void OnDisable()
        {
            LegacyAnimationSystem.Remove(this);
            Stop(null);
        }

        /// One update: advance the enabled states, sample them, stop the ones that ended.
        public void Update(float deltaTime)
        {
            var samples = new List<(AnimationState State, float Time)>();
            var ended = new List<AnimationState>();
            foreach (var s in States)
            {
                if (!s.enabled) continue;
                s.time += deltaTime * s.speed;
                samples.Add((s, ClipTime(s, out bool end)));
                if (end) ended.Add(s);
            }
            Apply(samples);
            foreach (var s in ended) StopState(s);
        }

        /// Samples the enabled states at their current times.
        public void Sample()
        {
            var samples = new List<(AnimationState, float)>();
            foreach (var s in States)
                if (s.enabled) samples.Add((s, ClipTime(s, out _)));
            Apply(samples);
        }

        /// The clip time of a state under its wrap mode; end is set when a Once/Clamp state has
        /// run past its end (or before its start, playing backwards).
        static float ClipTime(AnimationState s, out bool end)
        {
            end = false;
            float length = s.length, t = s.time;
            switch (s.wrapMode)
            {
                case WrapMode.Loop:
                    return length > 0f ? Mathf.Repeat(t, length) : 0f;
                case WrapMode.PingPong:
                    return length > 0f ? Mathf.PingPong(t, length) : 0f;
                case WrapMode.ClampForever:
                    return Mathf.Clamp(t, 0f, length);
                default: // Once, Clamp, Default
                    if (t >= length && s.speed >= 0f || t <= 0f && s.speed < 0f)
                    {
                        end = true;
                        return s.speed >= 0f ? length : 0f;
                    }
                    return Mathf.Clamp(t, 0f, length);
            }
        }

        readonly Dictionary<(PropertyTarget Target, int Component), (float Sum, float Weight)> blend = new();
        readonly List<PropertyTarget> staged = new();
        readonly HashSet<MonoBehaviour> notify = new(ReferenceEqualityComparer.Instance);

        /// Writes the weighted values of the sampled states. Higher layers take their weight
        /// first and lower layers share what remains; a property keeps its own value in
        /// proportion to the weight no state claims.
        void Apply(List<(AnimationState State, float Time)> samples)
        {
            if (samples.Count == 0) return;
            binder ??= new AnimationBinder((Transform)GameObject.Components[0]);
            blend.Clear();
            samples.Sort((a, b) => b.State.layer.CompareTo(a.State.layer));
            float remaining = 1f;
            for (int i = 0; i < samples.Count && remaining > 0f;)
            {
                int layer = samples[i].State.layer, end = i;
                float layerWeight = 0f;
                for (; end < samples.Count && samples[end].State.layer == layer; end++) layerWeight += Math.Max(0f, samples[end].State.weight);
                float scale = layerWeight > remaining ? remaining / layerWeight : 1f;
                for (; i < end; i++)
                {
                    var (state, time) = samples[i];
                    float w = Math.Max(0f, state.weight) * scale;
                    if (w > 0f) Accumulate(state.clip.Clip, state.clip.Clip.LegacyFirstKey + time, w);
                }
                remaining -= Math.Min(layerWeight, remaining);
            }

            staged.Clear();
            foreach (var ((target, component), (sum, weight)) in blend)
            {
                if (!target.Alive) continue;
                float value = weight >= 1f ? sum / weight : sum + (1f - weight) * target.Read(component);
                if (target.Stage(component, value)) staged.Add(target);
            }
            notify.Clear();
            foreach (var t in staged)
            {
                t.Commit();
                if (t.Notify) notify.Add(t.Notify);
            }
            foreach (var mb in notify)
                if (mb && mb.Native is BehaviourNative { Script.OnDidApplyAnimationProperties: { } message })
                    Lifecycle.Invoke(message, mb);
        }

        void Accumulate(AnimationClipNative clip, float time, float weight)
        {
            var curves = clip.LegacyCurves;
            for (int b = 0; b < curves.Count; b++)
            {
                var binding = curves.Bindings[b];
                var target = binder.Resolve(binding);
                if (target == null) continue;
                var components = curves.Curves[b];
                for (int c = 0; c < components.Length; c++)
                {
                    int component = AnimationBinder.ComponentOf(binding, c);
                    var key = (target, component);
                    blend.TryGetValue(key, out var acc);
                    blend[key] = (acc.Sum + components[c].Evaluate(time) * weight, acc.Weight + weight);
                }
            }
        }
    }

    /// The legacy Animations to update each frame (Unity's PreLateUpdate.LegacyAnimationUpdate;
    /// animatePhysics ones in FixedUpdate.LegacyFixedAnimationUpdate).
    internal static class LegacyAnimationSystem
    {
        static readonly List<AnimationNative> animations = new();

        static LegacyAnimationSystem()
        {
            PlayerLoop.AnimationUpdate += () => Run(fixedStep: false);
            PlayerLoop.FixedAnimationUpdate += () => Run(fixedStep: true);
        }

        public static void Add(AnimationNative a)
        {
            if (!ByReference.Contains(animations, a)) animations.Add(a);
        }

        public static void Remove(AnimationNative a) => ByReference.Remove(animations, a);

        static void Run(bool fixedStep)
        {
            if (animations.Count == 0) return;
            foreach (var a in animations.ToArray())
            {
                if (a.Destroyed || !a.IsActiveAndEnabledState || a.AnimatePhysics != fixedStep) continue;
                try
                {
                    a.Update(fixedStep ? Time.fixedDeltaTime : Time.deltaTime);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, a.Managed);
                }
            }
        }
    }
}
