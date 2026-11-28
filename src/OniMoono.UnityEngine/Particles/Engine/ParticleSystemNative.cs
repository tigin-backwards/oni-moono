using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Serialization;
using UnityEngine;

namespace OniMoono.Engine.Particles
{
    /// One live particle, in simulation space (emitter-local for Local simulation, world for
    /// World simulation). Size and color are the start values; the over-lifetime modules are
    /// applied when the particle is drawn.
    internal struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Lifetime;
        public Vector3 StartSize;
        /// Euler angles in radians; billboards use z.
        public Vector3 Rotation;
        public Color StartColor;
        public uint Seed;
        public TrailPath Trail;
    }

    /// A particle's trail: positions it passed (simulation space) with the simulation time they
    /// were recorded, oldest first. A trail outlives its particle unless dieWithParticles.
    internal sealed class TrailPath
    {
        public readonly List<(Vector3 Position, float Time)> Points = new();
        /// How long each point lasts (trail lifetime × particle lifetime).
        public float PointLifetime;
        /// The particle's drawn size and color when it died (orphaned trails keep them).
        public float LastWidth;
        public Color LastColor;
        public float LastAgeNormalized;
        public uint Seed;
    }

    /// Per-property salts for the particle random hash.
    internal static class Salt
    {
        public const uint Lifetime = 1, Speed = 2, SizeX = 3, SizeY = 4, SizeZ = 5, RotX = 6, RotY = 7, RotZ = 8,
            RotFlip = 9, Color = 10, ShapeA = 11, ShapeB = 12, ShapeC = 13, ShapeD = 14, ShapeE = 15, ShapeF = 16,
            Gravity = 17, ForceX = 18, ForceY = 19, ForceZ = 20, SizeOverLifetime = 21, ColorOverLifetime = 22,
            ColorBySpeed = 23, FrameOverTime = 24, StartFrame = 25, Trail = 26, TrailLifetime = 27, TrailColor = 28,
            TrailWidth = 29, Limit = 30, RotationOverLifetime = 31, Custom = 32 /* to 39: stream × 4 + component */,
            CustomColor = 40 /* and 41 */, StableRandom = 42 /* to 45 */, FlipX = 46, FlipY = 47;
    }

    /// What ParticleSystem.Emit(EmitParams, count) sets of its particles; null takes the modules'.
    internal struct EmitOverrides
    {
        public Vector3? Position, Velocity;
        public float? StartSize, StartLifetime, RotationDegrees;
        public Color32? StartColor;
        public bool ApplyShapeToPosition;
    }

    /// Engine side of a ParticleSystem: its modules (Config) and its simulation state.
    ///
    /// Play state follows Unity: a system is playing from Play until it has stopped emitting
    /// (Stop, or the end of a non-looping duration) and its last particle has died; it is then
    /// stopped, its time resets, and its stop action runs. Pause freezes a playing system.
    internal sealed class ParticleSystemNative : ComponentNative
    {
        public ParticleConfig Config = new();

        public bool Playing;
        public bool Paused;
        public bool StopEmitting;
        /// Playback position in the current loop, in seconds.
        public float Time;
        public float DelayRemaining;
        /// Simulated seconds since Play (trail point ages).
        public float SimTime;
        /// Set when the system stopped; the system manager runs the stop action.
        public bool StopActionPending;

        Particle[] particles = new Particle[16];
        int count;
        readonly List<TrailPath> orphanTrails = new();
        float rateAccumulator, distanceAccumulator;
        int[] burstNext = Array.Empty<int>();
        ParticleRandom random;
        Vector3 previousEmitterPosition;
        bool hasPreviousEmitterPosition;

        public int ParticleCount => count;
        public ReadOnlySpan<Particle> Particles => particles.AsSpan(0, count);
        public IReadOnlyList<TrailPath> OrphanTrails => orphanTrails;

        public override void Bind(Fields data, IReferenceResolver resolver)
        {
            Serialized = null;
            Config = ParticleConfig.Read(data, resolver, GameObject?.Name ?? "?");
        }

        /// Instantiate copies the modules; the clone starts stopped (Unity copies serialized
        /// state, not the simulation). Sub-emitter references are not remapped into the clone.
        public override void RemapReferences(Func<UnityEngine.Object, UnityEngine.Object> map)
        {
            foreach (var sub in Config.SubEmitters) sub.Emitter = (ParticleSystem)map(sub.Emitter);
        }

        public override void CopyStateFrom(NativeObject source) => Config = ((ParticleSystemNative)source).Config.Clone();

        // ---- State queries ----

        bool EmissionFinished => StopEmitting || (!Config.Looping && Time >= Config.Duration);

        public bool IsPlaying => Playing && !Paused;
        public bool IsEmitting => Playing && !Paused && !EmissionFinished;
        public bool IsStopped => !Playing && !Paused;

        /// Live particles or trails, or a playing system that will still emit.
        public bool IsAliveSelf => count > 0 || orphanTrails.Count > 0 || (Playing && !EmissionFinished);

        // ---- Control ----

        /// Starts a stopped system or resumes a paused one. A playing system, looping or not,
        /// emitting, stopping or past its duration, starts over: time 0, the start delay again,
        /// its live particles kept (measured: UnityProbe particleplay; Portal Network's fire
        /// thrower replays its smoke so at each boom).
        public void Play()
        {
            if (Paused)
            {
                Paused = false;
                return;
            }
            if (Playing)
            {
                StopEmitting = false;
                StopActionPending = false;
                RestartLoop();
                DelayRemaining = Math.Max(0f, Config.StartDelay.Evaluate(0f, random.NextFloat()));
                return;
            }
            Start();
        }

        void Start()
        {
            var c = Config;
            Playing = true;
            Paused = false;
            StopEmitting = false;
            StopActionPending = false;
            random = new ParticleRandom(c.AutoRandomSeed ? ParticleRandom.NextAutoSeed() : c.RandomSeed);
            RestartLoop();
            SimTime = 0f;
            rateAccumulator = 0f;
            distanceAccumulator = 0f;
            hasPreviousEmitterPosition = false;
            // A prewarmed looping system starts as if one full loop had run; it has no delay.
            if (c.Prewarm && c.Looping)
            {
                DelayRemaining = 0f;
                RunFixed(c.Duration, UnityEngine.Time.maximumParticleDeltaTime);
                Time = 0f;
                RestartBursts();
            }
            else DelayRemaining = Math.Max(0f, c.StartDelay.Evaluate(0f, random.NextFloat()));
        }

        void RestartLoop()
        {
            Time = 0f;
            RestartBursts();
        }

        void RestartBursts()
        {
            if (burstNext.Length != Config.Bursts.Length) burstNext = new int[Config.Bursts.Length];
            else Array.Clear(burstNext);
        }

        public void Pause()
        {
            if (Playing) Paused = true;
        }

        /// StopEmitting lets live particles finish; StopEmittingAndClear removes them and stops.
        public void Stop(ParticleSystemStopBehavior behavior)
        {
            if (behavior == ParticleSystemStopBehavior.StopEmittingAndClear) Clear();
            if (!Playing) return;
            StopEmitting = true;
            CheckFinished();
        }

        /// Stops and clears without a stop action (deactivation, Simulate restart).
        public void Reset()
        {
            Clear();
            Playing = false;
            Paused = false;
            StopEmitting = false;
            StopActionPending = false;
            RestartLoop();
            DelayRemaining = 0f;
        }

        public void Clear()
        {
            Array.Clear(particles, 0, count);
            count = 0;
            orphanTrails.Clear();
        }

        void CheckFinished()
        {
            if (!Playing || !EmissionFinished || count > 0 || orphanTrails.Count > 0) return;
            Playing = false;
            Paused = false;
            StopEmitting = false;
            RestartLoop();
            StopActionPending = Config.StopAction != 0;
        }

        /// Fast-forwards by t seconds in steps (Time.fixedDeltaTime when fixedTimeStep, else equal
        /// steps no longer than the maximum particle delta time, as a frame's: measured), then
        /// leaves the system paused. restart starts it over.
        public void Simulate(float t, bool restart, bool fixedTimeStep)
        {
            if (restart) Reset();
            if (!Playing) Start();
            Paused = false;
            hasPreviousEmitterPosition = false;
            float run = t * Config.SimulationSpeed;
            float step = UnityEngine.Time.fixedDeltaTime;
            if (!fixedTimeStep)
            {
                float max = UnityEngine.Time.maximumParticleDeltaTime;
                step = max > 0f && run > 0f ? run / Math.Max(1, (int)MathF.Ceiling(run / max)) : run;
            }
            RunFixed(run, step);
            if (Playing) Paused = true;
        }

        void RunFixed(float t, float step)
        {
            if (!(step > 0f)) step = t;
            var emitter = CaptureEmitter();
            previousEmitterPosition = emitter.Position;
            hasPreviousEmitterPosition = true;
            while (t > 0f && Playing)
            {
                float h = Math.Min(step, t);
                Advance(h, emitter, emitter.Position, 0f, 1f, 0f);
                t -= h;
            }
        }

        /// One frame of the player loop: the frame's delta (scaled or unscaled time) times the
        /// simulation speed, split into steps no longer than Time.maximumParticleDeltaTime.
        public void FrameUpdate()
        {
            var emitter = CaptureEmitter();
            var previous = hasPreviousEmitterPosition ? previousEmitterPosition : emitter.Position;
            previousEmitterPosition = emitter.Position;
            hasPreviousEmitterPosition = true;
            if (!Playing || Paused) return;

            var c = Config;
            float frameDt = c.UseUnscaledTime ? UnityEngine.Time.unscaledDeltaTime : UnityEngine.Time.deltaTime;
            float dt = frameDt * c.SimulationSpeed;
            if (!(dt > 0f)) return;
            float speed = EmitterSpeed(emitter, previous, frameDt);
            float max = UnityEngine.Time.maximumParticleDeltaTime;
            int steps = max > 0f ? Math.Max(1, (int)MathF.Ceiling(dt / max)) : 1;
            float h = dt / steps;
            for (int i = 0; i < steps && Playing; i++)
                Advance(h, emitter, previous, (float)i / steps, (float)(i + 1) / steps, speed / c.SimulationSpeed);
        }

        // ---- Emitter ----

        /// The emitter's world pose and the scales the scaling mode applies.
        internal readonly struct EmitterPose
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            /// Scale applied to the simulation (positions, velocities, sizes).
            public readonly Vector3 Scale;
            /// Extra scale applied only to shape positions (Shape scaling mode).
            public readonly Vector3 ShapeScale;

            public EmitterPose(Vector3 position, Quaternion rotation, Vector3 scale, Vector3 shapeScale)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
                ShapeScale = shapeScale;
            }

            /// Simulation space to world for a Local-space system.
            public Matrix4x4 LocalToWorld => Matrix4x4.TRS(Position, Rotation, Scale);

            /// A world direction (gravity, world forces) in the emitter's scaled local space.
            public Vector3 WorldToLocalVector(Vector3 v)
            {
                var l = Quaternion.Inverse(Rotation) * v;
                return new Vector3(Divide(l.x, Scale.x), Divide(l.y, Scale.y), Divide(l.z, Scale.z));
            }

            static float Divide(float a, float b) => b == 0f ? 0f : a / b;
        }

        internal EmitterPose CaptureEmitter()
        {
            var t = GameObject.Transform;
            var position = Hierarchy.Position(t);
            var rotation = t.WorldRotation;
            var lossy = Hierarchy.LossyScale(t);
            return Config.ScalingMode switch
            {
                0 => new EmitterPose(position, rotation, lossy, Vector3.one),
                1 => new EmitterPose(position, rotation, t.LocalScale, Vector3.one),
                _ => new EmitterPose(position, rotation, Vector3.one, lossy),
            };
        }

        /// World units per second the emitter moves (rate over distance): the attached
        /// Rigidbody's velocity in Rigidbody mode (the transform's motion when there is none),
        /// the custom velocity in Custom mode.
        float EmitterSpeed(EmitterPose emitter, Vector3 previous, float frameDt)
        {
            switch (Config.EmitterVelocityMode)
            {
                case 1:
                    for (var t = GameObject.Transform; t is not null; t = t.Parent)
                        foreach (var comp in t.GameObject.Components)
                        {
                            if (comp.EngineState is RigidbodyNative rb) return rb.LinearVelocity.magnitude;
                            if (comp.EngineState is Rigidbody2DNative rb2) return rb2.LinearVelocity.magnitude;
                        }
                    break;
                case 2:
                    return Config.CustomEmitterVelocity.magnitude;
            }
            return frameDt > 0f ? (emitter.Position - previous).magnitude / frameDt : 0f;
        }

        // ---- Simulation step ----

        /// Advances h seconds of simulation time. The emitter moves from previous to the
        /// current pose over the frame; this step covers frame fractions [f0, f1].
        void Advance(float h, EmitterPose emitter, Vector3 previous, float f0, float f1, float emitterSpeed)
        {
            var c = Config;
            SimTime += h;
            UpdateParticles(h, emitter);
            UpdateTrails();

            float emitSpan = h;
            if (DelayRemaining > 0f)
            {
                float d = Math.Min(DelayRemaining, emitSpan);
                DelayRemaining -= d;
                emitSpan -= d;
            }
            if (emitSpan > 0f)
            {
                var ctx = new EmitContext(emitter, previous, f0, f1, h);
                float remaining = emitSpan;
                while (remaining > 0f)
                {
                    float segEnd = Math.Min(Time + remaining, c.Duration);
                    float seg = segEnd - Time;
                    remaining -= seg;
                    if (!EmissionFinished && c.EmissionEnabled) EmitSegment(Time, segEnd, remaining, emitterSpeed, ctx);
                    Time = segEnd;
                    if (Time < c.Duration) break;
                    if (!c.Looping) break;
                    RestartLoop();
                }
            }
            CheckFinished();
        }

        readonly struct EmitContext(EmitterPose emitter, Vector3 previous, float f0, float f1, float h)
        {
            public readonly EmitterPose Emitter = emitter;
            public readonly Vector3 Previous = previous;
            public readonly float F0 = f0, F1 = f1, H = h;

            /// The emitter position when a particle of the given age (at step end) was born.
            public Vector3 PositionAtAge(float age)
            {
                float within = H > 0f ? Math.Clamp(1f - age / H, 0f, 1f) : 1f;
                return Vector3.LerpUnclamped(Previous, Emitter.Position, F0 + (F1 - F0) * within);
            }
        }

        /// Emission for playback time [a, b); particles born at time te are aged
        /// (b - te) + extraAge to the end of the step.
        void EmitSegment(float a, float b, float extraAge, float emitterSpeed, in EmitContext ctx)
        {
            var c = Config;
            float span = b - a;
            float normalized = a / c.Duration;

            float rate = c.RateOverTime.Evaluate(normalized, random.NextFloat());
            if (rate > 0f && span > 0f)
            {
                float acc = rateAccumulator + rate * span;
                for (int k = (int)MathF.Floor(rateAccumulator) + 1; k <= (int)MathF.Floor(acc); k++)
                {
                    float te = a + (k - rateAccumulator) / rate;
                    EmitParticle(te, (b - te) + extraAge, ctx);
                }
                rateAccumulator = acc - MathF.Floor(acc);
            }

            float perMeter = c.RateOverDistance.Evaluate(normalized, random.NextFloat());
            float distanceRate = perMeter * emitterSpeed;
            if (distanceRate > 0f && span > 0f)
            {
                float acc = distanceAccumulator + distanceRate * span;
                for (int k = (int)MathF.Floor(distanceAccumulator) + 1; k <= (int)MathF.Floor(acc); k++)
                {
                    float te = a + (k - distanceAccumulator) / distanceRate;
                    EmitParticle(te, (b - te) + extraAge, ctx);
                }
                distanceAccumulator = acc - MathF.Floor(acc);
            }

            // Bursts fire at time + n·interval (n < cycleCount, or unbounded for 0) inside the
            // loop, inclusive at the start of the window and exclusive at its end.
            for (int i = 0; i < c.Bursts.Length; i++)
            {
                var burst = c.Bursts[i];
                while (burst.CycleCount == 0 || burstNext[i] < burst.CycleCount)
                {
                    float bt = burst.Time + burstNext[i] * burst.RepeatInterval;
                    if (bt >= b) break;
                    burstNext[i]++;
                    if (bt < a) continue;
                    float roll = random.NextFloat();
                    int n = (int)MathF.Round(burst.Count.Evaluate(bt / c.Duration, random.NextFloat()));
                    if (burst.Probability < 1f && roll >= burst.Probability) continue;
                    for (int j = 0; j < n; j++) EmitParticle(bt, (b - bt) + extraAge, ctx);
                }
            }
        }

        /// Emit(EmitParams, count), as measured: particles made at once at age 0, from the main and
        /// shape modules where the parameters set nothing. A position set places them there, in
        /// simulation space, without the shape (its direction too: they leave along +Z) unless
        /// applyShapeToPosition, which offsets them by it. A system not playing then plays, without
        /// emitting, and stops once they are gone.
        public void Emit(EmitOverrides overrides, int count)
        {
            if (count <= 0) return;
            if (!Playing)
            {
                random = new ParticleRandom(Config.AutoRandomSeed ? ParticleRandom.NextAutoSeed() : Config.RandomSeed);
                Playing = true;
                Paused = false;
                StopEmitting = true;
                StopActionPending = false;
            }
            var emitter = CaptureEmitter();
            var ctx = new EmitContext(emitter, emitter.Position, 0f, 1f, 0f);
            for (int i = 0; i < count; i++) EmitParticle(Time, 0f, ctx, overrides);
        }

        void EmitParticle(float te, float age, in EmitContext ctx) => EmitParticle(te, age, ctx, null);

        void EmitParticle(float te, float age, in EmitContext ctx, EmitOverrides? overrides)
        {
            var c = Config;
            if (count >= c.MaxParticles) return;
            uint seed = random.NextUInt();
            float normalized = te / c.Duration;
            float R(uint salt) => ParticleRandom.Hash01(seed, salt);

            var p = new Particle { Seed = seed };
            var o = overrides ?? default;
            p.Lifetime = o.StartLifetime ?? c.StartLifetime.Evaluate(normalized, R(Salt.Lifetime));
            if (!(p.Lifetime > 0f) || age >= p.Lifetime) return;
            float speed = c.StartSpeed.Evaluate(normalized, R(Salt.Speed));
            float sx = c.StartSizeX.Evaluate(normalized, R(Salt.SizeX));
            p.StartSize = c.Size3D
                ? new Vector3(sx, c.StartSizeY.Evaluate(normalized, R(Salt.SizeY)), c.StartSizeZ.Evaluate(normalized, R(Salt.SizeZ)))
                : new Vector3(sx, sx, sx);
            float rz = c.StartRotationZ.Evaluate(normalized, R(Salt.RotZ));
            p.Rotation = c.Rotation3D
                ? new Vector3(c.StartRotationX.Evaluate(normalized, R(Salt.RotX)), c.StartRotationY.Evaluate(normalized, R(Salt.RotY)), rz)
                : new Vector3(0f, 0f, rz);
            if (c.RandomizeRotationDirection > 0f && R(Salt.RotFlip) < c.RandomizeRotationDirection) p.Rotation = -p.Rotation;
            p.StartColor = c.StartColor.Evaluate(normalized, R(Salt.Color));
            if (o.StartSize is float size) p.StartSize = new Vector3(size, size, size);
            if (o.RotationDegrees is float degrees) p.Rotation = new Vector3(0f, 0f, degrees * Mathf.Deg2Rad);
            if (o.StartColor is Color32 color) p.StartColor = color;

            Vector3 position, direction;
            if (o.Position.HasValue && !o.ApplyShapeToPosition)
            {
                position = Vector3.zero;
                direction = Vector3.forward;
            }
            else ShapeSample(seed, out position, out direction);
            if (c.Shape.Enabled && c.Shape.AlignToDirection && direction != Vector3.zero)
            {
                var e = Quaternion.LookRotation(direction).eulerAngles * Mathf.Deg2Rad;
                p.Rotation += e;
            }

            var emitter = ctx.Emitter;
            position = Vector3.Scale(position, emitter.ShapeScale);
            var velocity = direction * speed;
            if (c.SimulationSpace == 1)
            {
                // World simulation: the emitter pose is applied once, at birth.
                p.Position = ctx.PositionAtAge(age) + emitter.Rotation * Vector3.Scale(position, emitter.Scale);
                p.Velocity = emitter.Rotation * Vector3.Scale(velocity, emitter.Scale);
                p.StartSize = Vector3.Scale(p.StartSize, Abs(emitter.Scale));
            }
            else
            {
                p.Position = position;
                p.Velocity = velocity;
            }
            // Parameters are in simulation space; a set position takes the emitter's place.
            if (o.Position is Vector3 at) p.Position = o.ApplyShapeToPosition ? at + (p.Position - (c.SimulationSpace == 1 ? ctx.PositionAtAge(age) : Vector3.zero)) : at;
            if (o.Velocity is Vector3 v) p.Velocity = v;

            if (c.TrailsEnabled && R(Salt.Trail) < c.TrailRatio)
            {
                p.Trail = new TrailPath
                {
                    PointLifetime = c.TrailLifetime.Evaluate(normalized, R(Salt.TrailLifetime)) * p.Lifetime,
                    Seed = seed,
                };
                p.Trail.Points.Add((p.Position, SimTime - age));
            }

            // Simulate the part of the step since birth.
            p.Age = age;
            Integrate(ref p, age, emitter);

            if (count == particles.Length) Array.Resize(ref particles, Math.Min(Math.Max(16, count * 2), Math.Max(c.MaxParticles, 16)));
            particles[count++] = p;
        }

        static Vector3 Abs(Vector3 v) => new(Math.Abs(v.x), Math.Abs(v.y), Math.Abs(v.z));

        /// A birth position and direction in emitter space (before the emitter's scale) from the
        /// shape module; without it particles start at the origin moving along +Z.
        void ShapeSample(uint seed, out Vector3 position, out Vector3 direction)
        {
            var s = Config.Shape;
            float R(uint salt) => ParticleRandom.Hash01(seed, salt);
            position = Vector3.zero;
            direction = Vector3.forward;
            if (!s.Enabled || s.Type == ShapeKind.MeshRenderer) return;

            switch (s.Type)
            {
                case ShapeKind.Box:
                    position = new Vector3(R(Salt.ShapeA) - 0.5f, R(Salt.ShapeB) - 0.5f, R(Salt.ShapeC) - 0.5f);
                    direction = Vector3.forward;
                    break;
                case ShapeKind.Sphere:
                {
                    // Outward from the centre, uniform over the shell's volume (thickness 1 fills the
                    // sphere, 0 emits from its surface); the arc sweeps around Z as the circle's does.
                    float z = 2f * R(Salt.ShapeA) - 1f;
                    float angle = R(Salt.ShapeC) * s.Arc * Mathf.Deg2Rad;
                    float ring = MathF.Sqrt(Math.Max(0f, 1f - z * z));
                    var radial = new Vector3(ring * MathF.Cos(angle), ring * MathF.Sin(angle), z);
                    float inner = 1f - Mathf.Clamp01(s.RadiusThickness);
                    float cube = inner * inner * inner;
                    position = radial * (s.Radius * MathF.Cbrt(cube + R(Salt.ShapeB) * (1f - cube)));
                    direction = radial;
                    break;
                }
                case ShapeKind.Circle:
                {
                    float angle = R(Salt.ShapeA) * s.Arc * Mathf.Deg2Rad;
                    float r = s.Radius * RadialFraction(R(Salt.ShapeB), s.RadiusThickness);
                    var radial = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
                    position = radial * r;
                    direction = radial;
                    break;
                }
                case ShapeKind.Cone:
                {
                    // Emitted from the base disc; the direction leans outward with the radial
                    // position, reaching the cone angle at the rim.
                    float angle = R(Salt.ShapeA) * s.Arc * Mathf.Deg2Rad;
                    float fraction = RadialFraction(R(Salt.ShapeB), s.RadiusThickness);
                    var radial = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
                    position = radial * (s.Radius * fraction);
                    float lean = Mathf.Clamp(s.Angle, 0f, 90f) * Mathf.Deg2Rad;
                    direction = (radial * (MathF.Sin(lean) * fraction) + new Vector3(0f, 0f, MathF.Cos(lean))).normalized;
                    break;
                }
            }

            if (s.RandomDirectionAmount > 0f)
                direction = Vector3.LerpUnclamped(direction, RandomUnitVector(R(Salt.ShapeD), R(Salt.ShapeE)), s.RandomDirectionAmount).normalized;
            if (s.SphericalDirectionAmount > 0f && position != Vector3.zero)
                direction = Vector3.LerpUnclamped(direction, position.normalized, s.SphericalDirectionAmount).normalized;
            if (s.RandomPositionAmount > 0f)
                position += RandomUnitVector(R(Salt.ShapeF), R(Salt.ShapeD)) * (s.RandomPositionAmount * MathF.Cbrt(R(Salt.ShapeE)));

            // Shape transform: scale, rotate, offset.
            var rotation = Quaternion.Euler(s.Rotation);
            position = rotation * Vector3.Scale(position, s.Scale) + s.Position;
            direction = rotation * direction;
        }

        /// Radial fraction for uniform area density over the emitting ring: thickness 1 fills
        /// the disc, 0 emits from the edge.
        static float RadialFraction(float u, float thickness)
        {
            float inner = 1f - Mathf.Clamp01(thickness);
            return MathF.Sqrt(inner * inner + u * (1f - inner * inner));
        }

        static Vector3 RandomUnitVector(float u, float v)
        {
            float z = 2f * u - 1f;
            float a = v * 2f * MathF.PI;
            float r = MathF.Sqrt(Math.Max(0f, 1f - z * z));
            return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
        }

        void UpdateParticles(float h, EmitterPose emitter)
        {
            for (int i = 0; i < count;)
            {
                ref var p = ref particles[i];
                p.Age += h;
                if (p.Age >= p.Lifetime)
                {
                    Kill(i, emitter);
                    continue;
                }
                Integrate(ref p, h, emitter);
                i++;
            }
        }

        /// A dead particle is replaced by the last one (Unity's particle buffer order).
        void Kill(int index, EmitterPose emitter)
        {
            ref var p = ref particles[index];
            if (p.Trail != null && !Config.TrailDieWithParticles && p.Trail.Points.Count > 0)
            {
                p.Trail.Points.Add((p.Position, SimTime));
                var look = Look(p, emitter);
                p.Trail.LastColor = look.Color;
                p.Trail.LastWidth = look.Size.x;
                p.Trail.LastAgeNormalized = 1f;
                orphanTrails.Add(p.Trail);
            }
            particles[index] = particles[count - 1];
            particles[--count] = default;
        }

        /// Gravity and force over lifetime change the velocity, which moves the particle.
        void Integrate(ref Particle p, float h, EmitterPose emitter)
        {
            if (h <= 0f) return;
            var c = Config;
            Vector3 accel = Vector3.zero;
            if (!c.GravityModifier.IsZero)
            {
                float g = c.GravityModifier.Evaluate(Time / c.Duration, ParticleRandom.Hash01(p.Seed, Salt.Gravity));
                var gravity = Physics.gravity * g;
                accel += c.SimulationSpace == 1 ? gravity : emitter.WorldToLocalVector(gravity);
            }
            if (c.ForceEnabled)
            {
                float t = p.Age / p.Lifetime;
                uint rs = c.ForceRandomizePerFrame ? random.NextUInt() : p.Seed;
                var force = new Vector3(
                    c.ForceX.Evaluate(t, ParticleRandom.Hash01(rs, Salt.ForceX)),
                    c.ForceY.Evaluate(t, ParticleRandom.Hash01(rs, Salt.ForceY)),
                    c.ForceZ.Evaluate(t, ParticleRandom.Hash01(rs, Salt.ForceZ)));
                if (c.ForceInWorldSpace && c.SimulationSpace == 0) force = emitter.WorldToLocalVector(force);
                else if (!c.ForceInWorldSpace && c.SimulationSpace == 1) force = emitter.Rotation * force;
                accel += force;
            }
            p.Velocity += accel * h;
            if (c.LimitEnabled)
            {
                // The speed over the limit shrinks by (1 - dampen) every 1/30 s, before the
                // particle moves; a dampen of 1 holds it at the limit (measured).
                float limit = c.Limit.Evaluate(p.Age / p.Lifetime, ParticleRandom.Hash01(p.Seed, Salt.Limit));
                float speed = p.Velocity.magnitude;
                if (speed > limit && speed > 0f)
                {
                    float kept = MathF.Pow(1f - c.LimitDampen, h * 30f);
                    p.Velocity *= (limit + (speed - limit) * kept) / speed;
                }
            }
            p.Position += p.Velocity * h;
            if (c.RotationEnabled)
                p.Rotation.z += c.RotationZ.Evaluate(p.Age / p.Lifetime, ParticleRandom.Hash01(p.Seed, Salt.RotationOverLifetime)) * h;
        }

        /// Records trail points as particles move and drops expired points.
        void UpdateTrails()
        {
            if (!Config.TrailsEnabled && orphanTrails.Count == 0) return;
            float minDistance = Config.TrailMinVertexDistance;
            for (int i = 0; i < count; i++)
            {
                ref var p = ref particles[i];
                var trail = p.Trail;
                if (trail == null) continue;
                Expire(trail);
                var points = trail.Points;
                if (points.Count == 0 || (p.Position - points[^1].Position).magnitude >= minDistance)
                    points.Add((p.Position, SimTime));
            }
            for (int i = orphanTrails.Count - 1; i >= 0; i--)
            {
                Expire(orphanTrails[i]);
                if (orphanTrails[i].Points.Count == 0) orphanTrails.RemoveAt(i);
            }
        }

        void Expire(TrailPath trail)
        {
            var points = trail.Points;
            int expired = 0;
            while (expired < points.Count && SimTime - points[expired].Time >= trail.PointLifetime) expired++;
            if (expired > 0) points.RemoveRange(0, expired);
        }

        // ---- Appearance ----

        /// A particle as drawn: world position and velocity, size (x, y), color, roll and
        /// texture-sheet cell.
        internal struct ParticleLook
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 Size;
            public Color Color;
            public float Rotation;
            public Rect Uv;
            public float AgeNormalized;
            /// Euler angles in radians: the start rotation's x and y, and z with its rotation
            /// over lifetime (Rotation).
            public Vector3 Rotation3;
            /// The texture sheet's next frame (the UV2 stream) and the frame, fraction included
            /// (AnimFrame; AnimBlend is its fraction): measured, UnityProbe particlestreams
            /// (j0010426). Without the module, Uv2 is Uv and Frame 0.
            public Rect Uv2;
            public float Frame;
            public uint Seed;
            public float Lifetime;
        }

        /// A particle's Custom1 (stream 0) or Custom2 (1) values from the custom data module at
        /// normalized age t; zeros while the module is off (measured: UnityProbe particlestreams,
        /// j0010421: a vector stream's components past its count are 0, as are a disabled one's).
        internal Vector4 CustomData(int stream, uint seed, float t)
        {
            var c = Config;
            if (!c.CustomDataEnabled) return default;
            var s = c.CustomData[stream];
            switch (s.Mode)
            {
                case 1:
                {
                    var v = default(Vector4);
                    for (int k = 0; k < s.Components; k++) v[k] = s.Vector[k].Evaluate(t, ParticleRandom.Hash01(seed, Salt.Custom + (uint)(stream * 4 + k)));
                    return v;
                }
                case 2:
                {
                    var color = s.Color.Evaluate(t, ParticleRandom.Hash01(seed, Salt.CustomColor + (uint)stream));
                    return new Vector4(color.r, color.g, color.b, color.a);
                }
                default:
                    return default;
            }
        }

        internal ParticleLook Look(in Particle p, EmitterPose emitter)
        {
            var c = Config;
            float t = Mathf.Clamp01(p.Age / p.Lifetime);
            var look = new ParticleLook { Rotation = p.Rotation.z, AgeNormalized = t, Seed = p.Seed, Lifetime = p.Lifetime };

            var size = p.StartSize;
            if (c.SizeEnabled)
            {
                float r = ParticleRandom.Hash01(p.Seed, Salt.SizeOverLifetime);
                size = c.SizeSeparateAxes
                    ? Vector3.Scale(size, new Vector3(c.SizeX.Evaluate(t, r), c.SizeY.Evaluate(t, r), c.SizeZ.Evaluate(t, r)))
                    : size * c.SizeX.Evaluate(t, r);
            }

            if (c.SimulationSpace == 1)
            {
                look.Position = p.Position;
                look.Velocity = p.Velocity;
            }
            else
            {
                var m = emitter.LocalToWorld;
                look.Position = m.MultiplyPoint3x4(p.Position);
                look.Velocity = m.MultiplyVector(p.Velocity);
                size = Vector3.Scale(size, Abs(emitter.Scale));
            }
            look.Size = size;

            var color = p.StartColor;
            if (c.ColorEnabled) color *= c.ColorOverLifetime.Evaluate(t, ParticleRandom.Hash01(p.Seed, Salt.ColorOverLifetime));
            if (c.ColorBySpeedEnabled)
            {
                float s = Mathf.InverseLerp(c.ColorBySpeedRange.x, c.ColorBySpeedRange.y, p.Velocity.magnitude);
                color *= c.ColorBySpeed.Evaluate(s, ParticleRandom.Hash01(p.Seed, Salt.ColorBySpeed));
            }
            look.Color = color;

            look.Rotation3 = new Vector3(p.Rotation.x, p.Rotation.y, look.Rotation);
            look.Uv = new Rect(0f, 0f, 1f, 1f);
            look.Uv2 = look.Uv;
            if (c.UvEnabled)
            {
                int tiles = c.TilesX * c.TilesY;
                // Frame curves are stored normalized to the sheet (0..1 covers every tile).
                float frame = c.StartFrame.Evaluate(t, ParticleRandom.Hash01(p.Seed, Salt.StartFrame)) * tiles;
                if (c.UvTimeMode == 2) frame += p.Age * c.UvFps;
                else
                {
                    float cycle = Mathf.Repeat(t * c.Cycles, 1f);
                    frame += c.FrameOverTime.Evaluate(cycle, ParticleRandom.Hash01(p.Seed, Salt.FrameOverTime)) * tiles;
                }
                int index = (int)MathF.Floor(frame) % tiles;
                if (index < 0) index += tiles;
                look.Uv = Tile(index);
                look.Uv2 = Tile((index + 1) % tiles);
                look.Frame = index + (frame - MathF.Floor(frame));
            }
            return look;
        }

        /// A texture sheet tile's rectangle; row 0 is the top of the texture.
        Rect Tile(int index)
        {
            var c = Config;
            int column = index % c.TilesX, row = index / c.TilesX;
            float w = 1f / c.TilesX, h = 1f / c.TilesY;
            return new Rect(column * w, 1f - (row + 1) * h, w, h);
        }

        /// World-space AABB of the particles (and trails), each grown by its drawn extent: a quad's,
        /// moved by up to pivot (the renderer's pivot's length) times its size, or for mesh
        /// particles (meshRadius > 0) the mesh's farthest point from the particle at size 1, pivot
        /// included, times the particle's largest size.
        internal bool WorldBounds(float stretchLength, float stretchVelocity, float pivot, float meshRadius, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var emitter = CaptureEmitter();
            var toWorld = Config.SimulationSpace == 1 ? Matrix4x4.identity : emitter.LocalToWorld;
            void Add(Vector3 center, float radius, ref Bounds b)
            {
                var piece = new Bounds(center, Vector3.one * (radius * 2f));
                if (!any) { b = piece; any = true; }
                else b.Encapsulate(piece);
            }
            for (int i = 0; i < count; i++)
            {
                var look = Look(particles[i], emitter);
                // Half the quad's diagonal covers it at any roll and orientation.
                float radius = meshRadius > 0f
                    ? meshRadius * Math.Max(Math.Abs(look.Size.x), Math.Max(Math.Abs(look.Size.y), Math.Abs(look.Size.z)))
                    : (1f + 2f * pivot) * (0.5f * new Vector2(look.Size.x, look.Size.y).magnitude
                        + stretchLength * Math.Max(look.Size.x, look.Size.y) + stretchVelocity * look.Velocity.magnitude);
                Add(look.Position, radius, ref bounds);
                if (particles[i].Trail is { } trail)
                    foreach (var point in trail.Points) Add(toWorld.MultiplyPoint3x4(point.Position), 0.5f * look.Size.x, ref bounds);
            }
            foreach (var trail in orphanTrails)
                foreach (var point in trail.Points) Add(toWorld.MultiplyPoint3x4(point.Position), 0.5f * trail.LastWidth, ref bounds);
            return any;
        }
    }
}
