using System;
using System.Collections.Generic;
using OniMoono.Assets.TypeTrees;
using OniMoono.Engine.Rendering;
using OniMoono.Engine.Serialization;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace OniMoono.Engine.Video
{
    /// Unity's VideoRenderMode (the enum is not part of the game's API surface).
    internal enum VideoRenderTarget
    {
        CameraFarPlane = 0,
        CameraNearPlane = 1,
        RenderTexture = 2,
        MaterialOverride = 3,
        APIOnly = 4,
    }

    /// Unity's VideoAspectRatio.
    internal enum VideoAspect
    {
        NoScaling = 0,
        FitVertically = 1,
        FitHorizontally = 2,
        FitInside = 3,
        FitOutside = 4,
        Stretch = 5,
    }

    /// Unity's VideoTimeUpdateMode.
    internal enum VideoClockSource
    {
        DSPTime = 0,
        GameTime = 1,
        UnscaledGameTime = 2,
    }

    /// Unity's VideoSource.
    internal enum VideoSourceKind
    {
        VideoClip = 0,
        Url = 1,
    }

    /// What a camera draws for a VideoPlayer targeting it (render modes CameraFarPlane and
    /// CameraNearPlane): the current frame, the player's targetCameraAlpha and aspect-ratio rule,
    /// and whether it goes in front of (near plane) or behind (far plane) the scene.
    internal readonly record struct CameraVideo(Texture Texture, float Alpha, VideoAspect Aspect, bool NearPlane);

    /// Engine side of a VideoPlayer (class 328).
    ///
    /// Playback model. A player is idle (nothing loaded), preparing (a decode worker loads the
    /// movie and decodes the first frame) or prepared. Prepare, Play and Pause start preparing;
    /// preparation completes on the main thread, in the video update, when the first frame is
    /// decoded: it is presented, then prepareCompleted is invoked, then started if Play is
    /// pending. While playing, each video update advances the playback clock by the time update
    /// mode's delta × playbackSpeed (or follows externalReferenceTime under ExternalTime) and
    /// presents the latest decoded frame whose presentation time has been reached. With
    /// skipOnDrop, frames the decoder delivers too late are skipped (frameDropped per frame);
    /// without it, at most one new frame is presented per update and the clock waits for it, so
    /// slow decoding slows playback down. When the clock reaches the movie's length,
    /// loopPointReached is invoked; a looping player wraps to the start, any other stops at the
    /// last frame (isPlaying false, still prepared). Setting time seeks: the clock holds until
    /// the frame at the new time is decoded, then seekCompleted is invoked.
    ///
    /// Frames are decoded and converted to RGBA on the worker (VideoFrameConverter: the CPU form
    /// of Hidden/VideoDecode) and copied into the player's frame texture, a readable RGBA32
    /// Texture2D whose ContentChanged() makes the device re-upload it.
    ///
    /// Outputs, by render mode: RenderTexture — the renderer copies the frame texture into the
    /// target texture (VideoOutputs.RenderTextureContent); CameraFarPlane/CameraNearPlane — the
    /// target camera draws it (VideoOutputs.CameraVideos); APIOnly — only VideoPlayer.texture.
    /// MaterialOverride is not supported and reports an error when playback is prepared.
    internal sealed class VideoPlayerNative : BehaviourNative
    {
        // ---- configuration (serialized) ----
        public VideoClip Clip;
        public string Url = "";
        public VideoSourceKind Source = VideoSourceKind.VideoClip;
        public VideoRenderTarget RenderMode = VideoRenderTarget.CameraFarPlane;
        public Camera TargetCamera;
        public float TargetCameraAlpha = 1f;
        public int TargetCamera3DLayout;
        public RenderTexture TargetTexture;
        public Renderer TargetMaterialRenderer;
        public string TargetMaterialProperty = "_MainTex";
        public VideoAspect AspectRatio = VideoAspect.FitVertically;
        public VideoClockSource TimeUpdateMode = VideoClockSource.GameTime;
        public VideoTimeReference TimeReference = VideoTimeReference.Freerun;
        public float PlaybackSpeed = 1f;
        public VideoAudioOutputMode AudioOutputMode = VideoAudioOutputMode.Direct;
        public ushort ControlledAudioTrackCount = 1;
        public bool PlayOnAwake = true;
        public bool SkipOnDrop = true;
        public bool Looping;
        public bool WaitForFirstFrame = true;
        public bool SendFrameReadyEvents;

        public override void Bind(Fields f, IReferenceResolver resolver)
        {
            Serialized = null;
            Clip = resolver.Resolve(f.PPtr("m_VideoClip")) as VideoClip;
            TargetCameraAlpha = f.Float("m_TargetCameraAlpha");
            TargetCamera3DLayout = f.Int("m_TargetCamera3DLayout");
            TargetCamera = resolver.Resolve(f.PPtr("m_TargetCamera")) as Camera;
            TargetTexture = resolver.Resolve(f.PPtr("m_TargetTexture")) as RenderTexture;
            TimeReference = (VideoTimeReference)f.Int("m_TimeReference");
            TargetMaterialRenderer = resolver.Resolve(f.PPtr("m_TargetMaterialRenderer")) as Renderer;
            TargetMaterialProperty = f.String("m_TargetMaterialProperty");
            RenderMode = (VideoRenderTarget)f.Int("m_RenderMode");
            AspectRatio = (VideoAspect)f.Int("m_AspectRatio");
            Source = (VideoSourceKind)f.Int("m_DataSource");
            TimeUpdateMode = (VideoClockSource)f.Int("m_TimeUpdateMode");
            PlaybackSpeed = f.Float("m_PlaybackSpeed");
            AudioOutputMode = (VideoAudioOutputMode)f.Int("m_AudioOutputMode");
            Url = f.String("m_Url");
            ControlledAudioTrackCount = f.Get<ushort>("m_ControlledAudioTrackCount");
            PlayOnAwake = f.Bool("m_PlayOnAwake");
            SkipOnDrop = f.Bool("m_SkipOnDrop");
            Looping = f.Bool("m_Looping");
            WaitForFirstFrame = f.Bool("m_WaitForFirstFrame");
            SendFrameReadyEvents = f.Bool("m_FrameReadyEventEnabled");
        }

        public override void RemapReferences(Func<Object, Object> map)
        {
            TargetCamera = (Camera)map(TargetCamera);
            TargetMaterialRenderer = (Renderer)map(TargetMaterialRenderer);
        }

        public override void CopyStateFrom(NativeObject source)
        {
            base.CopyStateFrom(source);
            var p = (VideoPlayerNative)source;
            Clip = p.Clip;
            Url = p.Url;
            Source = p.Source;
            RenderMode = p.RenderMode;
            TargetCamera = p.TargetCamera;
            TargetCameraAlpha = p.TargetCameraAlpha;
            TargetCamera3DLayout = p.TargetCamera3DLayout;
            TargetTexture = p.TargetTexture;
            TargetMaterialRenderer = p.TargetMaterialRenderer;
            TargetMaterialProperty = p.TargetMaterialProperty;
            AspectRatio = p.AspectRatio;
            TimeUpdateMode = p.TimeUpdateMode;
            TimeReference = p.TimeReference;
            PlaybackSpeed = p.PlaybackSpeed;
            AudioOutputMode = p.AudioOutputMode;
            ControlledAudioTrackCount = p.ControlledAudioTrackCount;
            PlayOnAwake = p.PlayOnAwake;
            SkipOnDrop = p.SkipOnDrop;
            Looping = p.Looping;
            WaitForFirstFrame = p.WaitForFirstFrame;
            SendFrameReadyEvents = p.SendFrameReadyEvents;
        }

        // ---- playback state ----

        enum Phase { Idle, Preparing, Prepared }

        Phase phase;
        VideoDecodeWorker worker;
        int generation;
        /// A Play is in effect (also while preparing): Unity's isPlaying.
        bool playing;
        bool paused;
        bool seeking;
        bool startedPending;
        /// Playback reached the end of a non-looping movie.
        bool ended;
        /// Playback position in the movie, seconds.
        double clock;
        /// Completed passes through the movie since playback (re)started; frames of request
        /// wrap w belong to pass `baseLoop + w`.
        int loop, baseLoop;
        int presentedIndex = -1;
        int presentedLoop;
        double externalTime;
        bool externalTimeSet;
        /// Bumped by every state change made from outside the update (API calls, event handlers),
        /// so the update stops after an event handler changed the player.
        int version;

        /// The RGBA32 texture frames are presented into; null until a frame is presented.
        public Texture2D FrameTexture;

        public bool IsPrepared => phase == Phase.Prepared;
        public bool IsPlaying => playing;
        public bool IsPaused => paused;
        public bool HasPresentedFrame => FrameTexture != null && presentedIndex >= 0;

        /// A decoded frame is waiting to be presented (tests step playback deterministically).
        public bool HasQueuedFrame => worker?.Peek() != null;

        VideoClipNative ClipData => Source == VideoSourceKind.VideoClip && Clip != null ? Clip.Native as VideoClipNative : null;

        public double FrameRate => ClipData?.FrameRate ?? 0;
        public ulong FrameCount => ClipData?.FrameCount ?? 0;
        public double Length => ClipData?.Length ?? 0;

        VideoPlayer Player => (VideoPlayer)Managed;

        /// time: the presentation time of the frame in the texture; while seeking or before
        /// preparation, the requested time.
        public double Time
        {
            get
            {
                if (phase == Phase.Prepared && !seeking && presentedIndex >= 0) return worker.Stream.TimeOf(presentedIndex);
                return clock;
            }
        }

        /// frame: the index of the frame in the texture, -1 when none is.
        public long Frame => phase == Phase.Prepared && !seeking ? presentedIndex : -1;

        // ---- API operations ----

        public void SetClip(VideoClip clip)
        {
            if (ReferenceEquals(Clip, clip)) return;
            // A new source invalidates the prepared content: playback stops.
            StopInternal(resetTime: true);
            Clip = clip;
        }

        public void SetLooping(bool value)
        {
            Looping = value;
            if (worker != null) worker.Looping = value;
        }

        public void Prepare()
        {
            version++;
            if (phase == Phase.Idle) BeginPrepare();
        }

        public void Play()
        {
            version++;
            if (playing) return;
            if (ended)
            {
                // Replaying a movie that ended starts it over.
                ended = false;
                clock = 0;
                loop = 0;
                Seek(0);
            }
            playing = true;
            paused = false;
            if (phase == Phase.Idle) BeginPrepare();
            if (phase == Phase.Idle)
            {
                playing = false;
                return;
            }
            if (phase == Phase.Prepared) Invoke(VideoPlayer.InvokeStarted);
            else startedPending = true;
        }

        public void Pause()
        {
            version++;
            playing = false;
            paused = true;
            if (phase == Phase.Idle) BeginPrepare();
        }

        public void Stop()
        {
            version++;
            StopInternal(resetTime: true);
        }

        public void SetTime(double value)
        {
            version++;
            if (double.IsNaN(value)) return;
            value = Math.Max(0, value);
            if (Length > 0) value = Math.Min(value, Length);
            clock = value;
            ended = false;
            if (phase != Phase.Idle) Seek(value);
        }

        public void SetFrame(long value)
        {
            var stream = worker?.Stream;
            if (stream != null && phase == Phase.Prepared)
                SetTime(stream.TimeOf((int)Math.Clamp(value, 0, stream.FrameCount - 1)));
            else if (FrameRate > 0)
                SetTime(Math.Max(0, value) / FrameRate);
        }

        public void SetExternalReferenceTime(double value)
        {
            externalTime = value;
            externalTimeSet = true;
        }

        public double ExternalReferenceTime => externalTime;

        // Restarts decoding at `time`; the clock holds until the first frame arrives.
        void Seek(double time)
        {
            generation = worker.Start(time);
            baseLoop = loop;
            if (phase == Phase.Prepared) seeking = true;
        }

        void BeginPrepare()
        {
            if (Source == VideoSourceKind.Url)
            {
                ReportError("VideoPlayer cannot play from a URL: only VideoClip sources are supported.");
                return;
            }
            var clipData = ClipData;
            if (clipData == null) return; // nothing to play: Unity's player stays idle without a source
            if (RenderMode == VideoRenderTarget.MaterialOverride)
            {
                ReportError("VideoPlayer render mode MaterialOverride is not supported.");
                return;
            }
            if (clipData.AudioChannelCounts.Length > 0 && AudioOutputMode != VideoAudioOutputMode.None)
                Debug.LogError($"VideoClip {clipData.Name} has audio tracks; video audio playback is not supported, the video plays without sound.", Managed);
            worker = new VideoDecodeWorker(() => VideoStream.FromWebm(clipData.ReadMovie()), clipData.Name, Looping);
            phase = Phase.Preparing;
            loop = baseLoop = 0;
            presentedIndex = -1;
            seeking = false;
            generation = worker.Start(clock);
            externalTimeSet = false;
            VideoSystem.Add(this);
        }

        void StopInternal(bool resetTime)
        {
            version++;
            playing = false;
            paused = false;
            seeking = false;
            startedPending = false;
            ended = false;
            phase = Phase.Idle;
            worker?.Dispose();
            worker = null;
            presentedIndex = -1;
            loop = baseLoop = 0;
            if (resetTime) clock = 0;
            if (FrameTexture != null)
            {
                Object.DestroyImmediate(FrameTexture);
                FrameTexture = null;
            }
            VideoSystem.Remove(this);
        }

        void ReportError(string message)
        {
            // Unity logs VideoPlayer errors to the console (and raises errorReceived, an event
            // whose delegate type is outside the game's API surface).
            Debug.LogError(message, Managed);
        }

        // ---- lifecycle ----

        public void OnEnable()
        {
            if (PlayOnAwake && ClipData != null) Play();
        }

        /// Disabling the component (or deactivating its GameObject) stops playback.
        public void OnDisable() => StopInternal(resetTime: true);

        public void OnDestroy() => StopInternal(resetTime: true);

        // ---- the video update (main thread) ----

        public void Update()
        {
            if (worker == null || Destroyed) return;
            var error = worker.Error;
            if (error != null)
            {
                StopInternal(resetTime: true);
                ReportError("VideoPlayer cannot play " + (Clip != null ? Clip.name : "the clip") + ": " + error);
                return;
            }
            double delta = Delta();

            if (phase == Phase.Preparing)
            {
                // Without waitForFirstFrame the clock runs from Play, and frames that arrive late
                // are skipped.
                if (playing && !WaitForFirstFrame) clock += delta * Speed;
                var first = TakeFrame(f => true);
                if (first == null) return;
                phase = Phase.Prepared;
                Present(first);
                if (WaitForFirstFrame || !playing) clock = worker.Stream.TimeOf(presentedIndex);
                int before = version;
                Invoke(VideoPlayer.InvokePrepareCompleted);
                if (before != version || phase != Phase.Prepared) return;
                if (startedPending && playing)
                {
                    startedPending = false;
                    Invoke(VideoPlayer.InvokeStarted);
                }
                return;
            }

            if (seeking)
            {
                var first = TakeFrame(f => true);
                if (first == null) return;
                seeking = false;
                Present(first);
                clock = worker.Stream.TimeOf(presentedIndex);
                Invoke(VideoPlayer.InvokeSeekCompleted);
                return;
            }

            if (!playing) return;
            var stream = worker.Stream;
            double length = Length > 0 ? Length : stream.TimeOf(stream.FrameCount - 1);

            if (TimeReference == VideoTimeReference.ExternalTime && externalTimeSet) FollowExternalClock();
            else clock += delta * Speed;

            int v = version;
            if (SkipOnDrop) PresentLatest(stream);
            else PresentNext(stream, length);
            if (v != version) return;

            while (clock >= length)
            {
                Invoke(VideoPlayer.InvokeLoopPointReached);
                if (v != version) return;
                if (!Looping)
                {
                    clock = length;
                    playing = false;
                    ended = true;
                    return;
                }
                clock -= length;
                loop++;
                if (SkipOnDrop) PresentLatest(stream);
                else PresentNext(stream, length);
                if (v != version || length <= 0) return;
            }
        }

        double Speed => Math.Max(0f, PlaybackSpeed);

        double Delta() => TimeUpdateMode switch
        {
            VideoClockSource.GameTime => UnityEngine.Time.deltaTime,
            // DSPTime follows the audio output clock, which advances with real time.
            _ => UnityEngine.Time.unscaledDeltaTime,
        };

        /// ExternalTime: the clock follows the reference. A reference ahead of the clock is
        /// caught up with at once; one slightly behind holds the clock (the reference catches
        /// up); one more than half a second behind is a discontinuity and the player seeks to it.
        void FollowExternalClock()
        {
            if (externalTime >= clock) clock = externalTime;
            else if (clock - externalTime > 0.5)
            {
                clock = externalTime;
                Seek(externalTime);
            }
        }

        // The pass/time key of a decoded frame relative to the clock.
        bool IsDue(DecodedFrame f, VideoStream stream)
        {
            int frameLoop = baseLoop + f.Wrap;
            return frameLoop < loop || (frameLoop == loop && stream.TimeOf(f.Index) <= clock + 0.0005);
        }

        void PresentLatest(VideoStream stream)
        {
            DecodedFrame latest = null;
            int dropped = 0;
            DecodedFrame f;
            while ((f = TakeFrame(x => IsDue(x, stream))) != null)
            {
                if (latest != null)
                {
                    worker.Recycle(latest);
                    dropped++;
                }
                latest = f;
            }
            if (latest != null) Present(latest);
            // Frames before the one due now will be dropped: the worker need not convert them.
            worker.SkipBefore(loop - baseLoop, stream.FrameAt(clock));
            for (int i = 0; i < dropped; i++)
            {
                int v = version;
                Invoke(VideoPlayer.InvokeFrameDropped);
                if (v != version) return;
            }
        }

        void PresentNext(VideoStream stream, double length)
        {
            var f = TakeFrame(x => IsDue(x, stream));
            if (f != null) Present(f);
            // The clock may not pass the next frame until it has been presented.
            int nextIndex = presentedIndex + 1, nextLoop = presentedLoop;
            if (nextIndex >= stream.FrameCount)
            {
                nextIndex = 0;
                nextLoop++;
            }
            if (nextLoop == loop && clock > stream.TimeOf(nextIndex)) clock = stream.TimeOf(nextIndex);
            else if (nextLoop > loop && clock > length) clock = length;
        }

        /// Dequeues the oldest frame of the current request if `accept` takes it; frames of older
        /// requests are discarded.
        DecodedFrame TakeFrame(Func<DecodedFrame, bool> accept)
        {
            while (true)
            {
                var f = worker.Peek();
                if (f == null) return null;
                if (f.Generation != generation)
                {
                    worker.Dequeue();
                    worker.Recycle(f);
                    continue;
                }
                if (!accept(f)) return null;
                return worker.Dequeue();
            }
        }

        unsafe void Present(DecodedFrame f)
        {
            var stream = worker.Stream;
            if (FrameTexture == null)
            {
                FrameTexture = new Texture2D(stream.Width, stream.Height, TextureFormat.RGBA32, false)
                {
                    name = "VideoTexture",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            var t = (Texture2DNative)FrameTexture.NativeOrThrow;
            f.Rgba.AsSpan().CopyTo(t.Memory.Span);
            t.ContentChanged();
            presentedIndex = f.Index;
            presentedLoop = baseLoop + f.Wrap;
            worker.Recycle(f);
        }

        void Invoke(Action<VideoPlayer> callback)
        {
            try
            {
                callback(Player);
            }
            catch (Exception e)
            {
                Debug.LogException(e, Managed);
            }
        }
    }

    /// The players with loaded content, updated once per frame in PostLateUpdate (Unity's
    /// PostLateUpdate.UpdateVideo), in the order they started preparing.
    internal static class VideoSystem
    {
        static readonly List<VideoPlayerNative> players = new();

        static VideoSystem() => PlayerLoop.PostLateUpdate += Update;

        public static IReadOnlyList<VideoPlayerNative> Players => players;

        public static void Add(VideoPlayerNative p)
        {
            if (!players.Contains(p)) players.Add(p);
        }

        public static void Remove(VideoPlayerNative p) => players.Remove(p);

        static void Update()
        {
            if (players.Count == 0) return;
            foreach (var p in players.ToArray())
            {
                if (!players.Contains(p)) continue;
                try
                {
                    p.Update();
                }
                catch (Exception e)
                {
                    Debug.LogException(e, p.Managed);
                }
            }
        }
    }

    /// What the renderer reads from video players. Frames are CPU textures (RGBA32 Texture2D,
    /// bottom-up rows, ContentChanged() per new frame, so `updateCount` changes with every frame).
    internal static class VideoOutputs
    {
        /// The frame a RenderTexture-mode player has presented for `target`, or null when no
        /// playing or prepared player targets it. The renderer copies it into `target` (scaled
        /// per `aspect` when the sizes differ) before `target` is next sampled or read, whenever
        /// the frame texture's updateCount differs from the last copy. A null result leaves the
        /// render texture as it is: a stopped player leaves its last frame in the target.
        public static Texture2D RenderTextureContent(RenderTexture target, out VideoAspect aspect)
        {
            aspect = VideoAspect.NoScaling;
            if (target == null) return null;
            for (int i = VideoSystem.Players.Count - 1; i >= 0; i--)
            {
                var p = VideoSystem.Players[i];
                if (p.RenderMode != VideoRenderTarget.RenderTexture || !p.HasPresentedFrame || !p.IsActiveAndEnabledState) continue;
                if (!ReferenceEquals(p.TargetTexture, target)) continue;
                aspect = p.AspectRatio;
                return p.FrameTexture;
            }
            return null;
        }

        /// The videos `camera` draws (render modes CameraFarPlane / CameraNearPlane), in the order
        /// their players started. Far-plane videos are drawn before the scene, near-plane ones
        /// after it, each as a screen-filling quad fitted per Aspect and blended with Alpha.
        public static List<CameraVideo> CameraVideos(Camera camera) => CameraVideos(camera, new List<CameraVideo>());

        /// CameraVideos into `result` (cleared first): the renderer asks twice per camera per
        /// frame, and almost always for none.
        public static List<CameraVideo> CameraVideos(Camera camera, List<CameraVideo> result)
        {
            result.Clear();
            if (camera == null) return result;
            foreach (var p in VideoSystem.Players)
            {
                if (p.RenderMode is not (VideoRenderTarget.CameraFarPlane or VideoRenderTarget.CameraNearPlane)) continue;
                if (!p.HasPresentedFrame || !p.IsActiveAndEnabledState || !ReferenceEquals(p.TargetCamera, camera)) continue;
                result.Add(new CameraVideo(p.FrameTexture, p.TargetCameraAlpha, p.AspectRatio, p.RenderMode == VideoRenderTarget.CameraNearPlane));
            }
            return result;
        }
    }
}
