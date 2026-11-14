using System;
using System.Collections.Generic;

namespace UnityEngine.UI
{
    /// A queued layout rebuild of one layout root: sizes are computed bottom-up and applied
    /// top-down, horizontally first.
    public partial class LayoutRebuilder
    {
        RectTransform root;
        int hash;


        static LayoutRebuilder()
        {
            RectTransform.reapplyDrivenProperties += rect => MarkLayoutForRebuild(rect);
        }

        public LayoutRebuilder() { }

        LayoutRebuilder(RectTransform layoutRoot)
        {
            root = layoutRoot;
            hash = layoutRoot == null ? 0 : layoutRoot.GetHashCode();
        }

        public Transform transform => root;

        public bool IsDestroyed() => root == null;

        /// Lays out the rect's layout now.
        public static void ForceRebuildLayoutImmediate(RectTransform layoutRoot) => new LayoutRebuilder(layoutRoot).Rebuild(CanvasUpdate.Layout);

        public void Rebuild(CanvasUpdate executing)
        {
            if (executing != CanvasUpdate.Layout) return;
            // Only a rebuild the canvas update's layout pass runs itself leaves nested roots to
            // their own rebuilds (below); one asked for by anyone else, also from inside
            // such a rebuild, is whole.
            var outer = pruning;
            pruning = queuedRun && pending is { Count: > 0 } ? root : null;
            queuedRun = false;
            bool fresh = verify && early.Count > 0 && UnderEarly(root);
            if (fresh) freshRebuilds++;
            try
            {
                Calculate(root, e => e.CalculateLayoutInputHorizontal());
                Control(root, c => c.SetLayoutHorizontal());
                Calculate(root, e => e.CalculateLayoutInputVertical());
                Control(root, c => c.SetLayoutVertical());
            }
            finally
            {
                pruning = outer;
                if (fresh) freshRebuilds--;
            }
        }

        // ---- Nested layout roots in one layout pass ----
        //
        // A layout root is the topmost of a chain of layout groups; a group under a rect without
        // one (a scroll view's content under its viewport) is a root of its own. When both are
        // queued, the pass rebuilds the upper one first, and its calculation walks into the lower
        // root's whole subtree all the same (every Image is a layout element, so the walk goes
        // on), although its control pass stops at the rect without a controller and nothing
        // above reads what was calculated below it; the lower root's own rebuild, later in the
        // same pass, then calculates all of it again. A table screen has three such roots, one
        // inside the other (the screen, its scroll view and the rows' content), each over nearly
        // all of its rects: 64,000 for Consumables on a colony of 200 Duplicants, walked three
        // times in a pass, 266 ms where one walk takes 130 (Linux).
        //
        // So a rebuild run by the pass leaves out the subtree of a root still waiting in it, when
        // - the root's parent has no enabled layout controller (the upper control pass cannot
        //   reach the root, so it places nothing with what was left uncalculated), and
        // - every layout component at or below the rebuild's root is uGUI's or TextMeshPro's own,
        //   or a subclass that leaves their layout members alone: their calculations are empty or
        //   read their direct children only and keep nothing but their totals, which the waiting
        //   root's rebuild computes afresh before its own control pass reads them. One component
        //   with layout members of its own anywhere under the root (it might read further down)
        //   and the rebuild walks everything, as before.
        // ONI_VERIFY_UI_CACHES=1: a layout property read (LayoutUtility, a layout group's totals)
        // of a rect under a subtree left out, before its root's rebuild, is a line `layout roots
        // wrong`; a count of the subtrees left out now and then.

        /// The roots the running layout pass has still to rebuild; null outside a pass.
        static HashSet<RectTransform> pending;
        /// Set by the pass for the rebuild it is about to run.
        static bool queuedRun;
        /// The root of the rebuild that may leave nested roots out; null in any other rebuild.
        static RectTransform pruning;
        /// Per pass: the rebuilt roots found to hold only known layout components, or not.
        static readonly Dictionary<RectTransform, bool> knownRoots = new(ReferenceEqualityComparer.Instance);
        static readonly Stack<HashSet<RectTransform>> sparePending = new();

