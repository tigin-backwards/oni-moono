using System;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // Unity keeps its clocks in double precision and rounds them to float only when read, so a long
    // session's time does not drift from the sum of its frame times.
    public class TimeTests
    {
        [Fact]
        public void TimeDoesNotDriftOverLongSessions()
        {
            float scale = Time.timeScale;
            Time.timeScale = 1f;
            try
            {
                double start = Time.timeAsDouble, unscaledStart = Time.unscaledTimeAsDouble;
                const int frames = 1_000_000; // 4.6 hours at 60 fps
                for (int i = 0; i < frames; i++) Time.BeginFrame(1f / 60f);
                double expected = frames * (double)(1f / 60f);
                Assert.InRange(Time.timeAsDouble - start, expected - 0.001, expected + 0.001);
                Assert.InRange(Time.unscaledTimeAsDouble - unscaledStart, expected - 0.001, expected + 0.001);
                Assert.Equal((float)Time.timeAsDouble, Time.time);
            }
            finally
            {
                Time.timeScale = scale;
            }
        }

        [Fact]
        public void MaximumDeltaTimeIsNeverBelowTheFixedStep()
        {
            float fixedStep = Time.fixedDeltaTime, maximum = Time.maximumDeltaTime;
            try
            {
                Time.fixedDeltaTime = 0.03f;
                Time.maximumDeltaTime = 0.01f;
                Assert.Equal(0.03f, Time.maximumDeltaTime);
                Time.maximumDeltaTime = 0.2f;
                Time.fixedDeltaTime = 0.5f;
                Assert.Equal(0.5f, Time.maximumDeltaTime);
            }
            finally
            {
                Time.fixedDeltaTime = fixedStep;
                Time.maximumDeltaTime = maximum;
            }
        }

        [Fact]
        public void FixedTimeFollowsTimeInWholeSteps()
        {
            Time.BeginFrame(0.1f);
            int steps = 0;
            while (Time.BeginFixedStep())
            {
                Time.EndFixedStep();
                steps++;
            }
            Assert.True(Time.fixedTimeAsDouble <= Time.timeAsDouble);
            Assert.True(Time.fixedTimeAsDouble + Time.fixedDeltaTime > Time.timeAsDouble);
            Assert.True(steps >= 1);
        }

        /// The low-fps-timing tweak's game time cap (Time.CapNextFrame): the next frame's deltaTime at
        /// most the cap, real time untouched, frames under the cap bit for bit as without, one frame only.
        [Fact]
        public void CapNextFrameCapsOnlyTheScaledTimeOfOneFrame()
        {
            float scale = Time.timeScale;
            Time.timeScale = 3f;
            try
            {
                Time.BeginFrame(0.01f); // a frame armed before this test runs no further
                foreach (float real in new[] { 1f / 240, 1f / 60, 1f / 20, 1f / 15, 0.0667f, 0.1f, 0.2f, 0.25f, 0.5f, 2f })
                {
                    Time.BeginFrame(real);
                    float uncapped = Time.deltaTime;
                    double time = Time.timeAsDouble, unscaled = Time.unscaledTimeAsDouble;
                    Time.CapNextFrame(0.2f);
                    Time.BeginFrame(real);
                    Assert.Equal(uncapped < 0.2f ? uncapped : 0.2f, Time.deltaTime);
                    if (uncapped <= 0.2f) Assert.Equal(BitConverter.SingleToInt32Bits(uncapped), BitConverter.SingleToInt32Bits(Time.deltaTime));
                    Assert.Equal(real, Time.unscaledDeltaTime);
                    Assert.Equal(time + Time.deltaTime, Time.timeAsDouble);
                    Assert.Equal(unscaled + real, Time.unscaledTimeAsDouble);
                    Time.BeginFrame(real);
                    Assert.Equal(uncapped, Time.deltaTime);
                }
                // Speed 3 at 5 fps: 0.6 s of game time a frame, 0.2 s capped.
                Time.CapNextFrame(0.2f);
                Time.BeginFrame(0.2f);
                Assert.Equal(0.2f, Time.deltaTime);
            }
            finally
            {
                Time.timeScale = scale;
            }
        }
    }
}
