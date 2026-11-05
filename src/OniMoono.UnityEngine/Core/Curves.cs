using System;

namespace UnityEngine
{
    public partial struct Keyframe
    {
        /// Unity's default tangent weight: one third of the segment, which makes a weighted
        /// segment identical to the Hermite one.
        internal const float DefaultWeight = 1f / 3f;
        internal const int WeightedIn = 1, WeightedOut = 2;

        private float m_Time;
        private float m_Value;
        private float m_InTangent;
        private float m_OutTangent;
        private int m_WeightedMode;
        private float m_InWeight;
        private float m_OutWeight;

        // Weights 0 unless given (as the original's keys read back; unweighted segments do not
        // use them).
        public Keyframe(float time, float value) : this(time, value, 0f, 0f, 0f, 0f) { }

        public Keyframe(float time, float value, float inTangent, float outTangent)
            : this(time, value, inTangent, outTangent, 0f, 0f) { }

        public Keyframe(float time, float value, float inTangent, float outTangent, float inWeight, float outWeight)
        {
            m_Time = time;
            m_Value = value;
            m_InTangent = inTangent;
            m_OutTangent = outTangent;
            m_WeightedMode = 0;
            m_InWeight = inWeight;
            m_OutWeight = outWeight;
        }

        public float time { get => m_Time; set => m_Time = value; }
        public float value { get => m_Value; set => m_Value = value; }
        public float inTangent { get => m_InTangent; set => m_InTangent = value; }
        public float outTangent { get => m_OutTangent; set => m_OutTangent = value; }
        public float inWeight { get => m_InWeight; set => m_InWeight = value; }
        public float outWeight { get => m_OutWeight; set => m_OutWeight = value; }

        internal int WeightedModeBits => m_WeightedMode;

        public WeightedMode weightedMode
        {
            get => (WeightedMode)m_WeightedMode;
            set => m_WeightedMode = (int)value;
        }

        internal static Keyframe FromSerialized(float time, float value, float inTangent, float outTangent, int weightedMode, float inWeight, float outWeight)
            => new() { m_Time = time, m_Value = value, m_InTangent = inTangent, m_OutTangent = outTangent, m_WeightedMode = weightedMode, m_InWeight = inWeight, m_OutWeight = outWeight };
    }

    public partial class AnimationCurve
    {
        private Keyframe[] m_Keys;
        private WrapMode m_PreWrapMode = WrapMode.ClampForever;
        private WrapMode m_PostWrapMode = WrapMode.ClampForever;

        public AnimationCurve() => m_Keys = Array.Empty<Keyframe>();

        public AnimationCurve(params Keyframe[] keys) => this.keys = keys ?? Array.Empty<Keyframe>();

        /// A copy of the keys; assigning keys stores a copy sorted by time.
        public Keyframe[] keys
        {
            get => (Keyframe[])m_Keys.Clone();
            set
            {
                var copy = (Keyframe[])(value ?? Array.Empty<Keyframe>()).Clone();
                // Stable by time.
                var indices = new int[copy.Length];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                Array.Sort(indices, (a, b) => copy[a].time != copy[b].time ? copy[a].time.CompareTo(copy[b].time) : a.CompareTo(b));
                m_Keys = Array.ConvertAll(indices, i => copy[i]);
            }
        }

        public Keyframe this[int index] => m_Keys[index];
        public int length => m_Keys.Length;

        public WrapMode preWrapMode { get => m_PreWrapMode; set => m_PreWrapMode = value; }
        public WrapMode postWrapMode { get => m_PostWrapMode; set => m_PostWrapMode = value; }

        public static AnimationCurve Constant(float timeStart, float timeEnd, float value) => Linear(timeStart, value, timeEnd, value);

        public static AnimationCurve Linear(float timeStart, float valueStart, float timeEnd, float valueEnd)
        {
            if (timeStart == timeEnd) return new AnimationCurve(new Keyframe(timeStart, valueStart));
            float tangent = (valueEnd - valueStart) / (timeEnd - timeStart);
            return new AnimationCurve(new Keyframe(timeStart, valueStart, 0f, tangent), new Keyframe(timeEnd, valueEnd, tangent, 0f));
        }

        public static AnimationCurve EaseInOut(float timeStart, float valueStart, float timeEnd, float valueEnd)
        {
            if (timeStart == timeEnd) return new AnimationCurve(new Keyframe(timeStart, valueStart));
            return new AnimationCurve(new Keyframe(timeStart, valueStart, 0f, 0f), new Keyframe(timeEnd, valueEnd, 0f, 0f));
        }

