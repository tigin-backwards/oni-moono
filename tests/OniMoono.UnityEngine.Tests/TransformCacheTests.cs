using System;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// The transforms' cached matrices (TransformNative: local, world and inverse) never go stale:
    /// after any sequence of sets, with every level read between them, each read gives the bits a
    /// hierarchy built afresh from the same local values gives.
    public class TransformCacheTests : EngineTest
    {
        static string Bits(float f) => BitConverter.SingleToInt32Bits(f).ToString("x8");
        static string Bits(Vector3 v) => Bits(v.x) + "," + Bits(v.y) + "," + Bits(v.z);
        static string Bits(Quaternion q) => Bits(q.x) + "," + Bits(q.y) + "," + Bits(q.z) + "," + Bits(q.w);
        static string Bits(Matrix4x4 m)
        {
            var s = "";
            for (int i = 0; i < 16; i++) s += Bits(m[i]) + " ";
            return s;
        }

        static readonly Vector3 Probe = new Vector3(3.7f, -12.25f, 0.6f);

        static string Read(Transform t) =>
            Bits(t.position) + " | " + Bits(t.rotation) + " | " + Bits(t.lossyScale) + " | " + Bits(t.localToWorldMatrix)
            + "| " + Bits(t.worldToLocalMatrix) + "| " + Bits(t.InverseTransformPoint(Probe)) + " | " + Bits(t.TransformPoint(Probe));

        static Transform[] Fresh(Transform[] chain, int length)
        {
            var copy = new Transform[length];
            for (int i = 0; i < length; i++)
            {
                var t = new GameObject("fresh" + i).transform;
                if (i > 0) t.SetParent(copy[i - 1], false);
                t.localPosition = chain[i].localPosition;
                t.localRotation = chain[i].localRotation;
                t.localScale = chain[i].localScale;
                copy[i] = t;
            }
            return copy;
        }

        static Vector3 Scale(System.Random r) => r.Next(3) switch
        {
            0 => Vector3.one,
            1 => Vector3.one * (float)(0.25 + r.NextDouble() * 3),
            _ => new Vector3((float)(0.5 + r.NextDouble()), (float)(0.5 + r.NextDouble()), 1f),
        };

        static Vector3 Point(System.Random r) => new Vector3((float)(r.NextDouble() * 200 - 100), (float)(r.NextDouble() * 200 - 100), (float)(r.NextDouble() * 4 - 2));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReadsAfterSetsMatchAFreshHierarchy(bool rotations)
        {
            var r = new System.Random(rotations ? 11 : 7);
            for (int round = 0; round < 40; round++)
            {
                int length = 1 + r.Next(5);
                var chain = new Transform[length];
                for (int i = 0; i < length; i++)
                {
                    var t = new GameObject("t" + i).transform;
                    if (i > 0) t.SetParent(chain[i - 1], false);
                    t.localPosition = Point(r);
                    t.localScale = Scale(r);
                    if (rotations && r.Next(3) == 0) t.localRotation = Quaternion.Euler(0f, 0f, r.Next(360));
                    chain[i] = t;
                }
                for (int step = 0; step < 30; step++)
                {
                    // Read every level (filling every cache), change one, compare every level.
                    foreach (var t in chain) Read(t);
                    var target = chain[r.Next(length)];
                    switch (r.Next(6))
                    {
                        case 0:
                        case 1: target.position = Point(r); break;
                        case 2: target.localPosition = Point(r); break;
                        case 3: target.position = target.position; break;
                        case 4: target.localScale = Scale(r); break;
                        default:
                            if (rotations) target.localRotation = Quaternion.Euler(0f, 0f, r.Next(360));
                            else target.position += new Vector3(1e-7f, 0f, 0f);
                            break;
                    }
                    var fresh = Fresh(chain, length);
                    for (int i = 0; i < length; i++)
                    {
                        Assert.Equal(Bits(fresh[i].localPosition), Bits(chain[i].localPosition));
                        Assert.Equal(Read(fresh[i]), Read(chain[i]));
                    }
                    UnityEngine.Object.DestroyImmediate(fresh[0].gameObject);
                }
                UnityEngine.Object.DestroyImmediate(chain[0].gameObject);
            }
        }

        /// A child's position set reads its ancestors' inverse scales: with one of them changing
        /// scale between sets, the next set uses the new scale.
        [Fact]
        public void ChildSetAfterAnAncestorScaleChangeUsesTheNewScale()
        {
            var root = new GameObject("root").transform;
            var mid = new GameObject("mid").transform;
            var leaf = new GameObject("leaf").transform;
            mid.SetParent(root, false);
            leaf.SetParent(mid, false);
            mid.localPosition = new Vector3(1f, 2f, 0f);
            leaf.position = new Vector3(5f, 5f, 0f);
            Assert.Equal(new Vector3(4f, 3f, 0f), leaf.localPosition);
            mid.localScale = new Vector3(2f, 4f, 1f);
            leaf.position = new Vector3(5f, 5f, 0f);
            Assert.Equal(new Vector3(2f, 0.75f, 0f), leaf.localPosition);
            mid.localScale = Vector3.one;
            leaf.position = new Vector3(5f, 5f, 0f);
            Assert.Equal(new Vector3(4f, 3f, 0f), leaf.localPosition);
            Assert.Equal(new Vector3(5f, 5f, 0f), leaf.position);
        }

        static Transform[] Chain()
        {
            // The probe's chain: four levels, the root scaled (2, 2, 1) and the third 0.86.
            var chain = new Transform[4];
            for (int i = 0; i < 4; i++)
            {
                var t = new GameObject("hc" + i).transform;
                if (i > 0) t.SetParent(chain[i - 1], false);
                t.localPosition = new Vector3(1.25f * (i + 1), -0.5f * i, i == 2 ? 0.75f : 0f);
                if (i == 0) t.localScale = new Vector3(2f, 2f, 1f);
                if (i == 2) t.localScale = new Vector3(0.86f, 0.86f, 0.86f);
                chain[i] = t;
            }
            return chain;
        }

        static string Flags(Transform[] chain, Action<Transform[]> act)
        {
            foreach (var t in chain) t.hasChanged = false;
            act(chain);
            var s = "";
            foreach (var t in chain) s += t.hasChanged ? "1" : "0";
            UnityEngine.Object.DestroyImmediate(chain[0].gameObject);
            return s;
        }

        /// hasChanged after one set, root first, as the original player reports it
        /// (tools/UnityProbe `haschanged`, HC lines): a set to the value held, == component by
        /// component, marks nothing; one an ulp away marks the transform and everything below.
        [Fact]
        public void HasChangedFollowsTheOriginal()
        {
            Assert.Equal("0000", Flags(Chain(), c => c[0].position = c[0].position));
            Assert.Equal("0000", Flags(Chain(), c => c[0].localPosition = c[0].localPosition));
            Assert.Equal("0000", Flags(Chain(), c => c[0].rotation = c[0].rotation));
            Assert.Equal("0000", Flags(Chain(), c => c[0].localRotation = c[0].localRotation));
            Assert.Equal("0000", Flags(Chain(), c => c[0].localScale = c[0].localScale));
            Assert.Equal("0000", Flags(Chain(), c => c[0].eulerAngles = c[0].eulerAngles));
            Assert.Equal("1111", Flags(Chain(), c => c[0].position = c[0].position + new Vector3(1e-7f, 0f, 0f)));
            Assert.Equal("1111", Flags(Chain(), c => c[0].position = c[0].position + new Vector3(1f, 0f, 0f)));
            Assert.Equal("0000", Flags(Chain(), c => c[2].position = c[2].position));
            Assert.Equal("0000", Flags(Chain(), c => c[2].localPosition = c[2].localPosition));
            Assert.Equal("0000", Flags(Chain(), c => c[2].position = c[2].position + new Vector3(0f, 1e-7f, 0f)));
            Assert.Equal("0011", Flags(Chain(), c => c[2].position = c[2].position + new Vector3(0f, 1f, 0f)));
            // Under the 0.86 scale the leaf's world position does not come back to its local one.
            Assert.Equal("0001", Flags(Chain(), c => c[3].position = c[3].position));
            Assert.Equal("0000", Flags(Chain(), c => c[0].SetPositionAndRotation(c[0].position, c[0].rotation)));
            Assert.Equal("0000", Flags(Chain(), c => c[2].SetParent(c[1], true)));
            Assert.Equal("1111", Flags(Chain(), c =>
            {
                var p = c[0].position;
                c[0].position = p + new Vector3(3f, 0f, 0f);
                c[0].position = p;
            }));
            Assert.Equal("1111", Flags(Chain(), c =>
            {
                c[0].position = new Vector3(float.NaN, 1f, 0f);
                foreach (var t in c) t.hasChanged = false;
                c[0].position = new Vector3(float.NaN, 1f, 0f);
            }));
        }

        /// A -0 set in place of a +0 is equal, so marks nothing, but is stored, and the world
        /// matrices take its bits (the original's root position reads back with z -0).
        [Fact]
        public void NegativeZeroForZeroIsStoredUnmarked()
        {
            var chain = Chain();
            foreach (var t in chain) { var _ = t.localToWorldMatrix; t.hasChanged = false; }
            var p = chain[0].position;
            chain[0].position = new Vector3(p.x, p.y, -0f);
            Assert.Equal("80000000", Bits(chain[0].position.z));
            Assert.Equal("80000000", Bits(chain[0].localToWorldMatrix.m23));
            foreach (var t in chain) Assert.False(t.hasChanged);
            var fresh = Fresh(chain, 4);
            for (int i = 0; i < 4; i++) Assert.Equal(Read(fresh[i]), Read(chain[i]));
        }
    }
}
