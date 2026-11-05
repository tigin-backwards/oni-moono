// Unity members that mods call and ours lacked ("PROBE ... AG <part> ..."), at the main menu (10 s
// in). With PROBE_SCENARIO=apigaps only this probe runs; it ends with an ApiGapsProbeDone canvas.
// PROBE_AG_PARTS (comma-separated) runs only some parts. Parts:
//   rt      what RenderTexture.active binds: GL.Clear and GL drawing after setting it, against
//           Graphics.SetRenderTarget; the viewport after either; what active is after
//           SetRenderTarget, Blit, a camera's Render (and inside its onPostRender) and null;
//           whether setting it creates the texture.
//   shader  every loaded shader's property introspection (count, names, ids, types, flags,
//           descriptions, attributes, defaults, range limits, texture dimension and default
//           name, FindPropertyIndex), each getter on every property, out-of-range indices.
//   rtctor  each RenderTexture constructor: the description it makes.
//   web     UnityWebRequest.Post(string, string), UploadHandlerRaw, DownloadHandlerBuffer,
//           DownloadHandlerFile and UnityWebRequestTexture against a loopback server that logs
//           the raw request.
//   curve   AnimationCurve.RemoveKey and SmoothTangents.
//   copy    Graphics.CopyTexture between Texture2Ds, readable or not, and what Apply does after.
//   misc    GarbageCollector's incremental members, Camera.ResetCullingMatrix,
//           SpriteRenderer.drawMode, NativeArray with an Allocator.
//   ps      ParticleSystem bursts, velocity over lifetime, start size and rate multiplier.
//   bounds  Collider.bounds and Collider2D.bounds.
//   video   VideoPlayer source, url, renderMode, timeUpdateMode, sendFrameReadyEvents, width,
//           height, errorReceived and frameReady.
//   gui     GUILayout.SelectionGrid and PasswordField: their rects and results.
#pragma warning disable CS0618 // the obsolete members are what mods call
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

