// Package members that Workshop mods reach and this implementation lacks or calls elsewhere
// ("PROBE ... TG ..."), at the main menu. With PROBE_SCENARIO=tmpgaps only this probe runs;
// PROBE_TG_PART=meta, assign, mesh, dropdown, chars, hash, renderer, sprite, font or resources
// runs one part.
//
// meta: the members' metadata by reflection (visibility, static, virtual, signature, field type).
// assign: who calls TMP_InputField.AssignPositioningIfNeeded and when. A Harmony prefix counts
//   the calls per field and records the calling methods' names (System.Diagnostics.StackTrace),
//   through enable, frames, focus, typing, text set from code, the text's rect moved, the field
//   resized, layout rebuilt, components reassigned, reparenting, deactivation and re-enabling,
//   on a TMP_InputField and on Klei's KInputTextField. After each step it logs whether the
//   caret's RectTransform matches the text's.
// mesh: the protected TMP_InputField.m_Mesh (read by reflection) through the same life: null or
//   not, its instance, vertex count, bounds and first vertex colour; beside the caret's state.
// dropdown: the private TMP_Dropdown.m_Dropdown through Show, Hide (frame by frame until it is
//   gone), choosing an item, a second Show, disabling and destroying the dropdown.
// chars: TMP_FontAsset.HasCharacters(string, out uint[], bool, bool) on a static and a dynamic
//   font asset, with and without fallbacks and adding.
// hash: TMP_TextUtilities.GetHashCode(string) and the other Hash methods for sample strings.
// renderer: TextMeshProUGUI.canvasRenderer before Awake, after, and beside Graphic's.
// sprite: MaterialReferenceManager.AddSpriteAsset and TMP_Text.OnSpriteAssetRequest: which
//   sprite tags find which asset, when the event is raised and with what.
// font: MaterialReferenceManager.AddFontAsset: which font tags find which asset.
// resources: the game's own sprite and font tags, which name assets under Resources paths.
#pragma warning disable CS0618 // enableKerning, as SetTextStyleSetting.ApplyStyle sets it
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using UES = UnityEngine.EventSystems.EventSystem;

namespace SemanticsProbe
{
    public class TmpGaps : MonoBehaviour
    {
        bool started;
        float sceneStart = -1f;
        static TMP_FontAsset font;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static void L(string s) => Rec.Log("TG " + s);
        static string F(float v) { var s = Math.Round(v, 2).ToString("0.##", Inv); return s == "-0" ? "0" : s; }
        static string F(Vector2 v) => F(v.x) + "," + F(v.y);
        static string F(Vector3 v) => F(v.x) + "," + F(v.y) + "," + F(v.z);

        static string Esc(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s) sb.Append(ch >= 0x20 && ch < 0x7F && ch != '"' && ch != '\\' ? ch.ToString() : "\\u" + ((int)ch).ToString("X4"));
            return sb.Append('"').ToString();
        }

        // ----- AssignPositioningIfNeeded hook -----

