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
    }
}
