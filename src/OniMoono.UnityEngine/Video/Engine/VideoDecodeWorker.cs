using System;
using System.Collections.Generic;
using System.Threading;

namespace OniMoono.Engine.Video
{
    /// The video track of a movie, demuxed: the file bytes and the frames in decode order with
    /// their presentation times.
    internal sealed class VideoStream
    {
        public byte[] Data;
        public WebmFrame[] Frames;
        public int Width, Height;
        public bool HasAudio;

        public int FrameCount => Frames.Length;
        /// Black frames standing in for a movie a stripped copy dropped (NoContent).
        public bool IsBlank;

        /// `frameCount` black frames at `frameRate`, 16×16 (a VideoClip's own length, for the
        /// clip a stripped copy of the game has no movie for).
        public static VideoStream Blank(ulong frameCount, double frameRate)
        {
            double rate = frameRate > 0 ? frameRate : 30;
            var frames = new WebmFrame[(int)Math.Clamp(frameCount, 1UL, 1UL << 20)];
            for (int i = 0; i < frames.Length; i++) frames[i] = new WebmFrame(0, 0, (long)Math.Round(i * 1e9 / rate), true);
            return new VideoStream { Data = [], Frames = frames, Width = 16, Height = 16, IsBlank = true };
        }

        /// Presentation time of a frame in seconds (its container timestamp).
        public double TimeOf(int index) => Frames[index].TimestampNs * 1e-9;

        /// Demuxes a WebM file and checks that its first video track is VP8 starting with a keyframe.
        public static VideoStream FromWebm(byte[] data)
        {
            var file = WebmDemuxer.Parse(data);
            var track = file.FirstVideoTrack ?? throw new System.IO.InvalidDataException("The WebM file has no video track.");
            if (track.CodecId != "V_VP8") throw new System.IO.InvalidDataException($"Video codec {track.CodecId} is not supported (only V_VP8).");
            var frames = new List<WebmFrame>(file.FramesOf(track)).ToArray();
            if (frames.Length == 0) throw new System.IO.InvalidDataException("The WebM video track has no frames.");
            if (!frames[0].Keyframe) throw new System.IO.InvalidDataException("The WebM video track does not start with a keyframe.");
            if (track.PixelWidth <= 0 || track.PixelHeight <= 0) throw new System.IO.InvalidDataException("The WebM video track has no picture size.");
            return new VideoStream { Data = data, Frames = frames, Width = track.PixelWidth, Height = track.PixelHeight, HasAudio = file.HasAudio };
        }

        /// The last keyframe at or before `index`.
        public int KeyframeAtOrBefore(int index)
        {
            for (int i = index; i > 0; i--)
                if (Frames[i].Keyframe) return i;
            return 0;
        }

        /// The frame on screen at `time`: the last one whose presentation time is not after it
        /// (frame 0 before the first timestamp). Half a millisecond of slack absorbs the
        /// millisecond rounding of WebM timecodes.
        public int FrameAt(double time)
        {
            long ns = (long)Math.Round(time * 1e9) + 500_000;
            int lo = 0, hi = Frames.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (Frames[mid].TimestampNs <= ns) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }
    }

    /// A decoded, converted frame waiting to be presented.
    internal sealed class DecodedFrame
    {
        public int Generation;
        /// Frame index in the movie.
        public int Index;
        /// How many times decoding wrapped to the first frame since the request that produced it.
        public int Wrap;
        /// RGBA32, bottom-up rows.
        public byte[] Rgba;
    }

    /// Loads, demuxes and decodes one movie on its own thread. The main thread requests a start
    /// time (`Start`); the worker decodes from the keyframe preceding that frame and queues
    /// converted frames from it on, in order, wrapping to frame 0 at the end while `Looping` is
    /// set. At most `Capacity` frames are held; the worker waits for the main thread to consume
    /// them. Frames the main thread will not show (`SkipBefore`) are decoded but not converted.
    internal sealed class VideoDecodeWorker : IDisposable
    {
        const int Capacity = 3;
        /// Late frames in a row after which one is converted anyway.
        const int MaxLateRun = 4;
        int lateRun;

        readonly Func<VideoStream> load;
        readonly Thread thread;
        readonly object gate = new();
        readonly Queue<DecodedFrame> ready = new();
        readonly Stack<byte[]> pool = new();
        int buffersCreated;
        VideoStream stream;

        // Guarded by `gate`.
        int generation;
        double startTime;
        bool requested;
        bool disposed;
        int skipWrap, skipIndex;
        bool looping;
        string error;

        public VideoDecodeWorker(Func<VideoStream> load, string name, bool looping)
        {
            this.load = load;
            this.looping = looping;
            thread = new Thread(Run) { IsBackground = true, Name = "Video decode: " + name };
            thread.Start();
        }

        /// The demuxed movie, once loaded (set before the first frame is queued).
        public VideoStream Stream => Volatile.Read(ref stream);

        /// Why decoding stopped, once it failed.
        public string Error
        {
            get { lock (gate) return error; }
        }

        public bool Looping
        {
            set
            {
                lock (gate)
                {
                    looping = value;
                    Monitor.PulseAll(gate);
                }
            }
        }

        /// Discards queued frames and restarts decoding at the frame shown at `time`. Returns the
        /// generation that tags the frames of this request.
        public int Start(double time)
        {
            lock (gate)
            {
                generation++;
                startTime = time;
                requested = true;
                skipWrap = 0;
                skipIndex = 0;
                lateRun = 0;
                while (ready.Count > 0) pool.Push(ready.Dequeue().Rgba);
                Monitor.PulseAll(gate);
                return generation;
            }
        }

