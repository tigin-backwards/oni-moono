using OniMoono.Host.GameTweaks;
using Xunit;
using static OniMoono.Host.GameTweaks.PostLoadGcGate;

namespace OniMoono.Tests
{
    /// post-load-gc's wait (src/OniMoono.Host/GameTweaks/PostLoadGcGate.cs): it collects on the
    /// first fast frame after the load's long ones while paused, never once the game plays.
    public class PostLoadGcGateTests
    {
        [Fact]
        public void CollectsOnTheFirstFastPausedFrame()
        {
            var gate = new PostLoadGcGate();
            foreach (var slow in new[] { 2.5, 0.8, 0.3, 0.12 })
                Assert.Equal(Verdict.Wait, gate.Frame(slow, paused: true));
            Assert.Equal(Verdict.Collect, gate.Frame(0.04, paused: true));
            Assert.Equal(5, gate.Frames);
        }

        [Fact]
        public void AnswersOnce()
        {
            var gate = new PostLoadGcGate();
            Assert.Equal(Verdict.Collect, gate.Frame(0.02, paused: true));
            Assert.Equal(Verdict.Wait, gate.Frame(0.02, paused: true));
            Assert.Equal(Verdict.Wait, gate.Frame(0.02, paused: false));
        }

        [Fact]
        public void SkipsOnceTheGameIsUnpaused()
        {
            var gate = new PostLoadGcGate();
            Assert.Equal(Verdict.Wait, gate.Frame(1.0, paused: true));
            Assert.Equal(Verdict.Skip, gate.Frame(0.02, paused: false));
            Assert.Equal(Verdict.Wait, gate.Frame(0.02, paused: true));
        }

        [Fact]
        public void WaitsForTheSpeedControls()
        {
            var gate = new PostLoadGcGate();
            for (int i = 1; i < GiveUpFrames; i++)
                Assert.Equal(Verdict.Wait, gate.Frame(0.02, paused: null));
            Assert.Equal(Verdict.Skip, gate.Frame(0.02, paused: null));
        }

        [Fact]
        public void CollectsByMaxFramesWhenEveryPausedFrameIsSlow()
        {
            var gate = new PostLoadGcGate();
            for (int i = 1; i < MaxFrames; i++)
                Assert.Equal(Verdict.Wait, gate.Frame(0.25, paused: true));
            Assert.Equal(Verdict.Collect, gate.Frame(0.25, paused: true));
        }
    }
}
