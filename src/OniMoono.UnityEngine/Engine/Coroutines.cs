using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace OniMoono.Engine
{
    internal enum CoroutineWait { None, NextFrame, Seconds, FixedUpdate, EndOfFrame, Child, AsyncOperation }

    internal sealed class CoroutineState
    {
        public MonoBehaviour Owner;
        public BehaviourNative OwnerNative;
        public IEnumerator Root;
        public string MethodName; // when started by name
        public readonly Stack<IEnumerator> Stack = new();
        public bool Done;
        public Coroutine Handle;
        public CoroutineWait Wait;
        public float ResumeTime;
        /// The frame in which the current wait began.
        public int WaitFrame;
        public AsyncOperation Operation;
        public List<CoroutineState> Waiters;
    }

    /// Unity's coroutine scheduler.
    ///
    /// A coroutine runs synchronously from StartCoroutine until its first yield. Yielded
    /// IEnumerators (including CustomYieldInstructions) run nested, inline. `null` (and any
    /// unrecognized value) resumes after the next frame's Update, even when the coroutine
    /// started waiting before this frame's coroutine pass (started from Update or a late Start:
    /// observed in Unity's player); WaitForSeconds resumes after Update once scaled time has
    /// passed, from the next frame on; WaitForFixedUpdate after FixedUpdate; WaitForEndOfFrame
    /// at the end of the frame; a Coroutine when that coroutine finishes; an AsyncOperation after
    /// Update once it is done. An exception ends the coroutine and is logged.
    internal static class Coroutines
    {
        static List<CoroutineState> updateWaiters = new();
        static List<CoroutineState> fixedWaiters = new();
        static List<CoroutineState> endOfFrameWaiters = new();

        // Not inlined: LogTraces reads its frame (Unity's StartCoroutineManaged2).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Coroutine Start(MonoBehaviour owner, IEnumerator routine, string methodName = null)
        {
            if (routine == null) throw new NullReferenceException("routine is null");
            var bn = (BehaviourNative)owner.NativeOrThrow;
            if (!bn.GameObject.ActiveInHierarchy)
            {
                Debug.LogError($"Coroutine couldn't be started because the the game object '{bn.GameObject.Name}' is inactive!", owner);
                return null;
            }
            var state = new CoroutineState { Owner = owner, OwnerNative = bn, Root = routine, MethodName = methodName };
            state.Stack.Push(routine);
            state.Handle = new Coroutine(state);
            (bn.Coroutines ??= new List<CoroutineState>()).Add(state);
            Advance(state);
            return state.Handle;
        }

        /// Runs the coroutine until it yields something that makes it wait, or finishes.
        static void Advance(CoroutineState state)
        {
            while (!state.Done)
            {
                if (state.Owner.Native == null) { Finish(state); return; }
                var top = state.Stack.Peek();
                bool moved;
                try
                {
                    moved = Step(top);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, state.Owner);
                    Finish(state);
                    return;
                }
                if (state.Done) return; // stopped from inside its own MoveNext
                if (!moved)
                {
                    state.Stack.Pop();
                    if (state.Stack.Count == 0) { Finish(state); return; }
                    continue;
                }
                switch (top.Current)
                {
                    case null:
                        WaitIn(updateWaiters, state, CoroutineWait.NextFrame);
                        return;
                    case WaitForSeconds ws:
                        state.ResumeTime = Time.time + ws.m_Seconds;
                        WaitIn(updateWaiters, state, CoroutineWait.Seconds);
                        return;
                    case WaitForFixedUpdate:
                        WaitIn(fixedWaiters, state, CoroutineWait.FixedUpdate);
                        return;
                    case WaitForEndOfFrame:
                        WaitIn(endOfFrameWaiters, state, CoroutineWait.EndOfFrame);
                        return;
                    case Coroutine child:
                        var childState = child.State;
                        if (childState == null || childState.Done) continue;
                        state.Wait = CoroutineWait.Child;
                        (childState.Waiters ??= new List<CoroutineState>()).Add(state);
                        return;
                    case AsyncOperation op:
                        state.Operation = op;
                        WaitIn(updateWaiters, state, CoroutineWait.AsyncOperation);
                        return;
                    case IEnumerator nested:
                        state.Stack.Push(nested);
                        continue;
                    default:
                        WaitIn(updateWaiters, state, CoroutineWait.NextFrame);
                        return;
                }
            }
        }

        /// A step: Unity's SetupCoroutine.InvokeMoveNext in logged stack traces (LogTraces). Without
        /// profile-guided optimization (AggressiveOptimization) the JIT never inlines a hot
        /// MoveNext here, so the step keeps its frame in its exceptions' traces, as on Mono.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static bool Step(IEnumerator routine) => routine.MoveNext();

        static void WaitIn(List<CoroutineState> list, CoroutineState state, CoroutineWait wait)
        {
            state.Wait = wait;
            state.WaitFrame = Time.frameCount;
            list.Add(state);
        }

        static void Finish(CoroutineState state)
        {
            if (state.Done) return;
            state.Done = true;
            state.Wait = CoroutineWait.None;
            state.OwnerNative.Coroutines?.Remove(state);
            var waiters = state.Waiters;
            state.Waiters = null;
            if (waiters == null) return;
            foreach (var w in waiters)
                if (!w.Done && w.Wait == CoroutineWait.Child)
                {
                    w.Wait = CoroutineWait.None;
                    Advance(w);
                }
        }

        public static void Stop(CoroutineState state)
        {
            if (state == null || state.Done) return;
            Finish(state);
        }

        public static void StopAll(MonoBehaviour owner, BehaviourNative bn)
        {
            if (bn.Coroutines == null) return;
            foreach (var s in bn.Coroutines.ToArray()) Finish(s);
        }

        public static void StopByEnumerator(BehaviourNative bn, IEnumerator routine)
        {
            if (bn.Coroutines == null) return;
            foreach (var s in bn.Coroutines.ToArray())
                if (ReferenceEquals(s.Root, routine)) Finish(s);
        }

        public static void StopByName(BehaviourNative bn, string methodName)
        {
            if (bn.Coroutines == null) return;
            foreach (var s in bn.Coroutines.ToArray())
                if (s.MethodName == methodName) Finish(s);
        }

        /// Resumes waiters of one phase. Coroutines that start waiting during the pass wait for
        /// the next pass.
        static void RunPhase(ref List<CoroutineState> waiters, Func<CoroutineState, bool> ready)
        {
            if (waiters.Count == 0) return;
            var current = waiters;
            waiters = new List<CoroutineState>();
            foreach (var s in current)
            {
                if (s.Done) continue;
                if (!ready(s))
                {
                    waiters.Add(s);
                    continue;
                }
                s.Wait = CoroutineWait.None;
                s.Operation = null;
                Advance(s);
            }
        }

        /// After Update: yield null, WaitForSeconds, AsyncOperation; never in the frame the wait
        /// began.
        public static void RunUpdatePhase() => RunPhase(ref updateWaiters, s => s.WaitFrame < Time.frameCount && s.Wait switch
        {
            CoroutineWait.Seconds => Time.time >= s.ResumeTime,
            CoroutineWait.AsyncOperation => s.Operation.IsCompleted,
            _ => true,
        });

        public static void RunFixedUpdatePhase() => RunPhase(ref fixedWaiters, _ => true);
        public static void RunEndOfFramePhase() => RunPhase(ref endOfFrameWaiters, _ => true);
    }
}
