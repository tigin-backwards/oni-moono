using System;
using UnityEngine;
using Xunit;
using Random = System.Random;

namespace OniMoono.Tests
{
    /// Matrix4x4's products (vectorised with AVX2) against the scalar arithmetic they had when they
    /// were made to round as the original's do (each element's sum of products in double, left to
    /// right, rounded to float once; MathTests.MatrixProductsRoundOnceAsUnitysDo), for random
    /// matrices and vectors mixing every class of float: the same bits, and NaN where it gave NaN.
    /// A NaN's payload, when two NaN operands meet, is the one of the operand the JIT puts first,
    /// which changes with optimisation (the two agreed on every payload in a debug build and not
    /// in a release one), so NaNs compare as NaN.
    public class MatrixProductTests
    {
        // ----- The scalar arithmetic, as it was -----

        static float Dot(float a0, float b0, float a1, float b1, float a2, float b2, float a3, float b3)
            => (float)((double)a0 * b0 + (double)a1 * b1 + (double)a2 * b2 + (double)a3 * b3);

        static float Dot(float a0, float b0, float a1, float b1, float a2, float b2)
            => (float)((double)a0 * b0 + (double)a1 * b1 + (double)a2 * b2);

        static float Affine(float a0, float b0, float a1, float b1, float a2, float b2, float a3)
            => (float)((double)a0 * b0 + (double)a1 * b1 + (double)a2 * b2 + a3);

        static Matrix4x4 Mul(Matrix4x4 lhs, Matrix4x4 rhs)
        {
            Matrix4x4 r;
            r.m00 = Dot(lhs.m00, rhs.m00, lhs.m01, rhs.m10, lhs.m02, rhs.m20, lhs.m03, rhs.m30);
            r.m01 = Dot(lhs.m00, rhs.m01, lhs.m01, rhs.m11, lhs.m02, rhs.m21, lhs.m03, rhs.m31);
            r.m02 = Dot(lhs.m00, rhs.m02, lhs.m01, rhs.m12, lhs.m02, rhs.m22, lhs.m03, rhs.m32);
            r.m03 = Dot(lhs.m00, rhs.m03, lhs.m01, rhs.m13, lhs.m02, rhs.m23, lhs.m03, rhs.m33);
            r.m10 = Dot(lhs.m10, rhs.m00, lhs.m11, rhs.m10, lhs.m12, rhs.m20, lhs.m13, rhs.m30);
            r.m11 = Dot(lhs.m10, rhs.m01, lhs.m11, rhs.m11, lhs.m12, rhs.m21, lhs.m13, rhs.m31);
            r.m12 = Dot(lhs.m10, rhs.m02, lhs.m11, rhs.m12, lhs.m12, rhs.m22, lhs.m13, rhs.m32);
            r.m13 = Dot(lhs.m10, rhs.m03, lhs.m11, rhs.m13, lhs.m12, rhs.m23, lhs.m13, rhs.m33);
            r.m20 = Dot(lhs.m20, rhs.m00, lhs.m21, rhs.m10, lhs.m22, rhs.m20, lhs.m23, rhs.m30);
            r.m21 = Dot(lhs.m20, rhs.m01, lhs.m21, rhs.m11, lhs.m22, rhs.m21, lhs.m23, rhs.m31);
            r.m22 = Dot(lhs.m20, rhs.m02, lhs.m21, rhs.m12, lhs.m22, rhs.m22, lhs.m23, rhs.m32);
            r.m23 = Dot(lhs.m20, rhs.m03, lhs.m21, rhs.m13, lhs.m22, rhs.m23, lhs.m23, rhs.m33);
            r.m30 = Dot(lhs.m30, rhs.m00, lhs.m31, rhs.m10, lhs.m32, rhs.m20, lhs.m33, rhs.m30);
            r.m31 = Dot(lhs.m30, rhs.m01, lhs.m31, rhs.m11, lhs.m32, rhs.m21, lhs.m33, rhs.m31);
            r.m32 = Dot(lhs.m30, rhs.m02, lhs.m31, rhs.m12, lhs.m32, rhs.m22, lhs.m33, rhs.m32);
            r.m33 = Dot(lhs.m30, rhs.m03, lhs.m31, rhs.m13, lhs.m32, rhs.m23, lhs.m33, rhs.m33);
            return r;
        }

        static Vector4 Mul(Matrix4x4 lhs, Vector4 v) => new Vector4(
            Dot(lhs.m00, v.x, lhs.m01, v.y, lhs.m02, v.z, lhs.m03, v.w),
            Dot(lhs.m10, v.x, lhs.m11, v.y, lhs.m12, v.z, lhs.m13, v.w),
            Dot(lhs.m20, v.x, lhs.m21, v.y, lhs.m22, v.z, lhs.m23, v.w),
            Dot(lhs.m30, v.x, lhs.m31, v.y, lhs.m32, v.z, lhs.m33, v.w));

        static Vector3 MultiplyPoint(Matrix4x4 m, Vector3 point)
        {
            float x = Affine(m.m00, point.x, m.m01, point.y, m.m02, point.z, m.m03);
            float y = Affine(m.m10, point.x, m.m11, point.y, m.m12, point.z, m.m13);
            float z = Affine(m.m20, point.x, m.m21, point.y, m.m22, point.z, m.m23);
            float w = 1f / Affine(m.m30, point.x, m.m31, point.y, m.m32, point.z, m.m33);
            return new Vector3(x * w, y * w, z * w);
        }

