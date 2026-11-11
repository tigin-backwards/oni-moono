using System;
using System.Collections.Generic;
using System.IO;
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
        public void ACaptureTooLargeForOneImageLogsAnErrorAndWritesNothing()
        {
            var device = Install(2560, 1440);
            int reads = 0;
            device.OnReadPixels = (_, _, _, _, _) => reads++;
            var path = Path.Combine(folder, "huge.png");
            ScreenCapture.CaptureScreenshot(path, 32);
            Frame();

            Assert.Equal(0, reads);
            Assert.False(File.Exists(path));
            Assert.Contains("81920x46080", Assert.Single(errors));
            errors.Clear();
        }
    }
}
