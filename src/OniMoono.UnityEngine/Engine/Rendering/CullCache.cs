using System;
using UnityEngine;

namespace OniMoono.Engine.Rendering
{
    /// Switches and counters of the culling bounds FrameRenderer keeps for its candidates
    /// (ARCHITECTURE §3.5).
    internal static class CullCache
    {
        /// ONI_CULL_CACHE=0: every candidate's bounds are made again at each visit, as before.
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_CULL_CACHE") != "0";

        /// ONI_VERIFY_CULL_CACHE=1: each kept bounds a visit uses are made afresh too, and any
        /// difference is logged; the fresh ones are used.
        public static readonly bool Verifying = Environment.GetEnvironmentVariable("ONI_VERIFY_CULL_CACHE") == "1";

        /// Visits that used kept bounds, that made them (to keep or not), the kept ones checked
        /// against fresh ones, and the checks that differed.
        public static long Kept, Made, Verified, Wrong;

        /// Same bits in every component (equal floats may differ in their bits: -0 and 0, NaNs).
        public static bool SameBits(in Bounds a, in Bounds b)
        {
            Vector3 ac = a.center, ae = a.extents, bc = b.center, be = b.extents;
            return Bits(ac.x) == Bits(bc.x) && Bits(ac.y) == Bits(bc.y) && Bits(ac.z) == Bits(bc.z)
                && Bits(ae.x) == Bits(be.x) && Bits(ae.y) == Bits(be.y) && Bits(ae.z) == Bits(be.z);
        }

        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        /// One kept bounds compared with fresh ones.
        public static void Verify(RendererNative r, in Bounds kept, in Bounds fresh)
        {
            Verified++;
            if (!SameBits(kept, fresh))
                EngineLog.Engine($"cull cache wrong ({++Wrong}): {r.GetType().Name} '{r.GameObject.Name}' layer {r.GameObject.Layer}: kept {Show(kept)}, fresh {Show(fresh)}");
        }

        static string Show(in Bounds b) => $"({b.center.x:R}, {b.center.y:R}, {b.center.z:R}) ± ({b.extents.x:R}, {b.extents.y:R}, {b.extents.z:R})";

        public static void Report()
            => EngineLog.Engine($"cull cache: {Kept} visits used kept bounds, {Made} made them, {Verified} verified, {Wrong} wrong");
    }
}
