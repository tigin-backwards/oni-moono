// Line extents of superscript and subscript text ("PROBE ... SU ..."), at the main menu. Found in
// U60's Colony Summaries (u60-qa-spec.md R4): an achievement's description (NotoSans 14, wrapped,
// "... RelicAAAA<sup>AAAGHH</sup>.") was 31.74 high in the original and 38.08 in ours. Texts with
// <sup>, <sub> and plain ones, wrapped in a 338.22-wide rect and unwrapped, at 14 pt: the
// preferred height, each line's ascender, descender, baseline and line height, and each
// character's ascender, descender, baseline and scale. Set up as SetTextStyleSetting does
// (kerning, extra padding). With PROBE_SCENARIO=suptext only this probe runs.
#pragma warning disable CS0618 // enableKerning, as SetTextStyleSetting.ApplyStyle sets it
using System.Collections;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class SupText : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void L(string s) => Rec.Log("SU " + s);
        static string R(float v) => v.ToString("R", Inv);

        static readonly string[] Texts =
        {
            "Survive 100 cycles after Demolior asteroid collides with RelicAAAA<sup>AAAGHH</sup>.",
            "A<sup>B</sup>", "<sup>B</sup>", "A<sub>B</sub>", "<sub>B</sub>", "AB",
            "A\n<sup>B</sup>", "<sup>B</sup>\nA", "A\nB<sup>C</sup>", "A\nB<sub>C</sub>",
        };

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
            var cgo = new GameObject("SuCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            yield return null;
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "NotoSans-Regular");
            if (font == null) { L("font missing"); yield break; }
            var fi = font.faceInfo;
            L("face superscriptOffset=" + R(fi.superscriptOffset) + " superscriptSize=" + R(fi.superscriptSize)
              + " subscriptOffset=" + R(fi.subscriptOffset) + " subscriptSize=" + R(fi.subscriptSize)
              + " ascent=" + R(fi.ascentLine) + " descent=" + R(fi.descentLine) + " lineHeight=" + R(fi.lineHeight)
              + " pointSize=" + R(fi.pointSize) + " scale=" + R(fi.scale));
            for (int t = 0; t < Texts.Length; t++)
                foreach (bool wrap in new[] { true, false })
                {
                    var go = new GameObject("Su", typeof(RectTransform));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(canvas.transform, false);
                    rt.sizeDelta = new Vector2(338.22f, 200f);
                    var tmp = go.AddComponent<TextMeshProUGUI>();
                    tmp.enableKerning = true;
                    tmp.extraPadding = true;
                    tmp.font = font;
                    tmp.fontSize = 14f;
                    tmp.alignment = TextAlignmentOptions.TopLeft;
                    tmp.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                    tmp.text = Texts[t];
                    tmp.ForceMeshUpdate();
                    var info = tmp.textInfo;
                    L("text " + t + " wrap=" + (wrap ? 1 : 0) + " ph=" + R(tmp.preferredHeight) + " pw=" + R(tmp.preferredWidth)
                      + " lines=" + info.lineCount + " bounds=" + R(tmp.textBounds.size.y));
                    for (int i = 0; i < info.lineCount; i++)
                    {
                        var li = info.lineInfo[i];
                        L("  line " + i + " asc=" + R(li.ascender) + " desc=" + R(li.descender) + " base=" + R(li.baseline)
                          + " height=" + R(li.lineHeight)
                          + " first=" + li.firstCharacterIndex + " last=" + li.lastCharacterIndex);
                    }
                    for (int i = 0; i < info.characterCount; i++)
                    {
                        var ci = info.characterInfo[i];
                        if (ci.character == ' ') continue;
                        L("  char " + i + " '" + ci.character + "' asc=" + R(ci.ascender) + " desc=" + R(ci.descender)
                          + " base=" + R(ci.baseLine) + " scale=" + R(ci.scale) + " size=" + R(ci.pointSize) + " line=" + ci.lineNumber);
                    }
                    Destroy(go);
                    yield return null;
                }
            L("done");
            var done = new GameObject("SupTextProbeDone", typeof(RectTransform));
            done.transform.SetParent(canvas.transform, false);
        }
    }
}
