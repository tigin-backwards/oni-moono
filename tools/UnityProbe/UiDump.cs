// UI layout probe: once the front end has been up for a while, every canvas's RectTransforms
// (active state, rect, world corners) and texts (content, font size, preferred size, lines),
// one line each ("PROBE ... UI ...").
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class UiDump : MonoBehaviour
    {
        public static float DelaySeconds = 8f;
        float sceneStart = -1f;
        string scene;
        bool frontEndDone, gameDone;
        float lastClock = -1f;
        int runningFrames;

        int triggered;

        void Update()
        {
            // On demand: a file named uidump-now in the profile folder dumps the overlay UI with
            // each object's layout settings, then is removed.
            if (Time.frameCount % 30 == 0)
            {
                var trigger = System.IO.Path.Combine(Application.persistentDataPath, "uidump-now");
                if (System.IO.File.Exists(trigger))
                {
                    System.IO.File.Delete(trigger);
                    Dump("trigger " + ++triggered, true, true);
                    DumpMaterials("Klei/Liquid");
                    MeasureMultiTap();
                }
            }
            var active = SceneManager.GetActiveScene().name;
            if (active != scene)
            {
                scene = active;
                sceneStart = Time.realtimeSinceStartup;
            }
            float elapsed = Time.realtimeSinceStartup - sceneStart;
            if (!frontEndDone && scene == "frontend" && elapsed >= DelaySeconds)
            {
                frontEndDone = true;
                Dump("frontend", false);
            }
            // In game: once the clock has been running for 120 frames (embarked, unpaused).
            if (scene == "backend" && GameClock.Instance != null)
            {
                float t = GameClock.Instance.GetTime();
                runningFrames = t > lastClock ? runningFrames + 1 : runningFrames;
                lastClock = t;
            }
            if (!gameDone && scene == "backend" && runningFrames >= 120)
            {
                gameDone = true;
                Dump("game", true);
                Triggers.Ready = true;
            }
        }

        /// Every loaded material using the shader: its float, range and vector properties, with the
        /// zoom globals the game sets for it.
        static void DumpMaterials(string shaderName)
        {
            Rec.Log("=== materials " + shaderName + ": _WorldCameraPos " + Shader.GetGlobalVector("_WorldCameraPos") + " _CameraZoomInfo " + Shader.GetGlobalVector("_CameraZoomInfo"));
            foreach (var global in new[] { "_SolidLiquidGasMassTex", "_LiquidTex", "_MaterialDataTex", "_LiquidDataTex" })
            {
                var t = Shader.GetGlobalTexture(global);
                if (t != null) SaveTexture(t, global);
                Rec.Log("GLOBALTEX " + global + " " + (t == null ? "null" : t.GetType().Name + " " + t.name + " " + t.width + "x" + t.height + " " + t.filterMode + " " + t.wrapMode + " mips " + t.mipmapCount + " aniso " + t.anisoLevel + " bias " + t.mipMapBias.ToString("R", CultureInfo.InvariantCulture)));
            }
            foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (rt.name is "MRT0" or "MRT1" or "MRT0_Copy" or "MRT1_Copy")
                {
                    Rec.Log("RT " + rt.name + " " + rt.width + "x" + rt.height + " " + rt.format + " " + rt.filterMode + " " + rt.wrapMode);
                    SaveTexture(rt, rt.name);
                }
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m.shader == null || m.shader.name != shaderName) continue;
                var sb = new StringBuilder("MAT " + m.name + " keywords [" + string.Join(" ", m.shaderKeywords) + "]");
                var shader = m.shader;
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var name = shader.GetPropertyName(i);
                    switch (shader.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            sb.Append(' ').Append(name).Append('=').Append(m.GetFloat(name).ToString("R", CultureInfo.InvariantCulture)); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            sb.Append(' ').Append(name).Append('=').Append(m.GetVector(name)); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            var t = m.GetTexture(name);
                            sb.Append(' ').Append(name).Append('=').Append(t == null ? "null" : t.name + "(" + t.width + "x" + t.height + " " + t.filterMode + " mips " + t.mipmapCount + ")"); break;
                    }
                }
                Rec.Log(sb.ToString());
            }
        }

        /// Where Graphics.BlitMultiTap's taps land with the game's cone-tap blur (Hidden/BlurEffectConeTap,
        /// which builds its taps from _BlurOffsets): one white texel at (16, 16) of a 32×32 source,
        /// blitted with distinct offsets, and every lit texel of the result with its value.
        static void MeasureMultiTap()
        {
            var shader = Shader.Find("Hidden/BlurEffectConeTap");
            if (shader == null) { Rec.Log("MULTITAP no shader"); return; }
            var material = new Material(shader);
            var source = new Texture2D(32, 32, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[32 * 32];
            pixels[16 * 32 + 16] = new Color32(255, 255, 255, 255);
            source.SetPixels32(pixels);
            source.Apply();
            var cases = new[]
            {
                new[] { new Vector2(-2f, -2f), new Vector2(-2f, 2f), new Vector2(2f, 2f), new Vector2(2f, -2f) },
                new[] { new Vector2(3f, 1f), new Vector2(-5f, 4f), new Vector2(6f, -7f), new Vector2(0f, 2f) },
            };
            foreach (var offsets in cases)
            {
                var dest = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                dest.filterMode = FilterMode.Point;
                var previous = RenderTexture.active;
                RenderTexture.active = dest;
                GL.Clear(true, true, Color.clear);
                Graphics.BlitMultiTap(source, dest, material, offsets);
                RenderTexture.active = dest;
                var copy = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                var lit = new StringBuilder();
                var result = copy.GetPixels32();
                for (int i = 0; i < result.Length; i++)
                    if (result[i].r > 0) lit.Append(" (").Append(i % 32).Append(',').Append(i / 32).Append(")=").Append(result[i].r);
                Rec.Log("MULTITAP offsets " + string.Join(" ", System.Array.ConvertAll(offsets, o => o.ToString())) + " lit" + lit);
                RenderTexture.ReleaseTemporary(dest);
                Object.Destroy(copy);
            }
            Object.Destroy(source);
            Object.Destroy(material);
        }

        /// A texture's texels as raw RGBA bytes in the profile folder (texdump-NAME-WxH.rgba), read back through a
        /// render texture so it works for any texture.
        static void SaveTexture(Texture t, string name)
        {
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Graphics.Blit(t, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Application.persistentDataPath, "texdump-" + name + "-" + t.width + "x" + t.height + ".rgba"), copy.GetRawTextureData());
            Object.Destroy(copy);
        }

        /// Anchors, pivot and size, and the settings of the object's layout components.
        static void AppendLayout(StringBuilder sb, RectTransform rt)
        {
            sb.Append(" anchors ").Append(F(rt.anchorMin.x)).Append(',').Append(F(rt.anchorMin.y)).Append('-').Append(F(rt.anchorMax.x)).Append(',').Append(F(rt.anchorMax.y));
            sb.Append(" pivot ").Append(F(rt.pivot.x)).Append(',').Append(F(rt.pivot.y));
            sb.Append(" pos ").Append(F(rt.anchoredPosition.x)).Append(',').Append(F(rt.anchoredPosition.y));
            sb.Append(" sizeDelta ").Append(F(rt.sizeDelta.x)).Append(',').Append(F(rt.sizeDelta.y));
            foreach (var le in rt.GetComponents<UnityEngine.UI.LayoutElement>())
                sb.Append(" LE(").Append(le.enabled ? "" : "off ").Append(le.ignoreLayout ? "ignore " : "")
                  .Append(F(le.minWidth)).Append(',').Append(F(le.minHeight)).Append(' ').Append(F(le.preferredWidth)).Append(',').Append(F(le.preferredHeight))
                  .Append(' ').Append(F(le.flexibleWidth)).Append(',').Append(F(le.flexibleHeight)).Append(')');
            foreach (var g in rt.GetComponents<UnityEngine.UI.HorizontalOrVerticalLayoutGroup>())
                sb.Append(' ').Append(g is UnityEngine.UI.VerticalLayoutGroup ? "VLG(" : "HLG(").Append(g.enabled ? "" : "off ")
                  .Append("pad ").Append(g.padding.left).Append(',').Append(g.padding.right).Append(',').Append(g.padding.top).Append(',').Append(g.padding.bottom)
                  .Append(" sp ").Append(F(g.spacing)).Append(' ').Append(g.childAlignment)
                  .Append(" ctrl ").Append(g.childControlWidth ? 'W' : '-').Append(g.childControlHeight ? 'H' : '-')
                  .Append(" scale ").Append(g.childScaleWidth ? 'W' : '-').Append(g.childScaleHeight ? 'H' : '-')
                  .Append(" expand ").Append(g.childForceExpandWidth ? 'W' : '-').Append(g.childForceExpandHeight ? 'H' : '-')
                  .Append(g.reverseArrangement ? " reverse" : "").Append(')');
            foreach (var g in rt.GetComponents<UnityEngine.UI.GridLayoutGroup>())
                sb.Append(" GLG(cell ").Append(F(g.cellSize.x)).Append(',').Append(F(g.cellSize.y)).Append(" sp ").Append(F(g.spacing.x)).Append(',').Append(F(g.spacing.y))
                  .Append(' ').Append(g.constraint).Append(' ').Append(g.constraintCount).Append(')');
            foreach (var f in rt.GetComponents<UnityEngine.UI.ContentSizeFitter>())
                sb.Append(" CSF(").Append(f.enabled ? "" : "off ").Append(f.horizontalFit).Append(',').Append(f.verticalFit).Append(')');
            foreach (var f in rt.GetComponents<UnityEngine.UI.AspectRatioFitter>())
                sb.Append(" ARF(").Append(f.aspectMode).Append(' ').Append(F(f.aspectRatio)).Append(')');
            var sr = rt.GetComponent<UnityEngine.UI.ScrollRect>();
            if (sr != null) sb.Append(" ScrollRect(").Append(sr.horizontal ? 'H' : '-').Append(sr.vertical ? 'V' : '-').Append(')');
        }

        static string F(float v) { var s = v.ToString("F1", CultureInfo.InvariantCulture); return s == "-0.0" ? "0.0" : s; }

        static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        /// overlayOnly: only screen-space overlay canvases (world-space name tags follow the
        /// Duplicants, which move differently in each run).
        public static void Dump(string label, bool overlayOnly, bool layout = false)
        {
            Rec.Log("=== ui " + label + ": screen " + Screen.width + "x" + Screen.height);
            var corners = new Vector3[4];
            var lines = new System.Collections.Generic.List<string>();
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (canvas.isRootCanvas)
                {
                    var cam = canvas.worldCamera;
                    lines.Add("UI canvas " + Path(canvas.transform) + " mode=" + canvas.renderMode + " scale=" + canvas.scaleFactor.ToString("F3", CultureInfo.InvariantCulture)
                              + (cam == null ? "" : " camera " + cam.name + " " + cam.pixelWidth + "x" + cam.pixelHeight + " target " + (cam.targetTexture == null ? "none" : cam.targetTexture.width + "x" + cam.targetTexture.height)));
                }
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (c.isRootCanvas || (overlayOnly && c.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay && c.renderMode != RenderMode.ScreenSpaceOverlay)) continue;
                var own = c.GetComponentInParent<Canvas>();
                lines.Add("UI nested " + Path(c.transform) + " enabled=" + c.enabled + " root=" + c.rootCanvas.name + " mode=" + c.renderMode
                          + " override=" + c.overrideSorting + " order=" + c.sortingOrder + " inParent=" + (own == null ? "null" : own == c ? "self" : own.name));
            }
            foreach (var rt in Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var parentCanvas = rt.GetComponentInParent<Canvas>();
                if (parentCanvas == null) continue;
                if (overlayOnly && parentCanvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                var sb = new StringBuilder("UI ");
                try
                {
                    rt.GetWorldCorners(corners);
                    sb.Append(Path(rt));
                    if (rt.parent != null) sb.Append(" #").Append(rt.GetSiblingIndex());
                    var r = rt.rect;
                    sb.Append(" rect ").Append(F(r.x)).Append(',').Append(F(r.y)).Append(' ').Append(F(r.width)).Append('x').Append(F(r.height));
                    sb.Append(" world ").Append(F(corners[0].x)).Append(',').Append(F(corners[0].y)).Append(" to ").Append(F(corners[2].x)).Append(',').Append(F(corners[2].y));
                    if (layout) AppendLayout(sb, rt);
                    var text = rt.GetComponent<TMP_Text>();
                    if (text != null && text.enabled)
                    {
                        sb.Append(" text ").Append(text.text == null ? "null" : "\"" + text.text.Replace("\n", "\\n") + "\"");
                        sb.Append(" size ").Append(F(text.fontSize));
                        sb.Append(" pref ").Append(F(text.preferredWidth)).Append('x').Append(F(text.preferredHeight));
                        sb.Append(" lines ").Append(text.textInfo != null ? text.textInfo.lineCount : -1);
                    }
                }
                catch (System.Exception e)
                {
                    sb.Append(" ERROR ").Append(e.GetType().Name);
                }
                lines.Add(sb.ToString());
            }
            lines.Sort(System.StringComparer.Ordinal);
            foreach (var l in lines) Rec.Log(l);
            Rec.Log("=== ui " + label + ": done");
        }
    }
}