        public float Evaluate(float time)
        {
            var k = m_Keys;
            if (k.Length == 0) return 0f;
            if (k.Length == 1) return k[0].value;
            float start = k[0].time, end = k[^1].time;
            if (time < start) time = Wrap(time, start, end, m_PreWrapMode, before: true);
            else if (time > end) time = Wrap(time, start, end, m_PostWrapMode, before: false);
            if (time <= start) return k[0].value;
            if (time >= end) return k[^1].value;
            int hi = 1;
            while (k[hi].time < time) hi++;
            return EvaluateSegment(k[hi - 1], k[hi], time);
        }

        static float Wrap(float time, float start, float end, WrapMode mode, bool before)
        {
            float length = end - start;
            if (length <= 0f) return before ? start : end;
            switch (mode)
            {
                case WrapMode.Loop: return start + Mathf.Repeat(time - start, length);
                case WrapMode.PingPong: return start + Mathf.PingPong(time - start, length);
                default: return before ? start : end; // Clamp, ClampForever, Once, Default
            }
        }

        /// Cubic Hermite with the keys' tangents; a weighted key turns the segment into a cubic
        /// Bezier whose control points sit at the weights; an infinite tangent makes it a step.
        static float EvaluateSegment(Keyframe a, Keyframe b, float time)
        {
            if (float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) return a.value;
            float dt = b.time - a.time;
            bool weighted = (a.WeightedModeBits & Keyframe.WeightedOut) != 0 || (b.WeightedModeBits & Keyframe.WeightedIn) != 0;
            if (!weighted)
            {
                float t = (time - a.time) / dt;
                float t2 = t * t, t3 = t2 * t;
                float h00 = 2f * t3 - 3f * t2 + 1f, h10 = t3 - 2f * t2 + t, h01 = -2f * t3 + 3f * t2, h11 = t3 - t2;
                return h00 * a.value + h10 * dt * a.outTangent + h01 * b.value + h11 * dt * b.inTangent;
            }
            float wOut = (a.WeightedModeBits & Keyframe.WeightedOut) != 0 ? a.outWeight : Keyframe.DefaultWeight;
            float wIn = (b.WeightedModeBits & Keyframe.WeightedIn) != 0 ? b.inWeight : Keyframe.DefaultWeight;
            float x0 = a.time, x1 = a.time + wOut * dt, x2 = b.time - wIn * dt, x3 = b.time;
            float y0 = a.value, y1 = a.value + wOut * dt * a.outTangent, y2 = b.value - wIn * dt * b.inTangent, y3 = b.value;
            // Solve x(u) = time by Newton with bisection fallback; x is monotonic for weights in [0,1].
            float lo = 0f, hiU = 1f, u = (time - a.time) / dt;
            for (int i = 0; i < 16; i++)
            {
                float x = Bezier(x0, x1, x2, x3, u) - time;
                if (Math.Abs(x) < 1e-6f) break;
                if (x > 0f) hiU = u; else lo = u;
                float dx = BezierDerivative(x0, x1, x2, x3, u);
                float next = dx != 0f ? u - x / dx : -1f;
                u = next > lo && next < hiU ? next : (lo + hiU) * 0.5f;
            }
            return Bezier(y0, y1, y2, y3, u);
        }

        static float Bezier(float p0, float p1, float p2, float p3, float u)
        {
            float v = 1f - u;
            return v * v * v * p0 + 3f * v * v * u * p1 + 3f * v * u * u * p2 + u * u * u * p3;
        }

        static float BezierDerivative(float p0, float p1, float p2, float p3, float u)
        {
            float v = 1f - u;
            return 3f * v * v * (p1 - p0) + 6f * v * u * (p2 - p1) + 3f * u * u * (p3 - p2);
        }

        public int AddKey(float time, float value) => AddKey(new Keyframe(time, value));

        /// The other keys keep their tangents. An index out of range throws
        /// IndexOutOfRangeException("RemoveKey"), as in the original.
        public void RemoveKey(int index)
        {
            if (index < 0 || index >= m_Keys.Length) throw new IndexOutOfRangeException("RemoveKey");
            var list = new System.Collections.Generic.List<Keyframe>(m_Keys);
            list.RemoveAt(index);
            keys = list.ToArray();
        }

