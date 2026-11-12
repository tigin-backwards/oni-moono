using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using Xunit;

namespace OniMoono.Tests
{
    /// The loading screen's animation (GlLoadAnimation) runs only over the loading screen: over
    /// frames from before an armed scene load's frame, or, with the loading overlay up, over a
    /// frame shown since it went up once no frame has followed for the watchdog's time. A frame
    /// of the load's own frame or later (the colony) ends it, and nothing runs without arming.
    public class LoadAnimationGateTests
    {
        const int W = LoadingScreen.WatchdogMs;

        [Fact]
        public void AnArmedLoadAnimatesTheFramesBeforeItsOwn()
        {
            Assert.True(GlLoadAnimation.Wanted(shownFrame: 99, armedFrame: 100, overlayFrame: -1, idleMs: 0));
            Assert.True(GlLoadAnimation.Wanted(shownFrame: 97, armedFrame: 100, overlayFrame: -1, idleMs: 0));
        }

        [Fact]
        public void TheLoadsOwnFrameAndLaterEndIt()
        {
            Assert.False(GlLoadAnimation.Wanted(shownFrame: 100, armedFrame: 100, overlayFrame: -1, idleMs: 5000));
            Assert.False(GlLoadAnimation.Wanted(shownFrame: 140, armedFrame: 100, overlayFrame: -1, idleMs: 5000));
        }

        [Fact]
        public void TheOverlayAnimatesOnlyAfterTheWatchdog()
        {
            Assert.False(GlLoadAnimation.Wanted(shownFrame: 51, armedFrame: -1, overlayFrame: 50, idleMs: W - 1));
            Assert.True(GlLoadAnimation.Wanted(shownFrame: 51, armedFrame: -1, overlayFrame: 50, idleMs: W));
            Assert.Equal(1, GlLoadAnimation.WatchdogDue(shownFrame: 51, overlayFrame: 50, idleMs: W - 1));
            Assert.Equal(W, GlLoadAnimation.WatchdogDue(shownFrame: 50, overlayFrame: 50, idleMs: 0));
        }

        [Fact]
        public void AFrameFromBeforeTheOverlayIsNotTheLoadingScreen()
        {
            Assert.False(GlLoadAnimation.Wanted(shownFrame: 49, armedFrame: -1, overlayFrame: 50, idleMs: 10_000));
            Assert.Equal(-1, GlLoadAnimation.WatchdogDue(shownFrame: 49, overlayFrame: 50, idleMs: 0));
        }

        [Fact]
        public void NothingRunsUnarmed()
        {
            Assert.False(GlLoadAnimation.Wanted(shownFrame: 10, armedFrame: -1, overlayFrame: -1, idleMs: 60_000));
            Assert.Equal(-1, GlLoadAnimation.WatchdogDue(shownFrame: 10, overlayFrame: -1, idleMs: 60_000));
            Assert.False(GlLoadAnimation.Wanted(shownFrame: -1, armedFrame: 100, overlayFrame: 50, idleMs: 60_000));
        }
    }
}
