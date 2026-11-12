using System;

namespace OniMoono.Engine.UI
{
    /// ONI_VERIFY_UI_CACHES=1: each rect a RectTransform returns from its epoch's cache
    /// (RectTransformNative.ComputeRect) and each nearest canvas kept for an object
    /// (CanvasSystem.NearestCanvas) is computed again and must be the same (the rect bit for bit);
    /// a line every 1,000,000 checks and one for each wrong answer.
    internal static class UiCacheVerify
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("ONI_VERIFY_UI_CACHES") == "1";
        static long rects, rectsWrong, nearest, nearestWrong;

        public static void Rect(RectTransformNative r, UnityEngine.Rect kept, UnityEngine.Rect fresh)
        {
            if (!SameBits(kept, fresh))
                EngineLog.Engine($"ui caches wrong ({++rectsWrong}): rect of {r.GameObject?.Name} kept {kept}, computed {fresh}");
            if (++rects % 1000000 == 0) Line();
        }

        public static void Nearest(GameObjectNative go, CanvasNative kept, CanvasNative fresh)
        {
            if (!ReferenceEquals(kept, fresh))
                EngineLog.Engine($"ui caches wrong ({++nearestWrong}): nearest canvas of {go.Name} kept {kept?.GameObject?.Name}, found {fresh?.GameObject?.Name}");
            if (++nearest % 1000000 == 0) Line();
        }

        public static void Line() => EngineLog.Engine($"ui caches: {rects} rects verified, {rectsWrong} wrong; {nearest} nearest canvases verified, {nearestWrong} wrong");

        static bool SameBits(UnityEngine.Rect a, UnityEngine.Rect b)
            => BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x) && BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y)
               && BitConverter.SingleToInt32Bits(a.width) == BitConverter.SingleToInt32Bits(b.width) && BitConverter.SingleToInt32Bits(a.height) == BitConverter.SingleToInt32Bits(b.height);
    }
}