        /// Both tangents become the neighbouring segments' slopes mixed by weight: (1 + w) / 2
        /// of the one before and (1 - w) / 2 of the one after (the only one at an end), and the
        /// weights on the sides with a neighbour 1/3; a lone key is left as it is. Measured in
        /// the original (UnityProbe's apigaps "curve"): weights 0, ±1/2, 1, 2 and uneven spacing.
        public void SmoothTangents(int index, float weight)
        {
            if (index < 0 || index >= m_Keys.Length) throw new IndexOutOfRangeException("SmoothTangents");
            if (m_Keys.Length < 2) return;
            var copy = (Keyframe[])m_Keys.Clone();
            var k = copy[index];
            float tangent;
            if (index == 0) tangent = Slope(copy[0], copy[1]);
            else if (index == copy.Length - 1) tangent = Slope(copy[index - 1], copy[index]);
            else tangent = (1f + weight) * 0.5f * Slope(copy[index - 1], k) + (1f - weight) * 0.5f * Slope(k, copy[index + 1]);
            k.inTangent = tangent;
            k.outTangent = tangent;
            if (index > 0) k.inWeight = Keyframe.DefaultWeight;
            if (index < copy.Length - 1) k.outWeight = Keyframe.DefaultWeight;
            copy[index] = k;
            m_Keys = copy;
        }

        static float Slope(Keyframe a, Keyframe b)
        {
            float dt = b.time - a.time;
            return dt == 0f ? 0f : (b.value - a.value) / dt;
        }

        public int AddKey(Keyframe key)
        {
            foreach (var k in m_Keys)
                if (k.time == key.time) return -1;
            var list = new System.Collections.Generic.List<Keyframe>(m_Keys) { key };
            keys = list.ToArray();
            return Array.FindIndex(m_Keys, k => k.time == key.time);
        }

        public bool Equals(AnimationCurve other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (m_PreWrapMode != other.m_PreWrapMode || m_PostWrapMode != other.m_PostWrapMode || m_Keys.Length != other.m_Keys.Length) return false;
            for (int i = 0; i < m_Keys.Length; i++)
                if (!m_Keys[i].Equals(other.m_Keys[i])) return false;
            return true;
        }

        public override bool Equals(object o) => o is AnimationCurve c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(m_Keys.Length, m_PreWrapMode, m_PostWrapMode);
    }

    public partial struct GradientColorKey
    {
        public Color color;
        public float time;

        public GradientColorKey(Color col, float time)
        {
            color = col;
            this.time = time;
        }
    }

    public partial struct GradientAlphaKey
    {
        public float alpha;
        public float time;

        public GradientAlphaKey(float alpha, float time)
        {
            this.alpha = alpha;
            this.time = time;
        }
    }

    public partial class Gradient
    {

        /// Unity's gradient modes as stored in data: 0 Blend, 1 Fixed, 2 PerceptualBlend.
        internal int Mode;
        private GradientColorKey[] m_ColorKeys = { new(Color.white, 0f), new(Color.white, 1f) };
        private GradientAlphaKey[] m_AlphaKeys = { new(1f, 0f), new(1f, 1f) };

        public Gradient() { }

        /// From the serialized form: eight packed keys (color rgb + alpha a) with times in
        /// 1/65535 units, of which the first numColor / numAlpha are used.
        internal void SetFromSerialized(Color[] keys, ushort[] colorTimes, ushort[] alphaTimes, int mode, int numColor, int numAlpha)
        {
            Mode = mode;
            numColor = Math.Clamp(numColor, 1, 8);
            numAlpha = Math.Clamp(numAlpha, 1, 8);
            m_ColorKeys = new GradientColorKey[numColor];
            for (int i = 0; i < numColor; i++) m_ColorKeys[i] = new GradientColorKey(new Color(keys[i].r, keys[i].g, keys[i].b, 1f), colorTimes[i] / 65535f);
            m_AlphaKeys = new GradientAlphaKey[numAlpha];
            for (int i = 0; i < numAlpha; i++) m_AlphaKeys[i] = new GradientAlphaKey(keys[i].a, alphaTimes[i] / 65535f);
        }

        internal void CopyFrom(Gradient other)
        {
            Mode = other.Mode;
            m_ColorKeys = (GradientColorKey[])other.m_ColorKeys.Clone();
            m_AlphaKeys = (GradientAlphaKey[])other.m_AlphaKeys.Clone();
        }

        /// Unity keeps at most 8 keys, sorted by time.
        public GradientColorKey[] colorKeys
        {
            get => (GradientColorKey[])m_ColorKeys.Clone();
            set
            {
                if (value == null || value.Length == 0) throw new ArgumentException("Gradient needs at least one color key");
                var keys = (GradientColorKey[])value.Clone();
                if (keys.Length > 8) Array.Resize(ref keys, 8);
                Array.Sort(keys, (a, b) => a.time.CompareTo(b.time));
                m_ColorKeys = keys;
            }
        }

