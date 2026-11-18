using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;

namespace OniMoono.Engine.Mecanim
{
    /// Engine side of an Animator. The controller is "playing" (Unity's isInitialized) while the
    /// Animator is active and enabled with a controller: it is bound when it becomes enabled and
    /// released on disable unless keepAnimatorStateOnDisable is set. Re-binding captures default
    /// values afresh and resets parameters and states, as in Unity.
    internal sealed class AnimatorNative : BehaviourNative
    {
        public RuntimeAnimatorController Controller;
        public Avatar Avatar;
        public int CullingMode;
        /// AnimatorUpdateMode: 0 Normal, 1 Fixed (AnimatePhysics), 2 UnscaledTime.
        public int UpdateMode;
        public bool ApplyRootMotion, LinearVelocityBlending, StabilizeFeet, AnimatePhysics;
        public bool HasTransformHierarchy = true, AllowConstantClipSamplingOptimization = true;
        public bool KeepStateOnDisable, WriteDefaultValuesOnDisable;
        public float Speed = 1f;

        public ControllerPlayback Playback;

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            Avatar = resolver.Resolve(data.PPtr("m_Avatar")) as Avatar;
            Controller = resolver.Resolve(data.PPtr("m_Controller")) as RuntimeAnimatorController;
            CullingMode = data.Int("m_CullingMode");
            UpdateMode = data.Int("m_UpdateMode");
            ApplyRootMotion = data.Bool("m_ApplyRootMotion");
            LinearVelocityBlending = data.Bool("m_LinearVelocityBlending");
            StabilizeFeet = data.Bool("m_StabilizeFeet");
            AnimatePhysics = data.Bool("m_AnimatePhysics");
            HasTransformHierarchy = data.Bool("m_HasTransformHierarchy");
            AllowConstantClipSamplingOptimization = data.Bool("m_AllowConstantClipSamplingOptimization");
            KeepStateOnDisable = data.Bool("m_KeepAnimatorStateOnDisable");
            WriteDefaultValuesOnDisable = data.Bool("m_WriteDefaultValuesOnDisable");
            Serialized = null;
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var a = (AnimatorNative)source;
            Controller = a.Controller;
            Avatar = a.Avatar;
            CullingMode = a.CullingMode;
            UpdateMode = a.UpdateMode;
            ApplyRootMotion = a.ApplyRootMotion;
            LinearVelocityBlending = a.LinearVelocityBlending;
            StabilizeFeet = a.StabilizeFeet;
            AnimatePhysics = a.AnimatePhysics;
            HasTransformHierarchy = a.HasTransformHierarchy;
            AllowConstantClipSamplingOptimization = a.AllowConstantClipSamplingOptimization;
            KeepStateOnDisable = a.KeepStateOnDisable;
            WriteDefaultValuesOnDisable = a.WriteDefaultValuesOnDisable;
            Speed = a.Speed;
        }

        ControllerData ControllerData => Controller && Controller.Native is AnimatorControllerNative c ? c.Data : null;

        /// Binds the controller if the Animator is active and enabled and has one.
        public void Initialize()
        {
            if (Playback != null || !IsActiveAndEnabledState) return;
            var data = ControllerData;
            if (data == null) return;
            Playback = new ControllerPlayback(data, (Transform)GameObject.Components[0]);
        }

        public void Release() => Playback = null;

        public void Rebind()
        {
            Release();
            Initialize();
        }

        public void OnEnable()
        {
            Initialize();
            AnimatorSystem.Add(this);
        }

        public void OnDisable()
        {
            AnimatorSystem.Remove(this);
            if (KeepStateOnDisable) return;
            if (WriteDefaultValuesOnDisable) Playback?.WriteDefaults();
            Release();
        }

        /// One animator update: advance the state machines, then write the pose.
        public void Evaluate(float deltaTime)
        {
            var p = Playback;
            if (p == null) return;
            p.Update(deltaTime * Speed);
            p.Apply();
        }
    }

    /// The Animators to update each frame (Unity's DirectorUpdateAnimationBegin/End in
    /// PreLateUpdate; Fixed-mode Animators in FixedUpdate after the scripts' FixedUpdate).
    internal static class AnimatorSystem
    {
        static readonly List<AnimatorNative> animators = new();

        static AnimatorSystem()
        {
            PlayerLoop.AnimationUpdate += () => Run(fixedStep: false);
            PlayerLoop.FixedAnimationUpdate += () => Run(fixedStep: true);
        }

        public static void Add(AnimatorNative a)
        {
            if (!ByReference.Contains(animators, a)) animators.Add(a);
        }

        public static void Remove(AnimatorNative a) => ByReference.Remove(animators, a);

        const int FixedMode = 1, UnscaledTimeMode = 2;

        static void Run(bool fixedStep)
        {
            if (animators.Count == 0) return;
            foreach (var a in animators.ToArray())
            {
                if (a.Destroyed || !a.IsActiveAndEnabledState || a.Playback == null) continue;
                if ((a.UpdateMode == FixedMode) != fixedStep) continue;
                float dt = a.UpdateMode == UnscaledTimeMode ? Time.unscaledDeltaTime : Time.deltaTime;
                try
                {
                    a.Evaluate(dt);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, a.Managed);
                }
            }
        }
    }
}
