using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Xunit;

namespace OniMoono.Tests
{
    // When TMP_InputField writes its label and makes and places its caret
    // (docs/cleanroom/textmeshpro.md, "when the label and the caret are set up, measured").
    public class TmpInputSetupTests : EngineTest
    {
        // The canvas update (layout rebuilds) runs from the canvas system, which the host installs
        // at start; a test run of this class alone would otherwise have none.
        public TmpInputSetupTests() => OniMoono.Engine.UI.CanvasSystem.Install();

        const string Zwsp = "​";

        // Records the label's layout as the field's own LateUpdate leaves it.
        sealed class LateUpdateRecorder : MonoBehaviour
        {
            public TMP_Text Text;
            public int Characters = -1;
            public bool Overflowing;
            void LateUpdate()
            {
                Characters = Text.textInfo?.characterCount ?? 0;
                Overflowing = Text.isTextOverflowing;
            }
        }

        static TMP_InputField NewField(bool assignLater = false) => TmpInputTests.NewField(140f, assignLater);

        [Fact]
        public void EnableWritesTheLabelButLeavesItsLayoutToTheCanvasRebuild()
        {
            var field = NewField();
            var text = field.textComponent;
            Assert.Equal(Zwsp, text.text);
            Assert.Equal(0, text.textInfo?.characterCount ?? 0);
            var recorder = field.gameObject.AddComponent<LateUpdateRecorder>();
            recorder.Text = text;
            int atRender = -1;
            void render() => atRender = text.textInfo.characterCount;
            Canvas.willRenderCanvases += render;
            try { Frame(); }
            finally { Canvas.willRenderCanvases -= render; }
            Assert.Equal(0, recorder.Characters);
            Assert.False(recorder.Overflowing);
            Assert.Equal(1, atRender);
            Assert.Equal(1, text.textInfo.characterCount);
        }

        [Fact]
        public void ATextSetWhileInactiveIsShownOnEnable()
        {
            var field = NewField();
            field.gameObject.SetActive(false);
            field.text = "hello";
            field.gameObject.SetActive(true);
            Assert.Equal("hello" + Zwsp, field.textComponent.text);
        }

        [Fact]
        public void AssigningTheTextComponentLeavesItsTextAlone()
        {
            var field = NewField(assignLater: true);
            Assert.Equal("", field.textComponent.text);
            // Enabled without a text component: no caret, nor once one is assigned.
            Assert.Null(field.textViewport.Find("Caret"));
            Frame();
            Assert.Equal("", field.textComponent.text);
            // Its next enable makes one, and writes the label.
            field.gameObject.SetActive(false);
            field.gameObject.SetActive(true);
            Assert.NotNull(field.textViewport.Find("Caret"));
            Assert.Equal(Zwsp, field.textComponent.text);
        }

        [Fact]
        public void SettingTheTextItHoldsChangesNothing()
        {
            var field = NewField(assignLater: true);
            int changes = 0;
            field.onValueChanged.AddListener(_ => changes++);
            field.text = "";
            Assert.Equal("", field.textComponent.text);
            Assert.Equal(0, changes);
            field.text = "ab";
            Assert.Equal("ab" + Zwsp, field.textComponent.text);
            Assert.Equal(3, field.textComponent.textInfo.characterCount);
            field.text = "";
            Assert.Equal(Zwsp, field.textComponent.text);
            Assert.Equal(2, changes);
        }

        [Fact]
        public void ActivatingAFieldWhoseLabelWasNeverWrittenWritesIt()
        {
            var field = NewField(assignLater: true);
            field.ActivateInputField();
            Assert.Equal("", field.textComponent.text);
            string atRender = null;
            bool focusedAtRender = false;
            void render()
            {
                atRender = field.textComponent.text;
                focusedAtRender = field.isFocused;
            }
            Canvas.willRenderCanvases += render;
            try { Frame(); }
            finally { Canvas.willRenderCanvases -= render; }
            Assert.Equal(Zwsp, atRender);
            Assert.True(focusedAtRender);
        }

        static void AssertSameRect(RectTransform expected, RectTransform actual)
        {
            Assert.Equal(expected.localPosition, actual.localPosition);
            Assert.Equal(expected.localRotation, actual.localRotation);
            Assert.Equal(expected.localScale, actual.localScale);
            Assert.Equal(expected.anchorMin, actual.anchorMin);
            Assert.Equal(expected.anchorMax, actual.anchorMax);
            Assert.Equal(expected.anchoredPosition, actual.anchoredPosition);
            Assert.Equal(expected.sizeDelta, actual.sizeDelta);
            Assert.Equal(expected.pivot, actual.pivot);
        }

