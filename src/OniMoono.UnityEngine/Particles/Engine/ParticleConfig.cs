using System;
using System.IO;
using System.Linq;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;
using static OniMoono.Engine.Particles.ParticleFields;

namespace OniMoono.Engine.Particles
{
    /// ParticleSystemShapeType values the simulation implements.
    internal static class ShapeKind
    {
        public const int Sphere = 0, Cone = 4, Box = 5, Circle = 10, MeshRenderer = 13;
    }

    /// A burst of the emission module: count particles at time (seconds into each loop),
    /// repeated cycleCount times (0 = forever) every repeatInterval seconds, each firing with
    /// the given probability.
    internal sealed class BurstData
    {
        public float Time;
        public MinMaxCurveData Count = MinMaxCurveData.Const(30f);
        public int CycleCount = 1;
        public float RepeatInterval = 0.01f;
        public float Probability = 1f;
    }

    /// One stream of the custom data module (Custom1 or Custom2): ParticleSystemCustomDataMode
    /// 0 Disabled, 1 Vector (Components curves, the rest 0), 2 Color (a gradient), each over the
    /// particle's normalized age (measured: UnityProbe particlestreams, j0010421).
    internal sealed class CustomDataStream
    {
        public int Mode;
        public int Components = 4;
        public MinMaxCurveData[] Vector = [MinMaxCurveData.Const(0f), MinMaxCurveData.Const(0f), MinMaxCurveData.Const(0f), MinMaxCurveData.Const(0f)];
        public MinMaxGradientData Color = MinMaxGradientData.White();
    }

    internal sealed class SubEmitterData
    {
        public ParticleSystem Emitter;
        /// ParticleSystemSubEmitterType: 0 Birth, 1 Collision, 2 Death, 3 Trigger, 4 Manual.
        public int Type;
        public int Properties;
        public float EmitProbability = 1f;
    }

    /// The shape module. Positions and directions are generated in shape space (shape
    /// position/rotation/scale), then taken to the emitter.
    internal sealed class ShapeData
    {
        public bool Enabled = true;
        public int Type = ShapeKind.Cone;
        public float Angle = 25f;
        public float Length = 5f;
        public float Radius = 1f;
        public float RadiusThickness = 1f;
        public float Arc = 360f;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = Vector3.one;
        public bool AlignToDirection;
        public float RandomDirectionAmount;
        public float SphericalDirectionAmount;
        public float RandomPositionAmount;

        public ShapeData Clone() => (ShapeData)MemberwiseClone();
    }

    /// Everything a ParticleSystem component stores (its modules). Field defaults are Unity's
    /// for a ParticleSystem created by AddComponent. Curves and gradients are immutable once
    /// loaded and shared between clones; the values scripts can change are copied.
    internal sealed class ParticleConfig
    {
        // Main module (and the component's own fields).
        public float Duration = 5f;
        public bool Looping = true;
        public bool Prewarm;
        public bool PlayOnAwake = true;
        public bool UseUnscaledTime;
        public bool AutoRandomSeed = true;
        public uint RandomSeed;
        public float SimulationSpeed = 1f;
        /// ParticleSystemStopAction.
        public int StopAction;
        /// ParticleSystemCullingMode: 0 Automatic, 1 PauseAndCatchup, 2 Pause, 3 AlwaysSimulate.
        public int CullingMode;
        /// ParticleSystemEmitterVelocityMode: 0 Transform, 1 Rigidbody, 2 Custom.
        public int EmitterVelocityMode = 1;
        public Vector3 CustomEmitterVelocity;
        /// ParticleSystemSimulationSpace: 0 Local, 1 World.
        public int SimulationSpace;
        /// ParticleSystemScalingMode: 0 Hierarchy, 1 Local, 2 Shape.
        public int ScalingMode = 1;
        public MinMaxCurveData StartDelay = MinMaxCurveData.Const(0f);
        public MinMaxCurveData StartLifetime = MinMaxCurveData.Const(5f);
        public MinMaxCurveData StartSpeed = MinMaxCurveData.Const(5f);
        public bool Size3D;
        public MinMaxCurveData StartSizeX = MinMaxCurveData.Const(1f);
        public MinMaxCurveData StartSizeY = MinMaxCurveData.Const(1f);
        public MinMaxCurveData StartSizeZ = MinMaxCurveData.Const(1f);
        public bool Rotation3D;
        /// Radians, as stored.
        public MinMaxCurveData StartRotationX = MinMaxCurveData.Const(0f);
        public MinMaxCurveData StartRotationY = MinMaxCurveData.Const(0f);
        public MinMaxCurveData StartRotationZ = MinMaxCurveData.Const(0f);
        public float RandomizeRotationDirection;
        public MinMaxGradientData StartColor = MinMaxGradientData.White();
        /// ParticleSystemGravitySource: 0 Physics3D, 1 Physics2D.
        public int GravitySource;
        public MinMaxCurveData GravityModifier = MinMaxCurveData.Const(0f);
        public int MaxParticles = 1000;

