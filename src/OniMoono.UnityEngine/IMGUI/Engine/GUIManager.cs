using System;
using System.Collections.Generic;
using OniMoono.Engine.Rendering;
using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// A behaviour that takes part in IMGUI (its script declares OnGUI), with the state kept for
    /// it across events: its control IDs, its GUILayout entries and its depth. Its windows are
    /// in the one stack of every behaviour's windows (Windows).
    internal sealed class GUIParticipant(BehaviourNative behaviour, long order)
    {
        public readonly BehaviourNative Behaviour = behaviour;
        /// When it was enabled: the tie-break between equal depths.
        public readonly long Order = order;
        /// GUI.depth as last set by its OnGUI: lower is in front.
        public int Depth;
        public readonly ControlScope Scope = new();
        public readonly LayoutPass Layout = new();

        public bool UsesLayout => Behaviour.UseGUILayout;

        public bool Alive => !Behaviour.Destroyed && Behaviour.IsActiveAndEnabledState && Behaviour.Managed is MonoBehaviour;

        public void Release()
        {
            Scope.Release();
            Windows.Release(this);
        }
    }

    /// Runs IMGUI each frame, after the cameras and canvases have rendered: every input event
    /// of the frame, in order, then a Repaint ("GUILayout, measured", part 4).
    ///
    /// Every event is preceded by its own Layout: each behaviour's OnGUI front to back, then
    /// the windows of all the behaviours, front to back. Repaint goes to the behaviours back
    /// to front, then to the windows back to front. A mouse event goes first to the front
    /// window under the mouse (or the one whose control holds it), then to the behaviours
    /// front to back, until one of them has used it. GUI.depth orders the behaviours (lower in
    /// front); equal depths keep the order in which they were enabled, the first in front.
    /// The player sends no MouseMove events.
    internal static class GUIManager
    {
        public const string UnbalancedClips = "GUI Error: You are pushing more GUIClips than you are popping. Make sure they are balanced.";

        static bool installed;
        static readonly List<GUIParticipant> participants = new();
        static readonly Dictionary<BehaviourNative, GUIParticipant> byBehaviour = new(ReferenceEqualityComparer.Instance);
        /// The behaviours front to back.
        static readonly List<GUIParticipant> ordered = new();
        static long nextOrder;
        /// The '\t' character event that follows a Tab press which moved focus is used too.
        static bool swallowTabCharacter;
        /// The last mouse position input reported, for the Layout and Repaint events.
        static Vector2 lastMouse;
        // Layout and Repaint events are never used, so one of each serves every pass.
        static readonly Event layoutPass = new(), repaintPass = new();

        public static void Install()
        {
            if (installed) return;
            installed = true;
            PlayerLoop.GUI += RunFrame;
            FrameRenderer.RenderScreenLate += RepaintCapture;
        }

        public static void Register(BehaviourNative b)
        {
            if (b.Script?.OnGUI == null || byBehaviour.ContainsKey(b)) return;
            var p = new GUIParticipant(b, nextOrder++);
            participants.Add(p);
            byBehaviour[b] = p;
        }

        public static void Unregister(BehaviourNative b)
        {
            if (!byBehaviour.Remove(b, out var p)) return;
            participants.Remove(p);
            p.Release();
        }

        /// Forgets the participants, their windows and the queued events (tests).
        public static void Reset()
        {
            foreach (var p in participants) p.Release();
            participants.Clear();
            byBehaviour.Clear();
            InputEvents.Reset();
            Windows.Reset();
            GUIStateData.StaleHotControl = 0;
            swallowTabCharacter = false;
            lastMouse = Vector2.zero;
        }

        static void RunFrame()
        {
            var events = InputEvents.TakeForGUI();
            foreach (var e in events) Dispatch(e);
            events.Clear();
            Repaint();
        }

        static void Repaint()
        {
            bool draws = GraphicsDevice.Current.RendersFrames && participants.Count > 0;
            if (draws) GUIRenderer.BeginFrame();
            try
            {
                Dispatch(Pass(EventType.Repaint, null));
            }
            finally
            {
                if (draws) GUIRenderer.EndFrame();
            }
        }

        /// A super-sampled capture renders the screen again, OnGUI included.
        static void RepaintCapture()
        {
            if (GraphicsDevice.Current.RendersFrames) Repaint();
        }

        /// The Layout or Repaint event, at the mouse position of `source` or the last one known.
        static Event Pass(EventType type, Event source)
        {
            var e = type == EventType.Layout ? layoutPass : repaintPass;
            e.Prepare(type, source != null ? source.ScreenPosition : lastMouse, source?.modifiers ?? EventModifiers.None);
            return e;
        }

        static void Dispatch(Event e)
        {
            var type = e.rawType;
            if (e.isMouse || type == EventType.ScrollWheel) lastMouse = e.ScreenPosition;
            // The player sends no MouseMove events: a move only places the mouse.
            if (type == EventType.MouseMove) return;
            if (swallowTabCharacter)
            {
                swallowTabCharacter = false;
                if (type == EventType.KeyDown && e.keyCode == KeyCode.None && e.character == '\t') return;
            }
            // A control left holding the mouse by a release outside its window lets go at the
            // next press.
            if (type == EventType.MouseDown && GUIStateData.StaleHotControl != 0)
            {
                if (GUIStateData.HotControl == GUIStateData.StaleHotControl) GUIStateData.HotControl = 0;
                GUIStateData.StaleHotControl = 0;
            }
            Order();
            if (type == EventType.Layout)
            {
                LayoutPass(e);
                return;
            }
            LayoutPass(Pass(EventType.Layout, e));
            if (type == EventType.Repaint)
            {
                RepaintPass(e);
                return;
            }
            if (Modal() is { } modal)
            {
                // While a modal window is shown, input goes to it alone.
                SendModal(modal, e);
            }
            else
            {
                SendInput(e);
            }
            // A click no control took ends keyboard focus (a click outside a text field).
            if (type == EventType.MouseDown && e.rawType != EventType.Used) GUIStateData.KeyboardControl = 0;
        }

        /// Front to back: lower depth first, equal depths in the order they were enabled.
        static void Order()
        {
            ordered.Clear();
            ordered.AddRange(participants);
            ordered.Sort(static (a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Order.CompareTo(b.Order));
        }

        /// A Layout event: every behaviour's OnGUI front to back (their window declarations),
        /// then every window front to back.
        static void LayoutPass(Event layout)
        {
            Windows.BeginDeclarations();
            foreach (var p in ordered)
                if (p.Alive && p.UsesLayout) Call(p, layout);
            Windows.EndDeclarations();
            foreach (var w in Windows.Snapshot())
            {
                if (!w.Owner.Alive || !w.Owner.UsesLayout || !Windows.Shown(w)) continue;
                // A modal window that is not the one shown is skipped.
                if (w.Modal && w != Windows.ActiveModal) continue;
                Windows.Call(w, layout);
            }
        }

        /// Repaint: the behaviours back to front, then the windows back to front; the modal
        /// window shown is drawn last, in front of every other window.
        static void RepaintPass(Event e)
        {
            Windows.BeginRepaintDeclarations();
            for (int i = ordered.Count - 1; i >= 0; i--)
                if (ordered[i].Alive) Call(ordered[i], e);
            Windows.EndRepaintDeclarations();
            var windows = Windows.Snapshot();
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                var w = windows[i];
                if (w.Modal || !w.Owner.Alive || !Windows.Shown(w)) continue;
                Windows.Call(w, e);
            }
            if (Modal() is { } shown) Windows.Call(shown, e);
        }

        /// An input event: the window it is for, then the behaviours front to back; a behaviour
        /// that leaves it used ends it (the first one gets it even when the window used it).
        static void SendInput(Event e)
        {
            var (target, underMouse) = Windows.Target(e);
            if (target != null)
            {
                bool down = e.rawType == EventType.MouseDown && underMouse;
                if (e.rawType == EventType.MouseUp && !underMouse && GUIStateData.HotControl != 0 && !target.Contains(e.ScreenPosition))
                {
                    // A release outside the window whose control holds the mouse reaches it as
                    // Ignore, and the control keeps hotControl until the next press.
                    GUIStateData.StaleHotControl = GUIStateData.HotControl;
                    e.Masked = true;
                    try { Windows.Call(target, e); }
                    finally { e.Masked = false; }
                }
                else
                {
                    Windows.Call(target, e);
                }
                TabFocus(target.Scope, e);
                if (down)
                {
                    // A press on a window focuses it and brings it to the front at once; a press
                    // on its empty area reaches the behaviours unused.
                    Windows.Focused = target;
                    Windows.BringToFrontNow(target);
                }
            }
            else if (e.rawType == EventType.MouseDown)
            {
                Windows.Focused = null;
            }
            foreach (var p in ordered)
            {
                if (!p.Alive) continue;
                Call(p, e);
                TabFocus(p.Scope, e);
                if (e.rawType == EventType.Used) break;
            }
        }

        /// The modal window shown, if any: the last one declared, while its behaviour is alive
        /// and keeps declaring it.
        static WindowRecord Modal()
        {
            var w = Windows.ActiveModal;
            if (w == null) return null;
            if (w.Owner.Alive && byBehaviour.TryGetValue(w.Owner.Behaviour, out var p) && p == w.Owner && Windows.Shown(w)) return w;
            return null;
        }

        /// GUIUtility.hasModalWindow.
        public static bool HasModalWindow => Modal() != null;

        /// An input event while a modal window is shown: the modal window alone gets it.
        static void SendModal(WindowRecord modal, Event e)
        {
            bool down = e.rawType == EventType.MouseDown && modal.Contains(e.ScreenPosition);
            Windows.Call(modal, e);
            TabFocus(modal.Scope, e);
            if (down)
            {
                Windows.Focused = modal;
                Windows.BringToFrontNow(modal);
                if (e.rawType != EventType.Used) e.Use();
            }
        }

        /// A Tab press nothing used moves keyboard focus to the container's next focusable
        /// control (Shift+Tab: the previous one), when the focus is in that container or
        /// nowhere.
        static void TabFocus(ControlScope scope, Event e)
        {
            if (e.rawType != EventType.KeyDown || e.keyCode != KeyCode.Tab) return;
            int current = GUIStateData.KeyboardControl;
            if (current != 0 && ControlScope.OwnerOf(current) != scope) return;
            int next = scope.NextFocus(current, e.shift);
            if (next == 0) return;
            GUIStateData.KeyboardControl = next;
            e.Use();
            swallowTabCharacter = true;
        }

        /// Runs a behaviour's OnGUI for one event with the state a call starts with.
        static void Call(GUIParticipant p, Event e)
        {
            var previous = Event.current;
            Event.current = e;
            GUIStateData.InGUI = true;
            GUIStateData.InWindow = false;
            GUIStateData.Participant = p;
            GUIStateData.Window = null;
            GUIStateData.Scope = p.Scope;
            GUIStateData.BeginCall();
            p.Scope.BeginPass();
            ClipStack.Clear();
            ScrollViews.Reset();
            bool layoutEvent = e.rawType == EventType.Layout;
            if (p.UsesLayout)
            {
                if (layoutEvent) p.Layout.BeginRecord(GUIStyle.none);
                else p.Layout.BeginReplay();
                GUIStateData.Layout = p.Layout;
            }
            else
            {
                GUIStateData.Layout = null;
            }
            bool completed = Invoke(p, null);
            try
            {
                if (completed && ClipStack.Count > 0) Debug.LogError(UnbalancedClips);
                if (p.UsesLayout && layoutEvent) p.Layout.Compute();
            }
            finally
            {
                EndCall(previous);
            }
        }

        /// Runs a behaviour's OnGUI, or one of its window functions; an exception is logged with
        /// the behaviour as context, ExitGUI ends the call silently. Returns whether the code
        /// ran to its end.
        public static bool Invoke(GUIParticipant p, WindowRecord window)
        {
            var context = (MonoBehaviour)p.Behaviour.Managed;
            try
            {
                if (window != null) window.Function(window.Id);
                else p.Behaviour.Script.OnGUI(context);
                return true;
            }
            catch (ExitGUIException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, context);
                return false;
            }
            finally
            {
                GUIStateData.Exiting = false;
            }
        }

        public static void EndCall(Event previous)
        {
            ClipStack.Clear();
            ScrollViews.Reset();
            GUIStateData.InGUI = false;
            GUIStateData.InWindow = false;
            GUIStateData.Participant = null;
            GUIStateData.Window = null;
            GUIStateData.Scope = null;
            GUIStateData.Layout = null;
            Event.current = previous;
        }
    }
}
