// Nested indents ("PROBE ... IN ..."), at the main menu: TMP's preferred width, each character's
// pen and vertices and each line's metrics for texts whose <indent> changes part-way along a
// line, as the details screen's bulleted descriptors write them ("<indent=1em>•<indent=1.75em>
// …</indent>"): one indent, two and three nested in em, px and %, an inner indent behind the
// pen, </indent> closing part-way, <link> and <pos> beside it, several lines and wrapping. In
// NotoSans-Regular and GRAYSTROKE REGULAR SDF at 12, 14, 16 and 20 pt, set up as
// SetTextStyleSetting does (kerning, extra padding), at pivot 0 in a 400-wide rect (wrapped
// texts also 60 wide). Ends with an IndentProbeDone object. With PROBE_SCENARIO=indent only this
// probe runs.
#pragma warning disable CS0618 // enableKerning, as SetTextStyleSetting.ApplyStyle sets it
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class IndentWidth : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void L(string s) => Rec.Log("IN " + s);
        static string R(float v) => v.ToString("R", Inv);

        public static readonly string[] Texts =
        {
            "•Polluted Dirt",
            "<indent=1em>•Polluted Dirt",
            "<indent=1.75em>Polluted Dirt",
            "<indent=1em>•<indent=1.75em>Polluted Dirt",
            "<indent=1em>•<indent=1.75em>Polluted Dirt: 110 g/s</indent>",
            "<indent=1em>•<indent=1.75em><b><style=\"KLink\"><link=\"TOXICSAND\">Polluted Dirt</style></b></link>: 110 g/s</indent>",
            "<indent=1em>•<indent=1.75em><link=\"X\">Polluted Dirt</link>",
            "<indent=12>•<indent=21>Polluted Dirt",
            "<indent=12px>•<indent=21px>Polluted Dirt",
            "•<indent=1.75em>Polluted Dirt",
            "<indent=1em>•<indent=3em>Polluted Dirt",
            "<indent=1em>•<indent=1.2em>Polluted Dirt",
            "<indent=2em>ABCD<indent=1em>Polluted Dirt",
            "<indent=1em>•</indent>Polluted Dirt",
            "<indent=1em>•<indent=1.75em>Polluted</indent> Dirt",
            "<indent=1em>•<indent=1.75em>Polluted</indent></indent> Dirt",
            "<indent=1em><indent=1.75em>Polluted Dirt",
            "<indent=1em>•<indent=1.75em>Polluted Dirt\nSecond line",
            "<indent=1em>•<indent=1.75em>Polluted Dirt\n<indent=1em>•<indent=1.75em>Polluted Dirt: 110 g/s",
            "<indent=1em>•<indent=1.75em><indent=2.5em>Polluted Dirt",
            "<indent=1em>•<indent=1.75em>X<indent=4em>Y",
            "<indent=10%>•<indent=20%>Polluted Dirt",
            "<indent=1em>•<indent=1.75em>Polluted Dirt ",
            "A<indent=1.75em>B",
            "<pos=1em>•<pos=1.75em>Polluted Dirt",
            "<indent=1em>•<pos=1.75em>Polluted Dirt",
            "<indent=1em>•<indent=1.75em><size=150%>Polluted</size> Dirt",
            "<indent=1em>•<indent=1.75em>Polluted Dirt Polluted Dirt",
        };

        static string Hex(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(c >= 0x20 && c < 0x7F && c != '"' && c != '\\' ? c.ToString() : "\\u" + ((int)c).ToString("X4"));
            return sb.ToString();
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
            var cgo = new GameObject("InCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            yield return null;
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            foreach (var name in new[] { "NotoSans-Regular", "GRAYSTROKE REGULAR SDF" })
            {
                var font = fonts.FirstOrDefault(f => f.name == name);
                if (font == null) { L("font " + name + " missing"); continue; }
                foreach (float size in new[] { 12f, 14f, 16f, 20f })
                {
                    for (int t = 0; t < Texts.Length; t++)
                    {
                        try { Measure(canvas.transform, font, size, t, 400f, false); }
                        catch (Exception e) { L("t" + t + " threw " + e.GetType().Name + ": " + e.Message); }
                        if (Texts[t].Contains("\n") || Texts[t].EndsWith("Dirt Polluted Dirt"))
                        {
                            try { Measure(canvas.transform, font, size, t, 60f, true); }
                            catch (Exception e) { L("t" + t + " wrap threw " + e.GetType().Name + ": " + e.Message); }
                        }
                    }
                    yield return null;
                }
            }
            Destroy(cgo);
            L("=== done");
            var done = new GameObject("IndentProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        }

        static void Measure(Transform canvas, TMP_FontAsset font, float size, int t, float width, bool wrap)
        {
            string text = Texts[t];
            string label = font.name + " " + R(size) + " t" + t + (wrap ? " w" + R(width) : "");
            var go = new GameObject("In", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(width, 100);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            tmp.enableKerning = true;
            tmp.extraPadding = true;
            tmp.fontSize = size;
            tmp.font = font;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.text = text;
            if (size == 12f && !wrap && font.name == "NotoSans-Regular") L("text t" + t + " \"" + Hex(text) + "\"");
            float pw = tmp.preferredWidth, ph = tmp.preferredHeight;
            var pv = tmp.GetPreferredValues(text);
            var pvw = tmp.GetPreferredValues(text, width, 100);
            tmp.ForceMeshUpdate();
            var info = tmp.textInfo;
            L(label + " pref=" + R(pw) + "x" + R(ph) + " gpv=" + R(pv.x) + "x" + R(pv.y) + " gpvw=" + R(pvw.x) + "x" + R(pvw.y)
              + " rendered=" + R(tmp.renderedWidth) + "x" + R(tmp.renderedHeight) + " bounds=" + R(tmp.textBounds.min.x) + ".." + R(tmp.textBounds.max.x)
              + " counts=" + info.characterCount + "," + info.lineCount + "," + info.linkCount);
            var sb = new StringBuilder();
            for (int i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                sb.Append(' ').Append((int)ch.character).Append(':').Append(R(ch.origin)).Append('/').Append(R(ch.xAdvance))
                  .Append('/').Append(R(ch.bottomLeft.x)).Append('/').Append(R(ch.topRight.x))
                  .Append('/').Append(R(ch.vertex_BL.position.x)).Append('/').Append(R(ch.vertex_TR.position.x))
                  .Append('/').Append(ch.isVisible ? 'v' : 'i').Append(ch.lineNumber);
            }
            L(label + " chars" + sb);
            for (int k = 0; k < info.lineCount; k++)
            {
                var line = info.lineInfo[k];
                L(label + " line" + k + " first=" + line.firstCharacterIndex + " last=" + line.lastCharacterIndex + " width=" + R(line.width) + " length=" + R(line.length)
                  + " maxAdvance=" + R(line.maxAdvance) + " marginLeft=" + R(line.marginLeft) + " marginRight=" + R(line.marginRight)
                  + " extents=" + R(line.lineExtents.min.x) + ".." + R(line.lineExtents.max.x));
            }
            for (int k = 0; k < info.linkCount; k++)
            {
                var link = info.linkInfo[k];
                L(label + " link" + k + " id=" + link.GetLinkID() + " first=" + link.linkTextfirstCharacterIndex + " length=" + link.linkTextLength);
            }
            UnityEngine.Object.Destroy(go);
        }
    }
}
