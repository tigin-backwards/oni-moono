using System;
using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.Particles;

namespace UnityEngine
{
    public partial class ParticleSystem
    {
        internal ParticleSystemNative Data => (ParticleSystemNative)EngineStateOrThrow;

        public MainModule main => new(this);
        public EmissionModule emission => new(this);
        public ShapeModule shape => new(this);
        public SubEmittersModule subEmitters => new(this);

        /// Playing and not paused (true while a stopped system's last particles die).
        public bool isPlaying => Data.IsPlaying;
        public bool isEmitting => Data.IsEmitting;
        public bool isStopped => Data.IsStopped;
        public bool isPaused => Data.Paused;
        public int particleCount => Data.ParticleCount;

        /// Playback position in the current loop, in seconds.
        public float time => Data.Time;

        /// The seed used when useAutoRandomSeed is off; it takes effect at the next start.
        public uint randomSeed
        {
            get => Data.Config.RandomSeed;
            set => Data.Config.RandomSeed = value;
        }

        /// When on, each start of the system picks a new seed.
        public bool useAutoRandomSeed
        {
            get => Data.Config.AutoRandomSeed;
            set => Data.Config.AutoRandomSeed = value;
        }

        /// This system and, with children, the systems on child GameObjects, recursing only
        /// through children that have one.
        List<ParticleSystemNative> Systems(bool withChildren)
        {
            var self = Data;
            var list = new List<ParticleSystemNative> { self };
            if (withChildren) AddChildren(self.GameObject, list);
            return list;
        }

        static void AddChildren(GameObject go, List<ParticleSystemNative> list)
        {
            foreach (var child in go.Transform.Children)
                foreach (var c in child.GameObject.Components)
                    if (c is ParticleSystem && c.EngineState is ParticleSystemNative ps)
                    {
                        list.Add(ps);
                        AddChildren(child.GameObject, list);
                        break;
                    }
        }

        public void Play(bool withChildren)
        {
            foreach (var ps in Systems(withChildren)) ps.Play();
        }

        public void Play() => Play(true);

        public void Pause(bool withChildren)
        {
            foreach (var ps in Systems(withChildren)) ps.Pause();
        }

        public void Pause() => Pause(true);

        public void Stop(bool withChildren, ParticleSystemStopBehavior stopBehavior)
        {
            foreach (var ps in Systems(withChildren)) ps.Stop(stopBehavior);
        }

        public void Stop(bool withChildren) => Stop(withChildren, ParticleSystemStopBehavior.StopEmitting);
        public void Stop() => Stop(true);

        public void Clear(bool withChildren)
        {
            foreach (var ps in Systems(withChildren)) ps.Clear();
        }

        public void Clear() => Clear(true);

        /// Whether the system (and, with children, any child system) has live particles or
        /// will still emit.
        public bool IsAlive(bool withChildren)
        {
            foreach (var ps in Systems(withChildren))
                if (ps.IsAliveSelf) return true;
            return false;
        }

        public bool IsAlive() => IsAlive(true);

        /// Fast-forwards by t seconds and pauses (see ParticleSystemNative.Simulate).
        public void Simulate(float t, bool withChildren, bool restart, bool fixedTimeStep)
        {
            foreach (var ps in Systems(withChildren)) ps.Simulate(t, restart, fixedTimeStep);
        }

        public void Simulate(float t, bool withChildren, bool restart) => Simulate(t, withChildren, restart, true);
        public void Simulate(float t, bool withChildren) => Simulate(t, withChildren, true);
        public void Simulate(float t) => Simulate(t, true);

        // ---- Engine hooks ----

        /// AddComponent<ParticleSystem> also adds the ParticleSystemRenderer that draws it.
        internal override void EngineReset()
        {
            var go = Data.GameObject;
            foreach (var c in go.Components)
                if (c is ParticleSystemRenderer) return;
            Lifecycle.AddComponent(go, typeof(ParticleSystemRenderer));
        }

        /// The GameObject became active: the system is updated again, and starts if it plays
        /// on awake.
        internal override void EngineOnEnable()
        {
            var d = Data;
            d.StopActionPending = false;
            ParticleSystems.Add(d);
            if (d.Config.PlayOnAwake) d.Play();
        }

        /// Deactivation stops the system and removes its particles.
        internal override void EngineOnDisable()
        {
            var d = Data;
            ParticleSystems.Remove(d);
            d.Reset();
        }

        internal override void EngineOnDestroy() => ParticleSystems.Remove(Data);

        public partial struct MainModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal MainModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            public float duration => m_ParticleSystem.Data.Config.Duration;

            public bool loop
            {
                get => m_ParticleSystem.Data.Config.Looping;
                set => m_ParticleSystem.Data.Config.Looping = value;
            }

            public ParticleSystemStopAction stopAction
            {
                get => (ParticleSystemStopAction)m_ParticleSystem.Data.Config.StopAction;
                set => m_ParticleSystem.Data.Config.StopAction = (int)value;
            }
        }

        public partial struct EmissionModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal EmissionModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            public bool enabled
            {
                get => m_ParticleSystem.Data.Config.EmissionEnabled;
                set => m_ParticleSystem.Data.Config.EmissionEnabled = value;
            }
        }

        public partial struct ShapeModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal ShapeModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            /// Offset of the shape from the emitter, in emitter space.
            public Vector3 position
            {
                get => m_ParticleSystem.Data.Config.Shape.Position;
                set => m_ParticleSystem.Data.Config.Shape.Position = value;
            }

            public Vector3 scale
            {
                get => m_ParticleSystem.Data.Config.Shape.Scale;
                set => m_ParticleSystem.Data.Config.Shape.Scale = value;
            }
        }

        public partial struct SubEmittersModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal SubEmittersModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            /// Entries in the sub-emitter list, including ones with no system assigned.
            public int subEmittersCount => m_ParticleSystem.Data.Config.SubEmitters.Length;

            public ParticleSystem GetSubEmitterSystem(int index)
            {
                var list = m_ParticleSystem.Data.Config.SubEmitters;
                if ((uint)index >= (uint)list.Length) throw new ArgumentOutOfRangeException(nameof(index));
                return list[index].Emitter;
            }
        }
    }
}
