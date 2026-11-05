// What a sampler reads when its texture is missing ("PROBE ... TF ..."), at the main menu (10 s
// in). Found with the gas shader's _GasOffsetTex, which after a load names a render texture
// FlowOffsetRenderer has destroyed.
//
// Needs a game whose Resources/unity_builtin_extra has Hidden/BlitCopy's program (texture times
// _Color) in place of eight unused shaders, each keeping its name, with _MainTex declared with
// another default or not at all (tools/UnityProbe/tf-shaders.py writes it). Each case draws a
// quad with GL immediate drawing into a 4 x 4 ARGB32 texture and logs the bytes it reads:
// the declaration (white, black, grey, gray, bump, red, nonsense, "" and undeclared) crossed with
// the route (the material's _MainTex, or the global) and the state: never set, set to null, a
// Texture2D destroyed, a render texture destroyed, released, recreated at another size (and not
// recreated after the resize), never created (set as it is, then created, released, or only made
// a render target), and a live texture as the control. Each case is drawn after a draw that bound
// a red texture and after one that bound a green one, so a sampler left unbound shows what the
// unit held before (neither did). Targets are set with Graphics.SetRenderTarget: ours binds no
// target when RenderTexture.active alone is set. It also logs Material.GetTexture, HasProperty
// and HasTexture of an undeclared property and a new material's name.
// With PROBE_SCENARIO=texfallback only this probe runs; it ends with a TextureFallbackProbeDone
// canvas.
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class TextureFallback : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        RenderTexture target;
        Texture2D read;
        Material red, green;
        static readonly Color32 C1 = new Color32(51, 102, 153, 204), C2 = new Color32(204, 153, 102, 51);
        static readonly int MainTex = Shader.PropertyToID("_MainTex");

        static readonly (string Decl, string Shader)[] Shaders =
        {
            ("empty", "Hidden/BlitCopy"), ("white", "Hidden/VideoDecode"), ("black", "Hidden/VideoComposite"),
            ("grey", "Hidden/Compositing"), ("gray", "Hidden/BlitToDepth"), ("bump", "Hidden/BlitToDepth_MSAA"),
            ("red", "Hidden/BlitCopyHDRTonemap"), ("nonsense", "Hidden/VR/BlitTexArraySlice"),
            ("undeclared", "Hidden/VR/BlitFromTex2DToTexArraySlice"),
        };

        static void L(string s) => Rec.Log("TF " + s);

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static Texture2D Solid(Color32 c, int size = 4)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "TfSolid" };
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static RenderTexture SolidRt(Color32 c, int size = 4)
        {
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32) { name = "TfRt" };
            rt.Create();
            Fill(rt, c);
            return rt;
        }

        static void Fill(RenderTexture rt, Color32 c)
        {
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(rt);
            GL.Clear(false, true, c);
            Graphics.SetRenderTarget(prev);
        }

        string Draw(Material m)
        {
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            GL.Clear(true, true, new Color32(10, 230, 80, 170));
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, 4f, 0f, 4f);
            bool ok = m.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(4, 0, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(4, 4, 0);
            GL.TexCoord2(0, 1); GL.Vertex3(0, 4, 0);
            GL.End();
            GL.PopMatrix();
            read.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
            read.Apply();
            Graphics.SetRenderTarget(prev);
            Color32 p = read.GetPixel(1, 1);
            return (ok ? "" : "nopass:") + p.r + "," + p.g + "," + p.b + "," + p.a;
        }

        // The case drawn after a red and after a green draw.
        string Reads(Material m)
        {
            Draw(red);
            string a = Draw(m);
            Draw(green);
            string b = Draw(m);
            return a == b ? a : "after-red=" + a + " after-green=" + b;
        }

        static string Got(Texture t) => ReferenceEquals(t, null) ? "null" : t == null ? "destroyed" : t.name + " " + t.width + "x" + t.height + (t is RenderTexture rt ? " created=" + rt.IsCreated() : "");

        IEnumerator Run()
        {
            yield return new WaitForEndOfFrame();
            target = new RenderTexture(4, 4, 24, RenderTextureFormat.ARGB32) { name = "TfTarget" };
            target.Create();
            read = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var blit = Shader.Find("Hidden/BlitCopy");
            red = new Material(blit);
            red.SetTexture(MainTex, Solid(new Color32(255, 0, 0, 255)));
            green = new Material(blit);
            green.SetTexture(MainTex, Solid(new Color32(0, 255, 0, 255)));
            L("=== start colorSpace=" + QualitySettings.activeColorSpace + " device=" + SystemInfo.graphicsDeviceType + " red=" + Draw(red) + " green=" + Draw(green));
            var shaders = new List<(string Decl, Shader Shader)>();
            foreach (var (decl, name) in Shaders)
            {
                var s = Shader.Find(name);
                if (s == null) { L("shader " + decl + " " + name + " missing"); continue; }
                string prop;
                try
                {
                    int i = s.FindPropertyIndex("_MainTex");
                    prop = i < 0 ? "none" : s.GetPropertyType(i) + " default=" + s.GetPropertyTextureDefaultName(i) + " dim=" + s.GetPropertyTextureDimension(i);
                }
                catch (System.Exception e) { prop = e.GetType().Name; }
                L("shader " + decl + " " + name + " property=" + prop);
                shaders.Add((decl, s));
            }
            // Material.GetTexture of a property the shader does not declare: what it returns and
            // logs, and the name of a material made from a shader.
            foreach (var (decl, s) in shaders)
            {
                if (decl != "undeclared" && decl != "white") continue;
                var m = new Material(s);
                m.SetTexture(MainTex, Solid(C1));
                var logged = new List<string>();
                Application.LogCallback hook = (c, st, t) => logged.Add(t + ": " + c);
                Application.logMessageReceived += hook;
                var got = m.GetTexture(MainTex);
                var gotByName = m.GetTexture("_MainTex");
                bool has = m.HasProperty(MainTex), hasTexture = m.HasTexture(MainTex);
                Application.logMessageReceived -= hook;
                L("gettexture " + decl + " name='" + m.name + "' get=" + Got(got) + " byname=" + Got(gotByName) + " has=" + has + " hasTexture=" + hasTexture + " logged=[" + string.Join(" | ", logged) + "]");
            }
            // Global route first: nothing has set the global yet.
            foreach (var (decl, s) in shaders)
                L("global never " + decl + " = " + Reads(new Material(s)) + " get=" + Got(Shader.GetGlobalTexture(MainTex)));
            foreach (var route in new[] { "material", "global" })
            {
                foreach (var (decl, s) in shaders)
                {
                    string Case(string state, Texture t, System.Action change = null)
                    {
                        var m = new Material(s);
                        if (route == "material") m.SetTexture(MainTex, t); else Shader.SetGlobalTexture(MainTex, t);
                        string before = Draw(m);
                        change?.Invoke();
                        string r = Reads(m);
                        L(route + " " + state + " " + decl + " = " + r + " (before " + before + ") get=" + Got(route == "material" ? m.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)));
                        return r;
                    }
                    if (route == "material") L(route + " never " + decl + " = " + Reads(new Material(s)));
                    Case("null", null);
                    Case("live", Solid(C1));
                    Case("rt-live", SolidRt(C1));
                    // Destroy takes effect at the end of the frame.
                    {
                        var m = new Material(s);
                        var t = Solid(C1);
                        if (route == "material") m.SetTexture(MainTex, t); else Shader.SetGlobalTexture(MainTex, t);
                        string before = Draw(m);
                        Destroy(t);
                        yield return null;
                        yield return new WaitForEndOfFrame();
                        L(route + " destroyed " + decl + " = " + Reads(m) + " (before " + before + ") get=" + Got(route == "material" ? m.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)));
                    }
                    {
                        var m = new Material(s);
                        var rt = SolidRt(C1);
                        if (route == "material") m.SetTexture(MainTex, rt); else Shader.SetGlobalTexture(MainTex, rt);
                        string before = Draw(m);
                        rt.Release();
                        Destroy(rt);
                        yield return null;
                        yield return new WaitForEndOfFrame();
                        L(route + " rt-destroyed " + decl + " = " + Reads(m) + " (before " + before + ") get=" + Got(route == "material" ? m.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)));
                    }
                    {
                        RenderTexture rt = null;
                        Case("rt-released", rt = SolidRt(C1), () => rt.Release());
                        L(route + " rt-released " + decl + " after: created=" + rt.IsCreated());
                    }
                    {
                        RenderTexture rt = null;
                        Case("rt-resized", rt = SolidRt(C1), () => { rt.Release(); rt.width = 8; rt.height = 8; rt.Create(); Fill(rt, C2); });
                    }
                    // A render texture never created: set as it is, and set then created.
                    {
                        var m = new Material(s);
                        var rt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32) { name = "TfNew" };
                        if (route == "material") m.SetTexture(MainTex, rt); else Shader.SetGlobalTexture(MainTex, rt);
                        L(route + " rt-new " + decl + " = " + Reads(m) + " get=" + Got(route == "material" ? m.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)) + " created=" + rt.IsCreated());
                        rt.Create();
                        Fill(rt, C2);
                        L(route + " rt-new-created " + decl + " = " + Reads(m) + " get=" + Got(route == "material" ? m.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)));
                        rt.Release();
                        L(route + " rt-new-released " + decl + " = " + Reads(m) + " get=" + Got(route == "material" ? m.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)));
                        var m2 = new Material(s);
                        var unfilled = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32) { name = "TfNew2" };
                        if (route == "material") m2.SetTexture(MainTex, unfilled); else Shader.SetGlobalTexture(MainTex, unfilled);
                        Graphics.SetRenderTarget(unfilled);
                        Graphics.SetRenderTarget(null);
                        L(route + " rt-new-targeted " + decl + " = " + Reads(m2) + " created=" + unfilled.IsCreated() + " get=" + Got(route == "material" ? m2.GetTexture(MainTex) : Shader.GetGlobalTexture(MainTex)));
                    }
                    {
                        RenderTexture rt = null;
                        Case("rt-resized-uncreated", rt = SolidRt(C1), () => { rt.Release(); rt.width = 8; rt.height = 8; });
                        L(route + " rt-resized-uncreated " + decl + " after: created=" + rt.IsCreated());
                    }
                }
            }
            var done = new GameObject("TextureFallbackProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            L("=== done");
        }
    }
}
