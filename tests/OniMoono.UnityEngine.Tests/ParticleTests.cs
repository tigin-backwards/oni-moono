using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Assets.Files;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Particles;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    public class ParticleTests : EngineTest
    {
        static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762230");
                var dir = Path.Combine(game, "OxygenNotIncluded_Data");
                if (!Directory.Exists(dir)) throw new InvalidOperationException($"Game data not found at {dir}; set ONI_GAME_DIR.");
                return dir;
            }
        }

        sealed class FileResolver(SerializedFile file) : IReferenceResolver
        {
            public Object Resolve(PPtr pptr) => ObjectLoader.Resolve(file, pptr);
        }

        /// The game's particle components (ParticleSystem class 198, ParticleSystemRenderer 199)
        /// by owning GameObject. Their prefabs carry game scripts the tests cannot load, so the
        /// components' data is bound onto test GameObjects instead.
        static Dictionary<long, (Fields System, Fields Renderer)> GameParticleData(out SerializedFile file)
        {
            AssetSystem.Initialize(DataDir);
            file = AssetSystem.Get("sharedassets0.assets");
            var byOwner = new Dictionary<long, (Fields System, Fields Renderer)>();
            foreach (var info in file.Objects)
            {
                int classId = file.TypeOf(info).ClassId;
                if (classId is not (198 or 199)) continue;
                var f = TypeTreeReader.ReadNative(classId, file.OpenObject(info));
                long owner = f.PPtr("m_GameObject").PathId;
                byOwner.TryGetValue(owner, out var entry);
                byOwner[owner] = classId == 198 ? (f, entry.Renderer) : (entry.System, f);
            }
            return byOwner;
        }

        /// A scene GameObject carrying the particle system of the given game prefab GameObject.
        static ParticleSystem FromGame(long gameObjectPathId)
        {
            var data = GameParticleData(out var file)[gameObjectPathId];
            var go = new GameObject("fx" + gameObjectPathId);
            go.SetActive(false);
            var ps = go.AddComponent<ParticleSystem>();
            var resolver = new FileResolver(file);
            ps.Data.Bind(data.System, resolver);
            ps.GetComponent<ParticleSystemRenderer>().Native.Bind(data.Renderer, resolver);
            go.SetActive(true);
            return ps;
        }

        /// Every game prefab GameObject with a particle system (path ids change between builds).
        static IEnumerable<long> GameEffects => GameParticleData(out _).Keys.OrderBy(id => id).ToArray();

        /// The particle effect GameObject of this name (its name is unique among them).
        static ParticleSystem FromGame(string name)
        {
            GameParticleData(out var file);
            var matches = GameEffects.Where(id => TypeTreeReader.ReadNative(1, file.OpenObject(file.ObjectsById[id])).String("m_Name") == name).ToArray();
            return FromGame(Assert.Single(matches));
        }

        /// A system with Unity's AddComponent defaults, then adjusted.
        static ParticleSystem Make(Action<ParticleConfig> configure, bool play = true)
        {
            var go = new GameObject("ps");
            go.SetActive(false);
            var ps = go.AddComponent<ParticleSystem>();
            configure(ps.Data.Config);
            go.SetActive(true);
            if (!play) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return ps;
        }

        static void Frames(int n, float dt = 1f / 60f)
        {
            for (int i = 0; i < n; i++) Frame(dt);
        }

        [Fact]
        public void EveryParticleSystemInTheGameBinds()
        {
            var data = GameParticleData(out _);
            Assert.Equal(12, data.Count);
            Assert.All(data.Values, d => Assert.NotNull(d.System));
            Assert.All(data.Values, d => Assert.NotNull(d.Renderer));
            foreach (long id in GameEffects)
            {
                var ps = FromGame(id);
                Assert.True(ps.main.duration > 0f);
                Assert.Equal(1, ps.subEmitters.subEmittersCount);
                Assert.Null(ps.subEmitters.GetSubEmitterSystem(0));
                // Every game effect plays on awake.
                Assert.True(ps.isPlaying);
                Assert.NotNull(ps.GetComponent<ParticleSystemRenderer>().sharedMaterial);
            }
        }

        /// Issue #47: the passes of the game's particle materials keep their LightMode in their
        /// state's tags, upper-case. A camera draws only the forward one: the missile smoke
        /// trail's lit shader (Particles/Standard Surface) also has ShadowCaster and Deferred
        /// passes, which drew its puffs as an opaque white ribbon (Deferred writes albedo with
        /// alpha 1), and Particles/Standard Unlit's ShadowCaster drew black dither under the
        /// missile's sparkles and the pollination effect.
        [Theory]
        [InlineData("smoke_trail", "Particles/Standard Surface", "FORWARDBASE")]   // fx_missile_smoke_trail/TrailFX2
        [InlineData("missile_sparkle", "Particles/Standard Unlit", null)]          // fx_missile_smoke_trail/TrailFX
        [InlineData("pollinated_fx", "Particles/Standard Unlit", null)]            // fx_plant_pollinated
        [InlineData("long_smoke_trail", "Klei/Standard Unlit", "FORWARDBASE")]     // fx_long_range_missile_smoke_trail
        public void GameParticleMaterialsDrawOnlyTheirForwardPass(string materialName, string shaderName, string lightMode)
        {
            ParticleSystem ps = null;
            foreach (long id in GameEffects)
            {
                ps = FromGame(id);
                if (ps.GetComponent<ParticleSystemRenderer>().sharedMaterial.name == materialName) break;
                Object.DestroyImmediate(ps.gameObject);
                ps = null;
            }
            Assert.NotNull(ps);
            var material = ps.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            var shader = (ShaderNative)material.shader.NativeOrThrow;
            Assert.Equal(shaderName, shader.ShaderName);
            var passes = new List<(ShaderNative Shader, ShaderPass Pass)>();
            Assert.True(OniMoono.Desktop.Gl.GlDrawRecorder.Passes(passes, shader, material.Data, -1, false));
            var drawn = Assert.Single(passes);
            // Tag names compare without case.
            Assert.Equal(lightMode, drawn.Pass.Tags.TryGetValue("lightmode", out var mode) ? mode : null);
            // Every pass carries its LightMode from its state: the smoke's shader has its
            // GrabPass, ShadowCaster, Forward and Deferred passes in that order.
            if (shaderName == "Particles/Standard Surface")
                Assert.Equal(["GRABPASS", "SHADOWCASTER", "FORWARDBASE", "DEFERRED"], shader.ActiveSubShader.Passes.Select(p => p.Tags["LightMode"]));
            Object.DestroyImmediate(ps.gameObject);
        }

        [Fact]
        public void AddComponentAddsARendererAndUnityDefaults()
        {
            var go = new GameObject("ps");
            var ps = go.AddComponent<ParticleSystem>();
            Assert.NotNull(go.GetComponent<ParticleSystemRenderer>());
            Assert.Equal(5f, ps.main.duration);
            Assert.True(ps.main.loop);
            Assert.True(ps.isPlaying);
            Assert.True(ps.useAutoRandomSeed);
            Assert.Equal(ParticleSystemStopAction.None, ps.main.stopAction);
        }

        [Fact]
        public void MeteorShowerBackgroundEmitsAtItsRate()
        {
            var live = FromGame("fx_meteorshower_bg"); // looping, 10/s, lifetime 5 s
            Assert.True(live.main.loop);
            Assert.Equal(5f, live.main.duration);
            Assert.True(live.IsAlive());
            Frames(60);
            Assert.InRange(live.particleCount, 9, 11);
            Frames(60 * 4);
            Assert.InRange(live.particleCount, 49, 51);
            // Lifetime 5 s: births and deaths now balance.
            Frames(60 * 3);
            Assert.InRange(live.particleCount, 49, 51);
            Assert.InRange(live.time, 0f, 5f);
        }

        [Fact]
        public void ParticlesExpireAfterTheirLifetime()
        {
            var ps = Make(c =>
            {
                c.Looping = false;
                c.Duration = 1f;
                c.StartLifetime = MinMaxCurveData.Const(0.5f);
                c.RateOverTime = MinMaxCurveData.Const(20f);
            });
            Frames(61);
            Assert.False(ps.isEmitting);
            Assert.True(ps.isPlaying);
            Assert.InRange(ps.particleCount, 9, 11);
            Frames(31);
            Assert.Equal(0, ps.particleCount);
            Assert.True(ps.isStopped);
            Assert.False(ps.IsAlive());
            Assert.Equal(0f, ps.time);
        }

        [Fact]
        public void BurstsFireAtTheirTimesAndRepeat()
        {
            var ps = Make(c =>
            {
                c.RateOverTime = MinMaxCurveData.Const(0f);
                c.StartLifetime = MinMaxCurveData.Const(10f);
                c.Bursts = [new BurstData { Time = 0.5f, Count = MinMaxCurveData.Const(7f), CycleCount = 2, RepeatInterval = 0.25f, Probability = 1f }];
            });
            Frames(29); // 0.483 s
            Assert.Equal(0, ps.particleCount);
            Frames(2); // 0.517 s
            Assert.Equal(7, ps.particleCount);
            Frames(13); // 0.733 s
            Assert.Equal(7, ps.particleCount);
            Frames(2); // 0.767 s
            Assert.Equal(14, ps.particleCount);
            Frames(60);
            Assert.Equal(14, ps.particleCount);
            // The next loop (duration 5 s) fires them again.
            Frames(60 * 4);
            Assert.Equal(28, ps.particleCount);
        }

        [Fact]
        public void StopLetsParticlesDieThenRunsTheStopAction()
        {
            var ps = Make(c => c.StartLifetime = MinMaxCurveData.Const(1f));
            var go = ps.gameObject;
            Frames(30);
            Assert.True(ps.particleCount > 0);
            var main = ps.main;
            main.stopAction = ParticleSystemStopAction.Destroy;
            ps.Stop();
            Assert.False(ps.isEmitting);
            Assert.True(ps.isPlaying);
            Assert.True(ps.IsAlive());
            Frames(70);
            Assert.True(go == null);
        }

        [Fact]
        public void StopWithClearStopsImmediately()
        {
            var ps = Make(_ => { });
            Frames(30);
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            Assert.Equal(0, ps.particleCount);
            Assert.True(ps.isStopped);
            Assert.False(ps.IsAlive());
            ps.Play();
            Assert.True(ps.isPlaying);
        }

        [Fact]
        public void WithChildrenReachesChildSystems()
        {
            var parent = Make(_ => { });
            var child = Make(_ => { });
            child.transform.SetParent(parent.transform, false);
            Frames(30);
            parent.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            Assert.True(child.isPlaying);
            Assert.True(parent.IsAlive());
            Assert.False(parent.IsAlive(false));
            parent.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            Assert.False(parent.IsAlive());
        }

        [Fact]
        public void DisabledEmissionEmitsNothing()
        {
            var ps = Make(_ => { });
            var emission = ps.emission;
            emission.enabled = false;
            Frames(60);
            Assert.Equal(0, ps.particleCount);
            emission.enabled = true;
            Frames(60);
            Assert.InRange(ps.particleCount, 9, 11);
        }

        [Fact]
        public void DeactivationClearsAndReactivationReplays()
        {
            var ps = Make(_ => { });
            Frames(60);
            Assert.True(ps.particleCount > 0);
            ps.gameObject.SetActive(false);
            Assert.Equal(0, ps.particleCount);
            Assert.True(ps.isStopped);
            ps.gameObject.SetActive(true);
            Assert.True(ps.isPlaying);
        }

        [Fact]
        public void SimulateFastForwardsAndPauses()
        {
            var ps = Make(c => c.StartLifetime = MinMaxCurveData.Const(10f), play: false);
            ps.useAutoRandomSeed = false;
            ps.randomSeed = 1234;
            ps.Simulate(2f, true, true, true);
            Assert.True(ps.isPaused);
            Assert.False(ps.isPlaying);
            Assert.InRange(ps.time, 1.99f, 2.01f);
            Assert.InRange(ps.particleCount, 19, 21);
            var first = ps.Data.Particles.ToArray().Select(p => p.Position).ToArray();
            Frames(10);
            Assert.Equal(first.Length, ps.particleCount);

            // A fixed seed replays identically.
            ps.Simulate(2f, true, true, true);
            Assert.Equal(first, ps.Data.Particles.ToArray().Select(p => p.Position).ToArray());

            ps.Play();
            Assert.True(ps.isPlaying);
        }

        [Fact]
        public void ShapeModuleOffsetsAndScalesBirthPositions()
        {
            var ps = Make(c =>
            {
                c.Shape.Type = ShapeKind.Box;
                c.StartSpeed = MinMaxCurveData.Const(0f);
                c.RateOverTime = MinMaxCurveData.Const(100f);
            });
            var shape = ps.shape;
            shape.position = new Vector3(10f, 0f, 0f);
            shape.scale = new Vector3(2f, 4f, 0f);
            Assert.Equal(new Vector3(10f, 0f, 0f), shape.position);
            Frames(30);
            Assert.True(ps.particleCount > 20);
            foreach (var p in ps.Data.Particles)
            {
                Assert.InRange(p.Position.x, 9f, 11f);
                Assert.InRange(p.Position.y, -2f, 2f);
                Assert.Equal(0f, p.Position.z, 4);
            }
        }

        [Fact]
        public void WorldSpaceParticlesStayBehindAMovingEmitter()
        {
            var ps = Make(c =>
            {
                c.SimulationSpace = 1;
                c.StartSpeed = MinMaxCurveData.Const(0f);
                c.Shape.Enabled = false;
            });
            Frames(30);
            int before = ps.particleCount;
            Assert.True(before > 0);
            ps.transform.position = new Vector3(100f, 0f, 0f);
            Frames(1);
            Assert.Equal(before, ps.Data.Particles.ToArray().Count(p => p.Position.x == 0f));
        }

        [Fact]
        public void RateOverDistanceEmitsAsTheEmitterMoves()
        {
            var ps = Make(c =>
            {
                c.SimulationSpace = 1;
                c.EmitterVelocityMode = 0;
                c.RateOverTime = MinMaxCurveData.Const(0f);
                c.RateOverDistance = MinMaxCurveData.Const(10f);
                c.StartLifetime = MinMaxCurveData.Const(100f);
                c.StartSpeed = MinMaxCurveData.Const(0f);
                c.Shape.Enabled = false;
            });
            Frames(10);
            Assert.Equal(0, ps.particleCount);
            for (int i = 1; i <= 60; i++)
            {
                ps.transform.position = new Vector3(i * 0.1f, 0f, 0f);
                Frame();
            }
            Assert.InRange(ps.particleCount, 59, 61);
            // Births are spread along the emitter's path.
            var xs = ps.Data.Particles.ToArray().Select(p => p.Position.x).OrderBy(x => x).ToArray();
            Assert.True(xs.Zip(xs.Skip(1), (a, b) => b - a).Max() < 0.15f);
        }

        [Fact]
        public void BillboardGeometryFacesTheCamera()
        {
            var ps = Make(c =>
            {
                c.StartSpeed = MinMaxCurveData.Const(0f);
                c.StartSizeX = MinMaxCurveData.Const(2f);
                c.Shape.Enabled = false;
            });
            Frames(30);
            int n = ps.particleCount;
            Assert.True(n > 0);
            var renderer = (ParticleSystemRendererNative)ps.GetComponent<ParticleSystemRenderer>().Native;
            var mesh = new MeshNative();
            var view = new ParticleView(new Vector3(0f, 0f, -10f), Quaternion.identity, null);
            Assert.True(renderer.BuildGeometry(view, mesh, out var objectToWorld));
            Assert.Equal(Matrix4x4.identity, objectToWorld);
            Assert.Equal(4 * n, mesh.VertexCount);
            Assert.Equal(6 * n, mesh.SubMeshes[0].Indices.Length);
            Assert.All(mesh.Positions, p => Assert.Equal(0f, p.z, 4));
            Assert.Equal(2f, mesh.Bounds.size.x, 3);
            Assert.Equal(2f, mesh.Bounds.size.y, 3);
            Assert.All(mesh.Normals, nrm => Assert.Equal(Vector3.back, nrm));
            // Renderer bounds hold the quad at any roll: its diagonal.
            Assert.Equal(2f * Mathf.Sqrt(2f), ps.GetComponent<ParticleSystemRenderer>().bounds.size.x, 3);

            // An orthographic camera with size 1 (viewport 2 units high) clamps the 2-unit
            // particles to maxParticleSize 0.5 × 2 = 1 unit.
            var ortho = Matrix4x4.Ortho(-1f, 1f, -1f, 1f, 0.3f, 100f);
            Assert.True(renderer.BuildGeometry(new ParticleView(new Vector3(0f, 0f, -10f), Quaternion.identity, ortho), mesh, out _));
            Assert.Equal(1f, mesh.Bounds.size.x, 3);

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            Assert.False(renderer.BuildGeometry(view, mesh, out _));
        }

        [Fact]
        public void GameSystemsRunAndBuildGeometry()
        {
            foreach (long id in GameEffects)
            {
                var ps = FromGame(id);
                for (int i = 0; i < 90; i++)
                {
                    // Moving, for the effects that emit over distance.
                    ps.transform.position = new Vector3(i * 0.05f, 0f, 0f);
                    Frame();
                }
                Assert.True(ps.particleCount > 0, $"effect {id} emitted nothing");
                var renderer = (ParticleSystemRendererNative)ps.GetComponent<ParticleSystemRenderer>().Native;
                var mesh = new MeshNative();
                Assert.True(renderer.BuildGeometry(new ParticleView(new Vector3(0f, 0f, -20f), Quaternion.identity, null), mesh, out _));
                Assert.True(mesh.VertexCount >= ps.particleCount * 4);
                Assert.All(mesh.Positions, p => Assert.True(float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z)));
                Object.DestroyImmediate(ps.gameObject);
            }
        }

        [Fact]
        public void TemporalTearBeamDrawsTrails()
        {
            var ps = FromGame("fx_open_temporal_tear_beam");
            Frames(60);
            var renderer = (ParticleSystemRendererNative)ps.GetComponent<ParticleSystemRenderer>().Native;
            var mesh = new MeshNative();
            Assert.True(renderer.BuildGeometry(new ParticleView(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f), null), mesh, out _));
            Assert.Equal(2, mesh.SubMeshes.Count);
            Assert.NotEmpty(mesh.SubMeshes[1].Indices);
            // Non-looping 5 s system, lifetime 5 s, trail points lasting 5 s: done after 15 s.
            Assert.True(ps.IsAlive());
            Frames(60 * 13);
            Assert.True(ps.IsAlive());
            Frames(60 * 2 + 5);
            Assert.False(ps.IsAlive());
        }

        /// A system set up as ManualMiner sets up its laser's sparks: world space, no emission.
        static ParticleSystem Scripted()
        {
            var go = new GameObject("sparks");
            go.transform.position = new Vector3(100f, 200f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.enabled = false;
            return ps;
        }

        static ParticleSystem.EmitParams Full() => new()
        {
            position = new Vector3(110f, 205f, -3f),
            velocity = new Vector3(4f, 3f, 0f),
            startSize = 0.25f,
            startLifetime = 0.3f,
            startColor = new Color32(76, 255, 255, 255),
            rotation = 90f,
        };

        /// Measured in the original (tools/UnityProbe, particleemit): the modules' defaults on a
        /// new component, and what setters keep.
        [Fact]
        public void ScriptedModulesHaveUnityDefaults()
        {
            var ps = new GameObject("defaults").AddComponent<ParticleSystem>();
            var main = ps.main;
            Assert.Equal((true, ParticleSystemSimulationSpace.Local, 5f), (main.playOnAwake, main.simulationSpace, main.startSpeed.constant));
            var limit = ps.limitVelocityOverLifetime;
            Assert.Equal((false, 1f, 0f), (limit.enabled, limit.limit.constant, limit.dampen));
            var rotation = ps.rotationOverLifetime;
            Assert.Equal((false, false, 0.7853982f), (rotation.enabled, rotation.separateAxes, rotation.z.constant));
            var trails = ps.trails;
            Assert.Equal((false, 1f, 1f, 0.2f, true, true, 1f), (trails.enabled, trails.ratio, trails.lifetime.constant, trails.minVertexDistance,
                trails.sizeAffectsWidth, trails.inheritParticleColor, trails.widthOverTrail.constant));
            Assert.Equal((ParticleSystemGradientMode.Color, Color.white), (trails.colorOverTrail.mode, trails.colorOverTrail.color));
            var size = ps.sizeOverLifetime;
            Assert.False(size.enabled);
            Assert.Equal((ParticleSystemCurveMode.Curve, 1f, 0f, 0.5f, 1f), (size.size.mode, size.size.curveMultiplier, size.size.curve.Evaluate(0f),
                size.size.curve.Evaluate(0.5f), size.size.curve.Evaluate(1f)));
            Assert.Equal(ParticleSystemRenderMode.Billboard, ps.GetComponent<ParticleSystemRenderer>().renderMode);
            limit.dampen = 1.5f;
            Assert.Equal(1f, limit.dampen);
            limit.dampen = -1f;
            Assert.Equal(0f, limit.dampen);
            trails.ratio = 1.5f;
            Assert.Equal(1f, trails.ratio);
            main.startSpeed = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            Assert.Equal((0f, ParticleSystemSimulationSpace.World), (main.startSpeed.constant, main.simulationSpace));
            Object.DestroyImmediate(ps.gameObject);
        }

        /// Measured: Emit(EmitParams) makes its particles at once, the set values as given (a
        /// position without the shape, in simulation space), the rest from the modules.
        [Fact]
        public void EmitParamsOverrideTheModules()
        {
            var ps = Scripted();
            Frames(2);
            ps.Emit(Full(), 1);
            var p = ps.Data.Particles[0];
            Assert.Equal((new Vector3(110f, 205f, -3f), new Vector3(4f, 3f, 0f), new Vector3(0.25f, 0.25f, 0.25f), 0.3f),
                (p.Position, p.Velocity, p.StartSize, p.Lifetime));
            Assert.Equal(90f * Mathf.Deg2Rad, p.Rotation.z, 5);
            Assert.Equal((Color)new Color32(76, 255, 255, 255), p.StartColor);
            ps.Clear();

            // A position set and no velocity: straight along +Z at the start speed.
            var e = Full();
            e.ResetVelocity();
            ps.Emit(e, 1);
            Assert.Equal(new Vector3(0f, 0f, 5f), ps.Data.Particles[0].Velocity);
            ps.Clear();

            // No position: the emitter's, offset by the cone's base (radius 1, z 0).
            e = Full();
            e.ResetPosition();
            e.ResetStartSize();
            e.ResetStartLifetime();
            e.ResetRotation();
            e.ResetStartColor();
            ps.Emit(e, 1);
            p = ps.Data.Particles[0];
            Assert.InRange((p.Position - new Vector3(100f, 200f, 0f)).magnitude, 0f, 1.0001f);
            Assert.Equal(0f, p.Position.z);
            Assert.Equal((new Vector3(1f, 1f, 1f), 5f, 0f, Color.white), (p.StartSize, p.Lifetime, p.Rotation.z, p.StartColor));
            ps.Clear();

            // With the shape applied, the position is offset by it.
            e = Full();
            e.applyShapeToPosition = true;
            ps.Emit(e, 1);
            p = ps.Data.Particles[0];
            Assert.InRange((p.Position - new Vector3(110f, 205f, -3f)).magnitude, 0f, 1.0001f);
            Assert.Equal(-3f, p.Position.z);
            ps.Clear();

            var main = ps.main;
            main.maxParticles = 5;
            for (int i = 0; i < 4; i++) ps.Emit(Full(), 2);
            Assert.Equal(5, ps.particleCount);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// Measured: a stopped system that is given particles plays, without emitting, until they
        /// are gone, and they age meanwhile.
        [Fact]
        public void EmitOnAStoppedSystemPlaysUntilItsParticlesDie()
        {
            var ps = Scripted();
            Frames(1);
            ps.Stop();
            Frames(2);
            Assert.True(ps.isStopped);
            ps.Emit(Full(), 1);
            Assert.Equal((true, false, false), (ps.isPlaying, ps.isStopped, ps.isEmitting));
            Frames(3);
            Assert.InRange(ps.Data.Particles[0].Age, 0.04f, 0.06f);
            Frames(30);
            Assert.Equal((false, true, 0), (ps.isPlaying, ps.isStopped, ps.particleCount));
            Object.DestroyImmediate(ps.gameObject);
        }

        /// Measured speeds over steps of Simulate: the speed over the limit shrinks by
        /// (1 - dampen) every 1/30 s, before the particle moves.
        [Theory]
        [InlineData(1f, 0.2f, 0.01f, new[] { 9.417236f, 8.872207f, 8.362469f, 7.88573742f, 7.439875f, 7.022883f },
            new[] { 110.09417f, 110.182892f, 110.266518f, 110.345375f, 110.419777f, 110.490005f })]
        [InlineData(1f, 0.2f, 0.1f, new[] { 5.608f, 3.35929561f, 2.207959f, 1.61847484f, 1.31665909f, 1.1621294f },
            new[] { 110.702705f, 111.11129f, 111.369286f, 111.550179f, 111.691589f, 111.8128f })]
        [InlineData(1f, 0.5f, 0.05f, new[] { 4.18198f, 2.12499976f, 1.39774752f, 1.140625f, 1.0497185f, 1.01757813f },
            new[] { 110.263336f, 110.388763f, 110.465431f, 110.524864f, 110.5782f, 110.629379f })]
        [InlineData(3f, 0.5f, 0.02f, new[] { 7.61827755f, 6.046927f, 5.01022243f, 4.326252f, 3.875f, 3.57728481f },
            new[] { 110.152367f, 110.273308f, 110.373512f, 110.460037f, 110.537537f, 110.609085f })]
        [InlineData(3f, 1f, 0.05f, new[] { 3f, 3f, 3f, 3f, 3f, 3f },
            new[] { 110.149994f, 110.299988f, 110.449982f, 110.599976f, 110.749969f, 110.899963f })]
        public void LimitVelocityDampensTheExcessSpeed(float limit, float dampen, float dt, float[] speeds, float[] positions)
        {
            var ps = Scripted();
            var module = ps.limitVelocityOverLifetime;
            module.enabled = true;
            module.limit = new ParticleSystem.MinMaxCurve(limit);
            module.dampen = dampen;
            var e = Full();
            e.velocity = new Vector3(10f, 0f, 0f);
            e.startLifetime = 5f;
            ps.Emit(e, 1);
            ps.Pause();
            for (int i = 0; i < speeds.Length; i++)
            {
                ps.Simulate(dt, true, false, false);
                var p = ps.Data.Particles[0];
                Assert.Equal(speeds[i], p.Velocity.x, 3);
                Assert.Equal(positions[i], p.Position.x, 3);
            }
            Object.DestroyImmediate(ps.gameObject);
        }

        /// Measured: rotation over lifetime is in radians a second (a constant 2 turns a particle
        /// 5.73 degrees in 0.05 s).
        [Fact]
        public void RotationOverLifetimeTurnsInRadiansASecond()
        {
            var ps = Scripted();
            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(2f);
            var e = Full();
            e.rotation = 0f;
            e.startLifetime = 5f;
            ps.Emit(e, 1);
            ps.Pause();
            ps.Simulate(0.05f, true, false, false);
            Assert.Equal(5.729578f, ps.Data.Particles[0].Rotation.z * Mathf.Rad2Deg, 3);
            Object.DestroyImmediate(ps.gameObject);
        }
    }
}
