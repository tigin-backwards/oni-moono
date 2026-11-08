using System;
using UnityEngine;

namespace OniMoono.Engine.UI
{
    /// What a GraphicRaycaster's pass over its canvas's graphics reads before their raycast
    /// filters, as numbers that change with it (UnityEngine.UI.GraphicRaycaster keeps the graphics
    /// that pass while every number stands; Stamp has them for one canvas). Keyed per canvas: the
    /// game keeps all its UI under one object, where pooled popups, notifications and name bars
    /// change something every frame in a large colony, while most canvases below stay as they are.
    ///
    /// Version is the global part: a graphic's raycastTarget or raycastPadding, a CanvasRenderer's
    /// cull, an element numbered by a sort root while another's numbering still gave it a depth,
    /// a canvas no longer a sort root, and the canvas walk's hidden marks (the raycast's
    /// closed-screen skip, CanvasSystem.Unreachable).
    internal static class RaycastInputs
    {
        /// Bumped by every change of the global inputs above.
        public static long Version;

        public static void Changed() => Version++;

        /// ONI_RAYCAST_CACHE=0 turns the reuse off (for measuring).
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("ONI_RAYCAST_CACHE") != "0";

        /// ONI_VERIFY_RAYCAST_CACHE=1: each reuse also makes the pass afresh and compares.
        public static readonly bool Verifying = Environment.GetEnvironmentVariable("ONI_VERIFY_RAYCAST_CACHE") == "1";

        /// The numbers a raycast over a canvas reads, as they stand (Take): for the canvas's object,
        /// its hierarchy version, its count of RectTransform moves and resizes at or below it and
        /// its world matrix's number; for each object above it (the closed-screen skip looks up
        /// through them), its own changes (SelfVersion) and the walk's hidden mark, with its
        /// hierarchy version while it is marked (the mark stands only while that does); and the
        /// global Version.
        public sealed class Stamp
        {
            object[] objects = new object[8];
            long[] numbers = new long[24];
            int count;

            public bool IsSet => count > 0;

            public void Take(Component canvas)
            {
                count = 0;
                if (canvas == null || canvas.gameObject.Native is not GameObjectNative go || go.Transform is not { } t) return;
                Add(go, go.SubtreeVersion, t.SubtreeMoves, t.WorldStamp);
                AddNumbering(go);
                for (var p = t.Parent; p != null; p = p.Parent)
                {
                    var o = p.GameObject;
                    Add(o, o.SelfVersion, o.CanvasHiddenAt, o.CanvasHiddenAt >= 0 ? o.SubtreeVersion : -1);
                    AddNumbering(o);
                }
                Add(null, Version, 0, 0);
            }

            // A canvas here: its numbering, which says whether the elements it numbered have a
            // depth (CanvasRendererNative.HasDepth).
            void AddNumbering(GameObjectNative o)
            {
                if (CanvasSystem.AnyCanvasOn(o) is { } canvas) Add(canvas, canvas.DepthEpoch, 0, 0);
            }

            void Add(object o, long a, long b, long c)
            {
                if (count == objects.Length)
                {
                    Array.Resize(ref objects, count * 2);
                    Array.Resize(ref numbers, count * 6);
                }
                objects[count] = o;
                numbers[count * 3] = a;
                numbers[count * 3 + 1] = b;
                numbers[count * 3 + 2] = c;
                count++;
            }

            public bool Same(Stamp o)
            {
                if (count != o.count) return false;
                for (int i = 0; i < count; i++)
                    if (!ReferenceEquals(objects[i], o.objects[i])) return false;
                for (int i = 0; i < count * 3; i++)
                    if (numbers[i] != o.numbers[i]) return false;
                return true;
            }

            /// What differs from o (for the verify report), or null.
            public string Differs(Stamp o)
            {
                if (count != o.count) return "objects above";
                for (int i = 0; i < count; i++)
                {
                    if (!ReferenceEquals(objects[i], o.objects[i])) return "objects above";
                    for (int k = 0; k < 3; k++)
                        if (numbers[i * 3 + k] != o.numbers[i * 3 + k])
                            return i == count - 1 ? "flags" : objects[i] is CanvasNative ? "numbering" : i == 0 ? (k == 0 ? "hierarchy" : k == 1 ? "rect moves" : "canvas moved") : (k == 0 ? "object above changed" : "hidden marks above");
                }
                return null;
            }
        }

        static long made, reused, verified, wrong;

        static readonly System.Collections.Generic.Dictionary<string, long> reasons = new();

        /// A pass made afresh; `reason` (verify runs) names what changed since the kept one.
        public static void Made(string reason)
        {
            made++;
            if (!Verifying) return;
            reasons.TryGetValue(reason, out long n);
            reasons[reason] = n + 1;
            if ((made + reused) % 2000 == 0) Report();
        }

        public static void Reused()
        {
            reused++;
            if (Verifying && (made + reused) % 2000 == 0) Report();
        }

        /// ONI_VERIFY_RAYCAST_CACHE: one reuse compared with a fresh pass; `difference` null when
        /// they agree.
        public static void Verified(string canvas, string difference)
        {
            verified++;
            if (difference != null) EngineLog.Engine($"raycast cache wrong ({++wrong}): {canvas}: {difference}");
        }

        public static void Report()
            => EngineLog.Engine($"raycast cache: {made} passes made ({string.Join(", ", System.Linq.Enumerable.Select(reasons, r => $"{r.Key} {r.Value}"))}), {reused} reused, {verified} verified, {wrong} wrong");
    }
}
