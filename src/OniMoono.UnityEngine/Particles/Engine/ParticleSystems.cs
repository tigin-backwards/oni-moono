using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace OniMoono.Engine.Particles
{
    /// The particle systems to update each frame: those whose GameObject is active (Unity's
    /// ParticleSystem manager). Updated in PostLateUpdate.ParticleSystemEndUpdateAll, after
    /// LateUpdate and before rendering; stop actions run after the update.
    ///
    /// Every system is simulated whatever its culling mode: off-screen pausing would only
    /// change what is seen once the system comes back into view, and needs visibility the
    /// renderer reports a frame late.
    internal static class ParticleSystems
    {
        static readonly List<ParticleSystemNative> systems = new();

        static ParticleSystems() => PlayerLoop.ParticleUpdate += Run;

        public static void Add(ParticleSystemNative ps)
        {
            if (!systems.Contains(ps)) systems.Add(ps);
        }

        public static void Remove(ParticleSystemNative ps) => systems.Remove(ps);

        static readonly ListSnapshot<ParticleSystemNative> runSnapshot = new();

        static void Run()
        {
            if (systems.Count == 0) return;
            using var snapshot = runSnapshot.Take(systems);
            var batch = snapshot.Items;
            foreach (var ps in batch)
            {
                if (ps.Destroyed || !ps.GameObject.ActiveInHierarchy) continue;
                try
                {
                    ps.FrameUpdate();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, ps.Managed);
                }
            }
            foreach (var ps in batch)
                if (ps.StopActionPending && !ps.Destroyed && ps.GameObject.ActiveInHierarchy) RunStopAction(ps);
        }

        /// Disable deactivates the GameObject, Destroy destroys it, Callback sends
        /// OnParticleSystemStopped to its scripts.
        static void RunStopAction(ParticleSystemNative ps)
        {
            ps.StopActionPending = false;
            var go = (GameObject)ps.GameObject.Managed;
            switch ((ParticleSystemStopAction)ps.Config.StopAction)
            {
                case ParticleSystemStopAction.Disable:
                    go.SetActive(false);
                    break;
                case ParticleSystemStopAction.Destroy:
                    UnityEngine.Object.Destroy(go);
                    break;
                case ParticleSystemStopAction.Callback:
                    SendStopped(ps.GameObject);
                    break;
            }
        }

        static readonly ConcurrentDictionary<Type, Action<MonoBehaviour>> stoppedMessages = new();

        static void SendStopped(GameObjectNative go)
        {
            foreach (var c in go.Components)
            {
                if (c is not MonoBehaviour mb || c.Native is not BehaviourNative { IsActiveAndEnabledState: true }) continue;
                var message = stoppedMessages.GetOrAdd(mb.GetType(), FindStopped);
                if (message != null) Lifecycle.Invoke(message, mb);
            }
        }

        /// The most-derived parameterless instance method named OnParticleSystemStopped.
        static Action<MonoBehaviour> FindStopped(Type type)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                var m = t.GetMethod("OnParticleSystemStopped", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null)
                    return target =>
                    {
                        try
                        {
                            m.Invoke(target, null);
                        }
                        catch (TargetInvocationException e) when (e.InnerException != null)
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException);
                        }
                    };
            }
            return null;
        }
    }
}
