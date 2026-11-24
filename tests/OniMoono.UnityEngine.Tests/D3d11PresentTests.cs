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
    }
}
