using System;

namespace UnityEngine
{
    public partial struct Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Deg2Rad = PI * 2f / 360f;
        public const float Rad2Deg = 1f / Deg2Rad;

        /// Smallest positive float. Unity uses the minimum denormal unless the platform flushes
        /// denormals to zero; x64 desktop players do not.
        public static readonly float Epsilon = float.Epsilon;

        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Tan(float f) => (float)Math.Tan(f);
        public static float Asin(float f) => (float)Math.Asin(f);
        public static float Acos(float f) => (float)Math.Acos(f);
        public static float Atan(float f) => (float)Math.Atan(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Abs(float f) => Math.Abs(f);
        public static int Abs(int value) => Math.Abs(value);
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Exp(float power) => (float)Math.Exp(power);
        public static float Log(float f, float p) => (float)Math.Log(f, p);
        public static float Log(float f) => (float)Math.Log(f);
        public static float Log10(float f) => (float)Math.Log10(f);
        public static float Ceil(float f) => (float)Math.Ceiling(f);
        public static float Floor(float f) => (float)Math.Floor(f);
        /// Rounds half to even, like System.Math.Round.
        public static float Round(float f) => (float)Math.Round(f);
        // Unity's *ToInt convert as Mono does: NaN and values out of range give int.MinValue
        // (ARCHITECTURE D29).
        public static int CeilToInt(float f) => OniMoono.Engine.MonoConvert.ToInt32(Math.Ceiling(f));
        public static int FloorToInt(float f) => OniMoono.Engine.MonoConvert.ToInt32(Math.Floor(f));
        public static int RoundToInt(float f) => OniMoono.Engine.MonoConvert.ToInt32(Math.Round(f));
        public static float Sign(float f) => f >= 0f ? 1f : -1f;

        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;

        public static float Min(params float[] values)
        {
            if (values.Length == 0) return 0f;
            float m = values[0];
            for (int i = 1; i < values.Length; i++) if (values[i] < m) m = values[i];
            return m;
        }

        public static int Min(params int[] values)
        {
            if (values.Length == 0) return 0;
            int m = values[0];
            for (int i = 1; i < values.Length; i++) if (values[i] < m) m = values[i];
            return m;
        }

        public static float Max(params float[] values)
        {
            if (values.Length == 0) return 0f;
            float m = values[0];
            for (int i = 1; i < values.Length; i++) if (values[i] > m) m = values[i];
            return m;
        }

        public static int Max(params int[] values)
        {
            if (values.Length == 0) return 0;
            int m = values[0];
            for (int i = 1; i < values.Length; i++) if (values[i] > m) m = values[i];
            return m;
        }

        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        // In double, rounded once, as the original's Mono evaluates them (D25; see Vector2).
        public static float Lerp(float a, float b, float t) => (float)(a + ((double)b - a) * Clamp01(t));
        public static float LerpUnclamped(float a, float b, float t) => (float)(a + ((double)b - a) * t);

        public static float LerpAngle(float a, float b, float t)
        {
            float delta = Repeat(b - a, 360f);
            if (delta > 180f) delta -= 360f;
            return a + delta * Clamp01(t);
        }

        public static float InverseLerp(float a, float b, float value) => a != b ? Clamp01((float)(((double)value - a) / ((double)b - a))) : 0f;

        public static float MoveTowards(float current, float target, float maxDelta)
            => Abs(target - current) <= maxDelta ? target : current + Sign(target - current) * maxDelta;

        public static float SmoothStep(float from, float to, float t)
        {
            t = Clamp01(t);
            t = -2f * t * t * t + 3f * t * t;
            return to * t + from * (1f - t);
        }

        /// Relative comparison with a floor of 8 * Epsilon, as Unity defines it.
        public static bool Approximately(float a, float b)
            => Abs(b - a) < Max(1E-06f * Max(Abs(a), Abs(b)), Epsilon * 8f);

        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime)
            => SmoothDamp(current, target, ref currentVelocity, smoothTime, Infinity, Time.deltaTime);

        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float maxSpeed)
            => SmoothDamp(current, target, ref currentVelocity, smoothTime, maxSpeed, Time.deltaTime);

        /// A critically damped spring towards target (Game Programming Gems 4, 1.10). The distance
        /// still to cover is limited to maxSpeed * smoothTime; a step that would pass the target
        /// lands on it and stops.
        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            var spring = new OniMoono.Engine.SmoothDampSpring(smoothTime, deltaTime);
            float reach = maxSpeed * spring.SmoothTime;
            float offset = Clamp(current - target, -reach, reach);
            float goal = current - offset;
            float impulse = (currentVelocity + spring.Omega * offset) * deltaTime;
            currentVelocity = (currentVelocity - spring.Omega * impulse) * spring.Decay;
            float next = goal + (offset + impulse) * spring.Decay;
            // Overshoot: the target lies above current exactly when the step ends above it.
            if (target > current == next > target)
            {
                currentVelocity = 0f;
                return target;
            }
            return next;
        }

        public static float Repeat(float t, float length) => Clamp(t - Floor(t / length) * length, 0f, length);

        public static float PingPong(float t, float length)
        {
            t = Repeat(t, length * 2f);
            return length - Abs(t - length);
        }

        /// Unity's noise as measured in the original player: Ken Perlin's improved (3D) noise at
        /// z = 0 of the coordinates' absolute values, mapped as (n + 0.69) / 1.483 (0.4653 on whole
        /// coordinates, repeating every 256 units); it matches the player's to about 1e-4.
        public static float PerlinNoise(float x, float y)
        {
            x = Abs(x);
            y = Abs(y);
            int xi = (int)Floor(x) & 255, yi = (int)Floor(y) & 255;
            float xf = x - Floor(x), yf = y - Floor(y);
            float u = Fade(xf), v = Fade(yf);
            float bottom = Lerp(Gradient(Hash(xi, yi), xf, yf), Gradient(Hash(xi + 1, yi), xf - 1f, yf), u);
            float top = Lerp(Gradient(Hash(xi, yi + 1), xf, yf - 1f), Gradient(Hash(xi + 1, yi + 1), xf - 1f, yf - 1f), u);
            return (Lerp(bottom, top, v) + 0.69f) / 1.483f;

            static int Hash(int i, int j) => Permutation[Permutation[Permutation[i] + j]];
            static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
            // The improved noise's 3D gradients with z = 0.
            static float Gradient(int hash, float dx, float dy)
            {
                int h = hash & 15;
                float a = h < 8 ? dx : dy;
                float b = h < 4 ? dy : h == 12 || h == 14 ? dx : 0f;
                return ((h & 1) == 0 ? a : -a) + ((h & 2) == 0 ? b : -b);
            }
        }

        /// Ken Perlin's reference permutation, twice over.
        static readonly int[] Permutation = BuildPermutation();

        static int[] BuildPermutation()
        {
            int[] p =
            [
                151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142, 8, 99, 37, 240, 21, 10, 23,
                190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87, 174, 20,
                125, 136, 171, 168, 68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60, 211, 133, 230, 220,
                105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89, 18, 169, 200, 196,
                135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255,
                82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221,
                153, 101, 155, 167, 43, 172, 9, 129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97, 228,
                251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106,
                157, 184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78,
                66, 215, 61, 156, 180,
            ];
            var twice = new int[512];
            for (int i = 0; i < 512; i++) twice[i] = p[i & 255];
            return twice;
        }

        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360f);
            if (delta > 180f) delta -= 360f;
            return delta;
        }

        public static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;

        public static int NextPowerOfTwo(int value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        public static int ClosestPowerOfTwo(int value)
        {
            int next = NextPowerOfTwo(value);
            int prev = next >> 1;
            return value - prev < next - value ? prev : next;
        }
    }
}

namespace OniMoono.Engine
{
    /// The terms shared by every SmoothDamp overload: the clamped smooth time, the spring's
    /// angular frequency, and the per-step decay (a cubic approximation of exp(-omega * dt)).
    internal readonly struct SmoothDampSpring
    {
        public readonly float SmoothTime;
        public readonly float Omega;
        public readonly float Decay;

        public SmoothDampSpring(float smoothTime, float deltaTime)
        {
            SmoothTime = Math.Max(0.0001f, smoothTime);
            Omega = 2f / SmoothTime;
            float x = Omega * deltaTime;
            Decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        }
    }
}
