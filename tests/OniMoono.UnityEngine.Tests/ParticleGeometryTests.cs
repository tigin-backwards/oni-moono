using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.Engine.Particles;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// Particle geometry as the original's ParticleSystemRenderer.BakeMesh gives it, for the
    /// features Cartoon FX effects use (Portal Network, Spring Festival Pack): custom vertex
    /// streams, the custom data module, flipping, stretched particles and mesh particles. Each
    /// expected value is UnityProbe particlestreams's (tools/UnityProbe/ParticleStreams.cs; runs
    /// j0010421, j0010424, j0010426): a camera at (0, 0, -10), particles of size 2 at (1, 2, 0)
    /// unless said, coloured (255, 128, 64, 200).
    public class ParticleGeometryTests : EngineTest
    {
        static ParticleSystem Scripted()
        {
            var go = new GameObject("probe");
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.enabled = false;
            return ps;
        }

        static ParticleSystemRendererNative Renderer(ParticleSystem ps) => (ParticleSystemRendererNative)ps.GetComponent<ParticleSystemRenderer>().EngineState;

        static ParticleSystem.EmitParams One(Vector3 position, float size = 2f, float rotation = 0f) => new()
        {
            position = position,
            velocity = new Vector3(0f, 1f, 0f),
            startSize = size,
            startLifetime = 4f,
            startColor = new Color32(255, 128, 64, 200),
            rotation = rotation,
        };

        static MeshNative Build(ParticleSystem ps, Quaternion? camera = null)
        {
            var mesh = new MeshNative();
            Assert.True(Renderer(ps).BuildGeometry(new ParticleView(new Vector3(0f, 0f, -10f), camera ?? Quaternion.identity, null), mesh, out _));
            return mesh;
        }

        static VertexStreams Streams(params int[] streams) => VertexStreams.Of(streams, what => throw new InvalidOperationException(what));

        /// The vertex at a position.
        static int At(MeshNative mesh, float x, float y, float z = 0f)
        {
            for (int i = 0; i < mesh.VertexCount; i++)
                if ((mesh.Positions[i] - new Vector3(x, y, z)).magnitude < 1e-3f) return i;
            throw new Xunit.Sdk.XunitException($"no vertex at ({x}, {y}, {z})");
        }

        /// Within the probe's four decimals.
        static void Near(Vector4 expected, Vector4 actual)
        {
            for (int k = 0; k < 4; k++) Assert.True(Math.Abs(expected[k] - actual[k]) < 1e-3f, $"expected {expected}, got {actual}");
        }

        static void Near(Vector3 expected, Vector3 actual) => Near((Vector4)expected, (Vector4)actual);

        [Fact]
        public void DefaultStreamsAreTheBillboardQuad()
        {
            var ps = Scripted();
            ps.Emit(One(new Vector3(1f, 2f, 0f)), 1);
            var mesh = Build(ps);
            Assert.Equal(4, mesh.VertexCount);
            Near(new Vector4(0f, 1f), mesh.Uvs[0][At(mesh, 0f, 3f)]);
            Near(new Vector4(1f, 1f), mesh.Uvs[0][At(mesh, 2f, 3f)]);
            Near(new Vector4(1f, 0f), mesh.Uvs[0][At(mesh, 2f, 1f)]);
            Near(new Vector4(0f, 0f), mesh.Uvs[0][At(mesh, 0f, 1f)]);
            Assert.Equal(2, mesh.UvDimensions[0]);
            Assert.Null(mesh.Uvs[1]);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// The fire thrower's streams (Position, Normal, Color, UV, StableRandomXY, Custom1X):
        /// TEXCOORD0 four floats (UV, then the random pair), TEXCOORD1 one (Custom1.x).
        [Fact]
        public void CustomStreamsPackIntoTexcoordsInOrder()
        {
            var ps = Scripted();
            Renderer(ps).Streams = Streams(0, 1, 3, 4, 24, 31);
            ps.Emit(One(new Vector3(1f, 2f, 0f)), 1);
            var mesh = Build(ps);
            Assert.Equal((4, 1, 0), (mesh.UvDimensions[0], mesh.UvDimensions[1], mesh.UvDimensions[2]));
            Assert.NotNull(mesh.Normals);
            Assert.Null(mesh.Tangents);
            int top = At(mesh, 0f, 3f);
            Assert.Equal((0f, 1f), (mesh.Uvs[0][top].x, mesh.Uvs[0][top].y));
            // Stable random numbers are in [0, 1), the same at each vertex.
            var random = new Vector2(mesh.Uvs[0][top].z, mesh.Uvs[0][top].w);
            Assert.InRange(random.x, 0f, 1f);
            Assert.InRange(random.y, 0f, 1f);
            for (int i = 0; i < 4; i++) Assert.Equal(random, new Vector2(mesh.Uvs[0][i].z, mesh.Uvs[0][i].w));
            // Custom1 without the custom data module is 0.
            Assert.Equal(0f, mesh.Uvs[1][top].x);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// A stream that does not fit runs on into the next register: UV, StableRandomXYZW and
        /// Custom1XYZ take TEXCOORD0.xy, TEXCOORD0.zw + TEXCOORD1.xy, TEXCOORD1.zw + TEXCOORD2.x.
        [Fact]
        public void StreamsRunOnAcrossRegisters()
        {
            var streams = Streams(0, 3, 4, 26, 33);
            Assert.Equal(new[] { (4, 0, 2), (26, 2, 4), (33, 6, 3) }, streams.Texcoords);
            Assert.Equal((3, 4, 4, 1), (streams.Channels, streams.Dimension(0), streams.Dimension(1), streams.Dimension(2)));
            Assert.False(streams.HasNormal);
            // Position, Color, UV, Center, AgePercent, Velocity, Rotation, SizeXYZ: TEXCOORD0
            // (u, v, center.x, center.y), TEXCOORD1 (center.z, age, velocity.x, velocity.y),
            // TEXCOORD2 (velocity.z, rotation, size.x, size.y), TEXCOORD3 (size.z).
            var misc = VertexStreams.Of([0, 3, 4, 10, 21, 19, 15, 14], _ => { });
            Assert.Equal(13, misc.Components);
            Assert.Equal(new[] { (4, 0, 2), (10, 2, 3), (21, 5, 1), (19, 6, 3), (15, 9, 1), (14, 10, 3) }, misc.Texcoords);
        }

        [Fact]
        public void CenterAgeVelocityAndSizeStreams()
        {
            var ps = Scripted();
            Renderer(ps).Streams = VertexStreams.Of([0, 3, 4, 10, 21, 19, 14], _ => { });
            ps.Emit(One(new Vector3(1f, 2f, 0f)), 1);
            var mesh = Build(ps);
            int v = At(mesh, 0f, 3f);
            Near(new Vector4(0f, 1f, 1f, 2f), mesh.Uvs[0][v]);
            Near(new Vector4(0f, 0f, 0f, 1f), mesh.Uvs[1][v]);
            Near(new Vector4(0f, 2f, 2f, 2f), mesh.Uvs[2][v]);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// The smoke's streams with the custom data module as the probe set it: Custom1 a
        /// vector of three (a linear curve, 0.5, random between 2 and 4; its w 0), Custom2 a red
        /// to blue gradient whose alpha falls from 1 to 0, each at the normalized age.
        [Fact]
        public void CustomDataModuleFeedsTheCustomStreams()
        {
            var ps = Scripted();
            var config = ps.Data.Config;
            config.CustomDataEnabled = true;
            var gradient = new Gradient();
            gradient.SetKeys([new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.blue, 1f)], [new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f)]);
            config.CustomData =
            [
                new CustomDataStream
                {
                    Mode = 1,
                    Components = 3,
                    Vector =
                    [
                        new MinMaxCurveData { Mode = MinMaxCurveData.Curve, Scalar = 1f, MaxCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f) },
                        MinMaxCurveData.Const(0.5f),
                        new MinMaxCurveData { Mode = MinMaxCurveData.TwoConstants, MinScalar = 2f, Scalar = 4f },
                        MinMaxCurveData.Const(7f),
                    ],
                },
                new CustomDataStream { Mode = 2, Color = new MinMaxGradientData { Mode = MinMaxGradientData.Gradient, MaxGradient = gradient } },
            ];
            Renderer(ps).Streams = Streams(0, 3, 4, 34, 38);
            var e = One(new Vector3(1f, 2f, 0f));
            e.velocity = Vector3.zero;
            e.startLifetime = 2f;
            ps.Emit(e, 1);
            ps.Simulate(0.5f, true, false, false);
            var mesh = Build(ps);
            var first = mesh.Uvs[0][0];
            var second = mesh.Uvs[1][0];
            var third = mesh.Uvs[2][0];
            // UV, then Custom1.xy | Custom1.zw, Custom2.xy | Custom2.zw.
            Assert.Equal(0.25f, first.z, 3);
            Assert.Equal(0.5f, first.w, 3);
            float random = second.x;
            Assert.InRange(random, 2f, 4f);
            Assert.Equal(0f, second.y);
            Near(new Vector4(0.75f, 0f, 0.25f, 0.75f), new Vector4(second.z, second.w, third.x, third.y));
            Assert.Equal(2, mesh.UvDimensions[2]);
            ps.Simulate(0.5f, true, false, false);
            mesh = Build(ps);
            Assert.Equal(0.5f, mesh.Uvs[0][0].z, 3);
            Assert.Equal(random, mesh.Uvs[1][0].x);
            Object.DestroyImmediate(ps.gameObject);
        }

        [Fact]
        public void CustomDataModuleReadsFromABundle()
        {
            // As Portal Network's fire thrower: Custom1 a vector whose x falls from 1 to 0.2.
            var ps = Scripted();
            var config = ps.Data.Config;
            Assert.False(config.CustomDataEnabled);
            Assert.Equal(0, config.CustomData[0].Mode);
            Assert.Equal(Vector4.zero, ps.Data.CustomData(0, 1u, 0.5f));
            Object.DestroyImmediate(ps.gameObject);
        }

        /// UV2 is UV without a texture sheet; with one it is the next frame, AnimBlend the
        /// frame's fraction and AnimFrame the frame (a 2×2 sheet, 1.5 frames in).
        [Fact]
        public void TextureSheetFeedsUv2AndAnimBlend()
        {
            var ps = Scripted();
            Renderer(ps).Streams = Streams(0, 4, 5, 8, 9);
            var config = ps.Data.Config;
            config.UvEnabled = true;
            config.TilesX = config.TilesY = 2;
            config.FrameOverTime = new MinMaxCurveData { Mode = MinMaxCurveData.Curve, Scalar = 1f, MaxCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f) };
            config.StartFrame = MinMaxCurveData.Const(0f);
            var e = One(new Vector3(1f, 2f, 0f));
            e.velocity = Vector3.zero;
            ps.Emit(e, 1);
            ps.Simulate(1.5f, true, false, false);
            var mesh = Build(ps);
            int top = At(mesh, 0f, 3f);
            Near(new Vector4(0.5f, 1f, 0f, 0.5f), mesh.Uvs[0][top]);
            Near(new Vector4(0.5f, 1.5f, 0f, 0f), mesh.Uvs[1][top]);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// Flipping mirrors a particle's texture coordinates, not its corners: x the u, y the v.
        [Fact]
        public void FlippedBillboardsMirrorTheirTexture()
        {
            var ps = Scripted();
            Renderer(ps).Flip = new Vector3(1f, 0f, 0f);
            ps.Emit(One(Vector3.zero, size: 1f, rotation: 30f), 1);
            var mesh = Build(ps);
            // Turned 30° clockwise; its top-left corner reads (1, 1).
            Near(new Vector4(1f, 1f), mesh.Uvs[0][At(mesh, -0.183f, 0.683f)]);
            Near(new Vector4(0f, 1f), mesh.Uvs[0][At(mesh, 0.683f, 0.183f)]);
            Renderer(ps).Flip = new Vector3(0f, 1f, 0f);
            mesh = Build(ps);
            Near(new Vector4(0f, 0f), mesh.Uvs[0][At(mesh, -0.183f, 0.683f)]);
            Renderer(ps).Flip = Vector3.zero;
            mesh = Build(ps);
            Near(new Vector4(0f, 1f), mesh.Uvs[0][At(mesh, -0.183f, 0.683f)]);
            Object.DestroyImmediate(ps.gameObject);
        }

        [Fact]
        public void FlipProportionMirrorsSomeParticles()
        {
            var ps = Scripted();
            Renderer(ps).Flip = new Vector3(0.5f, 0f, 0f);
            for (int i = 0; i < 64; i++) ps.Emit(One(new Vector3(i * 3f, 0f, 0f), size: 1f), 1);
            var mesh = Build(ps);
            int flipped = 0;
            for (int i = 0; i < 64; i++)
                if (mesh.Uvs[0][At(mesh, i * 3f - 0.5f, 0.5f)].x == 1f) flipped++;
            Assert.InRange(flipped, 16, 48);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// A stretched particle trails behind its head along its velocity, u from the head (0) to
        /// the tail (1), v 1 on the side cross(toward the camera, velocity); its rotation is not
        /// applied. Size 1, rotation 30.
        [Theory]
        [InlineData(2f, 0f, 0f, 0f, 0f, 1f, 1.5f, 2f, 1.5f, 0f)]             // plain
        [InlineData(1f, 0.5f, 0f, 0f, 0f, 4f, 1.5f, 2f, 1.5f, -1f)]           // scales: 1 × size + 0.5 × speed 4
        [InlineData(2f, 0f, 0.1f, 0.8f, 0f, 1f, 1.6f, 3.6f, 1.6f, 1.6f)]      // pivot: 0.1 widths across, 0.8 lengths along
        public void StretchedParticleQuads(float lengthScale, float velocityScale, float pivotX, float pivotY, float vx, float vy,
            float headX, float headY, float tailX, float tailY)
        {
            var ps = Scripted();
            var r = Renderer(ps);
            r.RenderMode = 1;
            r.LengthScale = lengthScale;
            r.VelocityScale = velocityScale;
            r.Pivot = new Vector3(pivotX, pivotY, 0.3f);
            var e = One(new Vector3(1f, 2f, 0f), size: 1f, rotation: 30f);
            e.velocity = new Vector3(vx, vy, 0f);
            ps.Emit(e, 1);
            var mesh = Build(ps);
            Near(new Vector4(0f, 1f), mesh.Uvs[0][At(mesh, headX, headY)]);
            Near(new Vector4(1f, 1f), mesh.Uvs[0][At(mesh, tailX, tailY)]);
            Near(new Vector4(1f, 0f), mesh.Uvs[0][At(mesh, tailX - 1f, tailY)]);
            Near(new Vector4(0f, 0f), mesh.Uvs[0][At(mesh, headX - 1f, headY)]);
            Object.DestroyImmediate(ps.gameObject);
        }

        [Fact]
        public void SlantedStretchedParticle()
        {
            var ps = Scripted();
            Renderer(ps).RenderMode = 1;
            var e = One(new Vector3(1f, 2f, 0f), size: 1f, rotation: 30f);
            e.velocity = new Vector3(1f, 1f, 0f);
            ps.Emit(e, 1);
            var mesh = Build(ps);
            Near(new Vector4(0f, 1f), mesh.Uvs[0][At(mesh, 1.3536f, 1.6464f)]);
            Near(new Vector4(1f, 1f), mesh.Uvs[0][At(mesh, -0.0607f, 0.2322f)]);
            Near(new Vector4(1f, 0f), mesh.Uvs[0][At(mesh, -0.7678f, 0.9393f)]);
            Near(new Vector4(0f, 0f), mesh.Uvs[0][At(mesh, 0.6464f, 2.3536f)]);
            Object.DestroyImmediate(ps.gameObject);
        }

        /// The probe's mesh: four vertices, (0, 0, 0), (1, 0, 0), (0, 2, 0), (0, 0, 3), normals
        /// -z but the last's +x, tangents +x (the third's w -1) but the last's +y, red, green, blue
        /// and half-transparent white, and a second UV.
        static Mesh ProbeMesh()
        {
            var m = new Mesh();
            m.vertices = [new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 2f, 0f), new Vector3(0f, 0f, 3f)];
            m.normals = [new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f)];
            m.tangents = [new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, -1f), new Vector4(0f, 1f, 0f, 1f)];
            m.uv = [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f)];
            m.uv2 = [new Vector2(0.5f, 0.25f), new Vector2(0.75f, 0.25f), new Vector2(0.5f, 0.5f), new Vector2(0.75f, 0.5f)];
            m.colors32 = [new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 255, 128)];
            m.triangles = [0, 1, 2, 0, 2, 3];
            return m;
        }

        static ParticleSystem MeshSystem(Mesh mesh, int alignment)
        {
            var ps = Scripted();
            var r = Renderer(ps);
            r.RenderMode = 4;
            r.Mesh = mesh;
            r.Alignment = alignment;
            return ps;
        }

        [Fact]
        public void MeshParticlesScaleTheMeshAndTintItsColours()
        {
            var m = ProbeMesh();
            var ps = MeshSystem(m, 1);
            ps.Emit(One(new Vector3(1f, 2f, 3f)), 1);
            var mesh = Build(ps);
            Assert.Equal(4, mesh.VertexCount);
            Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, mesh.SubMeshes[0].Indices.ToArray());
            Near(new Vector3(1f, 2f, 3f), mesh.Positions[0]);
            Near(new Vector3(3f, 2f, 3f), mesh.Positions[1]);
            Near(new Vector3(1f, 6f, 3f), mesh.Positions[2]);
            Near(new Vector3(1f, 2f, 9f), mesh.Positions[3]);
            Near(new Vector3(1f, 0f, 0f), mesh.Normals[3]);
            var colors = mesh.Colors32;
            Assert.Equal(new Color32(255, 0, 0, 200), colors[0]);
            Assert.Equal(new Color32(0, 128, 0, 200), colors[1]);
            Assert.Equal(new Color32(255, 128, 64, 100), colors[3]);
            Near(new Vector4(1f, 1f), mesh.Uvs[0][3]);
            Object.DestroyImmediate(ps.gameObject);
            Object.DestroyImmediate(m);
        }

        /// The particle's rotation is Quaternion.Euler of its rotation (z alone without 3D
        /// rotation: 30° turns +x toward +y), then the alignment's: View the camera's, Local the
        /// emitter's.
        [Fact]
        public void MeshParticlesTurnWithRotationAndAlignment()
        {
            var m = ProbeMesh();
            var ps = MeshSystem(m, 1);
            ps.Emit(One(new Vector3(1f, 2f, 3f), rotation: 30f), 1);
            var mesh = Build(ps);
            Near(new Vector3(2.7321f, 3f, 3f), mesh.Positions[1]);
            Near(new Vector3(-1f, 5.4641f, 3f), mesh.Positions[2]);
            Object.DestroyImmediate(ps.gameObject);

            var camera = Quaternion.Euler(10f, 20f, 0f);
            ps = MeshSystem(m, 0);
            ps.Emit(One(new Vector3(1f, 2f, 3f)), 1);
            mesh = Build(ps, camera);
            Near(new Vector3(2.8794f, 2f, 2.316f), mesh.Positions[1]);
            Near(new Vector3(3.0209f, 0.9581f, 8.5525f), mesh.Positions[3]);
            Near(new Vector3(-0.3368f, 0.1736f, -0.9254f), mesh.Normals[0]);
            ps.Clear();
            ps.Emit(One(new Vector3(1f, 2f, 3f), rotation: 30f), 1);
            mesh = Build(ps, camera);
            Near(new Vector3(2.687f, 2.9848f, 2.5708f), mesh.Positions[1]);
            Object.DestroyImmediate(ps.gameObject);

            ps = MeshSystem(m, 2);
            ps.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            ps.Emit(One(new Vector3(1f, 2f, 3f), rotation: 30f), 1);
            mesh = Build(ps);
            Near(new Vector3(0f, 3.7321f, 3f), mesh.Positions[1]);
            Near(new Vector3(-2.4641f, 0f, 3f), mesh.Positions[2]);
            Object.DestroyImmediate(ps.gameObject);
            Object.DestroyImmediate(m);
        }

        /// 3D rotation (10, 20, 30) and 3D size (1, 2, 3), world-aligned; then a pivot of (0.1,
        /// 0.2, 0.3) (in units of the mesh's bounds, z toward -z), and flipped on x.
        [Fact]
        public void MeshParticlesIn3D()
        {
            var m = ProbeMesh();
            var ps = MeshSystem(m, 1);
            var config = ps.Data.Config;
            config.Rotation3D = true;
            config.Size3D = true;
            config.StartRotationX = MinMaxCurveData.Const(10f * Mathf.Deg2Rad);
            config.StartRotationY = MinMaxCurveData.Const(20f * Mathf.Deg2Rad);
            config.StartRotationZ = MinMaxCurveData.Const(30f * Mathf.Deg2Rad);
            config.StartSizeX = MinMaxCurveData.Const(1f);
            config.StartSizeY = MinMaxCurveData.Const(2f);
            config.StartSizeZ = MinMaxCurveData.Const(3f);
            var e = One(new Vector3(1f, 2f, 3f));
            e = new ParticleSystem.EmitParams { position = e.position, velocity = e.velocity, startLifetime = 4f, startColor = e.startColor };
            ps.Emit(e, 1);
            var mesh = Build(ps);
            Near(new Vector3(1.8435f, 2.4924f, 2.7854f), mesh.Positions[1]);
            Near(new Vector3(-0.6736f, 5.4115f, 4.2493f), mesh.Positions[2]);
            Near(new Vector3(4.0314f, 0.4372f, 11.3288f), mesh.Positions[3]);
            Near(new Vector3(0.8435f, 0.4924f, -0.2146f), mesh.Normals[3]);

            var r = Renderer(ps);
            r.Pivot = new Vector3(0.1f, 0.2f, 0.3f);
            mesh = Build(ps);
            Near(new Vector3(-0.1598f, 3.2004f, 0.7298f), mesh.Positions[0]);
            Near(new Vector3(0.6837f, 3.6928f, 0.5152f), mesh.Positions[1]);

            r.Pivot = Vector3.zero;
            r.Flip = new Vector3(1f, 0f, 0f);
            mesh = Build(ps);
            Near(new Vector3(0.1565f, 1.5076f, 3.2146f), mesh.Positions[1]);
            Near(new Vector3(-0.8435f, -0.4924f, 0.2146f), mesh.Normals[3]);

            // The leaves' streams: UV and the mesh's second UV in TEXCOORD0, tangents turned
            // with the mesh, w kept.
            r.Flip = Vector3.zero;
            r.Streams = Streams(0, 1, 3, 4, 5, 2);
            mesh = Build(ps);
            Near(new Vector4(1f, 0f, 0.75f, 0.25f), mesh.Uvs[0][1]);
            Near(new Vector4(0.8435f, 0.4924f, -0.2146f, -1f), mesh.Tangents[2]);
            Object.DestroyImmediate(ps.gameObject);
            Object.DestroyImmediate(m);
        }

        /// A mesh whose bounds (center (2.5, 4, 5.5), size (1, 2, 3)) do not start at its
        /// origin: the pivot goes by the bounds' size alone.
        [Theory]
        [InlineData(0f, 0f, 0f, 3f, 5f, 7f)]
        [InlineData(0.5f, 0f, 0f, 3.5f, 5f, 7f)]
        [InlineData(0f, 0.5f, 0f, 3f, 6f, 7f)]
        [InlineData(0f, 0f, 0.5f, 3f, 5f, 5.5f)]
        public void MeshPivotGoesByTheBoundsSize(float px, float py, float pz, float x, float y, float z)
        {
            var m = new Mesh();
            m.vertices = [new Vector3(2f, 3f, 4f), new Vector3(3f, 3f, 4f), new Vector3(2f, 5f, 4f), new Vector3(2f, 3f, 7f)];
            m.triangles = [0, 1, 2, 0, 2, 3];
            m.RecalculateBounds();
            var ps = MeshSystem(m, 1);
            Renderer(ps).Pivot = new Vector3(px, py, pz);
            ps.Emit(One(new Vector3(1f, 2f, 3f), size: 1f), 1);
            var mesh = Build(ps);
            Near(new Vector3(x, y, z), mesh.Positions[0]);
            // Bounds without normals read 0.
            Assert.Equal(Vector3.zero, mesh.Normals[0]);
            Object.DestroyImmediate(ps.gameObject);
            Object.DestroyImmediate(m);
        }

        /// A 2×2 texture sheet 1.5 frames in maps the mesh's UV into frame 1's tile and its UV2
        /// into frame 2's (j0010438).
        [Fact]
        public void MeshParticlesTakeTheTextureSheetFrame()
        {
            var m = new Mesh();
            m.vertices = [new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f)];
            m.uv = [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.25f, 0.75f)];
            m.uv2 = [new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)];
            m.triangles = [0, 1, 2];
            var ps = MeshSystem(m, 1);
            Renderer(ps).Streams = Streams(0, 4, 5, 8);
            var config = ps.Data.Config;
            config.UvEnabled = true;
            config.TilesX = config.TilesY = 2;
            config.FrameOverTime = new MinMaxCurveData { Mode = MinMaxCurveData.Curve, Scalar = 1f, MaxCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f) };
            config.StartFrame = MinMaxCurveData.Const(0f);
            var e = One(new Vector3(1f, 2f, 3f));
            e.velocity = Vector3.zero;
            ps.Emit(e, 1);
            ps.Simulate(1.5f, true, false, false);
            var mesh = Build(ps);
            Near(new Vector4(0.5f, 0.5f, 0.25f, 0.25f), mesh.Uvs[0][0]);
            Near(new Vector4(0.625f, 0.875f, 0.25f, 0.25f), mesh.Uvs[0][2]);
            Assert.Equal(0.5f, mesh.Uvs[1][0].x, 3);
            Object.DestroyImmediate(ps.gameObject);
            Object.DestroyImmediate(m);
        }

        [Fact]
        public void MeshParticlesWithoutAMeshDrawNothing()
        {
            var ps = Scripted();
            Renderer(ps).RenderMode = 4;
            ps.Emit(One(Vector3.zero), 1);
            Assert.False(Renderer(ps).BuildGeometry(new ParticleView(new Vector3(0f, 0f, -10f), Quaternion.identity, null), new MeshNative(), out _));
            Object.DestroyImmediate(ps.gameObject);
        }
    }
}
