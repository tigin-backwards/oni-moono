using System.Collections.Generic;

namespace UnityEngine.UI
{
    /// The clippers (RectMask2D) that compute their clipping each canvas update.
    public class ClipperRegistry
    {
        static ClipperRegistry singleton;

        readonly List<IClipper> clippers = new();
        readonly HashSet<IClipper> members = new(ReferenceEqualityComparer.Instance);

        protected ClipperRegistry() { }

        public static ClipperRegistry instance => singleton ??= new ClipperRegistry();

        /// Every registered clipper clips, in registration order.
        public void Cull()
        {
            for (int i = 0; i < clippers.Count; i++)
            {
                var c = clippers[i];
                if (c is Object o && o == null) continue;
                c.PerformClipping();
            }
        }

        public static void Register(IClipper c)
        {
            if (c == null) return;
            var r = instance;
            if (r.members.Add(c)) r.clippers.Add(c);
        }

        public static void Unregister(IClipper c)
        {
            var r = instance;
            if (c != null && r.members.Remove(c)) r.clippers.Remove(c);
        }

        public static void Disable(IClipper c) => Unregister(c);
    }

    /// The graphics of each canvas, and those of them that are raycast targets.
    public class GraphicRegistry
    {
        static GraphicRegistry singleton;

        readonly Dictionary<Canvas, GraphicSet> graphics = new();
        readonly Dictionary<Canvas, GraphicSet> raycastGraphics = new();
        static readonly List<Graphic> none = new();

        protected GraphicRegistry() { }

        public static GraphicRegistry instance => singleton ??= new GraphicRegistry();

        static void Add(Dictionary<Canvas, GraphicSet> map, Canvas c, Graphic graphic)
        {
            if (c == null || graphic == null) return;
            if (!map.TryGetValue(c, out var set)) map[c] = set = new GraphicSet();
            set.Add(graphic);
        }

        static void Remove(Dictionary<Canvas, GraphicSet> map, Canvas c, Graphic graphic)
        {
            if (c == null || graphic == null || !map.TryGetValue(c, out var set)) return;
            set.Remove(graphic);
            if (set.Count == 0) map.Remove(c);
        }

        public static void RegisterGraphicForCanvas(Canvas c, Graphic graphic)
        {
            Add(instance.graphics, c, graphic);
            if (graphic != null && graphic.raycastTarget) RegisterRaycastGraphicForCanvas(c, graphic);
        }

        public static void RegisterRaycastGraphicForCanvas(Canvas c, Graphic graphic)
        {
            if (graphic == null || !graphic.raycastTarget) return;
            Add(instance.raycastGraphics, c, graphic);
        }

        public static void UnregisterGraphicForCanvas(Canvas c, Graphic graphic)
        {
            Remove(instance.graphics, c, graphic);
            UnregisterRaycastGraphicForCanvas(c, graphic);
        }

        public static void UnregisterRaycastGraphicForCanvas(Canvas c, Graphic graphic) => Remove(instance.raycastGraphics, c, graphic);

        public static void DisableGraphicForCanvas(Canvas c, Graphic graphic) => UnregisterGraphicForCanvas(c, graphic);

        public static void DisableRaycastGraphicForCanvas(Canvas c, Graphic graphic) => UnregisterRaycastGraphicForCanvas(c, graphic);

        public static IList<Graphic> GetGraphicsForCanvas(Canvas canvas)
            => canvas != null && instance.graphics.TryGetValue(canvas, out var set) ? set.Items : none;

        public static IList<Graphic> GetRaycastableGraphicsForCanvas(Canvas canvas)
            => canvas != null && instance.raycastGraphics.TryGetValue(canvas, out var set) ? set.Items : none;

        /// The canvas's raycast targets and a number for them as listed: each change of any
        /// canvas's list takes a new one, so while it stands the list holds the same graphics in
        /// the same order (GraphicRaycaster keeps its pass while it does).
        internal static IList<Graphic> GetRaycastableGraphicsForCanvas(Canvas canvas, out long version)
        {
            if (canvas != null && instance.raycastGraphics.TryGetValue(canvas, out var set))
            {
                version = set.Version;
                return set.Items;
            }
            version = 0;
            return none;
        }

        /// Graphics in registration order, each once; removal swaps in the last one.
        sealed class GraphicSet
        {
            public readonly List<Graphic> Items = new();
            readonly Dictionary<Graphic, int> positions = new(ReferenceEqualityComparer.Instance);
            static long versions;
            public long Version = ++versions;

            public int Count => Items.Count;

            public void Add(Graphic g)
            {
                if (positions.ContainsKey(g)) return;
                positions[g] = Items.Count;
                Items.Add(g);
                Version = ++versions;
            }

            public void Remove(Graphic g)
            {
                if (!positions.Remove(g, out int i)) return;
                int last = Items.Count - 1;
                if (i != last)
                {
                    Items[i] = Items[last];
                    positions[Items[i]] = i;
                }
                Items.RemoveAt(last);
                Version = ++versions;
            }
        }
    }
}
