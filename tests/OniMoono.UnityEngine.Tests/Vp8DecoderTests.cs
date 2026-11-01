using System;
using System.Collections.Generic;
using System.IO;
using OniMoono.Engine.Assets;
using OniMoono.Engine.Video;
using OniMoono.Engine.Video.Vp8;
using UnityEngine.Video;
using Xunit;

namespace OniMoono.Tests
{
    /// The managed VP8 decoder against libvpx: VP8 decoding is exact by specification (RFC 6386),
    /// so the game's videos must decode to the same planes. The suite checks each clip's first
    /// 60 frames (its keyframe and inter frames); ONI_FULL_VIDEO_CHECK=1 checks every frame of
    /// every clip, a one-off for new game builds (docs/GAME-UPDATES.md).
    public class Vp8DecoderTests : EngineTest
    {
        static string DataDir
        {
            get
            {
                var game = Environment.GetEnvironmentVariable("ONI_GAME_DIR")
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects", "oni-linux-761211");
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

        static unsafe void AssertPlane(string what, byte* expected, int expectedStride, byte* actual, int actualStride, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                var e = new ReadOnlySpan<byte>(expected + (long)y * expectedStride, width);
                var a = new ReadOnlySpan<byte>(actual + (long)y * actualStride, width);
                if (e.SequenceEqual(a)) continue;
                int x = 0;
                while (e[x] == a[x]) x++;
                Assert.Fail($"{what}: row {y} differs from column {x} (libvpx {e[x]}, managed {a[x]})");
            }
        }

        [Fact]
        public unsafe void ClipsDecodeAsLibvpxDecodesThem()
        {
            int limit = Environment.GetEnvironmentVariable("ONI_FULL_VIDEO_CHECK") == "1" ? int.MaxValue : 60;
            var error = Vpx.EnsureLoaded();
            if (error != null) Assert.Skip(error);
            int frames = 0;
            foreach (var clip in AllClips())
            {
                var native = (VideoClipNative)clip.Native;
                var data = native.ReadMovie();
                var webm = WebmDemuxer.Parse(data);
                var track = webm.FirstVideoTrack;
                using var reference = new Vpx.Decoder(1);
                using var managed = new Vp8Decoder();
                int index = 0;
                foreach (var frame in webm.FramesOf(track))
                {
                    if (index == limit) break;
                    var packet = new ReadOnlySpan<byte>(data, frame.Offset, frame.Size);
                    var image = reference.Decode(packet);
                    bool shown = managed.Decode(packet);
                    string where = $"{clip.name} frame {index}";
                    Assert.True((image != null) == shown, $"{where}: shown {shown}, libvpx {(image != null ? "an image" : "none")}");
                    if (image != null)
                    {
                        int w = (int)image->DisplayWidth, h = (int)image->DisplayHeight;
                        Assert.Equal(w, managed.Width);
                        Assert.Equal(h, managed.Height);
                        AssertPlane(where + " Y", image->PlaneY, image->StrideY, managed.PlaneY, managed.StrideY, w, h);
                        AssertPlane(where + " U", image->PlaneU, image->StrideU, managed.PlaneU, managed.StrideU, (w + 1) / 2, (h + 1) / 2);
                        AssertPlane(where + " V", image->PlaneV, image->StrideV, managed.PlaneV, managed.StrideV, (w + 1) / 2, (h + 1) / 2);
                    }
                    index++;
                    frames++;
                }
            }
            Assert.True(frames > 0);
        }
    }
}
