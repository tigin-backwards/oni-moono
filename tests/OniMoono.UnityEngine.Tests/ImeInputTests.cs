using OniMoono.Engine.Platform;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Whether the window takes IME input (issue #30): only when the mode asks for it, so an IME
    /// leaves the game's keys alone outside text fields.
    public class ImeInputTests
    {
        [Fact]
        public void OnTakesImeInputWhateverHasTheFocus()
        {
            Assert.True(ImeInput.Wanted(IMECompositionMode.On, false));
            Assert.True(ImeInput.Wanted(IMECompositionMode.On, true));
        }

        [Fact]
        public void OffNeverTakesIt()
        {
            Assert.False(ImeInput.Wanted(IMECompositionMode.Off, false));
            Assert.False(ImeInput.Wanted(IMECompositionMode.Off, true));
        }

        [Fact]
        public void AutoTakesItOnlyForATextField()
        {
            // Measured: Auto with no field focused, the game's state outside text fields, has the
            // window's IME off.
            Assert.False(ImeInput.Wanted(IMECompositionMode.Auto, false));
            Assert.True(ImeInput.Wanted(IMECompositionMode.Auto, true));
        }

        [Fact]
        public void TheGameOutsideTextFieldsHasNoIme()
        {
            var saved = Input.imeCompositionMode;
            try
            {
                Input.imeCompositionMode = IMECompositionMode.Auto;
                GUIUtility.keyboardControl = 0;
                Assert.False(ImeInput.WantedNow);
                Input.imeCompositionMode = IMECompositionMode.On;
                Assert.True(ImeInput.WantedNow);
            }
            finally { Input.imeCompositionMode = saved; }
        }

        [Fact]
        public void TheCompositionIsTheWindowsImes()
        {
            try
            {
                ImeInput.Composition = "ni";
                Assert.Equal("ni", Input.compositionString);
            }
            finally { ImeInput.Composition = ""; }
            Assert.Equal("", Input.compositionString);
        }

        [Fact]
        public void TheCursorIsInWholeClientPixels()
        {
            var saved = Input.compositionCursorPos;
            try
            {
                Input.compositionCursorPos = new Vector2(676.5f, 707.1f);
                Assert.Equal((676, 707), ImeInput.CursorPixels);
                // The original's value before any field set it.
                Input.compositionCursorPos = new Vector2(0f, 1.2329950e29f);
                Assert.Equal((0, 32767), ImeInput.CursorPixels);
                Assert.Equal(0, ImeInput.Pixel(float.NaN));
                Assert.Equal(-32768, ImeInput.Pixel(float.NegativeInfinity));
            }
            finally { Input.compositionCursorPos = saved; }
        }
    }
}
