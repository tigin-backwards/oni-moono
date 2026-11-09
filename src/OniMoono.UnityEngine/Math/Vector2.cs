using System;
using System.Globalization;

namespace UnityEngine
{
    /// Members whose result rounds more than once are evaluated as the original's Mono runs them
    /// (float32 off, D25): in double, rounded to float only where a value is stored (a local, a
    /// component, an argument or a return). Each one matches the original bit for bit on
    /// UnityProbe's vecmath cases (tools/UnityProbe/vm-check.py). A single operation rounds the
    /// same either way, so the operators stay plain float.
    public partial struct Vector2
    {
        public const float kEpsilon = 1E-05f;
        public const float kEpsilonNormalSqrt = 1E-15f;

        public float x;
        public float y;

        public Vector2(float x, float y) { this.x = x; this.y = y; }

        public float this[int index]
        {
            get => index switch { 0 => x, 1 => y, _ => throw new IndexOutOfRangeException("Invalid Vector2 index!") };
            set
            {
                switch (index)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    default: throw new IndexOutOfRangeException("Invalid Vector2 index!");
                }
            }
        }

        public void Set(float newX, float newY) { x = newX; y = newY; }

        public float magnitude => (float)Math.Sqrt((double)x * x + (double)y * y);
        public float sqrMagnitude => (float)((double)x * x + (double)y * y);

        public Vector2 normalized
        {
            get
            {
                var v = new Vector2(x, y);
                v.Normalize();
                return v;
            }
        }

        public void Normalize()
        {
            float mag = magnitude;
            this = mag > kEpsilon ? this / mag : zero;
        }

        public static Vector2 zero => new Vector2(0f, 0f);
        public static Vector2 one => new Vector2(1f, 1f);
        public static Vector2 up => new Vector2(0f, 1f);
        public static Vector2 down => new Vector2(0f, -1f);
        public static Vector2 left => new Vector2(-1f, 0f);
        public static Vector2 right => new Vector2(1f, 0f);
        public static Vector2 positiveInfinity => new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        public static Vector2 negativeInfinity => new Vector2(float.NegativeInfinity, float.NegativeInfinity);

