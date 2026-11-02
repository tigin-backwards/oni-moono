// Bold and mid-line indents in TextMeshPro ("PROBE ... RT ..."), at the main menu: how <b> draws
// in the game's body font (NotoSans-Regular, as its tooltips set it) and in its heading font
// (GRAYSTROKE), and where text goes after an <indent> that comes part-way along a line (the
// hanging indent of a bullet, "•<indent=1em>text</indent>", nested and wrapped, and an indent
// left of where the line already is). Each case is laid out at a fixed place on a black
// full-screen panel, its characters logged (character, origin, advance, the bottom-left and
// top-right of its quad, line, style), and the whole screen captured to
// PROBE_CAPTURE_DIR/richtext.png for pixel comparison (rt-check.py). A `RichTextProbeDone`
// object marks the end. With PROBE_SCENARIO=richtext only this probe runs.
#pragma warning disable CS0618 // enableKerning, as SetTextStyleSetting.ApplyStyle sets it
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class RichText : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void L(string s) => Rec.Log("RT " + s);
        static string R(float v) => v.ToString("R", Inv);

        /// Each case: its font, size, rect width (0: no wrapping) and text.
        public static readonly (string Font, float Size, float Width, string Text)[] Cases =
        {
            ("NotoSans-Regular", 14f, 0f, "Regular <b>Bold</b> Regular"),
            ("NotoSans-Regular", 18f, 0f, "Regular <b>Bold</b> Regular"),
            ("NotoSans-Regular", 14f, 0f, "<b>2026.11.03.01</b> 2026.11.03.01"),
            ("GRAYSTROKE REGULAR SDF", 18f, 0f, "Regular <b>Bold</b> Regular"),
            ("NotoSans-Regular", 14f, 300f, "•<indent=1em>Big bases with Fast Track draw faster: each camera now looks only at renderers.</indent>"),
            ("NotoSans-Regular", 14f, 300f, "<indent=1em>–<indent=2em>A nested bullet that wraps onto a second line here.</indent></indent>"),
            ("NotoSans-Regular", 14f, 0f, "ab<indent=3em>cd"),
            ("NotoSans-Regular", 14f, 0f, "abcdefghijkl<indent=2em>mn"),
            ("NotoSans-Regular", 14f, 0f, "ab<indent=3em>cd\nef"),
            ("NotoSans-Regular", 14f, 0f, "ab<indent=40>cd</indent>ef"),
            ("NotoSans-Regular", 14f, 0f, "ab<pos=3em>cd"),
        };

        /// Where case i's rect's top-left goes, in screen pixels from the top left.
        public static Vector2 Place(int i) => new Vector2(20f + (i >= 6 ? 420f : 0f), 20f + 70f * (i >= 6 ? i - 6 : i));

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
            var cgo = new GameObject("RtCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            var bg = new GameObject("Black", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(cgo.transform, false);
            var bgr = (RectTransform)bg.transform;
            bgr.anchorMin = Vector2.zero;
            bgr.anchorMax = Vector2.one;
            bgr.sizeDelta = Vector2.zero;
            bg.color = Color.black;
            yield return null;
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < Cases.Length; i++)
            {
                var (name, size, width, text) = Cases[i];
                var font = fonts.FirstOrDefault(f => f.name == name);
                if (font == null) { L("font " + name + " missing"); continue; }
                var go = new GameObject("Rt" + i, typeof(RectTransform));
                var rt = (RectTransform)go.transform;
                rt.SetParent(cgo.transform, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                var at = Place(i);
                rt.anchoredPosition = new Vector2(at.x, -at.y);
                rt.sizeDelta = new Vector2(width > 0 ? width : 400f, 60f);
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.enableKerning = true;
                tmp.extraPadding = true;
                tmp.font = font;
                tmp.fontSize = size;
                tmp.color = Color.white;
                tmp.alignment = TextAlignmentOptions.TopLeft;
                tmp.textWrappingMode = width > 0 ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                tmp.text = text;
                tmp.ForceMeshUpdate();
                Log(i, tmp);
            }
            yield return null;
            yield return null;
            var dir = System.Environment.GetEnvironmentVariable("PROBE_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "richtext.png"));
                L("capture richtext screen " + Screen.width + "x" + Screen.height);
            }
            for (int f = 0; f < 10; f++) yield return null;
            var done = new GameObject("RichTextProbeDone", typeof(RectTransform));
            done.transform.SetParent(cgo.transform, false);
            L("=== done");
        }

        static void Log(int i, TextMeshProUGUI tmp)
        {
            var info = tmp.textInfo;
            var sb = new StringBuilder();
            for (int c = 0; c < info.characterCount; c++)
            {
                var ch = info.characterInfo[c];
                if (c > 0) sb.Append(';');
                sb.Append(((int)ch.character).ToString("X")).Append(':').Append(R(ch.origin)).Append(',').Append(R(ch.xAdvance))
                  .Append(",bl=").Append(R(ch.bottomLeft.x)).Append('/').Append(R(ch.bottomLeft.y))
                  .Append(",tr=").Append(R(ch.topRight.x)).Append('/').Append(R(ch.topRight.y))
                  .Append(",l").Append(ch.lineNumber).Append(",s").Append((int)ch.style);
            }
            L("case " + i + " pw=" + R(tmp.preferredWidth) + " ph=" + R(tmp.preferredHeight) + " lines=" + info.lineCount + " chars=" + sb);
        }
    }
}
