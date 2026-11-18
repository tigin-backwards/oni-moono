using OniMoono.Engine;
using OniMoono.Engine.Mecanim;

namespace UnityEngine
{
    public partial class Animator
    {
        public Animator() { }

        internal AnimatorNative AnimatorData => (AnimatorNative)EngineStateOrThrow;

        /// CRC32 of the name's UTF-8 bytes: the IDs of states, parameters, layers and tags.
        public static int StringToHash(string name) => unchecked((int)Crc32.Of(name));

        // ---- Setup ----

        public RuntimeAnimatorController runtimeAnimatorController
        {
            get => AnimatorData.Controller;
            set
            {
                Threads.AssertMainThread("set_runtimeAnimatorController");
                var a = AnimatorData;
                if (ReferenceEquals(a.Controller, value)) return;
                a.Controller = value;
                a.Rebind();
            }
        }

        public Avatar avatar
        {
            get => AnimatorData.Avatar;
            set
            {
                Threads.AssertMainThread("set_avatar");
                var a = AnimatorData;
                if (ReferenceEquals(a.Avatar, value)) return;
                a.Avatar = value;
                if (a.Playback != null) a.Rebind();
            }
        }

        /// The controller is bound and playing (the Animator is active and enabled with a
        /// controller, or kept its state while disabled).
        public bool isInitialized => AnimatorData.Playback != null;

        /// A playable graph (here: the controller) drives this Animator.
        public bool hasBoundPlayables => AnimatorData.Playback != null;

        public bool isHuman
        {
            get
            {
                var avatar = AnimatorData.Avatar;
                return avatar != null && avatar.AvatarData.IsHuman;
            }
        }

        /// The rig has root motion: a humanoid avatar or one with a root motion bone.
        public bool hasRootMotion
        {
            get
            {
                var avatar = AnimatorData.Avatar;
                if (avatar == null) return false;
                var a = avatar.AvatarData;
                return a.IsValid && (a.IsHuman || a.RootMotionBoneIndex != -1);
            }
        }

        public float speed
        {
            get => AnimatorData.Speed;
            set => AnimatorData.Speed = value;
        }

        public bool keepAnimatorStateOnDisable
        {
            get => AnimatorData.KeepStateOnDisable;
            set => AnimatorData.KeepStateOnDisable = value;
        }

        public bool writeDefaultValuesOnDisable
        {
            get => AnimatorData.WriteDefaultValuesOnDisable;
            set => AnimatorData.WriteDefaultValuesOnDisable = value;
        }

        public bool hasTransformHierarchy => AnimatorData.HasTransformHierarchy;

        public int layerCount => AnimatorData.Playback?.Layers.Length ?? 0;

        // ---- Evaluation ----

        /// Evaluates the Animator now, advancing it by deltaTime (scaled by speed).
        public void Update(float deltaTime)
        {
            Threads.AssertMainThread("Update");
            AnimatorData.Evaluate(deltaTime);
        }

        /// Re-binds the animated properties (capturing their current values as defaults) and
        /// restarts the controller.
        public void Rebind()
        {
            Threads.AssertMainThread("Rebind");
            AnimatorData.Rebind();
        }

        /// Writes the default values captured when the properties were bound.
        public void WriteDefaultValues()
        {
            Threads.AssertMainThread("WriteDefaultValues");
            AnimatorData.Playback?.WriteDefaults();
        }

        // ---- Parameters ----

        ControllerPlayback PlaybackOrWarn()
        {
            var p = AnimatorData.Playback;
            if (p == null) Debug.LogWarning("Animator is not playing an AnimatorController", this);
            return p;
        }

        /// The parameter's index, or -1 after Unity's warning when it does not exist.
        int ParameterIndex(ControllerPlayback p, int id, string name, ParameterType expected)
        {
            int i = p.Data.ParameterIndex(unchecked((uint)id));
            if (i < 0)
            {
                Debug.LogWarning($"Parameter '{name ?? "Hash " + id}' does not exist.", this);
                return -1;
            }
            var type = p.Data.Parameters[i].Type;
            bool compatible = type == expected || (expected == ParameterType.Bool && type == ParameterType.Trigger) || (expected == ParameterType.Trigger && type == ParameterType.Bool);
            if (!compatible)
            {
                Debug.LogWarning($"Parameter '{name ?? "Hash " + id}' is not of type {expected}.", this);
                return -1;
            }
            return i;
        }

