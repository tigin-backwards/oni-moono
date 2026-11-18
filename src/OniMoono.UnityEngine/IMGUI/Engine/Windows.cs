using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// A window a behaviour's OnGUI declared with GUI.Window or GUILayout.Window. It keeps the
    /// state in effect at the call (skin, colours, enabled, matrix), its own control IDs and
    /// GUILayout entries, and its rect, which DragWindow moves.
    internal sealed class WindowRecord
    {
        public readonly int Id;
        public readonly GUIParticipant Owner;
        public readonly ControlScope Scope;
        public readonly LayoutPass Layout = new();
        public Rect Rect;
        /// DragWindow moved it since the last GUI.Window call returned its rect.
        public bool Moved;
        /// Its behaviour called GUI.Window for it in the current declaration pass.
        public bool Touched;
        public GUI.WindowFunction Function;
        public GUIContent Title = new();
        public GUIStyle Style = GUIStyle.none;
        /// GUILayout.Window: sized from its content.
        public bool Fitted;
        /// GUI.ModalWindow: in front of every window, and the only one taking input.
        public bool Modal;
        public GUILayoutOption[] Options;
        /// The rect the last GUILayout.Window call passed in.
        public Rect Requested;
        /// The size the last layout of a GUILayout.Window gave it.
        public Vector2 FitSize;
        public bool HasFit;
        public GUISkin Skin;
        public Color Color, BackgroundColor, ContentColor;
        public bool Enabled;
        public Matrix4x4 Matrix = Matrix4x4.identity;

        public WindowRecord(int id, GUIParticipant owner)
        {
            Id = id;
            Owner = owner;
            Scope = new ControlScope(this);
        }

        /// Whether a point in screen GUI space is inside the window.
        public bool Contains(Vector2 screen) => Rect.Contains(Matrix.isIdentity ? screen : (Vector2)Matrix.inverse.MultiplyPoint3x4(screen));
    }

    /// GUI.Window and the window functions, and the stacking of the windows of every behaviour
    /// ("GUILayout, measured", part 4).
    ///
    /// The windows of all behaviours form one stack. A window declared in a Layout pass that
    /// it was not declared in the pass before goes in front of all the others; several new
    /// together go in front in the order they were declared, the first declared frontmost. A
    /// window not declared during a Layout pass is gone. A press on a window brings it to the
    /// front at once; BringWindowToFront and BringWindowToBack take effect in the window pass
    /// of the next Layout (the same one, when called during Layout); FocusWindow and
    /// UnfocusWindow do not change the order.
    internal static class Windows
    {
        /// The modal window last declared; only one is shown at a time. GUIManager.Modal
        /// checks that it is still alive.
        public static WindowRecord ActiveModal;
        /// The focused window: key events go to it when no control has keyboard focus.
        public static WindowRecord Focused;

        /// Every window, the front one first.
        static readonly List<WindowRecord> stack = new();
        /// Windows first declared in the current Layout pass, in the order of declaration.
        static readonly List<WindowRecord> added = new();
        static readonly List<(GUIParticipant Caller, int Id, bool Front)> requests = new();
        static readonly List<WindowRecord> snapshot = new();

        public static int Count => stack.Count;

        public static WindowRecord At(int index) => stack[index];

        public static bool Shown(WindowRecord w) => stack.Contains(w);

        /// The windows front to back, as they are now (window functions may change the stack).
        public static List<WindowRecord> Snapshot()
        {
            snapshot.Clear();
            snapshot.AddRange(stack);
            return snapshot;
        }

        static WindowRecord Find(GUIParticipant p, int id)
        {
            foreach (var w in stack)
                if (w.Owner == p && w.Id == id) return w;
            foreach (var w in added)
                if (w.Owner == p && w.Id == id) return w;
            return null;
        }

        /// The calling behaviour's window of that ID, else any window of that ID.
        static WindowRecord FindForCall(GUIParticipant p, int id)
        {
            var w = p != null ? Find(p, id) : null;
            if (w != null) return w;
            foreach (var other in stack)
                if (other.Id == id) return other;
            return null;
        }

        /// Before the behaviours' Layout calls: their windows are untouched, nothing is new.
        public static void BeginDeclarations()
        {
            foreach (var w in stack)
                if (w.Owner.UsesLayout) w.Touched = false;
            added.Clear();
        }

        /// After the behaviours' Layout calls: the windows not declared are gone, the new ones
        /// go in front, then the BringWindowToFront/Back calls since the last pass apply.
        public static void EndDeclarations()
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                var w = stack[i];
                if (w.Owner.UsesLayout && !w.Touched) Remove(i);
            }
            stack.InsertRange(0, added);
            added.Clear();
            foreach (var (caller, id, front) in requests)
            {
                var w = FindForCall(caller, id);
                if (w == null) continue;
                stack.Remove(w);
                if (front) stack.Insert(0, w);
                else stack.Add(w);
            }
            requests.Clear();
        }

        /// Behaviours without GUILayout declare their windows in Repaint instead.
        public static void BeginRepaintDeclarations()
        {
            foreach (var w in stack)
                if (!w.Owner.UsesLayout) w.Touched = false;
        }

        public static void EndRepaintDeclarations()
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                var w = stack[i];
                if (!w.Owner.UsesLayout && !w.Touched) Remove(i);
            }
        }

        static void Remove(int index)
        {
            var w = stack[index];
            stack.RemoveAt(index);
            Forget(w);
        }

        static void Forget(WindowRecord w)
        {
            w.Scope.Release();
            if (ActiveModal == w) ActiveModal = null;
            if (Focused == w) Focused = null;
        }

        /// A behaviour's windows go with it.
        public static void Release(GUIParticipant p)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
                if (stack[i].Owner == p) Remove(i);
            for (int i = added.Count - 1; i >= 0; i--)
            {
                if (added[i].Owner != p) continue;
                Forget(added[i]);
                added.RemoveAt(i);
            }
        }

        public static void Reset()
        {
            stack.Clear();
            added.Clear();
            requests.Clear();
            ActiveModal = null;
            Focused = null;
        }

        /// GUI.BringWindowToFront / BringWindowToBack: queued for the next window pass of a
        /// Layout. Outside OnGUI they do nothing.
        public static void Request(int id, bool front)
        {
            if (!GUIStateData.InGUI) return;
            requests.Add((GUIStateData.Participant, id, front));
        }

        /// A press on a window brings it to the front at once.
        public static void BringToFrontNow(WindowRecord w)
        {
            if (stack.Remove(w)) stack.Insert(0, w);
        }

        /// GUI.FocusWindow: key events go to that window; the order does not change.
        public static void Focus(int id)
        {
            var w = FindForCall(GUIStateData.Participant, id);
            if (w != null) Focused = w;
        }

        /// GUI.Window / GUILayout.Window: records the call and returns the window's rect, moved
        /// by dragging since the last call, otherwise `rect`. A GUILayout.Window returns the
        /// rect passed in during Layout, and the size its layout gave it in the other events.
        public static Rect Declare(int id, Rect rect, GUI.WindowFunction func, GUIContent title, GUIStyle style, bool fitted, GUILayoutOption[] options, bool modal = false)
        {
            GUIStateData.RequireGUI();
            var p = GUIStateData.Participant;
            if (func == null) throw new System.ArgumentNullException(nameof(func));
            if (p == null) return rect;
            // GUI.Window called inside a window function makes no window: its function never
            // runs, and the call returns an empty rect.
            if (GUIStateData.InWindow && !fitted && !modal) return default;
            bool layout = Event.current.rawType == EventType.Layout;
            var w = Find(p, id);
            if (w == null)
            {
                w = new WindowRecord(id, p) { Rect = rect };
                if (layout) added.Add(w);
                else stack.Insert(0, w);
            }
            w.Touched = true;
            w.Function = func;
            w.Title.CopyFrom(title ?? GUIContent.none);
            w.Style = style ?? GUIStyle.none;
            w.Fitted = fitted;
            w.Options = options;
            w.Modal = modal;
            if (modal) ActiveModal = w;
            else if (ActiveModal == w) ActiveModal = null;
            w.Skin = GUIStateData.Skin;
            w.Color = GUIStateData.Color;
            w.BackgroundColor = GUIStateData.BackgroundColor;
            w.ContentColor = GUIStateData.ContentColor;
            w.Enabled = GUIStateData.Enabled;
            w.Matrix = GUIStateData.Matrix;
            if (fitted) w.Requested = rect;
            if (w.Moved)
            {
                w.Moved = false;
                return w.Rect;
            }
            if (fitted && !layout && w.HasFit)
            {
                w.Rect = new Rect(rect.position, w.FitSize);
                return w.Rect;
            }
            w.Rect = rect;
            return rect;
        }

        /// The window an input event goes to: for mouse events the window whose control holds
        /// the mouse, else the front window under it; for key events the window with the
        /// keyboard control, else (when no control has it) the focused window.
        public static (WindowRecord Window, bool UnderMouse) Target(Event e)
        {
            var type = e.rawType;
            if (Event.IsPointer(type))
            {
                int hot = GUIStateData.HotControl;
                if (hot != 0)
                {
                    var holder = ControlScope.OwnerOf(hot)?.Window;
                    return holder != null && stack.Contains(holder) ? (holder, false) : (null, false);
                }
                foreach (var w in stack)
                    if (!w.Modal && w.Owner.Alive && w.Contains(e.ScreenPosition)) return (w, true);
                return (null, false);
            }
            if (type is EventType.KeyDown or EventType.KeyUp)
            {
                int keyboard = GUIStateData.KeyboardControl;
                if (keyboard != 0)
                {
                    var holder = ControlScope.OwnerOf(keyboard)?.Window;
                    return holder != null && stack.Contains(holder) ? (holder, false) : (null, false);
                }
                return (Focused != null && stack.Contains(Focused) ? Focused : null, false);
            }
            return (null, false);
        }

        /// Runs a window's function for one event: the background (on Repaint), then the
        /// function inside a clip at the window's rect, with the state of its GUI.Window call.
        public static void Call(WindowRecord w, Event e)
        {
            var p = w.Owner;
            var previous = Event.current;
            Event.current = e;
            GUIStateData.InGUI = true;
            GUIStateData.InWindow = true;
            GUIStateData.Participant = p;
            GUIStateData.Window = w;
            GUIStateData.Scope = w.Scope;
            GUIStateData.BeginCall();
            if (w.Skin) GUIStateData.Skin = w.Skin;
            GUIStateData.Color = w.Color;
            GUIStateData.BackgroundColor = w.BackgroundColor;
            GUIStateData.ContentColor = w.ContentColor;
            GUIStateData.Enabled = w.Enabled;
            GUIStateData.SetMatrix(w.Matrix);
            w.Scope.BeginPass();
            ClipStack.Clear();
            ScrollViews.Reset();
            try
            {
                if (e.rawType == EventType.Repaint)
                    w.Style.Draw(w.Rect, w.Title, w.Rect.Contains(e.mousePosition), false, Focused == w, false);
                ClipStack.PushAbsolute(w.Rect);
                bool layoutEvent = e.rawType == EventType.Layout;
                if (p.UsesLayout)
                {
                    // The content is a group with the window's style.
                    if (layoutEvent) w.Layout.BeginRecord(w.Style);
                    else w.Layout.BeginReplay();
                    GUIStateData.Layout = w.Layout;
                }
                else
                {
                    GUIStateData.Layout = null;
                }
                GUIManager.Invoke(p, w);
                // The function ends with its window's clip alone left, whether or not it ran to
                // its end.
                if (ClipStack.Count == 0) Debug.LogError(ClipStack.InvalidPop);
                else if (ClipStack.Count > 1) Debug.LogError(GUIManager.UnbalancedClips);
                if (p.UsesLayout && layoutEvent)
                {
                    if (w.Fitted)
                    {
                        w.FitSize = w.Layout.ComputeFitted(w.Requested.size, w.Options);
                        w.HasFit = true;
                        w.Rect.size = w.FitSize;
                    }
                    else
                    {
                        w.Layout.Compute(w.Rect.size);
                    }
                }
            }
            finally
            {
                GUIManager.EndCall(previous);
            }
        }

        /// GUI.DragWindow: a press in `position` (window coordinates) and a drag move the
        /// window by the mouse movement.
        public static void Drag(Rect position)
        {
            var w = GUIStateData.Window;
            if (w == null)
            {
                Debug.LogError("Dragwindow can only be called within a window callback");
                return;
            }
            var e = Event.current;
            int id = GUIUtility.GetControlID(ControlHints.DragWindow, FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (position.Contains(e.mousePosition))
                    {
                        GUIStateData.HotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIStateData.HotControl == id)
                    {
                        // The window's rect is in GUI space; its matrix scales the movement.
                        var move = w.Matrix.isIdentity ? e.ScreenDelta : (Vector2)w.Matrix.inverse.MultiplyVector(e.ScreenDelta);
                        w.Rect.position += move;
                        w.Moved = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIStateData.HotControl == id)
                    {
                        GUIStateData.HotControl = 0;
                        e.Use();
                    }
                    break;
            }
        }
    }
}
