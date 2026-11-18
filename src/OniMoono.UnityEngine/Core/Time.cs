using System.Diagnostics;

namespace UnityEngine
{
    /// Frame timing. The player loop advances it (OniMoono.Engine.PlayerLoop); reads are
    /// lock-free from any thread.
    public partial class Time
    {
        static readonly Stopwatch clock = Stopwatch.StartNew();

        // Kept in double, as Unity keeps them, so a long session does not drift; read as float.
        static double s_Time, s_UnscaledTime, s_FixedTime, s_FixedUnscaledTime;
        static float s_DeltaTime, s_UnscaledDeltaTime;
        static float s_TimeScale = 1f;
        static float s_FixedDeltaTime = 0.02f;
        static float s_MaximumDeltaTime = 1f / 3f;
        static float s_MaximumParticleDeltaTime = 0.03f;
        static double s_LevelLoadTime;
        static int s_FrameCount, s_RenderedFrameCount;
        static bool s_InFixedTimeStep;

        public static float time => (float)timeAsDouble;
        public static double timeAsDouble => s_InFixedTimeStep ? s_FixedTime : s_Time;
        public static float unscaledTime => (float)unscaledTimeAsDouble;
        public static double unscaledTimeAsDouble => s_InFixedTimeStep ? s_FixedUnscaledTime : s_UnscaledTime;
        public static float fixedTime => (float)s_FixedTime;
        public static double fixedTimeAsDouble => s_FixedTime;
        public static float fixedUnscaledTime => (float)s_FixedUnscaledTime;
        public static double fixedUnscaledTimeAsDouble => s_FixedUnscaledTime;
        public static float deltaTime => s_InFixedTimeStep ? s_FixedDeltaTime : s_DeltaTime;
        public static float unscaledDeltaTime => s_InFixedTimeStep ? s_FixedDeltaTime : s_UnscaledDeltaTime;
        public static float fixedUnscaledDeltaTime => s_FixedDeltaTime;
        public static float timeSinceLevelLoad => (float)(timeAsDouble - s_LevelLoadTime);
        public static double timeSinceLevelLoadAsDouble => timeAsDouble - s_LevelLoadTime;
        public static bool inFixedTimeStep => s_InFixedTimeStep;
        public static int frameCount => s_FrameCount;
        public static int renderedFrameCount => s_RenderedFrameCount;
        public static float realtimeSinceStartup => (float)clock.Elapsed.TotalSeconds;
        public static double realtimeSinceStartupAsDouble => clock.Elapsed.TotalSeconds;

        public static float timeScale
        {
            get => s_TimeScale;
            set => s_TimeScale = value;
        }

        // The longest frame is never shorter than a fixed step, whichever is set (measured in the
        // player: a maximum of 0.01 under ONI's 0.03 step reads 0.03, and a step of 0.5 raises a
        // maximum of 0.2 to 0.5).
        public static float fixedDeltaTime
        {
            get => s_FixedDeltaTime;
            set
            {
                s_FixedDeltaTime = value < 0.0001f ? 0.0001f : value;
                if (s_MaximumDeltaTime < s_FixedDeltaTime) s_MaximumDeltaTime = s_FixedDeltaTime;
            }
        }

        public static float maximumDeltaTime
        {
            get => s_MaximumDeltaTime;
            set => s_MaximumDeltaTime = value < s_FixedDeltaTime ? s_FixedDeltaTime : value;
        }

        public static float maximumParticleDeltaTime
        {
            get => s_MaximumParticleDeltaTime;
            set => s_MaximumParticleDeltaTime = value;
        }

        // ---- Player loop interface ----

        /// Starts a new frame given the real time elapsed since the previous one.
        internal static void BeginFrame(float realDelta)
        {
            s_FrameCount++;
            // Every frame renders (no on-demand rendering): Unity counts it from its start, so
            // both counters agree during the frame (observed in the player).
            s_RenderedFrameCount++;
            s_UnscaledDeltaTime = realDelta;
            s_UnscaledTime += realDelta;
            float clamped = realDelta < s_MaximumDeltaTime ? realDelta : s_MaximumDeltaTime;
            float scaled = clamped * s_TimeScale;
            // The low-fps-timing game tweak: armed by the game's sim step of the frame before,
            // so only while a colony runs; below the cap the frame's time is left as it is.
            if (s_ScaledCapArmed)
            {
                s_ScaledCapArmed = false;
                if (scaled > s_ScaledCap) scaled = s_ScaledCap;
            }
            s_DeltaTime = scaled;
            s_Time += s_DeltaTime;
            // Exponential smoothing of deltaTime; the first frame starts the average.
            s_SmoothDeltaTime = s_FrameCount <= 1 ? s_DeltaTime : s_SmoothDeltaTime + (s_DeltaTime - s_SmoothDeltaTime) * 0.2f;
        }

        static float s_SmoothDeltaTime;
        static float s_ScaledCap;
        static bool s_ScaledCapArmed;

        /// Caps the next frame's deltaTime (scaled, after maximumDeltaTime) at `cap`, for one
        /// frame: the low-fps-timing game tweak arms it at each of the game's sim steps, with the
        /// step's own cap (OniMoono.Host.GameTimeCap). unscaledDeltaTime stays the real time.
        internal static void CapNextFrame(float cap)
        {
            s_ScaledCap = cap;
            s_ScaledCapArmed = true;
        }

        /// deltaTime smoothed over recent frames.
        public static float smoothDeltaTime => s_SmoothDeltaTime;

        /// Advances fixed time by one step; returns false when fixed time has caught up with time.
        internal static bool BeginFixedStep()
        {
            if (s_FixedTime + s_FixedDeltaTime > s_Time) return false;
            s_FixedTime += s_FixedDeltaTime;
            s_FixedUnscaledTime += s_FixedDeltaTime;
            s_InFixedTimeStep = true;
            return true;
        }

        internal static void EndFixedStep() => s_InFixedTimeStep = false;

        internal static void LevelLoaded() => s_LevelLoadTime = s_Time;

        /// Settings from the project's TimeManager.
        internal static void Configure(float fixedTimestep, float maximumAllowedTimestep, float timeScaleSetting, float maximumParticleTimestep)
        {
            s_FixedDeltaTime = fixedTimestep;
            s_MaximumDeltaTime = maximumAllowedTimestep;
            s_TimeScale = timeScaleSetting;
            s_MaximumParticleDeltaTime = maximumParticleTimestep;
        }
    }
}
