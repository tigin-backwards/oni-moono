using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine.Mecanim
{
    /// A pending Play / CrossFade request; applied at the start of the next update, as in Unity.
    internal sealed class GotoRequest
    {
        public int State;
        /// Destination start time; NegativeInfinity = keep the time if the state is already current.
        public float Time;
        public bool TimeIsFixed;
        public bool CrossFade;
        public float Duration;
        public bool DurationIsFixed;
        /// Initial progress of a cross-fade (normalizedTransitionTime).
        public float Progress;
    }

    /// The state-machine position of one layer.
    internal sealed class LayerPlayback
    {
        public LayerData Data;
        public StateMachineData Machine;
        public float Weight;

        public int Current;
        public float CurrentTime;

        /// Active transition: destination state and its time, progress in [0, 1].
        public int Next = -1;
        public float NextTime;
        public TransitionData Transition;   // null for Play/CrossFade-initiated fades
        public int TransitionIndex;          // index in its owner's list (ordered interruption)
        public bool DurationIsFixed;
        public float Duration;
        public float Progress;
        /// The blended pose at the moment the transition was interrupted: its source.
        public float[] Frozen;
        public bool[] FrozenWritten;

        public GotoRequest Pending;

        public bool InTransition => Next >= 0;
    }

    internal struct ParameterValue
    {
        public float Float;
        public int Int;
        public bool Bool;
    }

    /// An AnimatorController playing on one Animator: parameters, per-layer state machines, the
    /// properties bound from the controller's clips, and evaluation.
    ///
    /// Per update and layer: pending Play/CrossFade requests apply; state times advance
    /// (normalized: seconds × state speed × speed parameter / motion length); a running transition
    /// progresses (fixed: seconds / duration, otherwise source normalized time / duration) and may
    /// be interrupted by the source's or destination's transitions per its interruption source;
    /// otherwise Any State transitions, then the current state's, are tested in order. A
    /// transition fires when all its conditions hold and, with an exit time, when the state's
    /// normalized time crossed it during this update (every cycle when the exit time is below 1).
    /// Triggers used by the transition taken are consumed.
    ///
    /// Output: each state samples its clip (looping clips wrap, others clamp); properties a state
    /// does not animate get their default value (captured at bind time) when the state writes
    /// defaults, and are otherwise left as they are. Transitions blend linearly by progress;
    /// layers above 0 blend over the result by their weight.
    internal sealed class ControllerPlayback
    {
        public readonly ControllerData Data;
        public readonly ParameterValue[] Parameters;
        public readonly LayerPlayback[] Layers;

        readonly PropertyTarget[] targets;
        readonly int[] components;
        readonly float[] defaults;
        /// Per controller clip: curve index → property index (-1 = unbound).
        readonly int[][] curveToProperty;
        readonly float[][] samples;

        // Evaluation scratch.
        readonly float[] result, layerValues, src, dst;
        readonly bool[] resultWritten, layerWritten, srcWritten, dstWritten;

        public ControllerPlayback(ControllerData data, Transform root)
        {
            Data = data;
            Parameters = new ParameterValue[data.Parameters.Length];
            ResetParameters();

            // Bind every curve of every clip; identical properties share one slot.
            var binder = new AnimationBinder(root);
            var slots = new Dictionary<(PropertyTarget, int), int>();
            var targetList = new List<PropertyTarget>();
            var componentList = new List<int>();
            curveToProperty = new int[data.Clips.Length][];
            samples = new float[data.Clips.Length][];
            for (int c = 0; c < data.Clips.Length; c++)
            {
                var clip = !data.Clips[c] ? null : data.Clips[c].Native as AnimationClipNative;
                var curves = clip?.Curves ?? ClipCurves.Empty;
                var map = new int[curves.CurveCount];
                Array.Fill(map, -1);
                foreach (var b in curves.Bindings)
                {
                    var target = binder.Resolve(b);
                    if (target == null) continue;
                    for (int k = 0; k < b.Dimension; k++)
                    {
                        var key = (target, AnimationBinder.ComponentOf(b, k));
                        if (!slots.TryGetValue(key, out int slot))
                        {
                            slot = targetList.Count;
                            slots[key] = slot;
                            targetList.Add(target);
                            componentList.Add(key.Item2);
                        }
                        map[b.FirstCurve + k] = slot;
                    }
                }
                curveToProperty[c] = map;
                samples[c] = new float[curves.CurveCount];
            }
            targets = targetList.ToArray();
            components = componentList.ToArray();
            int n = targets.Length;
            defaults = new float[n];
            for (int i = 0; i < n; i++) defaults[i] = targets[i].Read(components[i]);
            result = new float[n];
            layerValues = new float[n];
            src = new float[n];
            dst = new float[n];
            resultWritten = new bool[n];
            layerWritten = new bool[n];
            srcWritten = new bool[n];
            dstWritten = new bool[n];

            Layers = new LayerPlayback[data.Layers.Length];
            for (int i = 0; i < Layers.Length; i++)
            {
                var l = data.Layers[i];
                var machine = data.StateMachines[l.StateMachineIndex];
                var layer = new LayerPlayback { Data = l, Machine = machine, Weight = i == 0 ? 1f : l.DefaultWeight };
                layer.Current = EntryState(layer);
                Layers[i] = layer;
            }
        }

        public void ResetParameters()
        {
            for (int i = 0; i < Parameters.Length; i++)
            {
                var p = Data.Parameters[i];
                Parameters[i] = new ParameterValue { Float = p.DefaultFloat, Int = p.DefaultInt, Bool = p.DefaultBool };
            }
        }

        /// The state a layer starts in: its root Entry node's transitions, else the default state.
        int EntryState(LayerPlayback layer)
        {
            var m = layer.Machine;
            for (int i = 0; i < m.Selectors.Length; i++)
                if (m.Selectors[i].IsEntry && m.Selectors[i].FullPathId == layer.Data.NameHash)
                {
                    int s = Resolve(m, TransitionData.SelectorFlag + (uint)i, 0);
                    if (s >= 0) return s;
                }
            return m.States.Length == 0 ? -1 : m.DefaultState;
        }

        // ---- Parameters and conditions ----

        bool Condition(ConditionData c, float stateTime)
        {
            if (c.Mode == ConditionMode.ExitTime) return stateTime >= c.ExitTime;
            int i = Data.ParameterIndex(c.EventId);
            if (i < 0) return false;
            var p = Parameters[i];
            bool isFloat = Data.Parameters[i].Type == ParameterType.Float;
            float value = isFloat ? p.Float : p.Int;
            return c.Mode switch
            {
                ConditionMode.If => p.Bool,
                ConditionMode.IfNot => !p.Bool,
                ConditionMode.Greater => value > c.Threshold,
                ConditionMode.Less => value < c.Threshold,
                ConditionMode.Equals => value == c.Threshold,
                ConditionMode.NotEqual => value != c.Threshold,
                _ => false,
            };
        }

        bool Conditions(ConditionData[] conditions, float stateTime)
        {
            foreach (var c in conditions)
                if (!Condition(c, stateTime)) return false;
            return true;
        }

        void ConsumeTriggers(ConditionData[] conditions)
        {
            foreach (var c in conditions)
            {
                int i = Data.ParameterIndex(c.EventId);
                if (i >= 0 && Data.Parameters[i].Type == ParameterType.Trigger) Parameters[i].Bool = false;
            }
        }

        /// A transition destination as a state index: Entry/Exit nodes follow their first
        /// transition whose conditions hold. -1 when nothing is reached.
        int Resolve(StateMachineData m, uint destination, int depth)
        {
            if (destination == uint.MaxValue || depth > 32) return -1;
            if (destination < TransitionData.SelectorFlag) return destination < (uint)m.States.Length ? (int)destination : -1;
            uint selector = destination - TransitionData.SelectorFlag;
            if (selector >= (uint)m.Selectors.Length) return -1;
            foreach (var t in m.Selectors[selector].Transitions)
                if (Conditions(t.Conditions, 0f))
                {
                    int s = Resolve(m, t.Destination, depth + 1);
                    if (s < 0) continue;
                    ConsumeTriggers(t.Conditions);
                    return s;
                }
            return -1;
        }

        // ---- Timing ----

        float MotionLength(LayerPlayback layer, int state)
        {
            int clip = state < 0 ? -1 : layer.Machine.States[state].ClipByMotionSet[layer.Data.MotionSetIndex];
            if (clip < 0 || !Data.Clips[clip] || Data.Clips[clip].Native is not AnimationClipNative c) return 1f; // an empty state lasts one second
            float length = c.Length;
            return length > 0f ? length : 1f;
        }

        float FloatParameter(uint id, float fallback)
        {
            if (id == 0) return fallback;
            int i = Data.ParameterIndex(id);
            return i >= 0 && Data.Parameters[i].Type == ParameterType.Float ? Parameters[i].Float : fallback;
        }

        public float SpeedMultiplier(StateData s) => FloatParameter(s.SpeedParamId, 1f);

        /// Seconds the state takes for one normalized unit at its current speed.
        public float StateDuration(LayerPlayback layer, int state)
        {
            var s = layer.Machine.States[state];
            float speed = Math.Abs(s.Speed * SpeedMultiplier(s));
            float length = MotionLength(layer, state);
            return speed > 0f ? length / speed : length;
        }

        /// Normalized time advanced by `seconds`.
        float Advance(LayerPlayback layer, int state, float seconds)
        {
            var s = layer.Machine.States[state];
            return seconds * s.Speed * SpeedMultiplier(s) / MotionLength(layer, state);
        }

        float StepTime(LayerPlayback layer, int state, float time, float seconds)
        {
            var s = layer.Machine.States[state];
            if (s.TimeParamId != 0 && Data.ParameterIndex(s.TimeParamId) >= 0) return FloatParameter(s.TimeParamId, time);
            return time + Advance(layer, state, seconds);
        }

        /// The exit-time point crossed in (prev, cur], or NaN. Exit times below 1 recur every cycle.
        static float ExitPoint(TransitionData t, float prev, float cur)
        {
            if (cur <= prev) return float.NaN;
            float e = t.ExitTime;
            float x = e < 1f ? MathF.Floor(prev - e) + 1f + e : e;
            return x > prev && x <= cur ? x : float.NaN;
        }

        // ---- Update ----

        public void Update(float seconds)
        {
            foreach (var layer in Layers)
                if (layer.Current >= 0) StepLayer(layer, seconds);
        }

        void StepLayer(LayerPlayback layer, float seconds)
        {
            if (layer.Pending != null)
            {
                var request = layer.Pending;
                layer.Pending = null;
                ApplyGoto(layer, request);
            }

            float curPrev = layer.CurrentTime;
            layer.CurrentTime = StepTime(layer, layer.Current, curPrev, seconds);
            if (layer.InTransition)
            {
                float nextPrev = layer.NextTime;
                layer.NextTime = StepTime(layer, layer.Next, nextPrev, seconds);
                if (layer.Duration <= 0f) layer.Progress = 1f;
                else layer.Progress += layer.DurationIsFixed ? seconds / layer.Duration : (layer.CurrentTime - curPrev) / layer.Duration;
                if (layer.Progress >= 1f) FinishTransition(layer);
                else TryInterrupt(layer, curPrev, nextPrev);
                return;
            }

            var m = layer.Machine;
            for (int i = 0; i < m.AnyStateTransitions.Length; i++)
                if (TryStart(layer, m.AnyStateTransitions[i], i, curPrev, anyState: true)) return;
            var transitions = m.States[layer.Current].Transitions;
            for (int i = 0; i < transitions.Length; i++)
                if (TryStart(layer, transitions[i], i, curPrev, anyState: false)) return;
        }

        /// Starts `t` from the current state if it fires in this update.
        bool TryStart(LayerPlayback layer, TransitionData t, int index, float prev, bool anyState)
        {
            float cur = layer.CurrentTime;
            float exit = float.NaN;
            if (t.HasExitTime)
            {
                exit = ExitPoint(t, prev, cur);
                if (float.IsNaN(exit)) return false;
            }
            if (!Conditions(t.Conditions, cur)) return false;
            int destination = Resolve(layer.Machine, t.Destination, 0);
            if (destination < 0) return false;
            if (anyState && !t.CanTransitionToSelf && destination == layer.Current) return false;
            ConsumeTriggers(t.Conditions);

            layer.Next = destination;
            layer.NextTime = t.Offset;
            layer.Transition = t;
            layer.TransitionIndex = index;
            layer.DurationIsFixed = t.HasFixedDuration;
            layer.Duration = t.Duration;
            layer.Progress = 0f;
            // The part of this update past the exit point already belongs to the transition.
            if (!float.IsNaN(exit))
            {
                float overshoot = cur - exit;
                float overshootSeconds = overshoot * StateDuration(layer, layer.Current);
                layer.NextTime = StepTime(layer, destination, layer.NextTime, overshootSeconds);
                layer.Progress = layer.Duration <= 0f ? 1f : (layer.DurationIsFixed ? overshootSeconds : overshoot) / layer.Duration;
            }
            else if (layer.Duration <= 0f) layer.Progress = 1f;
            if (layer.Progress >= 1f) FinishTransition(layer);
            return true;
        }

        void FinishTransition(LayerPlayback layer)
        {
            layer.Current = layer.Next;
            layer.CurrentTime = layer.NextTime;
            ClearTransition(layer);
        }

        static void ClearTransition(LayerPlayback layer)
        {
            layer.Next = -1;
            layer.Transition = null;
            layer.Progress = 0f;
            layer.Frozen = null;
            layer.FrozenWritten = null;
        }

        void TryInterrupt(LayerPlayback layer, float curPrev, float nextPrev)
        {
            var t = layer.Transition;
            if (t == null) return;
            switch (t.Interruption)
            {
                case InterruptionSource.Source:
                    InterruptFrom(layer, source: true, curPrev, nextPrev);
                    break;
                case InterruptionSource.Destination:
                    InterruptFrom(layer, source: false, curPrev, nextPrev);
                    break;
                case InterruptionSource.SourceThenDestination:
                    if (!InterruptFrom(layer, source: true, curPrev, nextPrev)) InterruptFrom(layer, source: false, curPrev, nextPrev);
                    break;
                case InterruptionSource.DestinationThenSource:
                    if (!InterruptFrom(layer, source: false, curPrev, nextPrev)) InterruptFrom(layer, source: true, curPrev, nextPrev);
                    break;
            }
        }

        /// Tests the source's (ordered: only higher-priority ones) or the destination's transitions;
        /// the first that fires replaces the running transition, which is frozen as its source.
        bool InterruptFrom(LayerPlayback layer, bool source, float curPrev, float nextPrev)
        {
            var active = layer.Transition;
            int owner = source ? layer.Current : layer.Next;
            float prev = source ? curPrev : nextPrev;
            float cur = source ? layer.CurrentTime : layer.NextTime;
            var list = layer.Machine.States[owner].Transitions;
            int end = source && active.OrderedInterruption ? Math.Min(layer.TransitionIndex, list.Length) : list.Length;
            for (int i = 0; i < end; i++)
            {
                var t = list[i];
                if (t == active) continue;
                if (t.HasExitTime && float.IsNaN(ExitPoint(t, prev, cur))) continue;
                if (!Conditions(t.Conditions, cur)) continue;
                int destination = Resolve(layer.Machine, t.Destination, 0);
                if (destination < 0) continue;
                ConsumeTriggers(t.Conditions);

                var frozen = new float[targets.Length];
                var frozenWritten = new bool[targets.Length];
                LayerPose(layer, frozen, frozenWritten);
                layer.Frozen = frozen;
                layer.FrozenWritten = frozenWritten;
                layer.Current = owner;
                layer.CurrentTime = cur;
                layer.Next = destination;
                layer.NextTime = t.Offset;
                layer.Transition = t;
                layer.TransitionIndex = i;
                layer.DurationIsFixed = t.HasFixedDuration;
                layer.Duration = t.Duration;
                layer.Progress = t.Duration <= 0f ? 1f : 0f;
                if (layer.Progress >= 1f) FinishTransition(layer);
                return true;
            }
            return false;
        }

        void ApplyGoto(LayerPlayback layer, GotoRequest r)
        {
            float time = r.Time;
            if (float.IsNegativeInfinity(time)) time = !r.CrossFade && layer.Current == r.State ? layer.CurrentTime : 0f;
            else if (r.TimeIsFixed) time /= StateDuration(layer, r.State);

            if (!r.CrossFade)
            {
                layer.Current = r.State;
                layer.CurrentTime = time;
                ClearTransition(layer);
                return;
            }
            if (layer.InTransition)
            {
                var frozen = new float[targets.Length];
                var frozenWritten = new bool[targets.Length];
                LayerPose(layer, frozen, frozenWritten);
                layer.Frozen = frozen;
                layer.FrozenWritten = frozenWritten;
                layer.Current = layer.Next;
                layer.CurrentTime = layer.NextTime;
            }
            layer.Next = r.State;
            layer.NextTime = time;
            layer.Transition = null;
            layer.DurationIsFixed = r.DurationIsFixed;
            layer.Duration = r.Duration;
            layer.Progress = r.Duration <= 0f ? 1f : Math.Clamp(r.Progress, 0f, 1f);
            if (layer.Progress >= 1f) FinishTransition(layer);
        }

        /// The layer containing a state with this hash (full path, path or short name) and its index.
        public bool FindState(int layerIndex, int hash, out int foundLayer, out int state)
        {
            uint h = unchecked((uint)hash);
            for (int l = layerIndex < 0 ? 0 : layerIndex; l < Layers.Length; l++)
            {
                var states = Layers[l].Machine.States;
                for (int s = 0; s < states.Length; s++)
                    if (states[s].FullPathId == h || states[s].NameId == h || states[s].PathId == h)
                    {
                        foundLayer = l;
                        state = s;
                        return true;
                    }
                if (layerIndex >= 0) break;
            }
            foundLayer = -1;
            state = -1;
            return false;
        }

        public AnimatorStateInfo StateInfo(LayerPlayback layer, int state, float time)
        {
            if (state < 0) return default;
            var s = layer.Machine.States[state];
            return new AnimatorStateInfo(s.NameId, s.PathId, s.FullPathId, time, StateDuration(layer, state), s.Speed, SpeedMultiplier(s), s.TagId, s.Loop);
        }

        // ---- Evaluation ----

        void StatePose(LayerPlayback layer, int state, float time, float[] values, bool[] written)
        {
            Array.Clear(written);
            var s = layer.Machine.States[state];
            int clip = s.ClipByMotionSet[layer.Data.MotionSetIndex];
            if (clip >= 0 && Data.Clips[clip] && Data.Clips[clip].Native is AnimationClipNative c)
            {
                var buffer = samples[clip];
                float offset = s.CycleOffsetParamId != 0 ? FloatParameter(s.CycleOffsetParamId, s.CycleOffset) : s.CycleOffset;
                c.Curves.Sample(c.ClipTime(time + offset, s.Loop), buffer);
                var map = curveToProperty[clip];
                for (int i = 0; i < map.Length; i++)
                {
                    int p = map[i];
                    if (p < 0) continue;
                    values[p] = buffer[i];
                    written[p] = true;
                }
            }
            if (s.WriteDefaultValues)
                for (int p = 0; p < values.Length; p++)
                    if (!written[p])
                    {
                        values[p] = defaults[p];
                        written[p] = true;
                    }
        }

        void LayerPose(LayerPlayback layer, float[] values, bool[] written)
        {
            if (!layer.InTransition)
            {
                StatePose(layer, layer.Current, layer.CurrentTime, values, written);
                return;
            }
            if (layer.Frozen != null)
            {
                Array.Copy(layer.Frozen, src, src.Length);
                Array.Copy(layer.FrozenWritten, srcWritten, srcWritten.Length);
            }
            else StatePose(layer, layer.Current, layer.CurrentTime, src, srcWritten);
            StatePose(layer, layer.Next, layer.NextTime, dst, dstWritten);
            float w = Math.Clamp(layer.Progress, 0f, 1f);
            for (int p = 0; p < values.Length; p++)
            {
                if (!srcWritten[p] && !dstWritten[p])
                {
                    written[p] = false;
                    continue;
                }
                float a = srcWritten[p] ? src[p] : Current(p);
                float b = dstWritten[p] ? dst[p] : Current(p);
                values[p] = a + (b - a) * w;
                written[p] = true;
            }
        }

        float Current(int p) => targets[p].Alive ? targets[p].Read(components[p]) : defaults[p];

        /// Computes the pose of all layers and writes it to the bound properties.
        public void Apply()
        {
            Array.Clear(resultWritten);
            for (int l = 0; l < Layers.Length; l++)
            {
                var layer = Layers[l];
                if (layer.Current < 0) continue;
                float weight = l == 0 ? 1f : layer.Weight;
                if (weight <= 0f) continue;
                LayerPose(layer, layerValues, layerWritten);
                for (int p = 0; p < layerValues.Length; p++)
                {
                    if (!layerWritten[p]) continue;
                    float under = resultWritten[p] ? result[p] : Current(p);
                    result[p] = weight >= 1f ? layerValues[p] : under + (layerValues[p] - under) * weight;
                    resultWritten[p] = true;
                }
            }
            Write(result, resultWritten);
        }

        /// Restores the values captured when the properties were bound.
        public void WriteDefaults()
        {
            var all = new bool[defaults.Length];
            Array.Fill(all, true);
            Write(defaults, all);
        }

        readonly List<PropertyTarget> staged = new();
        readonly HashSet<MonoBehaviour> notify = new(ReferenceEqualityComparer.Instance);

        void Write(float[] values, bool[] written)
        {
            staged.Clear();
            for (int p = 0; p < values.Length; p++)
            {
                if (!written[p] || !targets[p].Alive) continue;
                if (targets[p].Stage(components[p], values[p])) staged.Add(targets[p]);
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
    }
}