        static readonly Dictionary<int, int> assignCount = new Dictionary<int, int>();
        static readonly List<string> assignCalls = new List<string>();
        static FieldInfo caretField, meshField, dropdownField;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(TMP_InputField), "AssignPositioningIfNeeded");
            caretField = HarmonyLib.AccessTools.Field(typeof(TMP_InputField), "caretRectTrans");
            meshField = HarmonyLib.AccessTools.Field(typeof(TMP_InputField), "m_Mesh");
            dropdownField = HarmonyLib.AccessTools.Field(typeof(TMP_Dropdown), "m_Dropdown");
            L("hook AssignPositioningIfNeeded " + (m == null ? "absent" : "found") + " caretRectTrans " + (caretField == null ? "absent" : "found")
              + " m_Mesh " + (meshField == null ? "absent" : "found") + " m_Dropdown " + (dropdownField == null ? "absent" : "found"));
            if (m != null) harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(TmpGaps), nameof(AssignPrefix)), postfix: new HarmonyLib.HarmonyMethod(typeof(TmpGaps), nameof(AssignPostfix)));
        }

        static void AssignPrefix(TMP_InputField __instance)
        {
            if (!__instance.name.StartsWith("tg")) return;
            int id = __instance.GetInstanceID();
            assignCount.TryGetValue(id, out int n);
            assignCount[id] = n + 1;
            assignCalls.Add(__instance.name + "<" + Callers() + ">" + Phase() + " entry:" + CaretState(__instance));
        }

        static void AssignPostfix(TMP_InputField __instance)
        {
            if (!__instance.name.StartsWith("tg")) return;
            assignCalls.Add("return:" + CaretState(__instance));
        }

        static string Phase()
            => (CanvasUpdateRegistry.IsRebuildingLayout() ? "[layout]" : "") + (CanvasUpdateRegistry.IsRebuildingGraphics() ? "[graphics]" : "");

        // The calling methods, nearest first, without this prefix and Harmony's replacement.
        static string Callers()
        {
            var st = new StackTrace(2, false);
            var names = new List<string>();
            for (int i = 0; i < st.FrameCount && names.Count < 4; i++)
            {
                var mb = st.GetFrame(i).GetMethod();
                if (mb == null) continue;
                var n = (mb.DeclaringType == null ? "?" : mb.DeclaringType.Name) + "." + mb.Name;
                if (n.Contains("AssignPositioningIfNeeded") || n.Contains("DMD")) continue;
                names.Add(n);
            }
            return string.Join(" < ", names.ToArray());
        }

        // ----- Run -----

        // The caret's state at points of the frame after the field is enabled.
        static TMP_InputField watched;
        static string lastWatch;
        void OnEnable() => Canvas.willRenderCanvases += WillRender;
        void OnDisable() => Canvas.willRenderCanvases -= WillRender;
        void WillRender() => Watch("willRenderCanvases");
        void LateUpdate() => Watch("LateUpdate(probe)");
        static void Watch(string where)
        {
            if (watched == null) return;
            var st = CaretState(watched);
            if (st == lastWatch) return;
            lastWatch = st;
            L("assign watch " + watched.name + " " + where + " " + st);
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
            var cgo = new GameObject("TgCanvas", typeof(RectTransform));
            DontDestroyOnLoad(cgo);
            var canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32001;
            cgo.AddComponent<GraphicRaycaster>();
            yield return null;
            font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == "NotoSans-Regular");
            if (font == null) { L("font missing"); yield break; }
            L("eventsystem " + (UES.current == null ? "null" : UES.current.name) + " screen " + Screen.width + "x" + Screen.height);
            var part = Environment.GetEnvironmentVariable("PROBE_TG_PART") ?? "all";
            bool P(string p) => part == "all" || part == p;
            if (P("meta")) Step("meta", Meta);
            if (P("assign"))
            {
                // First with whatever else patches the method (OniMcp carries PLib, whose
                // TextMeshProPatcher prefixes it), then with the other prefixes removed.
                var m = HarmonyLib.AccessTools.Method(typeof(TMP_InputField), "AssignPositioningIfNeeded");
                var info = HarmonyLib.Harmony.GetPatchInfo(m);
                L("assign patches prefixes=" + (info == null ? "-" : string.Join(",", info.Prefixes.Select(x => x.owner + ":" + x.PatchMethod.DeclaringType.Name).ToArray())));
                tagSuffix = info != null && info.Prefixes.Any(x => x.PatchMethod.DeclaringType != typeof(TmpGaps)) ? "-plib" : "";
                yield return StartCoroutine(Assign(cgo.transform, false));
                yield return StartCoroutine(Assign(cgo.transform, true));
                if (tagSuffix != "")
                {
                    foreach (var x in info.Prefixes.Where(x => x.PatchMethod.DeclaringType != typeof(TmpGaps)).ToArray())
                        new HarmonyLib.Harmony(x.owner).Unpatch(m, x.PatchMethod);
                    info = HarmonyLib.Harmony.GetPatchInfo(m);
                    L("assign patches after unpatch prefixes=" + string.Join(",", info.Prefixes.Select(x => x.owner + ":" + x.PatchMethod.DeclaringType.Name).ToArray()));
                    tagSuffix = "";
                    yield return StartCoroutine(Assign(cgo.transform, false));
                    yield return StartCoroutine(Assign(cgo.transform, true));
                }
            }
            if (P("mesh")) yield return StartCoroutine(MeshCases(cgo.transform));
            if (P("dropdown")) yield return StartCoroutine(Dropdown(cgo.transform));
            if (P("chars")) Step("chars", Chars);
            if (P("hash")) Step("hash", Hash);
            if (P("renderer")) Step("renderer", () => Renderer(cgo.transform));
            if (P("sprite")) yield return StartCoroutine(Sprites(cgo.transform));
            if (P("font")) yield return StartCoroutine(Fonts(cgo.transform));
            if (P("resources")) yield return StartCoroutine(ResourceTags(cgo.transform));
            Destroy(cgo);
            L("=== done");
            var done = new GameObject("TmpGapsProbeDone", typeof(RectTransform));
            DontDestroyOnLoad(done);
        }

        static void Step(string name, System.Action a)
        {
            try { a(); }
            catch (Exception e) { L(name + " threw " + e.GetType().Name + ": " + e.Message); }
        }

        // ----- meta -----

        static string Vis(MethodBase m) => m == null ? "none" : m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : m.IsFamilyAndAssembly ? "private protected" : "private";
        static string Vis(FieldInfo m) => m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : m.IsFamilyAndAssembly ? "private protected" : "private";

        static string Mods(MethodBase m)
            => (m.IsStatic ? " static" : "") + (m.IsVirtual ? " virtual" : "") + (m.IsAbstract ? " abstract" : "") + (m.IsFinal ? " final" : "")
               + ((m.Attributes & MethodAttributes.NewSlot) != 0 ? " newslot" : "") + (m.IsHideBySig ? " hidebysig" : "") + (m.IsSpecialName ? " specialname" : "");

        static string Sig(MethodBase m)
        {
            var ps = m.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + TypeName(p.ParameterType) + " " + p.Name
                + (p.IsOptional ? " = " + (p.DefaultValue == null ? "null" : p.DefaultValue is bool b ? (b ? "true" : "false") : Convert.ToString(p.DefaultValue, Inv)) : ""));
            var ret = m is MethodInfo mi ? TypeName(mi.ReturnType) + " " : "";
            return ret + m.Name + "(" + string.Join(", ", ps.ToArray()) + ")";
        }

        static string TypeName(Type t)
        {
            if (t.IsByRef) return TypeName(t.GetElementType());
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t.IsGenericType) return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName).ToArray()) + ">";
            return t.Namespace == "System" || t.Namespace == null ? t.Name : t.Namespace + "." + t.Name;
        }

        static void MetaMember(Type t, string name)
        {
            bool any = false;
            foreach (var m in t.GetMembers(All).Where(x => x.Name == name))
            {
                any = true;
                switch (m)
                {
                    case MethodInfo mi: L("meta " + t.Name + " method " + Vis(mi) + Mods(mi) + " " + Sig(mi)); break;
                    case FieldInfo fi:
                        L("meta " + t.Name + " field " + Vis(fi) + (fi.IsStatic ? " static" : "") + (fi.IsInitOnly ? " readonly" : "") + " " + TypeName(fi.FieldType) + " " + fi.Name
                        + (fi.GetCustomAttributes(typeof(SerializeField), false).Length > 0 ? " [SerializeField]" : "")); break;
                    case PropertyInfo pi:
                        L("meta " + t.Name + " property " + TypeName(pi.PropertyType) + " " + pi.Name + " get=" + Vis(pi.GetGetMethod(true)) + (pi.GetGetMethod(true) == null ? "" : Mods(pi.GetGetMethod(true)))
                          + " set=" + Vis(pi.GetSetMethod(true)) + (pi.GetSetMethod(true) == null ? "" : Mods(pi.GetSetMethod(true)))); break;
                    case EventInfo ei:
                        var add = ei.GetAddMethod(true);
                        L("meta " + t.Name + " event " + TypeName(ei.EventHandlerType) + " " + ei.Name + " add=" + Vis(add) + Mods(add) + " remove=" + Vis(ei.GetRemoveMethod(true))); break;
                    default: L("meta " + t.Name + " " + m.MemberType + " " + m.Name); break;
                }
            }
            if (!any) L("meta " + t.Name + " " + name + " absent");
        }

        static void Meta()
        {
            MetaMember(typeof(TMP_InputField), "AssignPositioningIfNeeded");
            MetaMember(typeof(TMP_InputField), "KeyPressed");
            MetaMember(typeof(TMP_InputField), "m_Mesh");
            MetaMember(typeof(TMP_InputField), "mesh");
            MetaMember(typeof(TMP_InputField), "caretRectTrans");
            MetaMember(typeof(TMP_Dropdown), "m_Dropdown");
            MetaMember(typeof(TMP_Dropdown), "m_Blocker");
            MetaMember(typeof(TMP_Dropdown), "IsExpanded");
            MetaMember(typeof(TMP_FontAsset), "HasCharacters");
            MetaMember(typeof(TMP_FontAsset), "HasCharacter");
            MetaMember(typeof(TextMeshProUGUI), "canvasRenderer");
            MetaMember(typeof(Graphic), "canvasRenderer");
            MetaMember(typeof(TMP_SubMeshUI), "canvasRenderer");
            MetaMember(typeof(TextMeshProUGUI), "m_canvasRenderer");
            foreach (var n in typeof(TMP_TextUtilities).GetMethods(All).Where(x => x.Name.Contains("Hash")).Select(x => x.Name).Distinct()) MetaMember(typeof(TMP_TextUtilities), n);
            foreach (var n in typeof(MaterialReferenceManager).GetMembers(All).Where(x => !(x is MethodInfo mi && mi.IsSpecialName)).Select(x => x.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                MetaMember(typeof(MaterialReferenceManager), n);
            foreach (var e in typeof(TMP_Text).GetEvents(All)) MetaMember(typeof(TMP_Text), e.Name);
            MetaMember(typeof(TMP_Asset), "hashCode");
            MetaMember(typeof(TMP_Asset), "m_Version");
            MetaMember(typeof(TMP_FontAsset), "materialHashCode");
            MetaMember(typeof(TMP_Asset), "materialHashCode");
        }

        // ----- Fields -----

        static TMP_InputField MakeField(Transform parent, string name, bool klei)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(160, 24);
            go.AddComponent<Image>();
            TMP_InputField f = klei ? (TMP_InputField)go.AddComponent<KInputTextField>() : go.AddComponent<TMP_InputField>();
            var vp = new GameObject("Text Area", typeof(RectTransform));
            var vrt = (RectTransform)vp.transform;
            vrt.SetParent(rt, false);
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.sizeDelta = Vector2.zero;
            vp.AddComponent<RectMask2D>();
            var tgo = new GameObject("Text", typeof(RectTransform));
            tgo.transform.SetParent(vrt, false);
            var t = tgo.AddComponent<TextMeshProUGUI>();
            t.font = font; t.fontSize = 14; t.enableKerning = true; t.extraPadding = true;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            var trt = t.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.sizeDelta = new Vector2(-6, -2); trt.anchoredPosition = Vector2.zero;
            f.textViewport = vrt;
            f.textComponent = t;
            f.onFocusSelectAll = false;
            return f;
        }

        static string CaretState(TMP_InputField f)
        {
            var c = caretField?.GetValue(f) as RectTransform;
            if (c == null) return "caret=null";
            var t = f.textComponent == null ? null : f.textComponent.rectTransform;
            if (t == null) return "caret=" + F(c.anchorMin) + "-" + F(c.anchorMax) + " size " + F(c.sizeDelta);
            bool same = c.localPosition == t.localPosition && c.localRotation == t.localRotation && c.localScale == t.localScale && c.anchorMin == t.anchorMin
                        && c.anchorMax == t.anchorMax && c.anchoredPosition == t.anchoredPosition && c.sizeDelta == t.sizeDelta && c.pivot == t.pivot;
            return "caret=" + (same ? "matches" : "differs ap " + F(c.anchoredPosition) + " size " + F(c.sizeDelta) + " text ap " + F(t.anchoredPosition) + " size " + F(t.sizeDelta));
        }

        static void LogAssign(string tag, string phase, TMP_InputField f)
        {
            assignCount.TryGetValue(f.GetInstanceID(), out int n);
            var calls = assignCalls.Count == 0 ? "-" : string.Join(" | ", assignCalls.ToArray());
            assignCalls.Clear();
            L("assign " + tag + " " + phase + " total=" + n + " calls=" + calls + " focused=" + f.isFocused + " " + CaretState(f));
        }

        static Event Key(KeyCode code, char ch, EventModifiers mods = EventModifiers.None)
            => new Event { type = EventType.KeyDown, keyCode = code, character = ch, modifiers = mods };

        IEnumerator Frames(string tag, string phase, TMP_InputField f, int n)
        {
            for (int i = 1; i <= n; i++) { yield return null; LogAssign(tag, phase + " frame" + i, f); }
        }

        static string tagSuffix = "";

        IEnumerator Assign(Transform root, bool klei)
        {
            string tag = (klei ? "klei" : "tmp") + tagSuffix;
            var f = MakeField(root, "tg-" + tag, klei);
            LogAssign(tag, "made-inactive", f);
            watched = f;
            f.gameObject.SetActive(true);
            LogAssign(tag, "SetActive(true)", f);
            yield return new WaitForEndOfFrame();
            LogAssign(tag, "end-of-frame", f);
            watched = null;
            yield return StartCoroutine(Frames(tag, "idle", f, 3));
            f.text = "abc";
            LogAssign(tag, "text=abc", f);
            yield return StartCoroutine(Frames(tag, "after-text", f, 2));
            var trt = f.textComponent.rectTransform;
            trt.anchoredPosition = new Vector2(5, 3); trt.sizeDelta = new Vector2(-20, -4);
            LogAssign(tag, "text-rect-moved", f);
            yield return StartCoroutine(Frames(tag, "after-move", f, 2));
            ((RectTransform)f.transform).sizeDelta = new Vector2(200, 30);
            LogAssign(tag, "field-resized", f);
            yield return StartCoroutine(Frames(tag, "after-resize", f, 2));
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)f.transform);
            LogAssign(tag, "ForceRebuildLayoutImmediate", f);
            Canvas.ForceUpdateCanvases();
            LogAssign(tag, "ForceUpdateCanvases", f);
            f.ForceLabelUpdate();
            LogAssign(tag, "ForceLabelUpdate", f);
            UES.current.SetSelectedGameObject(f.gameObject);
            LogAssign(tag, "selected", f);
            f.ActivateInputField();
            LogAssign(tag, "ActivateInputField", f);
            yield return StartCoroutine(Frames(tag, "focusing", f, 3));
            f.ProcessEvent(Key(KeyCode.None, 'd'));
            f.ForceLabelUpdate();
            LogAssign(tag, "typed-d", f);
            yield return StartCoroutine(Frames(tag, "after-type", f, 2));
            f.ProcessEvent(Key(KeyCode.A, 'a', EventModifiers.Control));
            LogAssign(tag, "ctrl-a", f);
            yield return StartCoroutine(Frames(tag, "after-select", f, 2));
            trt.anchoredPosition = new Vector2(-4, 1); trt.sizeDelta = new Vector2(-10, -6);
            LogAssign(tag, "text-rect-moved-focused", f);
            yield return StartCoroutine(Frames(tag, "after-move-focused", f, 3));
            f.text = "a much longer text that scrolls out of the field to the right";
            LogAssign(tag, "long-text-focused", f);
            f.ProcessEvent(Key(KeyCode.End, '\0'));
            yield return StartCoroutine(Frames(tag, "after-long", f, 2));
            f.DeactivateInputField();
            LogAssign(tag, "DeactivateInputField", f);
            UES.current.SetSelectedGameObject(null);
            yield return StartCoroutine(Frames(tag, "deactivated", f, 2));
            f.enabled = false;
            LogAssign(tag, "component-disabled", f);
            f.enabled = true;
            LogAssign(tag, "component-enabled", f);
            f.gameObject.SetActive(false);
            LogAssign(tag, "SetActive(false)", f);
            trt.anchoredPosition = new Vector2(7, -2);
            f.gameObject.SetActive(true);
            LogAssign(tag, "SetActive(true)-again-moved", f);
            yield return StartCoroutine(Frames(tag, "re-enabled", f, 2));
            var other = new GameObject("tg-parent", typeof(RectTransform));
            other.transform.SetParent(root, false);
            f.transform.SetParent(other.transform, false);
            LogAssign(tag, "reparented", f);
            yield return StartCoroutine(Frames(tag, "after-reparent", f, 1));
            // A second text component and viewport assigned.
            var t2go = new GameObject("Text2", typeof(RectTransform));
            t2go.transform.SetParent(f.textViewport, false);
            var t2 = t2go.AddComponent<TextMeshProUGUI>();
            t2.font = font; t2.fontSize = 14;
            t2.rectTransform.anchorMin = Vector2.zero; t2.rectTransform.anchorMax = Vector2.one; t2.rectTransform.sizeDelta = new Vector2(-30, -8);
            f.textComponent = t2;
            LogAssign(tag, "textComponent-reassigned", f);
            f.textViewport = f.textViewport;
            LogAssign(tag, "textViewport-reassigned", f);
            yield return StartCoroutine(Frames(tag, "after-reassign", f, 2));
            f.gameObject.SetActive(false); f.gameObject.SetActive(true);
            LogAssign(tag, "re-enabled-after-reassign", f);
            // Called directly, after the text rect moved.
            t2.rectTransform.anchoredPosition = new Vector2(9, 9);
            LogAssign(tag, "before-direct", f);
            HarmonyLib.AccessTools.Method(typeof(TMP_InputField), "AssignPositioningIfNeeded").Invoke(f, null);
            LogAssign(tag, "direct-call", f);
            // Multi-line with a vertical scrollbar.
            var m = MakeField(root, "tg-" + tag + "-multi", klei);
            m.lineType = TMP_InputField.LineType.MultiLineNewline;
            m.textComponent.textWrappingMode = TextWrappingModes.Normal;
            ((RectTransform)m.transform).sizeDelta = new Vector2(160, 40);
            var sgo = new GameObject("Scrollbar", typeof(RectTransform));
            sgo.transform.SetParent(m.transform, false);
            var sb = sgo.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;
            m.verticalScrollbar = sb;
            m.gameObject.SetActive(true);
            LogAssign(tag + "-multi", "SetActive(true)", m);
            UES.current.SetSelectedGameObject(m.gameObject);
            m.ActivateInputField();
            yield return StartCoroutine(Frames(tag + "-multi", "focusing", m, 2));
            m.text = "line one\nline two\nline three\nline four\nline five";
            m.ProcessEvent(Key(KeyCode.End, '\0', EventModifiers.Control));
            LogAssign(tag + "-multi", "five-lines", m);
            yield return StartCoroutine(Frames(tag + "-multi", "after-lines", m, 2));
            sb.value = 0.2f;
            LogAssign(tag + "-multi", "scrollbar-moved", m);
            yield return StartCoroutine(Frames(tag + "-multi", "after-scroll", m, 2));
            m.DeactivateInputField();
            UES.current.SetSelectedGameObject(null);
            Destroy(m.gameObject);
            Destroy(other);
            // Instantiate a live field.
            var src = MakeField(root, "tg-" + tag + "-src", klei);
            src.gameObject.SetActive(true);
            LogAssign(tag + "-src", "SetActive(true)", src);
            var clone = Instantiate(src.gameObject, root).GetComponent<TMP_InputField>();
            clone.name = "tg-" + tag + "-clone";
            LogAssign(tag + "-clone", "Instantiate", clone);
            yield return null;
            LogAssign(tag + "-clone", "frame1", clone);
            Destroy(src.gameObject); Destroy(clone.gameObject);
            yield return null;
        }

        // ----- m_Mesh -----

        static readonly MethodInfo getMesh = typeof(CanvasRenderer).GetMethod("GetMesh", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        static readonly Dictionary<int, int> meshIds = new Dictionary<int, int>();

        static string MeshState(TMP_InputField f)
        {
            if (meshField == null) return "m_Mesh absent";
            var mesh = meshField.GetValue(f) as Mesh;
            string s;
            if (mesh == null) s = "m_Mesh=" + (ReferenceEquals(meshField.GetValue(f), null) ? "null" : "destroyed");
            else
            {
                int id = mesh.GetInstanceID();
                if (!meshIds.ContainsKey(id)) meshIds[id] = meshIds.Count + 1;
                var cols = mesh.colors32;
                var verts = mesh.vertices;
                var b = mesh.bounds;
                s = "m_Mesh=#" + meshIds[id] + " name=" + Esc(mesh.name) + " hide=" + mesh.hideFlags + " verts=" + mesh.vertexCount + " tris=" + mesh.triangles.Length / 3
                    + " bounds=" + F((Vector2)b.min) + ".." + F((Vector2)b.max) + " col0=" + (cols.Length > 0 ? cols[0].ToString() : "-")
                    + " v=" + string.Join(";", verts.Take(8).Select(v => F((Vector2)v)).ToArray());
            }
            var c = caretField?.GetValue(f) as RectTransform;
            if (c != null)
            {
                var cr = c.GetComponent<CanvasRenderer>();
                string crMesh = "?";
                if (getMesh != null && cr != null)
                {
                    var cm = getMesh.Invoke(cr, null) as Mesh;
                    crMesh = cm == null ? "null" : (mesh != null && cm == mesh ? "same" : "other#" + cm.GetInstanceID() + " verts=" + cm.vertexCount);
                }
                s += " caretRenderer.mesh=" + crMesh + " caretCull=" + (cr != null && cr.cull);
            }
            return s;
        }

        static void LogMesh(string phase, TMP_InputField f)
            => L("mesh " + f.name + " " + phase + " focused=" + f.isFocused + " caret=" + f.caretPosition + "/" + f.selectionAnchorPosition + " text=" + Esc(f.text) + " " + MeshState(f));

        IEnumerator MeshFrames(string phase, TMP_InputField f, int n)
        {
            for (int i = 1; i <= n; i++) { yield return null; LogMesh(phase + " frame" + i, f); }
        }

        IEnumerator MeshCases(Transform root)
        {
            L("mesh CanvasRenderer.GetMesh " + (getMesh == null ? "absent" : "present"));
            foreach (bool klei in new[] { false, true })
            {
                var f = MakeField(root, klei ? "tgm-klei" : "tgm-tmp", klei);
                f.caretColor = new Color32(10, 20, 30, 255);
                f.customCaretColor = true;
                f.selectionColor = new Color32(200, 100, 50, 128);
                LogMesh("made-inactive", f);
                f.gameObject.SetActive(true);
                LogMesh("SetActive(true)", f);
                yield return StartCoroutine(MeshFrames("idle", f, 2));
                f.text = "abc";
                LogMesh("text=abc", f);
                yield return StartCoroutine(MeshFrames("after-text", f, 1));
                UES.current.SetSelectedGameObject(f.gameObject);
                f.ActivateInputField();
                LogMesh("ActivateInputField", f);
                yield return StartCoroutine(MeshFrames("focusing", f, 3));
                f.ProcessEvent(Key(KeyCode.None, 'd'));
                LogMesh("typed-d", f);
                yield return StartCoroutine(MeshFrames("after-type", f, 2));
                f.ProcessEvent(Key(KeyCode.End, '\0'));
                f.ProcessEvent(Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift));
                f.ProcessEvent(Key(KeyCode.LeftArrow, '\0', EventModifiers.Shift));
                f.ForceLabelUpdate();
                LogMesh("end-shift-left-x2", f);
                yield return StartCoroutine(MeshFrames("after-select", f, 2));
                yield return new WaitForSecondsRealtime(0.7f);
                LogMesh("after-select 0.7s", f);
                yield return new WaitForSecondsRealtime(0.7f);
                LogMesh("after-select 1.4s", f);
                f.ProcessEvent(Key(KeyCode.A, 'a', EventModifiers.Control));
                LogMesh("ctrl-a", f);
                yield return StartCoroutine(MeshFrames("after-select-all", f, 2));
                // Through a blink: the caret blinks at caretBlinkRate (half period each way).
                float until = Time.realtimeSinceStartup + 1.2f;
                f.ProcessEvent(Key(KeyCode.End, '\0'));
                yield return null;
                var seen = new List<string>();
                while (Time.realtimeSinceStartup < until)
                {
                    yield return null;
                    var st = MeshState(f);
                    var k = st.Substring(0, Math.Min(st.Length, 200));
                    if (seen.Count == 0 || seen[seen.Count - 1] != k) { seen.Add(k); LogMesh("blink t=" + F(until - Time.realtimeSinceStartup), f); }
                }
                f.DeactivateInputField();
                UES.current.SetSelectedGameObject(null);
                LogMesh("DeactivateInputField", f);
                yield return StartCoroutine(MeshFrames("deactivated", f, 2));
                f.gameObject.SetActive(false);
                LogMesh("SetActive(false)", f);
                f.gameObject.SetActive(true);
                LogMesh("SetActive(true)-again", f);
                yield return StartCoroutine(MeshFrames("re-enabled", f, 1));
                var go = f.gameObject;
                var keep = f;
                Destroy(go);
                yield return null;
                L("mesh " + (klei ? "tgm-klei" : "tgm-tmp") + " destroyed m_Mesh=" + (meshField == null ? "absent" : (meshField.GetValue(keep) as Mesh) == null ? "null-or-destroyed" : "alive"));
            }
        }

        // ----- m_Dropdown -----

        static TMP_Dropdown MakeDropdown(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(160, 30);
            var img = go.AddComponent<Image>();
            var dd = go.AddComponent<TMP_Dropdown>();
            dd.targetGraphic = img;
            var label = Text(rt, "Label", "");
            var template = new GameObject("Template", typeof(RectTransform));
            var trt = (RectTransform)template.transform;
            trt.SetParent(rt, false);
            trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 0); trt.pivot = new Vector2(0.5f, 1); trt.sizeDelta = new Vector2(0, 150);
            template.AddComponent<Image>();
            var scroll = template.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            var vp = new GameObject("Viewport", typeof(RectTransform));
            var vrt = (RectTransform)vp.transform;
            vrt.SetParent(trt, false);
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.sizeDelta = Vector2.zero; vrt.pivot = new Vector2(0, 1);
            vp.AddComponent<Image>();
            vp.AddComponent<Mask>().showMaskGraphic = false;
            var content = new GameObject("Content", typeof(RectTransform));
            var crt = (RectTransform)content.transform;
            crt.SetParent(vrt, false);
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); crt.sizeDelta = new Vector2(0, 28);
            var item = new GameObject("Item", typeof(RectTransform));
            var irt = (RectTransform)item.transform;
            irt.SetParent(crt, false);
            irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(1, 0.5f); irt.sizeDelta = new Vector2(0, 20);
            var toggle = item.AddComponent<Toggle>();
            var bg = new GameObject("Item Background", typeof(RectTransform));
            bg.transform.SetParent(irt, false);
            var bgi = bg.AddComponent<Image>();
            var ck = new GameObject("Item Checkmark", typeof(RectTransform));
            ck.transform.SetParent(irt, false);
            var cki = ck.AddComponent<Image>();
            var itemLabel = Text(irt, "Item Label", "Option");
            toggle.targetGraphic = bgi; toggle.graphic = cki; toggle.isOn = true;
            scroll.content = crt; scroll.viewport = vrt;
            template.SetActive(false);
            dd.template = trt;
            dd.captionText = label;
            dd.itemText = itemLabel;
            dd.options = new List<TMP_Dropdown.OptionData> { new TMP_Dropdown.OptionData("Alpha"), new TMP_Dropdown.OptionData("Beta"), new TMP_Dropdown.OptionData("Gamma") };
            go.SetActive(true);
            return dd;
        }

        static TextMeshProUGUI Text(Transform parent, string name, string text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font; t.fontSize = 14; t.text = text;
            var rt = t.rectTransform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.sizeDelta = Vector2.zero;
            return t;
        }

        static readonly Dictionary<int, int> listIds = new Dictionary<int, int>();

        static string DescribeGo(GameObject g, Transform root)
        {
            int id = g.GetInstanceID();
            if (!listIds.ContainsKey(id)) listIds[id] = listIds.Count + 1;
            var comps = string.Join(",", g.GetComponents<Component>().Select(c => c == null ? "null" : c.GetType().Name + (c is Behaviour b && !b.enabled ? "(off)" : "")).ToArray());
            var path = new List<string>();
            for (var t = g.transform; t != null; t = t.parent) path.Insert(0, t.name);
            var cg = g.GetComponent<CanvasGroup>();
            var scroll = g.GetComponent<ScrollRect>();
            return "#" + listIds[id] + " " + Esc(g.name) + " path=" + string.Join("/", path.ToArray()) + " active=" + g.activeInHierarchy + " comps=" + comps
                   + (cg == null ? "" : " alpha=" + F(cg.alpha)) + (scroll == null || scroll.content == null ? "" : " items=" + scroll.content.GetComponentsInChildren<Toggle>(true).Length + " contentChildren=" + scroll.content.childCount);
        }

        static GameObject lastList;

        static void LogDd(string phase, TMP_Dropdown dd, Transform root)
        {
            object v = dropdownField?.GetValue(dd);
            string s;
            if (dropdownField == null) s = "m_Dropdown absent";
            else if (ReferenceEquals(v, null)) s = "m_Dropdown=null";
            else if ((v as GameObject) == null) s = "m_Dropdown=destroyed-object";
            else { lastList = (GameObject)v; s = "m_Dropdown=" + DescribeGo(lastList, root); }
            string last = ReferenceEquals(lastList, null) ? "" : " lastList=" + (lastList == null ? "gone" : "alive active=" + lastList.activeInHierarchy);
            var blocker = GameObject.Find("Blocker");
            L("dropdown " + dd.name + " " + phase + " expanded=" + dd.IsExpanded + " value=" + dd.value + " " + s + last + " blocker=" + (blocker == null ? "none" : "present"));
        }

        IEnumerator Dropdown(Transform root)
        {
            foreach (var g in Resources.FindObjectsOfTypeAll<TMP_Dropdown>())
            {
                object v = dropdownField?.GetValue(g);
                L("dropdown game " + Esc(g.name) + " scene=" + g.gameObject.scene.IsValid() + " m_Dropdown=" + (ReferenceEquals(v, null) ? "null" : "set"));
            }
            var dd = MakeDropdown(root, "tgdd");
            dd.onValueChanged.AddListener(i => L("dropdown onValueChanged " + i + " m_Dropdown=" + (dropdownField?.GetValue(dd) == null ? "null" : "set") + " expanded=" + dd.IsExpanded));
            LogDd("made", dd, root);
            yield return null;
            LogDd("frame", dd, root);
            dd.Show();
            LogDd("Show", dd, root);
            yield return null;
            LogDd("Show+1", dd, root);
            var first = dropdownField?.GetValue(dd) as GameObject;
            dd.Show();
            LogDd("Show-again", dd, root);
            L("dropdown second Show same=" + (first != null && ReferenceEquals(first, dropdownField?.GetValue(dd))));
            dd.Hide();
            LogDd("Hide", dd, root);
            float t0 = Time.realtimeSinceStartup;
            for (int i = 1; i <= 40; i++)
            {
                yield return null;
                var v = dropdownField?.GetValue(dd);
                LogDd("after-hide frame" + i + " t=" + F(Time.realtimeSinceStartup - t0), dd, root);
                if (ReferenceEquals(v, null) && (lastList == null)) break;
            }
            yield return new WaitForSecondsRealtime(0.5f);
            LogDd("after-hide 0.5s", dd, root);
            // Choose an item by its toggle.
            dd.Show();
            yield return null;
            LogDd("Show-for-choice", dd, root);
            var list = dropdownField?.GetValue(dd) as GameObject;
            if (list != null)
            {
                var toggles = list.GetComponentsInChildren<Toggle>();
                L("dropdown toggles " + string.Join(",", toggles.Select(x => x.name + (x.isOn ? "(on)" : "")).ToArray()));
                if (toggles.Length > 2) toggles[2].isOn = true;
                LogDd("toggle[2].isOn", dd, root);
                if (toggles.Length > 2) { toggles[1].OnPointerClick(new PointerEventData(UES.current) { button = PointerEventData.InputButton.Left }); }
                LogDd("toggle[1] clicked", dd, root);
            }
            for (int i = 1; i <= 40; i++)
            {
                yield return null;
                var v = dropdownField?.GetValue(dd);
                LogDd("after-choice frame" + i, dd, root);
                if (ReferenceEquals(v, null) && lastList == null) break;
            }
            // value set from code while shown.
            dd.Show();
            yield return null;
            dd.value = 0;
            LogDd("value=0 while shown", dd, root);
            yield return null;
            LogDd("value=0 +1", dd, root);
            dd.Hide();
            yield return new WaitForSecondsRealtime(0.6f);
            LogDd("hidden 0.6s", dd, root);
            // Disabled while shown.
            dd.Show();
            yield return null;
            LogDd("Show-before-disable", dd, root);
            dd.gameObject.SetActive(false);
            LogDd("SetActive(false)", dd, root);
            yield return null;
            LogDd("SetActive(false)+1", dd, root);
            dd.gameObject.SetActive(true);
            LogDd("SetActive(true)", dd, root);
            yield return new WaitForSecondsRealtime(0.6f);
            LogDd("re-enabled 0.6s", dd, root);
            // Component disabled while shown.
            dd.Show();
            yield return null;
            LogDd("Show-before-enabled=false", dd, root);
            dd.enabled = false;
            LogDd("enabled=false while shown", dd, root);
            yield return new WaitForSecondsRealtime(0.6f);
            LogDd("enabled=false 0.6s", dd, root);
            dd.enabled = true;
            // Show then Hide in one frame.
            dd.Show(); dd.Hide();
            LogDd("Show+Hide same frame", dd, root);
            yield return new WaitForSecondsRealtime(0.6f);
            LogDd("Show+Hide 0.6s", dd, root);
            // Hide then Show again before the list is gone.
            dd.Show();
            yield return null;
            var a = dropdownField?.GetValue(dd) as GameObject;
            dd.Hide();
            yield return null;
            dd.Show();
            var b = dropdownField?.GetValue(dd) as GameObject;
            LogDd("Hide then Show next frame", dd, root);
            L("dropdown reshow new=" + (a != null && b != null && !ReferenceEquals(a, b)) + " oldAlive=" + (a != null));
            yield return new WaitForSecondsRealtime(0.6f);
            LogDd("reshow 0.6s", dd, root);
            L("dropdown reshow oldAlive=" + (a != null));
            // Destroyed while shown.
            var keep = dd;
            Destroy(dd.gameObject);
            yield return null;
            L("dropdown destroyed-while-shown list=" + (b == null ? "gone" : "alive"));
            yield return new WaitForSecondsRealtime(0.6f);
            L("dropdown destroyed-while-shown 0.6s list=" + (b == null ? "gone" : "alive"));
        }

        // ----- HasCharacters -----

        static string U(uint[] a) => a == null ? "null" : "[" + string.Join(",", a.Select(x => x.ToString("X")).ToArray()) + "]";

        static void HasCase(string label, TMP_FontAsset fa, string text, bool fallbacks, bool add)
        {
            int before = fa.characterTable.Count;
            string res;
            try
            {
                uint[] missing;
                bool r = fa.HasCharacters(text, out missing, fallbacks, add);
                res = r + " missing=" + U(missing);
            }
            catch (Exception e) { res = "threw " + e.GetType().Name; }
            L("chars " + label + " " + Esc(text) + " fallbacks=" + fallbacks + " add=" + add + " -> " + res + " table " + before + "->" + fa.characterTable.Count);
        }

        static void Chars()
        {
            L("chars font " + font.name + " mode=" + font.atlasPopulationMode + " table=" + font.characterTable.Count + " fallbacks=" + (font.fallbackFontAssetTable == null ? "null" : string.Join(",", font.fallbackFontAssetTable.Select(x => x == null ? "null" : x.name).ToArray())));
            L("chars settings fallbacks=" + (TMP_Settings.fallbackFontAssets == null ? "null" : string.Join(",", TMP_Settings.fallbackFontAssets.Select(x => x == null ? "null" : x.name).ToArray())));
            foreach (var g in Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderBy(x => x.name))
                L("chars game-font " + Esc(g.name) + " mode=" + g.atlasPopulationMode + " table=" + g.characterTable.Count + " fallbacks=" + (g.fallbackFontAssetTable == null ? "null" : g.fallbackFontAssetTable.Count.ToString()));
            var samples = new[] { "", "a", "abc", "aab", "a b", "\n", "\t", "é", "一", "a一一b丁", "\U0001F600", "x\U0001F600y", "​", " ", "…", "☃", "─", "�", "\uD83D" };
            foreach (var s in samples) { HasCase("static", font, s, false, false); HasCase("static", font, s, true, false); HasCase("static", font, s, true, true); HasCase("static", font, s, false, true); }
            HasCase("static", font, null, false, false);
            // A dynamic asset from an OS font.
            var dyn = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            if (dyn == null) { L("chars dynamic: no DejaVu Sans"); return; }
            dyn.name = "tg-dyn";
            L("chars dynamic mode=" + dyn.atlasPopulationMode + " table=" + dyn.characterTable.Count);
            HasCase("dynamic", dyn, "abc", false, false);
            HasCase("dynamic", dyn, "abc", true, false);
            HasCase("dynamic", dyn, "abc", false, true);
            HasCase("dynamic", dyn, "abc", false, false);
            HasCase("dynamic", dyn, "abd", false, false);
            HasCase("dynamic", dyn, "a一☃", false, true);
            HasCase("dynamic", dyn, "a一☃", false, false);
            HasCase("dynamic", dyn, "\U0001F600", false, true);
            HasCase("dynamic", dyn, "eé", true, true);
            // A static font with the dynamic one as its fallback.
            var saved = font.fallbackFontAssetTable;
            try
            {
                var dyn2 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                dyn2.name = "tg-dyn2";
                font.fallbackFontAssetTable = new List<TMP_FontAsset> { dyn2 };
                foreach (var s in new[] { "a☃", "☃─", "一" })
                {
                    HasCase("static+fallback", font, s, false, false);
                    HasCase("static+fallback", font, s, true, false);
                    L("chars fallback table " + dyn2.characterTable.Count);
                    HasCase("static+fallback", font, s, true, true);
                    L("chars fallback table " + dyn2.characterTable.Count);
                    HasCase("static+fallback", font, s, true, false);
                }
            }
            finally { font.fallbackFontAssetTable = saved; }
            // Without TMP_Settings' global fallbacks: the font's own fallback table alone.
            var global = TMP_Settings.fallbackFontAssets;
            var globalCopy = global == null ? null : new List<TMP_FontAsset>(global);
            try
            {
                if (global != null) global.Clear();
                L("chars global fallbacks cleared");
                foreach (var s in new[] { "\u4E00", "\u2603", "a" }) { HasCase("static-noglobal", font, s, true, false); }
                var dyn3 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                dyn3.name = "tg-dyn3";
                foreach (var s in new[] { "abc", "\n", "\t", "\u200B", "\r", " ", "\u00A0", "\u00AD", "\u2060", "\u0003" }) { HasCase("dynamic-noglobal", dyn3, s, false, false); HasCase("dynamic-noglobal", dyn3, s, true, false); }
                HasCase("dynamic-noglobal", dyn3, "abc", true, true);
                var saved2 = font.fallbackFontAssetTable;
                try
                {
                    var dyn4 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                    dyn4.name = "tg-dyn4";
                    font.fallbackFontAssetTable = new List<TMP_FontAsset> { dyn4 };
                    HasCase("static+own-fallback-noglobal", font, "a\u2603\u4E00", true, false);
                    HasCase("static+own-fallback-noglobal", font, "a\u2603\u4E00", true, true);
                    L("chars own fallback table " + dyn4.characterTable.Count);
                    HasCase("static+own-fallback-noglobal", font, "a\u2603\u4E00", true, false);
                    // A fallback's own fallback.
                    var dyn5 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                    dyn5.name = "tg-dyn5";
                    var empty = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                    empty.name = "tg-empty";
                    empty.atlasPopulationMode = AtlasPopulationMode.Static;
                    empty.fallbackFontAssetTable = new List<TMP_FontAsset> { dyn5 };
                    font.fallbackFontAssetTable = new List<TMP_FontAsset> { empty };
                    HasCase("static+nested-fallback-noglobal", font, "\u2500", true, true);
                    L("chars nested fallback tables empty=" + empty.characterTable.Count + " dyn5=" + dyn5.characterTable.Count);
                    HasCase("static-mode-dynamic-face", empty, "x", false, true);
                    // TMP_Settings' default font asset as the last fallback.
                    var defp = typeof(TMP_Settings).GetProperty("defaultFontAsset", BindingFlags.Static | BindingFlags.Public);
                    var deff = typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var def = TMP_Settings.defaultFontAsset;
                    L("chars default font " + (def == null ? "null" : Esc(def.name)) + " setter=" + (defp != null && defp.GetSetMethod() != null) + " field=" + (deff != null));
                    var dyn6 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                    dyn6.name = "tg-dyn6";
                    font.fallbackFontAssetTable = saved2;
                    HasCase("dynamic-noglobal-default", dyn6, "a\u4E00\u2603", true, false);
                    if (deff != null)
                    {
                        deff.SetValue(TMP_Settings.instance, null);
                        L("chars default font cleared: " + (TMP_Settings.defaultFontAsset == null ? "null" : Esc(TMP_Settings.defaultFontAsset.name)));
                        HasCase("dynamic-noglobal-nodefault", dyn6, "a\u4E00\u2603", true, false);
                        HasCase("dynamic-noglobal-nodefault", dyn6, "a", true, false);
                        deff.SetValue(TMP_Settings.instance, def);
                    }
                }
                finally { font.fallbackFontAssetTable = saved2; }
            }
            finally { if (global != null) { global.Clear(); global.AddRange(globalCopy); } }
            // Existing overloads beside it, for comparison.
            L("chars HasCharacters(string) abc=" + font.HasCharacters("abc") + " a一 =" + font.HasCharacters("a一"));
        }

        // ----- GetHashCode -----

        static void Hash()
        {
            var samples = new[] { "", "a", "A", "b", "ab", "AB", "abc", "ABC", "aBc", "sip_divider", "SIP_DIVIDER", "NotoSans-Regular", "Default Sprite Asset", "LiberationSans SDF", "font", "sprite", "0", " ", "a b", "é", "É", "一", "\U0001F600", "ı", "İ", "ß", "abcdefghijklmnopqrstuvwxyz0123456789" };
            var methods = typeof(TMP_TextUtilities).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name.Contains("Hash") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)).ToArray();
            foreach (var s in samples)
            {
                var parts = new List<string>();
                foreach (var m in methods)
                {
                    try { parts.Add(m.Name + (m.IsPublic ? "" : "(np)") + "=" + Convert.ToString(m.Invoke(null, new object[] { s }), Inv)); }
                    catch (TargetInvocationException e) { parts.Add(m.Name + " threw " + e.InnerException.GetType().Name); }
                }
                L("hash " + Esc(s) + " " + string.Join(" ", parts.ToArray()));
            }
            foreach (var m in methods)
            {
                try { L("hash null " + m.Name + "=" + Convert.ToString(m.Invoke(null, new object[] { null }), Inv)); }
                catch (TargetInvocationException e) { L("hash null " + m.Name + " threw " + e.InnerException.GetType().Name); }
            }
            try { L("hash direct GetHashCode(\"sip_divider\")=" + TMP_TextUtilities.GetHashCode("sip_divider")); }
            catch (Exception e) { L("hash direct threw " + e.GetType().Name); }
            // Assets' own hash codes beside the hash of their names.
            foreach (var a in Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderBy(x => x.name).Take(12))
                L("hash font " + Esc(a.name) + " hashCode=" + a.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode(a.name) + " material=" + (a.material == null ? "null" : Esc(a.material.name) + " ofMatName=" + TMP_TextUtilities.GetHashCode(a.material.name)));
            foreach (var a in Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>().OrderBy(x => x.name))
                L("hash sprite " + Esc(a.name) + " hashCode=" + a.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode(a.name));
            var fresh = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            L("hash fresh-sprite-asset hashCode=" + fresh.hashCode);
            fresh.name = "fresh";
            L("hash fresh-sprite-asset named hashCode=" + fresh.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode("fresh"));
            fresh.name = "renamed";
            L("hash fresh-sprite-asset renamed hashCode=" + fresh.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode("renamed"));
            fresh.hashCode = 0;
            L("hash fresh-sprite-asset hashCode=0 then read=" + fresh.hashCode);
            fresh.hashCode = 12345;
            fresh.name = "again";
            L("hash fresh-sprite-asset set 12345 renamed read=" + fresh.hashCode);
            var mp = typeof(TMP_Asset).GetProperty("materialHashCode");
            foreach (var a in Resources.FindObjectsOfTypeAll<TMP_FontAsset>().OrderBy(x => x.name).Take(12))
                L("hash font-material " + Esc(a.name) + " materialHashCode=" + mp.GetValue(a, null) + " material=" + (a.material == null ? "null" : Esc(a.material.name) + " ofMatName=" + TMP_TextUtilities.GetHashCode(a.material.name) + " simple=" + TMP_TextUtilities.GetSimpleHashCode(a.material.name) + " instanceID=" + a.material.GetInstanceID()));
            var dynm = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            if (dynm != null)
            {
                L("hash dyn materialHashCode=" + mp.GetValue(dynm, null) + " material=" + Esc(dynm.material.name) + " instanceID=" + dynm.material.GetInstanceID() + " ofMatName=" + TMP_TextUtilities.GetHashCode(dynm.material.name) + " simple=" + TMP_TextUtilities.GetSimpleHashCode(dynm.material.name));
                dynm.name = "MatProbe";
                L("hash dyn named MatProbe materialHashCode=" + mp.GetValue(dynm, null) + " ofName+Material=" + TMP_TextUtilities.GetHashCode("MatProbe Material"));
                dynm.material.name = "MatProbe Material X";
                L("hash dyn material renamed materialHashCode=" + mp.GetValue(dynm, null) + " ofNewMatName=" + TMP_TextUtilities.GetHashCode("MatProbe Material X"));
                var dynm2 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
                L("hash dyn2 materialHashCode=" + mp.GetValue(dynm2, null) + " instanceID=" + dynm2.material.GetInstanceID());
            }
            Destroy(fresh);
        }

        // ----- canvasRenderer -----

        static void Renderer(Transform root)
        {
            var go = new GameObject("tg-renderer-inactive", typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(root, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            var comp = go.GetComponent<CanvasRenderer>();
            L("renderer inactive comps=" + string.Join(",", go.GetComponents<Component>().Select(c => c.GetType().Name).ToArray()));
            L("renderer inactive (before Awake) tmp=" + R(t.canvasRenderer, comp) + " graphic=" + R(((Graphic)t).canvasRenderer, comp));
            go.SetActive(true);
            L("renderer active tmp=" + R(t.canvasRenderer, comp) + " graphic=" + R(((Graphic)t).canvasRenderer, comp));
            var go2 = new GameObject("tg-renderer-inactive2", typeof(RectTransform));
            go2.SetActive(false);
            go2.transform.SetParent(root, false);
            var t2 = go2.AddComponent<TextMeshProUGUI>();
            var comp2 = go2.GetComponent<CanvasRenderer>();
            L("renderer inactive2 graphic-first=" + R(((Graphic)t2).canvasRenderer, comp2) + " tmp=" + R(t2.canvasRenderer, comp2));
            var go3 = new GameObject("tg-renderer-active", typeof(RectTransform));
            go3.transform.SetParent(root, false);
            var t3 = go3.AddComponent<TextMeshProUGUI>();
            L("renderer active-at-once tmp=" + R(t3.canvasRenderer, go3.GetComponent<CanvasRenderer>()));
            t3.text = "x";
            Canvas.ForceUpdateCanvases();
            L("renderer after-layout tmp=" + R(t3.canvasRenderer, go3.GetComponent<CanvasRenderer>()) + " cull=" + t3.canvasRenderer.cull + " materialCount=" + t3.canvasRenderer.materialCount);
            var loc = Resources.FindObjectsOfTypeAll<LocText>().FirstOrDefault(x => x.gameObject.scene.IsValid() && x.isActiveAndEnabled);
            if (loc != null) L("renderer loctext " + Esc(loc.name) + " tmp=" + R(loc.canvasRenderer, loc.GetComponent<CanvasRenderer>()));
            // Through reflection on the declared getter, as a mod compiled against it calls it.
            var p = typeof(TextMeshProUGUI).GetProperty("canvasRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            L("renderer declared getter " + (p == null ? "absent" : R((CanvasRenderer)p.GetValue(t3, null), go3.GetComponent<CanvasRenderer>())));
            Destroy(go); Destroy(go2); Destroy(go3);
        }

        static string R(CanvasRenderer got, CanvasRenderer comp) => ReferenceEquals(got, null) ? "null" : got == null ? "destroyed" : got == comp ? "component" : "other";

        // ----- Sprites -----

        static readonly List<string> requests = new List<string>();
        static readonly Dictionary<string, TMP_SpriteAsset> served = new Dictionary<string, TMP_SpriteAsset>();
        static string secondHandlerTag;

        static TMP_SpriteAsset Request(int hashCode, string assetName)
        {
            requests.Add("(" + hashCode + "," + Esc(assetName) + (hashCode == TMP_TextUtilities.GetHashCode(assetName ?? "") ? ",=hash(name)" : ",!=hash(name)") + ")");
            served.TryGetValue(assetName ?? "", out var a);
            return a;
        }

        static TMP_SpriteAsset Request2(int hashCode, string assetName)
        {
            requests.Add("second(" + Esc(assetName) + ")");
            return secondHandlerTag == assetName && served.TryGetValue("second:" + assetName, out var a) ? a : null;
        }

        static TMP_SpriteAsset MakeSprite(string name, string spriteName, Material template)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var px = new Color32[64];
            for (int i = 0; i < 64; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px); tex.Apply();
            var glyph = new TMP_SpriteGlyph(0u, new GlyphMetrics(8, 8, 0, 8, 8), new GlyphRect(0, 0, 8, 8), 1f, 0);
            var ch = new TMP_SpriteCharacter(0u, glyph) { name = spriteName };
            var a = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            a.name = name;
            a.spriteSheet = tex;
            var vf = typeof(TMP_Asset).GetField("m_Version", BindingFlags.Instance | BindingFlags.NonPublic);
            if (vf != null) vf.SetValue(a, "1.1.0");
            a.spriteGlyphTable.Add(glyph);
            a.spriteCharacterTable.Add(ch);
            a.UpdateLookupTables();
            var mat = template != null ? new Material(template) : new Material(Shader.Find("TextMeshPro/Sprite"));
            mat.mainTexture = tex;
            mat.name = name + " mat";
            a.material = mat;
            return a;
        }

        static string SpriteOf(TMP_CharacterInfo c)
        {
            var e = c.textElement;
            if (e == null) return "null";
            var asset = e.GetType().GetProperty("textAsset")?.GetValue(e, null) as Object;
            return (asset == null ? "?" : asset.name) + ":" + ((e as TMP_SpriteCharacter)?.name ?? e.GetType().Name);
        }

        static string Glyphs(TextMeshProUGUI t)
        {
            var ti = t.textInfo;
            var sb = new StringBuilder();
            for (int i = 0; i < ti.characterCount; i++)
            {
                var c = ti.characterInfo[i];
                if (c.elementType == TMP_TextElementType.Sprite) sb.Append("[S:" + SpriteOf(c) + "]");
                else sb.Append(c.character == ' ' ? "_" : c.character.ToString());
            }
            return "chars=" + ti.characterCount + " " + Esc(sb.ToString()) + " sprites=" + ti.spriteCount + " materials=" + ti.materialCount;
        }

        IEnumerator Sprites(Transform root)
        {
            var def = TMP_Settings.defaultSpriteAsset;
            L("sprite default=" + (def == null ? "null" : Esc(def.name) + " hash=" + def.hashCode + " fallbacks=" + (def.fallbackSpriteAssets == null ? "null" : def.fallbackSpriteAssets.Count.ToString())) + " path=" + Esc(TMP_Settings.defaultSpriteAssetPath));
            var tmpl = def != null ? def.material : null;
            foreach (var n in new[] { "probe_reg", "probe_req", "probe_x", "probe_unreg", "Default Sprite Asset" })
            {
                TMP_SpriteAsset got;
                bool ok = MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode(n), out got);
                L("sprite initially TryGetSpriteAsset(" + Esc(n) + ")=" + ok + (got == null ? "" : " " + Esc(got.name)));
            }
            var reg = MakeSprite("probe_reg", "reg_line", tmpl);
            var req = MakeSprite("probe_req", "req_line", tmpl);
            var x = MakeSprite("probe_x", "x_line", tmpl);
            var dup = MakeSprite("probe_dup", "dup_line", tmpl);
            L("sprite made probe_reg hashCode=" + reg.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode("probe_reg"));

            var go = new GameObject("tg-sprite", typeof(RectTransform));
            go.transform.SetParent(root, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font; t.fontSize = 14;
            t.rectTransform.sizeDelta = new Vector2(400, 40);
            yield return null;

            IEnumerator Case(string label, string text, int frames = 1)
            {
                requests.Clear();
                t.text = text;
                L("sprite " + label + " text=" + Esc(text) + " after-set requests=" + (requests.Count == 0 ? "-" : string.Join(" ", requests.ToArray())));
                requests.Clear();
                t.ForceMeshUpdate();
                L("sprite " + label + " ForceMeshUpdate requests=" + (requests.Count == 0 ? "-" : string.Join(" ", requests.ToArray())) + " " + Glyphs(t));
                requests.Clear();
                t.ForceMeshUpdate(true, true);
                L("sprite " + label + " ForceMeshUpdate-again requests=" + (requests.Count == 0 ? "-" : string.Join(" ", requests.ToArray())));
                for (int i = 0; i < frames; i++)
                {
                    requests.Clear();
                    yield return null;
                    L("sprite " + label + " frame" + (i + 1) + " requests=" + (requests.Count == 0 ? "-" : string.Join(" ", requests.ToArray())) + " " + Glyphs(t));
                }
            }

            // Without a handler.
            yield return StartCoroutine(Case("no-handler unregistered", "a<sprite=\"probe_unreg\" index=0>b"));
            TMP_Text.OnSpriteAssetRequest += Request;
            yield return StartCoroutine(Case("handler-null unregistered", "a<sprite=\"probe_unreg\" index=0>b", 2));
            yield return StartCoroutine(Case("handler-null unregistered by-name", "a<sprite=\"probe_unreg\" name=\"x\">b"));
            yield return StartCoroutine(Case("handler-null default-index", "a<sprite=0>b"));
            yield return StartCoroutine(Case("handler-null default-name", "a<sprite name=\"reg_line\">b"));
            yield return StartCoroutine(Case("handler-null unquoted", "a<sprite=probe_unreg index=0>b"));
            // The handler serves probe_req.
            served["probe_req"] = req;
            yield return StartCoroutine(Case("handler-serves", "a<sprite=\"probe_req\" index=0>b", 2));
            {
                TMP_SpriteAsset got;
                bool ok = MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("probe_req"), out got);
                L("sprite after-serve TryGetSpriteAsset(probe_req)=" + ok + (got == null ? "" : " " + Esc(got.name)) + " served.hashCode=" + req.hashCode);
            }
            yield return StartCoroutine(Case("handler-serves by-name", "a<sprite=\"probe_req\" name=\"req_line\">b"));
            yield return StartCoroutine(Case("handler-serves twice", "<sprite=\"probe_req\" index=0><sprite=\"probe_req\" index=0>"));
            served.Remove("probe_req");
            yield return StartCoroutine(Case("handler-stopped-serving", "a<sprite=\"probe_req\" index=0>b"));
            // Registered by AddSpriteAsset(int, asset) under the hash of its name.
            requests.Clear();
            try { MaterialReferenceManager.AddSpriteAsset(TMP_TextUtilities.GetHashCode("probe_reg"), reg); L("sprite AddSpriteAsset(hash(probe_reg), probe_reg) ok requests=" + requests.Count); }
            catch (Exception e) { L("sprite AddSpriteAsset threw " + e.GetType().Name + ": " + e.Message); }
            {
                TMP_SpriteAsset got;
                bool ok = MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("probe_reg"), out got);
                L("sprite after-add TryGetSpriteAsset(probe_reg)=" + ok + (got == null ? "" : " " + Esc(got.name)));
            }
            yield return StartCoroutine(Case("registered", "a<sprite=\"probe_reg\" index=0>b"));
            yield return StartCoroutine(Case("registered by-name", "a<sprite=\"probe_reg\" name=\"reg_line\">b"));
            yield return StartCoroutine(Case("registered wrong-name", "a<sprite=\"probe_reg\" name=\"nope\">b"));
            yield return StartCoroutine(Case("registered index-out", "a<sprite=\"probe_reg\" index=5>b"));
            yield return StartCoroutine(Case("registered other-case", "a<sprite=\"PROBE_REG\" index=0>b"));
            // Registered under another key.
            try { MaterialReferenceManager.AddSpriteAsset(TMP_TextUtilities.GetHashCode("probe_alias"), x); L("sprite AddSpriteAsset(hash(probe_alias), probe_x) ok"); }
            catch (Exception e) { L("sprite AddSpriteAsset alias threw " + e.GetType().Name); }
            yield return StartCoroutine(Case("alias by-key", "a<sprite=\"probe_alias\" index=0>b"));
            yield return StartCoroutine(Case("alias by-asset-name", "a<sprite=\"probe_x\" index=0>b"));
            // The same key twice.
            try { MaterialReferenceManager.AddSpriteAsset(TMP_TextUtilities.GetHashCode("probe_reg"), dup); L("sprite AddSpriteAsset(hash(probe_reg), probe_dup) ok"); }
            catch (Exception e) { L("sprite AddSpriteAsset dup threw " + e.GetType().Name + ": " + e.Message); }
            {
                TMP_SpriteAsset got;
                MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("probe_reg"), out got);
                L("sprite after-dup TryGetSpriteAsset(probe_reg)=" + (got == null ? "null" : Esc(got.name)));
            }
            yield return StartCoroutine(Case("dup", "a<sprite=\"probe_reg\" index=0>b"));
            // AddSpriteAsset(asset), the one-argument overload, by reflection.
            var one = typeof(MaterialReferenceManager).GetMethod("AddSpriteAsset", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(TMP_SpriteAsset) }, null);
            if (one != null)
            {
                var o1 = MakeSprite("probe_one", "one_line", tmpl);
                L("sprite probe_one hashCode=" + o1.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode("probe_one"));
                one.Invoke(null, new object[] { o1 });
                TMP_SpriteAsset got;
                bool ok1 = MaterialReferenceManager.TryGetSpriteAsset(o1.hashCode, out got);
                bool ok2 = MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("probe_one"), out got);
                L("sprite AddSpriteAsset(asset) TryGet(asset.hashCode)=" + ok1 + " TryGet(hash(name))=" + ok2);
                yield return StartCoroutine(Case("one-arg", "a<sprite=\"probe_one\" index=0>b"));
            }
            // Two handlers.
            TMP_Text.OnSpriteAssetRequest += Request2;
            served["second:probe_two"] = MakeSprite("probe_two", "two_line", tmpl);
            secondHandlerTag = "probe_two";
            yield return StartCoroutine(Case("two-handlers second-serves", "a<sprite=\"probe_two\" index=0>b"));
            served["probe_three"] = MakeSprite("probe_three", "three_line", tmpl);
            yield return StartCoroutine(Case("two-handlers first-serves", "a<sprite=\"probe_three\" index=0>b"));
            TMP_Text.OnSpriteAssetRequest -= Request2;
            // Text set but laid out only by the frame's rebuild.
            requests.Clear();
            t.text = "c<sprite=\"probe_unreg2\" index=0>d";
            L("sprite rebuild-only after-set requests=" + requests.Count);
            yield return null;
            L("sprite rebuild-only frame1 requests=" + string.Join(" ", requests.ToArray()) + " " + Glyphs(t));
            requests.Clear();
            yield return null;
            L("sprite rebuild-only frame2 requests=" + requests.Count);
            // Preferred size alone.
            requests.Clear();
            var pv = t.GetPreferredValues("e<sprite=\"probe_unreg3\" index=0>f");
            L("sprite GetPreferredValues requests=" + string.Join(" ", requests.ToArray()) + " w=" + F(pv.x));
            // Rich text off.
            t.richText = false;
            yield return StartCoroutine(Case("richText-off", "a<sprite=\"probe_unreg4\" index=0>b"));
            t.richText = true;
            // The text's own sprite asset named in the tag.
            t.spriteAsset = x;
            yield return StartCoroutine(Case("own-asset by-name", "a<sprite=\"probe_x\" index=0>b"));
            TMP_Text.OnSpriteAssetRequest -= Request;
            requests.Clear();
            yield return StartCoroutine(Case("handler-removed", "a<sprite=\"probe_unreg5\" index=0>b"));
            Destroy(go);
        }

        // ----- Fonts -----

        static string FontsOf(TextMeshProUGUI t)
        {
            var ti = t.textInfo;
            var sb = new StringBuilder();
            string last = null;
            for (int i = 0; i < ti.characterCount; i++)
            {
                var c = ti.characterInfo[i];
                var n = c.fontAsset == null ? "null" : c.fontAsset.name;
                if (n != last) { sb.Append("[" + n + "]"); last = n; }
                sb.Append(c.character == ' ' ? '_' : c.character);
            }
            return "chars=" + ti.characterCount + " " + Esc(sb.ToString()) + " materials=" + ti.materialCount;
        }

        static string TryFont(string n)
        {
            TMP_FontAsset got;
            bool ok = MaterialReferenceManager.TryGetFontAsset(TMP_TextUtilities.GetHashCode(n), out got);
            return Esc(n) + "=" + ok + (got == null ? "" : ":" + Esc(got.name));
        }

        IEnumerator Fonts(Transform root)
        {
            L("font initially " + string.Join(" ", new[] { "NotoSans-Regular", "NotoSans-Bold", "GRAYSTROKE REGULAR SDF", "RobotoCondensed-Regular", "ProbeFontA" }.Select(TryFont).ToArray()));
            var go = new GameObject("tg-font", typeof(RectTransform));
            go.transform.SetParent(root, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font; t.fontSize = 14;
            t.rectTransform.sizeDelta = new Vector2(400, 40);
            yield return null;
            void Case(string label, string text)
            {
                t.text = text;
                t.ForceMeshUpdate();
                L("font " + label + " text=" + Esc(text) + " " + FontsOf(t));
            }
            var others = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Where(x => x != font).OrderBy(x => x.name).Select(x => x.name).Take(3).ToArray();
            foreach (var o in others) Case("game-font-tag", "a<font=\"" + o + "\">b</font>c");
            L("font after game tags " + string.Join(" ", others.Select(TryFont).ToArray()));
            Case("unknown", "a<font=\"ProbeFontA\">b</font>c");
            Case("default", "a<font=\"default\">b</font>c");
            Case("own font by name", "a<font=\"NotoSans-Regular\">b</font>c");
            L("font after own-name tag " + TryFont("NotoSans-Regular"));
            var a = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            if (a == null) { L("font no DejaVu Sans"); yield break; }
            L("font created name=" + Esc(a.name) + " hashCode=" + a.hashCode + " ofName(\"\")=" + TMP_TextUtilities.GetHashCode(""));
            a.name = "ProbeFontA";
            L("font renamed ProbeFontA hashCode=" + a.hashCode + " ofName=" + TMP_TextUtilities.GetHashCode("ProbeFontA") + " materialHashCode=" + Convert.ToString(typeof(TMP_Asset).GetProperty("materialHashCode")?.GetValue(a, null) ?? typeof(TMP_FontAsset).GetProperty("materialHashCode")?.GetValue(a, null), Inv));
            try { MaterialReferenceManager.AddFontAsset(a); L("font AddFontAsset(ProbeFontA) ok"); }
            catch (Exception e) { L("font AddFontAsset threw " + e.GetType().Name + ": " + e.Message); }
            {
                TMP_FontAsset got;
                bool byHash = MaterialReferenceManager.TryGetFontAsset(a.hashCode, out got);
                L("font after-add TryGet(asset.hashCode)=" + byHash + " " + TryFont("ProbeFontA") + " " + TryFont(""));
                Material m;
                bool mat = MaterialReferenceManager.TryGetMaterial(a.material.GetInstanceID(), out m);
                bool matName = MaterialReferenceManager.TryGetMaterial(TMP_TextUtilities.GetHashCode(a.material.name), out m);
                L("font after-add TryGetMaterial(instanceID)=" + mat + " TryGetMaterial(hash(material.name))=" + matName + " material.name=" + Esc(a.material.name));
            }
            Case("ProbeFontA", "a<font=\"ProbeFontA\">b</font>c");
            // An asset whose hashCode was set to its name's hash before it is added.
            var mhp = typeof(TMP_Asset).GetProperty("materialHashCode");
            // A second dynamic asset: its material has the same name and materialHashCode.
            var b0 = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            b0.name = "ProbeFontB0";
            L("font B0 materialHashCode=" + mhp.GetValue(b0, null) + " A's=" + mhp.GetValue(a, null));
            try { MaterialReferenceManager.AddFontAsset(b0); L("font AddFontAsset(B0, same material key) ok"); }
            catch (Exception e) { L("font AddFontAsset(B0, same material key) threw " + e.GetType().Name + ": " + e.Message); }
            L("font after-B0 " + TryFont("ProbeFontB0"));
            Case("ProbeFontB0", "a<font=\"ProbeFontB0\">b</font>c");
            try { MaterialReferenceManager.AddFontAsset(b0); L("font AddFontAsset(B0) again ok"); }
            catch (Exception e) { L("font AddFontAsset(B0) again threw " + e.GetType().Name); }
            try { MaterialReferenceManager.AddFontAsset(a); L("font AddFontAsset(A) again ok"); }
            catch (Exception e) { L("font AddFontAsset(A) again threw " + e.GetType().Name); }
            var b = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            b.name = "ProbeFontB";
            b.hashCode = TMP_TextUtilities.GetHashCode("ProbeFontB");
            mhp.SetValue(b, TMP_TextUtilities.GetHashCode("ProbeFontB material"), null);
            try { MaterialReferenceManager.AddFontAsset(b); L("font AddFontAsset(B) ok"); }
            catch (Exception e) { L("font AddFontAsset(B) threw " + e.GetType().Name); }
            {
                Material m;
                bool okm = MaterialReferenceManager.TryGetMaterial(TMP_TextUtilities.GetHashCode("ProbeFontB material"), out m);
                L("font after-add-B TryGetMaterial(materialHashCode)=" + okm + (m == null ? "" : " " + Esc(m.name) + " same=" + (m == b.material)));
            }
            L("font after-add-B " + TryFont("ProbeFontB"));
            Case("ProbeFontB", "a<font=\"ProbeFontB\">b</font>c");
            // hashCode set to another name's hash.
            var c = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            c.name = "ProbeFontC";
            c.hashCode = TMP_TextUtilities.GetHashCode("AliasC");
            mhp.SetValue(c, TMP_TextUtilities.GetHashCode("ProbeFontC material"), null);
            try { MaterialReferenceManager.AddFontAsset(c); } catch (Exception e) { L("font AddFontAsset(C) threw " + e.GetType().Name); }
            L("font after-add-C " + TryFont("ProbeFontC") + " " + TryFont("AliasC"));
            Case("ProbeFontC by name", "a<font=\"ProbeFontC\">b</font>c");
            Case("ProbeFontC by alias", "a<font=\"AliasC\">b</font>c");
            // Added twice, and another asset with the same key.
            try { MaterialReferenceManager.AddFontAsset(b); L("font AddFontAsset(B) twice ok"); }
            catch (Exception e) { L("font AddFontAsset twice threw " + e.GetType().Name); }
            var d = TMP_FontAsset.CreateFontAsset("DejaVu Sans", "Book");
            d.name = "ProbeFontD";
            d.hashCode = TMP_TextUtilities.GetHashCode("ProbeFontB");
            mhp.SetValue(d, TMP_TextUtilities.GetHashCode("ProbeFontD material"), null);
            try { MaterialReferenceManager.AddFontAsset(d); L("font AddFontAsset(D with B's key) ok"); }
            catch (Exception e) { L("font AddFontAsset D threw " + e.GetType().Name); }
            L("font after-add-D " + TryFont("ProbeFontB"));
            Case("ProbeFontB after D", "a<font=\"ProbeFontB\">b</font>c");
            // A renamed asset after it was added.
            a.name = "ProbeFontA2";
            L("font renamed-after-add hashCode=" + a.hashCode + " " + TryFont("ProbeFontA") + " " + TryFont("ProbeFontA2"));
            Case("ProbeFontA after rename", "a<font=\"ProbeFontA\">b</font>c");
            Case("ProbeFontA2 after rename", "a<font=\"ProbeFontA2\">b</font>c");
            Case("ProbeFontA other case", "a<font=\"probefonta\">b</font>c");
            Case("ProbeFontA unquoted", "a<font=ProbeFontA>b</font>c");
            // Game fonts that share one material.
            var g1 = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == "GRAYSTROKE NAME SDF");
            var g2 = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == "GRAYSTROKE OUTLINE SDF");
            if (g1 != null && g2 != null)
            {
                L("font game materialHashCodes " + mhp.GetValue(g1, null) + " " + mhp.GetValue(g2, null));
                try { MaterialReferenceManager.AddFontAsset(g1); L("font AddFontAsset(GRAYSTROKE NAME SDF) ok"); }
                catch (Exception e) { L("font AddFontAsset(GRAYSTROKE NAME SDF) threw " + e.GetType().Name); }
                try { MaterialReferenceManager.AddFontAsset(g2); L("font AddFontAsset(GRAYSTROKE OUTLINE SDF) ok"); }
                catch (Exception e) { L("font AddFontAsset(GRAYSTROKE OUTLINE SDF) threw " + e.GetType().Name + ": " + e.Message); }
                L("font after game adds " + TryFont("GRAYSTROKE NAME SDF") + " " + TryFont("GRAYSTROKE OUTLINE SDF"));
                Case("game font registered", "a<font=\"GRAYSTROKE NAME SDF\">b</font>c");
                Case("game font second", "a<font=\"GRAYSTROKE OUTLINE SDF\">b</font>c");
            }
            // Null.
            try { MaterialReferenceManager.AddFontAsset(null); L("font AddFontAsset(null) ok"); }
            catch (Exception e) { L("font AddFontAsset(null) threw " + e.GetType().Name); }
            Destroy(go);
            yield return null;
        }

        // ----- Tags naming assets under Resources -----

        static readonly List<string> fontRequests = new List<string>();

        static TMP_FontAsset FontRequest(int hashCode, string assetName)
        {
            fontRequests.Add("(" + hashCode + "," + Esc(assetName) + ")");
            return null;
        }

        static string MaterialsOf(TextMeshProUGUI t)
        {
            var ti = t.textInfo;
            var names = new List<string>();
            for (int i = 0; i < ti.materialCount; i++) names.Add(ti.meshInfo[i].material == null ? "null" : ti.meshInfo[i].material.name);
            return "mats=" + Esc(string.Join("|", names.ToArray()));
        }

        // The game's own tags name assets under Resources ("Sprite Assets/oni_sprite_assets",
        // "Fonts & Materials/Bangers SDF"), which no code registers: whether they are found,
        // whether OnSpriteAssetRequest / OnFontAssetRequest are raised for them first, and
        // whether they are registered afterwards. Also how an unfound font tag's closing tag
        // reads.
        IEnumerator ResourceTags(Transform root)
        {
            L("res paths font=" + Esc(TMP_Settings.defaultFontAssetPath) + " sprite=" + Esc(TMP_Settings.defaultSpriteAssetPath));
            L("res initially " + TryFont("Bangers SDF") + " " + TryFont("LiberationSans SDF"));
            {
                TMP_SpriteAsset got;
                L("res initially TryGetSpriteAsset(oni_sprite_assets)=" + MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("oni_sprite_assets"), out got));
            }
            var go = new GameObject("tg-res", typeof(RectTransform));
            go.transform.SetParent(root, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font; t.fontSize = 14;
            t.rectTransform.sizeDelta = new Vector2(600, 40);
            yield return null;
            TMP_Text.OnSpriteAssetRequest += Request;
            var fontEvent = typeof(TMP_Text).GetEvent("OnFontAssetRequest");
            Delegate fontHandler = null;
            if (fontEvent != null)
            {
                fontHandler = Delegate.CreateDelegate(fontEvent.EventHandlerType, typeof(TmpGaps).GetMethod(nameof(FontRequest), BindingFlags.Static | BindingFlags.NonPublic));
                fontEvent.AddEventHandler(null, fontHandler);
            }
            L("res font event=" + (fontEvent != null));
            void Case(string label, string text)
            {
                requests.Clear();
                fontRequests.Clear();
                t.text = text;
                t.ForceMeshUpdate();
                L("res " + label + " text=" + Esc(text) + " " + Glyphs(t) + " " + FontsOf(t) + " " + MaterialsOf(t)
                    + " spriteRequests=" + (requests.Count == 0 ? "-" : string.Join(" ", requests.ToArray()))
                    + " fontRequests=" + (fontRequests.Count == 0 ? "-" : string.Join(" ", fontRequests.ToArray())));
            }
            Case("sprite game", "a<sprite=\"oni_sprite_assets\" name=\"oni_sprite_assets_germs\">b");
            Case("sprite game other-case", "a<sprite=\"ONI_SPRITE_ASSETS\" name=\"oni_sprite_assets_fresh\">b");
            Case("sprite game index", "a<sprite=\"oni_sprite_assets\" index=1>b");
            Case("sprite resources EmojiOne", "a<sprite=\"EmojiOne\" index=0>b");
            {
                TMP_SpriteAsset got;
                bool ok = MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("oni_sprite_assets"), out got);
                L("res after TryGetSpriteAsset(oni_sprite_assets)=" + ok + (got == null ? "" : " " + Esc(got.name)));
                ok = MaterialReferenceManager.TryGetSpriteAsset(TMP_TextUtilities.GetHashCode("EmojiOne"), out got);
                L("res after TryGetSpriteAsset(EmojiOne)=" + ok);
            }
            Case("font game", "a<font=\"Bangers SDF\" material=\"Bangers SDF - Drop Shadow\">BANG</font>c");
            Case("font game no-material", "a<font=\"Bangers SDF\">BANG</font>c");
            Case("font game other-case", "a<font=\"bangers sdf\">BANG</font>c");
            Case("font resources LiberationSans", "a<font=\"LiberationSans SDF\">b</font>c");
            L("res after " + TryFont("Bangers SDF") + " " + TryFont("bangers sdf") + " " + TryFont("LiberationSans SDF"));
            {
                Material m;
                bool ok = MaterialReferenceManager.TryGetMaterial(TMP_TextUtilities.GetHashCode("Bangers SDF - Drop Shadow"), out m);
                L("res after TryGetMaterial(hash(Bangers SDF - Drop Shadow))=" + ok);
            }
            // Unfound fonts: the closing tag, and a closing tag with nothing open.
            Case("font unknown closing", "a<font=\"ProbeNoSuchFont\">b</font>c");
            Case("font lone closing", "ab</font>c");
            Case("font unknown nested", "a<font=\"default\">b<font=\"ProbeNoSuchFont\">c</font>d</font>e");
            Case("material resources", "a<material=\"LiberationSans SDF - Outline\">b</material>c");
            TMP_Text.OnSpriteAssetRequest -= Request;
            if (fontHandler != null) fontEvent.RemoveEventHandler(null, fontHandler);
            Destroy(go);
            yield return null;
        }
    }
}
