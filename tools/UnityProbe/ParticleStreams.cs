// Particle geometry as ParticleSystemRenderer.BakeMesh gives it ("PROBE ... PS ..."), at the main
// menu (10 s in), for the features Cartoon FX effects use (Portal Network 2968184715, Spring
// Festival Pack 3152316922): custom vertex streams, the custom data module, flipping and mesh
// particles. It records:
// - enum: ParticleSystemVertexStream's names and values;
// - streams: one billboard particle with Custom1 and Custom2 set by SetCustomParticleData, baked
//   with each stream set the mods' renderers use (and one whose 4-component stream starts at a
//   register's z), with the mesh's vertex attributes and the first vertices' values;
// - customdata: the custom data module's values (vector curves, random between constants, a
//   colour) at given ages, and with the module off;
// - flip: billboard and stretched quads under renderer.flip;
// - mesh: mesh particles (a known four-vertex mesh) under each alignment, with rotation, 3D
//   rotation, 3D size and a pivot, and the mesh's own UV2 and tangents through custom streams.
// With PROBE_SCENARIO=particlestreams only this probe runs; it ends with a
// ParticleStreamsProbeDone overlay canvas.
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class ParticleStreams : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        Camera cam;
        Material material;

        static void L(string s) => Rec.Log("PS " + s);
        static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => "(" + F(v.x) + " " + F(v.y) + " " + F(v.z) + ")";
        static string V4(Vector4 v) => "(" + F(v.x) + " " + F(v.y) + " " + F(v.z) + " " + F(v.w) + ")";

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        ParticleSystem NewSystem(string name)
        {
            var go = new GameObject(name);
            go.transform.position = Vector3.zero;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1000;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            return ps;
        }

        static ParticleSystem.EmitParams One(Vector3 position)
        {
            var e = new ParticleSystem.EmitParams();
            e.position = position;
            e.velocity = new Vector3(0f, 1f, 0f);
            e.startSize = 2f;
            e.startLifetime = 4f;
            e.startColor = new Color32(255, 128, 64, 200);
            e.rotation = 0f;
            return e;
        }

        static void Dump(string label, Mesh m, int maxVerts)
        {
            var sb = new StringBuilder();
            foreach (var a in m.GetVertexAttributes()) sb.Append(" " + a.attribute + ":" + a.format + "x" + a.dimension + "@" + a.stream);
            var idx = m.subMeshCount > 0 ? m.GetIndices(0) : new int[0];
            var isb = new StringBuilder();
            for (int i = 0; i < idx.Length && i < 12; i++) isb.Append(idx[i] + ",");
            L(label + " verts=" + m.vertexCount + " submeshes=" + m.subMeshCount + " indices=" + idx.Length + " [" + isb + "] attrs" + sb);
            var pos = m.vertices;
            var nrm = m.normals;
            var tan = m.tangents;
            var col = m.colors32;
            var uvs = new List<Vector4>[8];
            for (int c = 0; c < 8; c++)
                if (m.HasVertexAttribute(VertexAttribute.TexCoord0 + c))
                {
                    uvs[c] = new List<Vector4>();
                    m.GetUVs(c, uvs[c]);
                }
            for (int v = 0; v < m.vertexCount && v < maxVerts; v++)
            {
                var line = new StringBuilder(label + "  v" + v + " pos=" + V(pos[v]));
                if (nrm.Length > v) line.Append(" n=" + V(nrm[v]));
                if (tan.Length > v) line.Append(" t=" + V4(tan[v]));
                if (col.Length > v) line.Append(" c=" + col[v].r + "," + col[v].g + "," + col[v].b + "," + col[v].a);
                for (int c = 0; c < 8; c++)
                    if (uvs[c] != null && uvs[c].Count > v) line.Append(" uv" + c + "=" + V4(uvs[c][v]));
                L(line.ToString());
            }
        }

#pragma warning disable CS0618 // BakeMesh(Mesh, Camera, bool): the form the 2020.3 bundles' era used, still in 6000.3
        void Bake(string label, ParticleSystem ps, int maxVerts, bool useTransform = false)
        {
            var mesh = new Mesh();
            ps.GetComponent<ParticleSystemRenderer>().BakeMesh(mesh, cam, useTransform);
            Dump(label, mesh, maxVerts);
            Destroy(mesh);
        }
