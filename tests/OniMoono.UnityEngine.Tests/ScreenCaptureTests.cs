using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OniMoono.Engine.Platform;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    // ScreenCapture.CaptureScreenshot with a superSize: a capture that cannot be made logs an
    // error and writes nothing, and the game goes on (issue #75: the debug 8x screenshot of a
    // 2560x1440 screen ran out of memory reading back its 20480x11520 render, and the exception
    // ended the game).
    public class ScreenCaptureTests : EngineTest, IDisposable
    {
        readonly List<string> errors = new();
        readonly IPlatform previousPlatform = PlatformServices.Current;
        readonly RenderDevice previousDevice = GraphicsDevice.Current;
        readonly string folder = Path.Combine(Path.GetTempPath(), "oni-capture-" + Guid.NewGuid().ToString("N"));

        void OnLog(string message, string stackTrace, LogType type)
        {
            if (type is LogType.Error or LogType.Exception) errors.Add(message);
        }

        public ScreenCaptureTests()
        {
            Application.logMessageReceivedThreaded += OnLog;
            Directory.CreateDirectory(folder);
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            OniMoono.Engine.Assets.ProjectSettings.Load();
            FrameRenderer.Install();
        }

        void IDisposable.Dispose()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            GraphicsDevice.Install(previousDevice);
            PlatformServices.Install(previousPlatform);
            Directory.Delete(folder, true);
            base.Dispose();
        }

        RecordingDevice Install(int width, int height)
        {
            PlatformServices.Install(new HeadlessPlatform(width, height));
            var device = new RecordingDevice();
            GraphicsDevice.Install(device);
            return device;
        }

        [Fact]
        public void ASuperSizedCaptureWritesTheRenderScaleTimesTheScreen()
        {
            var device = Install(40, 30);
            new GameObject("Camera").AddComponent<Camera>();
            var reads = new List<(int, int)>();
            device.OnReadPixels = (source, rect, _, _, _) => reads.Add((source.Width, source.Height));
            var path = Path.Combine(folder, "shot.png");
            ScreenCapture.CaptureScreenshot(path, 3);
            Frame();

            Assert.Empty(errors);
            Assert.Equal(new[] { (120, 90) }, reads);
            var image = new Texture2D(2, 2);
            Assert.True(image.LoadImage(File.ReadAllBytes(path)));
            Assert.Equal((120, 90), (image.width, image.height));
        }

        [Fact]
        public void ACaptureKeepsTheRowsInPlace()
        {
            // Each row reads as its own colour; the PNG's rows run top to bottom, the device's
            // bottom to top, read in bands.
            var device = Install(600, 9000);
            device.OnReadPixels = (_, rect, destination, x, y) =>
            {
                var mip = destination.Memory.Span.Slice((int)destination.MipOffset(0), (int)destination.MipSize(0));
                for (int j = 0; j < rect.height; j++)
                    for (int i = 0; i < rect.width; i++)
                        TextureFormats.Encode(destination.Format, mip, destination.Width, x + i, y + j, new Color((rect.y + j) % 251 / 255f, (rect.y + j) / 255 / 255f, i % 7 / 255f, 1f));
            };
            var path = Path.Combine(folder, "rows.png");
            ScreenCapture.CaptureScreenshot(path, 1);
            Frame();

            Assert.Empty(errors);
            var image = new Texture2D(2, 2);
            Assert.True(image.LoadImage(File.ReadAllBytes(path)));
            Assert.Equal((600, 9000), (image.width, image.height));
            foreach (int row in new[] { 0, 1, 3494, 3495, 6990, 8999 })
            {
                var c = (Color32)image.GetPixel(5, row);
                Assert.Equal((row % 251, row / 255, 5), (c.r, c.g, c.b));
            }
        }

        [Fact]
        public void PngStreamWritesTheBytesPngEncodeDoes()
        {
            var random = new System.Random(75);
            const int w = 333, h = 517;
            var rows = new byte[h * (1 + w * 4)];
            for (int y = 0; y < h; y++)
            {
                var row = rows.AsSpan(y * (1 + w * 4) + 1, w * 4);
                // Runs and noise, so the deflate output has both matches and literals.
                if (y % 3 == 0) random.NextBytes(row);
                else row.Fill((byte)(y * 7));
            }
            var expected = OniMoono.Engine.Png.Encode(w, h, 4, rows);
            using var stream = new MemoryStream();
            using (var png = new OniMoono.Engine.PngStream(stream, w, h, 4))
            {
                for (int y = 0; y < h; y++) png.WriteRow(rows.AsSpan(y * (1 + w * 4) + 1, w * 4));
                png.Finish();
            }
            Assert.Equal(expected, stream.ToArray());
        }

        [Fact]
        public void ACaptureWhoseRenderFailsLogsAnErrorAndWritesNothing()
        {
            var device = Install(40, 30);
            new GameObject("Camera").AddComponent<Camera>();
            // The driver could not make the 20480x11520 targets (issue #75's "framebuffer
            // incomplete"): a failed device command during the capture.
            device.OnReadPixels = (_, _, _, _, _) => device.Failures++;
            var path = Path.Combine(folder, "failed.png");
            ScreenCapture.CaptureScreenshot(path, 8);
            Frame();

            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(folder));
            Assert.Contains("rendering at 320x240 failed", Assert.Single(errors));
            errors.Clear();
            Frame();
            Assert.Empty(errors);
        }

        [Fact]
        public void ACaptureOutOfMemoryLogsAnErrorAndTheGameGoesOn()
        {
            var device = Install(40, 30);
            device.OnReadPixels = (_, _, _, _, _) => throw new OutOfMemoryException();
            var path = Path.Combine(folder, "oom.png");
            ScreenCapture.CaptureScreenshot(path, 8);
            Frame();

            Assert.False(File.Exists(path));
            Assert.Contains("out of memory for 320x240 pixels", Assert.Single(errors));
            errors.Clear();
        }

        [Fact]
        public void ACaptureLargerThanTheGpusTexturesRendersInWindows()
        {
            // 4x of 4100x30 is 16400 wide, past the null device's 16384: windows of bands of
            // rows, each window's useful part read into its place.
            var device = Install(4100, 30);
            new GameObject("Camera").AddComponent<Camera>();
            var reads = new List<(int Width, int Height, RectInt Rect)>();
            device.OnReadPixels = (source, rect, _, _, _) => reads.Add((source.Width, source.Height, rect));
            var path = Path.Combine(folder, "wide.png");
            ScreenCapture.CaptureScreenshot(path, 4);
            Frame();

            Assert.Empty(errors);
            Assert.True(reads.Count > 1);
            foreach (var (w, h, rect) in reads)
            {
                Assert.True(w <= 16384 && h <= 120);
                Assert.True(rect.x >= 0 && rect.y >= 0 && rect.xMax <= w && rect.yMax <= h);
            }
            Assert.Equal(16400L * 120, reads.Sum(r => (long)r.Rect.width * r.Rect.height));
            var image = new Texture2D(2, 2);
            Assert.True(image.LoadImage(File.ReadAllBytes(path)));
            Assert.Equal((16400, 120), (image.width, image.height));
        }

        [Fact]
        public void ASuperSizedCaptureLeavesTheOverlayUiOut()
        {
            // The original's super-sized captures show the cameras only (a 2x capture of a
            // 1280x720 screen, which fits one target, leaves out the overlay canvas the frame
            // has); a 1x capture is the frame, UI and all.
            var device = Install(40, 30);
            int overlays = 0;
            void Count() => overlays++;
            FrameRenderer.RenderOverlays += Count;
            try
            {
                Frame();
                int perFrame = overlays;
                overlays = 0;
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "two.png"), 2);
                Frame();
                Assert.Equal(perFrame, overlays);
                overlays = 0;
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "one.png"), 1);
                Frame();
                Assert.Equal(perFrame, overlays);
                Assert.Empty(errors);
            }
            finally
            {
                FrameRenderer.RenderOverlays -= Count;
            }
        }

        [Fact]
        public void ScreenSizedTexturesAreTheCapturesSizeForIt()
        {
            // The game's screen-sized targets (its world's MRT targets, the light buffer) render
            // at the capture's size, their cameras seeing the screen's; afterwards they are as
            // they were, their device textures too.
            var device = Install(40, 30);
            var target = new RenderTexture(40, 30, 24);
            var other = new RenderTexture(64, 64, 0);
            var native = (RenderTextureNative)target.NativeOrThrow;
            var gpu = new object();
            native.Gpu = gpu;
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.targetTexture = target;
            var during = new List<(int, int, Vector2, int)>();
            device.OnReadPixels = (_, _, _, _, _) => during.Add((native.Width, native.Height, ((CameraNative)camera.NativeOrThrow).TargetSize, other.width));
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "sized.png"), 3);
            Frame();

            Assert.Empty(errors);
            Assert.Equal((120, 90, new Vector2(40f, 30f), 64), Assert.Single(during));
            Assert.Equal((40, 30), (target.width, target.height));
            Assert.Same(gpu, native.Gpu);
            Assert.Equal(default, native.CaptureLogicalSize);
        }
    }
}