        static readonly bool verify = OniMoono.Engine.UI.UiCacheVerify.On;
        internal static bool Verifying => verify;
        /// Verify mode: the roots left out and not yet rebuilt, and the rebuilds running under one.
        static readonly HashSet<RectTransform> early = new(ReferenceEqualityComparer.Instance);
        static int freshRebuilds;
        static long leftOut, readEarly;

        /// The layout pass begins: the roots queued in it. Returns what the caller gives back to
        /// EndPass (a pass started inside another one's rebuild keeps the outer one's roots).
        internal static HashSet<RectTransform> BeginPass(ElementQueue queue)
        {
            var outer = pending;
            var roots = sparePending.Count > 0 ? sparePending.Pop() : new HashSet<RectTransform>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < queue.Count; i++)
                if (queue[i] is LayoutRebuilder { root: { } r }) roots.Add(r);
            pending = roots;
            knownRoots.Clear();
            return outer;
        }

        internal static void EndPass(HashSet<RectTransform> outer)
        {
            var roots = pending;
            pending = outer;
            knownRoots.Clear();
            if (roots == null) return;
            roots.Clear();
            sparePending.Push(roots);
            if (verify) early.Clear();
        }

        /// The pass's own rebuild of this queued root.
        internal void RebuildQueued()
        {
            pending?.Remove(root);
            if (verify) early.Remove(root);
            queuedRun = true;
            try { Rebuild(CanvasUpdate.Layout); }
            finally { queuedRun = false; }
        }

        /// Whether the calculation of the running rebuild leaves this rect's subtree to the
        /// rect's own rebuild, later in the pass.
        static bool LeftToItsRebuild(RectTransform rect)
        {
            if (ReferenceEquals(rect, pruning) || !pending.Contains(rect)) return false;
            if (rect.parent is not RectTransform parent || HasActive<ILayoutController>(parent)) return false;
            if (!OnlyKnownLayouts(pruning)) return false;
            leftOut++;
            if (verify)
            {
                early.Add(rect);
                if (leftOut % 20 == 1) OniMoono.Engine.EngineLog.Engine($"layout roots: {leftOut} nested roots left to their own rebuild, {readEarly} read early");
            }
            return true;
        }

        /// Whether every layout component at or below the root is a known one (KnownLayouts),
        /// asked once per root and pass; a root under one already found so is so too.
        static bool OnlyKnownLayouts(RectTransform root)
        {
            if (knownRoots.TryGetValue(root, out bool known)) return known;
            for (var t = root.parent; t != null && !known; t = t.parent)
                known = t is RectTransform above && knownRoots.TryGetValue(above, out bool k) && k;
            if (!known) known = KnownBelow(root.TransformNative);
            knownRoots[root] = known;
            return known;
        }

        static bool KnownBelow(OniMoono.Engine.TransformNative t)
        {
            if (!KnownOn(t.GameObject)) return false;
            foreach (var child in t.Children)
                if (!KnownBelow(child)) return false;
            return true;
        }

        /// Whether the object's layout components are all known ones: kept with its component
        /// types' shared layout (ComponentLayout.LayoutKinds: 1 known, 2 not).
        static bool KnownOn(OniMoono.Engine.GameObjectNative go)
        {
            var layout = OniMoono.Engine.Components.LayoutFor(go);
            if (layout.LayoutKinds != 0) return layout.LayoutKinds == 1;
            bool known = true;
            foreach (var c in go.Components)
                if (c != null && !KnownLayouts(c.GetType()))
                {
                    known = false;
                    break;
                }
            layout.LayoutKinds = (byte)(known ? 1 : 2);
            return known;
        }

        static readonly Dictionary<Type, bool> knownTypes = new();
        static readonly Type[] layoutInterfaces = { typeof(ILayoutElement), typeof(ILayoutController), typeof(ILayoutIgnorer) };
        static readonly HashSet<string> reported = new();

