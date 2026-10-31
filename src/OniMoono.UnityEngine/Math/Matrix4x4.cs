using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace UnityEngine
{
    /// Column-major 4x4 matrix; mRC is row R, column C. Fields are declared in memory order.
    public partial struct Matrix4x4
    {
        public float m00, m10, m20, m30;
        public float m01, m11, m21, m31;
        public float m02, m12, m22, m32;
        public float m03, m13, m23, m33;

        public Matrix4x4(Vector4 column0, Vector4 column1, Vector4 column2, Vector4 column3)
        {
            m00 = column0.x; m10 = column0.y; m20 = column0.z; m30 = column0.w;
            m01 = column1.x; m11 = column1.y; m21 = column1.z; m31 = column1.w;
            m02 = column2.x; m12 = column2.y; m22 = column2.z; m32 = column2.w;
            m03 = column3.x; m13 = column3.y; m23 = column3.z; m33 = column3.w;
        }

        public float this[int row, int column]
        {
            get => this[row + column * 4];
            set => this[row + column * 4] = value;
        }

        public float this[int index]
        {
            get => index switch
            {
                0 => m00, 1 => m10, 2 => m20, 3 => m30,
                4 => m01, 5 => m11, 6 => m21, 7 => m31,
                8 => m02, 9 => m12, 10 => m22, 11 => m32,
                12 => m03, 13 => m13, 14 => m23, 15 => m33,
                _ => throw new IndexOutOfRangeException("Invalid matrix index!"),
            };
            set
            {
                switch (index)
                {
                    case 0: m00 = value; break; case 1: m10 = value; break; case 2: m20 = value; break; case 3: m30 = value; break;
                    case 4: m01 = value; break; case 5: m11 = value; break; case 6: m21 = value; break; case 7: m31 = value; break;
                    case 8: m02 = value; break; case 9: m12 = value; break; case 10: m22 = value; break; case 11: m32 = value; break;
                    case 12: m03 = value; break; case 13: m13 = value; break; case 14: m23 = value; break; case 15: m33 = value; break;
                    default: throw new IndexOutOfRangeException("Invalid matrix index!");
                }
            }
        }

        public static Matrix4x4 zero => default;

        public static Matrix4x4 identity => new Matrix4x4(
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 0f, 0f, 1f));

        public Vector4 GetColumn(int index) => index switch
        {
            0 => new Vector4(m00, m10, m20, m30),
            1 => new Vector4(m01, m11, m21, m31),
            2 => new Vector4(m02, m12, m22, m32),
            3 => new Vector4(m03, m13, m23, m33),
            _ => throw new IndexOutOfRangeException("Invalid column index!"),
        };

        public Vector4 GetRow(int index) => index switch
        {
            0 => new Vector4(m00, m01, m02, m03),
            1 => new Vector4(m10, m11, m12, m13),
            2 => new Vector4(m20, m21, m22, m23),
            3 => new Vector4(m30, m31, m32, m33),
            _ => throw new IndexOutOfRangeException("Invalid row index!"),
        };

        public void SetColumn(int index, Vector4 column)
        {
            this[0, index] = column.x; this[1, index] = column.y; this[2, index] = column.z; this[3, index] = column.w;
        }

        public void SetRow(int index, Vector4 row)
        {
            this[index, 0] = row.x; this[index, 1] = row.y; this[index, 2] = row.z; this[index, 3] = row.w;
        }

        public Vector3 GetPosition() => new Vector3(m03, m13, m23);

        public static Matrix4x4 operator *(Matrix4x4 lhs, Matrix4x4 rhs)
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

        public static Vector4 operator *(Matrix4x4 lhs, Vector4 v)
        {
            if (Avx2.IsSupported)
            {
                var c = Columns(in lhs);
                var f = Avx.ConvertToVector128Single(Combine(in c, Avx.ConvertToVector256Double(Unsafe.As<Vector4, Vector128<float>>(ref v))));
                return Unsafe.As<Vector128<float>, Vector4>(ref f);
            }
            return new Vector4(
                Dot(lhs.m00, v.x, lhs.m01, v.y, lhs.m02, v.z, lhs.m03, v.w),
                Dot(lhs.m10, v.x, lhs.m11, v.y, lhs.m12, v.z, lhs.m13, v.w),
                Dot(lhs.m20, v.x, lhs.m21, v.y, lhs.m22, v.z, lhs.m23, v.w),
                Dot(lhs.m30, v.x, lhs.m31, v.y, lhs.m32, v.z, lhs.m33, v.w));
        }

        // With AVX2 the products of a matrix and a vector run four rows at once: a column of the
        // matrix widened to double, times an element widened and broadcast, the products added in
        // the same order (vmulpd and vaddpd round each lane as mulsd and addsd do, and no product
        // is fused into an add), each lane narrowed to float once. Each product of two floats is
        // exact in double, so only the adds round, as in Dot: the same bits as the scalar
        // arithmetic for every input whose result is not NaN, and NaN where it is NaN
        // (MatrixProductTests). Which NaN, when two NaN operands meet, depends on the order the
        // JIT puts them in, which changes with optimisation: the two agreed on every payload in a
        // debug build and not in a release one.
        // MultiplyPoint3x4 is about three times as fast, and six times over a mesh's vertices
        // (MultiplyPoints3x4). Matrix times matrix stays scalar: four columns this way measured
        // no faster, the time going to moving the 64-byte operands.

        /// A matrix's columns widened to double.
        internal readonly struct WideColumns(Vector256<double> c0, Vector256<double> c1, Vector256<double> c2, Vector256<double> c3)
        {
            public readonly Vector256<double> C0 = c0, C1 = c1, C2 = c2, C3 = c3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static WideColumns Columns(in Matrix4x4 m)
        {
            ref float f = ref Unsafe.As<Matrix4x4, float>(ref Unsafe.AsRef(in m));
            return new WideColumns(
                Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref f, 0)),
                Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref f, 4)),
                Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref f, 8)),
                Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref f, 12)));
        }

        /// The columns times a vector's elements, c0 v0 + c1 v1 + c2 v2 + c3 v3, in that order, the
        /// elements widened together and broadcast by permutes.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector256<double> Combine(in WideColumns c, Vector256<double> v)
            => Avx.Add(Avx.Add(Avx.Add(
                Avx.Multiply(c.C0, Avx2.Permute4x64(v, 0x00)),
                Avx.Multiply(c.C1, Avx2.Permute4x64(v, 0x55))),
                Avx.Multiply(c.C2, Avx2.Permute4x64(v, 0xAA))),
                Avx.Multiply(c.C3, Avx2.Permute4x64(v, 0xFF)));

        /// MultiplyPoint3x4's arithmetic on widened columns, all four rows (the last unused).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<float> AffineAvx(in WideColumns c, Vector3 p)
            => Avx.ConvertToVector128Single(Avx.Add(Avx.Add(Avx.Add(
                Avx.Multiply(c.C0, Vector256.Create((double)p.x)),
                Avx.Multiply(c.C1, Vector256.Create((double)p.y))),
                Avx.Multiply(c.C2, Vector256.Create((double)p.z))),
                c.C3));

        /// MultiplyPoint3x4 of each point, the matrix widened once.
        internal void MultiplyPoints3x4(ReadOnlySpan<Vector3> points, Span<Vector3> result)
        {
            if (!Avx2.IsSupported)
            {
                for (int i = 0; i < points.Length; i++) result[i] = MultiplyPoint3x4(points[i]);
                return;
            }
            var c = Columns(in this);
            for (int i = 0; i < points.Length; i++)
            {
                var f = AffineAvx(in c, points[i]);
                result[i] = new Vector3(f.GetElement(0), f.GetElement(1), f.GetElement(2));
            }
        }

        // The products are evaluated as Unity's own Matrix4x4 is under its Mono (ARCHITECTURE
        // D25: float32 off): each element's sum of products in double, left to right, rounded to
        // float once. Measured bit for bit in the original on 400 random cases of each product
        // (tools/UnityProbe MatrixMath.cs, mm-check.py); rounding every multiply and add, as
        // CoreCLR does with the float expression, matched 0 to 130 of them. ScrollRect's bounds
        // depend on it: whether a content's bounds in its viewport change by an ulp when the
        // content grows decides whether the scrollbar is updated again (docs/cleanroom/ugui.md,
        // "ScrollRect: bounds to the bit").
        static float Dot(float a0, float b0, float a1, float b1, float a2, float b2, float a3, float b3)
            => (float)((double)a0 * b0 + (double)a1 * b1 + (double)a2 * b2 + (double)a3 * b3);

        static float Dot(float a0, float b0, float a1, float b1, float a2, float b2)
            => (float)((double)a0 * b0 + (double)a1 * b1 + (double)a2 * b2);

        static float Affine(float a0, float b0, float a1, float b1, float a2, float b2, float a3)
            => (float)((double)a0 * b0 + (double)a1 * b1 + (double)a2 * b2 + a3);

        public static bool operator ==(Matrix4x4 lhs, Matrix4x4 rhs)
            => lhs.GetColumn(0) == rhs.GetColumn(0) && lhs.GetColumn(1) == rhs.GetColumn(1)
            && lhs.GetColumn(2) == rhs.GetColumn(2) && lhs.GetColumn(3) == rhs.GetColumn(3);

        public static bool operator !=(Matrix4x4 lhs, Matrix4x4 rhs) => !(lhs == rhs);

        /// Full projective transform with the perspective divide.
        public Vector3 MultiplyPoint(Vector3 point)
        {
            float x, y, z, w;
            if (Avx2.IsSupported)
            {
                var c = Columns(in this);
                var f = AffineAvx(in c, point);
                x = f.GetElement(0); y = f.GetElement(1); z = f.GetElement(2);
                w = 1f / f.GetElement(3);
            }
            else
            {
                x = Affine(m00, point.x, m01, point.y, m02, point.z, m03);
                y = Affine(m10, point.x, m11, point.y, m12, point.z, m13);
                z = Affine(m20, point.x, m21, point.y, m22, point.z, m23);
                w = 1f / Affine(m30, point.x, m31, point.y, m32, point.z, m33);
            }
            return new Vector3(x * w, y * w, z * w);
        }

        public Vector3 MultiplyPoint3x4(Vector3 point)
        {
            if (Avx2.IsSupported)
            {
                var c = Columns(in this);
                var f = AffineAvx(in c, point);
                return new Vector3(f.GetElement(0), f.GetElement(1), f.GetElement(2));
            }
            return new Vector3(
                Affine(m00, point.x, m01, point.y, m02, point.z, m03),
                Affine(m10, point.x, m11, point.y, m12, point.z, m13),
                Affine(m20, point.x, m21, point.y, m22, point.z, m23));
        }

        public Vector3 MultiplyVector(Vector3 vector)
        {
            if (Avx2.IsSupported)
            {
                var c = Columns(in this);
                var f = Avx.ConvertToVector128Single(Avx.Add(Avx.Add(
                    Avx.Multiply(c.C0, Vector256.Create((double)vector.x)),
                    Avx.Multiply(c.C1, Vector256.Create((double)vector.y))),
                    Avx.Multiply(c.C2, Vector256.Create((double)vector.z))));
                return new Vector3(f.GetElement(0), f.GetElement(1), f.GetElement(2));
            }
            return new Vector3(
                Dot(m00, vector.x, m01, vector.y, m02, vector.z),
                Dot(m10, vector.x, m11, vector.y, m12, vector.z),
                Dot(m20, vector.x, m21, vector.y, m22, vector.z));
        }

        public static Matrix4x4 Scale(Vector3 vector)
        {
            var m = identity;
            m.m00 = vector.x; m.m11 = vector.y; m.m22 = vector.z;
            return m;
        }

        public static Matrix4x4 Translate(Vector3 vector)
        {
            var m = identity;
            m.m03 = vector.x; m.m13 = vector.y; m.m23 = vector.z;
            return m;
        }

        /// The original's, bit for bit (400 of 400 quaternions, unit and not, and 400 from
        /// AngleAxis; tools/UnityProbe PROBE_SCENARIO=projection, pj-check.py): the usual
        /// quaternion matrix with each product stored to float, as Unity's managed code runs under
        /// its Mono (ARCHITECTURE D25), so the diagonal's 1 − (yy + zz) is evaluated in double and
        /// rounded once. The float form differed in the diagonal in 224 cases.
        public static Matrix4x4 Rotate(Quaternion q)
        {
            float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
            float xx = q.x * x, yy = q.y * y, zz = q.z * z;
            float xy = q.x * y, xz = q.x * z, yz = q.y * z;
            float wx = q.w * x, wy = q.w * y, wz = q.w * z;
            Matrix4x4 m;
            m.m00 = (float)(1.0 - ((double)yy + zz)); m.m10 = xy + wz; m.m20 = xz - wy; m.m30 = 0f;
            m.m01 = xy - wz; m.m11 = (float)(1.0 - ((double)xx + zz)); m.m21 = yz + wx; m.m31 = 0f;
            m.m02 = xz + wy; m.m12 = yz - wx; m.m22 = (float)(1.0 - ((double)xx + yy)); m.m32 = 0f;
            m.m03 = 0f; m.m13 = 0f; m.m23 = 0f; m.m33 = 1f;
            return m;
        }

        /// The original's, bit for bit (400 of 400): the quaternion matrix in float throughout,
        /// unlike Rotate, each column times its scale.
        public static Matrix4x4 TRS(Vector3 pos, Quaternion q, Vector3 s)
        {
            float x = q.x * 2f, y = q.y * 2f, z = q.z * 2f;
            float xx = q.x * x, yy = q.y * y, zz = q.z * z;
            float xy = q.x * y, xz = q.x * z, yz = q.y * z;
            float wx = q.w * x, wy = q.w * y, wz = q.w * z;
            Matrix4x4 m;
            m.m00 = 1f - (yy + zz); m.m10 = xy + wz; m.m20 = xz - wy; m.m30 = 0f;
            m.m01 = xy - wz; m.m11 = 1f - (xx + zz); m.m21 = yz + wx; m.m31 = 0f;
            m.m02 = xz + wy; m.m12 = yz - wx; m.m22 = 1f - (xx + yy); m.m32 = 0f;
            m.m33 = 1f;
            m.m00 *= s.x; m.m10 *= s.x; m.m20 *= s.x;
            m.m01 *= s.y; m.m11 *= s.y; m.m21 *= s.y;
            m.m02 *= s.z; m.m12 *= s.z; m.m22 *= s.z;
            m.m03 = pos.x; m.m13 = pos.y; m.m23 = pos.z;
            return m;
        }

        public void SetTRS(Vector3 pos, Quaternion q, Vector3 s) => this = TRS(pos, q, s);

        /// The same arithmetic as Mesa's _math_float_ortho, every operation rounded to float, and
        /// so the original's bit for bit (417 of 417 boxes, degenerate ones included; tools/UnityProbe
        /// PROBE_SCENARIO=projection, pj-check.py).
        public static Matrix4x4 Ortho(float left, float right, float bottom, float top, float zNear, float zFar)
        {
            var m = identity;
            m.m00 = 2f / (right - left);
            m.m11 = 2f / (top - bottom);
            m.m22 = -2f / (zFar - zNear);
            m.m03 = -(right + left) / (right - left);
            m.m13 = -(top + bottom) / (top - bottom);
            m.m23 = -(zFar + zNear) / (zFar - zNear);
            return m;
        }

        /// The original's, bit for bit (440 of 440 cases, degenerate ones included; tools/UnityProbe
        /// PROBE_SCENARIO=projection, pj-check.py): float arithmetic, the cotangent as cosf / sinf
        /// of (fov / 360) · π (the C library's sinf and cosf, which MathF calls), and the depth row
        /// over (near − far), so that near == far gives +∞ where Mesa's frustum and GLU give −∞.
        public static Matrix4x4 Perspective(float fov, float aspect, float zNear, float zFar)
        {
            float radians = fov / 360f * MathF.PI;
            float cotangent = MathF.Cos(radians) / MathF.Sin(radians);
            float depth = zNear - zFar;
            Matrix4x4 m = default;
            m.m00 = cotangent / aspect;
            m.m11 = cotangent;
            m.m22 = (zNear + zFar) / depth;
            m.m23 = 2f * zNear * zFar / depth;
            m.m32 = -1f;
            return m;
        }

        /// Mesa's frustum matrix, as the original (MesaFrustum).
        public static Matrix4x4 Frustum(float left, float right, float bottom, float top, float zNear, float zFar)
            => MesaFrustum.Frustum(left, right, bottom, top, zNear, zFar);

        /// gluLookAt's basis arithmetic in Unity's layout, as the original (GluLookAt).
        public static Matrix4x4 LookAt(Vector3 from, Vector3 to, Vector3 up) => GluLookAt.LookAt(from, to, up);

        public Matrix4x4 transpose => Transpose(this);

        public static Matrix4x4 Transpose(Matrix4x4 m)
            => new Matrix4x4(m.GetRow(0), m.GetRow(1), m.GetRow(2), m.GetRow(3));

        public Matrix4x4 inverse => Inverse(this);

        /// Mesa's Gauss–Jordan inversion, bit for bit as the original (MesaInverse); a matrix it
        /// finds singular gives Matrix4x4.zero, as in the original.
        public static Matrix4x4 Inverse(Matrix4x4 m) => MesaInverse.Invert(m, out var r) ? r : zero;

        public static Matrix4x4 Inverse(in Matrix4x4 m) => MesaInverse.Invert(m, out var r) ? r : zero;

        public static bool Inverse3DAffine(Matrix4x4 input, ref Matrix4x4 result) => Inverse3DAffine(in input, ref result);

        /// The inverse of the upper 3 × 4, as the original's, bit for bit (tools/UnityProbe
        /// PROBE_SCENARIO=projection, pj-check.py: 629 of 629 cases, the singular, near-singular,
        /// NaN and overflowing ones included; of a NaN input the NaNs' signs can differ, the
        /// original's operand order there not being CoreCLR's). Every operation rounds to float. The determinant sums
        /// column 0's lanes of col0 · (col1 × col2), each lane a difference of two triple
        /// products; the 3 × 3 is the cofactors (each a·b − c·d) times 1 / det; the translation is
        /// −((t.x r0 + t.y r1) + t.z r2) per row; the bottom row is 0 · (1 / det), so −0 for a
        /// negative determinant, then 1. The input's bottom row is ignored. A determinant whose
        /// float square is under 1e-25 (|det| ≤ 3.1622776e-13, found by bisection; NaN and
        /// infinity pass) gives false and the zero matrix.
        public static bool Inverse3DAffine(in Matrix4x4 input, ref Matrix4x4 result)
        {
            float m00 = input.m00, m01 = input.m01, m02 = input.m02, m03 = input.m03;
            float m10 = input.m10, m11 = input.m11, m12 = input.m12, m13 = input.m13;
            float m20 = input.m20, m21 = input.m21, m22 = input.m22, m23 = input.m23;
            float l0 = m00 * m11 * m22 - m00 * m21 * m12;
            float l1 = m10 * m21 * m02 - m10 * m01 * m22;
            float l2 = m20 * m01 * m12 - m20 * m11 * m02;
            float det = l0 + l1 + l2;
            if (det * det < 1e-25f)
            {
                result = zero;
                return false;
            }
            float k = 1f / det;
            Matrix4x4 r;
            r.m00 = (m11 * m22 - m21 * m12) * k;
            r.m01 = -(m01 * m22 - m21 * m02) * k;
            r.m02 = (m01 * m12 - m11 * m02) * k;
            r.m10 = -(m10 * m22 - m20 * m12) * k;
            r.m11 = (m00 * m22 - m20 * m02) * k;
            r.m12 = -(m00 * m12 - m10 * m02) * k;
            r.m20 = (m10 * m21 - m20 * m11) * k;
            r.m21 = -(m00 * m21 - m20 * m01) * k;
            r.m22 = (m00 * m11 - m10 * m01) * k;
            r.m03 = -(m03 * r.m00 + m13 * r.m01 + m23 * r.m02);
            r.m13 = -(m03 * r.m10 + m13 * r.m11 + m23 * r.m12);
            r.m23 = -(m03 * r.m20 + m13 * r.m21 + m23 * r.m22);
            float z = 0f * k;
            r.m30 = z; r.m31 = z; r.m32 = z; r.m33 = 1f;
            result = r;
            return true;
        }

        public float determinant
        {
            get
            {
                float a0 = m00 * m11 - m01 * m10, a1 = m00 * m12 - m02 * m10, a2 = m00 * m13 - m03 * m10;
                float a3 = m01 * m12 - m02 * m11, a4 = m01 * m13 - m03 * m11, a5 = m02 * m13 - m03 * m12;
                float b0 = m20 * m31 - m21 * m30, b1 = m20 * m32 - m22 * m30, b2 = m20 * m33 - m23 * m30;
                float b3 = m21 * m32 - m22 * m31, b4 = m21 * m33 - m23 * m31, b5 = m22 * m33 - m23 * m32;
                return a0 * b5 - a1 * b4 + a2 * b3 + a3 * b2 - a4 * b1 + a5 * b0;
            }
        }

        public bool isIdentity => this.Equals(identity);

        public override bool Equals(object other) => other is Matrix4x4 m && Equals(m);

        public bool Equals(Matrix4x4 other)
            => GetColumn(0).Equals(other.GetColumn(0)) && GetColumn(1).Equals(other.GetColumn(1))
            && GetColumn(2).Equals(other.GetColumn(2)) && GetColumn(3).Equals(other.GetColumn(3));

        public override int GetHashCode()
            => GetColumn(0).GetHashCode() ^ (GetColumn(1).GetHashCode() << 2) ^ (GetColumn(2).GetHashCode() >> 2) ^ (GetColumn(3).GetHashCode() >> 1);

        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);

        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (string.IsNullOrEmpty(format)) format = "F5";
            formatProvider ??= CultureInfo.InvariantCulture.NumberFormat;
            string F(float v) => UnityFormat.Number(v, format, formatProvider);
            return $"{F(m00)}\t{F(m01)}\t{F(m02)}\t{F(m03)}\n{F(m10)}\t{F(m11)}\t{F(m12)}\t{F(m13)}\n{F(m20)}\t{F(m21)}\t{F(m22)}\t{F(m23)}\n{F(m30)}\t{F(m31)}\t{F(m32)}\t{F(m33)}\n";
        }
    }
}
