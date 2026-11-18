using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine
{
    /// MonoBehaviour.Invoke and InvokeRepeating, as measured in the original player (PerfProbe
    /// measure.txt): a call is due when the scaled game time reaches it; due calls run after
    /// Update, before coroutines resume, and again after LateUpdate (one made in between, such as
    /// a delay of zero from a coroutine, runs then, in the same frame). They run whether the
    /// behaviour is enabled and its GameObject active or not, until it is destroyed or the call
    /// cancelled. A repeating call is due again its rate after its last due time. A method of that
    /// name that cannot be found is skipped without a message.
    internal static class DelayedCalls
    {
        sealed class Call
        {
            public MonoBehaviour Target;
            public string Method;
            public double Due;
            public float Rate; // < 0: once
            public bool Cancelled;
        }

        static readonly List<Call> calls = new();

        public static void Add(MonoBehaviour target, string method, float delay, float rate)
        {
            calls.Add(new Call { Target = target, Method = method, Due = Time.timeAsDouble + Math.Max(0f, delay), Rate = rate });
        }

        public static void Cancel(MonoBehaviour target, string method)
        {
            foreach (var c in calls)
                if (ReferenceEquals(c.Target, target) && (method == null || c.Method == method)) c.Cancelled = true;
            calls.RemoveAll(c => c.Cancelled);
        }

        public static bool IsPending(MonoBehaviour target, string method)
        {
            foreach (var c in calls)
                if (!c.Cancelled && ReferenceEquals(c.Target, target) && (method == null || c.Method == method)) return true;
            return false;
        }

        /// Runs the calls due now: each at most once a pass, so a call made or repeated during
        /// the pass waits for the next.
        public static void Run()
        {
            if (calls.Count == 0) return;
            double now = Time.timeAsDouble;
            var due = new List<Call>();
            foreach (var c in calls)
                if (!c.Cancelled && c.Due <= now) due.Add(c);
            foreach (var c in due)
            {
                if (c.Cancelled) continue;
                if (!c.Target) { c.Cancelled = true; continue; }
                if (c.Rate < 0f) c.Cancelled = true;
                else c.Due += Math.Max(c.Rate, 1e-5f);
                var method = ScriptInfo.FindByName(c.Target.GetType(), c.Method, null, false);
                if (method == null || method.GetParameters().Length != 0) continue;
                try
                {
                    method.Invoke(c.Target, null);
                }
                catch (System.Reflection.TargetInvocationException e)
                {
                    Debug.LogException(e.InnerException ?? e, c.Target);
                }
            }
            calls.RemoveAll(c => c.Cancelled || (object)c.Target == null || !c.Target);
        }
    }
}
