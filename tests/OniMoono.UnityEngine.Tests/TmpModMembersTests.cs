using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    // Package members Workshop mods patch, read by reflection or from a subclass, and call
    // (docs/cleanroom/textmeshpro.md, "Package members mods reach").
    public class TmpModMembersTests : EngineTest
    {
        public TmpModMembersTests() => OniMoono.Engine.UI.CanvasSystem.Install();

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static Event Key(KeyCode key, char character = '\0', EventModifiers modifiers = 0) =>
            new() { type = EventType.KeyDown, keyCode = key, character = character, modifiers = modifiers };

        // Keys through the event system, a frame each.
        static void Keys(params Event[] events)
        {
            foreach (var e in events)
            {
                Event.Enqueue(e);
                Frame();
            }
        }

        static void Focus(TMP_InputField f)
        {
            EventSystem.current.SetSelectedGameObject(f.gameObject);
            f.ActivateInputField();
            Frame();
            Frame();
            Assert.True(f.isFocused);
        }

        static RectTransform CaretRect(TMP_InputField f) => (RectTransform)f.textViewport.Find("Caret");

        static bool SameRect(RectTransform a, RectTransform b) =>
            a.localPosition == b.localPosition && a.localRotation == b.localRotation && a.localScale == b.localScale
            && a.anchorMin == b.anchorMin && a.anchorMax == b.anchorMax && a.anchoredPosition == b.anchoredPosition
            && a.sizeDelta == b.sizeDelta && a.pivot == b.pivot;

        // ----- Where AssignPositioningIfNeeded is called -----

        static readonly MethodInfo AssignPositioning =
            typeof(TMP_InputField).GetMethod("AssignPositioningIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        static readonly List<TMP_InputField> assignCalls = new();
        static readonly List<bool> assignEntryDiffered = new();

        // As PLib's prefix takes them; it lets the method run.
        static void CountingPrefix(TMP_InputField __instance, RectTransform ___caretRectTrans, TMP_Text ___m_TextComponent)
        {
            assignCalls.Add(__instance);
            assignEntryDiffered.Add(___caretRectTrans != null && !SameRect(___caretRectTrans, ___m_TextComponent.rectTransform));
        }

        static void WithCountedAssign(Action body)
        {
            assignCalls.Clear();
            assignEntryDiffered.Clear();
            var harmony = new Harmony("oni-moono.tests.assign-points");
            harmony.Patch(AssignPositioning, prefix: new HarmonyMethod(typeof(TmpModMembersTests).GetMethod(nameof(CountingPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            try { body(); }
            finally { harmony.UnpatchAll(harmony.Id); }
        }

        [Fact]
        public void AssignPositioningKeepsItsSignature()
        {
            Assert.NotNull(AssignPositioning);
            Assert.True(AssignPositioning.IsPrivate);
            Assert.False(AssignPositioning.IsVirtual);
            Assert.Equal(typeof(void), AssignPositioning.ReturnType);
        }

        [Fact]
        public void ScrollingASingleLineToTheCaretRealignsTheCaret()
        {
            WithCountedAssign(() =>
            {
                var f = TmpInputTests.NewField(160f);
                Assert.Single(assignCalls);
                Focus(f);
                var text = f.textComponent.rectTransform;
                f.text = "a much longer text that scrolls out of the field to the right";
                Frame();
                Assert.Single(assignCalls);
                Keys(Key(KeyCode.End));
                // The rebuild moved the text left to show the caret, then placed the caret.
                Assert.True(text.anchoredPosition.x < 0f);
                Assert.Equal(2, assignCalls.Count);
                Assert.True(assignEntryDiffered[1]);
                Assert.True(SameRect(text, CaretRect(f)));
                // Frames with the caret in view, the blink included, move nothing.
                for (int i = 0; i < 90; i++) Frame();
                Assert.Equal(2, assignCalls.Count);
            });
        }

        [Fact]
        public void EditsThatLeaveTheTextWhereItIsDoNotRealignTheCaret()
        {
            WithCountedAssign(() =>
            {
                var f = TmpInputTests.NewField(160f);
                f.text = "abc";
                Focus(f);
                f.stringPosition = 3;
                Frame();
                Keys(Key(KeyCode.None, 'd'));
                Keys(Key(KeyCode.A, 'a', EventModifiers.Control));
                for (int i = 0; i < 60; i++) Frame();
                f.ForceLabelUpdate();
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)f.transform);
                f.DeactivateInputField();
                Frame();
                f.enabled = false;
                f.enabled = true;
                Frame();
                // Only OnEnable's call when the caret was made.
                Assert.Single(assignCalls);
            });
        }

        [Fact]
        public void AMultiLineTextScrolledToTheCaretRealignsTheCaret()
        {
            WithCountedAssign(() =>
            {
                var f = TmpInputTests.NewField(160f);
                ((RectTransform)f.transform).sizeDelta = new Vector2(160f, 40f);
                f.lineType = TMP_InputField.LineType.MultiLineNewline;
                f.text = "one\ntwo\nthree\nfour\nfive";
                Focus(f);
                var text = f.textComponent.rectTransform;
                Assert.Equal(0f, text.anchoredPosition.y);
                f.ProcessEvent(Key(KeyCode.End, '\0', EventModifiers.Control));
                Assert.Equal(f.text.Length, f.stringPosition);
                Frame();
                // Moved up to show the last line.
                Assert.True(text.anchoredPosition.y > 0f);
                Assert.Equal(2, assignCalls.Count);
                Assert.True(SameRect(text, CaretRect(f)));
                Frame();
                Assert.Equal(2, assignCalls.Count);
            });
        }

        [Fact]
        public void TheVerticalScrollbarsChangeRealignsTheCaret()
        {
            WithCountedAssign(() =>
            {
                var f = TmpInputTests.NewField(160f);
                ((RectTransform)f.transform).sizeDelta = new Vector2(160f, 40f);
                f.lineType = TMP_InputField.LineType.MultiLineNewline;
                var barGo = new GameObject("Scrollbar", typeof(RectTransform));
                barGo.transform.SetParent(f.transform, false);
                var bar = barGo.AddComponent<Scrollbar>();
                bar.direction = Scrollbar.Direction.TopToBottom;
                f.verticalScrollbar = bar;
                // The field listens to a scrollbar it has when enabled.
                f.gameObject.SetActive(false);
                f.gameObject.SetActive(true);
                f.text = "one\ntwo\nthree\nfour\nfive";
                Focus(f);
                int before = assignCalls.Count;
                var text = f.textComponent.rectTransform;
                bar.value = 0.5f;
                Assert.True(assignCalls.Count > before);
                Assert.True(text.anchoredPosition.y > 0f);
                Assert.True(SameRect(text, CaretRect(f)));
                // The caret's blink leaves the scrolled text where it is.
                float scrolled = text.anchoredPosition.y;
                before = assignCalls.Count;
                for (int i = 0; i < 60; i++) Frame();
                Assert.Equal(scrolled, text.anchoredPosition.y);
                Assert.Equal(before, assignCalls.Count);
                // Not called for a value set without notifying.
                bar.SetValueWithoutNotify(0.1f);
                Frame();
                Assert.Equal(before, assignCalls.Count);
            });
        }

        [Fact]
        public void ACaretDriftsFromATextMovedFromCode()
        {
            WithCountedAssign(() =>
            {
                var f = TmpInputTests.NewField(160f);
                var text = f.textComponent.rectTransform;
                text.anchoredPosition = new Vector2(5f, 3f);
                text.sizeDelta = new Vector2(-20f, -4f);
                Frame();
                Focus(f);
                Keys(Key(KeyCode.None, 'x'));
                Frame();
                Assert.Single(assignCalls);
                Assert.False(SameRect(text, CaretRect(f)));
            });
        }

        // The caret's quads are in the text's local space, drawn in the caret's rect: a caret
        // whose rect stayed at its defaults draws them away from the text.
        [Fact]
        public void TheCaretIsDrawnInItsOwnRect()
        {
            var f = TmpInputTests.NewField(160f);
            Focus(f);
            var caret = CaretRect(f);
            var mesh = MeshOf(f);
            Assert.Equal(4, mesh.vertexCount);
            var aligned = mesh.vertices;
            caret.anchoredPosition += new Vector2(30f, 0f);
            f.ForceLabelUpdate();
            Frame();
            Assert.Equal(aligned, MeshOf(f).vertices);
        }

        // ----- m_Mesh -----

        static readonly FieldInfo MeshField = typeof(TMP_InputField).GetField("m_Mesh", Instance);

        static Mesh MeshOf(TMP_InputField f) => (Mesh)MeshField.GetValue(f);

        // A subclass reads the field as Applied Logistics Network's does.
        sealed class DiagnosticField : TMP_InputField
        {
            public int CaretVertices => m_Mesh != null ? m_Mesh.vertexCount : -1;
            public Mesh Property => mesh;
        }

        [Fact]
        public void TheCaretMeshHasTheShippedMembers()
        {
            Assert.NotNull(MeshField);
            Assert.True(MeshField.IsFamily);
            Assert.Equal(typeof(Mesh), MeshField.FieldType);
            var property = typeof(TMP_InputField).GetProperty("mesh", Instance);
            Assert.NotNull(property);
            Assert.True(property.GetMethod.IsFamily);
            Assert.Null(property.SetMethod);
            Assert.Equal(typeof(Mesh), property.PropertyType);
            var go = new GameObject("Diagnostic", typeof(RectTransform));
            var d = go.AddComponent<DiagnosticField>();
            Assert.Equal(-1, d.CaretVertices);
        }

        [Fact]
        public void TheCaretMeshFollowsTheCaretsLife()
        {
            var f = TmpInputTests.NewField(160f);
            f.customCaretColor = true;
            f.caretColor = new Color32(10, 20, 30, 255);
            f.selectionColor = new Color32(200, 100, 50, 128);
            // Null until the first rebuild after enabling.
            Assert.Null(MeshOf(f));
            Frame();
            var mesh = MeshOf(f);
            Assert.NotNull(mesh);
            Assert.Equal("", mesh.name);
            Assert.Equal(HideFlags.None, mesh.hideFlags);
            Assert.Equal(0, mesh.vertexCount);
            // Focused: one quad, the caret's width by the line's height, in the caret colour.
            Focus(f);
            Assert.Same(mesh, MeshOf(f));
            Assert.Equal(4, mesh.vertexCount);
            Assert.Equal(6, mesh.triangles.Length);
            var b = mesh.bounds;
            Assert.Equal(1f, b.size.x, 3);
            Assert.True(b.size.y > 5f);
            Assert.All(mesh.colors32, c => Assert.Equal(new Color32(10, 20, 30, 255), c));
            // Its corners: bottom left, top left, top right, bottom right.
            var v = mesh.vertices;
            Assert.Equal(v[0].x, v[1].x);
            Assert.True(v[1].y > v[0].y);
            Assert.Equal(v[2].y, v[1].y);
            // A selection: one quad in the selection colour, bottom left, bottom right, top right,
            // top left.
            f.text = "dabc";
            f.selectionStringAnchorPosition = 1;
            f.selectionStringFocusPosition = 3;
            Frame();
            Assert.Same(mesh, MeshOf(f));
            Assert.Equal(4, mesh.vertexCount);
            Assert.All(mesh.colors32, c => Assert.Equal(new Color32(200, 100, 50, 128), c));
            Assert.True(mesh.bounds.size.x > 5f);
            v = mesh.vertices;
            Assert.Equal(v[0].y, v[1].y);
            Assert.True(v[1].x > v[0].x);
            Assert.Equal(v[2].x, v[1].x);
            // Ended: nothing from the next rebuild.
            f.DeactivateInputField();
            Frame();
            Assert.Same(mesh, MeshOf(f));
            Assert.Equal(0, mesh.vertexCount);
            // Disabling drops it; the next enable leaves it null until a rebuild makes a new one.
            f.gameObject.SetActive(false);
            Assert.Null(MeshOf(f));
            f.gameObject.SetActive(true);
            Assert.Null(MeshOf(f));
            Frame();
            Assert.NotNull(MeshOf(f));
            Assert.NotSame(mesh, MeshOf(f));
        }

        // ----- KeyPressed -----

        sealed class KeyField : TMP_InputField
        {
            // A subclass can call it.
            public void Press(Event e) => KeyPressed(e);
        }

        [Fact]
        public void KeyPressedIsProtectedAndNotVirtual()
        {
            var m = typeof(TMP_InputField).GetMethod("KeyPressed", Instance, null, new[] { typeof(Event) }, null);
            Assert.NotNull(m);
            Assert.True(m.IsFamily);
            Assert.False(m.IsVirtual);
            Assert.False(m.IsStatic);
            Assert.Equal("evt", m.GetParameters()[0].Name);
            Assert.NotNull(typeof(KeyField).GetMethod(nameof(KeyField.Press)));
        }

        // ----- TMP_Dropdown.m_Dropdown -----

        static readonly FieldInfo DropdownField = typeof(TMP_Dropdown).GetField("m_Dropdown", Instance);

        static GameObject ListOf(TMP_Dropdown d) => (GameObject)DropdownField.GetValue(d);

        // A dropdown with three options and a template laid out as uGUI documents it: Template
        // (Image, ScrollRect) > Viewport (Image, Mask) > Content > Item (Toggle) with a
        // background, a checkmark and a label.
        static TMP_Dropdown NewDropdown()
        {
            TmpTestSupport.LoadGameData();
            if (EventSystem.current == null) new GameObject("EventSystem", typeof(EventSystem), typeof(TmpInputTests.SelectedUpdateModule));
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<GraphicRaycaster>();
            var root = new GameObject("tgdd", typeof(RectTransform));
            root.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)root.transform).sizeDelta = new Vector2(160f, 30f);
            root.SetActive(false);
            root.AddComponent<Image>();
            var dropdown = root.AddComponent<TMP_Dropdown>();
            var caption = Child<TextMeshProUGUI>(root, "Label");
            caption.font = TmpTestSupport.Graystroke;
            var template = Child<Image>(root, "Template").gameObject;
            var templateRect = (RectTransform)template.transform;
            templateRect.anchorMin = new Vector2(0f, 0f);
            templateRect.anchorMax = new Vector2(1f, 0f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.sizeDelta = new Vector2(0f, 150f);
            var scroll = template.AddComponent<ScrollRect>();
            var viewport = Child<Image>(template, "Viewport");
            viewport.gameObject.AddComponent<Mask>();
            var viewportRect = viewport.rectTransform;
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 28f);
            scroll.content = contentRect;
            scroll.viewport = viewportRect;
            var item = new GameObject("Item", typeof(RectTransform));
            item.transform.SetParent(content.transform, false);
            var itemRect = (RectTransform)item.transform;
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.sizeDelta = new Vector2(0f, 20f);
            var toggle = item.AddComponent<Toggle>();
            toggle.targetGraphic = Child<Image>(item, "Item Background");
            toggle.graphic = Child<Image>(item, "Item Checkmark");
            var itemLabel = Child<TextMeshProUGUI>(item, "Item Label");
            itemLabel.font = TmpTestSupport.Graystroke;
            dropdown.template = templateRect;
            dropdown.captionText = caption;
            dropdown.itemText = itemLabel;
            template.SetActive(false);
            root.SetActive(true);
            dropdown.options = new List<TMP_Dropdown.OptionData> { new("Alpha"), new("Beta"), new("Gamma") };
            Frame();
            return dropdown;
        }

        static T Child<T>(GameObject parent, string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go.AddComponent<T>();
        }

        // Frames until the field clears; the count of frames.
        static int FramesUntilCleared(TMP_Dropdown d, GameObject list)
        {
            for (int i = 1; i <= 60; i++)
            {
                Frame();
                if (ListOf(d) == null) return i;
                Assert.Same(list, ListOf(d));
                Assert.True(d.IsExpanded);
            }
            return -1;
        }

        [Fact]
        public void TheShownListIsInMDropdown()
        {
            Assert.NotNull(DropdownField);
            Assert.True(DropdownField.IsPrivate);
            Assert.Equal(typeof(GameObject), DropdownField.FieldType);
            Assert.NotNull(typeof(TMP_Dropdown).GetField("m_Blocker", Instance));
            var d = NewDropdown();
            Assert.Null(ListOf(d));
            Assert.False(d.IsExpanded);
            d.Show();
            var list = ListOf(d);
            Assert.NotNull(list);
            Assert.True(d.IsExpanded);
            Assert.Equal("Dropdown List", list.name);
            Assert.Same(d.transform, list.transform.parent);
            Assert.True(list.activeSelf);
            Assert.Equal(new[] { "RectTransform", "CanvasRenderer", "Image", "ScrollRect", "Canvas", "GraphicRaycaster", "CanvasGroup" },
                list.GetComponents<Component>().Select(c => c.GetType().Name).ToArray());
            var content = list.GetComponent<ScrollRect>().content;
            var toggles = content.GetComponentsInChildren<Toggle>(true);
            Assert.Equal(4, toggles.Length);
            Assert.Equal(new[] { "Item 0: Alpha", "Item 1: Beta", "Item 2: Gamma" }, toggles.Where(t => t.gameObject.activeSelf).Select(t => t.name).ToArray());
            // A second Show keeps it.
            d.Show();
            Assert.Same(list, ListOf(d));
            // Hide fades it out with the field still set, then clears it; the list goes a frame
            // later.
            Frame();
            d.Hide();
            Assert.Same(list, ListOf(d));
            Assert.True(d.IsExpanded);
            int frames = FramesUntilCleared(d, list);
            Assert.InRange(frames, 2, 30);
            Assert.False(d.IsExpanded);
            Frame();
            Assert.True(list == null);
        }

        [Fact]
        public void ChoosingAnItemSendsItsChangeWithTheListStillShown()
        {
            var d = NewDropdown();
            d.Show();
            var list = ListOf(d);
            GameObject atChange = null;
            bool expandedAtChange = false;
            d.onValueChanged.AddListener(_ =>
            {
                atChange = ListOf(d);
                expandedAtChange = d.IsExpanded;
            });
            var toggles = list.GetComponent<ScrollRect>().content.GetComponentsInChildren<Toggle>();
            toggles[1].isOn = true;
            Assert.Same(list, atChange);
            Assert.True(expandedAtChange);
            Assert.True(FramesUntilCleared(d, list) > 0);
            // value set from code while shown leaves it shown.
            d.Show();
            var second = ListOf(d);
            atChange = null;
            d.value = 2;
            Assert.Same(second, atChange);
            Frame();
            Frame();
            Assert.Same(second, ListOf(d));
        }

        [Fact]
        public void AShowAfterAHideTakesANewList()
        {
            var d = NewDropdown();
            d.Show();
            var first = ListOf(d);
            Frame();
            d.Hide();
            Frame();
            d.Show();
            var second = ListOf(d);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.True(first != null);
            // The old list finishes its fade and goes; the field keeps the new one.
            for (int i = 0; i < 30; i++) Frame();
            Assert.True(first == null);
            Assert.Same(second, ListOf(d));
            Assert.True(d.IsExpanded);
            // Show and Hide in one frame: set, and cleared when the fade ends.
            d.Hide();
            Assert.True(FramesUntilCleared(d, second) > 0);
            d.Show();
            var third = ListOf(d);
            d.Hide();
            Assert.Same(third, ListOf(d));
            Assert.True(FramesUntilCleared(d, third) > 0);
        }

        [Fact]
        public void DisablingTheDropdownClearsTheFieldAtOnce()
        {
            var d = NewDropdown();
            d.Show();
            var list = ListOf(d);
            d.gameObject.SetActive(false);
            Assert.Null(ListOf(d));
            Assert.False(d.IsExpanded);
            Assert.False(list.activeInHierarchy);
            Frame();
            Assert.True(list == null);
            d.gameObject.SetActive(true);
            d.Show();
            list = ListOf(d);
            d.enabled = false;
            Assert.Null(ListOf(d));
            Assert.False(d.IsExpanded);
            Assert.True(list != null);
        }

        // ----- TMP_FontAsset.HasCharacters(string, out uint[], bool, bool) -----

        static TMP_FontAsset NewDynamic(AtlasPopulationMode mode = AtlasPopulationMode.Dynamic)
        {
            var font = TmpTestSupport.Load<Font>("sharedassets0.assets", "LiberationSans");
            var asset = TMP_FontAsset.CreateFontAsset(font, 48, 5, GlyphRenderMode.SDFAA, 256, 256, AtlasPopulationMode.Dynamic, true);
            asset.atlasPopulationMode = mode;
            return asset;
        }

        // Runs with TMP_Settings' fallbacks and default font asset as given, restoring them.
        static void WithSettings(List<TMP_FontAsset> fallbacks, TMP_FontAsset defaultFont, Action body)
        {
            TmpTestSupport.LoadGameData();
            var oldFallbacks = TMP_Settings.fallbackFontAssets;
            var oldDefault = TMP_Settings.defaultFontAsset;
            TMP_Settings.fallbackFontAssets = fallbacks;
            TMP_Settings.defaultFontAsset = defaultFont;
            try { body(); }
            finally
            {
                TMP_Settings.fallbackFontAssets = oldFallbacks;
                TMP_Settings.defaultFontAsset = oldDefault;
            }
        }

        [Fact]
        public void HasCharactersHasTheShippedOverload()
        {
            var m = typeof(TMP_FontAsset).GetMethod("HasCharacters", new[] { typeof(string), typeof(uint[]).MakeByRefType(), typeof(bool), typeof(bool) });
            Assert.NotNull(m);
            var p = m.GetParameters();
            Assert.Equal(new[] { "text", "missingCharacters", "searchFallbacks", "tryAddCharacter" }, p.Select(x => x.Name).ToArray());
            Assert.True(p[1].IsOut);
            Assert.False((bool)p[2].DefaultValue);
            Assert.False((bool)p[3].DefaultValue);
        }

        [Fact]
        public void HasCharactersListsTheMissingCodePoints()
        {
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            Assert.False(noto.HasCharacters("a一一b丁", out uint[] missing));
            Assert.Equal(new uint[] { 0x4E00, 0x4E00, 0x4E01 }, missing);
            Assert.True(noto.HasCharacters("abc", out missing));
            Assert.Null(missing);
            Assert.True(noto.HasCharacters("", out missing));
            Assert.Null(missing);
            Assert.False(noto.HasCharacters("x😀y", out missing));
            Assert.Equal(new uint[] { 0x1F600 }, missing);
            Assert.False(noto.HasCharacters("x\uD83Dy", out missing));
            Assert.Equal(new uint[] { 0xD83D }, missing);
            Assert.Throws<NullReferenceException>(() => noto.HasCharacters(null, out missing));
            // Control characters are never missing; spaces are looked up like any other.
            var empty = NewDynamic(AtlasPopulationMode.Static);
            Assert.True(empty.HasCharacters("\u0003\t\n\r", out missing));
            Assert.False(empty.HasCharacters("  ", out missing));
            Assert.Equal(new uint[] { 0x20, 0xA0 }, missing);
        }

        [Fact]
        public void HasCharactersSearchesTheFallbacksThenTheSettings()
        {
            TmpTestSupport.LoadGameData();
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            // The game's global fallbacks hold the CJK fonts.
            Assert.True(noto.HasCharacters("一", out uint[] missing, true));
            Assert.Null(missing);
            Assert.False(noto.HasCharacters("一", out missing, false));
            WithSettings(new List<TMP_FontAsset>(), TMP_Settings.defaultFontAsset, () =>
            {
                Assert.False(noto.HasCharacters("一", out missing, true));
                // An empty asset finds "abc" in the default font asset.
                var empty = NewDynamic(AtlasPopulationMode.Static);
                Assert.True(empty.HasCharacters("abc", out missing, true));
            });
            WithSettings(new List<TMP_FontAsset>(), null, () =>
            {
                var empty = NewDynamic(AtlasPopulationMode.Static);
                Assert.False(empty.HasCharacters("abc", out missing, true));
                Assert.Equal(new uint[] { 0x61, 0x62, 0x63 }, missing);
                // A fallback's own fallback is searched.
                var middle = NewDynamic(AtlasPopulationMode.Static);
                middle.fallbackFontAssetTable = new List<TMP_FontAsset> { noto };
                empty.fallbackFontAssetTable = new List<TMP_FontAsset> { middle };
                Assert.True(empty.HasCharacters("abc", out missing, true));
                Assert.False(empty.HasCharacters("abc", out missing, false));
            });
        }

        [Fact]
        public void HasCharactersAddsToTheFirstDynamicAssetWhoseFaceHasThem()
        {
            WithSettings(new List<TMP_FontAsset>(), null, () =>
            {
                var own = NewDynamic();
                Assert.True(own.HasCharacters("eé", out uint[] missing, true, true));
                Assert.Null(missing);
                Assert.Equal(2, own.characterTable.Count);
                Assert.True(own.HasCharacters("e", out missing, true, true));
                Assert.Equal(2, own.characterTable.Count);
                // Without adding, nothing changes anywhere.
                var other = NewDynamic();
                Assert.False(other.HasCharacters("q", out missing, true, false));
                Assert.Empty(other.characterTable);
                // A static asset adds nothing; its dynamic fallback, and a nested one, do.
                var host = NewDynamic(AtlasPopulationMode.Static);
                var fallback = NewDynamic(AtlasPopulationMode.Static);
                var nested = NewDynamic();
                fallback.fallbackFontAssetTable = new List<TMP_FontAsset> { nested };
                host.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
                Assert.True(host.HasCharacters("q", out missing, true, true));
                Assert.Empty(host.characterTable);
                Assert.Empty(fallback.characterTable);
                Assert.Single(nested.characterTable);
                // Characters the face lacks stay missing and are not added.
                Assert.False(own.HasCharacters("一", out missing, true, true));
                Assert.Equal(new uint[] { 0x4E00 }, missing);
                Assert.Equal(2, own.characterTable.Count);
            });
        }

        // ----- TMP_FontAsset.HasCharacter(char, bool, bool) -----
        // Measured on the shipped package (UnityProbe fontfallback, lines "FF chain"): with
        // searchFallbacks the asset's fallbacks, TMP_Settings' fallbacks and the default font
        // asset are searched, each with its own fallbacks; a null entry ends its list.

        [Fact]
        public void HasCharacterSearchesTheSettingsFallbacksAndTheDefault()
        {
            TmpTestSupport.LoadGameData();
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            var empty = NewDynamic(AtlasPopulationMode.Static);
            // As the game has them: the CJK fonts are global fallbacks, LiberationSans the default.
            Assert.False(empty.HasCharacter('a'));
            Assert.False(empty.HasCharacter((int)'a'));
            Assert.True(empty.HasCharacter('a', true));
            Assert.True(empty.HasCharacter('一', true));
            Assert.True(empty.HasCharacter(' ', true));
            Assert.True(empty.HasCharacter(' ', true));
            WithSettings(new List<TMP_FontAsset>(), TMP_Settings.defaultFontAsset, () =>
            {
                Assert.True(empty.HasCharacter('a', true));
                Assert.False(empty.HasCharacter('一', true));
            });
            WithSettings(TMP_Settings.fallbackFontAssets, null, () => Assert.True(empty.HasCharacter('一', true)));
            WithSettings(new List<TMP_FontAsset>(), null, () => Assert.False(empty.HasCharacter('a', true)));
            WithSettings(null, null, () => Assert.False(empty.HasCharacter('a', true)));
            WithSettings(new List<TMP_FontAsset>(), noto, () => Assert.True(empty.HasCharacter('a', true)));
            // A global's and the default's own fallbacks are searched too.
            var middle = NewDynamic(AtlasPopulationMode.Static);
            middle.fallbackFontAssetTable = new List<TMP_FontAsset> { noto };
            WithSettings(new List<TMP_FontAsset> { middle }, null, () => Assert.True(empty.HasCharacter('a', true)));
            WithSettings(new List<TMP_FontAsset>(), middle, () => Assert.True(empty.HasCharacter('a', true)));
            // The asset itself among the globals.
            WithSettings(new List<TMP_FontAsset> { empty, noto }, null, () => Assert.True(empty.HasCharacter('a', true)));
        }

        [Fact]
        public void ANullFallbackEndsItsListsSearch()
        {
            TmpTestSupport.LoadGameData();
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            var empty = NewDynamic(AtlasPopulationMode.Static);
            var middle = NewDynamic(AtlasPopulationMode.Static);
            WithSettings(new List<TMP_FontAsset>(), null, () =>
            {
                empty.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                Assert.False(empty.HasCharacter('a', true));
                Assert.False(empty.HasCharacters("a", out uint[] missing, true));
                Assert.Equal(new uint[] { 0x61 }, missing);
                middle.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
                empty.fallbackFontAssetTable = new List<TMP_FontAsset> { middle };
                Assert.False(empty.HasCharacter('a', true));
                // The list around it goes on.
                middle.fallbackFontAssetTable = new List<TMP_FontAsset> { null };
                empty.fallbackFontAssetTable = new List<TMP_FontAsset> { middle, noto };
                Assert.True(empty.HasCharacter('a', true));
            });
            // So do the global fallbacks and the default after one in the asset's own list.
            WithSettings(new List<TMP_FontAsset> { noto }, null, () =>
            {
                empty.fallbackFontAssetTable = new List<TMP_FontAsset> { null, middle };
                Assert.True(empty.HasCharacter('a', true));
            });
            empty.fallbackFontAssetTable = new List<TMP_FontAsset>();
            WithSettings(new List<TMP_FontAsset> { null }, noto, () => Assert.True(empty.HasCharacter('a', true)));
            WithSettings(new List<TMP_FontAsset> { null, noto }, null, () =>
            {
                Assert.False(empty.HasCharacter('a', true));
                Assert.False(empty.HasCharacters("a", out uint[] missing, true));
            });
            middle.fallbackFontAssetTable = new List<TMP_FontAsset> { null, noto };
            WithSettings(new List<TMP_FontAsset>(), middle, () => Assert.False(empty.HasCharacter('a', true)));
        }

        [Fact]
        public void HasCharacterAddsToTheFirstDynamicAssetInTheSearch()
        {
            TmpTestSupport.LoadGameData();
            var noto = TmpTestSupport.FindAsset<TMP_FontAsset>("NotoSans-Regular");
            var empty = NewDynamic(AtlasPopulationMode.Static);
            var global = NewDynamic();
            var def = NewDynamic();
            var own = NewDynamic();
            WithSettings(new List<TMP_FontAsset> { global }, def, () =>
            {
                Assert.False(empty.HasCharacter('q', true));
                Assert.Empty(global.characterTable);
                Assert.True(empty.HasCharacter('q', true, true));
                Assert.Single(global.characterTable);
                Assert.Empty(def.characterTable);
                // A character no face has is added nowhere.
                Assert.False(empty.HasCharacter('一', true, true));
                Assert.Single(global.characterTable);
                // The asset's own fallbacks come first.
                empty.fallbackFontAssetTable = new List<TMP_FontAsset> { own };
                Assert.True(empty.HasCharacter('r', true, true));
                Assert.Single(own.characterTable);
                Assert.Single(global.characterTable);
                empty.fallbackFontAssetTable = new List<TMP_FontAsset>();
            });
            WithSettings(new List<TMP_FontAsset>(), def, () =>
            {
                Assert.True(empty.HasCharacter('s', true, true));
                Assert.Single(def.characterTable);
            });
            // A dynamic asset adds before a later asset's table is looked in.
            var first = NewDynamic();
            WithSettings(new List<TMP_FontAsset> { first, noto }, null, () =>
            {
                Assert.True(empty.HasCharacter('u', true, true));
                Assert.Single(first.characterTable);
            });
        }

        // ----- TextMeshProUGUI.canvasRenderer -----

        [Fact]
        public void TheTextDeclaresItsCanvasRendererGetter()
        {
            var p = typeof(TextMeshProUGUI).GetProperty("canvasRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.NotNull(p);
            Assert.Equal(typeof(CanvasRenderer), p.PropertyType);
            Assert.Null(p.SetMethod);
            Assert.False(p.GetMethod.IsVirtual);
            Assert.NotNull(typeof(TextMeshProUGUI).GetField("m_canvasRenderer", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.Null(typeof(TMP_SubMeshUI).GetProperty("canvasRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

            // Before Awake, on an inactive object, read first or after Graphic's.
            TmpTestSupport.LoadGameData();
            var go = new GameObject("t", typeof(RectTransform));
            go.SetActive(false);
            var t = go.AddComponent<TextMeshProUGUI>();
            var own = t.canvasRenderer;
            Assert.NotNull(own);
            Assert.Same(go.GetComponent<CanvasRenderer>(), own);
            Assert.Same(((Graphic)t).canvasRenderer, own);
            var go2 = new GameObject("t2", typeof(RectTransform));
            go2.SetActive(false);
            var t2 = go2.AddComponent<TextMeshProUGUI>();
            var graphics = ((Graphic)t2).canvasRenderer;
            Assert.Same(graphics, t2.canvasRenderer);
            go.SetActive(true);
            t.text = "x";
            t.ForceMeshUpdate();
            Assert.Same(go.GetComponent<CanvasRenderer>(), t.canvasRenderer);
            Assert.Same(own, p.GetValue(t));
        }

        // ----- Hashes -----

        [Theory]
        [InlineData("", 0)]
        [InlineData("a", 65)]
        [InlineData("A", 65)]
        [InlineData("ab", 2083)]
        [InlineData("abc", 68800)]
        [InlineData("ABC", 68800)]
        [InlineData("aBc", 68800)]
        [InlineData("a b", 69667)]
        [InlineData("sip_divider", -197458124)]
        [InlineData("SIP_DIVIDER", -197458124)]
        [InlineData("NotoSans-Regular", 382882690)]
        [InlineData("😀", 1769949)]
        [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", 1902480570)]
        public void GetHashCodeFoldsAsciiCase(string s, int expected) => Assert.Equal(expected, TMP_TextUtilities.GetHashCode(s));

        [Fact]
        public void TheOtherHashesAsMeasured()
        {
            Assert.Equal(0, TMP_TextUtilities.GetHashCode(null));
            // Only ASCII changes case.
            Assert.Equal(233, TMP_TextUtilities.GetHashCode("é"));
            Assert.Equal(305, TMP_TextUtilities.GetHashCode("ı"));
            Assert.Equal(223, TMP_TextUtilities.GetHashCode("ß"));
            Assert.Equal(97, TMP_TextUtilities.GetSimpleHashCode("a"));
            Assert.Equal(1165012724, TMP_TextUtilities.GetSimpleHashCode("sip_divider"));
            Assert.Throws<NullReferenceException>(() => TMP_TextUtilities.GetSimpleHashCode(null));
            Assert.Equal(4097509172u, TMP_TextUtilities.GetHashCodeCaseInSensitive("sip_divider"));
            Assert.Equal(3238964083u, TMP_TextUtilities.GetHashCodeCaseInSensitive("LiberationSans SDF"));
            Assert.Equal(735819793u, TMP_TextUtilities.GetSimpleHashCodeLowercase("SIP_DIVIDER"));
            // Only GetHashCode takes null (measured).
            Assert.Throws<NullReferenceException>(() => TMP_TextUtilities.GetHashCodeCaseInSensitive(null));
            Assert.Throws<NullReferenceException>(() => TMP_TextUtilities.GetSimpleHashCodeLowercase(null));
            Assert.Equal(5381u, TMP_TextUtilities.GetSimpleHashCodeLowercase(""));
            Assert.Equal(177604u, TMP_TextUtilities.GetSimpleHashCodeLowercase("a"));
            Assert.Equal(177604u, TMP_TextUtilities.GetSimpleHashCodeLowercase("A"));
        }

        [Fact]
        public void AnAssetsHashCodeComesFromItsNameWhenUnset()
        {
            TmpTestSupport.LoadGameData();
            foreach (var name in new[] { "NotoSans-Regular", "GRAYSTROKE REGULAR SDF", "oni_sprite_assets" })
            {
                var asset = name == "oni_sprite_assets" ? (TMP_Asset)TmpTestSupport.FindAsset<TMP_SpriteAsset>(name) : TmpTestSupport.FindAsset<TMP_FontAsset>(name);
                Assert.Equal(TMP_TextUtilities.GetHashCode(name), asset.hashCode);
            }
            var s = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            Assert.Equal(0, s.hashCode);
            s.name = "fresh";
            Assert.Equal(85746282, s.hashCode);
            s.name = "renamed";
            Assert.Equal(85746282, s.hashCode);
            s.hashCode = 12345;
            s.name = "again";
            Assert.Equal(12345, s.hashCode);
        }

        [Fact]
        public void ACreatedFontAssetsMaterialHashCodeIsSetWhenMade()
        {
            var a = NewDynamic();
            string atCreation = a.name;
            Assert.Equal(TMP_TextUtilities.GetSimpleHashCode(atCreation + " Atlas Material"), a.materialHashCode);
            a.name = "Renamed";
            Assert.Equal(TMP_TextUtilities.GetSimpleHashCode(atCreation + " Atlas Material"), a.materialHashCode);
            Assert.Equal(-2006316128, TMP_TextUtilities.GetSimpleHashCode(" Atlas Material"));
        }
    }
}
