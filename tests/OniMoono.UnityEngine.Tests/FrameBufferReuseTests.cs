using System;
using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Engine.Rendering;
using UnityEngine;
using Xunit;

namespace OniMoono.Tests
{
    /// Buffers the engine's own frame keeps instead of allocating each frame: pass snapshots,
    /// temporary render texture lookups and render target arrays. Each gives what a new one gave.
    public class FrameBufferReuseTests : EngineTest
    {
        [Fact]
        public void ASnapshotIsTheListAsItWasAndANestedOneHasItsOwnArray()
        {
            var snapshots = new ListSnapshot<string>();
            var list = new List<string> { "a", "b", "c" };
            string[] seen;
            using (var outer = snapshots.Take(list))
            {
                list.Add("d");
                Assert.Equal(["a", "b", "c"], outer.Items.ToArray());
                using var inner = snapshots.Take(list);
                Assert.Equal(["a", "b", "c", "d"], inner.Items.ToArray());
                // Changing the list does not change either copy.
                list.Clear();
                Assert.Equal(3, outer.Items.Length);
                Assert.Equal(4, inner.Items.Length);
                seen = outer.Items.ToArray();
            }
            Assert.Equal(["a", "b", "c"], seen);
            using var again = snapshots.Take(new List<string> { "x" });
            Assert.Equal(["x"], again.Items.ToArray());
        }

        [Fact]
        public void ASnapshotHoldsNoObjectsAfterItsPass()
        {
            var snapshots = new ListSnapshot<object>();
            var big = new List<object> { new(), new(), new() };
            using (snapshots.Take(big)) { }
            // The kept array is the same one, cleared: a later, shorter copy leaves no old entries.
            using var small = snapshots.Take(new List<object> { "only" });
            Assert.Equal(1, small.Items.Length);
            var field = typeof(ListSnapshot<object>).GetField("array", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var array = (object[])field.GetValue(snapshots);
            Assert.Equal("only", array[0]);
            Assert.Null(array[1]);
            Assert.Null(array[2]);
        }

        /// GetTemporary looks its pool up with a description it keeps: a released texture of the
        /// same description is given again, another description makes a texture of its own with
        /// that description. (Linear read/write: the default reads the project's colour space,
        /// which needs the game's data.)
        [Fact]
        public void TemporariesAreFoundAndMadeAsBefore()
        {
            var a = RenderTexture.GetTemporary(24, 12, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture.ReleaseTemporary(a);
            var again = RenderTexture.GetTemporary(24, 12, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Assert.Same(a, again);
            var other = RenderTexture.GetTemporary(24, 12, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Assert.NotSame(again, other);
            Assert.Equal(24, other.width);
            Assert.Equal(12, other.height);
            Assert.Equal(16, other.depth);
            Assert.False(other.sRGB);
            Assert.Equal(24, again.width);
            Assert.Equal(0, again.depth);
            Assert.NotSame(again.Native, other.Native);
            RenderTexture.ReleaseTemporary(again);
            RenderTexture.ReleaseTemporary(other);
        }

        [Fact]
        public void ATexturesTargetArrayIsItsOwnAndHoldsItAlone()
        {
            // A native alone: a constructed texture reads the project's colour space (game data).
            var native = new RenderTextureNative { Width = 8, Height = 8 };
            var a = RenderTargetBinding.Of(native);
            var b = RenderTargetBinding.Of(native);
            Assert.Same(a.Colors, b.Colors);
            Assert.Equal([native], a.Colors);
            Assert.Same(native, a.Depth);
            Assert.Null(RenderTargetBinding.Of(null).Colors);
            Assert.NotSame(a.Colors, RenderTargetBinding.Of(new RenderTextureNative()).Colors);
        }

        /// A delegate list calls what a multicast delegate given the same adds and removes calls,
        /// in the same order: removal takes the last matching run, a missing one changes nothing.
        [Fact]
        public void ADelegateListCallsAsAMulticastDelegateWould()
        {
            var calls = new List<string>();
            Action a = () => calls.Add("a"), b = () => calls.Add("b"), c = () => calls.Add("c");
            Action ab = a + b;
            var steps = new (bool Add, Action Value)[]
            {
                (true, a), (true, b), (true, a), (true, ab), (true, c), (false, a), (false, ab), (false, c), (false, c),
                (true, null), (false, null), (true, ab), (false, b + a), (false, b), (true, c), (false, a),
            };
            var list = new DelegateList<Action>();
            Action multicast = null;
            foreach (var (add, value) in steps)
            {
                if (add)
                {
                    list.Add(value);
                    multicast += value;
                }
                else
                {
                    list.Remove(value);
                    multicast -= value;
                }
                calls.Clear();
                multicast?.Invoke();
                var expected = calls.ToArray();
                calls.Clear();
                using (var s = list.Take())
                    foreach (var d in s.Items) d();
                Assert.Equal(expected, calls);
            }
        }

        /// Canvas.willRenderCanvases calls the handlers there were when the call began: one added
        /// by a handler waits for the next call, one removed by a handler is still called.
        [Fact]
        public void WillRenderCanvasesCallsTheHandlersAsTheyWere()
        {
            var calls = new List<string>();
            Canvas.WillRenderCanvases late = () => calls.Add("late");
            Canvas.WillRenderCanvases second = () => calls.Add("second");
            Canvas.WillRenderCanvases first = null;
            first = () =>
            {
                calls.Add("first");
                Canvas.willRenderCanvases -= second;
                Canvas.willRenderCanvases += late;
            };
            Canvas.willRenderCanvases += first;
            Canvas.willRenderCanvases += second;
            try
            {
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["first", "second"], calls);
                calls.Clear();
                Canvas.willRenderCanvases -= first;
                Canvas.SendWillRenderCanvases();
                Assert.Equal(["late"], calls);
            }
            finally
            {
                Canvas.willRenderCanvases -= first;
                Canvas.willRenderCanvases -= second;
                Canvas.willRenderCanvases -= late;
            }
        }
    }
}
