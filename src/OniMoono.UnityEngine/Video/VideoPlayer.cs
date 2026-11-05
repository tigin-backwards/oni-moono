using OniMoono.Engine;
using OniMoono.Engine.Video;

namespace UnityEngine.Video
{
    /// Plays VideoClips (VP8 WebM) into a render texture, a camera plane or its own texture.
    /// OniMoono.Engine.Video.VideoPlayerNative documents the playback model. Members the game
    /// does not use (source, renderMode, aspectRatio, timeUpdateMode, errorReceived, frameReady,
    /// …) are absent; their serialized values still drive playback. Their types exist, so that a
    /// mod's fields of them load (docs/research/unity-api-gaps.md).
    public partial class VideoPlayer
    {
        public event EventHandler prepareCompleted;

        public event EventHandler loopPointReached;

        public event EventHandler started;

        public event EventHandler frameDropped;

        public event EventHandler seekCompleted;

        VideoPlayerNative Data => (VideoPlayerNative)NativeOrThrow;

        // ---- content and targets ----

        public VideoClip clip
        {
            get => Data.Clip;
            set
            {
                Threads.AssertMainThread("set_clip");
                Data.SetClip(value);
            }
        }

        public RenderTexture targetTexture
        {
            get => Data.TargetTexture;
            set => Data.TargetTexture = value;
        }

        public Camera targetCamera
        {
            get => Data.TargetCamera;
            set => Data.TargetCamera = value;
        }

        /// Opacity of the picture drawn by the target camera.
        public float targetCameraAlpha
        {
            get => Data.TargetCameraAlpha;
            set => Data.TargetCameraAlpha = value;
        }

        /// The texture holding the current frame: the target texture in render-texture mode,
        /// otherwise the player's own texture (null until a frame has been decoded).
        public Texture texture
        {
            get
            {
                var d = Data;
                if (d.RenderMode == VideoRenderTarget.RenderTexture && d.TargetTexture != null) return d.TargetTexture;
                return d.FrameTexture;
            }
        }

        public VideoAudioOutputMode audioOutputMode
        {
            get => Data.AudioOutputMode;
            set => Data.AudioOutputMode = value;
        }

        // ---- playback settings ----

        public bool playOnAwake
        {
            get => Data.PlayOnAwake;
            set => Data.PlayOnAwake = value;
        }

        public bool waitForFirstFrame
        {
            get => Data.WaitForFirstFrame;
            set => Data.WaitForFirstFrame = value;
        }

        public bool skipOnDrop
        {
            get => Data.SkipOnDrop;
            set => Data.SkipOnDrop = value;
        }

        public bool isLooping
        {
            get => Data.Looping;
            set => Data.SetLooping(value);
        }

        /// Multiplies the rate at which the playback clock advances (negative values hold it).
        public float playbackSpeed
        {
            get => Data.PlaybackSpeed;
            set => Data.PlaybackSpeed = value;
        }

        public VideoTimeReference timeReference
        {
            get => Data.TimeReference;
            set => Data.TimeReference = value;
        }

        /// Under VideoTimeReference.ExternalTime, the reference clock the playback follows.
        public double externalReferenceTime
        {
            get => Data.ExternalReferenceTime;
            set => Data.SetExternalReferenceTime(value);
        }

        // ---- state ----

        public bool isPrepared => Data.IsPrepared;

        /// True from Play (including while the content is still being prepared) until Pause,
        /// Stop or the end of a non-looping movie.
        public bool isPlaying => Data.IsPlaying;

        public bool isPaused => Data.IsPaused;

        /// The presentation time of the frame in the texture, in seconds. Setting it seeks.
        public double time
        {
            get => Data.Time;
            set
            {
                Threads.AssertMainThread("set_time");
                Data.SetTime(value);
            }
        }

        /// The index of the frame in the texture (-1 when there is none). Setting it seeks.
        public long frame
        {
            get => Data.Frame;
            set
            {
                Threads.AssertMainThread("set_frame");
                Data.SetFrame(value);
            }
        }

        /// The clip's frame count, frame rate and length (0 without a clip).
        public ulong frameCount => Data.FrameCount;

        public float frameRate => (float)Data.FrameRate;

        public double length => Data.Length;

        // ---- control ----

        /// Loads the content and decodes its first frame; prepareCompleted is invoked when done.
        public void Prepare()
        {
            Threads.AssertMainThread("Prepare");
            Data.Prepare();
        }

        public void Play()
        {
            Threads.AssertMainThread("Play");
            Data.Play();
        }

        /// Pauses at the current frame (preparing the content first if needed).
        public void Pause()
        {
            Threads.AssertMainThread("Pause");
            Data.Pause();
        }

        /// Stops playback, releases the content and resets the time to 0.
        public void Stop()
        {
            Threads.AssertMainThread("Stop");
            Data.Stop();
        }

        // ---- engine side ----

        internal static void InvokePrepareCompleted(VideoPlayer source) => source.prepareCompleted?.Invoke(source);

        internal static void InvokeLoopPointReached(VideoPlayer source) => source.loopPointReached?.Invoke(source);

        internal static void InvokeStarted(VideoPlayer source) => source.started?.Invoke(source);

        internal static void InvokeFrameDropped(VideoPlayer source) => source.frameDropped?.Invoke(source);

        internal static void InvokeSeekCompleted(VideoPlayer source) => source.seekCompleted?.Invoke(source);

        internal override void EngineOnEnable() => Data.OnEnable();

        internal override void EngineOnDisable() => Data.OnDisable();

        internal override void EngineOnDestroy()
        {
            if (Native is VideoPlayerNative d) d.OnDestroy();
        }
    }
}
