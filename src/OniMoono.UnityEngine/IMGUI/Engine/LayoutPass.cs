using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// The GUILayout entries of one container (a behaviour's OnGUI or a window's function).
    ///
    /// During a Layout event the GUILayout calls record entries: a leaf per control, Space,
    /// FlexibleSpace and GetRect, a group per Begin call, and areas in a list of their own.
    /// Once the call has returned they are laid out. In every later event each call takes the
    /// next recorded entry of the current group, by position only, whatever it was recorded
    /// for; a Begin call needs a group there ("GUILayout, measured", part 1). During an event
    /// whose type is Used nothing is checked: every call gets the dummy rect and no entry is
    /// taken.
    internal sealed class LayoutPass
    {
        LayoutGroup root;
        LayoutGroup areas = new(true, GUIStyle.none, null);
        /// The group the calls go to (Unity's "topLevel"), and the open groups.
        LayoutGroup current;
        readonly Stack<LayoutGroup> stack = new();
        bool recording;

        /// The rect GUILayout calls return during Layout, when nothing is computed yet.
        public static Rect Dummy => LayoutEntry.Dummy;

        public bool Recording => recording;

        public LayoutGroup Root => root;

        /// Nothing is checked or taken (an event of type Used).
        public static bool Unchecked => Event.current != null && Event.current.type == EventType.Used;

        /// Recording or unchecked: calls get the dummy rect.
        public bool Dummies => recording || Unchecked;

        /// Starts recording: the root is an empty vertical group with `style`.
        public void BeginRecord(GUIStyle style)
        {
            root = new LayoutGroup(true, style, null);
            // The list of areas is only its entries and cursor: an empty one serves again (most
            // OnGUI calls begin no area).
            if (areas.Entries.Count > 0) areas = new LayoutGroup(true, GUIStyle.none, null);
            areas.Cursor = 0;
            Reset(root);
            recording = true;
        }

        /// Starts replaying the last recording.
        public void BeginReplay()
        {
            root ??= new LayoutGroup(true, GUIStyle.none, null);
            Reset(root);
            areas.Cursor = 0;
            recording = false;
        }

        void Reset(LayoutGroup top)
        {
            stack.Clear();
            stack.Push(top);
            current = top;
            top.Cursor = 0;
        }

        /// An OnGUI call's layout: the root from (0, 0) at its entries' largest sizes, within
        /// the screen (labels: as wide as the widest; a scroll view between MinHeight 50 and
        /// MaxHeight 70 gets 70), each area at its rect's size.
        public void Compute()
        {
            if (root == null) return;
            root.CalcWidth();
            root.SetHorizontal(0f, Within(root.MaxWidth, Screen.width));
            root.CalcHeight();
            root.SetVertical(0f, Within(root.MaxHeight, Screen.height));
            LayOutAreas();
        }

        static float Within(float size, int screen) => screen > 0 ? Mathf.Min(size, screen) : size;

        /// A GUI.Window's layout: the content at the window's size.
        public void Compute(Vector2 size)
        {
            if (root == null) return;
            root.LayOut(new Rect(0f, 0f, size.x, size.y));
            LayOutAreas();
        }

        /// GUILayout.Window's size: per axis the larger of the content's minimum and the size
        /// passed in after the options (Width/Height stand for it, Min and Max clamp it);
        /// the content is laid out at that size.
        public Vector2 ComputeFitted(Vector2 requested, GUILayoutOption[] options)
        {
            if (root == null) return requested;
            root.CalcWidth();
            float width = Mathf.Max(root.MinWidth, Given(requested.x, options, true));
            root.SetHorizontal(0f, width);
            root.CalcHeight();
            float height = Mathf.Max(root.MinHeight, Given(requested.y, options, false));
            root.SetVertical(0f, height);
            LayOutAreas();
            return new Vector2(width, height);
        }

        static float Given(float size, GUILayoutOption[] options, bool horizontal)
        {
            if (options == null) return size;
            float min = float.NegativeInfinity, max = float.PositiveInfinity;
            foreach (var o in options)
            {
                if (o == null || o.Horizontal != horizontal) continue;
                switch (o.Rule)
                {
                    case LayoutRule.Fixed: size = o.Value; break;
                    case LayoutRule.Min: min = o.Value; break;
                    case LayoutRule.Max: max = o.Value; break;
                }
            }
            if (size > max) size = max;
            if (size < min) size = min;
            return size;
        }

        void LayOutAreas()
        {
            foreach (var e in areas.Entries)
                if (e is LayoutGroup area) area.LayOut(new Rect(0f, 0f, area.AreaSize.x, area.AreaSize.y));
        }

        /// GUILayoutUtility.GetLastRect: the entry the last call of the current group took.
        public Rect LastRect => Dummies ? Dummy : current.GetLast();

        // ----- Entries -----

        /// A leaf call: records `entry` during Layout, else takes the next entry's rect.
        public Rect Leaf(LayoutEntry entry)
        {
            if (recording)
            {
                current.Entries.Add(entry);
                return Dummy;
            }
            if (Unchecked) return Dummy;
            return current.GetNext().Rect;
        }

        /// The orientation of the group that entries currently go to.
        public bool InVertical => current == null || current.Vertical;

        /// BeginHorizontal / BeginVertical: a new group during Layout; later, the recorded
        /// group in its place, of whatever kind (null when unchecked).
        public LayoutGroup BeginGroup(bool vertical, GUIStyle style, GUILayoutOption[] options)
        {
            if (recording) return Open(new LayoutGroup(vertical, style, options));
            if (Unchecked) return null;
            return Open(TakeGroup(current));
        }

        public ScrollGroup BeginScroll(bool alwaysShowHorizontal, bool alwaysShowVertical, GUIStyle horizontalScrollbar, GUIStyle verticalScrollbar, GUIStyle background, GUILayoutOption[] options)
        {
            if (recording)
            {
                var g = new ScrollGroup(alwaysShowHorizontal, alwaysShowVertical, horizontalScrollbar, verticalScrollbar, background, options);
                Open(g);
                return g;
            }
            return null;
        }

        /// BeginScrollView after Layout: the recorded group in its place (null when unchecked).
        public LayoutGroup TakeScroll() => Unchecked ? null : Open(TakeGroup(current));

        LayoutGroup Open(LayoutGroup g)
        {
            if (recording) current.Entries.Add(g);
            g.Cursor = 0;
            stack.Push(g);
            current = g;
            return g;
        }

        /// The next entry of `group`, which must be a group: a leaf there ends the call with
        /// ExitGUIException (not logged), and the group is not opened.
        static LayoutGroup TakeGroup(LayoutGroup group)
        {
            var e = group.GetNext();
            if (e is LayoutGroup g) return g;
            throw new ExitGUIException("GUILayout: Mismatched LayoutGroup." + Event.current.type);
        }

        /// EndHorizontal / EndVertical / EndScrollView: closes the current group. Closing the
        /// last one leaves an empty group current.
        public void EndGroup()
        {
            if (Unchecked) return;
            stack.Pop();
            current = stack.Count > 0 ? stack.Peek() : new LayoutGroup(true, GUIStyle.none, null);
        }

        /// BeginArea's layout part: an area of its own during Layout; later the next recorded
        /// area. Areas are not entries of the group they are begun in.
        public void BeginArea(Rect rect, GUIStyle style)
        {
            if (recording)
            {
                var g = new LayoutGroup(true, style, null) { IsArea = true, AreaSize = rect.size };
                areas.Entries.Add(g);
                g.Cursor = 0;
                stack.Push(g);
                current = g;
                return;
            }
            if (Unchecked) return;
            var area = TakeGroup(areas);
            area.Cursor = 0;
            stack.Push(area);
            current = area;
        }

        /// EndArea's layout part: closes the current group; with none left the group under it
        /// cannot be read (InvalidOperationException "Stack empty.").
        public void EndArea()
        {
            if (Unchecked) return;
            stack.Pop();
            current = stack.Peek();
        }
    }
}
