using OniMoono.Desktop;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Platform;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // The legacy input state latches the platform's key events once a frame. Several edges of one
    // key can arrive between two frames (fast double-clicks at a low frame rate); they apply in
    // the order they happened.
    public class InputStateTests
    {
        [Fact]
        public void ReleaseThenPressInOneFrameLeavesTheKeyHeld()
        {
            var input = new InputState();
            input.KeyPressed(KeyCode.Mouse0);
            input.BeginFrame(0.05f);
            Assert.True(input.KeyDown(KeyCode.Mouse0));

            input.KeyReleased(KeyCode.Mouse0);
            input.KeyPressed(KeyCode.Mouse0);
            input.BeginFrame(0.05f);
            Assert.True(input.KeyUp(KeyCode.Mouse0));
            Assert.True(input.KeyDown(KeyCode.Mouse0));
            Assert.True(input.Key(KeyCode.Mouse0));

            input.KeyReleased(KeyCode.Mouse0);
            input.BeginFrame(0.05f);
            Assert.True(input.KeyUp(KeyCode.Mouse0));
            Assert.False(input.Key(KeyCode.Mouse0));
        }

        [Fact]
        public void PressReleasePressInOneFrameEndsHeld()
        {
            var input = new InputState();
            input.KeyPressed(KeyCode.A);
            input.KeyReleased(KeyCode.A);
            input.KeyPressed(KeyCode.A);
            input.BeginFrame(0.05f);
            Assert.True(input.KeyDown(KeyCode.A));
            Assert.True(input.KeyUp(KeyCode.A));
            Assert.True(input.Key(KeyCode.A));
        }

        [Fact]
        public void PressAndReleaseInOneFrameReportsBothEdges()
        {
            var input = new InputState();
            input.KeyPressed(KeyCode.Space);
            input.KeyReleased(KeyCode.Space);
            input.BeginFrame(0.05f);
            Assert.True(input.KeyDown(KeyCode.Space));
            Assert.True(input.KeyUp(KeyCode.Space));
            Assert.False(input.Key(KeyCode.Space));

            input.BeginFrame(0.05f);
            Assert.False(input.KeyDown(KeyCode.Space));
            Assert.False(input.KeyUp(KeyCode.Space));
        }

        [Fact]
        public void EdgesHoldForOneFrameOnly()
        {
            var input = new InputState();
            input.KeyPressed(KeyCode.B);
            input.BeginFrame(0.05f);
            input.BeginFrame(0.05f);
            Assert.False(input.KeyDown(KeyCode.B));
            Assert.True(input.Key(KeyCode.B));
        }
        // ONI's "Mouse ScrollWheel" axis (sensitivity 0.1). Unity's X11 and Windows players report
        // a wheel notch as a mouseScrollDelta of one; the game zooms once a frame the axis is not
        // zero, so the notches of one frame zoom once in both players.
        static readonly ProjectSettings.InputAxis ScrollWheelAxis =
            new("Mouse ScrollWheel", "", "", "", "", 0f, 0f, 0.1f, false, false, 1, 2, 0);

        [Fact]
        public void WheelNotchesAddUpWithinAFrameAndHoldForThatFrame()
        {
            var input = new InputState();
            input.InstallAxes(new[] { ScrollWheelAxis });
            input.Scrolled(new Vector2(0f, -1f));
            input.BeginFrame(0.05f);
            Assert.Equal(new Vector2(0f, -1f), input.ScrollDelta);
            Assert.Equal(-0.1f, input.GetAxis("Mouse ScrollWheel"));

            input.Scrolled(new Vector2(0f, -1f));
            input.Scrolled(new Vector2(0f, -1f));
            input.BeginFrame(0.05f);
            Assert.Equal(new Vector2(0f, -2f), input.ScrollDelta);
            Assert.Equal(-0.2f, input.GetAxis("Mouse ScrollWheel"), 6);

            input.BeginFrame(0.05f);
            Assert.Equal(Vector2.zero, input.ScrollDelta);
            Assert.Equal(0f, input.GetAxis("Mouse ScrollWheel"));
        }

        // Typed characters keep their whole code point: one above U+FFFF is two UTF-16 units, and
        // a value that is no character's types nothing.
        [Fact]
        public void TypedCodePointsBecomeTheirUtf16Units()
        {
            Assert.Equal("a", InputStateFeed.TextOf('a'));
            Assert.Equal("\U0001F914", InputStateFeed.TextOf(0x1F914));
            Assert.Equal("\U0001D800", InputStateFeed.TextOf(0x1D800));
            Assert.Equal("", InputStateFeed.TextOf(0xD83E));
            Assert.Equal("", InputStateFeed.TextOf(0xDD14));
            Assert.Equal("", InputStateFeed.TextOf(0x110000));
        }

        // IMGUI's ScrollWheel event: down for wheel-down, one per notch on Linux and three on
        // Windows, as the original players report it (Linux: tests/e2e/scroll-zoom.yaml; Windows:
        // real WHEEL_DELTA notches in the U59 player, delta 3 and 60 px a notch in a scroll view).
        [Fact]
        public void WheelEventDeltaIsOneANotchOnLinuxAndThreeOnWindows()
        {
            Assert.Equal(new Vector2(0f, 1f), InputStateFeed.WheelEventDelta(new Vector2(0f, -1f), windows: false));
            Assert.Equal(new Vector2(0f, -2f), InputStateFeed.WheelEventDelta(new Vector2(0f, 2f), windows: false));
            Assert.Equal(new Vector2(-1f, 0f), InputStateFeed.WheelEventDelta(new Vector2(1f, 0f), windows: false));
            Assert.Equal(new Vector2(0f, 3f), InputStateFeed.WheelEventDelta(new Vector2(0f, -1f), windows: true));
            Assert.Equal(new Vector2(0f, -6f), InputStateFeed.WheelEventDelta(new Vector2(0f, 2f), windows: true));
        }
    }
}
