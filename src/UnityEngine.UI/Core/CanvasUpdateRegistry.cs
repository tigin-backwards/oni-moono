using System;
using System.Collections.Generic;

namespace UnityEngine.UI
{
    /// The per-frame UI rebuild: layout, then clipping, then graphics, run from
    /// Canvas.willRenderCanvases (once per frame before the canvases render, and on
    /// Canvas.ForceUpdateCanvases).
    public class CanvasUpdateRegistry
    {
        static CanvasUpdateRegistry singleton;

        readonly ElementQueue layoutQueue = new();
        readonly ElementQueue graphicQueue = new();
        bool inLayout;
        bool inGraphics;

        protected CanvasUpdateRegistry()
        {
            Canvas.willRenderCanvases += PerformUpdate;
        }

        public static CanvasUpdateRegistry instance => singleton ??= new CanvasUpdateRegistry();

        /// Hierarchy depth: the number of ancestors.
        static int Depth(ICanvasElement element)
        {
            var t = element.transform;
            int depth = 0;
            if (t == null) return 0;
            for (t = t.parent; t != null; t = t.parent) depth++;
            return depth;
        }

        static bool Invalid(ICanvasElement element)
        {
            if (element == null) return true;
            if (element is Object o && o == null) return true;
            return element.IsDestroyed();
        }

        void PerformUpdate()
        {
            layoutQueue.Compact(Invalid);
            graphicQueue.Compact(Invalid);

            // Layout: parents before children, each phase over the whole queue.
            inLayout = true;
            layoutQueue.Iterating = true;
            layoutQueue.SortBy(Depth);
            // The queued layout roots, for the rebuilds of the roots above them (LayoutRebuilder).
            var outerRoots = LayoutRebuilder.BeginPass(layoutQueue);
            try
            {
                for (int phase = (int)CanvasUpdate.Prelayout; phase <= (int)CanvasUpdate.PostLayout; phase++)
                    for (int i = 0; i < layoutQueue.Count; i++)
                    {
                        var e = layoutQueue[i];
                        if (e == null) continue;
                        try
                        {
                            if (phase == (int)CanvasUpdate.Layout && e is LayoutRebuilder rebuilder) rebuilder.RebuildQueued();
                            else e.Rebuild((CanvasUpdate)phase);
                        }
                        catch (Exception ex) { Debug.LogException(ex, e.transform); }
                    }
            }
            finally { LayoutRebuilder.EndPass(outerRoots); }
            for (int i = 0; i < layoutQueue.Count; i++)
                layoutQueue[i]?.LayoutComplete();
            layoutQueue.Iterating = false;
            layoutQueue.Clear();
            inLayout = false;

            ClipperRegistry.instance.Cull();

            inGraphics = true;
            graphicQueue.Iterating = true;
            for (int phase = (int)CanvasUpdate.PreRender; phase < (int)CanvasUpdate.MaxUpdateValue; phase++)
                for (int i = 0; i < graphicQueue.Count; i++)
                {
                    var e = graphicQueue[i];
                    if (e == null) continue;
                    try { e.Rebuild((CanvasUpdate)phase); }
                    catch (Exception ex) { Debug.LogException(ex, e.transform); }
                }
            for (int i = 0; i < graphicQueue.Count; i++)
                graphicQueue[i]?.GraphicUpdateComplete();
            graphicQueue.Iterating = false;
            graphicQueue.Clear();
            inGraphics = false;
        }

        public static void RegisterCanvasElementForLayoutRebuild(ICanvasElement element) => instance.QueueLayout(element, quiet: false);

        public static bool TryRegisterCanvasElementForLayoutRebuild(ICanvasElement element) => instance.QueueLayout(element, quiet: true);

        public static void RegisterCanvasElementForGraphicRebuild(ICanvasElement element) => instance.QueueGraphic(element, quiet: false);

        public static bool TryRegisterCanvasElementForGraphicRebuild(ICanvasElement element) => instance.QueueGraphic(element, quiet: true);

        /// Queues an element once; refused while the layout queue is being rebuilt.
        bool QueueLayout(ICanvasElement element, bool quiet)
        {
            if (element == null) return false;
            if (inLayout)
            {
                if (!quiet) Debug.LogError($"Trying to add {element} for layout rebuild while we are already inside a layout rebuild loop. This is not supported.");
                return false;
            }
            return layoutQueue.Add(element);
        }