        public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector2((float)(a.x + ((double)b.x - a.x) * t), (float)(a.y + ((double)b.y - a.y) * t));
        }

        public static Vector2 LerpUnclamped(Vector2 a, Vector2 b, float t)
            => new Vector2((float)(a.x + ((double)b.x - a.x) * t), (float)(a.y + ((double)b.y - a.y) * t));

        public static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDistanceDelta)
        {
            float dx = target.x - current.x, dy = target.y - current.y;
            float sq = (float)((double)dx * dx + (double)dy * dy);
            if (sq == 0f || (maxDistanceDelta >= 0f && sq <= (double)maxDistanceDelta * maxDistanceDelta)) return target;
            float dist = (float)Math.Sqrt(sq);
            return new Vector2((float)(current.x + (double)dx / dist * maxDistanceDelta), (float)(current.y + (double)dy / dist * maxDistanceDelta));
        }

        public static Vector2 Scale(Vector2 a, Vector2 b) => new Vector2(a.x * b.x, a.y * b.y);
        public void Scale(Vector2 scale) { x *= scale.x; y *= scale.y; }

        public static Vector2 Reflect(Vector2 inDirection, Vector2 inNormal)
        {
            float f = -2f * Dot(inNormal, inDirection);
            return new Vector2((float)((double)f * inNormal.x + inDirection.x), (float)((double)f * inNormal.y + inDirection.y));
        }

        public static Vector2 Perpendicular(Vector2 inDirection) => new Vector2(-inDirection.y, inDirection.x);
        public static float Dot(Vector2 lhs, Vector2 rhs) => (float)((double)lhs.x * rhs.x + (double)lhs.y * rhs.y);

        public static float Angle(Vector2 from, Vector2 to)
        {
            float denominator = (float)Math.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denominator < kEpsilonNormalSqrt) return 0f;
            float dot = Mathf.Clamp(Dot(from, to) / denominator, -1f, 1f);
            return (float)Math.Acos(dot) * Mathf.Rad2Deg;
        }

        public static float SignedAngle(Vector2 from, Vector2 to)
        {
            float unsigned = Angle(from, to);
            float sign = Mathf.Sign(from.x * to.y - from.y * to.x);
            return unsigned * sign;
        }

        public static float Distance(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return (float)Math.Sqrt((double)dx * dx + (double)dy * dy);
        }

        public static Vector2 ClampMagnitude(Vector2 vector, float maxLength)
        {
            float sq = vector.sqrMagnitude;
            if (sq > (double)maxLength * maxLength)
            {
                float mag = (float)Math.Sqrt(sq);
                return new Vector2(vector.x / mag, vector.y / mag) * maxLength;
            }
            return vector;
        }

        public static float SqrMagnitude(Vector2 a) => a.sqrMagnitude;
        public float SqrMagnitude() => sqrMagnitude;
        public static Vector2 Min(Vector2 lhs, Vector2 rhs) => new Vector2(Mathf.Min(lhs.x, rhs.x), Mathf.Min(lhs.y, rhs.y));
        public static Vector2 Max(Vector2 lhs, Vector2 rhs) => new Vector2(Mathf.Max(lhs.x, rhs.x), Mathf.Max(lhs.y, rhs.y));

        public static Vector2 SmoothDamp(Vector2 current, Vector2 target, ref Vector2 currentVelocity, float smoothTime)
            => SmoothDamp(current, target, ref currentVelocity, smoothTime, Mathf.Infinity, Time.deltaTime);

        public static Vector2 SmoothDamp(Vector2 current, Vector2 target, ref Vector2 currentVelocity, float smoothTime, float maxSpeed)
            => SmoothDamp(current, target, ref currentVelocity, smoothTime, maxSpeed, Time.deltaTime);

        /// Mathf.SmoothDamp's spring on the whole vector: the offset to the target is limited in
        /// length to maxSpeed * smoothTime, and a step ending beyond the target (along the
        /// direction from current to target) lands on it and stops.
        public static Vector2 SmoothDamp(Vector2 current, Vector2 target, ref Vector2 currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            var spring = new OniMoono.Engine.SmoothDampSpring(smoothTime, deltaTime);
            Vector2 offset = ClampMagnitude(current - target, maxSpeed * spring.SmoothTime);
            Vector2 goal = current - offset;
            Vector2 impulse = (currentVelocity + offset * spring.Omega) * deltaTime;
            currentVelocity = (currentVelocity - impulse * spring.Omega) * spring.Decay;
            Vector2 next = goal + (offset + impulse) * spring.Decay;
            if (Dot(target - current, next - target) > 0f)
            {
                currentVelocity = zero;
                return target;
            }
            return next;
        }

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, Vector2 b) => new Vector2(a.x * b.x, a.y * b.y);
        public static Vector2 operator /(Vector2 a, Vector2 b) => new Vector2(a.x / b.x, a.y / b.y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.x, -a.y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.x * d, a.y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.x / d, a.y / d);

        /// Approximate equality: squared distance below kEpsilon², as Unity defines ==.
        public static bool operator ==(Vector2 lhs, Vector2 rhs)
        {
            float dx = lhs.x - rhs.x, dy = lhs.y - rhs.y;
            return (double)dx * dx + (double)dy * dy < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector2 lhs, Vector2 rhs) => !(lhs == rhs);

        public static implicit operator Vector2(Vector3 v) => new Vector2(v.x, v.y);
        public static implicit operator Vector3(Vector2 v) => new Vector3(v.x, v.y, 0f);

        public override bool Equals(object other) => other is Vector2 v && Equals(v);
        public bool Equals(Vector2 other) => x == other.x && y == other.y;
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);

        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);

        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (string.IsNullOrEmpty(format)) format = "F2";
            formatProvider ??= CultureInfo.InvariantCulture.NumberFormat;
            return string.Format("({0}, {1})", UnityFormat.Number(x, format, formatProvider), UnityFormat.Number(y, format, formatProvider));
        }
    }
}
