using System;
using OniMoono.Engine;

namespace OniMoono.Desktop.Vulkan
{
    /// Verify mode (ONI_VERIFY_VK_DRAWS=1, device thread): every draw also runs the draw path's
    /// full work beside its shortcuts and counts where they part. The shortcuts: a texture found
    /// ready earlier in the pass (VkReadyStamps), set 0's identities computed before any write
    /// (VkDescriptorCache.SetFor), and the pipeline lookup's last-key shortcut. A summary every
    /// 600 frames, which qa-tools.py's verify gate fails on any non-zero count; the first 20
    /// mismatches each get a line of their own.
    internal static class VkDrawVerify
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("ONI_VERIFY_VK_DRAWS") == "1";

        public enum Kind { Preparation, Key, Set, Pipeline }

        static long verified;
        static readonly long[] wrong = new long[4];
        static int incidents;

        /// A draw checked.
        public static void Verified() => verified++;

        public static void Wrong(Kind kind, string program, string detail)
        {
            wrong[(int)kind]++;
            if (incidents++ < 20) EngineLog.Engine($"vulkan draws: mismatch: {Name(kind)}: {program}: {detail}");
        }

        static string Name(Kind kind) => kind switch
        {
            Kind.Preparation => "preparation",
            Kind.Key => "set 0's key",
            Kind.Set => "set 0",
            _ => "the pipeline",
        };

        /// The summary line, every 600 presented frames.
        public static void Presented(int frame)
        {
            if (frame % 600 == 0) EngineLog.Engine(Summary());
        }

        public static string Summary() =>
            $"vulkan draws: {verified} verified, {wrong[0]} wrong in preparation, {wrong[1]} wrong in set 0's key, {wrong[2]} wrong in set 0, {wrong[3]} wrong in the pipeline";
    }
}