        public int colorKeyCount => m_ColorKeys.Length;
        public int alphaKeyCount => m_AlphaKeys.Length;
        /// Unity keeps at most 8 keys, sorted by time.
        public GradientAlphaKey[] alphaKeys
        {
            get => (GradientAlphaKey[])m_AlphaKeys.Clone();
            set
            {
                if (value == null || value.Length == 0) throw new ArgumentException("Gradient needs at least one alpha key");
                var keys = (GradientAlphaKey[])value.Clone();
                if (keys.Length > 8) Array.Resize(ref keys, 8);
                Array.Sort(keys, (a, b) => a.time.CompareTo(b.time));
                m_AlphaKeys = keys;
            }
        }

        public void SetKeys(GradientColorKey[] colorKeys, GradientAlphaKey[] alphaKeys)
        {
            this.colorKeys = colorKeys;
            this.alphaKeys = alphaKeys;
        }

        public Color Evaluate(float time)
        {
            Color c = EvaluateColor(time);
            c.a = EvaluateAlpha(time);
            return c;
        }

        Color EvaluateColor(float t)
        {
            var keys = m_ColorKeys;
            if (t <= keys[0].time) return keys[0].color;
            if (t >= keys[^1].time) return keys[^1].color;
            int hi = 1;
            while (keys[hi].time < t) hi++;
            var a = keys[hi - 1];
            var b = keys[hi];
            if (Mode == 1) return b.color; // Fixed: the color of the key at or after t
            float f = (t - a.time) / (b.time - a.time);
            return Mode == 2 ? Oklab.Lerp(a.color, b.color, f) : Color.LerpUnclamped(a.color, b.color, f);
        }

        float EvaluateAlpha(float t)
        {
            var keys = m_AlphaKeys;
            if (t <= keys[0].time) return keys[0].alpha;
            if (t >= keys[^1].time) return keys[^1].alpha;
            int hi = 1;
            while (keys[hi].time < t) hi++;
            var a = keys[hi - 1];
            var b = keys[hi];
            if (Mode == 1) return b.alpha;
            float f = (t - a.time) / (b.time - a.time);
            return a.alpha + (b.alpha - a.alpha) * f;
        }

        public bool Equals(Gradient other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (Mode != other.Mode || m_ColorKeys.Length != other.m_ColorKeys.Length || m_AlphaKeys.Length != other.m_AlphaKeys.Length) return false;
            for (int i = 0; i < m_ColorKeys.Length; i++)
                if (!m_ColorKeys[i].color.Equals(other.m_ColorKeys[i].color) || m_ColorKeys[i].time != other.m_ColorKeys[i].time) return false;
            for (int i = 0; i < m_AlphaKeys.Length; i++)
                if (m_AlphaKeys[i].alpha != other.m_AlphaKeys[i].alpha || m_AlphaKeys[i].time != other.m_AlphaKeys[i].time) return false;
            return true;
        }

        public override bool Equals(object o) => o is Gradient g && Equals(g);
        public override int GetHashCode() => HashCode.Combine(Mode, m_ColorKeys.Length, m_AlphaKeys.Length);

        /// Perceptual blending in the Oklab color space (gamma-space input).
        static class Oklab
        {
            public static Color Lerp(Color a, Color b, float t)
            {
                var la = ToOklab(a);
                var lb = ToOklab(b);
                return FromOklab(Vector3.LerpUnclamped(la, lb, t), a.a + (b.a - a.a) * t);
            }

            static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
            static float ToGamma(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;

            static Vector3 ToOklab(Color c)
            {
                float r = ToLinear(c.r), g = ToLinear(c.g), b = ToLinear(c.b);
                float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
                float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
                float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
                return new Vector3(
                    0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                    1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                    0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
            }

            static Color FromOklab(Vector3 lab, float alpha)
            {
                float l = lab.x + 0.3963377774f * lab.y + 0.2158037573f * lab.z;
                float m = lab.x - 0.1055613458f * lab.y - 0.0638541728f * lab.z;
                float s = lab.x - 0.0894841775f * lab.y - 1.2914855480f * lab.z;
                l = l * l * l; m = m * m * m; s = s * s * s;
                return new Color(
                    ToGamma(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s),
                    ToGamma(-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s),
                    ToGamma(-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s),
                    alpha);
            }
        }
    }
}