        public ShapeData Shape = new();

        // Emission.
        public bool EmissionEnabled = true;
        public MinMaxCurveData RateOverTime = MinMaxCurveData.Const(10f);
        public MinMaxCurveData RateOverDistance = MinMaxCurveData.Const(0f);

        // Noise module: not simulated (no game data enables it); its settings are kept for
        // scripts that read them back.
        public bool NoiseEnabled;
        public MinMaxCurveData NoiseStrength = MinMaxCurveData.Const(1f);
        public BurstData[] Bursts = Array.Empty<BurstData>();

        // Size over lifetime.
        public bool SizeEnabled;
        public bool SizeSeparateAxes;
        /// A new component's: a curve from 0 to 1 over the lifetime (measured).
        public MinMaxCurveData SizeX = LinearRise(), SizeY = LinearRise(), SizeZ = LinearRise();

        // Limit velocity over lifetime (scripts only: a bundle's module is not read). A new
        // component's limit is 1 and its dampen 0 (measured).
        public bool LimitEnabled;
        public MinMaxCurveData Limit = MinMaxCurveData.Const(1f);
        /// Kept within [0, 1], as Unity keeps it.
        public float LimitDampen;

        // Rotation over lifetime (scripts only, z): radians a second, 45° on a new component.
        public bool RotationEnabled;
        public bool RotationSeparateAxes;
        public MinMaxCurveData RotationZ = MinMaxCurveData.Const(0.7853982f);

        // Color over lifetime.
        public bool ColorEnabled;
        public MinMaxGradientData ColorOverLifetime = MinMaxGradientData.White();

        // Color by speed.
        public bool ColorBySpeedEnabled;
        public MinMaxGradientData ColorBySpeed;
        public Vector2 ColorBySpeedRange;

        // Force over lifetime.
        public bool ForceEnabled;
        public MinMaxCurveData ForceX, ForceY, ForceZ;
        public bool ForceInWorldSpace;
        public bool ForceRandomizePerFrame;

        // Texture sheet animation (grid mode, whole sheet).
        public bool UvEnabled;
        /// ParticleSystemAnimationTimeMode: 0 Lifetime, 2 FPS.
        public int UvTimeMode;
        public float UvFps;
        public MinMaxCurveData FrameOverTime;
        public MinMaxCurveData StartFrame;
        public int TilesX = 1, TilesY = 1;
        public float Cycles = 1f;

        // Trails (per-particle mode, stretched texture).
        // A new component's values are measured ones.
        public bool TrailsEnabled;
        public float TrailRatio = 1f;
        public MinMaxCurveData TrailLifetime = MinMaxCurveData.Const(1f);
        public float TrailMinVertexDistance = 0.2f;
        public bool TrailDieWithParticles = true;
        public bool TrailSizeAffectsWidth = true;
        public bool TrailSizeAffectsLifetime;
        public bool TrailInheritParticleColor = true;
        public MinMaxGradientData TrailColorOverLifetime = MinMaxGradientData.White();
        public MinMaxCurveData TrailWidthOverTrail = MinMaxCurveData.Const(1f);
        public MinMaxGradientData TrailColorOverTrail = MinMaxGradientData.White();

        // Custom data (Custom1, Custom2), fed to the shaders by custom vertex streams.
        public bool CustomDataEnabled;
        public CustomDataStream[] CustomData = [new(), new()];

        // Sub emitters (listed for the API; the module itself is never enabled in the data).
        public SubEmitterData[] SubEmitters = Array.Empty<SubEmitterData>();

        static MinMaxCurveData LinearRise() => new()
        {
            Mode = MinMaxCurveData.Curve,
            Scalar = 1f,
            MaxCurve = new AnimationCurve(new Keyframe(0f, 0f, 0f, 1f), new Keyframe(1f, 1f, 1f, 0f)),
        };

