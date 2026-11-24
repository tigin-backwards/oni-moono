using System.Collections.Generic;
using System.Threading;
using OniMoono.Desktop;
using OniMoono.Desktop.D3d11;
using OniMoono.Desktop.Gl;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The Direct3D 11 device's presentation helpers (D3d11Present, D3d11LoadAnimation): the
    /// loading animation's tile geometry and bar, and the device thread's timed idle work
    /// (GlWorker.IdleWaitMs) that the repeats, splash and loading animation run on.
    public class D3d11PresentTests
    {
        static LoadingScreen.KanimFrames Frames(bool screenRows)
            => new()
            {
                Strip = new RenderTextureNative { Width = 256, Height = 128, ScreenRows = screenRows },
                Count = 8, Columns = 4, TileWidth = 64, TileHeight = 64,
                Area = new RectInt(100, 50, 64, 64), ScreenWidth = 1280, ScreenHeight = 720,
            };

        [Fact]
        public void TileOfABottomFirstStripReadsItTurnedOver()
        {
            // Tile 5: column 1, row 1 up the strip (y 64). Bottom first: image rows 64..128, read
            // from 128 at the area's top to 64 at its bottom.
            var t = D3d11LoadAnimation.TileOf(Frames(false), 5, 1280, 720, 128, first: true);
            Assert.Equal((100, 720 - 114, 164, 720 - 50), (t.X0, t.Top, t.X1, t.Bottom));
            Assert.Equal((64, 128, 64), (t.SrcX, t.SrcTop, t.SrcBottom));
            Assert.False(t.Scaled);
            Assert.True(t.First);
        }

        [Fact]
        public void TileOfATopFirstStripReadsItAsStored()
        {
            // Stored top first, the tile 64 px up the 128-px strip is image rows 0..64.
            var t = D3d11LoadAnimation.TileOf(Frames(true), 5, 1280, 720, 128, first: false);
            Assert.Equal((0, 64), (t.SrcTop, t.SrcBottom));
        }

        [Fact]
        public void TileOfInAWindowOfAnotherSizeIsScaled()
        {
            var t = D3d11LoadAnimation.TileOf(Frames(false), 0, 2560, 1440, 128, first: true);
            Assert.True(t.Scaled);
            Assert.Equal((200, 328), (t.X0, t.X1));
        }

        [Fact]
        public void BarRectsHaveATrackAFillAndPartCoveredEdges()
        {
            var rects = new List<SplashRain.BarRect>();
            D3d11LoadAnimation.BarRects(10, 20, 100, 4, 10, 40.5, rects);
            Assert.Equal(new SplashRain.BarRect(9, 19, 102, 6, 0.20f, 0.18f, 0.27f), rects[0]);
            Assert.Equal((10, 30), (rects[1].X, rects[1].Width));
            // The column at 40 is half covered; nothing before 10 (the fill starts on a column).
            Assert.Equal(3, rects.Count);
            Assert.Equal((40, 1), (rects[2].X, rects[2].Width));
        }

        [Fact]
        public void IdleWorkRunsWhenItsWaitIsDue()
        {
            // The worker waits IdleWaitMs, then finds the work due (HasIdleWork) and runs it,
            // with no frame handed over and no Wake.
            long due = 0;
            int ran = 0;
            using var done = new ManualResetEventSlim();
            var worker = new GlWorker("test", () => { }, () => { }, _ => { });
            int Due() => Interlocked.Read(ref due) is var d && d == 0 ? -1 : (int)System.Math.Max(0, (d - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / System.Diagnostics.Stopwatch.Frequency);
            worker.HasIdleWork = () => Due() == 0;
            worker.IdleWaitMs = Due;
            worker.IdleWork = () =>
            {
                ran++;
                Interlocked.Exchange(ref due, 0);
                done.Set();
                return true;
            };
            Interlocked.Exchange(ref due, System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 20);
            worker.Wake();
            Assert.True(done.Wait(5000, TestContext.Current.CancellationToken));
            worker.Dispose();
            Assert.Equal(1, ran);
        }

        [Fact]
        public void IdleWorkFallingDueBetweenTheTwoQuestionsIsNotMissed()
        {
            // HasIdleWork says no, then the work falls due before IdleWaitMs is asked, which says
            // 0: the worker must wait 0 ms and ask again, not wait untimed for a Wake.
            int armed = 0, asked = 0;
            using var done = new ManualResetEventSlim();
            var worker = new GlWorker("test", () => { }, () => { }, _ => { });
            worker.HasIdleWork = () => Volatile.Read(ref armed) == 1 && asked++ > 0;
            worker.IdleWaitMs = () => Volatile.Read(ref armed) == 1 ? 0 : -1;
            worker.IdleWork = () =>
            {
                Volatile.Write(ref armed, 0);
                done.Set();
                return true;
            };
            Volatile.Write(ref armed, 1);
            worker.Wake();
            bool ran = done.Wait(2000, TestContext.Current.CancellationToken);
            worker.Dispose();
            Assert.True(ran);
            Assert.True(asked >= 2);
        }

        [Fact]
        public void AWaitForTheWorkerCallsTheWaitPumpWhileItWaits()
        {
            // D3d11Exclusive: while the device thread's DXGI calls may send the window messages,
            // the main thread's waits for it answer them (GlWorker.WaitPump) instead of blocking.
            int pumped = 0;
            var worker = new GlWorker("test", () => { }, () => { }, _ => { });
            worker.WaitPump = () => Interlocked.Increment(ref pumped);
            worker.Call(() => Thread.Sleep(50));
            worker.WaitPump = null;
            worker.Call(() => { });
            worker.Dispose();
            Assert.True(pumped > 0);
        }
    }
}