        /// A component type is known when it has no layout members (it implements none of
        /// ILayoutElement, ILayoutController, ILayoutIgnorer) or each one it has is declared in
        /// this package or in TextMeshPro: Image, the layout groups, ScrollRect and the like, and
        /// the game's subclasses of them that override none of those members (KImage, LocText).
        static bool KnownLayouts(Type type)
        {
            if (knownTypes.TryGetValue(type, out bool known)) return known;
            known = true;
            foreach (var layoutInterface in layoutInterfaces)
            {
                if (!known || !layoutInterface.IsAssignableFrom(type)) continue;
                foreach (var method in type.GetInterfaceMap(layoutInterface).TargetMethods)
                {
                    var owner = method.DeclaringType?.Assembly;
                    if (owner == typeof(LayoutRebuilder).Assembly || owner?.GetName().Name == "Unity.TextMeshPro") continue;
                    known = false;
                    break;
                }
            }
            knownTypes[type] = known;
            if (!known && reported.Add(type.FullName ?? type.Name))
                OniMoono.Engine.EngineLog.Engine($"layout roots: {type.FullName} has layout members of its own; layouts holding one are rebuilt whole");
            return known;
        }

        static bool UnderEarly(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t is RectTransform r && early.Contains(r)) return true;
            return false;
        }

        /// Verify mode: a layout property of the rect is being read.
        internal static void VerifyRead(RectTransform rect)
        {
            if (!verify || early.Count == 0 || freshRebuilds > 0 || rect == null || !UnderEarly(rect)) return;
            OniMoono.Engine.EngineLog.Engine($"layout roots wrong ({++readEarly}): a layout property of {rect.name} was read before its root's rebuild, which the pass had left its calculation to");
        }

        /// The rect's enabled components of a layout interface, in component order; give the
        /// list back to Scratch. The typed query reads the object's component layout
        /// (OniMoono.Engine.Components.All) straight into the list: a layout rebuild asks every
        /// rect below its root four times, 64,000 rects for a Consumables table of 200
        /// Duplicants, and the query by Type into a list of components, with a pass to drop the
        /// disabled ones, was most of each visit.
        static List<T> Active<T>(RectTransform rect) where T : class
        {
            var list = Scratch<T>.Take();
            rect.GetComponents(list);
            int kept = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c is Behaviour b && !b.isActiveAndEnabled) continue;
                if (kept != i) list[kept] = c;
                kept++;
            }
            if (kept < list.Count) list.RemoveRange(kept, list.Count - kept);
            return list;
        }

        /// Children before parents. A rect without layout elements or a layout group ends the walk:
        /// layouts below it are roots of their own.
        static void Calculate(RectTransform rect, Action<ILayoutElement> step)
        {
            if (rect == null) return;
            if (pruning != null && pending.Count > 0 && LeftToItsRebuild(rect)) return;
            var elements = Active<ILayoutElement>(rect);
            if (elements.Count > 0 || rect.GetComponent<ILayoutGroup>() as Object != null)
            {
                for (int i = 0; i < rect.childCount; i++) Calculate(rect.GetChild(i) as RectTransform, step);
                foreach (var e in elements) step(e);
            }
            Scratch<ILayoutElement>.Return(elements);
        }

        /// Parents before children; on one object the self controllers (fitters) before the
        /// others (groups), which lay out children within the size just set. A rect without
        /// controllers ends the walk.
        static void Control(RectTransform rect, Action<ILayoutController> step)
        {
            if (rect == null) return;
            var controllers = Active<ILayoutController>(rect);
            if (controllers.Count > 0)
            {
                foreach (var c in controllers)
                    if (c is ILayoutSelfController) step(c);
                foreach (var c in controllers)
                    if (c is not ILayoutSelfController) step(c);
                for (int i = 0; i < rect.childCount; i++) Control(rect.GetChild(i) as RectTransform, step);
            }
            Scratch<ILayoutController>.Return(controllers);
        }

        /// Queues the rect's layout root for the next canvas update: the topmost of the
        /// consecutive ancestors with an active layout group, or the rect itself when it has a
        /// layout controller.
        public static void MarkLayoutForRebuild(RectTransform rect)
        {
            if (rect == null || rect.gameObject == null) return;
            var layoutRoot = rect;
            for (var parent = rect.parent as RectTransform; parent != null; parent = parent.parent as RectTransform)
            {
                if (!HasActive<ILayoutGroup>(parent)) break;
                layoutRoot = parent;
            }
            if (layoutRoot == rect && !HasActive<ILayoutController>(rect)) return;
            MarkLayoutRootForRebuild(layoutRoot);
        }

        static bool HasActive<T>(RectTransform rect) where T : class
        {
            var list = Scratch<T>.Take();
            rect.GetComponents(list);
            bool found = false;
            foreach (var c in list)
                if (c is Behaviour b && b.isActiveAndEnabled)
                {
                    found = true;
                    break;
                }
            Scratch<T>.Return(list);
            return found;
        }

        static void MarkLayoutRootForRebuild(RectTransform controller)
        {
            if (controller == null) return;
            CanvasUpdateRegistry.TryRegisterCanvasElementForLayoutRebuild(new LayoutRebuilder(controller));
        }

        public void LayoutComplete() { }

        public void GraphicUpdateComplete() { }

        /// Rebuilders of the same root are one queue entry.
        public override bool Equals(object obj) => obj is LayoutRebuilder other && other.root == root;

        public override int GetHashCode() => hash;

        public override string ToString() => "(Layout Rebuilder for) " + root;
    }

    /// Reads layout sizes from the ILayoutElement components on a rect.
    public static class LayoutUtility
    {
        public static float GetMinSize(RectTransform rect, int axis) => axis == 0 ? GetMinWidth(rect) : GetMinHeight(rect);

        public static float GetPreferredSize(RectTransform rect, int axis) => axis == 0 ? GetPreferredWidth(rect) : GetPreferredHeight(rect);

        public static float GetFlexibleSize(RectTransform rect, int axis) => axis == 0 ? GetFlexibleWidth(rect) : GetFlexibleHeight(rect);

        public static float GetMinWidth(RectTransform rect) => GetLayoutProperty(rect, e => e.minWidth, 0f);

        /// Never below the minimum.
        public static float GetPreferredWidth(RectTransform rect) => Mathf.Max(GetLayoutProperty(rect, e => e.minWidth, 0f), GetLayoutProperty(rect, e => e.preferredWidth, 0f));

        public static float GetFlexibleWidth(RectTransform rect) => GetLayoutProperty(rect, e => e.flexibleWidth, 0f);

        public static float GetMinHeight(RectTransform rect) => GetLayoutProperty(rect, e => e.minHeight, 0f);

        public static float GetPreferredHeight(RectTransform rect) => Mathf.Max(GetLayoutProperty(rect, e => e.minHeight, 0f), GetLayoutProperty(rect, e => e.preferredHeight, 0f));

        public static float GetFlexibleHeight(RectTransform rect) => GetLayoutProperty(rect, e => e.flexibleHeight, 0f);

        public static float GetLayoutProperty(RectTransform rect, Func<ILayoutElement, float> property, float defaultValue)
            => GetLayoutProperty(rect, property, defaultValue, out _);

        /// The value of the enabled layout element with the highest priority that sets it (a
        /// negative value is unset); among equal priorities the largest. defaultValue when none
        /// sets it.
        public static float GetLayoutProperty(RectTransform rect, Func<ILayoutElement, float> property, float defaultValue, out ILayoutElement source)
        {
            source = null;
            if (rect == null) return 0f;
            if (LayoutRebuilder.Verifying) LayoutRebuilder.VerifyRead(rect);
            float result = defaultValue;
            int best = int.MinValue;
            var list = Scratch<ILayoutElement>.Take();
            rect.GetComponents(list);
            foreach (var e in list)
            {
                if (e is Behaviour b && !b.isActiveAndEnabled) continue;
                int priority = e.layoutPriority;
                if (priority < best) continue;
                float value = property(e);
                if (value < 0f) continue;
                if (priority > best)
                {
                    result = value;
                    best = priority;
                    source = e;
                }
                else if (value > result)
                {
                    result = value;
                    source = e;
                }
            }
            Scratch<ILayoutElement>.Return(list);
            return result;
        }
    }
}
