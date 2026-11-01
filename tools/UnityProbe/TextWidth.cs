// Text widths at full precision ("PROBE ... TW ..."), at the main menu: TMP's preferred width of
// short labels in the game's fonts, set up as SetTextStyleSetting does it (kerning, extra
// padding), alone, under a HorizontalLayoutGroup beside a 24-wide icon and copied into a
// LayoutElement, each value in "R" format beside the two-decimal form E2eDriver's ui_dump
// prints. Then that two-decimal formatting itself on chosen floats, so the BCL's part shows on
// its own. With PROBE_SCENARIO=textwidth only this probe runs.
#pragma warning disable CS0618 // enableKerning, as SetTextStyleSetting.ApplyStyle sets it
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class TextWidth : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void L(string s) => Rec.Log("TW " + s);
        static string R(float v) => v.ToString("R", Inv);
        static string R(double v) => v.ToString("R", Inv);
        static string Bits(float v) => BitConverter.ToInt32(BitConverter.GetBytes(v), 0).ToString("X8");
        /// E2eDriver's ui_dump form (tools/E2eDriver/Ui.cs F).
        static string Dump(float v) { var s = Math.Round(v, 2).ToString("0.##", Inv); return s == "-0" ? "0" : s; }
        static string All(float v) => R(v) + " bits=" + Bits(v) + " dump=" + Dump(v);

        static readonly string[] Texts =
        {
            "Ethanol Gas", "Super Coolant Gas", "Nuclear Fallout", "Mercury Gas", "Oxygen", "Hydrogen",
            "Polluted Oxygen", "Carbon Dioxide", "Chlorine", "Natural Gas", "Steam", "Search", "Room",
            "A", "i", "W", " ", "  ", "", "Light Collector",
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
            L("=== formatting");
            Formatting();
            var cgo = new GameObject("TwCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            yield return null;
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderBy(f => f.name, StringComparer.Ordinal).ToArray();
            L("fonts " + string.Join(",", fonts.Select(f => f.name)));
            var styles = Resources.FindObjectsOfTypeAll<TextStyleSetting>().OrderBy(s => s.name, StringComparer.Ordinal).ToArray();
            foreach (var s in styles)
                L("style " + s.name + " font=" + (s.sdfFont == null ? "null" : s.sdfFont.name) + " size=" + R(s.fontSize) + " style=" + s.style + " wrap=" + s.enableWordWrapping);
            foreach (var font in fonts.Where(f => f.name.StartsWith("NotoSans") || f.name.StartsWith("GRAYSTROKE")))
            {
                var fi = font.faceInfo;
                L("face " + font.name + " pointSize=" + R(fi.pointSize) + " scale=" + R(fi.scale) + " lineHeight=" + R(fi.lineHeight) + " ascent=" + R(fi.ascentLine)
                  + " descent=" + R(fi.descentLine) + " padding=" + R(font.atlasPadding) + " atlas=" + font.atlasWidth + "x" + font.atlasHeight + " normalStyle=" + R(font.normalStyle)
                  + " normalSpacing=" + R(font.normalSpacingOffset) + " boldStyle=" + R(font.boldStyle) + " boldSpacing=" + R(font.boldSpacing));
                foreach (float size in new[] { 12f, 14f, 16f })
                    foreach (var fs in new[] { FontStyles.Normal, FontStyles.Bold })
                        yield return Measure(canvas.transform, font, size, fs);
            }
            Destroy(cgo);
            L("=== done");
        }

        IEnumerator Measure(Transform canvas, TMP_FontAsset font, float size, FontStyles style)
        {
            string tag = font.name + " " + R(size) + " " + style;
            var c = new Vector2(0.5f, 0.5f);
            foreach (var text in Texts)
            {
                string label = tag + " \"" + text + "\"";
                // 1. Alone, in a fixed 400-wide rect: centred (its left edge at -200), at pivot 0
                //    (the edge at 0) and at pivot 0.37 (-148), labelled "@0" and "@0.37".
                foreach (float px in new[] { 0.5f, 0f, 0.37f })
                {
                    label = tag + (px == 0.5f ? "" : "@" + R(px)) + " \"" + text + "\"";
                    var go = new GameObject("Tw", typeof(RectTransform));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(canvas, false);
                    rt.anchorMin = rt.anchorMax = c; rt.pivot = new Vector2(px, 0.5f); rt.sizeDelta = new Vector2(400, 40);
                    var tmp = go.AddComponent<TextMeshProUGUI>();
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.enableKerning = true;
                    tmp.extraPadding = true;
                    tmp.fontSize = size;
                    tmp.font = font;
                    tmp.fontStyle = style;
                    tmp.text = text;
                    L(label + " alone preferredWidth=" + All(tmp.preferredWidth) + " preferredHeight=" + All(tmp.preferredHeight));
                    var pv = tmp.GetPreferredValues(text);
                    L(label + " alone GetPreferredValues=" + All(pv.x) + "," + All(pv.y));
                    tmp.ForceMeshUpdate();
                    var info = tmp.textInfo;
                    var sb = new StringBuilder();
                    for (int i = 0; i < info.characterCount; i++)
                    {
                        var ch = info.characterInfo[i];
                        sb.Append(' ').Append((int)ch.character).Append(':').Append(R(ch.origin)).Append('/').Append(R(ch.xAdvance)).Append('/').Append(R(ch.scale));
                    }
                    L(label + " chars" + sb);
                    if (info.lineCount > 0)
                    {
                        var line = info.lineInfo[0];
                        L(label + " line0 width=" + R(line.width) + " length=" + R(line.length) + " maxAdvance=" + R(line.maxAdvance) + " lineExtents=" + R(line.lineExtents.min.x) + ".." + R(line.lineExtents.max.x));
                    }
                    var bounds = tmp.textBounds;
                    L(label + " textBounds=" + R(bounds.min.x) + ".." + R(bounds.max.x) + " rendered=" + All(tmp.renderedWidth));
                    Destroy(go);
                }
                label = tag + " \"" + text + "\"";

                // 2. Under a HorizontalLayoutGroup beside a 24-wide icon (the overlay legend's
                //    row: 44 of padding, icon and spacing), and in a LayoutElement.
                var row = new GameObject("TwRow", typeof(RectTransform));
                var rrt = (RectTransform)row.transform;
                rrt.SetParent(canvas, false);
                rrt.anchorMin = rrt.anchorMax = rrt.pivot = c; rrt.sizeDelta = new Vector2(222, 24);
                var hlg = row.AddComponent<HorizontalLayoutGroup>();
                hlg.padding = new RectOffset(4, 4, 0, 0);
                hlg.spacing = 12;
                hlg.childControlWidth = true; hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
                var icon = new GameObject("Icon", typeof(RectTransform));
                icon.transform.SetParent(rrt, false);
                var le = icon.AddComponent<LayoutElement>();
                le.minWidth = le.preferredWidth = 24; le.minHeight = le.preferredHeight = 24;
                var tgo = new GameObject("Text", typeof(RectTransform));
                tgo.transform.SetParent(rrt, false);
                var t2 = tgo.AddComponent<TextMeshProUGUI>();
                t2.textWrappingMode = TextWrappingModes.NoWrap;
                t2.enableKerning = true;
                t2.extraPadding = true;
                t2.fontSize = size;
                t2.font = font;
                t2.fontStyle = style;
                t2.text = text;
                LayoutRebuilder.ForceRebuildLayoutImmediate(rrt);
                L(label + " row hlg.preferredWidth=" + All(hlg.preferredWidth) + " text.preferredWidth=" + All(t2.preferredWidth)
                  + " LayoutUtility=" + All(LayoutUtility.GetPreferredWidth((RectTransform)tgo.transform)) + " textRect=" + All(((RectTransform)tgo.transform).rect.width)
                  + " textRectH=" + All(((RectTransform)tgo.transform).rect.height));
                var copy = new GameObject("TwLe", typeof(RectTransform));
                copy.transform.SetParent(canvas, false);
                var le2 = copy.AddComponent<LayoutElement>();
                le2.minWidth = le2.preferredWidth = t2.preferredWidth;
                L(label + " le minWidth=" + All(le2.minWidth) + " preferredWidth=" + All(le2.preferredWidth));
                Destroy(row);
                Destroy(copy);
            }
            yield return null;
        }

        /// Math.Round(float, 2) then "0.##" (E2eDriver's dump form), "F2", and the float's "R",
        /// on values near the third-decimal midpoint.
        static void Formatting()
        {
            float[] chosen = { 67.255f, 104.255f, 111.255f, 148.255f, 13.755f, 100.005f, 0.005f, 19.035f, 1.005f, 2.675f, 0.125f, 0.375f, 12.345f };
            foreach (var v in chosen)
                L("fmt " + All(v) + " F2=" + v.ToString("F2", Inv) + " G=" + v.ToString(Inv) + " double=" + R((double)v)
                  + " round=" + R(Math.Round(v, 2)) + " roundD=" + R(Math.Round((double)v, 2)) + " roundAway=" + R(Math.Round(v, 2, MidpointRounding.AwayFromZero))
                  + " mathf=" + R(Mathf.Round(v * 100f) / 100f) + " x100=" + R(v * 100f) + " x100d=" + R((double)v * 100.0));
            // A sweep: every float whose third decimal is a 5, from 0.005 to 300, in steps of 0.01;
            // a count of those rounded up and a hash of every dump form, then those below 20 rounded down.
            int n = 0, up = 0;
            var h = 17L;
            var downs = new StringBuilder();
            for (int k = 0; k < 30000; k++)
            {
                float v = (float)((k * 10 + 5) / 1000.0);
                string d = Dump(v);
                double lo = Math.Floor(v * 100.0) / 100.0;
                bool rounded = Math.Abs(double.Parse(d, Inv) - lo) > 1e-9;
                n++; if (rounded) up++;
                foreach (char ch in d) h = unchecked(h * 31 + ch);
                if (k < 2000 && !rounded) downs.Append(' ').Append(d);
            }
            L("fmt sweep n=" + n + " up=" + up + " hash=" + h);
            L("fmt sweep down<20" + downs);
        }
    }
}
