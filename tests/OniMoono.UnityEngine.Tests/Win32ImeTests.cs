using System;
using System.Collections.Generic;
using OniMoono.Desktop;
using Xunit;

namespace OniMoono.Tests
{
    /// The window's IME state on Windows (issue #30): applied at each frame's poll when the mode
    /// changed, and again whatever it was at the window's focus and input language messages, as
    /// Unity's player does.
    public class Win32ImeTests
    {
        static (Win32Ime ime, List<bool> calls) Ime(Func<bool> wanted)
        {
            var calls = new List<bool>();
            return (new Win32Ime(wanted, calls.Add, e => throw e), calls);
        }

        [Fact]
        public void APollAppliesTheModeOnlyWhenItChanges()
        {
            bool on = false;
            var (ime, calls) = Ime(() => on);
            ime.Apply();
            ime.Apply();
            on = true;
            ime.Apply();
            ime.Apply();
            Assert.Equal(new[] { false, true }, calls);
        }

        [Fact]
        public void FocusAndInputLanguageApplyItAgain()
        {
            var (ime, calls) = Ime(() => false);
            ime.Apply();
            ime.Received(Win32Ime.WM_SETFOCUS);
            ime.Received(Win32Ime.WM_INPUTLANGCHANGE);
            ime.Apply(); // unchanged since
            Assert.Equal(new[] { false, false, false }, calls);
        }

        [Fact]
        public void OtherMessagesLeaveItAlone()
        {
            var (ime, calls) = Ime(() => false);
            ime.Apply();
            ime.Received(0x0008); // WM_KILLFOCUS
            ime.Received(0x0100); // WM_KEYDOWN
            Assert.Equal(new[] { false }, calls);
        }
    }
}
