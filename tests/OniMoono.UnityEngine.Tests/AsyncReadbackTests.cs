using System;
using System.Runtime.InteropServices;
using OniMoono.Engine.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    /// AsyncGPUReadback of a render texture through the device's own read-back
    /// (RenderDevice.BeginReadback): the main thread never waits for it, the request is done no
    /// earlier than three frames on and no earlier than the device has the texels, and its data
    /// is the device's until the frame after.
    public class AsyncReadbackTests : EngineTest, IDisposable
    {
        readonly RenderDevice previous = GraphicsDevice.Current;

        /// A RenderTexture reads the project's colour space (PlayerSettings) when it is made.
        public AsyncReadbackTests()
        {
            OniMoono.Engine.Assets.AssetSystem.Initialize(GameInstall.DataDir);
            OniMoono.Engine.Assets.ProjectSettings.Load();
        }

        void IDisposable.Dispose()
        {
            GraphicsDevice.Install(previous);
            base.Dispose();
        }

        /// A device read-back that has its texels after `polls` polls.
        sealed unsafe class FakeReadback : DeviceReadback
        {
            public int PollsLeft, Polls, Waits, Releases;
            public bool Fails;

            public FakeReadback(int polls, int size)
            {
                PollsLeft = polls;
                Data = (byte*)NativeMemory.Alloc((nuint)size);
                for (int i = 0; i < size; i++) Data[i] = (byte)(i * 7);
                Size = size;
            }

            public override void Poll()
            {
                Polls++;
                if (--PollsLeft <= 0) Finish();
            }

            void Finish()
            {
                Failed = Fails;
                Ready = true;
            }

            public override void Wait()
            {
                Waits++;
                Finish();
            }

            public override void Release()
            {
                Releases++;
                NativeMemory.Free(Data);
                Data = null;
            }
        }

        [Fact]
        public void TheRequestWaitsForTheDeviceButNotTheMainThread()
        {
            var device = new RecordingDevice();
            GraphicsDevice.Install(device);
            FakeReadback read = null;
            device.OnBeginReadback = (_, w, h, format) => read = new FakeReadback(polls: 5, size: w * h * 4);
            int reads = 0;
            device.OnReadPixels = (_, _, _, _, _) => reads++;
            var rt = new RenderTexture(4, 2, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            int calls = 0;
            byte[] seen = null;
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, r =>
            {
                calls++;
                seen = r.GetData<byte>().ToArray();
            });
            Assert.NotNull(read);
            Assert.Equal(0, reads);
            Assert.Equal((4, 2), (request.width, request.height));
            // Three frames would do for the frame count; the device has the texels at the fifth.
            for (int i = 0; i < 4; i++)
            {
                Frame();
                Assert.False(request.done);
            }
            Frame();
            Assert.True(request.done);
            Assert.False(request.hasError);
            Assert.Equal(1, calls);
            Assert.Equal(0, read.Waits);
            Assert.Equal(32, seen.Length);
            Assert.Equal((byte)(5 * 7), seen[5]);
            Assert.Equal(0, read.Releases);
            Frame();
            Assert.Equal(1, read.Releases);
            Assert.Throws<InvalidOperationException>(() => request.GetData<byte>());
            Object.DestroyImmediate(rt);
        }

        [Fact]
        public void AReadbackTheDeviceHasAtOnceIsStillDoneThreeFramesOn()
        {
            var device = new RecordingDevice();
            GraphicsDevice.Install(device);
            device.OnBeginReadback = (_, w, h, format) => new FakeReadback(polls: 1, size: w * h * 4);
            var rt = new RenderTexture(4, 2, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            Frame();
            Frame();
            Assert.False(request.done);
            Frame();
            Assert.True(request.done);
            Object.DestroyImmediate(rt);
        }

        [Fact]
        public void WaitingForCompletionWaitsForTheDevice()
        {
            var device = new RecordingDevice();
            GraphicsDevice.Install(device);
            FakeReadback read = null;
            device.OnBeginReadback = (_, w, h, format) => read = new FakeReadback(polls: 100, size: w * h * 4);
            var rt = new RenderTexture(4, 2, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            int calls = 0;
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, _ => calls++);
            request.WaitForCompletion();
            Assert.True(request.done);
            Assert.Equal((1, 1), (read.Waits, calls));
            Assert.Equal(32, request.GetData<byte>().Length);
            var second = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, _ => calls++);
            AsyncGPUReadback.WaitAllRequests();
            Assert.True(second.done);
            Assert.Equal(2, calls);
            Frame();
            Frame();
            Object.DestroyImmediate(rt);
        }

        [Fact]
        public void AReadbackTheDeviceCouldNotMakeIsDoneWithAnError()
        {
            var device = new RecordingDevice();
            GraphicsDevice.Install(device);
            device.OnBeginReadback = (_, w, h, format) => new FakeReadback(polls: 1, size: 4) { Fails = true };
            var rt = new RenderTexture(4, 2, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            bool error = false;
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, r => error = r.hasError);
            for (int i = 0; i < 3; i++) Frame();
            Assert.True(request.done);
            Assert.True(error);
            Assert.Throws<InvalidOperationException>(() => request.GetData<byte>());
            Frame();
            Object.DestroyImmediate(rt);
        }
    }
}
