using OniMoono.Engine.Particles;

namespace UnityEngine
{
    /// Modules mods build their effects with by script (ManualMiner's laser sparks), as measured
    /// in the original player (tools/UnityProbe ParticleEmit.cs, scenario particleemit).
    public partial class ParticleSystem
    {
        public LimitVelocityOverLifetimeModule limitVelocityOverLifetime => new(this);
        public RotationOverLifetimeModule rotationOverLifetime => new(this);
        public SizeOverLifetimeModule sizeOverLifetime => new(this);
        public TrailModule trails => new(this);

        public partial struct MainModule
        {
            public bool playOnAwake
            {
                get => m_ParticleSystem.Data.Config.PlayOnAwake;
                set => m_ParticleSystem.Data.Config.PlayOnAwake = value;
            }

            /// Custom space (a transform of the script's choosing) is not simulated: setting it
            /// keeps the system local, as a bundle's custom space is read (ParticleConfig).
            public ParticleSystemSimulationSpace simulationSpace
            {
                get => (ParticleSystemSimulationSpace)m_ParticleSystem.Data.Config.SimulationSpace;
                set => m_ParticleSystem.Data.Config.SimulationSpace = value == ParticleSystemSimulationSpace.World ? 1 : 0;
            }

            public MinMaxCurve startSpeed
            {
                get => MinMaxCurve.FromData(m_ParticleSystem.Data.Config.StartSpeed);
                set => m_ParticleSystem.Data.Config.StartSpeed = value.ToData();
            }
        }

        /// Slows particles faster than the limit: the speed over it shrinks by (1 - dampen) every
        /// 1/30 s (measured).
        public partial struct LimitVelocityOverLifetimeModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal LimitVelocityOverLifetimeModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            public bool enabled
            {
                get => m_ParticleSystem.Data.Config.LimitEnabled;
                set => m_ParticleSystem.Data.Config.LimitEnabled = value;
            }

            public MinMaxCurve limit
            {
                get => MinMaxCurve.FromData(m_ParticleSystem.Data.Config.Limit);
                set => m_ParticleSystem.Data.Config.Limit = value.ToData();
            }

            public float dampen
            {
                get => m_ParticleSystem.Data.Config.LimitDampen;
                set => m_ParticleSystem.Data.Config.LimitDampen = Mathf.Clamp01(value);
            }
        }

        /// Turns particles at an angular velocity over their lifetime (z only, as 2D particles turn).
        public partial struct RotationOverLifetimeModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal RotationOverLifetimeModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            public bool enabled
            {
                get => m_ParticleSystem.Data.Config.RotationEnabled;
                set => m_ParticleSystem.Data.Config.RotationEnabled = value;
            }

            public bool separateAxes
            {
                get => m_ParticleSystem.Data.Config.RotationSeparateAxes;
                set => m_ParticleSystem.Data.Config.RotationSeparateAxes = value;
            }

