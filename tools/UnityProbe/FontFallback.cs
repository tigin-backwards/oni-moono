// TMP's fallback chain ("PROBE ... FF ..."), at the main menu, 10 s after it loads:
//
// - FF settings: TMP_Settings' default font asset and global fallback list; FF font: each loaded
//   TMP_FontAsset's population mode and own fallback table.
// - FF has: HasCharacter(c), HasCharacter(c, true) and HasCharacter(c, true, true) for each
//   loaded font and a set of characters (no-break spaces, CJK, Cyrillic, symbols, other scripts,
//   a private-use character and an emoji).
// - FF chain: HasCharacter(c, true[, true]) of empty static assets made at run time from the
//   LiberationSans face, with the global list and the default font asset each emptied or set,
//   and with fallbacks of fallbacks: which of them the search walks, and where tryAddCharacter
//   adds a character.
// - FF draw: a TextMeshProUGUI per loaded font and string group, each character's unicode, the
//   element's unicode and glyph index, the font asset it came from, visibility, origin and
//   advance, and the preferred width; the texts stay on screen for a shot (an empty static asset
//   whose own fallback is NotoSans-Regular among them, for the order of the chain).
// - FF game: every active TMP text of the menu's own UI, with the characters per font asset its
//   characters came from and the number shown as the missing glyph.
// - FF log: what the log received meanwhile (TMP's missing-character warnings).
//
// A `FontFallbackProbeDone` object marks the end. With PROBE_SCENARIO=fontfallback only this
// probe runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace SemanticsProbe
{
    public class FontFallback : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly List<string> logged = new List<string>();

        static void L(string s) => Rec.Log("FF " + s);
        static string R(float v) => v.ToString("R", Inv);
        static string N(UnityEngine.Object o) => o == null ? "null" : o.name;
        static string Names(IEnumerable<TMP_FontAsset> list) => list == null ? "null" : "[" + string.Join(",", list.Select(N)) + "]";

        static string Try(Func<string> f)
        {
            try { return f(); }
            catch (Exception e) { return "EXCEPTION " + e.GetType().Name; }
        }

        static readonly char[] Chars =
        {
            'a', ' ', '\u00A0', '\u202F', '\u2009', '\u4E00', '\u6F22', '\u3042', '\uD55C', '\u0416', '\u044F', '\u2605', '\u2022',
            '\u20AC', '\u2122', '\u00B0', '\u2192', '\u2026', '\u25A1', '\u0E01', '\u05D0', '\u0645', '\uE000',
        };

        static readonly string[] Groups =
        {
            "a b c d e",
            "Latin 一二 漢字 かな 한국",
            "Привет Жя",
            "★•€™°→…□±×",
            "กข שלום مرحبا \U0001F600",
        };

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (!message.StartsWith("PROBE")) logged.Add(type + ": " + message.Replace("\n", "\\n"));
        }

        static void Flush(string label)
        {
            foreach (var m in logged.GroupBy(m => m).OrderBy(g => g.Key, StringComparer.Ordinal))
                L("log " + label + " " + m.Count() + "x " + m.Key);
            logged.Clear();
        }

        static TMP_FontAsset[] Loaded() => Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
            .GroupBy(f => f.name).Select(g => g.First()).OrderBy(f => f.name, StringComparer.Ordinal).ToArray();

        IEnumerator Run()
        {
            Application.logMessageReceived += OnLog;
            // The game's own texts first, before this probe adds any.
            GameTexts();
            Flush("game");
            Settings();
            var fonts = Loaded();
            foreach (var f in fonts) L("font " + f.name + " mode=" + f.atlasPopulationMode + " fallbacks=" + Names(f.fallbackFontAssetTable));
            foreach (var f in fonts)
                foreach (var c in Chars)
                    L("has " + f.name + " U+" + ((int)c).ToString("X4") + " " + Try(() => (f.HasCharacter((int)c) ? "own" : "-")
                        + "/" + (f.HasCharacter(c, true) ? "fb" : "-")));
            Flush("has");
            Chain();
            Flush("chain");
            yield return Draw(fonts);
            Flush("draw");
            // tryAddCharacter last: it changes the dynamic assets.
            foreach (var f in fonts)
                foreach (var c in Chars)
                    L("hasadd " + f.name + " U+" + ((int)c).ToString("X4") + " " + Try(() => (f.HasCharacter(c, true, true) ? "fb" : "-")));
            Flush("hasadd");
            Settings();
            L("=== done");
            var done = new GameObject("FontFallbackProbeDone", typeof(RectTransform));
            done.transform.SetParent(canvasObject.transform, false);
        }

        static void Settings()
        {
            L("settings instance=" + (TMP_Settings.instance != null) + " default=" + N(TMP_Settings.defaultFontAsset)
                + " globals=" + Names(TMP_Settings.fallbackFontAssets) + " missingGlyph=" + TMP_Settings.missingGlyphCharacter);
            var d = TMP_Settings.defaultFontAsset;
            if (d != null) L("settings default fallbacks=" + Names(d.fallbackFontAssetTable));
            if (TMP_Settings.fallbackFontAssets != null)
                foreach (var g in TMP_Settings.fallbackFontAssets)
                    if (g != null) L("settings global " + g.name + " mode=" + g.atlasPopulationMode + " fallbacks=" + Names(g.fallbackFontAssetTable));
        }

        static Font face;

        static TMP_FontAsset Empty(string name, AtlasPopulationMode mode)
        {
            if (face == null)
                face = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == "LiberationSans")
                    ?? Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault();
            var a = TMP_FontAsset.CreateFontAsset(face, 48, 5, GlyphRenderMode.SDFAA, 256, 256, AtlasPopulationMode.Dynamic, true);
            a.name = name;
            a.atlasPopulationMode = mode;
            return a;
        }

        // Runs with TMP_Settings' global fallbacks and default font asset as given, restoring them.
        static void With(List<TMP_FontAsset> globals, TMP_FontAsset def, System.Action body)
        {
            var oldGlobals = TMP_Settings.fallbackFontAssets;
            var oldDefault = TMP_Settings.defaultFontAsset;
            TMP_Settings.fallbackFontAssets = globals;
            TMP_Settings.defaultFontAsset = def;
            try { body(); }
            catch (Exception e) { L("chain EXCEPTION " + e.GetType().Name + " " + e.Message); }
            finally
            {
                TMP_Settings.fallbackFontAssets = oldGlobals;
                TMP_Settings.defaultFontAsset = oldDefault;
            }
        }

        static void Has(string label, TMP_FontAsset f, char c, bool search, bool add)
            => L("chain " + label + " U+" + ((int)c).ToString("X4") + " search=" + search + " add=" + add + " " + Try(() => f.HasCharacter(c, search, add).ToString()));

        static void Chain()
        {
            var noto = Loaded().FirstOrDefault(f => f.name == "NotoSans-Regular");
            var cjk = TMP_Settings.fallbackFontAssets?.FirstOrDefault(f => f != null && f.HasCharacter(0x4E00));
            L("chain noto=" + N(noto) + " cjk=" + N(cjk) + " face=" + N(face));
            if (noto == null) return;
            var x = Empty("ProbeEmpty", AtlasPopulationMode.Static);
            var m = Empty("ProbeMiddle", AtlasPopulationMode.Static);
            L("chain empty face=" + N(face) + " chars=" + x.characterTable.Count);
            // As the game has them.
            Has("as-is", x, 'a', false, false);
            Has("as-is", x, 'a', true, false);
            Has("as-is", x, '\u4E00', true, false);
            Has("as-is", x, '\u00A0', true, false);
            Has("as-is", x, '\u202F', true, false);
            Has("as-is-int", x, 'a', false, false);
            L("chain as-is-int-overload " + Try(() => x.HasCharacter(0x61).ToString()));
            // Globals emptied, default kept.
            With(new List<TMP_FontAsset>(), TMP_Settings.defaultFontAsset, () =>
            {
                Has("no-globals", x, 'a', true, false);
                Has("no-globals", x, '\u4E00', true, false);
                Has("no-globals", x, '\u202F', true, false);
            });
            // Default null, globals kept.
            With(TMP_Settings.fallbackFontAssets, null, () =>
            {
                Has("no-default", x, 'a', true, false);
                Has("no-default", x, '\u4E00', true, false);
                Has("no-default", x, '\u00A0', true, false);
            });
            // Neither.
            With(new List<TMP_FontAsset>(), null, () =>
            {
                Has("none", x, 'a', true, false);
                Has("none", x, '\u4E00', true, false);
            });
            With(null, null, () => Has("null-globals", x, 'a', true, false));
            // Default only: Noto.
            With(new List<TMP_FontAsset>(), noto, () => Has("default-noto", x, 'a', true, false));
            // Globals only: Noto, after a null entry.
            With(new List<TMP_FontAsset> { null, noto }, null, () => Has("globals-null-noto", x, 'a', true, false));
            // Own fallback's fallback.
            With(new List<TMP_FontAsset>(), null, () =>
            {
                m.fallbackFontAssetTable = new List<TMP_FontAsset> { noto };
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { m };
                Has("own-nested", x, 'a', true, false);
                Has("own-nested", x, 'a', false, false);
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { noto };
                Has("own-direct", x, 'a', true, false);
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                Has("own-null-noto", x, 'a', true, false);
                x.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            // A global's own fallbacks.
            With(new List<TMP_FontAsset> { m }, null, () => Has("global-nested", x, 'a', true, false));
            // The default's own fallbacks.
            With(new List<TMP_FontAsset>(), m, () => Has("default-nested", x, 'a', true, false));
            m.fallbackFontAssetTable = new List<TMP_FontAsset>();
            // tryAddCharacter: a dynamic global, then a dynamic default, then a dynamic own fallback.
            var dg = Empty("ProbeDynGlobal", AtlasPopulationMode.Dynamic);
            var dd = Empty("ProbeDynDefault", AtlasPopulationMode.Dynamic);
            var df = Empty("ProbeDynOwn", AtlasPopulationMode.Dynamic);
            With(new List<TMP_FontAsset> { dg }, dd, () =>
            {
                Has("dyn", x, 'q', true, false);
                L("chain dyn after-noadd global=" + dg.characterTable.Count + " default=" + dd.characterTable.Count);
                Has("dyn", x, 'q', true, true);
                L("chain dyn after-add global=" + dg.characterTable.Count + " default=" + dd.characterTable.Count);
                Has("dyn", x, '\u4E00', true, true);
                L("chain dyn after-cjk global=" + dg.characterTable.Count + " default=" + dd.characterTable.Count);
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { df };
                Has("dyn-own", x, 'r', true, true);
                L("chain dyn-own after-add own=" + df.characterTable.Count + " global=" + dg.characterTable.Count + " default=" + dd.characterTable.Count);
                x.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            With(new List<TMP_FontAsset>(), dd, () =>
            {
                Has("dyn-default", x, 's', true, true);
                L("chain dyn-default after-add default=" + dd.characterTable.Count);
            });
            // Null entries: where the search goes on after one.
            With(new List<TMP_FontAsset>(), null, () =>
            {
                m.fallbackFontAssetTable = new List<TMP_FontAsset> { null };
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { m, noto };
                Has("own-nested-null-then-noto", x, 'a', true, false);
                m.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { m };
                Has("own-nested-null-noto", x, 'a', true, false);
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                L("chain own-null-noto HasCharacters " + Try(() => x.HasCharacters("a", out uint[] missing, true, false) + " " + (missing == null ? "null" : missing.Length.ToString())));
                m.fallbackFontAssetTable = new List<TMP_FontAsset>();
                x.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            With(new List<TMP_FontAsset> { noto }, null, () =>
            {
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { null };
                Has("own-null-global-noto", x, 'a', true, false);
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { null, m };
                Has("own-null-m-global-noto", x, 'a', true, false);
                x.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            With(new List<TMP_FontAsset> { null }, noto, () => Has("globals-null-default-noto", x, 'a', true, false));
            With(new List<TMP_FontAsset> { null, noto }, null, () =>
                L("chain globals-null-noto HasCharacters " + Try(() => x.HasCharacters("a", out uint[] missing, true, false) + " " + (missing == null ? "null" : missing.Length.ToString()))));
            With(new List<TMP_FontAsset> { m, noto }, null, () =>
            {
                m.fallbackFontAssetTable = new List<TMP_FontAsset> { null };
                Has("global-nested-null-then-noto", x, 'a', true, false);
                m.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            With(new List<TMP_FontAsset>(), m, () =>
            {
                m.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                Has("default-null-noto", x, 'a', true, false);
                m.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            // Whether a dynamic asset adds before a later asset's table is looked in.
            var dg2 = Empty("ProbeDynGlobal2", AtlasPopulationMode.Dynamic);
            With(new List<TMP_FontAsset> { dg2, noto }, null, () =>
            {
                Has("dyn-then-static", x, 'u', true, true);
                L("chain dyn-then-static after-add global=" + dg2.characterTable.Count);
            });
            var df2 = Empty("ProbeDynOwn2", AtlasPopulationMode.Dynamic);
            With(new List<TMP_FontAsset>(), null, () =>
            {
                x.fallbackFontAssetTable = new List<TMP_FontAsset> { df2, noto };
                Has("own-dyn-then-static", x, 'w', true, true);
                L("chain own-dyn-then-static after-add own=" + df2.characterTable.Count);
                x.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            var dx = Empty("ProbeDynSelf", AtlasPopulationMode.Dynamic);
            With(new List<TMP_FontAsset>(), null, () =>
            {
                Has("self-dyn-search", dx, 'x', true, false);
                L("chain self-dyn-search after own=" + dx.characterTable.Count);
                Has("self-dyn-nosearch-add", dx, 'y', false, true);
                L("chain self-dyn-nosearch-add after own=" + dx.characterTable.Count);
            });
            // The asset itself in the globals.
            With(new List<TMP_FontAsset> { x, noto }, null, () => Has("self-global", x, 'a', true, false));
        }

        static GameObject canvasObject;

        IEnumerator Draw(TMP_FontAsset[] fonts)
        {
            var cgo = canvasObject = new GameObject("FfCanvas", typeof(RectTransform));
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
            var drawn = fonts.Where(f => f.material != null).ToList();
            var noto = fonts.FirstOrDefault(f => f.name == "NotoSans-Regular");
            if (noto != null)
            {
                var host = Empty("ProbeHost", AtlasPopulationMode.Static);
                host.fallbackFontAssetTable = new List<TMP_FontAsset> { noto };
                drawn.Add(host);
                // A null entry first: whether drawing skips it or ends the list there.
                var hostNull = Empty("ProbeHostNull", AtlasPopulationMode.Static);
                hostNull.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                drawn.Add(hostNull);
            }
            var texts = new List<(string, TextMeshProUGUI)>();
            float rowHeight = Mathf.Min(30f, 700f / Mathf.Max(1, drawn.Count));
            for (int i = 0; i < drawn.Count; i++)
                for (int g = 0; g < Groups.Length; g++)
                {
                    var go = new GameObject("Ff" + i + "_" + g, typeof(RectTransform));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(cgo.transform, false);
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                    rt.anchoredPosition = new Vector2(10f + 254f * g, -10f - rowHeight * i);
                    rt.sizeDelta = new Vector2(250f, rowHeight);
                    var tmp = go.AddComponent<TextMeshProUGUI>();
                    tmp.font = drawn[i];
                    tmp.fontSize = Mathf.Min(14f, rowHeight * 0.6f);
                    tmp.color = Color.white;
                    tmp.alignment = TextAlignmentOptions.TopLeft;
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.text = Groups[g];
                    tmp.ForceMeshUpdate();
                    texts.Add((drawn[i].name + " g" + g, tmp));
                }
            yield return null;
            yield return null;
            foreach (var (label, tmp) in texts) Log(label, tmp);
        }

        static string Elem(TMP_CharacterInfo ch)
        {
            var e = ch.textElement;
            return ((int)ch.character).ToString("X") + ":" + (e == null ? "null" : e.unicode.ToString("X") + "/g" + e.glyphIndex)
                + "@" + N(ch.fontAsset) + (ch.isVisible ? "" : ",inv") + "," + R(ch.origin) + "+" + R(ch.xAdvance);
        }

        static void Log(string label, TMP_Text tmp)
        {
            var info = tmp.textInfo;
            var sb = new StringBuilder();
            for (int c = 0; c < info.characterCount; c++)
            {
                if (c > 0) sb.Append(';');
                sb.Append(Elem(info.characterInfo[c]));
            }
            L("draw " + label + " pw=" + R(tmp.preferredWidth) + " chars=" + sb);
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static void GameTexts()
        {
            var lines = new List<string>();
            var total = new Dictionary<string, int>();
            foreach (var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (t == null || !t.isActiveAndEnabled || t.font == null || string.IsNullOrEmpty(t.text)) continue;
                var info = t.textInfo;
                if (info == null) continue;
                var per = new SortedDictionary<string, int>(StringComparer.Ordinal);
                int missing = 0;
                var missingChars = new SortedSet<int>();
                for (int c = 0; c < info.characterCount; c++)
                {
                    var ch = info.characterInfo[c];
                    if (ch.elementType != TMP_TextElementType.Character) continue;
                    var e = ch.textElement;
                    if (e != null && e.unicode != ch.character && ch.character != 0x20 && ch.character != 0xA0)
                    {
                        missing++;
                        missingChars.Add(ch.character);
                    }
                    var key = N(ch.fontAsset);
                    per[key] = per.TryGetValue(key, out var n) ? n + 1 : 1;
                    var pair = t.font.name + "->" + key;
                    total[pair] = total.TryGetValue(pair, out var m) ? m + 1 : 1;
                }
                lines.Add(PathOf(t.transform) + " font=" + t.font.name + " chars=" + info.characterCount + " "
                    + string.Join(",", per.Select(p => p.Key + ":" + p.Value)) + " missing=" + missing
                    + (missingChars.Count > 0 ? " [" + string.Join(",", missingChars.Select(x => x.ToString("X"))) + "]" : ""));
            }
            foreach (var l in lines.OrderBy(s => s, StringComparer.Ordinal)) L("game " + l);
            foreach (var p in total.OrderBy(p => p.Key, StringComparer.Ordinal)) L("game total " + p.Key + " " + p.Value);
        }
    }
}
