// Preferred sizes of wrapped, auto-sized texts ("PROBE ... AP ..."), at the main menu. Found in
// U60's All Diagnostics screen (u60-qa-spec.md U2): its value labels (LocText, NotoSans, auto-size
// 8 to 14, wrapping, 72 wide, their height set by the row's layout group from their preferred
// height) showed at 14 pt in the original and 8 pt in ours. The texts are laid out in rects 72
// wide and 0, 5, 10.88, 19.04 and 100 high, then each case reads the font size auto-size chose
// and the preferred width and height (and GetPreferredValues(72, 0) and with the rect's height).
// The same in a HorizontalLayoutGroup row that controls its children's height (min 24), as the
// screen's rows are, after a layout rebuild. Set up as SetTextStyleSetting does (kerning, extra
// padding). With PROBE_SCENARIO=autosizepref only this probe runs.
#pragma warning disable CS0618 // enableKerning, as SetTextStyleSetting.ApplyStyle sets it
using System.Collections;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class AutoSizePreferred : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void L(string s) => Rec.Log("AP " + s);
        static string R(float v) => v.ToString("R", Inv);

        static readonly string[] Texts = { "0/0", "0.0 J", "Avg. 0 rads", "PowerUseDiagnosticKey", "one two three four five six" };
        static readonly float[] Heights = { 0f, 5f, 10.88f, 19.04f, 100f };

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static string State(TextMeshProUGUI t)
        {
            var rt = (RectTransform)t.transform;
            var v = t.GetPreferredValues(72f, 0f);
            var w = t.GetPreferredValues(72f, rt.rect.height);
            return "fs=" + R(t.fontSize) + " rect=" + R(rt.rect.width) + "x" + R(rt.rect.height)
                   + " pw=" + R(t.preferredWidth) + " ph=" + R(t.preferredHeight)
                   + " gpv72x0=" + R(v.x) + "," + R(v.y) + " gpv72xh=" + R(w.x) + "," + R(w.y)
                   + " lc=" + (t.textInfo == null ? -1 : t.textInfo.lineCount);
        }

        IEnumerator Run()
        {
            var cgo = new GameObject("ApCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            yield return null;
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "NotoSans-Regular");
            if (font == null) { L("font missing"); yield break; }

            // A free rect of each height.
            foreach (var text in Texts)
                foreach (float h in Heights)
                {
                    var t = Make(canvas.transform, font);
                    ((RectTransform)t.transform).sizeDelta = new Vector2(72f, h);
                    t.text = text;
                    t.ForceMeshUpdate();
                    L("free \"" + text + "\" h=" + R(h) + " " + State(t));
                    Destroy(t.gameObject);
                }
            yield return null;

            // In a row: HorizontalLayoutGroup controlling width and height, the label's
            // LayoutElement 72 wide; the row's LayoutElement min height 24.
            foreach (var text in Texts)
            {
                var row = new GameObject("ApRow", typeof(RectTransform));
                var rrt = (RectTransform)row.transform;
                rrt.SetParent(canvas.transform, false);
                rrt.sizeDelta = new Vector2(520f, 24f);
                var hlg = row.AddComponent<HorizontalLayoutGroup>();
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = false;
                var fitter = row.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var rle = row.AddComponent<LayoutElement>();
                rle.minHeight = 24f;
                var t = Make(rrt, font);
                var le = t.gameObject.AddComponent<LayoutElement>();
                le.minWidth = 72f;
                le.preferredWidth = 72f;
                t.text = text;
                LayoutRebuilder.ForceRebuildLayoutImmediate(rrt);
                t.ForceMeshUpdate();
                L("row0 \"" + text + "\" " + State(t) + " row=" + R(rrt.rect.height));
                yield return null;
                yield return null;
                L("row2 \"" + text + "\" " + State(t) + " row=" + R(rrt.rect.height));
                Destroy(row);
            }
            L("done");
            var done = new GameObject("AutoSizePreferredProbeDone", typeof(RectTransform));
            done.transform.SetParent(canvas.transform, false);
        }

        static TextMeshProUGUI Make(Transform parent, TMP_FontAsset font)
        {
            var go = new GameObject("Ap", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.enableKerning = true;
            t.extraPadding = true;
            t.font = font;
            t.fontSize = 14f;
            t.alignment = TextAlignmentOptions.TopRight;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.fontSizeMin = 8f;
            t.fontSizeMax = 14f;
            t.enableAutoSizing = true;
            return t;
        }
    }
}