            public MinMaxCurve z
            {
                get => MinMaxCurve.FromData(m_ParticleSystem.Data.Config.RotationZ);
                set => m_ParticleSystem.Data.Config.RotationZ = value.ToData();
            }
        }

        public partial struct SizeOverLifetimeModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal SizeOverLifetimeModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            public bool enabled
            {
                get => m_ParticleSystem.Data.Config.SizeEnabled;
                set => m_ParticleSystem.Data.Config.SizeEnabled = value;
            }

            public MinMaxCurve size
            {
                get => MinMaxCurve.FromData(m_ParticleSystem.Data.Config.SizeX);
                set => m_ParticleSystem.Data.Config.SizeX = value.ToData();
            }
        }

        public partial struct TrailModule
        {
            internal ParticleSystem m_ParticleSystem;

            internal TrailModule(ParticleSystem particleSystem) => m_ParticleSystem = particleSystem;

            ParticleConfig Config => m_ParticleSystem.Data.Config;

            public bool enabled
            {
                get => Config.TrailsEnabled;
                set => Config.TrailsEnabled = value;
            }

            public float ratio
            {
                get => Config.TrailRatio;
                set => Config.TrailRatio = Mathf.Clamp01(value);
            }

            public MinMaxCurve lifetime
            {
                get => MinMaxCurve.FromData(Config.TrailLifetime);
                set => Config.TrailLifetime = value.ToData();
            }

            public float minVertexDistance
            {
                get => Config.TrailMinVertexDistance;
                set => Config.TrailMinVertexDistance = value;
            }

            public bool sizeAffectsWidth
            {
                get => Config.TrailSizeAffectsWidth;
                set => Config.TrailSizeAffectsWidth = value;
            }

            public bool inheritParticleColor
            {
                get => Config.TrailInheritParticleColor;
                set => Config.TrailInheritParticleColor = value;
            }

            public MinMaxCurve widthOverTrail
            {
                get => MinMaxCurve.FromData(Config.TrailWidthOverTrail);
                set => Config.TrailWidthOverTrail = value.ToData();
            }

            public MinMaxGradient colorOverTrail
            {
                get => MinMaxGradient.FromData(Config.TrailColorOverTrail);
                set => Config.TrailColorOverTrail = value.ToData();
            }
        }

        /// Overrides for the particles Emit(EmitParams, count) makes; what is not set comes from
        /// the modules as for emitted particles.
        public partial struct EmitParams
        {
            Vector3 m_Position, m_Velocity;
            float m_StartSize, m_StartLifetime, m_Rotation;
            Color32 m_StartColor;
            bool m_PositionSet, m_VelocitySet, m_StartSizeSet, m_StartLifetimeSet, m_StartColorSet, m_RotationSet, m_ApplyShapeToPosition;

            public Vector3 position { get => m_Position; set { m_Position = value; m_PositionSet = true; } }
            public Vector3 velocity { get => m_Velocity; set { m_Velocity = value; m_VelocitySet = true; } }
            public float startSize { get => m_StartSize; set { m_StartSize = value; m_StartSizeSet = true; } }
            public float startLifetime { get => m_StartLifetime; set { m_StartLifetime = value; m_StartLifetimeSet = true; } }
            public Color32 startColor { get => m_StartColor; set { m_StartColor = value; m_StartColorSet = true; } }
            /// Degrees.
            public float rotation { get => m_Rotation; set { m_Rotation = value; m_RotationSet = true; } }
            public bool applyShapeToPosition { get => m_ApplyShapeToPosition; set => m_ApplyShapeToPosition = value; }

            public void ResetPosition() => m_PositionSet = false;
            public void ResetVelocity() => m_VelocitySet = false;
            public void ResetStartSize() => m_StartSizeSet = false;
            public void ResetStartLifetime() => m_StartLifetimeSet = false;
            public void ResetStartColor() => m_StartColorSet = false;
            public void ResetRotation() => m_RotationSet = false;

            internal EmitOverrides ToOverrides() => new()
            {
                Position = m_PositionSet ? m_Position : null,
                Velocity = m_VelocitySet ? m_Velocity : null,
                StartSize = m_StartSizeSet ? m_StartSize : null,
                StartLifetime = m_StartLifetimeSet ? m_StartLifetime : null,
                StartColor = m_StartColorSet ? m_StartColor : null,
                RotationDegrees = m_RotationSet ? m_Rotation : null,
                ApplyShapeToPosition = m_ApplyShapeToPosition,
            };
        }

        public void Emit(EmitParams emitParams, int count) => Data.Emit(emitParams.ToOverrides(), count);
    }

    public partial class ParticleSystemRenderer
    {
        /// Mesh particles draw the bundle's mesh (m_Mesh): set to Mesh by script, a renderer without one draws nothing.
        public ParticleSystemRenderMode renderMode
        {
            get => (ParticleSystemRenderMode)((ParticleSystemRendererNative)EngineStateOrThrow).RenderMode;
            set => ((ParticleSystemRendererNative)EngineStateOrThrow).RenderMode = (int)value;
        }
    }
}
