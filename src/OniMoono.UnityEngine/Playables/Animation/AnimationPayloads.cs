using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace OniMoono.Engine.Playables
{
    /// Settings of an AnimationClipPlayable. They are consumed by Unity's animation evaluator,
    /// which this engine does not have: evaluating a clip playable for an animation output that
    /// targets an Animator throws instead of silently leaving the Animator unanimated.
    internal sealed class AnimationClipPayload : PlayablePayload
    {
        public AnimationClip Clip;
        public bool ApplyFootIK;
        public bool ApplyPlayableIK;
        public bool RemoveStartOffset;
        public bool OverrideLoopTime;
        public bool LoopTime;

        public override void CheckEvaluable(PlayableNode node, OutputNode output, float effectiveWeight)
        {
            if (Clip && output.Kind == typeof(AnimationPlayableOutput) && output.Target)
                throw new NotSupportedException($"Playing AnimationClip '{Clip.name}' through a PlayableGraph needs Unity's animation system, which is not implemented.");
        }
    }

    internal sealed class AnimationLayerMixerPayload : PlayablePayload
    {
        public bool SingleLayerOptimization;
        public readonly Dictionary<uint, AvatarMask> LayerMasks = new Dictionary<uint, AvatarMask>();
    }

    internal sealed class AnimationMotionXToDeltaPayload : PlayablePayload
    {
        public bool AbsoluteMotion;
    }

    internal sealed class AnimationOffsetPayload : PlayablePayload
    {
        public Vector3 Position;
        public Quaternion Rotation;
    }
}