#pragma warning restore CS0618

        static string Streams(ParticleSystemRenderer r)
        {
            var list = new List<ParticleSystemVertexStream>();
            r.GetActiveVertexStreams(list);
            var sb = new StringBuilder();
            foreach (var s in list) sb.Append(s + ",");
            return sb.ToString();
        }

        IEnumerator Run()
        {
            L("=== particle streams");
            var camGo = new GameObject("PSCamera");
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            camGo.transform.position = new Vector3(0f, 0f, -10f);
            camGo.transform.rotation = Quaternion.identity;
            material = new Material(Shader.Find("Hidden/Internal-Colored"));

            // enum
            foreach (ParticleSystemVertexStream s in System.Enum.GetValues(typeof(ParticleSystemVertexStream)))
                L("enum " + s + "=" + (int)s);

            // streams
            var sets = new (string, ParticleSystemVertexStream[])[]
            {
                ("fire", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomXY, ParticleSystemVertexStream.Custom1X }),
                ("smoke", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.Custom1XYZW, ParticleSystemVertexStream.Custom2XYZW }),
                ("ring", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.Custom1XYZW, ParticleSystemVertexStream.Custom2XYZ, ParticleSystemVertexStream.SizeX }),
                ("debris", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Tangent, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomXY, ParticleSystemVertexStream.Custom1XYZW }),
                ("leaves", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.Tangent }),
                ("straddle", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomXYZW, ParticleSystemVertexStream.Custom1XYZ }),
                ("misc", new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.Center, ParticleSystemVertexStream.AgePercent, ParticleSystemVertexStream.Velocity, ParticleSystemVertexStream.Rotation, ParticleSystemVertexStream.SizeXYZ }),
            };
            {
                var ps = NewSystem("Streams");
                var r = ps.GetComponent<ParticleSystemRenderer>();
                L("streams default " + Streams(r));
                ps.Emit(One(new Vector3(1f, 2f, 0f)), 1);
                var c1 = new List<Vector4> { new Vector4(0.11f, 0.22f, 0.33f, 0.44f) };
                var c2 = new List<Vector4> { new Vector4(0.55f, 0.66f, 0.77f, 0.88f) };
                ps.SetCustomParticleData(c1, ParticleSystemCustomData.Custom1);
                ps.SetCustomParticleData(c2, ParticleSystemCustomData.Custom2);
                Bake("streams default", ps, 4);
                foreach (var (name, set) in sets)
                {
                    r.SetActiveVertexStreams(new List<ParticleSystemVertexStream>(set));
                    L("streams " + name + " active " + Streams(r));
                    Bake("streams " + name, ps, 4);
                }
                Destroy(ps.gameObject);
            }

            // customdata
            {
                var ps = NewSystem("CustomData");
                var cd = ps.customData;
                L("customdata default enabled=" + cd.enabled + " mode1=" + cd.GetMode(ParticleSystemCustomData.Custom1) + " count1=" + cd.GetVectorComponentCount(ParticleSystemCustomData.Custom1));
                ps.Emit(One(Vector3.zero), 1);
                var list = new List<Vector4>();
                ps.GetCustomParticleData(list, ParticleSystemCustomData.Custom1);
                L("customdata off emitted: " + (list.Count > 0 ? V4(list[0]) : "none"));
                ps.Clear();
                cd.enabled = true;
                cd.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Vector);
                cd.SetVectorComponentCount(ParticleSystemCustomData.Custom1, 3);
                cd.SetVector(ParticleSystemCustomData.Custom1, 0, new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f)));
                cd.SetVector(ParticleSystemCustomData.Custom1, 1, new ParticleSystem.MinMaxCurve(0.5f));
                cd.SetVector(ParticleSystemCustomData.Custom1, 2, new ParticleSystem.MinMaxCurve(2f, 4f));
                cd.SetVector(ParticleSystemCustomData.Custom1, 3, new ParticleSystem.MinMaxCurve(7f));
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.red, 0f), new GradientColorKey(Color.blue, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
                cd.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Color);
                cd.SetColor(ParticleSystemCustomData.Custom2, new ParticleSystem.MinMaxGradient(g));
                for (int i = 0; i < 3; i++)
                {
                    var e = One(new Vector3(i, 0f, 0f));
                    e.startLifetime = 2f;
                    ps.Emit(e, 1);
                }
                ps.Pause();
                var sb = new StringBuilder();
                var l2 = new List<Vector4>();
                ps.GetCustomParticleData(list, ParticleSystemCustomData.Custom1);
                ps.GetCustomParticleData(l2, ParticleSystemCustomData.Custom2);
                for (int i = 0; i < list.Count; i++) sb.Append(" " + V4(list[i]) + "/" + V4(l2[i]));
                L("customdata emitted:" + sb);
                foreach (float step in new[] { 0.5f, 1f })
                {
                    ps.Simulate(step, true, false, false);
                    ps.GetCustomParticleData(list, ParticleSystemCustomData.Custom1);
                    ps.GetCustomParticleData(l2, ParticleSystemCustomData.Custom2);
                    sb.Clear();
                    for (int i = 0; i < list.Count; i++) sb.Append(" " + V4(list[i]) + "/" + V4(l2[i]));
                    L("customdata +" + F(step) + ":" + sb);
                }
                // Values set by script on a system whose module drives the stream.
                var set = new List<Vector4> { new Vector4(9f, 9f, 9f, 9f), new Vector4(9f, 9f, 9f, 9f), new Vector4(9f, 9f, 9f, 9f) };
                ps.SetCustomParticleData(set, ParticleSystemCustomData.Custom1);
                ps.Simulate(0.1f, true, false, false);
                ps.GetCustomParticleData(list, ParticleSystemCustomData.Custom1);
                L("customdata after set and +0.1: " + V4(list[0]));
                var r = ps.GetComponent<ParticleSystemRenderer>();
                r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.Custom1XYZW, ParticleSystemVertexStream.Custom2XYZW });
                Bake("customdata baked", ps, 4);
                Destroy(ps.gameObject);
            }

            // flip
            {
                foreach (var (mode, flip) in new[] { (ParticleSystemRenderMode.Billboard, new Vector3(1f, 0f, 0f)), (ParticleSystemRenderMode.Billboard, new Vector3(0f, 1f, 0f)), (ParticleSystemRenderMode.Billboard, new Vector3(0.5f, 0f, 0f)), (ParticleSystemRenderMode.Stretch, new Vector3(1f, 0.5f, 0f)) })
                {
                    var ps = NewSystem("Flip");
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    r.renderMode = mode;
                    r.flip = flip;
                    for (int i = 0; i < 8; i++)
                    {
                        var e = One(new Vector3(i * 3f, 0f, 0f));
                        e.rotation = 30f;
                        e.startSize = 1f;
                        ps.Emit(e, 1);
                    }
                    Bake("flip " + mode + " " + V(flip), ps, 32);
                    Destroy(ps.gameObject);
                }
            }

            // stretch: unflipped quads, the length and velocity scales, a pivot, a slanted velocity
            {
                foreach (var (label, lengthScale, velocityScale, pivot, velocity) in new[]
                {
                    ("plain", 2f, 0f, Vector3.zero, new Vector3(0f, 1f, 0f)),
                    ("scales", 1f, 0.5f, Vector3.zero, new Vector3(0f, 4f, 0f)),
                    ("pivot", 2f, 0f, new Vector3(0.1f, 0.8f, 0.3f), new Vector3(0f, 1f, 0f)),
                    ("slant", 2f, 0f, Vector3.zero, new Vector3(1f, 1f, 0f)),
                })
                {
                    var ps = NewSystem("Stretch");
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    r.renderMode = ParticleSystemRenderMode.Stretch;
                    r.lengthScale = lengthScale;
                    r.velocityScale = velocityScale;
                    r.pivot = pivot;
                    var e = One(new Vector3(1f, 2f, 0f));
                    e.startSize = 1f;
                    e.velocity = velocity;
                    e.rotation = 30f;
                    ps.Emit(e, 1);
                    Bake("stretch " + label, ps, 4);
                    Destroy(ps.gameObject);
                }
                var b = NewSystem("BillboardPivot");
                b.GetComponent<ParticleSystemRenderer>().pivot = new Vector3(0.1f, 0.2f, 0.3f);
                b.Emit(One(new Vector3(1f, 2f, 0f)), 1);
                Bake("billboard pivot", b, 4);
                Destroy(b.gameObject);
            }

            // sheet: UV2 and AnimBlend under a 2x2 texture sheet, half-way through frame 1
            {
                var ps = NewSystem("Sheet");
                var ts = ps.textureSheetAnimation;
                ts.enabled = true;
                ts.numTilesX = 2;
                ts.numTilesY = 2;
                ts.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
                var r = ps.GetComponent<ParticleSystemRenderer>();
                r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.AnimBlend, ParticleSystemVertexStream.AnimFrame });
                var e = One(new Vector3(1f, 2f, 0f));
                e.startLifetime = 4f;
                ps.Emit(e, 1);
                ps.Pause();
                ps.Simulate(1.5f, true, false, false);
                var buffer = new ParticleSystem.Particle[1];
                ps.GetParticles(buffer);
                L("sheet age=" + F(buffer[0].startLifetime - buffer[0].remainingLifetime));
                Bake("sheet", ps, 4);
                Destroy(ps.gameObject);
            }

            // meshsheet: a mesh particle under the same 2x2 sheet, frame 1, with UV2
            {
                var m = new Mesh();
                m.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
                m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.25f, 0.75f) };
                m.uv2 = new[] { new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f) };
                m.triangles = new[] { 0, 1, 2 };
                var ps = NewSystem("MeshSheet");
                var ts = ps.textureSheetAnimation;
                ts.enabled = true;
                ts.numTilesX = 2;
                ts.numTilesY = 2;
                ts.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
                var r = ps.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = m;
                r.alignment = ParticleSystemRenderSpace.World;
                r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.AnimBlend });
                var e = One(new Vector3(1f, 2f, 3f));
                e.velocity = Vector3.zero;
                ps.Emit(e, 1);
                ps.Pause();
                ps.Simulate(1.5f, true, false, false);
                Bake("meshsheet", ps, 3);
                Destroy(ps.gameObject);
                Destroy(m);
            }

            // meshbare: a mesh with positions and triangles only, under the leaves' streams
            {
                var m = new Mesh();
                m.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
                m.triangles = new[] { 0, 1, 2 };
                var ps = NewSystem("MeshBare");
                var r = ps.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = m;
                r.alignment = ParticleSystemRenderSpace.World;
                r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.Tangent });
                ps.Emit(One(new Vector3(1f, 2f, 3f)), 1);
                Bake("meshbare", ps, 3);
                Destroy(ps.gameObject);
                Destroy(m);
            }

            // meshpivot: a mesh whose bounds do not start at the origin
            {
                var m = new Mesh();
                m.vertices = new[] { new Vector3(2f, 3f, 4f), new Vector3(3f, 3f, 4f), new Vector3(2f, 5f, 4f), new Vector3(2f, 3f, 7f) };
                m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                m.RecalculateBounds();
                L("meshpivot bounds center=" + V(m.bounds.center) + " size=" + V(m.bounds.size));
                foreach (var (label, pivot, size3) in new[]
                {
                    ("none", Vector3.zero, new Vector3(1f, 1f, 1f)),
                    ("x", new Vector3(0.5f, 0f, 0f), new Vector3(1f, 1f, 1f)),
                    ("y", new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f)),
                    ("z", new Vector3(0f, 0f, 0.5f), new Vector3(1f, 1f, 1f)),
                    ("xyz size3", new Vector3(0.1f, 0.2f, 0.3f), new Vector3(1f, 2f, 3f)),
                })
                {
                    var ps = NewSystem("MeshPivot");
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    r.renderMode = ParticleSystemRenderMode.Mesh;
                    r.mesh = m;
                    r.alignment = ParticleSystemRenderSpace.World;
                    r.pivot = pivot;
                    var main = ps.main;
                    main.startSize3D = true;
                    var e = One(new Vector3(1f, 2f, 3f));
                    e.startSize3D = size3;
                    ps.Emit(e, 1);
                    Bake("meshpivot " + label, ps, 4);
                    Destroy(ps.gameObject);
                }
                Destroy(m);
            }

            // mesh
            {
                var m = new Mesh();
                m.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 2f, 0f), new Vector3(0f, 0f, 3f) };
                m.normals = new[] { new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f) };
                m.tangents = new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, -1f), new Vector4(0f, 1f, 0f, 1f) };
                m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
                m.uv2 = new[] { new Vector2(0.5f, 0.25f), new Vector2(0.75f, 0.25f), new Vector2(0.5f, 0.5f), new Vector2(0.75f, 0.5f) };
                m.colors32 = new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 255, 128) };
                m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                cam.transform.rotation = Quaternion.Euler(10f, 20f, 0f);
                foreach (var align in new[] { ParticleSystemRenderSpace.View, ParticleSystemRenderSpace.World, ParticleSystemRenderSpace.Local, ParticleSystemRenderSpace.Facing, ParticleSystemRenderSpace.Velocity })
                {
                    var ps = NewSystem("Mesh");
                    ps.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    r.renderMode = ParticleSystemRenderMode.Mesh;
                    r.mesh = m;
                    r.alignment = align;
                    var e = One(new Vector3(1f, 2f, 3f));
                    ps.Emit(e, 1);
                    Bake("mesh " + align + " plain", ps, 4);
                    if (align == ParticleSystemRenderSpace.Local) Bake("mesh " + align + " plain useTransform", ps, 4, true);
                    if (align == ParticleSystemRenderSpace.View || align == ParticleSystemRenderSpace.Local)
                    {
                        ps.Clear();
                        e.rotation = 30f;
                        ps.Emit(e, 1);
                        Bake("mesh " + align + " rot30", ps, 4);
                        ps.Clear();
                        var main = ps.main;
                        main.startRotation3D = true;
                        e.rotation3D = new Vector3(10f, 20f, 30f);
                        ps.Emit(e, 1);
                        Bake("mesh " + align + " rot3d", ps, 4);
                    }
                    Destroy(ps.gameObject);
                }
                cam.transform.rotation = Quaternion.identity;
                // Rotation, 3D rotation, 3D size, pivot, flip and streams, world alignment.
                {
                    var ps = NewSystem("Mesh2");
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    r.renderMode = ParticleSystemRenderMode.Mesh;
                    r.mesh = m;
                    r.alignment = ParticleSystemRenderSpace.World;
                    var e = One(new Vector3(1f, 2f, 3f));
                    e.rotation = 30f;
                    ps.Emit(e, 1);
                    Bake("mesh rot30", ps, 4);
                    ps.Clear();
                    var main = ps.main;
                    main.startRotation3D = true;
                    main.startSize3D = true;
                    e = One(new Vector3(1f, 2f, 3f));
                    e.rotation3D = new Vector3(10f, 20f, 30f);
                    e.startSize3D = new Vector3(1f, 2f, 3f);
                    ps.Emit(e, 1);
                    Bake("mesh rot3d size3d", ps, 4);
                    r.pivot = new Vector3(0.1f, 0.2f, 0.3f);
                    Bake("mesh rot3d size3d pivot", ps, 4);
                    r.pivot = Vector3.zero;
                    r.flip = new Vector3(1f, 0f, 0f);
                    Bake("mesh rot3d size3d flipx", ps, 4);
                    r.flip = Vector3.zero;
                    r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.Tangent });
                    Bake("mesh leaves streams", ps, 4);
                    r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.Custom1XYZW, ParticleSystemVertexStream.Custom2XYZ, ParticleSystemVertexStream.SizeX });
                    Bake("mesh ring streams", ps, 4);
                    Destroy(ps.gameObject);
                }
                Destroy(m);
            }

            yield return null;
            Destroy(cam.gameObject);
            var done = new GameObject("ParticleStreamsProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("=== done");
        }
    }
}