        float GetFloatInternal(int id, string name)
        {
            var p = PlaybackOrWarn();
            if (p == null) return 0f;
            int i = ParameterIndex(p, id, name, ParameterType.Float);
            return i < 0 ? 0f : p.Parameters[i].Float;
        }

        void SetFloatInternal(int id, string name, float value)
        {
            Threads.AssertMainThread("SetFloat");
            var p = PlaybackOrWarn();
            if (p == null) return;
            int i = ParameterIndex(p, id, name, ParameterType.Float);
            if (i >= 0) p.Parameters[i].Float = value;
        }

        int GetIntegerInternal(int id, string name)
        {
            var p = PlaybackOrWarn();
            if (p == null) return 0;
            int i = ParameterIndex(p, id, name, ParameterType.Int);
            return i < 0 ? 0 : p.Parameters[i].Int;
        }

        void SetIntegerInternal(int id, string name, int value)
        {
            Threads.AssertMainThread("SetInteger");
            var p = PlaybackOrWarn();
            if (p == null) return;
            int i = ParameterIndex(p, id, name, ParameterType.Int);
            if (i >= 0) p.Parameters[i].Int = value;
        }

        bool GetBoolInternal(int id, string name)
        {
            var p = PlaybackOrWarn();
            if (p == null) return false;
            int i = ParameterIndex(p, id, name, ParameterType.Bool);
            return i >= 0 && p.Parameters[i].Bool;
        }

        void SetBoolInternal(int id, string name, bool value, ParameterType type)
        {
            Threads.AssertMainThread(type == ParameterType.Trigger ? "SetTrigger" : "SetBool");
            var p = PlaybackOrWarn();
            if (p == null) return;
            int i = ParameterIndex(p, id, name, type);
            if (i >= 0) p.Parameters[i].Bool = value;
        }

        public float GetFloat(string name) => GetFloatInternal(StringToHash(name), name);
        public float GetFloat(int id) => GetFloatInternal(id, null);
        public void SetFloat(string name, float value) => SetFloatInternal(StringToHash(name), name, value);
        public void SetFloat(int id, float value) => SetFloatInternal(id, null, value);

        public int GetInteger(string name) => GetIntegerInternal(StringToHash(name), name);
        public int GetInteger(int id) => GetIntegerInternal(id, null);
        public void SetInteger(string name, int value) => SetIntegerInternal(StringToHash(name), name, value);
        public void SetInteger(int id, int value) => SetIntegerInternal(id, null, value);

        public bool GetBool(string name) => GetBoolInternal(StringToHash(name), name);
        public bool GetBool(int id) => GetBoolInternal(id, null);
        public void SetBool(string name, bool value) => SetBoolInternal(StringToHash(name), name, value, ParameterType.Bool);
        public void SetBool(int id, bool value) => SetBoolInternal(id, null, value, ParameterType.Bool);

        /// A trigger stays set until a transition consumes it or it is reset.
        public void SetTrigger(string name) => SetBoolInternal(StringToHash(name), name, true, ParameterType.Trigger);
        public void SetTrigger(int id) => SetBoolInternal(id, null, true, ParameterType.Trigger);
        public void ResetTrigger(string name) => SetBoolInternal(StringToHash(name), name, false, ParameterType.Trigger);
        public void ResetTrigger(int id) => SetBoolInternal(id, null, false, ParameterType.Trigger);

        // ---- States ----

        bool CheckLayer(ControllerPlayback p, int layerIndex)
        {
            if ((uint)layerIndex < (uint)p.Layers.Length) return true;
            Debug.LogError($"Invalid Layer Index '{layerIndex}'", this);
            return false;
        }

        public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layerIndex)
        {
            var p = AnimatorData.Playback;
            if (p == null || !CheckLayer(p, layerIndex)) return default;
            var layer = p.Layers[layerIndex];
            return p.StateInfo(layer, layer.Current, layer.CurrentTime);
        }

        public AnimatorStateInfo GetNextAnimatorStateInfo(int layerIndex)
        {
            var p = AnimatorData.Playback;
            if (p == null || !CheckLayer(p, layerIndex)) return default;
            var layer = p.Layers[layerIndex];
            return layer.InTransition ? p.StateInfo(layer, layer.Next, layer.NextTime) : default;
        }

        public bool IsInTransition(int layerIndex)
        {
            var p = AnimatorData.Playback;
            if (p == null || !CheckLayer(p, layerIndex)) return false;
            return p.Layers[layerIndex].InTransition;
        }