        /// Queues an element once; refused while the graphic queue is being rebuilt (allowed during
        /// layout, for the same pass).
        bool QueueGraphic(ICanvasElement element, bool quiet)
        {
            if (element == null) return false;
            if (inGraphics)
            {
                if (!quiet) Debug.LogError($"Trying to add {element} for graphic rebuild while we are already inside a graphic rebuild loop. This is not supported.");
                return false;
            }
            return graphicQueue.Add(element);
        }

        /// Takes the element out of both queues and tells it at once that its layout and graphic
        /// updates are complete, the layout first (both calls are made whichever queues held it).
        /// The queue being rebuilt cannot be changed: that half logs an error and is skipped.
        public static void UnRegisterCanvasElementForRebuild(ICanvasElement element) => instance.Withdraw(element);

        /// The same as UnRegisterCanvasElementForRebuild (measured on the shipped package: both
        /// completion calls are made here too).
        public static void DisableCanvasElementForRebuild(ICanvasElement element) => instance.Withdraw(element);

        void Withdraw(ICanvasElement element)
        {
            if (element == null) return;
            if (inLayout) RefuseRemoval(element);
            else
            {
                layoutQueue.Remove(element);
                element.LayoutComplete();
            }
            if (inGraphics) RefuseRemoval(element);
            else
            {
                graphicQueue.Remove(element);
                element.GraphicUpdateComplete();
            }
        }

        static void RefuseRemoval(ICanvasElement element)
            => Debug.LogError($"Trying to remove {element} from rebuild list while we are already inside a rebuild loop. This is not supported.");

        public static bool IsRebuildingLayout() => instance.inLayout;

        public static bool IsRebuildingGraphics() => instance.inGraphics;
    }

    /// An insertion-ordered set of canvas elements (by Equals: layout rebuilders of one root are
    /// one entry) whose removals leave holes (null entries)
    /// while it may be iterated; holes are squeezed out on Compact and Clear.
    internal sealed class ElementQueue
    {
        readonly List<ICanvasElement> items = new();
        /// Each member's position in items.
        readonly Dictionary<ICanvasElement, int> positions = new();

        public int Count => items.Count;

        public ICanvasElement this[int index] => items[index];

        public bool Add(ICanvasElement element)
        {
            if (positions.ContainsKey(element)) return false;
            positions.Add(element, items.Count);
            items.Add(element);
            return true;
        }

        /// Set while the queue is being rebuilt: removals then leave holes.
        public bool Iterating;

        /// Outside a rebuild the last element takes the removed one's place (measured: the order
        /// in which the shipped package rebuilds graphics, which decides where a dynamic font
        /// places the glyphs they request).
        public void Remove(ICanvasElement element)
        {
            if (element == null || !positions.Remove(element, out int i)) return;
            int last = items.Count - 1;
            if (Iterating || i == last)
            {
                items[i] = null;
                if (!Iterating) items.RemoveAt(last);
                return;
            }
            var moved = items[last];
            items[i] = moved;
            if (moved != null) positions[moved] = i;
            items.RemoveAt(last);
        }

        public void Compact(Func<ICanvasElement, bool> drop)
        {
            int kept = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var e = items[i];
                if (e == null) continue;
                if (drop(e))
                {
                    positions.Remove(e);
                    continue;
                }
                positions[e] = kept;
                items[kept++] = e;
            }
            items.RemoveRange(kept, items.Count - kept);
        }

        /// Stable: elements with equal keys keep their queue order. Call after Compact.
        public void SortBy(Func<ICanvasElement, int> key)
        {
            var keyed = new (int Key, int Order, ICanvasElement Element)[items.Count];
            for (int i = 0; i < items.Count; i++) keyed[i] = (key(items[i]), i, items[i]);
            Array.Sort(keyed, (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Order.CompareTo(b.Order));
            for (int i = 0; i < keyed.Length; i++)
            {
                items[i] = keyed[i].Element;
                positions[items[i]] = i;
            }
        }

        public void Clear()
        {
            items.Clear();
            positions.Clear();
        }
    }
}
