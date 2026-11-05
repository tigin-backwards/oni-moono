using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Video;
using UnityEngine;
using UnityEngine.Video;
using Xunit;
using Object = UnityEngine.Object;

namespace OniMoono.Tests
{
    public class VideoTests : EngineTest
    {
        static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-762107");
                var dir = Path.Combine(game, "OxygenNotIncluded_Data");
                if (!Directory.Exists(dir)) throw new InvalidOperationException($"Game data not found at {dir}; set ONI_GAME_DIR.");
                return dir;
            }
        }

        static List<VideoClip> AllClips()
        {
            AssetSystem.Initialize(DataDir);
            var file = AssetSystem.Get("resources.assets");
            var clips = new List<VideoClip>();
            foreach (var info in file.Objects)
                if (file.TypeOf(info).ClassId == 329)
                    clips.Add((VideoClip)ObjectLoader.Load(file, info.PathId));
            return clips;
        }

        static VideoClip Clip(string name) => AllClips().Find(c => c.name == name) ?? throw new InvalidOperationException("No clip " + name);

        [Fact]
        public void EveryClipDemuxesToItsDeclaredFrames()
        {
            var clips = AllClips();
            Assert.Equal(18, clips.Count);
            foreach (var clip in clips)
            {
                var native = (VideoClipNative)clip.Native;
                Assert.StartsWith("Assets/resources/video_webm/", clip.originalPath);
                var file = WebmDemuxer.Parse(native.ReadMovie());
                Assert.Single(file.Tracks);
                Assert.False(file.HasAudio);
                Assert.Equal(0, clip.audioTrackCount);
                var track = file.FirstVideoTrack;
                Assert.Equal("V_VP8", track.CodecId);
                Assert.Equal((int)clip.width, track.PixelWidth);
                Assert.Equal((int)clip.height, track.PixelHeight);
                var frames = file.FramesOf(track);
                Assert.Equal(clip.frameCount, (ulong)frames.Count);
                Assert.True(frames[0].Keyframe);
                // Container timecodes are milliseconds: frame i is at i / frameRate, rounded.
                for (int i = 0; i < frames.Count; i++)
                    Assert.InRange(frames[i].TimestampNs * 1e-9 - i / clip.frameRate, -0.0006, 0.0006);
                Assert.Equal(clip.frameCount / clip.frameRate, clip.length, 9);
                // The segment ends after the last frame and no later than the clip's length.
                Assert.InRange(file.DurationNs * 1e-9, frames[^1].TimestampNs * 1e-9, clip.length + 0.0006);
            }
        }

        [Fact]
        public void ADecoderBehindTheClockStillDeliversFrames()
        {
            // A 1080p intro behind its audio clock (seconds of loading) is decoded faster than
            // real time but every frame is late until it catches up: the picture must still move,
            // as Unity's dropping playback does, not hold its first frame.
            var clip = AllClips().OrderBy(c => c.frameCount).First();
            var native = (VideoClipNative)clip.Native;
            var worker = new VideoDecodeWorker(() => VideoStream.FromWebm(native.ReadMovie()), clip.name, looping: false);
            try
            {
                worker.Start(0);
                worker.SkipBefore(0, int.MaxValue); // every frame is late
                var indices = new List<int>();
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 20 && indices.Count < 3)
                {
                    var f = worker.Dequeue();
                    if (f == null) { Thread.Sleep(2); continue; }
                    indices.Add(f.Index);
                    worker.Recycle(f);
                }
                Assert.True(indices.Count >= 3, $"{indices.Count} frames while every frame was late");
                Assert.True(indices[1] > indices[0] && indices[2] > indices[1]);
            }
            finally
            {
                worker.Dispose();
            }
        }

        [Fact]
        public unsafe void ConversionFollowsVideoDecodeAndFlipsRows()
        {
            // 2×2 grey picture (one neutral chroma sample): top row Y 16 and Y 235, bottom row
            // Y 81. VideoDecode's luma gain is 1.15625, so nominal white comes out as 253.
            byte[] y = [16, 235, 81, 81];
            byte[] u = [128];
            byte[] v = [128];
            var rgba = new byte[16];
            fixed (byte* py = y, pu = u, pv = v, dst = rgba)
                VideoFrameConverter.I420ToRgba32(py, 2, pu, 1, pv, 1, 2, 2, dst);
            // Bottom-up: the picture's top row is the texture's second row.
            Assert.Equal(new byte[] { 0, 0, 0, 255, 253, 253, 253, 255 }, rgba[8..16]);
            Assert.Equal(new byte[] { 75, 75, 75, 255, 75, 75, 75, 255 }, rgba[0..8]);

            byte[] redY = [81], redU = [90], redV = [240];
            var red = new byte[4];
            fixed (byte* py = redY, pu = redU, pv = redV, dst = red)
                VideoFrameConverter.I420ToRgba32(py, 1, pu, 1, pv, 1, 1, 1, dst);
            Assert.Equal(new byte[] { 254, 0, 0, 255 }, red);
        }

        [Fact]
        public void DecoderMethodsAreCompiledOptimizedFromTheirFirstCall()
        {
            // Unoptimized tier-0 code decoded the menu's intro four times slower than real time
            // during start-up (Vp8Decoder's summary).
            var types = typeof(VideoFrameConverter).Assembly.GetTypes()
                .Where(t => t.Namespace == "OniMoono.Engine.Video.Vp8" || t == typeof(VideoFrameConverter));
            var flags = System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var methods = types.SelectMany(t => t.GetMethods(flags))
                .Where(m => !m.IsSpecialName && !m.IsAbstract && m.Name != "Finalize" && !m.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
                            && !m.DeclaringType.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                .ToList();
            Assert.Contains(methods, m => m.Name == "Decode");
            var unoptimized = methods
                .Where(m => (m.MethodImplementationFlags & System.Reflection.MethodImplAttributes.AggressiveOptimization) == 0)
                .Select(m => m.DeclaringType.Name + "." + m.Name);
            Assert.Empty(unoptimized);
        }

        [Fact]
        public unsafe void FirstFrameDecodesToAnImage()
        {
            var clip = Clip("Artifact_loop");
            var go = new GameObject("Video");
            var vp = go.AddComponent<VideoPlayer>();
            var prepared = 0;
            vp.prepareCompleted += p => prepared++;
            vp.clip = clip;
            vp.Prepare();
            Assert.False(vp.isPrepared);
            PumpUntil(() => vp.isPrepared);
            Assert.Equal(1, prepared);
            Assert.False(vp.isPlaying);
            Assert.Equal(0, vp.frame);
            Assert.Equal(0.0, vp.time);

            var texture = Assert.IsType<Texture2D>(vp.texture);
            Assert.Equal(1920, texture.width);
            Assert.Equal(1080, texture.height);
            Assert.Equal(TextureFormat.RGBA32, texture.format);
            Assert.True(texture.updateCount > 0);
            var memory = ((Texture2DNative)texture.Native).Memory.Span;
            long sum = 0, sumSq = 0;
            var distinct = new HashSet<int>();
            for (int i = 0; i < memory.Length; i += 4)
            {
                Assert.Equal(255, memory[i + 3]);
                int luma = (memory[i] * 2 + memory[i + 1] * 5 + memory[i + 2]) >> 3;
                sum += luma;
                sumSq += luma * luma;
                distinct.Add(memory[i] | memory[i + 1] << 8 | memory[i + 2] << 16);
            }
            long n = memory.Length / 4;
            double mean = (double)sum / n, variance = (double)sumSq / n - mean * mean;
            Assert.True(variance > 100, $"variance {variance}");
            Assert.True(distinct.Count > 1000, $"{distinct.Count} colours");

            vp.Stop();
            Assert.False(vp.isPrepared);
            Assert.Null(vp.texture);
            Object.DestroyImmediate(go);
        }

        [Fact]
        public void PlaybackStateTransitionsAndEvents()
        {
            var clip = Clip("Artifact_loop"); // 341 frames at 30 fps
            var go = new GameObject("Video");
            var vp = go.AddComponent<VideoPlayer>();
            var native = (VideoPlayerNative)vp.Native;
            var events = new List<string>();
            vp.prepareCompleted += p => events.Add("prepared");
            vp.started += p => events.Add("started");
            vp.loopPointReached += p => events.Add("loop");
            vp.seekCompleted += p => events.Add("seek");
            vp.frameDropped += p => events.Add("dropped");

            vp.clip = clip;
            Assert.Equal(341UL, vp.frameCount);
            Assert.Equal(30f, vp.frameRate);
            Assert.Equal(341 / 30.0, vp.length, 9);
            // Frames are presented in order, one per update when the clock allows, so the steps
            // below are independent of decoding speed.
            vp.skipOnDrop = false;
            vp.Play();
            Assert.True(vp.isPlaying);
            Assert.False(vp.isPrepared);
            PumpUntil(() => vp.isPrepared);
            Assert.Equal(new[] { "prepared", "started" }, events);
            Assert.Equal(0, vp.frame);

            for (int i = 1; i <= 10; i++)
            {
                WaitForDecodedFrame(native);
                Frame(1f / 30f);
                Assert.Equal(i, vp.frame);
            }
            Assert.Equal(0.333, vp.time, 3);

            vp.Pause();
            Assert.False(vp.isPlaying);
            Assert.True(vp.isPaused);
            Frame(1f / 30f);
            Frame(1f / 30f);
            Assert.Equal(10, vp.frame);

            vp.time = 11.0;
            PumpUntil(() => events.Contains("seek"));
            Assert.Equal(330, vp.frame);
            Assert.Equal(11.0, vp.time, 3);

            events.Clear();
            vp.Play();
            Assert.Equal(new[] { "started" }, events);
            for (int i = 0; i < 40 && !events.Contains("loop"); i++)
            {
                if (vp.frame < 340) WaitForDecodedFrame(native);
                Frame(1f / 30f);
            }
            Assert.Equal(new[] { "started", "loop" }, events);
            Assert.False(vp.isPlaying);
            Assert.True(vp.isPrepared);
            Assert.Equal(340, vp.frame);

            // Looping: playing again starts over and wraps at the end.
            events.Clear();
            vp.isLooping = true;
            vp.Play();
            PumpUntil(() => events.Contains("seek"));
            Assert.Equal(0, vp.frame);
            vp.time = 11.2;
            PumpUntil(() => events.Count(e => e == "seek") == 2);
            for (int i = 0; i < 40 && vp.frame != 1; i++)
            {
                WaitForDecodedFrame(native);
                Frame(1f / 30f);
            }
            Assert.Contains("loop", events);
            Assert.True(vp.isPlaying);
            Assert.Equal(1, vp.frame);

            vp.Stop();
            Assert.False(vp.isPlaying);
            Assert.False(vp.isPrepared);
            Assert.Equal(0.0, vp.time);
            Assert.Equal(-1, vp.frame);
            Assert.DoesNotContain("dropped", events);
            Object.DestroyImmediate(go);
        }

        [Fact]
        public void RenderTextureTargetReceivesFramesAndExternalTimeDrivesTheClock()
        {
            var clip = Clip("Stay_loop");
            var go = new GameObject("Video");
            go.SetActive(false);
            var vp = go.AddComponent<VideoPlayer>();
            var native = (VideoPlayerNative)vp.Native;
            native.RenderMode = VideoRenderTarget.RenderTexture;
            // A render texture described directly (its constructor reads the project's colour space).
            var rt = ObjectLoader.CreateNative<RenderTexture>(typeof(RenderTexture), new RenderTextureNative { Width = (int)clip.width, Height = (int)clip.height, DepthBits = 16 });
            vp.targetTexture = rt;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            vp.clip = clip;
            vp.timeReference = VideoTimeReference.ExternalTime;
            // playOnAwake starts playback when the component becomes enabled.
            go.SetActive(true);
            Assert.True(vp.isPlaying);
            Assert.Same(rt, vp.texture);
            PumpUntil(() => vp.isPrepared);
            var frameTexture = VideoOutputs.RenderTextureContent(rt, out var aspect);
            Assert.NotNull(frameTexture);
            Assert.Equal(VideoAspect.FitVertically, aspect);
            uint updates = frameTexture.updateCount;

            // The reference clock jumps to 2 s: the player catches up (skipping frames).
            vp.externalReferenceTime = 2.0;
            var deadline = Stopwatch.StartNew();
            while (vp.frame < 60 && deadline.ElapsedMilliseconds < 10000)
            {
                Frame(0f);
                Thread.Sleep(5);
            }
            Assert.Equal(60, vp.frame);
            Assert.True(frameTexture.updateCount > updates);

            // Deactivating the GameObject stops playback; the render texture is no longer fed.
            go.SetActive(false);
            Assert.False(vp.isPlaying);
            Assert.Null(VideoOutputs.RenderTextureContent(rt, out _));
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(rt);
        }

        [Fact]
        public void VideoPlayerDataLoadsFromThePrefabs()
        {
            AssetSystem.Initialize(DataDir);
            int players = 0;
            foreach (var name in new[] { "sharedassets0.assets", "sharedassets2.assets" })
            {
                var file = AssetSystem.Get(name);
                foreach (var info in file.Objects)
                {
                    if (file.TypeOf(info).ClassId != 328) continue;
                    // Bound on its own: loading the whole prefab needs the game's script assemblies.
                    var native = new VideoPlayerNative();
                    native.Bind(TypeTreeReader.ReadNative(328, file.OpenObject(info)), new NullResolver());
                    var vp = ObjectLoader.CreateNative<VideoPlayer>(typeof(VideoPlayer), native);
                    players++;
                    Assert.Null(vp.clip);
                    Assert.Equal(VideoRenderTarget.RenderTexture, native.RenderMode);
                    Assert.Equal(VideoClockSource.UnscaledGameTime, native.TimeUpdateMode);
                    Assert.True(vp.playOnAwake);
                    Assert.True(vp.skipOnDrop);
                    Assert.True(vp.waitForFirstFrame);
                    Assert.False(vp.isLooping);
                    Assert.Equal(1f, vp.playbackSpeed);
                    Assert.Equal(VideoAudioOutputMode.Direct, vp.audioOutputMode);
                    Assert.Equal(VideoTimeReference.Freerun, vp.timeReference);
                }
            }
            Assert.Equal(3, players);
        }

        sealed class NullResolver : OniMoono.Engine.Serialization.IReferenceResolver
        {
            public Object Resolve(PPtr pptr)
                => pptr.IsNull ? null : throw new InvalidOperationException("Unexpected reference " + pptr);
        }

        static void WaitForDecodedFrame(VideoPlayerNative native)
        {
            var deadline = Stopwatch.StartNew();
            while (!native.HasQueuedFrame)
            {
                if (deadline.ElapsedMilliseconds > 10000) throw new TimeoutException("No decoded frame.");
                Thread.Sleep(1);
            }
        }

        static void PumpUntil(Func<bool> condition)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                if (deadline.ElapsedMilliseconds > 15000) throw new TimeoutException("Condition not reached.");
                Frame(0f);
                Thread.Sleep(2);
            }
        }
    }
}