        /// Queues a jump to a state, applied at the next update. A layer of -1 picks the first layer
        /// with the state. A time of NegativeInfinity keeps the current time when the state is
        /// already playing and starts it from 0 otherwise.
        void Goto(int stateNameHash, int layer, GotoRequest request)
        {
            Threads.AssertMainThread("Play");
            var p = PlaybackOrWarn();
            if (p == null) return;
            if (layer >= p.Layers.Length || layer < -1)
            {
                Debug.LogError($"Invalid Layer Index '{layer}'", this);
                return;
            }
            if (!p.FindState(layer, stateNameHash, out int found, out int state))
            {
                Debug.LogWarning("Animator.GotoState: State could not be found", this);
                return;
            }
            request.State = state;
            p.Layers[found].Pending = request;
        }

        public void Play(string stateName) => Play(StringToHash(stateName), -1, float.NegativeInfinity);
        public void Play(string stateName, int layer) => Play(StringToHash(stateName), layer, float.NegativeInfinity);
        public void Play(string stateName, int layer, float normalizedTime) => Play(StringToHash(stateName), layer, normalizedTime);
        public void Play(int stateNameHash) => Play(stateNameHash, -1, float.NegativeInfinity);
        public void Play(int stateNameHash, int layer) => Play(stateNameHash, layer, float.NegativeInfinity);
        public void Play(int stateNameHash, int layer, float normalizedTime)
            => Goto(stateNameHash, layer, new GotoRequest { Time = normalizedTime });

        public void PlayInFixedTime(string stateName) => PlayInFixedTime(StringToHash(stateName), -1, float.NegativeInfinity);
        public void PlayInFixedTime(string stateName, int layer) => PlayInFixedTime(StringToHash(stateName), layer, float.NegativeInfinity);
        public void PlayInFixedTime(string stateName, int layer, float fixedTime) => PlayInFixedTime(StringToHash(stateName), layer, fixedTime);
        public void PlayInFixedTime(int stateNameHash) => PlayInFixedTime(stateNameHash, -1, float.NegativeInfinity);
        public void PlayInFixedTime(int stateNameHash, int layer) => PlayInFixedTime(stateNameHash, layer, float.NegativeInfinity);
        public void PlayInFixedTime(int stateNameHash, int layer, float fixedTime)
            => Goto(stateNameHash, layer, new GotoRequest { Time = fixedTime, TimeIsFixed = true });

        public void CrossFade(string stateName, float normalizedTransitionDuration)
            => CrossFade(StringToHash(stateName), normalizedTransitionDuration, -1, float.NegativeInfinity, 0f);
        public void CrossFade(string stateName, float normalizedTransitionDuration, int layer)
            => CrossFade(StringToHash(stateName), normalizedTransitionDuration, layer, float.NegativeInfinity, 0f);
        public void CrossFade(string stateName, float normalizedTransitionDuration, int layer, float normalizedTimeOffset)
            => CrossFade(StringToHash(stateName), normalizedTransitionDuration, layer, normalizedTimeOffset, 0f);
        public void CrossFade(string stateName, float normalizedTransitionDuration, int layer, float normalizedTimeOffset, float normalizedTransitionTime)
            => CrossFade(StringToHash(stateName), normalizedTransitionDuration, layer, normalizedTimeOffset, normalizedTransitionTime);
        public void CrossFade(int stateHashName, float normalizedTransitionDuration)
            => CrossFade(stateHashName, normalizedTransitionDuration, -1, float.NegativeInfinity, 0f);
        public void CrossFade(int stateHashName, float normalizedTransitionDuration, int layer)
            => CrossFade(stateHashName, normalizedTransitionDuration, layer, float.NegativeInfinity, 0f);
        public void CrossFade(int stateHashName, float normalizedTransitionDuration, int layer, float normalizedTimeOffset)
            => CrossFade(stateHashName, normalizedTransitionDuration, layer, normalizedTimeOffset, 0f);

        /// Fades from the current pose to the state over a duration relative to the current state's
        /// length; a NegativeInfinity offset starts the destination from 0.
        public void CrossFade(int stateHashName, float normalizedTransitionDuration, int layer, float normalizedTimeOffset, float normalizedTransitionTime)
            => Goto(stateHashName, layer, new GotoRequest
            {
                CrossFade = true,
                Time = normalizedTimeOffset,
                Duration = normalizedTransitionDuration,
                Progress = normalizedTransitionTime,
            });

