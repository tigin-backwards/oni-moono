// What a planned Direct3D 11 device has to match in the original's player, measured on its
// default renderer (Direct3D 11 on Windows) and with -force-glcore (lines "PROBE ... DX ..."), 10 s
// into the main menu. With PROBE_SCENARIO=d3d11 only this probe runs; it ends with a
// "DX done" line and a D3d11ProbeDone object. docs/research/d3d11-unity-probes.md has the results.
//
// Sections, in this order (the native one last, so a crash there loses nothing before it):
//   DX sys   SystemInfo: device type, version, name, vendor, IDs, shader level, reversed Z, UV
//            origin, limits; SupportsTextureFormat, SupportsRenderTextureFormat,
//            GetGraphicsFormat(DefaultFormat), and IsFormatSupported for every GraphicsFormat
//            and usage (one line per format: the mask of supported usages).
//   DX gpm   GL.GetGPUProjectionMatrix of perspective and ortho matrices, renderIntoTexture
//            false and true, as float bits.
//   DX rt    RenderTexture: depthStencilFormat for depth 0/16/24/32, the graphicsFormat each
//            RenderTextureFormat becomes; row order of ReadPixels from a render texture and from
//            the back buffer (a quad drawn at Unity's pixel (0,0)), Graphics.Blit and
//            Graphics.CopyTexture of an asymmetric pattern, ReadPixels with an offset rect,
//            AsyncGPUReadback and ScreenCapture; face culling and the stored depth of a clear.
//   DX up    Texture uploads: Apply with and without mips (mip 1 read back from the GPU through
//            CopyTexture), LoadRawTextureData for RGB24, Alpha8, R8, DXT1, DXT5, BC4, BC5, BC7
//            (sampled through Blit, and decoded by GetPixels32), GetPixels after ReadPixels
//            without Apply and after a GPU-only CopyTexture.
//   DX nat   (D3d11Native.cs) on the render thread through GL.IssuePluginEvent: what
//            GetNativeTexturePtr is (D3D11: QueryInterface results and the texture or view
//            description; GLCore: the texture name and its internal format), the device's
//            feature level, creation flags and threading support, and the constant buffers
//            (D3D11) or uniforms (GLCore) a shader has bound right after a draw by a camera:
//            _ProjectionParams, _ScreenParams, _ZBufferParams, unity_OrthoParams and the
//            matrices as the program reads them, with the render targets and viewport.
// Pixels are logged in Unity's order (index y * width + x, row 0 at the bottom).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class D3d11Probe : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;

        internal static void L(string s) => Rec.Log("DX " + s);
        internal static string H(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
        internal static string F(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        internal static string M(Matrix4x4 m)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 4; r++)
            {
                sb.Append(r == 0 ? "[" : " [");
                for (int c = 0; c < 4; c++) sb.Append(c == 0 ? "" : ",").Append(F(m[r, c]));
                sb.Append("]");
            }
            return sb.ToString();
        }
        static string MH(Matrix4x4 m)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    sb.Append(r == 0 && c == 0 ? "" : ",").Append(H(m[r, c]));
            return sb.ToString();
        }

        static void Guard(string what, System.Action a)
        {
            try { a(); }
            catch (Exception e) { L(what + " EXCEPTION " + e.GetType().Name + ": " + e.Message); }
        }

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            L("=== start device=" + SystemInfo.graphicsDeviceType + " screen=" + Screen.width + "x" + Screen.height
                + " unity=" + Application.unityVersion + " cmdline=" + string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Where(a => a.StartsWith("-force"))));
            Guard("sys", Sys);
            Guard("formats", Formats);
            Guard("gpm", Gpm);
            Guard("rtformats", RtFormats);
            Guard("rows", Rows);
            Guard("depth", DepthClear);
            Guard("cull", Cull);
            yield return BackBuffer();
            yield return AsyncRead();
            Guard("uploads", Uploads);
            Guard("raw", RawUploads);
            Guard("cpucopy", CpuAfterGpu);
            if (Environment.GetEnvironmentVariable("PROBE_D3D11_NATIVE") != "0")
                yield return D3d11Native.Run(this);
            L("done");
            new GameObject("D3d11ProbeDone");
        }

        // ---------------------------------------------------------------- SystemInfo

        static void Sys()
        {
            L("sys graphicsDeviceType=" + SystemInfo.graphicsDeviceType);
            L("sys graphicsDeviceVersion=\"" + SystemInfo.graphicsDeviceVersion + "\"");
            L("sys graphicsDeviceName=\"" + SystemInfo.graphicsDeviceName + "\" vendor=\"" + SystemInfo.graphicsDeviceVendor
                + "\" id=0x" + SystemInfo.graphicsDeviceID.ToString("x") + " vendorID=0x" + SystemInfo.graphicsDeviceVendorID.ToString("x"));
            L("sys graphicsShaderLevel=" + SystemInfo.graphicsShaderLevel + " graphicsMemorySize=" + SystemInfo.graphicsMemorySize
                + " graphicsMultiThreaded=" + SystemInfo.graphicsMultiThreaded + " renderingThreadingMode=" + SystemInfo.renderingThreadingMode);
            L("sys usesReversedZBuffer=" + SystemInfo.usesReversedZBuffer + " graphicsUVStartsAtTop=" + SystemInfo.graphicsUVStartsAtTop);
            L("sys maxTextureSize=" + SystemInfo.maxTextureSize + " maxCubemapSize=" + SystemInfo.maxCubemapSize
                + " maxTexture3DSize=" + SystemInfo.maxTexture3DSize + " maxTextureArraySlices=" + SystemInfo.maxTextureArraySlices);
            L("sys copyTextureSupport=" + SystemInfo.copyTextureSupport + " (" + (int)SystemInfo.copyTextureSupport + ")"
                + " supportsComputeShaders=" + SystemInfo.supportsComputeShaders + " npotSupport=" + SystemInfo.npotSupport
                + " supportedRenderTargetCount=" + SystemInfo.supportedRenderTargetCount);
            // The rest of SystemInfo's static properties, by reflection, so none is missed.
            foreach (var p in typeof(SystemInfo).GetProperties(BindingFlags.Public | BindingFlags.Static).OrderBy(p => p.Name))
            {
                if (p.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0) continue;
                if (p.Name == "deviceUniqueIdentifier" || p.Name == "deviceName") continue; // nothing to compare; private
                string v;
                try { v = Convert.ToString(p.GetValue(null), System.Globalization.CultureInfo.InvariantCulture); }
                catch (Exception e) { v = "EXCEPTION " + (e.InnerException ?? e).GetType().Name; }
                L("sysall " + p.Name + "=" + v);
            }
        }

        static IEnumerable<T> Values<T>() where T : struct
            => Enum.GetValues(typeof(T)).Cast<T>().GroupBy(v => Convert.ToInt64(v)).Select(g => g.First());

        static void Formats()
        {
            foreach (var f in Values<TextureFormat>())
            {
                string s;
                try { s = SystemInfo.SupportsTextureFormat(f).ToString(); } catch (Exception e) { s = "EXCEPTION " + e.GetType().Name; }
                L("texfmt " + f + "=" + (int)f + " " + s);
            }
            foreach (var f in Values<RenderTextureFormat>())
            {
                string s;
                try { s = SystemInfo.SupportsRenderTextureFormat(f).ToString(); } catch (Exception e) { s = "EXCEPTION " + e.GetType().Name; }
                L("rtfmt " + f + "=" + (int)f + " " + s);
            }
            foreach (var d in Values<DefaultFormat>())
                Guard("default " + d, () => L("default " + d + " " + SystemInfo.GetGraphicsFormat(d)));
            // IsFormatSupported(GraphicsFormat, usage): the usage enum is FormatUsage or
            // GraphicsFormatUsage depending on the Unity version, so it is found by reflection.
            var isSup = typeof(SystemInfo).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "IsFormatSupported" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(GraphicsFormat)
                    && m.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length == 0)
                .OrderByDescending(m => m.GetParameters()[1].ParameterType.Name.Length).FirstOrDefault();
            var compat = typeof(SystemInfo).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetCompatibleFormat" && isSup != null && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == isSup.GetParameters()[1].ParameterType);
            if (isSup == null) { L("gfmt IsFormatSupported missing"); return; }
            var usageType = isSup.GetParameters()[1].ParameterType;
            var usages = Enum.GetValues(usageType).Cast<object>().GroupBy(v => Convert.ToInt64(v)).Select(g => g.First()).ToList();
            L("gfmt usages " + usageType.Name + ": " + string.Join(" ", usages.Select(u => u + "=" + Convert.ToInt64(u))));
            foreach (var f in Values<GraphicsFormat>())
            {
                var sup = new List<string>();
                foreach (var u in usages)
                {
                    try { if ((bool)isSup.Invoke(null, new object[] { f, u })) sup.Add(u.ToString()); }
                    catch (Exception e) { sup.Add(u + ":EXC:" + (e.InnerException ?? e).GetType().Name); }
                }
                string c = "";
                if (compat != null)
                {
                    try
                    {
                        var sample = usages.FirstOrDefault(u => u.ToString() == "Sample") ?? usages[0];
                        var render = usages.FirstOrDefault(u => u.ToString() == "Render") ?? usages[0];
                        c = " compat(Sample)=" + compat.Invoke(null, new object[] { f, sample }) + " compat(Render)=" + compat.Invoke(null, new object[] { f, render });
                    }
                    catch (Exception e) { c = " compat EXC " + (e.InnerException ?? e).GetType().Name; }
                }
                L("gfmt " + f + "=" + (int)f + " [" + string.Join(",", sup) + "]" + c);
            }
        }

        // ---------------------------------------------------------------- projection

        static void Gpm()
        {
            var mats = new List<(string, Matrix4x4)>
            {
                ("persp60_1.5_0.3_1000", Matrix4x4.Perspective(60, 1.5f, 0.3f, 1000)),
                ("persp45_1.333_0.5_200", Matrix4x4.Perspective(45, 4f / 3f, 0.5f, 200)),
                ("ortho-8,8,-6,6,0.3,1000", Matrix4x4.Ortho(-8, 8, -6, 6, 0.3f, 1000)),
                ("ortho-5,5,-5,5,-100,100", Matrix4x4.Ortho(-5, 5, -5, 5, -100, 100)),
                ("pixel0,1024,0,768,-1,1", Matrix4x4.Ortho(0, 1024, 0, 768, -1, 1)),
                ("identity", Matrix4x4.identity),
            };
            foreach (var (name, m) in mats)
            {
                L("gpm " + name + " in=" + M(m));
                L("gpm " + name + " false=" + M(GL.GetGPUProjectionMatrix(m, false)) + " bits=" + MH(GL.GetGPUProjectionMatrix(m, false)));
                L("gpm " + name + " true=" + M(GL.GetGPUProjectionMatrix(m, true)) + " bits=" + MH(GL.GetGPUProjectionMatrix(m, true)));
            }
        }

        // ---------------------------------------------------------------- render textures

        static void RtFormats()
        {
            foreach (int d in new[] { 0, 16, 24, 32 })
            {
                var rt = new RenderTexture(4, 4, d, RenderTextureFormat.ARGB32);
                string before = "depth=" + rt.depth + " depthStencilFormat=" + rt.depthStencilFormat + " graphicsFormat=" + rt.graphicsFormat;
                rt.Create();
                L("rtdepth " + d + " " + before + " created=" + rt.IsCreated() + " after: depthStencilFormat=" + rt.depthStencilFormat + " depth=" + rt.depth);
                rt.Release(); Destroy(rt);
                var desc = new RenderTextureDescriptor(4, 4, RenderTextureFormat.ARGB32, d);
                L("rtdepth desc " + d + " depthStencilFormat=" + desc.depthStencilFormat + " graphicsFormat=" + desc.graphicsFormat);
            }
            foreach (var f in Values<RenderTextureFormat>())
            {
                Guard("rtgf " + f, () =>
                {
                    var rt = new RenderTexture(4, 4, 0, f);
                    string gf = rt.graphicsFormat.ToString(), ds = rt.depthStencilFormat.ToString();
                    bool ok = false;
                    try { ok = rt.Create(); } catch (Exception) { }
                    string util;
                    try { util = GraphicsFormatUtility.GetGraphicsFormat(f, false) + "/" + GraphicsFormatUtility.GetGraphicsFormat(f, true); } catch (Exception e) { util = e.GetType().Name; }
                    L("rtgf " + f + "=" + (int)f + " graphicsFormat=" + gf + " depthStencilFormat=" + ds + " created=" + ok + " after=" + rt.graphicsFormat + " util(lin/srgb)=" + util
                        + " rw=" + new RenderTexture(1, 1, 0, f, RenderTextureReadWrite.Linear).graphicsFormat + "/" + new RenderTexture(1, 1, 0, f, RenderTextureReadWrite.sRGB).graphicsFormat);
                    rt.Release(); Destroy(rt);
                });
            }
            foreach (var t in new[] { TextureFormat.RGB24, TextureFormat.Alpha8, TextureFormat.R8, TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.BGRA32, TextureFormat.RHalf, TextureFormat.RGBAHalf, TextureFormat.RFloat, TextureFormat.RGBAFloat, TextureFormat.DXT1, TextureFormat.DXT5, TextureFormat.BC7, TextureFormat.RGB565, TextureFormat.RGBA4444, TextureFormat.ARGB4444, TextureFormat.R16 })
                Guard("t2dgf " + t, () => { var x = new Texture2D(4, 4, t, false); L("t2dgf " + t + " graphicsFormat=" + x.graphicsFormat + " linear=" + new Texture2D(4, 4, t, false, true).graphicsFormat); });
        }

        internal static Color32 PatternAt(int x, int y) => new Color32((byte)(x * 60 + 10), (byte)(y * 60 + 10), 77, 255);

        internal static Texture2D Pattern(int w = 4, int h = 4, bool mips = false)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, mips) { name = "DxPattern", filterMode = FilterMode.Point };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = PatternAt(x, y);
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        internal static string Px(Color32[] px)
            => string.Join(" ", px.Select(c => c.r + "," + c.g + "," + c.b + "," + c.a));

        // Which pattern texel each read pixel holds, as "x.y", or "?" (rows separated by "|").
        static string Where(Color32[] px, int w)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < px.Length; i++)
            {
                if (i > 0) sb.Append(i % w == 0 ? " | " : " ");
                var c = px[i];
                int x = (c.r - 10) / 60, y = (c.g - 10) / 60;
                bool ok = c.b == 77 && (c.r - 10) % 60 == 0 && (c.g - 10) % 60 == 0;
                sb.Append(ok ? x + "." + y : c.r + "," + c.g + "," + c.b);
            }
            return sb.ToString();
        }

        internal static Color32[] Read(RenderTexture rt, Rect? rect = null)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var r = rect ?? new Rect(0, 0, rt.width, rt.height);
            var t = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
            t.ReadPixels(r, 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            var px = t.GetPixels32();
            Destroy(t);
            return px;
        }

        static RenderTexture Rt(int w = 4, int h = 4, int depth = 0, RenderTextureFormat f = RenderTextureFormat.ARGB32)
        {
            var rt = new RenderTexture(w, h, depth, f) { name = "DxRt", filterMode = FilterMode.Point };
            rt.Create();
            return rt;
        }

        static Material colored;
        internal static Material Colored(Color c, int cull = 0, int zTest = 8, int zWrite = 0)
        {
            if (colored == null) colored = new Material(Shader.Find("Hidden/Internal-Colored"));
            var m = new Material(colored);
            m.SetColor("_Color", c);
            m.SetInt("_SrcBlend", (int)BlendMode.One);
            m.SetInt("_DstBlend", (int)BlendMode.Zero);
            m.SetInt("_Cull", cull);
            m.SetInt("_ZTest", zTest);
            m.SetInt("_ZWrite", zWrite);
            return m;
        }

        // A quad over Unity's pixel rect [x0,x1] x [y0,y1] of the active target (pixel matrix w x h).
        internal static void Quad(Material m, float w, float h, float x0, float y0, float x1, float y1, float z = 0)
        {
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            m.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Vertex3(x0, y0, z); GL.Vertex3(x1, y0, z); GL.Vertex3(x1, y1, z); GL.Vertex3(x0, y1, z);
            GL.End();
            GL.PopMatrix();
        }

        static void Rows()
        {
            var src = Pattern();
            L("rows pattern texel (x,y) = (x*60+10, y*60+10, 77); reads list pixel index y*4+x as x.y of the texel it holds");
            L("rows src GetPixels32 " + Where(src.GetPixels32(), 4));
            var rt = Rt();
            Graphics.Blit(src, rt);
            L("rows Blit(tex->rt) ReadPixels " + Where(Read(rt), 4));
            L("rows ReadPixels rect(1,2,2,2) of Blit result " + Where(Read(rt, new Rect(1, 2, 2, 2)), 2));
            var rt2 = Rt();
            Graphics.Blit(rt, rt2);
            L("rows Blit(rt->rt) ReadPixels " + Where(Read(rt2), 4));
            var rt3 = Rt();
            Graphics.CopyTexture(src, rt3);
            L("rows CopyTexture(tex->rt) ReadPixels " + Where(Read(rt3), 4));
            var rt4 = Rt();
            Graphics.CopyTexture(rt, rt4);
            L("rows CopyTexture(rt->rt) ReadPixels " + Where(Read(rt4), 4));
            var rt5 = Rt();
            Graphics.CopyTexture(src, 0, 0, 0, 0, 2, 1, rt5, 0, 0, 1, 2);
            L("rows CopyTexture region src(0,0,2x1)->dst(1,2) ReadPixels " + Where(Read(rt5), 4));
            // A quad drawn over Unity's pixel (0,0)..(1,1) (bottom left) and one over (2,0)..(4,1).
            var t = Rt();
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(t);
            GL.Clear(true, true, new Color32(0, 0, 0, 255));
            Quad(Colored(Color.red), 4, 4, 0, 0, 1, 1);
            Quad(Colored(Color.green), 4, 4, 2, 0, 4, 1);
            Graphics.SetRenderTarget(prev);
            L("rows GL quad red at pixel(0,0) green at (2..3,0), rt ReadPixels " + Px(Read(t)));
            var blitBack = Rt();
            Graphics.Blit(t, blitBack);
            L("rows Blit of that quad rt ReadPixels " + Px(Read(blitBack)));
            // Blit with a flipping scale (Unity docs: scale/offset) for orientation of the scale.
            var flip = Rt();
            Graphics.Blit(src, flip, new Vector2(1, -1), new Vector2(0, 1));
            L("rows Blit scale(1,-1) offset(0,1) ReadPixels " + Where(Read(flip), 4));
            L("rows texelSize src=" + F(src.texelSize.x) + "," + F(src.texelSize.y) + " rt=" + F(rt.texelSize.x) + "," + F(rt.texelSize.y));
            foreach (var r in new[] { rt, rt2, rt3, rt4, rt5, t, blitBack, flip }) { r.Release(); Destroy(r); }
        }

        // The depth a clear stores, read through a Blit of the depth texture into a float target.
        static void DepthClear()
        {
            foreach (float v in new[] { 1f, 0.25f, 0f })
            {
                var d = new RenderTexture(4, 4, 24, RenderTextureFormat.Depth) { name = "DxDepth", filterMode = FilterMode.Point };
                d.Create();
                var prev = RenderTexture.active;
                Graphics.SetRenderTarget(d);
                GL.Clear(true, false, Color.black, v);
                Graphics.SetRenderTarget(prev);
                var f = Rt(4, 4, 0, RenderTextureFormat.ARGBFloat);
                Graphics.Blit(d, f);
                RenderTexture.active = f;
                var t = new Texture2D(4, 4, TextureFormat.RGBAFloat, false);
                t.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                t.Apply();
                RenderTexture.active = prev;
                var c = t.GetPixel(1, 1);
                L("depthclear GL.Clear(depth=" + F(v) + ") sampled=" + F(c.r) + "," + F(c.g) + "," + F(c.b) + "," + F(c.a) + " depthStencilFormat=" + d.depthStencilFormat + " graphicsFormat=" + d.graphicsFormat);
                d.Release(); f.Release(); Destroy(d); Destroy(f); Destroy(t);
            }
            // Depth test with Unity's LEqual (4) and Greater (5): red at z=0.5 written first, then
            // green at z=-0.5 and blue at z=0.9 (pixel-matrix eye space).
            var c2 = Rt(4, 4, 24);
            var p2 = RenderTexture.active;
            Graphics.SetRenderTarget(c2);
            GL.Clear(true, true, Color.black);
            Quad(Colored(Color.red, 0, 4, 1), 4, 4, 0, 0, 4, 4, 0.5f);
            Quad(Colored(Color.green, 0, 4, 1), 4, 4, 0, 0, 2, 4, -0.5f);
            Quad(Colored(Color.blue, 0, 4, 1), 4, 4, 2, 0, 4, 4, 0.9f);
            Graphics.SetRenderTarget(p2);
            var px = Read(c2);
            L("depthtest LEqual: red z=0.5, then green z=-0.5 (left half), blue z=0.9 (right half): left=" + Px(new[] { px[0] }) + " right=" + Px(new[] { px[3] }));
            c2.Release(); Destroy(c2);
        }

        // Which winding Unity's Cull Back (2) and Cull Front (1) keep, into a render texture.
        static void Cull()
        {
            foreach (int cull in new[] { 1, 2 })
            {
                var t = Rt();
                var prev = RenderTexture.active;
                Graphics.SetRenderTarget(t);
                GL.Clear(true, true, Color.black);
                var m = Colored(Color.white, cull);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, 4, 0, 4);
                m.SetPass(0);
                GL.Begin(GL.TRIANGLES);
                // Left half: counter-clockwise in Unity's y-up pixel space; right half: clockwise.
                GL.Vertex3(0, 0, 0); GL.Vertex3(2, 0, 0); GL.Vertex3(0, 4, 0);
                GL.Vertex3(2, 0, 0); GL.Vertex3(2, 4, 0); GL.Vertex3(4, 0, 0);
                GL.End();
                GL.PopMatrix();
                Graphics.SetRenderTarget(prev);
                var px = Read(t);
                L("cull " + (cull == 1 ? "Front" : "Back") + " rt: ccw(left)=" + (px[0].r > 128 ? "drawn" : "culled") + " cw(right)=" + (px[3].r > 128 ? "drawn" : "culled"));
                t.Release(); Destroy(t);
            }
        }

        IEnumerator BackBuffer()
        {
            yield return new WaitForEndOfFrame();
            Guard("backbuffer", () =>
            {
                L("backbuffer active=" + (RenderTexture.active == null ? "null" : RenderTexture.active.name) + " screen=" + Screen.width + "x" + Screen.height);
                GL.Clear(true, true, Color.black);
                Quad(Colored(Color.red), Screen.width, Screen.height, 0, 0, 1, 1);
                Quad(Colored(Color.green), Screen.width, Screen.height, 2, 0, 4, 1);
                var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                t.Apply();
                L("backbuffer GL quad red at pixel(0,0) green at (2..3,0), ReadPixels(0,0,4,4) " + Px(t.GetPixels32()));
                t.ReadPixels(new Rect(0, Screen.height - 4, 4, 4), 0, 0);
                t.Apply();
                L("backbuffer ReadPixels(0,h-4,4,4) " + Px(t.GetPixels32()));
                foreach (int cull in new[] { 1, 2 })
                {
                    GL.Clear(true, true, Color.black);
                    var m = Colored(Color.white, cull);
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0, Screen.width, 0, Screen.height);
                    m.SetPass(0);
                    GL.Begin(GL.TRIANGLES);
                    GL.Vertex3(0, 0, 0); GL.Vertex3(2, 0, 0); GL.Vertex3(0, 4, 0);
                    GL.Vertex3(2, 0, 0); GL.Vertex3(2, 4, 0); GL.Vertex3(4, 0, 0);
                    GL.End();
                    GL.PopMatrix();
                    t.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                    t.Apply();
                    var px = t.GetPixels32();
                    L("cull " + (cull == 1 ? "Front" : "Back") + " backbuffer: ccw(left)=" + (px[0].r > 128 ? "drawn" : "culled") + " cw(right)=" + (px[3].r > 128 ? "drawn" : "culled"));
                }
                GL.Clear(true, true, Color.black);
                Quad(Colored(Color.red), Screen.width, Screen.height, 0, 0, 1, 1);
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                var sp = shot.GetPixels32(0);
                L("backbuffer CaptureScreenshotAsTexture " + shot.width + "x" + shot.height + " " + shot.format + " pixel[0]=" + Px(new[] { sp[0] }) + " pixel[last row,0]=" + Px(new[] { sp[(shot.height - 1) * shot.width] }));
                Destroy(shot); Destroy(t);
            });
        }

        IEnumerator AsyncRead()
        {
            var src = Pattern();
            var rt = Rt();
            Graphics.Blit(src, rt);
            AsyncGPUReadbackRequest req = default;
            bool ok = true;
            try { req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32); }
            catch (Exception e) { L("async EXCEPTION " + e.Message); ok = false; }
            if (!ok) yield break;
            for (int i = 0; i < 120 && !req.done; i++) yield return null;
            Guard("async", () =>
            {
                if (!req.done || req.hasError) { L("async done=" + req.done + " error=" + req.hasError); return; }
                var data = req.GetData<Color32>().ToArray();
                L("async AsyncGPUReadback(rt) " + Where(data, 4));
            });
            rt.Release(); Destroy(rt);
        }

        // ---------------------------------------------------------------- uploads

        static void Uploads()
        {
            foreach (bool mips in new[] { true, false })
            {
                var t = new Texture2D(8, 8, TextureFormat.RGBA32, true) { filterMode = FilterMode.Point };
                var px = new Color32[64];
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) px[y * 8 + x] = new Color32((byte)(x * 32), (byte)(y * 32), (byte)((x + y) % 2 * 200), 255);
                t.SetPixels32(px);
                t.SetPixels32(Enumerable.Repeat(new Color32(9, 9, 9, 9), 16).ToArray(), 1);
                t.Apply(mips);
                var m1 = Rt();
                Graphics.CopyTexture(t, 0, 1, m1, 0, 0);
                L("upload Apply(updateMipmaps=" + mips + ") mipmapCount=" + t.mipmapCount + " (mip1 CPU set to 9,9,9,9 first) GetPixels32(1)=" + Px(t.GetPixels32(1).Take(4).ToArray())
                    + " GPU mip1 via CopyTexture=" + Px(Read(m1)));
                m1.Release(); Destroy(m1); Destroy(t);
            }
        }

        static void Sampled(string what, Texture2D t)
        {
            var rt = Rt();
            Graphics.Blit(t, rt);
            string cpu;
            try { cpu = Px(t.GetPixels32()); } catch (Exception e) { cpu = "EXCEPTION " + e.GetType().Name; }
            L("raw " + what + " graphicsFormat=" + t.graphicsFormat + " sampled(Blit,ReadPixels)=" + Px(Read(rt)));
            L("raw " + what + " GetPixels32=" + cpu);
            rt.Release(); Destroy(rt);
        }

        static void Raw(string what, TextureFormat f, byte[] data)
        {
            Guard("raw " + what, () =>
            {
                if (!SystemInfo.SupportsTextureFormat(f)) { L("raw " + what + " unsupported"); return; }
                var t = new Texture2D(4, 4, f, false) { filterMode = FilterMode.Point };
                t.LoadRawTextureData(data);
                t.Apply(false);
                Sampled(what, t);
                Destroy(t);
            });
        }

        static void RawUploads()
        {
            L("raw data: memory row k holds the texels listed; Unity's raw row 0 is texture row y=0 (bottom)");
            var rgb = new byte[48];
            for (int i = 0; i < 16; i++) { rgb[i * 3] = (byte)(i * 16); rgb[i * 3 + 1] = (byte)(255 - i * 16); rgb[i * 3 + 2] = (byte)(i * 3 + 1); }
            Raw("RGB24 byte[i*3..]=(i*16,255-i*16,i*3+1)", TextureFormat.RGB24, rgb);
            var one = new byte[16];
            for (int i = 0; i < 16; i++) one[i] = (byte)(i * 16 + 5);
            Raw("Alpha8 byte[i]=i*16+5", TextureFormat.Alpha8, one);
            Raw("R8 byte[i]=i*16+5", TextureFormat.R8, one);
            // BC1: color0 = 0xF800 (red), color1 = 0x001F (blue), so 4-colour mode. Index rows in
            // memory order: row 0 all index 0, row 1 all 1, row 2 all 2, row 3 all 3.
            var bc1 = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0x00, 0x55, 0xAA, 0xFF };
            Raw("DXT1 c0=red c1=blue memrow0=idx0 memrow1=idx1 memrow2=idx2 memrow3=idx3", TextureFormat.DXT1, bc1);
            // BC3 (DXT5): alpha0 255, alpha1 0 (8-alpha mode); alpha index rows 0,1,2,3 by memory
            // row (3 bits each: row k all index k), then the BC1 colour block above.
            var bc3 = new byte[16];
            bc3[0] = 255; bc3[1] = 0;
            ulong bits = 0;
            for (int row = 0; row < 4; row++) for (int x = 0; x < 4; x++) bits |= (ulong)row << (3 * (row * 4 + x));
            for (int i = 0; i < 6; i++) bc3[2 + i] = (byte)(bits >> (8 * i));
            Array.Copy(bc1, 0, bc3, 8, 8);
            Raw("DXT5 a0=255 a1=0 alpha memrow k=idx k; colour as DXT1", TextureFormat.DXT5, bc3);
            var bc4 = new byte[8];
            Array.Copy(bc3, 0, bc4, 0, 8);
            Raw("BC4 r0=255 r1=0 memrow k=idx k", TextureFormat.BC4, bc4);
            var bc5 = new byte[16];
            Array.Copy(bc3, 0, bc5, 0, 8);
            bc5[8] = 0; bc5[9] = 255; Array.Copy(bc3, 2, bc5, 10, 6);
            Raw("BC5 red as BC4; green g0=0 g1=255 memrow k=idx k", TextureFormat.BC5, bc5);
            // BC7 mode 6 (bit 6 set): one subset, RGBA 7.7.7.7 endpoints + p-bits, 4-bit indices.
            // Endpoint 0 = black opaque-ish, endpoint 1 = white; index of texel i = i (first texel
            // takes 3 bits, anchor). Built bit by bit.
            var bc7 = new byte[16];
            int pos = 0;
            void Put(int v, int n) { for (int k = 0; k < n; k++, pos++) if (((v >> k) & 1) != 0) bc7[pos >> 3] |= (byte)(1 << (pos & 7)); }
            Put(1 << 6, 7);                 // mode 6
            Put(0, 7); Put(127, 7);         // R0 R1
            Put(0, 7); Put(127, 7);         // G0 G1
            Put(0, 7); Put(127, 7);         // B0 B1
            Put(127, 7); Put(127, 7);       // A0 A1
            Put(1, 1); Put(1, 1);           // p-bits
            Put(0, 3);                      // texel 0 index 0 (anchor, 3 bits)
            for (int i = 1; i < 16; i++) Put(i, 4);
            Raw("BC7 mode6 black->white, texel index i = i (memory order)", TextureFormat.BC7, bc7);
        }

        // GetPixels after the GPU copy is rendered into: ReadPixels without Apply, and CopyTexture.
        static void CpuAfterGpu()
        {
            var src = Pattern();
            var rt = Rt();
            Graphics.Blit(src, rt);
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            t.SetPixels32(Enumerable.Repeat(new Color32(1, 2, 3, 4), 16).ToArray());
            t.Apply();
            RenderTexture.active = rt;
            t.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
            RenderTexture.active = null;
            L("cpu ReadPixels without Apply, GetPixels32 " + Where(t.GetPixels32(), 4));
            var t2 = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            t2.SetPixels32(Enumerable.Repeat(new Color32(1, 2, 3, 4), 16).ToArray());
            t2.Apply();
            Graphics.CopyTexture(rt, t2);
            L("cpu CopyTexture(rt->tex) then GetPixels32 " + Where(t2.GetPixels32(), 4));
            var back = Rt();
            Graphics.Blit(t2, back);
            L("cpu CopyTexture(rt->tex) GPU content via Blit " + Where(Read(back), 4));
            rt.Release(); back.Release();
        }
    }
}