        /// Frames before (wrap, index) of the current request need not be converted.
        public void SkipBefore(int wrap, int index)
        {
            lock (gate)
            {
                skipWrap = wrap;
                skipIndex = index;
            }
        }

        /// The oldest queued frame, without removing it.
        public DecodedFrame Peek()
        {
            lock (gate) return ready.Count > 0 ? ready.Peek() : null;
        }

        /// Removes the oldest queued frame; the caller hands its buffer back with `Recycle`.
        public DecodedFrame Dequeue()
        {
            lock (gate) return ready.Count > 0 ? ready.Dequeue() : null;
        }

        public void Recycle(DecodedFrame frame)
        {
            lock (gate)
            {
                if (frame.Rgba != null) pool.Push(frame.Rgba);
                frame.Rgba = null;
                Monitor.PulseAll(gate);
            }
        }

        /// Asks the worker to stop. It does not wait: the thread finishes the step it is in
        /// (loading the movie, decoding a frame) and then releases the decoder itself.
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                ready.Clear();
                pool.Clear();
                Monitor.PulseAll(gate);
            }
        }

        /// Waits for the decode thread to finish after Dispose (tests).
        public bool Join(int milliseconds) => thread.Join(milliseconds);

        unsafe void Run()
        {
            Vp8.Vp8Decoder decoder = null;
            try
            {
                var s = load();
                Volatile.Write(ref stream, s);
                int frameBytes = s.Width * s.Height * 4;
                if (!s.IsBlank) decoder = new Vp8.Vp8Decoder();
                int decodedUpTo = -1; // the last frame fed to the decoder
                int myGeneration = -1, next = 0, wrap = 0;
                while (true)
                {
                    lock (gate)
                    {
                        // Idle until a request, and while parked at the end of a non-looping movie.
                        while (!disposed && !requested && (myGeneration < 0 || (next >= s.FrameCount && !looping)))
                            Monitor.Wait(gate);
                        if (disposed) return;
                        if (requested)
                        {
                            requested = false;
                            myGeneration = generation;
                            next = s.FrameAt(startTime);
                            skipIndex = Math.Max(skipIndex, next);
                            wrap = 0;
                            // Continue from the decoder state when it is between the start frame's
                            // keyframe and the start frame; otherwise restart at that keyframe.
                            int key = s.KeyframeAtOrBefore(next);
                            if (!(decodedUpTo >= key && decodedUpTo < next)) decodedUpTo = key - 1;
                        }
                        if (next >= s.FrameCount)
                        {
                            next = 0;
                            wrap++;
                            decodedUpTo = -1;
                        }
                    }

                    // Bring the decoder up to the frame before `next`.
                    while (decodedUpTo < next - 1 && !StaleOrDisposed(myGeneration))
                    {
                        decodedUpTo++;
                        decoder?.Decode(FrameBytes(s, decodedUpTo));
                    }
                    if (StaleOrDisposed(myGeneration)) continue;

                    int index = next++;
                    bool shown = decoder == null || decoder.Decode(FrameBytes(s, index));
                    decodedUpTo = index;
                    if (!shown) continue; // a frame with nothing to show

                    byte[] buffer;
                    lock (gate)
                    {
                        if (myGeneration != generation) continue;
                        // A frame already late is not converted, unless the worker has been
                        // behind for MaxLateRun frames: catching up (a long movie starting
                        // seconds behind its clock) it still delivers every few frames, so the
                        // picture moves, as Unity's dropping playback does.
                        if (wrap < skipWrap || (wrap == skipWrap && index < skipIndex))
                        {
                            // Only while the player takes what is queued (not paused): a paused
                            // player keeps the worker catching up instead.
                            if (++lateRun < MaxLateRun || ready.Count > 0) continue;
                        }
                        lateRun = 0;
                        while (!disposed && myGeneration == generation && pool.Count == 0 && buffersCreated >= Capacity)
                            Monitor.Wait(gate);
                        if (disposed) return;
                        if (myGeneration != generation) continue;
                        if (pool.Count > 0) buffer = pool.Pop();
                        else
                        {
                            buffer = GC.AllocateUninitializedArray<byte>(frameBytes);
                            buffersCreated++;
                        }
                    }

                    if (decoder == null)
                    {
                        // Opaque black.
                        for (int i = 0; i < buffer.Length; i += 4) buffer[i] = buffer[i + 1] = buffer[i + 2] = 0;
                        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
                    }
                    else if (decoder.Width != s.Width || decoder.Height != s.Height)
                        throw new InvalidOperationException($"A decoded frame is {decoder.Width}x{decoder.Height}; the stream declares {s.Width}x{s.Height}.");
                    else fixed (byte* dst = buffer)
                        VideoFrameConverter.I420ToRgba32(decoder.PlaneY, decoder.StrideY, decoder.PlaneU, decoder.StrideU, decoder.PlaneV, decoder.StrideV,
                            s.Width, s.Height, dst);

                    lock (gate)
                    {
                        if (myGeneration != generation) pool.Push(buffer);
                        else ready.Enqueue(new DecodedFrame { Generation = myGeneration, Index = index, Wrap = wrap, Rgba = buffer });
                    }
                }
            }
            catch (Exception e)
            {
                lock (gate) error = e.Message;
            }
            finally
            {
                decoder?.Dispose();
            }
        }

        bool StaleOrDisposed(int myGeneration)
        {
            lock (gate) return disposed || myGeneration != generation;
        }

        static ReadOnlySpan<byte> FrameBytes(VideoStream s, int index)
        {
            var f = s.Frames[index];
            return new ReadOnlySpan<byte>(s.Data, f.Offset, f.Size);
        }
    }
}
