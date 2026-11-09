using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Xunit;

namespace OniMoono.Tests
{
    // Backspace with modifiers, and the members Text Input Hotkeys patches
    // (docs/cleanroom/textmeshpro.md, "Backspace with modifiers").
    public class TmpInputBackspaceTests : EngineTest
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static TMP_InputField Focused(string text)
        {
            var field = TmpInputTests.NewField(300f);
            field.onFocusSelectAll = false;
            EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
            Frame();
            Frame();
            Assert.True(field.isFocused);
            foreach (char c in text) field.ProcessEvent(Key(KeyCode.None, c));
            Assert.Equal(text, field.text);
            return field;
        }

        static Event Key(KeyCode key, char character = '\0', EventModifiers modifiers = 0) =>
            new() { type = EventType.KeyDown, keyCode = key, character = character, modifiers = modifiers };

        [Theory]
        [InlineData(EventModifiers.None)]
        [InlineData(EventModifiers.Control)]
        [InlineData(EventModifiers.Shift)]
        [InlineData(EventModifiers.Control | EventModifiers.Shift)]
        [InlineData(EventModifiers.Alt)]
        [InlineData(EventModifiers.Command)]
        public void BackspaceRemovesOneCharacterWhateverTheModifiers(EventModifiers modifiers)
        {
            var field = Focused("liquid pump");
            var changes = new List<string>();
            field.onValueChanged.AddListener(changes.Add);
            field.ProcessEvent(Key(KeyCode.Backspace, '\b', modifiers));
            Assert.Equal("liquid pum", field.text);
            Assert.Equal(10, field.stringPosition);
            Assert.Equal(10, field.caretPosition);
            Assert.Equal(new[] { "liquid pum" }, changes);
        }

        [Fact]
        public void CtrlBackspaceRemovesTheSelection()
        {
            var field = Focused("liquid pump");
            field.selectionStringAnchorPosition = 2;
            field.selectionStringFocusPosition = 9;
            field.ProcessEvent(Key(KeyCode.Backspace, '\b', EventModifiers.Control));
            Assert.Equal("limp", field.text);
            Assert.Equal(2, field.stringPosition);
        }

        [Fact]
        public void BackspaceRemovesASurrogatePairWhole()
        {
            var field = Focused("a\U0001F600");
            field.ProcessEvent(Key(KeyCode.Backspace, '\b', EventModifiers.Control));
            Assert.Equal("a", field.text);
            Assert.Equal(1, field.stringPosition);
        }

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void TheMembersHaveTheModsNamesAndSignatures()
        {
            var backspaces = typeof(TMP_InputField).GetMethods(Instance | BindingFlags.Static).Where(m => m.Name == "Backspace").ToArray();
            var backspace = Assert.Single(backspaces);
            Assert.True(backspace.IsPrivate);
            Assert.False(backspace.IsStatic);
            Assert.Empty(backspace.GetParameters());
            Assert.Equal(typeof(void), backspace.ReturnType);
            Assert.Same(backspace, AccessTools.Method(typeof(TMP_InputField), "Backspace"));

            var keyPressed = typeof(TMP_InputField).GetMethod("KeyPressed", Instance, null, new[] { typeof(Event) }, null);
            Assert.NotNull(keyPressed);
            // Protected and not virtual (textmeshpro.md, "KeyPressed is protected").
            Assert.True(keyPressed.IsFamily);
            Assert.False(keyPressed.IsVirtual);
            Assert.Equal("evt", keyPressed.GetParameters()[0].Name);
            Assert.Equal("EditState", keyPressed.ReturnType.Name);

            var position = typeof(TMP_InputField).GetProperty("stringPositionInternal", Instance);
            Assert.NotNull(position);
            Assert.Equal(typeof(int), position.PropertyType);
            Assert.True(position.GetMethod.IsAssembly);

            var wordBegin = typeof(TMP_InputField).GetMethod("FindPrevWordBegin", Instance, null, System.Type.EmptyTypes, null);
            Assert.NotNull(wordBegin);
            Assert.True(wordBegin.IsPrivate);
            Assert.Equal(typeof(int), wordBegin.ReturnType);

            var field = Focused("liquid pump");
            Assert.Equal(11, (int)position.GetValue(field));
            Assert.Equal(7, (int)wordBegin.Invoke(field, null));
        }

        // ----- An ordinary Harmony patch on Backspace() -----

        static readonly List<TMP_InputField> backspaceCalls = new();

        static void Prefix(TMP_InputField __instance) => backspaceCalls.Add(__instance);

        [NeedsGameAssembly("0Harmony")]
        [Fact]
        public void APrefixOnBackspaceRunsOncePerBackspaceKeyWhateverTheModifiers()
        {
            backspaceCalls.Clear();
            var harmony = new Harmony("oni-moono.tests.backspace-prefix");
            harmony.Patch(AccessTools.Method(typeof(TMP_InputField), "Backspace"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TmpInputBackspaceTests), nameof(Prefix))));
            try
            {
                var field = Focused("liquid pump");
                Assert.Empty(backspaceCalls);
                field.ProcessEvent(Key(KeyCode.Backspace, '\b'));
                field.ProcessEvent(Key(KeyCode.Backspace, '\b', EventModifiers.Control));
                field.ProcessEvent(Key(KeyCode.Backspace, '\b', EventModifiers.Control | EventModifiers.Shift));
                field.ProcessEvent(Key(KeyCode.Delete));
                Assert.Equal(new[] { field, field, field }, backspaceCalls);
                Assert.Equal("liquid p", field.text);
            }
            finally { harmony.UnpatchAll(harmony.Id); }
        }
    }
}
