namespace OniMoono.Host
{
    /// The engine's frame-time cap, for the low-fps-timing game tweak's game time part
    /// (OniMoono.GameTweaks.FrameTimeCap, which reads this class by name: the tweaks are built
    /// against the game's assemblies, not ours). Main thread.
    public static class GameTimeCap
    {
        /// Caps the next frame's Time.deltaTime at `cap` seconds of game time.
        public static void CapNextFrame(float cap) => UnityEngine.Time.CapNextFrame(cap);
    }
}