        public ParticleConfig Clone()
        {
            var c = (ParticleConfig)MemberwiseClone();
            c.Shape = Shape.Clone();
            c.Bursts = (BurstData[])Bursts.Clone();
            c.CustomData = (CustomDataStream[])CustomData.Clone();
            c.SubEmitters = SubEmitters.Select(s => new SubEmitterData { Emitter = s.Emitter, Type = s.Type, Properties = s.Properties, EmitProbability = s.EmitProbability }).ToArray();
            return c;
        }

        /// ParticleSystem (class 198). Modules the simulation does not implement are rejected
        /// when enabled, so unsupported content fails at load instead of rendering wrongly.
        public static ParticleConfig Read(Fields f, IReferenceResolver resolver, string owner)
        {
            // Features the game's data never uses are not simulated (mods' effects may use them):
            // each is logged and the system runs without it, as described where it is met.
            void Unsupported(string what) => EngineLog.Engine($"particles: ParticleSystem on {owner}: {what} is not simulated");

            var c = new ParticleConfig
            {
                // Unity clamps the duration to at least 0.05 s.
                Duration = Math.Max(0.05f, F(f, "lengthInSec")),
                SimulationSpeed = F(f, "simulationSpeed"),
                StopAction = I(f, "stopAction"),
                CullingMode = I(f, "cullingMode"),
                // Bundles from before Unity 2022 (a mod's) store the mode as useRigidbodyForVelocity,
                // when there were only these two modes.
                EmitterVelocityMode = f.Contains("emitterVelocityMode") ? I(f, "emitterVelocityMode") : B(f, "useRigidbodyForVelocity") ? 1 : 0,
                Looping = B(f, "looping"),
                Prewarm = B(f, "prewarm"),
                PlayOnAwake = B(f, "playOnAwake"),
                UseUnscaledTime = B(f, "useUnscaledTime"),
                AutoRandomSeed = B(f, "autoRandomSeed"),
                StartDelay = MinMaxCurveData.Read(f.Struct("startDelay")),
                SimulationSpace = I(f, "moveWithTransform"),
                ScalingMode = I(f, "scalingMode"),
                RandomSeed = unchecked((uint)I(f, "randomSeed")),
            };
            if (I(f, "ringBufferMode") != 0) Unsupported("ring buffer mode");
            if (c.SimulationSpace is not (0 or 1))
            {
                Unsupported("custom simulation space (simulated in local space)");
                c.SimulationSpace = 0;
            }

            var init = f.Struct("InitialModule");
            c.StartLifetime = MinMaxCurveData.Read(init.Struct("startLifetime"));
            c.StartSpeed = MinMaxCurveData.Read(init.Struct("startSpeed"));
            c.StartColor = MinMaxGradientData.Read(init.Struct("startColor"));
            c.StartSizeX = MinMaxCurveData.Read(init.Struct("startSize"));
            c.StartSizeY = MinMaxCurveData.Read(init.Struct("startSizeY"));
            c.StartSizeZ = MinMaxCurveData.Read(init.Struct("startSizeZ"));
            c.StartRotationX = MinMaxCurveData.Read(init.Struct("startRotationX"));
            c.StartRotationY = MinMaxCurveData.Read(init.Struct("startRotationY"));
            c.StartRotationZ = MinMaxCurveData.Read(init.Struct("startRotation"));
            c.RandomizeRotationDirection = F(init, "randomizeRotationDirection");
            // Older bundles predate 2D-physics gravity and custom emitter velocity.
            c.GravitySource = init.Contains("gravitySource") ? I(init, "gravitySource") : 0;
            c.MaxParticles = Math.Max(0, I(init, "maxNumParticles"));
            c.CustomEmitterVelocity = init.Contains("customEmitterVelocity") ? V3(init, "customEmitterVelocity") : Vector3.zero;
            c.Size3D = B(init, "size3D");
            c.Rotation3D = B(init, "rotation3D");
            c.GravityModifier = MinMaxCurveData.Read(init.Struct("gravityModifier"));
            // The project's 2D gravity is not modelled; no data combines it with a non-zero modifier.
            if (c.GravitySource != 0 && !c.GravityModifier.IsZero) Unsupported("2D-physics gravity (3D gravity used)");

            var shape = f.Struct("ShapeModule");
            var s = c.Shape = new ShapeData
            {
                Enabled = B(shape, "enabled"),
                Type = I(shape, "type"),
                Angle = F(shape, "angle"),
                Length = F(shape, "length"),
                RadiusThickness = F(shape, "radiusThickness"),
                Position = V3(shape, "m_Position"),
                Rotation = V3(shape, "m_Rotation"),
                Scale = V3(shape, "m_Scale"),
                AlignToDirection = B(shape, "alignToDirection"),
                RandomDirectionAmount = F(shape, "randomDirectionAmount"),
                SphericalDirectionAmount = F(shape, "sphericalDirectionAmount"),
                RandomPositionAmount = F(shape, "randomPositionAmount"),
                Radius = F(shape.Struct("radius"), "value"),
                Arc = F(shape.Struct("arc"), "value"),
            };
            if (s.Enabled)
            {
                switch (s.Type)
                {
                    case ShapeKind.Box:
                        break;
                    case ShapeKind.Sphere:
                    case ShapeKind.Cone:
                    case ShapeKind.Circle:
                        // Only random placement around the arc/radius (mode Random).
                        if (I(shape.Struct("radius"), "mode") != 0 || I(shape.Struct("arc"), "mode") != 0) Unsupported("shape radius/arc modes other than Random (Random used)");
                        break;
                    case ShapeKind.MeshRenderer:
                        // With no MeshRenderer assigned the shape has nothing to sample, and Unity
                        // emits from the emitter origin along +Z (the shape module is skipped).
                        if (!shape.PPtr("m_MeshRenderer").IsNull) Unsupported("mesh renderer shapes (emitted from the origin)");
                        break;
                    default:
                        Unsupported($"shape type {s.Type} (emitted from the origin)");
                        s.Enabled = false;
                        break;
                }
            }

            var emission = f.Struct("EmissionModule");
            c.EmissionEnabled = B(emission, "enabled");
            c.RateOverTime = MinMaxCurveData.Read(emission.Struct("rateOverTime"));
            c.RateOverDistance = MinMaxCurveData.Read(emission.Struct("rateOverDistance"));
            var bursts = emission.Array("m_Bursts");
            int burstCount = Math.Min(I(emission, "m_BurstCount"), bursts.Length);
            c.Bursts = new BurstData[burstCount];
            for (int i = 0; i < burstCount; i++)
            {
                var b = (Fields)bursts[i];
                c.Bursts[i] = new BurstData
                {
                    Time = F(b, "time"),
                    Count = MinMaxCurveData.Read(b.Struct("countCurve")),
                    CycleCount = I(b, "cycleCount"),
                    // Unity keeps the interval above 0.0001 s.
                    RepeatInterval = Math.Max(0.0001f, F(b, "repeatInterval")),
                    Probability = F(b, "probability"),
                };
            }

            var size = f.Struct("SizeModule");
            c.SizeEnabled = B(size, "enabled");
            c.SizeX = MinMaxCurveData.Read(size.Struct("curve"));
            c.SizeY = MinMaxCurveData.Read(size.Struct("y"));
            c.SizeZ = MinMaxCurveData.Read(size.Struct("z"));
            c.SizeSeparateAxes = B(size, "separateAxes");

            var color = f.Struct("ColorModule");
            c.ColorEnabled = B(color, "enabled");
            c.ColorOverLifetime = MinMaxGradientData.Read(color.Struct("gradient"));

            var colorBySpeed = f.Struct("ColorBySpeedModule");
            c.ColorBySpeedEnabled = B(colorBySpeed, "enabled");
            c.ColorBySpeed = MinMaxGradientData.Read(colorBySpeed.Struct("gradient"));
            c.ColorBySpeedRange = V2(colorBySpeed, "range");

            var force = f.Struct("ForceModule");
            c.ForceEnabled = B(force, "enabled");
            c.ForceX = MinMaxCurveData.Read(force.Struct("x"));
            c.ForceY = MinMaxCurveData.Read(force.Struct("y"));
            c.ForceZ = MinMaxCurveData.Read(force.Struct("z"));
            c.ForceInWorldSpace = B(force, "inWorldSpace");
            c.ForceRandomizePerFrame = B(force, "randomizePerFrame");

            var uv = f.Struct("UVModule");
            c.UvEnabled = B(uv, "enabled");
            c.UvTimeMode = I(uv, "timeMode");
            c.UvFps = F(uv, "fps");
            c.FrameOverTime = MinMaxCurveData.Read(uv.Struct("frameOverTime"));
            c.StartFrame = MinMaxCurveData.Read(uv.Struct("startFrame"));
            c.TilesX = Math.Max(1, I(uv, "tilesX"));
            c.TilesY = Math.Max(1, I(uv, "tilesY"));
            c.Cycles = F(uv, "cycles");
            if (c.UvEnabled)
            {
                string unsupported = I(uv, "mode") != 0 ? "sprite-mode texture sheet animation"
                    : I(uv, "animationType") != 0 ? "single-row texture sheet animation"
                    : c.UvTimeMode is not (0 or 2) ? "speed-driven texture sheet animation"
                    : F(uv, "flipU") != 0f || F(uv, "flipV") != 0f ? "texture sheet UV flipping" : null;
                if (unsupported != null)
                {
                    Unsupported(unsupported + " (the module is off)");
                    c.UvEnabled = false;
                }
            }

            var trail = f.Struct("TrailModule");
            c.TrailsEnabled = B(trail, "enabled");
            c.TrailRatio = F(trail, "ratio");
            c.TrailLifetime = MinMaxCurveData.Read(trail.Struct("lifetime"));
            c.TrailMinVertexDistance = F(trail, "minVertexDistance");
            c.TrailDieWithParticles = B(trail, "dieWithParticles");
            c.TrailSizeAffectsWidth = B(trail, "sizeAffectsWidth");
            c.TrailSizeAffectsLifetime = B(trail, "sizeAffectsLifetime");
            c.TrailInheritParticleColor = B(trail, "inheritParticleColor");
            c.TrailColorOverLifetime = MinMaxGradientData.Read(trail.Struct("colorOverLifetime"));
            c.TrailWidthOverTrail = MinMaxCurveData.Read(trail.Struct("widthOverTrail"));
            c.TrailColorOverTrail = MinMaxGradientData.Read(trail.Struct("colorOverTrail"));
            if (c.TrailsEnabled)
            {
                string unsupported = I(trail, "mode") != 0 ? "ribbon trails"
                    : I(trail, "textureMode") != 0 ? "trail texture modes other than Stretch"
                    : B(trail, "worldSpace") && c.SimulationSpace == 0 ? "world-space trails of a local-space system"
                    : c.TrailSizeAffectsLifetime ? "trail lifetime scaled by particle size" : null;
                if (unsupported != null)
                {
                    Unsupported(unsupported + " (no trails)");
                    c.TrailsEnabled = false;
                }
                else if (B(trail, "generateLightingData")) Unsupported("trail lighting data");
            }

            var custom = f.Struct("CustomDataModule");
            c.CustomDataEnabled = B(custom, "enabled");
            for (int i = 0; i < 2; i++)
            {
                var stream = c.CustomData[i] = new CustomDataStream
                {
                    Mode = I(custom, $"mode{i}"),
                    Components = Math.Clamp(I(custom, $"vectorComponentCount{i}"), 0, 4),
                    Color = MinMaxGradientData.Read(custom.Struct($"color{i}")),
                };
                for (int k = 0; k < 4; k++) stream.Vector[k] = MinMaxCurveData.Read(custom.Struct($"vector{i}_{k}"));
                if (c.CustomDataEnabled && stream.Mode is not (0 or 1 or 2))
                {
                    Unsupported($"custom data mode {stream.Mode} (zeros)");
                    stream.Mode = 0;
                }
            }

            var sub = f.Struct("SubModule");
            c.SubEmitters = sub.Array("subEmitters").Cast<Fields>().Select(e => new SubEmitterData
            {
                Emitter = resolver.Resolve(e.PPtr("emitter")) as ParticleSystem,
                Type = I(e, "type"),
                Properties = I(e, "properties"),
                EmitProbability = F(e, "emitProbability"),
            }).ToArray();
            if (B(sub, "enabled"))
            {
                Unsupported("sub emitters");
                c.SubEmitters = Array.Empty<SubEmitterData>();
            }

            foreach (var module in new[]
            {
                "RotationModule", "VelocityModule", "InheritVelocityModule", "LifetimeByEmitterSpeedModule",
                "ExternalForcesModule", "ClampVelocityModule", "NoiseModule", "SizeBySpeedModule",
                "RotationBySpeedModule", "CollisionModule", "TriggerModule", "LightsModule",
            })
                if (B(f.Struct(module), "enabled")) Unsupported(module);
            c.NoiseEnabled = B(f.Struct("NoiseModule"), "enabled");

            return c;
        }
    }
}