namespace SemanticsProbe
{
    public class ApiGaps : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static string part = "";
        static void L(string s) => Rec.Log("AG " + part + " " + s);
        static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        static string F(double f) => f.ToString("R", CultureInfo.InvariantCulture);
        static string V(Vector2 v) => "(" + F(v.x) + "," + F(v.y) + ")";
        static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
        static string V(Vector4 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "," + F(v.w) + ")";
        static string R(Rect r) => "(" + F(r.x) + "," + F(r.y) + "," + F(r.width) + "," + F(r.height) + ")";
        static string Ex(Exception e) => e.GetType().Name + ": " + e.Message.Replace("\n", "\\n");

        static string Try(Func<object> f)
        {
            try
            {
                var v = f();
                switch (v)
                {
                    case null: return "null";
                    case float x: return F(x);
                    case Vector2 x: return V(x);
                    case Vector3 x: return V(x);
                    case Vector4 x: return V(x);
                    case Color x: return V((Vector4)x);
                    case string s: return "'" + s + "'";
                    case string[] a: return "[" + string.Join("|", a) + "]";
                    case UnityEngine.Object o: return o == null ? "destroyed" : "'" + o.name + "'";
                    default: return Convert.ToString(v, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                return "THROWS " + Ex(e);
            }
        }

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static bool Wanted(string name)
        {
            var parts = Environment.GetEnvironmentVariable("PROBE_AG_PARTS");
            return string.IsNullOrEmpty(parts) || parts.Split(',').Contains(name);
        }

        IEnumerator Run()
        {
            Application.logMessageReceived += (msg, trace, type) =>
            {
                if (type != LogType.Log) Rec.Log("AG log " + type + " " + msg.Replace("\n", "\\n"));
            };
            var steps = new (string, Func<IEnumerator>)[]
            {
                ("rt", ActiveParts), ("shader", ShaderParts), ("rtctor", CtorParts), ("web", WebParts), ("curve", CurveParts),
                ("copy", CopyParts), ("misc", MiscParts), ("ps", ParticleParts), ("bounds", BoundsParts), ("video", VideoParts),
                ("gui", GuiParts),
            };
            foreach (var (name, run) in steps)
            {
                if (!Wanted(name)) continue;
                part = name;
                L("begin");
                var e = run();
                while (true)
                {
                    object current;
                    try
                    {
                        if (!e.MoveNext()) break;
                        current = e.Current;
                    }
                    catch (Exception ex)
                    {
                        L("PART THREW " + Ex(ex) + " " + ex.StackTrace.Replace("\n", " | "));
                        break;
                    }
                    yield return current;
                }
                L("end");
            }
            part = "";
            var done = new GameObject("ApiGapsProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("done");
        }

        // ----- rt -----

        static string Name(RenderTexture t) => t == null ? "null" : t.name;

        static Texture2D readback;

        /// The texture's texels, read through Graphics.SetRenderTarget (which binds in both
        /// players), as "rrggbbaa" per texel bottom row first; the previous target is restored
        /// the same way.
        static string Texels(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            if (readback == null || readback.width != rt.width || readback.height != rt.height)
                readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            Graphics.SetRenderTarget(rt);
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            Graphics.SetRenderTarget(prev);
            var px = readback.GetPixels32();
            var sb = new StringBuilder();
            for (int i = 0; i < px.Length; i++)
            {
                if (i > 0) sb.Append(i % rt.width == 0 ? "/" : " ");
                sb.Append(px[i].r.ToString("x2")).Append(px[i].g.ToString("x2")).Append(px[i].b.ToString("x2")).Append(px[i].a.ToString("x2"));
            }
            return sb.ToString();
        }

        /// A single texel's colour name, when the texture is uniform.
        static string Uniform(RenderTexture rt)
        {
            var t = Texels(rt);
            var parts = t.Split(' ', '/').Distinct().ToArray();
            return parts.Length == 1 ? parts[0] : t;
        }

        static void Fill(RenderTexture rt, Color32 c)
        {
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(rt);
            GL.Clear(true, true, c);
            Graphics.SetRenderTarget(prev);
        }

        static Material colorMat;

        /// A quad over [0,1]² in GL.LoadOrtho's space, coloured c.
        static void Quad(Color c)
        {
            if (colorMat == null) colorMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            GL.PushMatrix();
            GL.LoadOrtho();
            colorMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(0, 0, 0);
            GL.Vertex3(1, 0, 0);
            GL.Vertex3(1, 1, 0);
            GL.Vertex3(0, 1, 0);
            GL.End();
            GL.PopMatrix();
        }

        static RenderTexture NewRt(string name, int size = 4)
        {
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32) { name = name };
            rt.Create();
            return rt;
        }

        static readonly Color32 Grey = new Color32(40, 40, 40, 255), Red = new Color32(255, 0, 0, 255), Green = new Color32(0, 255, 0, 255),
            Blue = new Color32(0, 0, 255, 255), Yellow = new Color32(255, 255, 0, 255), Cyan = new Color32(0, 255, 255, 255);

        IEnumerator ActiveParts()
        {
            L("start active=" + Name(RenderTexture.active));
            var a = NewRt("A");
            var b = NewRt("B");
            var a8 = NewRt("A8", 8);
            var b8 = NewRt("B8", 8);

            Fill(a, Grey); Fill(b, Grey);
            RenderTexture.active = a;
            L("set A: active=" + Name(RenderTexture.active));
            GL.Clear(false, true, Red);
            L("set A, GL.Clear red: A=" + Uniform(a) + " B=" + Uniform(b));

            Fill(a, Grey); Fill(b, Grey);
            Graphics.SetRenderTarget(b);
            RenderTexture.active = a;
            L("SetRenderTarget B then set A: active=" + Name(RenderTexture.active));
            GL.Clear(false, true, Green);
            L("SetRenderTarget B, set A, GL.Clear green: A=" + Uniform(a) + " B=" + Uniform(b));

            Fill(a, Grey); Fill(b, Grey);
            RenderTexture.active = b;
            Graphics.SetRenderTarget(a);
            L("set B then SetRenderTarget A: active=" + Name(RenderTexture.active));
            GL.Clear(false, true, Yellow);
            L("set B, SetRenderTarget A, GL.Clear yellow: A=" + Uniform(a) + " B=" + Uniform(b));

            Fill(a, Grey); Fill(b, Grey);
            RenderTexture.active = a;
            Quad(Blue);
            L("set A, GL quad blue: A=" + Uniform(a) + " B=" + Uniform(b));

            // The viewport: a small one set on the previous target, then the target changes.
            foreach (var how in new[] { "set", "SetRenderTarget", "set-same", "SetRenderTarget-same" })
            {
                Fill(a8, Grey); Fill(b8, Grey);
                Graphics.SetRenderTarget(a8);
                GL.Viewport(new Rect(0, 0, 2, 2));
                var next = how.EndsWith("-same") ? a8 : b8;
                if (how.StartsWith("set")) RenderTexture.active = next;
                else Graphics.SetRenderTarget(next);
                Quad(Cyan);
                L("viewport 2x2 on A8, then " + how + " " + next.name + ", quad cyan: A8=" + Texels(a8) + " B8=" + Texels(b8));
                Fill(a8, Grey); Fill(b8, Grey);
                Graphics.SetRenderTarget(a8);
                GL.Viewport(new Rect(0, 0, 2, 2));
                if (how.StartsWith("set")) RenderTexture.active = next;
                else Graphics.SetRenderTarget(next);
                GL.Clear(false, true, Cyan);
                L("viewport 2x2 on A8, then " + how + " " + next.name + ", GL.Clear cyan: A8=" + Texels(a8) + " B8=" + Texels(b8));
            }

            // ReadPixels after setting active alone.
            Fill(a, Red); Fill(b, Green);
            Graphics.SetRenderTarget(b);
            RenderTexture.active = a;
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
            var c = t.GetPixel(1, 1);
            L("SetRenderTarget B (green), set A (red), ReadPixels: " + V((Vector4)c));

            // Not created: does setting active create it?
            var nc = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32) { name = "NotCreated" };
            L("new: created=" + nc.IsCreated());
            RenderTexture.active = nc;
            L("set new: created=" + nc.IsCreated() + " active=" + Name(RenderTexture.active));
            GL.Clear(false, true, Red);
            L("set new, GL.Clear red: NotCreated=" + Uniform(nc));
            RenderTexture.active = null;

            // What active is after the ways a target changes.
            Graphics.SetRenderTarget(a);
            L("after SetRenderTarget A: active=" + Name(RenderTexture.active));
            Graphics.SetRenderTarget(null);
            L("after SetRenderTarget null: active=" + Name(RenderTexture.active));
            RenderTexture.active = a;
            Graphics.Blit(Texture2D.whiteTexture, b);
            L("set A, Blit to B: active=" + Name(RenderTexture.active));
            RenderTexture.active = a;
            Graphics.Blit(Texture2D.whiteTexture, b, colorMat);
            L("set A, Blit to B with a material: active=" + Name(RenderTexture.active));

            var camTarget = new RenderTexture(4, 4, 24, RenderTextureFormat.ARGB32) { name = "CamTarget" };
            camTarget.Create();
            var go = new GameObject("ApiGapsCamera");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.targetTexture = camTarget;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;
            string inside = "";
            Camera.CameraCallback post = cc =>
            {
                if (cc != cam) return;
                inside += " onPostRender active=" + Name(RenderTexture.active);
                Fill(a, Grey);
                RenderTexture.active = a;
                GL.Clear(false, true, Yellow);
                inside += " (set A, cleared yellow)";
            };
            foreach (var before in new[] { (RenderTexture)null, b })
            {
                inside = "";
                Fill(a, Grey); Fill(camTarget, Grey);
                Camera.onPostRender += post;
                RenderTexture.active = before;
                cam.Render();
                Camera.onPostRender -= post;
                L("set " + Name(before) + ", Render a camera into CamTarget: active=" + Name(RenderTexture.active) + inside + " A=" + Uniform(a) + " CamTarget=" + Uniform(camTarget));
                Fill(a, Grey);
                GL.Clear(false, true, Green);
                L("  then GL.Clear green: A=" + Uniform(a) + " B=" + Uniform(b) + " CamTarget=" + Uniform(camTarget));
            }
            Destroy(go);

            // null: the screen.
            Graphics.SetRenderTarget(a);
            RenderTexture.active = null;
            L("SetRenderTarget A, set null: active=" + Name(RenderTexture.active));
            Fill(a, Grey);
            GL.Clear(false, true, Red);
            var screen = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            screen.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
            L("SetRenderTarget A, set null, GL.Clear red: A=" + Uniform(a) + " screen(0,0)=" + V((Vector4)screen.GetPixel(0, 0)));
            yield return null;
            L("next frame: active=" + Name(RenderTexture.active));
            RenderTexture.active = a;
            yield return null;
            L("set A, next frame: active=" + Name(RenderTexture.active));
            yield return new WaitForEndOfFrame();
            L("at end of frame: active=" + Name(RenderTexture.active));
            RenderTexture.active = a;
            Fill(b, Grey);
            yield return new WaitForEndOfFrame();
            L("set A, at end of frame: active=" + Name(RenderTexture.active));
            RenderTexture.active = null;
        }

        // ----- shader -----

        IEnumerator ShaderParts()
        {
            var shaders = Resources.FindObjectsOfTypeAll<Shader>().OrderBy(s => s.name, StringComparer.Ordinal).ToArray();
            L("shaders " + shaders.Length);
            foreach (var s in shaders)
            {
                int n = s.GetPropertyCount();
                L("shader '" + s.name + "' count=" + n + " find(_MainTex)=" + Try(() => s.FindPropertyIndex("_MainTex")) + " find(nope)=" + Try(() => s.FindPropertyIndex("nope"))
                  + " find(null)=" + Try(() => s.FindPropertyIndex(null)) + " find('')=" + Try(() => s.FindPropertyIndex("")));
                for (int i = -1; i <= n; i++)
                {
                    int k = i;
                    L("  [" + k + "] name=" + Try(() => s.GetPropertyName(k)) + " id=" + Try(() => s.GetPropertyNameId(k))
                      + " idOk=" + Try(() => s.GetPropertyNameId(k) == Shader.PropertyToID(s.GetPropertyName(k)))
                      + " find=" + Try(() => s.FindPropertyIndex(s.GetPropertyName(k)))
                      + " findUpper=" + Try(() => s.FindPropertyIndex(s.GetPropertyName(k).ToUpperInvariant()))
                      + " type=" + Try(() => s.GetPropertyType(k)) + " flags=" + Try(() => s.GetPropertyFlags(k)) + " (" + Try(() => (int)s.GetPropertyFlags(k)) + ")"
                      + " desc=" + Try(() => s.GetPropertyDescription(k)) + " attrs=" + Try(() => s.GetPropertyAttributes(k))
                      + " float=" + Try(() => s.GetPropertyDefaultFloatValue(k)) + " vector=" + Try(() => s.GetPropertyDefaultVectorValue(k))
                      + " range=" + Try(() => s.GetPropertyRangeLimits(k)) + " dim=" + Try(() => s.GetPropertyTextureDimension(k))
                      + " texdef=" + Try(() => s.GetPropertyTextureDefaultName(k)));
                    if (k >= 0 && k < n) L("  [" + k + "] int=" + Try(() => s.GetPropertyDefaultIntValue(k)));
                }
            }
            yield break;
        }

        // ----- rtctor -----

        static void Describe(string what, Func<RenderTexture> make)
        {
            RenderTexture rt;
            try
            {
                rt = make();
            }
            catch (Exception e)
            {
                L(what + " THROWS " + Ex(e));
                return;
            }
            if (rt == null) { L(what + " null"); return; }
            var d = rt.descriptor;
            L(what + ": " + rt.width + "x" + rt.height + " depth=" + rt.depth + " format=" + Try(() => rt.format) + " graphicsFormat=" + Try(() => rt.graphicsFormat)
              + " depthStencilFormat=" + Try(() => rt.depthStencilFormat) + " stencilFormat=" + Try(() => rt.stencilFormat) + " sRGB=" + rt.sRGB
              + " useMipMap=" + rt.useMipMap + " autoGenerateMips=" + rt.autoGenerateMips + " mipmapCount=" + rt.mipmapCount + " aa=" + rt.antiAliasing
              + " dimension=" + Try(() => rt.dimension) + " volumeDepth=" + rt.volumeDepth + " created=" + rt.IsCreated() + " name='" + rt.name + "'"
              + " filter=" + rt.filterMode + " wrap=" + rt.wrapMode + " aniso=" + rt.anisoLevel + " readable=" + rt.isReadable
              + " | desc " + d.width + "x" + d.height + " colorFormat=" + d.colorFormat + " graphicsFormat=" + d.graphicsFormat + " depthBufferBits=" + d.depthBufferBits
              + " depthStencilFormat=" + Try(() => d.depthStencilFormat) + " sRGB=" + d.sRGB + " mipCount=" + d.mipCount + " msaa=" + d.msaaSamples + " useMipMap=" + d.useMipMap
              + " autoGenerateMips=" + d.autoGenerateMips + " dimension=" + d.dimension + " volumeDepth=" + d.volumeDepth);
            try
            {
                L(what + "   create=" + rt.Create() + " created=" + rt.IsCreated() + " mipmapCount=" + rt.mipmapCount);
                rt.Release();
            }
            catch (Exception e)
            {
                L(what + "   create THROWS " + Ex(e));
            }
            Destroy(rt);
        }

        IEnumerator CtorParts()
        {
            L("colorSpace=" + QualitySettings.activeColorSpace + " LDR=" + SystemInfo.GetGraphicsFormat(DefaultFormat.LDR) + " HDR=" + SystemInfo.GetGraphicsFormat(DefaultFormat.HDR));
            foreach (DefaultFormat f in Enum.GetValues(typeof(DefaultFormat)))
                L("SystemInfo.GetGraphicsFormat(" + f + ")=" + Try(() => SystemInfo.GetGraphicsFormat(f)));
            Describe("(16,8,24)", () => new RenderTexture(16, 8, 24));
            Describe("(16,8,24,ARGB32)", () => new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32));
            foreach (DefaultFormat f in Enum.GetValues(typeof(DefaultFormat)))
            {
                var ff = f;
                Describe("(16,8,0," + f + ")", () => new RenderTexture(16, 8, 0, ff));
                Describe("(16,8,24," + f + ")", () => new RenderTexture(16, 8, 24, ff));
            }
            foreach (var g in new[] { GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.R32_SFloat,
                         GraphicsFormat.B8G8R8A8_UNorm, GraphicsFormat.None, GraphicsFormat.D24_UNorm_S8_UInt, GraphicsFormat.RGBA_DXT5_UNorm })
            {
                var gg = g;
                Describe("(16,8,0," + g + ")", () => new RenderTexture(16, 8, 0, gg));
                Describe("(16,8,24," + g + ")", () => new RenderTexture(16, 8, 24, gg));
                Describe("(16,8,16," + g + ",mip 3)", () => new RenderTexture(16, 8, 16, gg, 3));
            }
            foreach (var mips in new[] { -1, 0, 1, 3, 5, 6, 20 })
            {
                var m = mips;
                Describe("(16,8,24,R8G8B8A8_UNorm,mip " + m + ")", () => new RenderTexture(16, 8, 24, GraphicsFormat.R8G8B8A8_UNorm, m));
                Describe("(16,8,24,ARGB32,mip " + m + ")", () => new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, m));
            }
            foreach (var f in new[] { RenderTextureFormat.ARGB32, RenderTextureFormat.Default, RenderTextureFormat.ARGBHalf, RenderTextureFormat.RFloat, RenderTextureFormat.DefaultHDR,
                         RenderTextureFormat.Depth, RenderTextureFormat.R8, RenderTextureFormat.BGRA32 })
            {
                var ff = f;
                Describe("(16,8,0," + f + ",mip 2)", () => new RenderTexture(16, 8, 0, ff, 2));
                foreach (RenderTextureReadWrite rw in Enum.GetValues(typeof(RenderTextureReadWrite)))
                {
                    var r = rw;
                    Describe("(16,8,24," + f + "," + rw + ")", () => new RenderTexture(16, 8, 24, ff, r));
                }
            }
            Describe("(16,8,0,ARGB32,(RenderTextureReadWrite)7)", () => new RenderTexture(16, 8, 0, RenderTextureFormat.ARGB32, (RenderTextureReadWrite)7));
            foreach (var (c, ds) in new[]
                     {
                         (GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.D24_UNorm_S8_UInt), (GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.None),
                         (GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.D32_SFloat), (GraphicsFormat.None, GraphicsFormat.D24_UNorm_S8_UInt),
                         (GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.D16_UNorm), (GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.R8G8B8A8_UNorm),
                         (GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.D32_SFloat_S8_UInt), (GraphicsFormat.None, GraphicsFormat.None),
                     })
            {
                var (cc, dd) = (c, ds);
                Describe("(16,8," + c + "," + ds + ")", () => new RenderTexture(16, 8, cc, dd));
                Describe("(16,8," + c + "," + ds + ",mip 3)", () => new RenderTexture(16, 8, cc, dd, 3));
                Describe("(16,8," + c + "," + ds + ",mip 0)", () => new RenderTexture(16, 8, cc, dd, 0));
            }
            Describe("(0,8,24,ARGB32,Linear)", () => new RenderTexture(0, 8, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear));
            var temp = RenderTexture.GetTemporary(16, 8, 0);
            L("GetTemporary(16,8,0): wrap=" + temp.wrapMode + " filter=" + temp.filterMode + " aniso=" + temp.anisoLevel);
            RenderTexture.ReleaseTemporary(temp);
            Describe("(desc 16x8 ARGB32 24)", () => new RenderTexture(new RenderTextureDescriptor(16, 8, RenderTextureFormat.ARGB32, 24)));
            Describe("(8,0,24,ARGB32,Linear)", () => new RenderTexture(8, 0, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear));
            Describe("(16,8,24,ARGB32,mip 6) useMipMap", () => new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, 6) { useMipMap = true });
            Describe("(16,8,24,ARGB32,mip 3) useMipMap", () => new RenderTexture(16, 8, 24, RenderTextureFormat.ARGB32, 3) { useMipMap = true });
            Describe("(16,8,16,DepthStencil)", () => new RenderTexture(16, 8, 16, DefaultFormat.DepthStencil));
            Describe("(16,8,32,Shadow)", () => new RenderTexture(16, 8, 32, DefaultFormat.Shadow));
            Describe("(16,8,R8G8B8A8_UNorm,D16_UNorm_S8_UInt)", () => new RenderTexture(16, 8, GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.D16_UNorm_S8_UInt));
            Describe("(16,8,D24_UNorm_S8_UInt,D16_UNorm)", () => new RenderTexture(16, 8, GraphicsFormat.D24_UNorm_S8_UInt, GraphicsFormat.D16_UNorm));
            Describe("(16,8,7,ARGB32,Linear)", () => new RenderTexture(16, 8, 7, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear));
            Describe("(16,8,17,R8G8B8A8_UNorm)", () => new RenderTexture(16, 8, 17, GraphicsFormat.R8G8B8A8_UNorm));
            Describe("(16,8,32,R8G8B8A8_UNorm)", () => new RenderTexture(16, 8, 32, GraphicsFormat.R8G8B8A8_UNorm));
            Describe("(-4,8,0,R8G8B8A8_UNorm,D24_UNorm_S8_UInt)", () => new RenderTexture(-4, 8, GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.D24_UNorm_S8_UInt));
            // Rendering into the sRGB variants: a mid grey cleared and read back.
            foreach (var rw in new[] { RenderTextureReadWrite.Linear, RenderTextureReadWrite.sRGB, RenderTextureReadWrite.Default })
            {
                var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32, rw) { name = "rw" };
                Fill(rt, new Color32(128, 128, 128, 255));
                L("clear 128 into ARGB32 " + rw + ": " + Uniform(rt));
                var t2 = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                t2.SetPixel(0, 0, new Color32(128, 128, 128, 255));
                t2.Apply();
                Graphics.Blit(t2, rt);
                L("blit 128 into ARGB32 " + rw + ": " + Uniform(rt));
                Destroy(rt);
            }
            yield break;
        }

        // ----- web -----

        sealed class LoopbackServer
        {
            readonly TcpListener listener;
            public readonly int Port;
            public readonly List<string> Requests = new List<string>();
            public byte[] Png;
            volatile bool stop;

            public LoopbackServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                new Thread(Loop) { IsBackground = true }.Start();
            }

            public void Stop() { stop = true; listener.Stop(); }

            void Loop()
            {
                while (!stop)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch { return; }
                    try { Serve(client); }
                    catch (Exception e) { lock (Requests) Requests.Add("SERVER " + Ex(e)); }
                }
            }

            void Serve(TcpClient client)
            {
                using (client)
                {
                    var s = client.GetStream();
                    var head = new List<byte>();
                    while (true)
                    {
                        int b = s.ReadByte();
                        if (b < 0) return;
                        head.Add((byte)b);
                        int n = head.Count;
                        if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
                    }
                    var headText = Encoding.ASCII.GetString(head.ToArray());
                    var lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    int length = 0;
                    bool chunked = false;
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                        if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && line.Contains("chunked")) chunked = true;
                    }
                    var body = new MemoryStream();
                    if (chunked)
                    {
                        while (true)
                        {
                            var sizeLine = new StringBuilder();
                            int c;
                            while ((c = s.ReadByte()) >= 0 && c != '\n') if (c != '\r') sizeLine.Append((char)c);
                            int size = int.Parse(sizeLine.ToString().Split(';')[0], NumberStyles.HexNumber);
                            if (size == 0) { s.ReadByte(); s.ReadByte(); break; }
                            var buf = new byte[size];
                            int got = 0;
                            while (got < size) got += s.Read(buf, got, size - got);
                            body.Write(buf, 0, size);
                            s.ReadByte(); s.ReadByte();
                        }
                    }
                    else
                    {
                        var buf = new byte[length];
                        int got = 0;
                        while (got < length) { int r = s.Read(buf, got, length - got); if (r <= 0) break; got += r; }
                        body.Write(buf, 0, got);
                    }
                    var bodyText = Encoding.UTF8.GetString(body.ToArray());
                    var sorted = lines.Skip(1).Where(l => !l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
                    lock (Requests) Requests.Add(lines[0] + " {" + string.Join("; ", sorted) + "} chunked=" + chunked + " body[" + body.Length + "]='" + bodyText + "'");
                    var path = lines[0].Split(' ')[1];
                    byte[] payload;
                    string type;
                    int status = 200;
                    if (path.StartsWith("/png")) { payload = Png; type = "image/png"; }
                    else if (path.StartsWith("/404")) { payload = Encoding.UTF8.GetBytes("missing"); type = "text/plain"; status = 404; }
                    else { payload = Encoding.UTF8.GetBytes("hello é"); type = "text/plain; charset=utf-8"; }
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(response, 0, response.Length);
                    s.Write(payload, 0, payload.Length);
                    s.Flush();
                }
            }
        }

        static string Handlers(UnityWebRequest r)
        {
            var u = r.uploadHandler;
            var d = r.downloadHandler;
            return "upload=" + (u == null ? "null" : u.GetType().Name + " contentType=" + Try(() => u.contentType) + " data=" + Try(() => u.data == null ? "null" : u.data.Length.ToString())
                                                    + " progress=" + Try(() => u.progress))
                   + " download=" + (d == null ? "null" : d.GetType().Name)
                   + " method=" + r.method + " Content-Type=" + Try(() => r.GetRequestHeader("Content-Type"))
                   + " chunked=" + Try(() => r.chunkedTransfer) + " useHttpContinue=" + Try(() => r.useHttpContinue);
        }

        static string Outcome(UnityWebRequest r)
        {
            var d = r.downloadHandler;
            return "result=" + r.result + " code=" + r.responseCode + " error=" + Try(() => r.error) + " uploaded=" + r.uploadedBytes + " downloaded=" + r.downloadedBytes
                   + " uploadProgress=" + F(r.uploadProgress)
                   + (d == null ? "" : " text=" + Try(() => d.text) + " data=" + Try(() => d.data == null ? "null" : d.data.Length.ToString()) + " isDone=" + Try(() => d.isDone)
                                       + " dlError=" + Try(() => d.error))
                   + (r.uploadHandler == null ? "" : " uploadProgress(handler)=" + Try(() => r.uploadHandler.progress));
        }

        IEnumerator Send(LoopbackServer server, string what, UnityWebRequest r)
        {
            L(what + " before: " + Handlers(r));
            yield return r.SendWebRequest();
            L(what + " after: " + Outcome(r));
            yield return new WaitForSecondsRealtime(0.2f);
            lock (server.Requests)
            {
                foreach (var q in server.Requests) L(what + " server: " + q);
                server.Requests.Clear();
            }
        }

        IEnumerator WebParts()
        {
            var server = new LoopbackServer();
            var png = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            var px = new Color32[8];
            for (int i = 0; i < 8; i++) px[i] = new Color32((byte)(i * 30), 100, 200, 255);
            png.SetPixels32(px);
            png.Apply();
            server.Png = png.EncodeToPNG();
            string url = "http://127.0.0.1:" + server.Port + "/x";

            var raw = new UploadHandlerRaw(new byte[] { 1, 2, 3 });
            L("UploadHandlerRaw([1,2,3]): contentType=" + Try(() => raw.contentType) + " data=" + Try(() => string.Join(",", raw.data)) + " progress=" + Try(() => raw.progress)
              + " sameArray=" + Try(() => ReferenceEquals(raw.data, raw.data)));
            raw.contentType = "application/json";
            L("contentType set: " + Try(() => raw.contentType));
            raw.Dispose();
            L("disposed: data=" + Try(() => raw.data) + " contentType=" + Try(() => raw.contentType));
            var empty = new UploadHandlerRaw(null);
            L("UploadHandlerRaw(null): data=" + Try(() => empty.data) + " contentType=" + Try(() => empty.contentType));
            var zero = new UploadHandlerRaw(new byte[0]);
            L("UploadHandlerRaw([]): data=" + Try(() => zero.data == null ? "null" : zero.data.Length.ToString()));
            var buffer = new DownloadHandlerBuffer();
            L("DownloadHandlerBuffer new: text=" + Try(() => buffer.text) + " data=" + Try(() => buffer.data) + " isDone=" + buffer.isDone + " error=" + Try(() => buffer.error));

            foreach (var data in new[] { "a=1&b=two words", "", "{\"k\":\"é\"}", null })
            {
                var d = data;
                UnityWebRequest r = null;
                L("Post(url, " + (d == null ? "null" : "'" + d + "'") + ") = " + Try(() => (r = UnityWebRequest.Post(url, d)) == null ? "null" : "ok"));
                if (r == null) continue;
                yield return Send(server, "Post '" + d + "'", r);
                r.Dispose();
            }

            var ascii = new StringBuilder();
            for (char ch = ' '; ch < (char)0x7f; ch++) ascii.Append(ch);
            ascii.Append("\t\n\u00ff\u4e2d");
            var all = UnityWebRequest.Post(url, ascii.ToString());
            yield return Send(server, "Post printable ASCII", all);
            all.Dispose();

            var p = UnityWebRequest.Post(url, "");
            p.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{\"a\":1}")) { contentType = "application/json" };
            yield return Send(server, "Post '' then raw application/json", p);
            p.Dispose();

            p = UnityWebRequest.Post(url, "");
            p.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{\"a\":1}"));
            yield return Send(server, "Post '' then uploadHandler raw", p);
            p.Dispose();

            p = UnityWebRequest.Post(url, "");
            var u2 = new UploadHandlerRaw(Encoding.UTF8.GetBytes("xyz")) { contentType = "application/json" };
            p.uploadHandler = u2;
            p.SetRequestHeader("Content-Type", "text/x-header");
            yield return Send(server, "Post '' then raw application/json + header text/x-header", p);
            p.Dispose();
            L("after Dispose: upload data=" + Try(() => u2.data));

            p = new UnityWebRequest(url, "PUT");
            p.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("put body"));
            p.downloadHandler = new DownloadHandlerBuffer();
            yield return Send(server, "PUT raw + buffer", p);
            p.Dispose();

            p = new UnityWebRequest(url, "POST");
            p.uploadHandler = new UploadHandlerRaw(new byte[0]);
            p.downloadHandler = new DownloadHandlerBuffer();
            yield return Send(server, "POST empty raw", p);
            p.Dispose();

            p = new UnityWebRequest(url, "GET");
            p.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("get body"));
            p.downloadHandler = new DownloadHandlerBuffer();
            yield return Send(server, "GET with raw", p);
            p.Dispose();

            p = new UnityWebRequest(url, "POST", new DownloadHandlerBuffer(), new UploadHandlerRaw(Encoding.UTF8.GetBytes("ctor body")));
            yield return Send(server, "ctor(url, POST, buffer, raw)", p);
            p.Dispose();
            p = new UnityWebRequest(new Uri(url), "POST", null, null);
            yield return Send(server, "ctor(uri, POST, null, null)", p);
            p.Dispose();

            p = UnityWebRequest.Get("http://127.0.0.1:" + server.Port + "/404");
            yield return Send(server, "Get 404", p);
            p.Dispose();

            p = UnityWebRequest.Post(url, "x");
            L("Post: set uploadHandler after send? " + Try(() => { p.SendWebRequest(); p.uploadHandler = new UploadHandlerRaw(new byte[] { 1 }); return "set"; }));
            while (!p.isDone) yield return null;
            p.Dispose();
            lock (server.Requests) server.Requests.Clear();

            var file = Path.Combine(Path.GetTempPath(), "apigaps-dl-" + Guid.NewGuid().ToString("N") + ".txt");
            var fh = new DownloadHandlerFile(file);
            L("DownloadHandlerFile new: removeFileOnAbort=" + fh.removeFileOnAbort + " exists=" + File.Exists(file) + " text=" + Try(() => fh.text) + " data=" + Try(() => fh.data));
            p = new UnityWebRequest(url, "GET", fh, null);
            yield return Send(server, "Get into file", p);
            L("file: exists=" + File.Exists(file) + " bytes=" + (File.Exists(file) ? string.Join(",", File.ReadAllBytes(file)) : ""));
            p.Dispose();
            L("disposed: exists=" + File.Exists(file));
            File.Delete(file);
            var dir = Path.Combine(Path.GetTempPath(), "apigaps-dir-" + Guid.NewGuid().ToString("N"));
            var nested = Path.Combine(dir, "a", "b.txt");
            fh = new DownloadHandlerFile(nested, true);
            L("DownloadHandlerFile(nested, append): exists dir=" + Directory.Exists(Path.Combine(dir, "a")));
            p = new UnityWebRequest(url, "GET", fh, null);
            yield return Send(server, "Get into nested file", p);
            L("nested file: exists=" + File.Exists(nested) + " bytes=" + (File.Exists(nested) ? File.ReadAllBytes(nested).Length.ToString() : ""));
            p.Dispose();
            File.WriteAllText(nested, "pre-");
            fh = new DownloadHandlerFile(nested, true);
            p = new UnityWebRequest(url, "GET", fh, null);
            yield return Send(server, "Get appending", p);
            L("appended: '" + File.ReadAllText(nested) + "'");
            p.Dispose();
            fh = new DownloadHandlerFile(nested);
            p = new UnityWebRequest(url, "GET", fh, null);
            yield return Send(server, "Get overwriting", p);
            L("overwritten: '" + File.ReadAllText(nested) + "'");
            p.Dispose();
            fh = new DownloadHandlerFile(nested) { removeFileOnAbort = true };
            p = new UnityWebRequest("http://127.0.0.1:" + server.Port + "/404", "GET", fh, null);
            yield return Send(server, "Get 404 into file", p);
            L("404 file: exists=" + File.Exists(nested) + " text=" + (File.Exists(nested) ? File.ReadAllText(nested) : ""));
            p.Dispose();
            File.WriteAllText(nested, "kept");
            fh = new DownloadHandlerFile(nested);
            p = new UnityWebRequest("http://127.0.0.1:" + server.Port + "/404", "GET", fh, null);
            yield return Send(server, "Get 404 into file, removeFileOnAbort false", p);
            L("404 file kept: exists=" + File.Exists(nested) + " text=" + (File.Exists(nested) ? File.ReadAllText(nested) : ""));
            p.Dispose();
            fh = new DownloadHandlerFile(nested) { removeFileOnAbort = true };
            p = new UnityWebRequest("http://127.0.0.1:1/x", "GET", fh, null);
            yield return Send(server, "Get refused into file, removeFileOnAbort", p);
            L("refused file: exists=" + File.Exists(nested));
            p.Dispose();
            var buf404 = new DownloadHandlerBuffer();
            p = new UnityWebRequest("http://127.0.0.1:1/x", "GET", buf404, null);
            yield return Send(server, "Get refused into buffer", p);
            p.Dispose();
            Directory.Delete(dir, true);

            foreach (var nonReadable in new[] { false, true })
            {
                p = nonReadable ? UnityWebRequestTexture.GetTexture("http://127.0.0.1:" + server.Port + "/png", true) : UnityWebRequestTexture.GetTexture("http://127.0.0.1:" + server.Port + "/png");
                L("GetTexture(" + nonReadable + "): " + Handlers(p));
                yield return Send(server, "GetTexture " + nonReadable, p);
                var dht = (DownloadHandlerTexture)p.downloadHandler;
                var tex = Try(() => { var t = dht.texture; return t == null ? "null" : t.width + "x" + t.height + " " + t.format + " mips=" + t.mipmapCount + " readable=" + t.isReadable + " wrap=" + t.wrapMode + " filter=" + t.filterMode + " name='" + t.name + "'" + (t.isReadable ? " px(1,0)=" + V((Vector4)t.GetPixel(1, 0)) : ""); });
                L("  texture: " + tex + " same=" + Try(() => ReferenceEquals(dht.texture, dht.texture)) + " content=" + Try(() => DownloadHandlerTexture.GetContent(p) == dht.texture));
                p.Dispose();
            }
            p = UnityWebRequestTexture.GetTexture(url);
            yield return Send(server, "GetTexture of text", p);
            L("  texture: " + Try(() => { var t = ((DownloadHandlerTexture)p.downloadHandler).texture; return t == null ? "null" : t.width + "x" + t.height + " " + t.format; }));
            p.Dispose();
            var own = new DownloadHandlerTexture();
            L("DownloadHandlerTexture new: texture=" + Try(() => own.texture) + " text=" + Try(() => own.text));
            server.Stop();
        }

        // ----- curve -----

        static string Keys(AnimationCurve c) => string.Join(" ", c.keys.Select(k => "(" + F(k.time) + "," + F(k.value) + " in=" + F(k.inTangent) + " out=" + F(k.outTangent)
                                                                                     + " wm=" + k.weightedMode + " iw=" + F(k.inWeight) + " ow=" + F(k.outWeight) + ")"));

        IEnumerator CurveParts()
        {
            var c = new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.5f, 1, 2, -1), new Keyframe(1, 0.25f));
            L("curve " + Keys(c));
            c.RemoveKey(1);
            L("RemoveKey(1) " + Keys(c) + " eval(0.5)=" + F(c.Evaluate(0.5f)));
            foreach (var i in new[] { -1, 2, 5 })
            {
                var cc = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1));
                L("RemoveKey(" + i + ") " + Try(() => { cc.RemoveKey(i); return Keys(cc); }));
            }
            var single = new AnimationCurve(new Keyframe(0, 3));
            single.RemoveKey(0);
            L("RemoveKey last: length=" + single.length + " eval=" + F(single.Evaluate(0.3f)));
            var expand = AnimationCurve.EaseInOut(0, 0, 1, 1);
            L("EaseInOut " + Keys(expand));
            expand.RemoveKey(1);
            L("EaseInOut RemoveKey(1) " + Keys(expand) + " eval(0.5)=" + F(expand.Evaluate(0.5f)));
            foreach (var w in new[] { 0f, 0.5f, 1f, -1f, 2f })
            {
                var s = new AnimationCurve(new Keyframe(0, 0, 5, 5), new Keyframe(1, 2, 7, -3), new Keyframe(3, 1, 0, 4), new Keyframe(4, 5));
                for (int i = 0; i < s.length; i++) s.SmoothTangents(i, w);
                L("SmoothTangents w=" + F(w) + " " + Keys(s));
            }
            var u = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 2), new Keyframe(1.5f, -1), new Keyframe(4, 4));
            u.SmoothTangents(1, 0);
            u.SmoothTangents(2, 0.3f);
            L("SmoothTangents uneven " + Keys(u));
            var weighted = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1, 3, 3, 0.2f, 0.7f) { weightedMode = WeightedMode.Both }, new Keyframe(2, 0));
            weighted.SmoothTangents(1, 0);
            L("SmoothTangents weighted " + Keys(weighted));
            L("SmoothTangents out of range " + Try(() => { var t = new AnimationCurve(new Keyframe(0, 0)); t.SmoothTangents(3, 0); return Keys(t); }));
            var one = new AnimationCurve(new Keyframe(0, 0, 2, 3));
            one.SmoothTangents(0, 0);
            L("SmoothTangents single " + Keys(one));
            var ends = new AnimationCurve(new Keyframe(0, 0, 2, 3), new Keyframe(1, 1, 4, 5));
            ends.SmoothTangents(0, 0);
            ends.SmoothTangents(1, 0);
            L("SmoothTangents ends " + Keys(ends));
            yield break;
        }

        // ----- copy -----

        static string Shown(Texture t)
        {
            var rt = NewRt("Shown", 4);
            Graphics.Blit(t, rt);
            var r = Uniform(rt);
            Destroy(rt);
            RenderTexture.active = null;
            return r;
        }

        static Texture2D Solid(Color32 c, TextureFormat f = TextureFormat.RGBA32, bool mips = false)
        {
            var t = new Texture2D(4, 4, f, mips);
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = c;
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        IEnumerator CopyParts()
        {
            foreach (var srcReadable in new[] { true, false })
            {
                var src = Solid(Red);
                if (!srcReadable) src.Apply(false, true);
                var dst = Solid(Green);
                Graphics.CopyTexture(src, dst);
                L("readable=" + srcReadable + " src to dst: dst cpu=" + Try(() => V((Vector4)dst.GetPixel(1, 1))) + " gpu=" + Shown(dst));
                dst.Apply();
                L("  then Apply: cpu=" + Try(() => V((Vector4)dst.GetPixel(1, 1))) + " gpu=" + Shown(dst));
                L("  new Texture2D(4,4,RGBA32,12): " + Try(() => new Texture2D(4, 4, TextureFormat.RGBA32, 12, false).mipmapCount));
                var fresh = new Texture2D(4, 4, TextureFormat.RGBA32, 3, false);
                L("  new Texture2D(4,4,RGBA32,3) mips=" + fresh.mipmapCount + " cpu=" + V((Vector4)fresh.GetPixel(1, 1)));
                L("  copy into it: " + Try(() => { Graphics.CopyTexture(src, fresh); return "ok"; }) + " cpu=" + Try(() => V((Vector4)fresh.GetPixel(1, 1))) + " gpu=" + Shown(fresh));
                fresh.Apply();
                L("  then Apply: cpu=" + Try(() => V((Vector4)fresh.GetPixel(1, 1))) + " gpu=" + Shown(fresh));
            }
            var mipped = Solid(Blue, TextureFormat.RGBA32, true);
            var plain = Solid(Green);
            L("mips to none: " + Try(() => { Graphics.CopyTexture(mipped, plain); return "ok"; }) + " cpu=" + Try(() => V((Vector4)plain.GetPixel(1, 1))) + " gpu=" + Shown(plain));
            var other = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            L("size mismatch: " + Try(() => { Graphics.CopyTexture(plain, other); return "ok"; }));
            var rgb = new Texture2D(4, 4, TextureFormat.RGB24, false);
            L("format mismatch RGBA32 to RGB24: " + Try(() => { Graphics.CopyTexture(plain, rgb); return "ok"; }) + " cpu=" + Try(() => V((Vector4)rgb.GetPixel(1, 1))));
            var argb = new Texture2D(4, 4, TextureFormat.ARGB32, false);
            L("format RGBA32 to ARGB32: " + Try(() => { Graphics.CopyTexture(plain, argb); return "ok"; }) + " cpu=" + Try(() => V((Vector4)argb.GetPixel(1, 1))) + " gpu=" + Shown(argb));
            var rt = NewRt("CopyRt", 4);
            Fill(rt, Yellow);
            var fromRt = Solid(Green);
            L("rt to texture: " + Try(() => { Graphics.CopyTexture(rt, fromRt); return "ok"; }) + " cpu=" + Try(() => V((Vector4)fromRt.GetPixel(1, 1))) + " gpu=" + Shown(fromRt));
            var toRt = NewRt("CopyRt2", 4);
            L("texture to rt: " + Try(() => { Graphics.CopyTexture(Solid(Cyan), toRt); return "ok"; }) + " rt=" + Uniform(toRt));
            L("null: " + Try(() => { Graphics.CopyTexture(null, plain); return "ok"; }) + " / " + Try(() => { Graphics.CopyTexture(plain, null); return "ok"; }));
            yield break;
        }

        // ----- misc -----

        IEnumerator MiscParts()
        {
            L("GarbageCollector: mode=" + UnityEngine.Scripting.GarbageCollector.GCMode + " incremental=" + UnityEngine.Scripting.GarbageCollector.isIncremental
              + " slice=" + UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds);
            L("CollectIncremental(0)=" + Try(() => UnityEngine.Scripting.GarbageCollector.CollectIncremental(0)) + " CollectIncremental(1000000)=" + Try(() => UnityEngine.Scripting.GarbageCollector.CollectIncremental(1000000)));
            UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = 1234567;
            L("slice set 1234567: " + UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds);
            UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds = 3000000;

            var go = new GameObject("ApiGapsCull");
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = 3;
            go.transform.position = new Vector3(1, 2, -10);
            L("cullingMatrix default=" + cam.cullingMatrix + " vp=" + (cam.projectionMatrix * cam.worldToCameraMatrix));
            cam.cullingMatrix = Matrix4x4.Scale(new Vector3(2, 2, 2));
            L("cullingMatrix set=" + cam.cullingMatrix);
            go.transform.position = new Vector3(5, 5, -10);
            L("moved: cullingMatrix=" + cam.cullingMatrix);
            cam.ResetCullingMatrix();
            L("reset: cullingMatrix=" + cam.cullingMatrix + " vp=" + (cam.projectionMatrix * cam.worldToCameraMatrix));
            cam.orthographicSize = 5;
            L("size 5 after reset: cullingMatrix=" + cam.cullingMatrix + " vp=" + (cam.projectionMatrix * cam.worldToCameraMatrix));
            Destroy(go);

            var sgo = new GameObject("ApiGapsSprite");
            var sr = sgo.AddComponent<SpriteRenderer>();
            L("SpriteRenderer new: drawMode=" + sr.drawMode + " size=" + V(sr.size) + " tileMode=" + Try(() => sr.tileMode) + " adaptive=" + Try(() => sr.adaptiveModeThreshold));
            var tex = Solid(Red);
            var sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 2f);
            sr.sprite = sprite;
            L("sprite set: size=" + V(sr.size) + " bounds=" + sr.bounds);
            sr.drawMode = SpriteDrawMode.Sliced;
            L("Sliced: size=" + V(sr.size) + " bounds=" + sr.bounds);
            sr.size = new Vector2(0.01f, 0.01f);
            L("Sliced size 0.01: size=" + V(sr.size) + " bounds=" + sr.bounds);
            sr.size = new Vector2(3, 1);
            L("Sliced size 3x1: bounds=" + sr.bounds);
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0, 0), 1f);
            L("Sliced, new sprite: size=" + V(sr.size) + " bounds=" + sr.bounds);
            sr.drawMode = SpriteDrawMode.Tiled;
            L("Tiled: size=" + V(sr.size) + " bounds=" + sr.bounds + " drawMode=" + sr.drawMode);
            sr.drawMode = SpriteDrawMode.Simple;
            L("Simple again: size=" + V(sr.size) + " bounds=" + sr.bounds);
            sr.drawMode = (SpriteDrawMode)7;
            L("drawMode 7: " + (int)sr.drawMode + " bounds=" + sr.bounds);
            Destroy(sgo);

            foreach (Allocator a in new[] { Allocator.Temp, Allocator.TempJob, Allocator.Persistent, Allocator.None, Allocator.Invalid, (Allocator)4, (Allocator)9 })
            {
                var aa = a;
                foreach (NativeArrayOptions o in new[] { NativeArrayOptions.ClearMemory, NativeArrayOptions.UninitializedMemory })
                {
                    var oo = o;
                    L("NativeArray<int>(4, " + a + "(" + (int)a + "), " + o + "): " + Try(() =>
                    {
                        var arr = new NativeArray<int>(4, aa, oo);
                        var r = "created=" + arr.IsCreated + " length=" + arr.Length + (oo == NativeArrayOptions.ClearMemory ? " values=" + string.Join(",", arr.ToArray()) : "");
                        arr[1] = 7;
                        r += " [1]=" + arr[1];
                        arr.Dispose();
                        return r + " disposed created=" + arr.IsCreated;
                    }));
                }
            }
            // A negative length ends the original's player ("Overflow in memory allocator", SIGTRAP).
            L("NativeArray<int>(0, Persistent): " + Try(() => { var arr = new NativeArray<int>(0, Allocator.Persistent); var r = "ok " + arr.Length + " created=" + arr.IsCreated; arr.Dispose(); return r; }));
            L("NativeArray<int>(new[]{1,2,3}, Persistent): " + Try(() => { var arr = new NativeArray<int>(new[] { 1, 2, 3 }, Allocator.Persistent); var r = string.Join(",", arr.ToArray()); arr.Dispose(); return r; }));
            yield break;
        }

        // ----- ps -----

        static string Curve(ParticleSystem.MinMaxCurve c) => c.mode + "(" + F(c.constant) + "," + F(c.constantMin) + "," + F(c.constantMax) + " x" + F(c.curveMultiplier) + ")";

        static string Burst(ParticleSystem.Burst b) => "time=" + F(b.time) + " count=" + Curve(b.count) + " min=" + b.minCount + " max=" + b.maxCount + " cycles=" + b.cycleCount
                                                        + " interval=" + F(b.repeatInterval) + " p=" + F(b.probability);

        IEnumerator ParticleParts()
        {
            L("Burst(0, 12): " + Burst(new ParticleSystem.Burst(0f, (short)12)));
            L("Burst(1, 2, 5): " + Burst(new ParticleSystem.Burst(1f, (short)2, (short)5)));
            L("Burst(1, 2, 5, 3, 0.5): " + Burst(new ParticleSystem.Burst(1f, (short)2, (short)5, 3, 0.5f)));
            L("Burst(1, curve 4): " + Burst(new ParticleSystem.Burst(1f, new ParticleSystem.MinMaxCurve(4f))));
            L("Burst(1, 3f): " + Burst(new ParticleSystem.Burst(1f, 3f)));
            L("Burst(1, curve 2..6, 2, 0.25): " + Burst(new ParticleSystem.Burst(1f, new ParticleSystem.MinMaxCurve(2f, 6f), 2, 0.25f)));
            var bb = new ParticleSystem.Burst(0f, (short)1);
            L("Burst probability 2: " + Try(() => { bb.probability = 2f; return Burst(bb); }));
            L("Burst probability -1: " + Try(() => { bb.probability = -1f; return Burst(bb); }));
            L("Burst cycleCount -3: " + Try(() => { bb.cycleCount = -3; return Burst(bb); }));
            L("Burst repeatInterval -1: " + Try(() => { bb.repeatInterval = -1f; return Burst(bb); }));
            L("Burst repeatInterval 0: " + Try(() => { bb.repeatInterval = 0f; return Burst(bb); }));
            L("Burst time -2: " + Try(() => { bb.time = -2f; return Burst(bb); }));
            L("Burst min 9 max 4: " + Try(() => { bb.minCount = 9; bb.maxCount = 4; return Burst(bb); }));
            L("Burst count 7: " + Try(() => { bb.count = 7f; return Burst(bb); }));
            L("Burst(0, 2, 5, 0, 0.1): " + Try(() => Burst(new ParticleSystem.Burst(0f, (short)2, (short)5, 0, 0.1f))));
            L("Burst(0, 2, 5, 3, 0): " + Try(() => Burst(new ParticleSystem.Burst(0f, (short)2, (short)5, 3, 0f))));
            L("default Burst: " + Burst(default(ParticleSystem.Burst)));

            var go = new GameObject("ApiGapsPs");
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var em = ps.emission;
            L("emission: burstCount=" + em.burstCount + " rate=" + Curve(em.rateOverTime) + " rateMult=" + F(em.rateOverTimeMultiplier));
            em.rateOverTimeMultiplier = 3f;
            L("rateOverTimeMultiplier 3: rate=" + Curve(em.rateOverTime) + " mult=" + F(em.rateOverTimeMultiplier));
            em.rateOverTime = new ParticleSystem.MinMaxCurve(2f, 8f);
            em.rateOverTimeMultiplier = 0.5f;
            L("rate 2..8 then mult 0.5: rate=" + Curve(em.rateOverTime) + " mult=" + F(em.rateOverTimeMultiplier));
            em.burstCount = 1;
            L("burstCount=1: burstCount=" + em.burstCount + " burst0=" + Burst(em.GetBurst(0)));
            em.SetBurst(0, new ParticleSystem.Burst(0f, (short)12));
            L("SetBurst(0, (0,12)): burstCount=" + em.burstCount + " burst0=" + Burst(em.GetBurst(0)));
            L("SetBurst(1): " + Try(() => { em.SetBurst(1, new ParticleSystem.Burst(0.5f, (short)3)); return "ok burstCount=" + em.burstCount; }));
            L("GetBurst(1): " + Try(() => Burst(em.GetBurst(1))));
            L("SetBurst(-1): " + Try(() => { em.SetBurst(-1, new ParticleSystem.Burst(0.5f, (short)3)); return "ok"; }));
            em.burstCount = 3;
            L("burstCount=3: " + em.burstCount + " burst1=" + Try(() => Burst(em.GetBurst(1))) + " burst2=" + Try(() => Burst(em.GetBurst(2))));
            var arr = new ParticleSystem.Burst[em.burstCount];
            L("GetBursts: n=" + em.GetBursts(arr) + " " + string.Join(" | ", arr.Select(Burst)));
            em.burstCount = 0;
            L("burstCount=0: " + em.burstCount);
            L("burstCount=-1: " + Try(() => { em.burstCount = -1; return em.burstCount; }));
            L("burstCount=1000: " + Try(() => { em.burstCount = 1000; return em.burstCount; }));
            em.burstCount = 0;

            var main = ps.main;
            L("main.startSize default=" + Curve(main.startSize) + " mult=" + F(main.startSizeMultiplier));
            main.startSize = 0.3f;
            L("startSize 0.3: " + Curve(main.startSize) + " mult=" + F(main.startSizeMultiplier) + " startSizeX=" + Curve(main.startSizeX));
            main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            L("startSize 0.1..0.4: " + Curve(main.startSize) + " mult=" + F(main.startSizeMultiplier));

            var vel = ps.velocityOverLifetime;
            L("velocityOverLifetime default: enabled=" + vel.enabled + " x=" + Curve(vel.x) + " y=" + Curve(vel.y) + " z=" + Curve(vel.z) + " space=" + vel.space
              + " xm=" + F(vel.xMultiplier) + " speed=" + Curve(vel.speedModifier) + " orbX=" + Curve(vel.orbitalX) + " radial=" + Curve(vel.radial)
              + " offset=" + Curve(vel.orbitalOffsetX));
            vel.enabled = true;
            vel.x = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
            vel.y = new ParticleSystem.MinMaxCurve(0.3f, 1.5f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
            L("set: enabled=" + vel.enabled + " x=" + Curve(vel.x) + " y=" + Curve(vel.y) + " xm=" + F(vel.xMultiplier) + " ym=" + F(vel.yMultiplier));
            vel.yMultiplier = 2f;
            L("ym 2: y=" + Curve(vel.y) + " ym=" + F(vel.yMultiplier));
            vel.x = new ParticleSystem.MinMaxCurve(1f);
            vel.y = new ParticleSystem.MinMaxCurve(0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f);
            vel.space = ParticleSystemSimulationSpace.World;
            L("x=1 world: space=" + vel.space);

            // The motion: a burst of particles with a constant velocity over lifetime and no start speed.
            main.startSpeed = 0f;
            main.startLifetime = 10f;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.loop = false;
            var shape = ps.shape;
            shape.enabled = false;
            em.rateOverTime = 0f;
            em.burstCount = 1;
            em.SetBurst(0, new ParticleSystem.Burst(0f, (short)3));
            foreach (var space in new[] { ParticleSystemSimulationSpace.Local, ParticleSystemSimulationSpace.World })
            {
                vel.space = space;
                go.transform.rotation = Quaternion.Euler(0, 0, 90);
                go.transform.localScale = new Vector3(2, 2, 2);
                ps.Simulate(0f, true, true, true);
                ps.Simulate(0.5f, true, false, false);
                var parts = new ParticleSystem.Particle[8];
                int n = ps.GetParticles(parts);
                L("simulated 0.5 s, velocity (1,0,0) " + space + ", rotated 90 scaled 2: n=" + n + " p0=" + V(parts[0].position) + " v0=" + V(parts[0].velocity) + " total=" + V(parts[0].totalVelocity));
            }
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.speedModifier = new ParticleSystem.MinMaxCurve(2f);
            ps.Simulate(0f, true, true, true);
            ps.Simulate(0.5f, true, false, false);
            {
                var parts = new ParticleSystem.Particle[8];
                int n = ps.GetParticles(parts);
                L("speedModifier 2: n=" + n + " p0=" + V(parts[0].position));
            }
            vel.speedModifier = new ParticleSystem.MinMaxCurve(1f);
            em.SetBurst(0, new ParticleSystem.Burst(0.2f, (short)5, (short)5, 3, 0.1f));
            ps.Simulate(0f, true, true, true);
            ps.Simulate(0.15f, true, false, false);
            L("burst (0.2, 5, cycles 3, interval 0.1) at 0.15: " + ps.particleCount);
            ps.Simulate(0.1f, true, false, false);
            L("  at 0.25: " + ps.particleCount);
            ps.Simulate(0.1f, true, false, false);
            L("  at 0.35: " + ps.particleCount);
            ps.Simulate(0.3f, true, false, false);
            L("  at 0.65: " + ps.particleCount);
            var never = new ParticleSystem.Burst(0f, new ParticleSystem.MinMaxCurve(4f), 1, 0.01f);
            never.probability = 0f;
            em.SetBurst(0, never);
            ps.Simulate(0f, true, true, true);
            ps.Simulate(0.1f, true, false, false);
            L("burst probability 0: " + ps.particleCount);
            Destroy(go);
            yield break;
        }

        // ----- bounds -----

        static string B(Bounds b) => "c=" + V(b.center) + " e=" + V(b.extents);

        IEnumerator BoundsParts()
        {
            var root = new GameObject("ApiGapsBounds");
            root.transform.position = new Vector3(1, 2, 3);
            root.transform.rotation = Quaternion.Euler(0, 0, 30);
            root.transform.localScale = new Vector3(2, 1, 1);
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0.5f, 0, 0);
            box.size = new Vector3(1, 2, 3);
            L("BoxCollider: " + B(box.bounds));
            box.enabled = false;
            L("BoxCollider disabled: " + B(box.bounds));
            box.enabled = true;
            var sgo = new GameObject("Sphere");
            sgo.transform.position = new Vector3(-1, 0, 0);
            sgo.transform.localScale = new Vector3(1, 3, 2);
            var sphere = sgo.AddComponent<SphereCollider>();
            sphere.radius = 0.75f;
            sphere.center = new Vector3(0, 1, 0);
            L("SphereCollider: " + B(sphere.bounds));
            var cgo = new GameObject("Capsule");
            cgo.transform.rotation = Quaternion.Euler(0, 0, 45);
            var capsule = cgo.AddComponent<CapsuleCollider>();
            capsule.radius = 0.5f;
            capsule.height = 3f;
            L("CapsuleCollider: " + B(capsule.bounds));
            var mgo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            mgo.transform.position = new Vector3(4, 0, 0);
            mgo.transform.rotation = Quaternion.Euler(0, 45, 0);
            L("MeshCollider quad: " + B(mgo.GetComponent<Collider>().bounds));
            var inactive = new GameObject("Inactive");
            inactive.SetActive(false);
            inactive.transform.position = new Vector3(7, 7, 7);
            var ib = inactive.AddComponent<BoxCollider>();
            L("BoxCollider on an inactive object: " + B(ib.bounds));

            var b2 = new GameObject("Box2D");
            b2.transform.position = new Vector3(1, 2, 3);
            b2.transform.rotation = Quaternion.Euler(0, 0, 30);
            b2.transform.localScale = new Vector3(2, 1, 1);
            var box2 = b2.AddComponent<BoxCollider2D>();
            box2.offset = new Vector2(0.5f, 0);
            box2.size = new Vector2(1, 2);
            L("BoxCollider2D: " + B(box2.bounds));
            yield return new WaitForFixedUpdate();
            L("BoxCollider2D after a fixed update: " + B(box2.bounds));
            box2.size = new Vector2(3, 3);
            L("BoxCollider2D resized: " + B(box2.bounds));
            b2.transform.position = new Vector3(5, 5, 5);
            L("BoxCollider2D moved: " + B(box2.bounds));
            yield return new WaitForFixedUpdate();
            L("BoxCollider2D moved, after a fixed update: " + B(box2.bounds));
            box2.enabled = false;
            L("BoxCollider2D disabled: " + B(box2.bounds));
            var c2 = new GameObject("Circle2D");
            c2.transform.localScale = new Vector3(1, 3, 1);
            var circle = c2.AddComponent<CircleCollider2D>();
            circle.radius = 0.5f;
            circle.offset = new Vector2(1, 1);
            L("CircleCollider2D: " + B(circle.bounds));
            var p2 = new GameObject("Poly2D");
            var poly = p2.AddComponent<PolygonCollider2D>();
            L("PolygonCollider2D default: " + B(poly.bounds) + " paths=" + poly.pathCount + " path0=" + string.Join(" ", poly.GetPath(0).Select(V)));
            poly.SetPath(0, new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(0, 1) });
            L("PolygonCollider2D triangle: " + B(poly.bounds));
            var e2 = new GameObject("Edge2D");
            var edge = e2.AddComponent<EdgeCollider2D>();
            L("EdgeCollider2D default: " + B(edge.bounds));
            var i2 = new GameObject("Inactive2D");
            i2.SetActive(false);
            var ib2 = i2.AddComponent<BoxCollider2D>();
            L("BoxCollider2D on an inactive object: " + B(ib2.bounds));
            foreach (var g in new[] { root, sgo, cgo, mgo, inactive, b2, c2, p2, e2, i2 }) Destroy(g);
        }

        // ----- video -----

        IEnumerator VideoParts()
        {
            var go = new GameObject("ApiGapsVideo");
            var vp = go.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            L("new: source=" + vp.source + " url=" + Try(() => vp.url) + " renderMode=" + vp.renderMode + " timeUpdateMode=" + vp.timeUpdateMode + " sendFrameReadyEvents=" + vp.sendFrameReadyEvents
              + " width=" + vp.width + " height=" + vp.height + " canSetSource=" + Try(() => vp.canSetTime) + " audio=" + vp.audioOutputMode);
            var errors = new List<string>();
            int frames = 0;
            long lastFrame = -2;
            vp.errorReceived += (s, m) => errors.Add("error(" + (s == vp) + "): " + m);
            vp.frameReady += (s, f) => { frames++; lastFrame = f; };
            vp.prepareCompleted += s => errors.Add("prepared w=" + s.width + " h=" + s.height + " frames=" + s.frameCount + " texture=" + (s.texture == null ? "null" : s.texture.width + "x" + s.texture.height));
            vp.source = VideoSource.Url;
            L("source Url: url=" + Try(() => vp.url) + " clip=" + Try(() => vp.clip));
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            vp.sendFrameReadyEvents = true;
            L("set: renderMode=" + vp.renderMode + " timeUpdateMode=" + vp.timeUpdateMode + " sendFrameReadyEvents=" + vp.sendFrameReadyEvents + " target=" + Try(() => vp.targetTexture));
            var target = new RenderTexture(32, 32, 0) { name = "VideoTarget" };
            vp.targetTexture = target;

            foreach (var u in new[] { "file:///nonexistent/x.webm", "/nonexistent/x.webm", Path.Combine(Application.streamingAssetsPath, "nope.mp4") })
            {
                errors.Clear();
                vp.url = u;
                L("url='" + u + "': url=" + Try(() => vp.url) + " isPrepared=" + vp.isPrepared);
                vp.Prepare();
                for (int i = 0; i < 60 && errors.Count == 0; i++) yield return null;
                L("  after prepare: isPrepared=" + vp.isPrepared + " width=" + vp.width + " height=" + vp.height + " events=[" + string.Join(" | ", errors) + "]");
                vp.Stop();
            }

            var videoDir = Path.Combine(Application.streamingAssetsPath, "Videos");
            var file = Directory.Exists(videoDir) ? Directory.GetFiles(videoDir, "*.webm").OrderBy(f => new FileInfo(f).Length).FirstOrDefault() : null;
            if (file == null)
            {
                L("no game video under " + videoDir);
            }
            else
            {
                foreach (var u in new[] { file, new Uri(file).AbsoluteUri })
                {
                    errors.Clear();
                    frames = 0;
                    vp.url = u;
                    L("url=" + Path.GetFileName(u) + " (" + (u.StartsWith("file:") ? "uri" : "path") + "): url ends " + Try(() => Path.GetFileName(vp.url)));
                    vp.Prepare();
                    for (int i = 0; i < 300 && !vp.isPrepared && errors.Count == 0; i++) yield return null;
                    L("  prepared=" + vp.isPrepared + " width=" + vp.width + " height=" + vp.height + " frameCount=" + vp.frameCount + " events=[" + string.Join(" | ", errors) + "]");
                    vp.Play();
                    for (int i = 0; i < 30; i++) yield return null;
                    L("  after 30 frames playing: frameReady events>0=" + (frames > 0) + " lastFrame>=0=" + (lastFrame >= 0) + " frame=" + (vp.frame >= 0) + " target=" + Uniform(target).Length);
                    vp.sendFrameReadyEvents = false;
                    frames = 0;
                    for (int i = 0; i < 10; i++) yield return null;
                    L("  frameReady off: events=" + frames);
                    vp.sendFrameReadyEvents = true;
                    vp.Stop();
                    L("  stopped: width=" + vp.width + " height=" + vp.height + " isPrepared=" + vp.isPrepared);
                }
            }
            vp.source = VideoSource.VideoClip;
            L("source VideoClip: url=" + Try(() => vp.url));
            vp.url = "";
            vp.source = VideoSource.Url;
            L("url '' source Url: url=" + Try(() => vp.url));
            vp.url = null;
            L("url null: url=" + Try(() => vp.url));
            Destroy(go);
            Destroy(target);
        }

        // ----- gui -----

        sealed class GuiProbe : MonoBehaviour
        {
            public int Frames;
            public readonly List<string> Lines = new List<string>();
            int sel1 = 1, sel2 = 0, sel3 = 2;
            string pw = "secret", pw2 = "", pw3 = "abcdefghijk";

            void OnGUI()
            {
                if (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout) return;
                bool log = Event.current.type == EventType.Repaint && Frames == 2;
                GUILayout.BeginArea(new Rect(20, 30, 400, 600));
                sel1 = GUILayout.SelectionGrid(sel1, new[] { "One", "Two", "Three", "Four", "Five" }, 3);
                if (log) Lines.Add("SelectionGrid(5, xCount 3) rect=" + R(GUILayoutUtility.GetLastRect()) + " sel=" + sel1);
                sel2 = GUILayout.SelectionGrid(sel2, new[] { "Alpha", "B" }, 1, GUI.skin.label);
                if (log) Lines.Add("SelectionGrid(2, xCount 1, label) rect=" + R(GUILayoutUtility.GetLastRect()) + " sel=" + sel2);
                sel3 = GUILayout.SelectionGrid(sel3, new[] { "a", "bb", "ccc", "dddd" }, 2, GUI.skin.button, GUILayout.Width(150));
                if (log) Lines.Add("SelectionGrid(4, xCount 2, width 150) rect=" + R(GUILayoutUtility.GetLastRect()) + " sel=" + sel3);
                int s4 = GUILayout.SelectionGrid(7, new[] { "x", "y", "z" }, 0);
                if (log) Lines.Add("SelectionGrid(3, xCount 0, selected 7) rect=" + R(GUILayoutUtility.GetLastRect()) + " sel=" + s4);
                int s5 = GUILayout.SelectionGrid(-1, new string[0], 2);
                if (log) Lines.Add("SelectionGrid(empty) rect=" + R(GUILayoutUtility.GetLastRect()) + " sel=" + s5);
                pw = GUILayout.PasswordField(pw, '*', GUI.skin.textField);
                if (log) Lines.Add("PasswordField('secret', '*', textField) rect=" + R(GUILayoutUtility.GetLastRect()) + " value='" + pw + "'");
                pw2 = GUILayout.PasswordField(pw2, '#');
                if (log) Lines.Add("PasswordField('', '#') rect=" + R(GUILayoutUtility.GetLastRect()) + " value='" + pw2 + "'");
                pw3 = GUILayout.PasswordField(pw3, '●', 4, GUILayout.Width(80));
                if (log) Lines.Add("PasswordField('abcdefghijk', dot, max 4, width 80) rect=" + R(GUILayoutUtility.GetLastRect()) + " value='" + pw3 + "'");
                var sized = GUI.skin.textField.CalcSize(new GUIContent("******"));
                if (log) Lines.Add("textField CalcSize('******')=" + V(sized) + " ('secret')=" + V(GUI.skin.textField.CalcSize(new GUIContent("secret"))));
                GUILayout.EndArea();
                if (Event.current.type == EventType.Repaint)
                {
                    int g1 = GUI.SelectionGrid(new Rect(500, 30, 200, 60), 1, new[] { "p", "q", "r" }, 2);
                    string p1 = GUI.PasswordField(new Rect(500, 100, 200, 20), "hidden", '*');
                    string p2 = GUI.PasswordField(new Rect(500, 130, 200, 20), "hidden", '*', 3);
                    if (log) Lines.Add("GUI.SelectionGrid=" + g1 + " GUI.PasswordField='" + p1 + "' max 3='" + p2 + "'");
                    Frames++;
                }
            }
        }

        IEnumerator GuiParts()
        {
            var go = new GameObject("ApiGapsGui");
            var g = go.AddComponent<GuiProbe>();
            while (g.Frames < 4) yield return null;
            foreach (var l in g.Lines) L(l);
            var path = Environment.GetEnvironmentVariable("PROBE_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(path))
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(path, "apigaps-gui.png"));
                yield return null;
                yield return null;
            }
            Destroy(go);
        }
    }
}
