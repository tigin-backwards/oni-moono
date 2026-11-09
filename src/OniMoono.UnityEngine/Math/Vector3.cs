using System;
using System.Globalization;

namespace UnityEngine
{
    /// Evaluated as the original's Mono runs it, like Vector2 (see there).
    public partial struct Vector3
    {
        public const float kEpsilon = 1E-05f;
        public const float kEpsilonNormalSqrt = 1E-15f;

        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; z = 0f; }

        public float this[int index]
        {
            get => index switch { 0 => x, 1 => y, 2 => z, _ => throw new IndexOutOfRangeException("Invalid Vector3 index!") };
            set
            {
                switch (index)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    default: throw new IndexOutOfRangeException("Invalid Vector3 index!");
                }
            }
        }

        public void Set(float newX, float newY, float newZ) { x = newX; y = newY; z = newZ; }

        public float magnitude => (float)Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
        public float sqrMagnitude => (float)((double)x * x + (double)y * y + (double)z * z);
        public Vector3 normalized => Normalize(this);

        public void Normalize() => this = Normalize(this);

        public static Vector3 Normalize(Vector3 value)
        {
            float mag = Magnitude(value);
            return mag > kEpsilon ? value / mag : zero;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 positiveInfinity => new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        public static Vector3 negativeInfinity => new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            return LerpUnclamped(a, b, Mathf.Clamp01(t));
        }

        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
            => new Vector3((float)(a.x + ((double)b.x - a.x) * t), (float)(a.y + ((double)b.y - a.y) * t), (float)(a.z + ((double)b.z - a.z) * t));

        /// Spherical interpolation of direction with linear interpolation of length; t is clamped.
        public static Vector3 Slerp(Vector3 a, Vector3 b, float t) => SlerpUnclamped(a, b, Mathf.Clamp01(t));

        public static Vector3 SlerpUnclamped(Vector3 a, Vector3 b, float t)
        {
            float aMag = a.magnitude, bMag = b.magnitude;
            if (aMag < kEpsilon || bMag < kEpsilon) return LerpUnclamped(a, b, t);
            Vector3 aDir = a / aMag, bDir = b / bMag;
            float length = aMag + (bMag - aMag) * t;
            float dot = Mathf.Clamp(Dot(aDir, bDir), -1f, 1f);
            if (dot > 1f - kEpsilon) return LerpUnclamped(a, b, t);
            Vector3 axis;
            if (dot < -1f + kEpsilon)
            {
                // Antiparallel: any axis perpendicular to a.
                axis = Cross(aDir, Math.Abs(aDir.x) < 0.9f ? right : up).normalized;
            }
            else
            {
                axis = Cross(aDir, bDir).normalized;
            }
            float angle = (float)Math.Acos(dot) * t;
            return Quaternion.AngleAxis(angle * Mathf.Rad2Deg, axis) * aDir * length;
        }

        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
        {
            float dx = target.x - current.x, dy = target.y - current.y, dz = target.z - current.z;
            float sq = (float)((double)dx * dx + (double)dy * dy + (double)dz * dz);
            if (sq == 0f || (maxDistanceDelta >= 0f && sq <= (double)maxDistanceDelta * maxDistanceDelta)) return target;
            float dist = (float)Math.Sqrt(sq);
            return new Vector3((float)(current.x + (double)dx / dist * maxDistanceDelta), (float)(current.y + (double)dy / dist * maxDistanceDelta),
                (float)(current.z + (double)dz / dist * maxDistanceDelta));
        }




        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        public void Scale(Vector3 scale) { x *= scale.x; y *= scale.y; z *= scale.z; }

        public static Vector3 Cross(Vector3 lhs, Vector3 rhs)
            => new Vector3((float)((double)lhs.y * rhs.z - (double)lhs.z * rhs.y), (float)((double)lhs.z * rhs.x - (double)lhs.x * rhs.z),
                (float)((double)lhs.x * rhs.y - (double)lhs.y * rhs.x));

        public static Vector3 Reflect(Vector3 inDirection, Vector3 inNormal)
        {
            float f = -2f * Dot(inNormal, inDirection);
            return new Vector3((float)((double)f * inNormal.x + inDirection.x), (float)((double)f * inNormal.y + inDirection.y),
                (float)((double)f * inNormal.z + inDirection.z));
        }

        public static float Dot(Vector3 lhs, Vector3 rhs) => (float)((double)lhs.x * rhs.x + (double)lhs.y * rhs.y + (double)lhs.z * rhs.z);

        public static Vector3 Project(Vector3 vector, Vector3 onNormal)
        {
            float sqrMag = Dot(onNormal, onNormal);
            if (sqrMag < Mathf.Epsilon) return zero;
            float dot = Dot(vector, onNormal);
            return onNormal * (dot / sqrMag);
        }

        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            float sqrMag = Dot(planeNormal, planeNormal);
            if (sqrMag < Mathf.Epsilon) return vector;
            float dot = Dot(vector, planeNormal);
            return new Vector3(vector.x - planeNormal.x * dot / sqrMag, vector.y - planeNormal.y * dot / sqrMag, vector.z - planeNormal.z * dot / sqrMag);
        }

        public static float Angle(Vector3 from, Vector3 to)
        {
            float denominator = (float)Math.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denominator < kEpsilonNormalSqrt) return 0f;
            float dot = Mathf.Clamp(Dot(from, to) / denominator, -1f, 1f);
            return (float)Math.Acos(dot) * Mathf.Rad2Deg;
        }

        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
        {
            float unsigned = Angle(from, to);
            float cx = from.y * to.z - from.z * to.y;
            float cy = from.z * to.x - from.x * to.z;
            float cz = from.x * to.y - from.y * to.x;
            float sign = Mathf.Sign(axis.x * cx + axis.y * cy + axis.z * cz);
            return unsigned * sign;
        }

        public static float Distance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
        }

        public static Vector3 ClampMagnitude(Vector3 vector, float maxLength)
        {
            float sq = vector.sqrMagnitude;
            if (sq > (double)maxLength * maxLength)
            {
                float mag = (float)Math.Sqrt(sq);
                return new Vector3(vector.x / mag, vector.y / mag, vector.z / mag) * maxLength;
            }
            return vector;
        }

        public static float Magnitude(Vector3 vector) => vector.magnitude;
        public static float SqrMagnitude(Vector3 vector) => vector.sqrMagnitude;
        public static Vector3 Min(Vector3 lhs, Vector3 rhs) => new Vector3(Mathf.Min(lhs.x, rhs.x), Mathf.Min(lhs.y, rhs.y), Mathf.Min(lhs.z, rhs.z));
        public static Vector3 Max(Vector3 lhs, Vector3 rhs) => new Vector3(Mathf.Max(lhs.x, rhs.x), Mathf.Max(lhs.y, rhs.y), Mathf.Max(lhs.z, rhs.z));

        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float smoothTime)
            => SmoothDamp(current, target, ref currentVelocity, smoothTime, Mathf.Infinity, Time.deltaTime);

        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float smoothTime, float maxSpeed)
            => SmoothDamp(current, target, ref currentVelocity, smoothTime, maxSpeed, Time.deltaTime);

        /// Mathf.SmoothDamp's spring on the whole vector: the offset to the target is limited in
        /// length to maxSpeed * smoothTime, and a step ending beyond the target (along the
        /// direction from current to target) lands on it and stops.
        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            var spring = new OniMoono.Engine.SmoothDampSpring(smoothTime, deltaTime);
            Vector3 offset = ClampMagnitude(current - target, maxSpeed * spring.SmoothTime);
            Vector3 goal = current - offset;
            Vector3 impulse = (currentVelocity + offset * spring.Omega) * deltaTime;
            currentVelocity = (currentVelocity - impulse * spring.Omega) * spring.Decay;
            Vector3 next = goal + (offset + impulse) * spring.Decay;
            if (Dot(target - current, next - target) > 0f)
            {
                currentVelocity = zero;
                return target;
            }
            return next;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);

        /// Approximate equality: squared distance below kEpsilon², as Unity defines ==.
        public static bool operator ==(Vector3 lhs, Vector3 rhs)
        {
            float dx = lhs.x - rhs.x, dy = lhs.y - rhs.y, dz = lhs.z - rhs.z;
            return (double)dx * dx + (double)dy * dy + (double)dz * dz < kEpsilon * kEpsilon;
        }

        public static bool operator !=(Vector3 lhs, Vector3 rhs) => !(lhs == rhs);

        public override bool Equals(object other) => other is Vector3 v && Equals(v);
        public bool Equals(Vector3 other) => x == other.x && y == other.y && z == other.z;
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);

        public override string ToString() => ToString(null, null);
        public string ToString(string format) => ToString(format, null);

        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (string.IsNullOrEmpty(format)) format = "F2";
            formatProvider ??= CultureInfo.InvariantCulture.NumberFormat;
            return string.Format("({0}, {1}, {2})", UnityFormat.Number(x, format, formatProvider), UnityFormat.Number(y, format, formatProvider), UnityFormat.Number(z, format, formatProvider));
        }
    }
}
