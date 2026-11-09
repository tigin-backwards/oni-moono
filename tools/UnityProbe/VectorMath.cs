// Vector2, Vector3, Mathf, Rect and Bounds members to the bit (lines "PROBE ... VM ..."), at mod
// load. Found with issue #66: a seed's map differed from the original's, partly because
// worldgen's geometry calls these (Vector2.Distance 25 times) and the original evaluates their
// float arithmetic as Mono does with float32 off (D25): in double, rounded to float where a
// value is stored. Each case draws its inputs from a seeded System.Random (a mix of magnitudes,
// signs and fractions, near-equal pairs for the epsilon comparisons) and logs, as float bits in
// hex, the inputs and every member's result; tools/UnityProbe/vm-check.py compares two players'
// lines and tests candidate formulas against one. Run alone with PROBE_SCENARIO=vecmath.
using System;
using System.Text;
using UnityEngine;

namespace SemanticsProbe
{
    public static class VectorMath
    {
        static string H(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8");
        static string H(bool b) => b ? "1" : "0";
        static string H(Vector2 v) => H(v.x) + "," + H(v.y);
        static string H(Vector3 v) => H(v.x) + "," + H(v.y) + "," + H(v.z);
        static string H(Rect r) => H(r.x) + "," + H(r.y) + "," + H(r.width) + "," + H(r.height);

        static float Draw(System.Random rng)
        {
            double mag = Math.Pow(10, rng.Next(-3, 4));
            return (float)((rng.NextDouble() * 2 - 1) * mag);
        }

        /// A second value near the first (the epsilon comparisons' edge) or unrelated.
        static float Near(System.Random rng, float a) => rng.Next(3) switch
        {
            // Summed in double and rounded once, so both players draw the same input.
            0 => (float)(a + (rng.NextDouble() * 2 - 1) * 1e-5),
            1 => (float)(a + (rng.NextDouble() * 2 - 1) * 1e-2),
            _ => Draw(rng),
        };

        static float T(System.Random rng) => (float)(rng.NextDouble() * 1.4 - 0.2);

        public static void Run()
        {
            var rng = new System.Random(20261109);
            for (int i = 0; i < 2000; i++)
            {
                var a = new Vector2(Draw(rng), Draw(rng));
                var b = new Vector2(Near(rng, a.x), Near(rng, a.y));
                float t = T(rng), d = Math.Abs(Draw(rng));
                var sb = new StringBuilder("VM2 " + i + " a=" + H(a) + " b=" + H(b) + " t=" + H(t) + " d=" + H(d));
                sb.Append(" mag=").Append(H(a.magnitude)).Append(" sqr=").Append(H(a.sqrMagnitude)).Append(" ssqr=").Append(H(Vector2.SqrMagnitude(a)));
                sb.Append(" nrm=").Append(H(a.normalized));
                var n = a; n.Normalize();
                sb.Append(" nrm2=").Append(H(n));
                sb.Append(" dist=").Append(H(Vector2.Distance(a, b))).Append(" eq=").Append(H(a == b)).Append(" ne=").Append(H(a != b));
                sb.Append(" dot=").Append(H(Vector2.Dot(a, b))).Append(" lerp=").Append(H(Vector2.Lerp(a, b, t))).Append(" lerpu=").Append(H(Vector2.LerpUnclamped(a, b, t)));
                sb.Append(" mt=").Append(H(Vector2.MoveTowards(a, b, d))).Append(" cm=").Append(H(Vector2.ClampMagnitude(a, d)));
                sb.Append(" ang=").Append(H(Vector2.Angle(a, b))).Append(" refl=").Append(H(Vector2.Reflect(a, b)));
                sb.Append(" sum=").Append(H(a + b)).Append(" dif=").Append(H(a - b)).Append(" mul=").Append(H(a * t)).Append(" div=").Append(H(a / d));
                Rec.Log(sb.ToString());
            }
            for (int i = 0; i < 2000; i++)
            {
                var a = new Vector3(Draw(rng), Draw(rng), Draw(rng));
                var b = new Vector3(Near(rng, a.x), Near(rng, a.y), Near(rng, a.z));
                float t = T(rng), d = Math.Abs(Draw(rng));
                var sb = new StringBuilder("VM3 " + i + " a=" + H(a) + " b=" + H(b) + " t=" + H(t) + " d=" + H(d));
                sb.Append(" mag=").Append(H(a.magnitude)).Append(" sqr=").Append(H(a.sqrMagnitude)).Append(" nrm=").Append(H(a.normalized));
                sb.Append(" dist=").Append(H(Vector3.Distance(a, b))).Append(" eq=").Append(H(a == b)).Append(" dot=").Append(H(Vector3.Dot(a, b)));
                sb.Append(" cross=").Append(H(Vector3.Cross(a, b))).Append(" lerp=").Append(H(Vector3.Lerp(a, b, t))).Append(" mt=").Append(H(Vector3.MoveTowards(a, b, d)));
                sb.Append(" cm=").Append(H(Vector3.ClampMagnitude(a, d))).Append(" proj=").Append(H(Vector3.Project(a, b)));
                Rec.Log(sb.ToString());
            }
            for (int i = 0; i < 2000; i++)
            {
                float a = Draw(rng), b = Near(rng, a), t = T(rng), v = Draw(rng);
                var r = new Rect(Draw(rng), Draw(rng), Math.Abs(Draw(rng)), Math.Abs(Draw(rng)));
                var s = new Rect(Near(rng, r.x), Near(rng, r.y), Math.Abs(Draw(rng)), Math.Abs(Draw(rng)));
                var p = new Vector2(Near(rng, r.xMax), Near(rng, r.yMax));
                var bounds = new Bounds(new Vector3(Draw(rng), Draw(rng), Draw(rng)), new Vector3(Math.Abs(Draw(rng)), Math.Abs(Draw(rng)), Math.Abs(Draw(rng))));
                var q = new Vector3(Draw(rng), Draw(rng), Draw(rng));
                var grown = bounds;
                grown.Encapsulate(q);
                var other = new Bounds(q, new Vector3(Math.Abs(Draw(rng)), Math.Abs(Draw(rng)), Math.Abs(Draw(rng))));
                Rec.Log("VMF " + i + " a=" + H(a) + " b=" + H(b) + " t=" + H(t) + " v=" + H(v) + " r=" + H(r) + " s=" + H(s) + " p=" + H(p)
                    + " bc=" + H(bounds.center) + " be=" + H(bounds.extents) + " q=" + H(q)
                    + " lerp=" + H(Mathf.Lerp(a, b, t)) + " lerpu=" + H(Mathf.LerpUnclamped(a, b, t)) + " ilerp=" + H(Mathf.InverseLerp(a, b, v))
                    + " mt=" + H(Mathf.MoveTowards(a, b, Math.Abs(v))) + " c01=" + H(Mathf.Clamp01(t))
                    + " rxmax=" + H(r.xMax) + " rymax=" + H(r.yMax) + " rc=" + H(r.center) + " rcont=" + H(r.Contains(p)) + " rov=" + H(r.Overlaps(s))
                    + " mmr=" + H(Rect.MinMaxRect(r.x, r.y, s.x, s.y))
                    + " enc=" + H(grown.center) + "," + H(grown.extents) + " bint=" + H(bounds.Intersects(other)) + " bmin=" + H(bounds.min) + " bmax=" + H(bounds.max));
            }
            Rec.Log("VM done");
        }
    }
}