        static Vector3 MultiplyPoint3x4(Matrix4x4 m, Vector3 point) => new Vector3(
            Affine(m.m00, point.x, m.m01, point.y, m.m02, point.z, m.m03),
            Affine(m.m10, point.x, m.m11, point.y, m.m12, point.z, m.m13),
            Affine(m.m20, point.x, m.m21, point.y, m.m22, point.z, m.m23));

        static Vector3 MultiplyVector(Matrix4x4 m, Vector3 vector) => new Vector3(
            Dot(m.m00, vector.x, m.m01, vector.y, m.m02, vector.z),
            Dot(m.m10, vector.x, m.m11, vector.y, m.m12, vector.z),
            Dot(m.m20, vector.x, m.m21, vector.y, m.m22, vector.z));

        // ----- Inputs -----

        static readonly float[] Specials =
        [
            0f, -0f, 1f, -1f, 0.5f, 2f, float.PositiveInfinity, float.NegativeInfinity, float.NaN,
            BitConverter.Int32BitsToSingle(0x7fc00001), BitConverter.Int32BitsToSingle(unchecked((int)0xffc12345)),
            BitConverter.Int32BitsToSingle(0x7f800001), BitConverter.Int32BitsToSingle(0x7fa5a5a5), // signalling NaNs
            float.Epsilon, -float.Epsilon, BitConverter.Int32BitsToSingle(0x007fffff), BitConverter.Int32BitsToSingle(unchecked((int)0x807fffff)),
            BitConverter.Int32BitsToSingle(0x00800000), float.MaxValue, float.MinValue, 1e30f, -1e30f, 1e-30f, 3.4e38f / 3f,
        ];

        /// A float of one class: UI-like (a mix of magnitudes and fractions, as tools/UnityProbe
        /// draws them), any bit pattern (NaNs with payloads, subnormals, infinities), special
        /// values, huge or tiny (overflow and underflow in the sums), or a subnormal.
        static float Draw(Random rng)
        {
            switch (rng.Next(10))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    return (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.Next(-2, 4)));
                case 4:
                case 5:
                    return BitConverter.Int32BitsToSingle(rng.Next() ^ (rng.Next(2) << 31));
                case 6:
                case 7:
                    return Specials[rng.Next(Specials.Length)];
                case 8:
                    return (float)((rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.Next(-45, 39)));
                default:
                    return BitConverter.Int32BitsToSingle(rng.Next(0x00800000) | (rng.Next(2) << 31));
            }
        }

        static Matrix4x4 DrawMatrix(Random rng)
        {
            var m = new Matrix4x4();
            for (int i = 0; i < 16; i++) m[i] = Draw(rng);
            // Most UI matrices are affine.
            if (rng.Next(3) == 0)
            {
                m.m30 = m.m31 = m.m32 = 0f;
                m.m33 = 1f;
            }
            return m;
        }

        static string Bits(params float[] values) => string.Join(",", Array.ConvertAll(values, f => float.IsNaN(f) ? "nan" : BitConverter.SingleToInt32Bits(f).ToString("x8")));
        static string Bits(Matrix4x4 m) { var v = new float[16]; for (int i = 0; i < 16; i++) v[i] = m[i]; return Bits(v); }
        static string Bits(Vector4 v) => Bits(v.x, v.y, v.z, v.w);
        static string Bits(Vector3 v) => Bits(v.x, v.y, v.z);

        const int Cases = 200_000;

        [Fact]
        public void ProductsHaveTheScalarArithmeticsBits()
        {
            var rng = new Random(20261031);
            int wrong = 0;
            string first = null;
            void Check(string what, string expected, string actual, Matrix4x4 m)
            {
                if (expected == actual) return;
                wrong++;
                first ??= $"{what}: {Bits(m)}: expected {expected}, got {actual}";
            }
            for (int i = 0; i < Cases; i++)
            {
                var a = DrawMatrix(rng);
                var b = DrawMatrix(rng);
                var p = new Vector3(Draw(rng), Draw(rng), Draw(rng));
                var q = new Vector4(Draw(rng), Draw(rng), Draw(rng), Draw(rng));
                Check("matrix", Bits(Mul(a, b)), Bits(a * b), a);
                Check("vector4", Bits(Mul(a, q)), Bits(a * q), a);
                Check("MultiplyPoint", Bits(MultiplyPoint(a, p)), Bits(a.MultiplyPoint(p)), a);
                Check("MultiplyPoint3x4", Bits(MultiplyPoint3x4(a, p)), Bits(a.MultiplyPoint3x4(p)), a);
                Check("MultiplyVector", Bits(MultiplyVector(a, p)), Bits(a.MultiplyVector(p)), a);
            }
            Assert.True(wrong == 0, $"{wrong} of {5 * Cases} differ; first {first}");
        }

        /// The canvas's vertex loop: each point as MultiplyPoint3x4 maps it.
        [Fact]
        public void MultiplyPoints3x4IsMultiplyPoint3x4OfEach()
        {
            var rng = new Random(31);
            for (int i = 0; i < 2000; i++)
            {
                var m = DrawMatrix(rng);
                var points = new Vector3[rng.Next(0, 9)];
                for (int j = 0; j < points.Length; j++) points[j] = new Vector3(Draw(rng), Draw(rng), Draw(rng));
                var mapped = new Vector3[points.Length];
                m.MultiplyPoints3x4(points, mapped);
                for (int j = 0; j < points.Length; j++) Assert.Equal(Bits(MultiplyPoint3x4(m, points[j])), Bits(mapped[j]));
            }
        }
    }
}
