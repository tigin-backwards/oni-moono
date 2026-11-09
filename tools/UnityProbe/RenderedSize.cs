// Rendered sizes read right after a text change ("PROBE ... RS ..."), at the main menu, about
// ten seconds. Thermal Tooltips Reworked (3780815871) measures a string for its hover card by
// instantiating the hover drawer's LocText prefab, activating it, styling it with
// SetTextStyleSetting.ApplyStyle, setting its text, calling KForceUpdateDirty and reading
// GetRenderedValues() at once; HoverTextDrawer.DrawText reads renderedWidth the same way on a
// pooled widget whose text it just changed. Each case logs, in a fixed order after the value it
// reads first: GetRenderedValues() and (false), renderedWidth/Height, preferredWidth,
// textInfo.characterCount, bounds, textBounds and havePropertiesChanged.
// - fresh: an instance of an inactive (or active) prefab, as the mod builds it; with and without
//   KForceUpdateDirty; each value read first in turn.
// - reuse: a text laid out with one string, then given another (with and without
//   KForceUpdateDirty), read at once and a frame later.
// - inactive: a fresh text left inactive, then KForceUpdateDirty.
// With PROBE_SCENARIO=renderedsize only this probe runs. A RsProbeDone object appears at the end,
// for a spec's ui_wait.
using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class RenderedSize : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void L(string s) => Rec.Log("RS " + s);
        static string R(float v) => v.ToString("R", Inv);
        static string V(Vector2 v) => R(v.x) + "x" + R(v.y);

        static readonly string[] Reads = { "rv", "rvall", "rw", "pw", "count", "bounds", "tbounds", "dirty" };

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        static string Read(LocText t, string what)
        {
            switch (what)
            {
                case "rv": return "rv=" + V(t.GetRenderedValues());
                case "rvall": return "rvall=" + V(t.GetRenderedValues(false));
                case "rw": return "rw=" + R(t.renderedWidth) + "x" + R(t.renderedHeight);
                case "pw": return "pw=" + R(t.preferredWidth);
                case "count": return "count=" + (t.textInfo == null ? "null" : t.textInfo.characterCount.ToString(Inv));
                case "bounds": return "bounds=" + R(t.bounds.min.x) + ".." + R(t.bounds.max.x) + "," + R(t.bounds.min.y) + ".." + R(t.bounds.max.y);
                case "tbounds": return "tbounds=" + R(t.textBounds.min.x) + ".." + R(t.textBounds.max.x) + "," + R(t.textBounds.min.y) + ".." + R(t.textBounds.max.y);
                case "dirty": return "dirty=" + t.havePropertiesChanged;
            }
            return "?";
        }

        static string ReadAll(LocText t, string first)
        {
            string s = Read(t, first);
            foreach (var r in Reads) if (r != first) s += " " + Read(t, r);
            return s;
        }

        static TextStyleSetting Style(TMP_FontAsset font)
        {
            var s = ScriptableObject.CreateInstance<TextStyleSetting>();
            s.sdfFont = font;
            s.fontSize = 14;
            s.textColor = Color.white;
            s.style = FontStyles.Normal;
            s.enableWordWrapping = false;
            return s;
        }

        static GameObject Prefab(Transform parent, TMP_FontAsset font, bool active)
        {
            var go = new GameObject("RsPrefab", typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<LocText>();
            t.font = font;
            t.fontSize = 14;
            t.text = "";
            go.SetActive(active);
            return go;
        }

        static LocText Fresh(GameObject prefab, Transform root, TextStyleSetting style, string text, bool force, bool activate = true)
        {
            var go = Instantiate(prefab, root, false);
            if (activate) go.SetActive(true);
            var t = go.GetComponent<LocText>();
            t.textStyleSetting = style;
            SetTextStyleSetting.ApplyStyle(t, t.textStyleSetting);
            t.text = text;
            if (force) t.KForceUpdateDirty();
            return t;
        }

        IEnumerator Run()
        {
            var cgo = new GameObject("RsCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            var root = new GameObject("RsRoot", typeof(RectTransform)).transform;
            root.SetParent(cgo.transform, false);
            yield return null;
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "NotoSans-Regular");
            if (font == null) { L("font missing"); yield break; }
            var style = Style(font);
            const string text = "°C: 0.000000000";
            foreach (bool prefabActive in new[] { false, true })
            {
                var prefab = Prefab(cgo.transform, font, prefabActive);
                yield return null;
                string p = prefabActive ? "active" : "inactive";
                foreach (bool force in new[] { true, false })
                {
                    foreach (var first in Reads)
                    {
                        LocText t = null;
                        try
                        {
                            t = Fresh(prefab, root, style, text, force);
                            L("fresh " + p + (force ? " force" : " noforce") + " first=" + first + " " + ReadAll(t, first));
                        }
                        catch (Exception e) { L("fresh " + p + " " + first + " threw " + e.GetType().Name + ": " + e.Message); }
                        if (t != null) Destroy(t.gameObject);
                    }
                }
                // After ForceMeshUpdate, for the laid-out values.
                {
                    var t = Fresh(prefab, root, style, text, true);
                    t.ForceMeshUpdate();
                    L("fresh " + p + " fmu " + ReadAll(t, "rv"));
                    Destroy(t.gameObject);
                }
                // The text read a frame later.
                {
                    var t = Fresh(prefab, root, style, text, true);
                    yield return null;
                    L("fresh " + p + " frame " + ReadAll(t, "rv"));
                    Destroy(t.gameObject);
                }
                // A text left inactive.
                {
                    var t = Fresh(prefab, root, style, text, true, activate: false);
                    L("fresh " + p + " unactivated " + ReadAll(t, "rv"));
                    Destroy(t.gameObject);
                }
                // A pooled widget given a new string, as HoverTextDrawer.DrawText does.
                foreach (bool force in new[] { true, false })
                {
                    foreach (var first in new[] { "rv", "pw", "count" })
                    {
                        var t = Fresh(prefab, root, style, "ABC", true);
                        yield return null;
                        L("reuse " + p + " before " + ReadAll(t, "rv"));
                        t.text = text;
                        if (force) t.KForceUpdateDirty();
                        L("reuse " + p + (force ? " force" : " noforce") + " first=" + first + " " + ReadAll(t, first));
                        yield return null;
                        L("reuse " + p + (force ? " force" : " noforce") + " frame " + ReadAll(t, "rv"));
                        Destroy(t.gameObject);
                    }
                }
                Destroy(prefab);
                yield return null;
            }
            Destroy(cgo);
            L("=== done");
            var done = new GameObject("RsProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
            done.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        }
    }
}
