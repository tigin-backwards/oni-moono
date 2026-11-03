using System;

namespace OniMoono.Desktop.Vulkan
{
    /// Measurement only (ONI_VK_ALT=NAME[,NAME...], with --frame-stats): the named draw-path
    /// changes run as they did before them in every other 60-frame window, so that adjacent
    /// windows of one run compare before and after on the same machine load
    /// (docs/research/vulkan-draw-binding.md, Method). Each window's frame counts end with
    /// "variant=0" (before) or "variant=1" (after). Names: writes-first (set 0's writes built
    /// for every draw, VkDescriptorCache.SetForWritesFirst) and prepare-every-texture (no
    /// VkReadyStamps: every texture of every draw checked in full) and push-writes (pushed sets
    /// as a write per slot, not with the program's update template). Unset, every change is on.
    internal static class VkDrawAlternation
    {
        public const int WritesFirst = 1, PrepareEveryTexture = 2, PushWrites = 4;

        static readonly int named = Parse(Environment.GetEnvironmentVariable("ONI_VK_ALT"));
        /// The current window runs the named changes as before them (device thread).
        static bool before = true;

        public static int Parse(string value)
        {
            int changes = 0;
            foreach (var name in (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                changes |= name switch
                {
                    "writes-first" => WritesFirst,
                    "prepare-every-texture" => PrepareEveryTexture,
                    "push-writes" => PushWrites,
                    _ => 0,
                };
            return changes;
        }

        /// The change runs as before it in this window.
        /// ONI_CAPTURE_TWICE=full-draws runs every change as before it while a capture renders
        /// again (FrameRenderer.FullDrawWork).
        public static bool Before(int change) => (named & change) != 0 && before || System.Threading.Volatile.Read(ref OniMoono.Engine.Rendering.FrameRenderer.FullDrawWork);

        /// The ending window's variant for its frame counts (empty when not alternating); the
        /// next window runs the other.
        public static string EndWindow()
        {
            if (named == 0) return "";
            var text = before ? "; variant=0" : "; variant=1";
            before = !before;
            return text;
        }
    }
}
