// worldToLocalMatrix to the bit (lines "PROBE ... IS ..."), at mod load. Found with the known
// e2e finding scroll-normalized-position: a ScrollRect whose content exactly fits its viewport
// reads 0 or 1 by the rounding of the viewport's worldToLocalMatrix, and the original's inverse
// scale is not 1/s rounded (docs/cleanroom/ugui.md, "Transform hierarchy to the bit").
//
// IS1: single transforms (no parent, no rotation) with drawn scales of every exponent and both
//      signs and a drawn position; logs the scale, the position, worldToLocalMatrix and
//      InverseTransformPoint of a drawn point.
// IS2: a dense sweep of mantissas in [1, 2): scale (1 + k/2^23, same, same), k in steps.
// IS3: chains of 1 to 6 levels as UI builds them: a root at a uniform (s, s, s) or (s, s, 1)
//      scale, children mostly at scale 1, some scaled, some z-rotated, all with positions.
//      Logs each level's local values (read back) and the leaf's localToWorldMatrix,
//      worldToLocalMatrix, InverseTransformPoint of a drawn point and that point.
// IS4: the same chains with general rotations and non-uniform scales.
// IS7 (PROBE_IS_PART=position): Transform.position's setter on a child of rotation-free chains
//      of 1 to 8 levels (UI-like and general: non-uniform, negative and z-1 scales, zero
//      positions); logs each level's local values, the point, the child's localPosition after
//      the set and the parent's InverseTransformPoint of the point.
// IS8 (PROBE_IS_PART=position): the same with rotations on some levels (z only, as UI rotates,
//      and general), for what the engine's fallback keeps.
// Run alone with PROBE_SCENARIO=invscale.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SemanticsProbe
{
    public static class InverseScale
    {
        static string H(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
        static string H(Vector3 v) => H(v.x) + "," + H(v.y) + "," + H(v.z);
        static string H(Quaternion q) => H(q.x) + "," + H(q.y) + "," + H(q.z) + "," + H(q.w);

        static string H(Matrix4x4 m)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    sb.Append(r == 0 && c == 0 ? "" : ",").Append(H(m[r, c]));
            return sb.ToString();
        }

        static float Bits(int b) => BitConverter.ToSingle(BitConverter.GetBytes(b), 0);

        static float DrawScale(System.Random rng)
        {
            int exp = 127 + rng.Next(-8, 9);
            int mant = rng.Next(0, 1 << 23);
            float s = Bits((exp << 23) | mant);
            return rng.Next(4) == 0 ? -s : s;
        }

        static float DrawPos(System.Random rng)
        {
            double mag = Math.Pow(10, rng.Next(-1, 4));
            return (float)((rng.NextDouble() * 2 - 1) * mag);
        }

        static Vector3 DrawV(System.Random rng) => new Vector3(DrawPos(rng), DrawPos(rng), DrawPos(rng));

        static Quaternion DrawQ(System.Random rng, bool general)
        {
            if (!general || rng.Next(3) == 0)
                return Quaternion.Euler(0, 0, (float)(rng.NextDouble() * 360));
            return Quaternion.Euler((float)(rng.NextDouble() * 360), (float)(rng.NextDouble() * 360), (float)(rng.NextDouble() * 360));
        }

        static float DrawUiScale(System.Random rng)
        {
            switch (rng.Next(5))
            {
                case 0: return 1f;
                case 1: return 0.86f;
                case 2: return (float)(0.25 + rng.NextDouble() * 2) * (rng.Next(6) == 0 ? -1 : 1);
                case 3: return DrawScale(rng);
                default: return (float)Math.Pow(2, rng.Next(-3, 4)) * (float)(0.5 + rng.NextDouble());
            }
        }

        static float DrawUiPos(System.Random rng) => rng.Next(4) == 0 ? 0f : DrawPos(rng);

        /// IS7 and IS8: localPosition after Transform.position is set, and InverseTransformPoint.
        static void PositionSet(System.Random rng)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool rotated = pass == 1;
                string tag = rotated ? "IS8" : "IS7";
                for (int i = 0; i < (rotated ? 1000 : 4000); i++)
                {
                    int n = 1 + rng.Next(8);
                    Transform parent = null, first = null;
                    var sb = new StringBuilder(tag + " " + i + " n=" + n);
                    bool anyRotation = false;
                    for (int l = 0; l < n; l++)
                    {
                        var lt = new GameObject("P" + l).transform;
                        if (first == null) first = lt;
                        if (parent != null) lt.SetParent(parent, false);
                        lt.localPosition = new Vector3(DrawUiPos(rng), DrawUiPos(rng), rng.Next(3) == 0 ? DrawUiPos(rng) : 0f);
                        float sc = DrawUiScale(rng);
                        lt.localScale = rng.Next(3) == 0 ? new Vector3(sc, DrawUiScale(rng), rng.Next(2) == 0 ? 1f : DrawUiScale(rng))
                            : new Vector3(sc, sc, rng.Next(2) == 0 ? sc : 1f);
                        if (rotated && (rng.Next(3) == 0 || (l == n - 1 && !anyRotation)))
                        {
                            lt.localRotation = DrawQ(rng, rng.Next(2) == 0);
                            anyRotation = true;
                        }
                        sb.Append(" L" + l + "=" + H(lt.localPosition) + ";" + H(lt.localRotation) + ";" + H(lt.localScale));
                        parent = lt;
                    }
                    var leaf = new GameObject("leaf").transform;
                    leaf.SetParent(parent, false);
                    var v = new Vector3(DrawUiPos(rng), DrawUiPos(rng), rng.Next(2) == 0 ? 0f : DrawUiPos(rng));
                    leaf.position = v;
                    sb.Append(" v=" + H(v) + " set=" + H(leaf.localPosition) + " itp=" + H(parent.InverseTransformPoint(v)));
                    Rec.Log(sb.ToString());
                    UnityEngine.Object.DestroyImmediate(first.gameObject);
                }
            }
        }

        public static void Run()
        {
            var rng = new System.Random(5723);
            var root = new GameObject("InverseScaleProbe");
            var t = root.transform;
            if (Environment.GetEnvironmentVariable("PROBE_IS_PART") == "edge")
            {
                // IS6: the inverse scale at the edges: every exponent at mantissas 1, 1.5 and the
                // largest, special values, and the smallest and largest scales whose inverse is not 0.
                Func<float, float> inv = s => { t.localScale = new Vector3(s, 1f, 1f); return t.worldToLocalMatrix.m00; };
                var specials = new List<float> { 0f, -0f, float.Epsilon, 1e-45f, 1e-40f, 1e-38f, 1.1754944e-38f, 1e-30f, 1e-25f, 1e-21f, 1e-20f,
                    1e-19f, 1e-18f, 1e-17f, 1e-16f, 1e-15f, 1e-14f, 1e-13f, 1e-12f, 1e-11f, 1e-10f, 1e-9f, 1e-8f, 1e-7f, 1e-6f, 1e-5f,
                    1e30f, 1e37f, 1e38f, 3e38f, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN, -1e-20f, -1e-10f };
                foreach (var s in specials) Rec.Log("IS6 s=" + H(s) + " f=" + H(inv(s)));
                for (int e = 1; e < 255; e++)
                    foreach (int m in new[] { 0, 1 << 22, (1 << 23) - 1 })
                    {
                        float s = Bits((e << 23) | m);
                        Rec.Log("IS6 s=" + H(s) + " f=" + H(inv(s)));
                    }
                // bisection on the bits: smallest positive s with f != 0 (between 1e-30 and 1)
                int lo = BitConverter.ToInt32(BitConverter.GetBytes(1e-30f), 0), hi = BitConverter.ToInt32(BitConverter.GetBytes(1f), 0);
                if (inv(Bits(lo)) == 0f)
                    while (hi - lo > 1) { int mid = lo + (hi - lo) / 2; if (inv(Bits(mid)) == 0f) lo = mid; else hi = mid; }
                Rec.Log("IS6 smallest nonzero s=" + H(Bits(hi)) + " f=" + H(inv(Bits(hi))) + " below s=" + H(Bits(lo)) + " f=" + H(inv(Bits(lo))));
                for (int b = hi - 3; b <= hi + 3; b++) Rec.Log("IS6 s=" + H(Bits(b)) + " f=" + H(inv(Bits(b))));
                Rec.Log("IS done");
                return;
            }
            if (Environment.GetEnvironmentVariable("PROBE_IS_PART") == "position")
            {
                UnityEngine.Object.DestroyImmediate(root);
                PositionSet(rng);
                Rec.Log("IS done");
                return;
            }
            if (Environment.GetEnvironmentVariable("PROBE_IS_PART") == "full")
            {
                // IS5: every mantissa of [1, 2): bits(m00) - bits(1/s) + 1, two bits each (3: other),
                // 4096 values per line as hex, 2 bits per value, most significant first.
                var sb = new StringBuilder();
                int acc = 0, nacc = 0;
                for (int k = 0; k < (1 << 23); k++)
                {
                    float s = Bits((127 << 23) | k);
                    t.localScale = new Vector3(s, 1f, 1f);
                    float f = t.worldToLocalMatrix.m00;
                    int d = BitConverter.ToInt32(BitConverter.GetBytes(f), 0) - BitConverter.ToInt32(BitConverter.GetBytes(1f / s), 0) + 1;
                    if (d < 0 || d > 2) d = 3;
                    acc = (acc << 2) | d;
                    if (++nacc == 2)
                    {
                        sb.Append("0123456789abcdef"[acc]);
                        acc = 0; nacc = 0;
                    }
                    if ((k & 4095) == 4095)
                    {
                        Rec.Log("IS5 " + (k >> 12) + " " + sb);
                        sb.Length = 0;
                    }
                }
                Rec.Log("IS done");
                return;
            }
            for (int i = 0; i < 6000; i++)
            {
                var s = new Vector3(DrawScale(rng), DrawScale(rng), DrawScale(rng));
                if (i % 3 == 0) s = new Vector3(s.x, s.x, s.x);
                var p = DrawV(rng);
                var v = DrawV(rng);
                t.localPosition = p;
                t.localRotation = Quaternion.identity;
                t.localScale = s;
                Rec.Log("IS1 " + i + " s=" + H(t.localScale) + " p=" + H(t.localPosition) + " w2l=" + H(t.worldToLocalMatrix)
                    + " v=" + H(v) + " itp=" + H(t.InverseTransformPoint(v)));
            }
            t.localPosition = Vector3.zero;
            for (int k = 0; k < (1 << 23); k += 997)
            {
                float s = Bits((127 << 23) | k);
                t.localScale = new Vector3(s, s, s);
                Rec.Log("IS2 " + k + " s=" + H(s) + " w2l=" + H(t.worldToLocalMatrix.m00));
            }
            UnityEngine.Object.DestroyImmediate(root);

            for (int pass = 0; pass < 2; pass++)
            {
                bool general = pass == 1;
                string tag = general ? "IS4" : "IS3";
                for (int i = 0; i < 1500; i++)
                {
                    int n = 1 + rng.Next(6);
                    var levels = new List<Transform>();
                    Transform parent = null;
                    for (int l = 0; l < n; l++)
                    {
                        var go = new GameObject("L" + l);
                        var lt = go.transform;
                        if (parent != null) lt.SetParent(parent, false);
                        lt.localPosition = DrawV(rng);
                        Vector3 sc;
                        if (general)
                            sc = rng.Next(3) == 0 ? Vector3.one : new Vector3(DrawScale(rng), DrawScale(rng), DrawScale(rng));
                        else if (l == 0)
                        {
                            float s = rng.Next(4) == 0 ? (float)(0.5 + rng.NextDouble()) : DrawScale(rng);
                            if (s < 0) s = -s;
                            if (rng.Next(5) == 0) s = 0.86f;
                            sc = rng.Next(2) == 0 ? new Vector3(s, s, s) : new Vector3(s, s, 1);
                        }
                        else
                        {
                            float s = DrawScale(rng);
                            sc = rng.Next(3) == 0 ? new Vector3(s, s, 1) : Vector3.one;
                        }
                        lt.localScale = sc;
                        if (general ? rng.Next(3) != 0 : rng.Next(4) == 0)
                            lt.localRotation = DrawQ(rng, general);
                        levels.Add(lt);
                        parent = lt;
                    }
                    var sb = new StringBuilder(tag + " " + i + " n=" + n);
                    for (int l = 0; l < n; l++)
                        sb.Append(" L" + l + "=" + H(levels[l].localPosition) + ";" + H(levels[l].localRotation) + ";" + H(levels[l].localScale));
                    var leaf = levels[n - 1];
                    var v = DrawV(rng);
                    sb.Append(" l2w=" + H(leaf.localToWorldMatrix) + " w2l=" + H(leaf.worldToLocalMatrix)
                        + " pos=" + H(leaf.position) + " rot=" + H(leaf.rotation) + " lossy=" + H(leaf.lossyScale)
                        + " v=" + H(v) + " itp=" + H(leaf.InverseTransformPoint(v)));
                    Rec.Log(sb.ToString());
                    UnityEngine.Object.DestroyImmediate(levels[0].gameObject);
                }
            }
            Rec.Log("IS done");
        }
    }
}