        [Fact]
        public void TheCaretTakesTheTextsRectOnEnable()
        {
            var field = NewField();
            var caret = (RectTransform)field.textViewport.GetChild(0);
            Assert.Equal("Caret", caret.name);
            Assert.Equal(HideFlags.DontSave, caret.gameObject.hideFlags);
            AssertSameRect(field.textComponent.rectTransform, caret);
            Assert.Equal(field.textComponent.rectTransform.rect, caret.rect);
        }

        // ----- AssignPositioningIfNeeded, patched as mods patch it -----

        static readonly MethodInfo AssignPositioning =
            typeof(TMP_InputField).GetMethod("AssignPositioningIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);

        static readonly List<TMP_InputField> calls = new();
        static readonly List<string> entryRects = new();
        static bool skip;

        static string Describe(RectTransform rt) => $"{rt.anchorMin}-{rt.anchorMax} {rt.sizeDelta}";

        // Takes the private fields PLib's prefix takes, by the names it uses.
        static bool Prefix(TMP_InputField __instance, RectTransform ___caretRectTrans, TMP_Text ___m_TextComponent)
        {
            calls.Add(__instance);
            Assert.NotNull(___m_TextComponent);
            entryRects.Add(___caretRectTrans == null ? "null" : Describe(___caretRectTrans));
            return !skip;
        }

        static Harmony Patch()
        {
            calls.Clear();
            entryRects.Clear();
            skip = false;
            Assert.NotNull(AssignPositioning);
            Assert.Equal(typeof(void), AssignPositioning.ReturnType);
            var field = typeof(TMP_InputField).GetField("caretRectTrans", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(typeof(RectTransform), field.FieldType);
            var harmony = new Harmony("oni-moono.tests.assign-positioning");
            harmony.Patch(AssignPositioning, prefix: new HarmonyMethod(typeof(TmpInputSetupTests).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
            return harmony;
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void AssignPositioningRunsOncePerCaretMade()
        {
            var harmony = Patch();
            try
            {
                var field = NewField();
                Assert.Equal(new[] { field }, calls);
                // On entry the caret has the RectTransform defaults.
                Assert.Equal(Describe(NewDefaultRect()), entryRects[0]);
                field.gameObject.SetActive(false);
                field.gameObject.SetActive(true);
                EventSystem.current.SetSelectedGameObject(field.gameObject);
                Frame();
                Assert.True(field.isFocused);
                Event.Enqueue(new Event { type = EventType.KeyDown, character = 'a' });
                Frame();
                field.text = "xyz";
                EventSystem.current.SetSelectedGameObject(null);
                field.ActivateInputField();
                Frame();
                Frame();
                Assert.Single(calls);
                // A clone makes its own caret, and positions it once.
                var clone = Object.Instantiate(field.gameObject, field.transform.parent).GetComponent<TMP_InputField>();
                Assert.Equal(new[] { field, clone }, calls);
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        }

        static RectTransform NewDefaultRect()
        {
            var go = new GameObject("Default", typeof(RectTransform));
            return (RectTransform)go.transform;
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void ACaretWhosePositioningIsSkippedKeepsTheDefaults()
        {
            var harmony = Patch();
            skip = true;
            try
            {
                var field = NewField();
                Assert.Single(calls);
                var caret = (RectTransform)field.textViewport.GetChild(0);
                Assert.Equal(new Vector2(0.5f, 0.5f), caret.anchorMin);
                Assert.Equal(new Vector2(0.5f, 0.5f), caret.anchorMax);
                Assert.Equal(new Vector2(100f, 100f), caret.sizeDelta);
                Assert.Equal(new Vector2(0.5f, 0.5f), caret.pivot);
                // Made at the world origin, it keeps that world position under the viewport.
                Assert.Equal(Vector3.zero, caret.position);
                field.gameObject.SetActive(false);
                field.gameObject.SetActive(true);
                field.ActivateInputField();
                Frame();
                Assert.Equal(new Vector2(100f, 100f), caret.sizeDelta);
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        }
    }
}