        public void CrossFadeInFixedTime(string stateName, float fixedTransitionDuration)
            => CrossFadeInFixedTime(StringToHash(stateName), fixedTransitionDuration, -1, 0f, 0f);
        public void CrossFadeInFixedTime(string stateName, float fixedTransitionDuration, int layer)
            => CrossFadeInFixedTime(StringToHash(stateName), fixedTransitionDuration, layer, 0f, 0f);
        public void CrossFadeInFixedTime(string stateName, float fixedTransitionDuration, int layer, float fixedTimeOffset)
            => CrossFadeInFixedTime(StringToHash(stateName), fixedTransitionDuration, layer, fixedTimeOffset, 0f);
        public void CrossFadeInFixedTime(string stateName, float fixedTransitionDuration, int layer, float fixedTimeOffset, float normalizedTransitionTime)
            => CrossFadeInFixedTime(StringToHash(stateName), fixedTransitionDuration, layer, fixedTimeOffset, normalizedTransitionTime);
        public void CrossFadeInFixedTime(int stateHashName, float fixedTransitionDuration)
            => CrossFadeInFixedTime(stateHashName, fixedTransitionDuration, -1, 0f, 0f);
        public void CrossFadeInFixedTime(int stateHashName, float fixedTransitionDuration, int layer)
            => CrossFadeInFixedTime(stateHashName, fixedTransitionDuration, layer, 0f, 0f);
        public void CrossFadeInFixedTime(int stateHashName, float fixedTransitionDuration, int layer, float fixedTimeOffset)
            => CrossFadeInFixedTime(stateHashName, fixedTransitionDuration, layer, fixedTimeOffset, 0f);

        /// Fades over fixedTransitionDuration seconds; the destination starts at fixedTimeOffset seconds.
        public void CrossFadeInFixedTime(int stateHashName, float fixedTransitionDuration, int layer, float fixedTimeOffset, float normalizedTransitionTime)
            => Goto(stateHashName, layer, new GotoRequest
            {
                CrossFade = true,
                Time = fixedTimeOffset,
                TimeIsFixed = true,
                Duration = fixedTransitionDuration,
                DurationIsFixed = true,
                Progress = normalizedTransitionTime,
            });

        // ---- Engine hooks ----

        internal override void EngineOnEnable() => AnimatorData.OnEnable();
        internal override void EngineOnDisable() => AnimatorData.OnDisable();
        internal override void EngineOnDestroy() => AnimatorSystem.Remove(AnimatorData);
    }

    public partial struct AnimatorStateInfo
    {
        private int m_Name;
        private int m_Path;
        private int m_FullPath;
        private float m_NormalizedTime;
        private float m_Length;
        private float m_Speed;
        private float m_SpeedMultiplier;
        private int m_Tag;
        private int m_Loop;

        internal AnimatorStateInfo(uint name, uint path, uint fullPath, float normalizedTime, float length, float speed, float speedMultiplier, uint tag, bool loop)
        {
            m_Name = unchecked((int)name);
            m_Path = unchecked((int)path);
            m_FullPath = unchecked((int)fullPath);
            m_NormalizedTime = normalizedTime;
            m_Length = length;
            m_Speed = speed;
            m_SpeedMultiplier = speedMultiplier;
            m_Tag = unchecked((int)tag);
            m_Loop = loop ? 1 : 0;
        }

        public int fullPathHash => m_FullPath;

        [System.Obsolete("AnimatorStateInfo.nameHash has been deprecated. Use AnimatorStateInfo.fullPathHash instead.")]
        public int nameHash => m_Path;

        public int shortNameHash => m_Name;
        public float normalizedTime => m_NormalizedTime;
        public float length => m_Length;
        public float speed => m_Speed;
        public float speedMultiplier => m_SpeedMultiplier;
        public int tagHash => m_Tag;
        public bool loop => m_Loop != 0;

        public bool IsName(string name)
        {
            int hash = Animator.StringToHash(name);
            return hash == m_FullPath || hash == m_Name || hash == m_Path;
        }

        public bool IsTag(string tag) => Animator.StringToHash(tag) == m_Tag;
    }

    public partial class RuntimeAnimatorController
    {
        protected RuntimeAnimatorController() { }

        /// Every clip the controller's states use.
        public AnimationClip[] animationClips
            => NativeOrThrow is AnimatorControllerNative c && c.Data != null ? (AnimationClip[])c.Data.Clips.Clone() : System.Array.Empty<AnimationClip>();
    }
}
