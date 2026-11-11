using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering
{
    /// Texture read-backs (U60's Timelapser, mods: Fast Save's timelapse images). A render
    /// texture is read by the device without waiting for the GPU (GL: a pixel buffer and a fence;
    /// Vulkan: a copy into a host buffer the frame's fence covers); a texture's texels, or a
    /// render texture the device cannot read so, are read when the request is made. The request
    /// is done three frames later, as measured in the original player (a render texture read back
    /// to RGBA32), or once the device has the texels if that takes longer; its callback runs
    /// then, and its data stays readable for that frame only.
    public static partial class AsyncGPUReadback
    {
        internal const int Latency = 3;

        static readonly List<AsyncGPUReadbackRequest.State> pending = new();
        static bool hooked;

        public static AsyncGPUReadbackRequest Request(Texture src, int mipIndex = 0, Action<AsyncGPUReadbackRequest> callback = null)
            => Request(src, mipIndex, SourceFormat(src), callback);

        public static AsyncGPUReadbackRequest Request(Texture src, int mipIndex, TextureFormat dstFormat, Action<AsyncGPUReadbackRequest> callback = null)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            Threads.AssertMainThread("AsyncGPUReadback.Request");
            var state = new AsyncGPUReadbackRequest.State { Callback = callback, DueFrame = Time.frameCount + Latency };
            state.HasError = !Read(src, mipIndex, dstFormat, state);
            if (!hooked)
            {
                hooked = true;
                OniMoono.Engine.PlayerLoop.EarlyUpdate += Advance;
            }
            pending.Add(state);
            return new AsyncGPUReadbackRequest(state);
        }

        public static void WaitAllRequests()
        {
            foreach (var state in pending.ToArray()) Complete(state);
        }

        /// The texels of one mip level in `format`, into the state's buffer; false when the source
        /// cannot be read (no such level, no texel data, a format the engine cannot encode).
        static unsafe bool Read(Texture src, int mipIndex, TextureFormat format, AsyncGPUReadbackRequest.State state)
        {
            if (format == (TextureFormat)(-1) || !TextureFormats.CanEncode(format) || mipIndex < 0) return false;
            int width = Math.Max(1, src.width >> mipIndex), height = Math.Max(1, src.height >> mipIndex);
            if (src.NativeOrThrow is RenderTextureNative started && mipIndex == 0 && started.Created
                && GraphicsDevice.Current.BeginReadback(started, width, height, format) is { } device)
            {
                state.Device = device;
                state.Width = width;
                state.Height = height;
                return true;
            }
            var copy = new Texture2D(width, height, format, false);
            try
            {
                var target = (Texture2DNative)copy.NativeOrThrow;
                switch (src.NativeOrThrow)
                {
                    case RenderTextureNative rt:
                        if (mipIndex != 0 || !rt.Created) return false;
                        GraphicsDevice.Current.ReadPixels(rt, new RectInt(0, 0, width, height), target, 0, 0);
                        break;
                    case Texture2DNative texture:
                        if (mipIndex >= texture.MipCount || texture.Memory == null || !TextureFormats.CanDecode(texture.Format)) return false;
                        var level = texture.Memory.Span.Slice((int)texture.MipOffset(mipIndex), (int)texture.MipSize(mipIndex));
                        var into = target.Memory.Span;
                        for (int y = 0; y < height; y++)
                            for (int x = 0; x < width; x++)
                                TextureFormats.Encode(format, into, width, x, y, TextureFormats.Decode(texture.Format, level, width, x, y));
                        break;
                    default:
                        return false;
                }
                var bytes = target.Memory.Span.Slice(0, (int)target.MipSize(0));
                state.Data = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
                bytes.CopyTo(new Span<byte>(state.Data, bytes.Length));
                state.Size = bytes.Length;
                state.Width = width;
                state.Height = height;
                return true;
            }
            finally
            {
                Object.DestroyImmediate(copy);
            }
        }

        /// A render texture's colour format as a texture format; a texture's own.
        static TextureFormat SourceFormat(Texture src) => src?.NativeOrThrow switch
        {
            Texture2DNative texture => texture.Format,
            RenderTextureNative rt => rt.ColorFormat switch
            {
                GraphicsFormat.R8G8B8A8_UNorm or GraphicsFormat.R8G8B8A8_SRGB => TextureFormat.RGBA32,
                GraphicsFormat.B8G8R8A8_UNorm or GraphicsFormat.B8G8R8A8_SRGB => TextureFormat.BGRA32,
                GraphicsFormat.R16G16B16A16_SFloat => TextureFormat.RGBAHalf,
                GraphicsFormat.R32G32B32A32_SFloat => TextureFormat.RGBAFloat,
                GraphicsFormat.R32_SFloat => TextureFormat.RFloat,
                GraphicsFormat.R16_SFloat => TextureFormat.RHalf,
                GraphicsFormat.R8_UNorm => TextureFormat.R8,
                GraphicsFormat.R8G8_UNorm => TextureFormat.RG16,
                _ => (TextureFormat)(-1),
            },
            _ => (TextureFormat)(-1),
        };

        /// Requests whose frame has come are done (their callbacks run); a done request's data is
        /// released the frame after.
        static void Advance()
        {
            int frame = Time.frameCount;
            foreach (var state in pending.ToArray())
            {
                if (!state.Done)
                {
                    var device = state.Device;
                    if (device != null && !device.Ready) device.Poll();
                    if (frame >= state.DueFrame && (device == null || device.Ready)) Complete(state);
                }
                else if (frame > state.DoneFrame) Release(state);
            }
        }

        /// Runs the callback, first waiting for the device's texels if they are not there yet
        /// (WaitForCompletion, WaitAllRequests).
        internal static unsafe void Complete(AsyncGPUReadbackRequest.State state)
        {
            if (state.Done) return;
            if (state.Device is { } device)
            {
                if (!device.Ready) device.Wait();
                if (device.Failed) state.HasError = true;
                else
                {
                    state.Data = device.Data;
                    state.Size = device.Size;
                }
            }
            state.Done = true;
            state.DoneFrame = Time.frameCount;
            state.Callback?.Invoke(new AsyncGPUReadbackRequest(state));
        }

        static unsafe void Release(AsyncGPUReadbackRequest.State state)
        {
            pending.Remove(state);
            if (state.Device != null) state.Device.Release();
            else if (state.Data != null) NativeMemory.Free(state.Data);
            state.Device = null;
            state.Data = null;
            state.Released = true;
        }
    }

    public partial struct AsyncGPUReadbackRequest
    {
        internal sealed unsafe class State
        {
            public Action<AsyncGPUReadbackRequest> Callback;
            public int DueFrame, DoneFrame;
            public bool Done, HasError, Released;
            public byte* Data;
            public int Size, Width, Height;
            /// The device's read-back, while it reads (its texels are Data once done).
            public DeviceReadback Device;
        }

        readonly State state;

        internal AsyncGPUReadbackRequest(State state) => this.state = state;

        public bool done => state == null || state.Done;
        public bool hasError => state == null || state.HasError;
        public int layerCount => state?.HasError == false ? 1 : 0;
        public int layerDataSize => state?.HasError == false ? state.Size : 0;
        public int width => state?.Width ?? 0;
        public int height => state?.Height ?? 0;
        public int depth => state?.HasError == false ? 1 : 0;

        /// The request's status is current: it changes as frames pass.
        public void Update() { }

        public void WaitForCompletion()
        {
            if (state != null) AsyncGPUReadback.Complete(state);
        }

        public unsafe NativeArray<T> GetData<T>(int layer = 0) where T : struct
        {
            if (state == null || !state.Done || state.HasError || state.Released)
                throw new InvalidOperationException("Cannot access the data as it is not available");
            if (layer != 0) throw new ArgumentException($"Layer index is out of range {layer} / 1");
            return new NativeArray<T>(state.Data, state.Size / Unsafe.SizeOf<T>(), owned: false);
        }
    }
}
