using System;
using System.Collections.Generic;
using OniMoono.Desktop;
using Xunit;

namespace OniMoono.Tests
{
    /// The window's IME on Windows (issue #30): its state applied at each frame's poll when the
    /// mode changed, and again whatever it was at the window's focus and input language messages,
    /// as Unity's player does; the composition read from the IME's messages and kept from GLFW.
    public class Win32ImeTests
    {
        sealed class Context : Win32Ime.IContext
        {
            public readonly List<string> Calls = new();
            public readonly Dictionary<uint, string> Strings = new();
            public void Associate(bool on) => Calls.Add(on ? "on" : "off");
            public string Read(uint index) => Strings.TryGetValue(index, out var s) ? s : "";
            public void Place(int x, int y) => Calls.Add($"place {x},{y}");
            public void Cancel() => Calls.Add("cancel");
        }

        sealed class Fixture
        {
            public bool On;
            public (int x, int y) Cursor = (10, 20);
            public readonly Context Context = new();
            public readonly List<string> Composed = new(), Committed = new();
            public readonly Win32Ime Ime;
            public Fixture() => Ime = new Win32Ime(() => On, () => Cursor, Context, Composed.Add, Committed.Add, e => throw e);

            public bool Send(uint message, long lParam = 0)
            {
                nint l = (nint)lParam;
                return Ime.Intercepted(message, ref l);
            }
        }

        [Fact]
        public void APollAppliesTheModeOnlyWhenItChanges()
        {
            var f = new Fixture();
            f.Ime.Apply();
            f.Ime.Apply();
            f.On = true;
            f.Ime.Apply();
            f.Ime.Apply();
            Assert.Equal(new[] { "off", "on", "place 10,20" }, f.Context.Calls);
        }

        [Fact]
        public void FocusAndInputLanguageApplyItAgain()
        {
            var f = new Fixture();
            f.Ime.Apply();
            f.Ime.Received(Win32Ime.WM_SETFOCUS);
            f.Ime.Received(Win32Ime.WM_INPUTLANGCHANGE);
            f.Ime.Apply(); // unchanged since
            Assert.Equal(new[] { "off", "off", "off" }, f.Context.Calls);
        }

        [Fact]
        public void OtherMessagesLeaveItAlone()
        {
            var f = new Fixture();
            f.Ime.Apply();
            f.Ime.Received(0x0008); // WM_KILLFOCUS
            f.Ime.Received(0x0100); // WM_KEYDOWN
            Assert.Equal(new[] { "off" }, f.Context.Calls);
            Assert.False(f.Send(0x0100));
            Assert.False(f.Send(0x0102)); // WM_CHAR
            Assert.False(f.Send(0x0282)); // WM_IME_NOTIFY: the IME's candidate list is its own
        }

        [Fact]
        public void WhileOnItsWindowsFollowTheCaret()
        {
            var f = new Fixture { On = true };
            f.Ime.Apply();
            f.Ime.Apply();
            f.Cursor = (30, 40);
            f.Ime.Apply();
            // Turned on again, and at a composition's start, they are placed again.
            f.On = false;
            f.Ime.Apply();
            f.On = true;
            f.Ime.Apply();
            Assert.True(f.Send(Win32Ime.WM_IME_STARTCOMPOSITION));
            Assert.Equal(new[] { "on", "place 10,20", "place 30,40", "off", "on", "place 30,40", "place 30,40" }, f.Context.Calls);
        }

        [Fact]
        public void TheCompositionIsReadAndTheResultTyped()
        {
            var f = new Fixture { On = true };
            f.Ime.Apply();
            Assert.True(f.Send(Win32Ime.WM_IME_STARTCOMPOSITION));
            f.Context.Strings[Win32Ime.GCS_COMPSTR] = "ni";
            Assert.True(f.Send(Win32Ime.WM_IME_COMPOSITION, Win32Ime.GCS_COMPSTR));
            f.Context.Strings[Win32Ime.GCS_COMPSTR] = "ni hao";
            Assert.True(f.Send(Win32Ime.WM_IME_COMPOSITION, Win32Ime.GCS_COMPSTR | 0x0080)); // GCS_CURSORPOS too
            f.Context.Strings[Win32Ime.GCS_RESULTSTR] = "你好";
            Assert.True(f.Send(Win32Ime.WM_IME_COMPOSITION, Win32Ime.GCS_RESULTSTR));
            Assert.True(f.Send(Win32Ime.WM_IME_ENDCOMPOSITION));
            Assert.Equal(new[] { "ni", "ni hao", "" }, f.Composed);
            Assert.Equal(new[] { "你好" }, f.Committed);
        }

        [Fact]
        public void AResultWithANewCompositionTypesTheResultFirst()
        {
            var f = new Fixture { On = true };
            var order = new List<string>();
            var ime = new Win32Ime(() => true, () => (0, 0), f.Context, s => order.Add("composed " + s), s => order.Add("committed " + s), e => throw e);
            f.Context.Strings[Win32Ime.GCS_RESULTSTR] = "你";
            f.Context.Strings[Win32Ime.GCS_COMPSTR] = "hao";
            nint l = (nint)(Win32Ime.GCS_RESULTSTR | Win32Ime.GCS_COMPSTR);
            Assert.True(ime.Intercepted(Win32Ime.WM_IME_COMPOSITION, ref l));
            Assert.Equal(new[] { "committed 你", "composed hao" }, order);
        }

        [Fact]
        public void TheImeDrawsNoCompositionWindowOfItsOwn()
        {
            var f = new Fixture();
            nint l = unchecked((nint)0xC000000FL); // composition window and all four candidate windows
            Assert.False(f.Ime.Intercepted(Win32Ime.WM_IME_SETCONTEXT, ref l));
            Assert.Equal((nint)0x4000000FL, l);
        }

        [Fact]
        public void ACompositionLeftWhenTheFieldLetsGoIsDropped()
        {
            var f = new Fixture { On = true };
            f.Ime.Apply();
            f.Context.Strings[Win32Ime.GCS_COMPSTR] = "ni";
            f.Send(Win32Ime.WM_IME_COMPOSITION, Win32Ime.GCS_COMPSTR);
            f.On = false;
            f.Ime.Apply();
            Assert.Equal(new[] { "on", "place 10,20", "cancel", "off" }, f.Context.Calls);
            Assert.Equal(new[] { "ni", "" }, f.Composed);
            Assert.Empty(f.Committed);
        }
    }
}
