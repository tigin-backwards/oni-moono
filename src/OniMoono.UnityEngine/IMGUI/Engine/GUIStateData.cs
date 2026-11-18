using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.Engine.IMGUI
{
    /// The IMGUI state that GUI, GUIUtility and the controls read and write: the per-call state
    /// (skin, colours, enabled, matrix, changed), reset at the start of each OnGUI call, and the
    /// state kept across calls (mouse and keyboard control, state objects).
    internal static class GUIStateData
    {
        /// An OnGUI call or a window function is running.
        public static bool InGUI;
        /// A window function is running.
        public static bool InWindow;

        public static GUISkin Skin;
        public static Color Color = Color.white;
        public static Color BackgroundColor = Color.white;
        public static Color ContentColor = Color.white;
        public static bool Enabled = true;
        public static bool Changed;
        static Matrix4x4 matrix = Matrix4x4.identity;
        static Matrix4x4 inverse = Matrix4x4.identity;

        public static int HotControl;
        public static int KeyboardControl;
        /// The control that held the mouse when its release came outside its window (and was
        /// delivered there as Ignore): it keeps hotControl until the next press.
        public static int StaleHotControl;

        /// GUI.tooltip: one global value that only controls (on Repaint) and user code write;
        /// nothing resets it between events, calls or frames.
        public static string Tooltip = "";
        /// GUI.tooltipRect: set by a hovered label with a tooltip, persisting like the tooltip.
        public static Rect TooltipRect;
        /// GUIUtility.ExitGUI was called and the call it leaves has not ended yet.
        public static bool Exiting;

        /// The behaviour whose OnGUI (or whose window's function) is running.
        public static GUIParticipant Participant;
        /// The window whose function is running.
        public static WindowRecord Window;
        /// The control-ID container of the running call.
        public static ControlScope Scope;
        /// The GUILayout state of the running call; null when the behaviour does not use
        /// GUILayout.
        public static LayoutPass Layout;

        /// The name GUI.SetNextControlName gave to the next control.
        public static string NextControlName;

        static readonly Dictionary<int, object> stateObjects = new();

        public static Matrix4x4 Matrix => matrix;
        public static Matrix4x4 InverseMatrix => inverse;

        public static void SetMatrix(Matrix4x4 m)
        {
            matrix = m;
            inverse = m.isIdentity ? Matrix4x4.identity : m.inverse;
        }

        /// The skin of the running call, or the default skin outside OnGUI.
        public static GUISkin CurrentSkin => InGUI && Skin ? Skin : DefaultSkin.Get();

        /// GUI functions that need an OnGUI call check it here.
        public static void RequireGUI()
        {
            if (!InGUI) throw new ArgumentException("You can only call GUI functions from inside OnGUI.");
        }

        /// The state every OnGUI call starts with.
        public static void BeginCall()
        {
            Skin = DefaultSkin.Get();
            Color = Color.white;
            BackgroundColor = Color.white;
            ContentColor = Color.white;
            Enabled = true;
            Changed = false;
            SetMatrix(Matrix4x4.identity);
            NextControlName = null;
        }

        public static object GetStateObject(Type type, int controlId)
        {
            if (stateObjects.TryGetValue(controlId, out var o) && o != null && o.GetType() == type) return o;
            o = Activator.CreateInstance(type);
            stateObjects[controlId] = o;
            return o;
        }

        public static object QueryStateObject(Type type, int controlId)
            => stateObjects.TryGetValue(controlId, out var o) && type.IsInstanceOfType(o) ? o : null;

        /// Forgets everything (tests, a new session).
        public static void Reset()
        {
            InGUI = false;
            InWindow = false;
            Skin = null;
            BeginCall();
            HotControl = 0;
            KeyboardControl = 0;
            StaleHotControl = 0;
            Tooltip = "";
            TooltipRect = default;
            Exiting = false;
            Participant = null;
            Window = null;
            Scope = null;
            Layout = null;
            stateObjects.Clear();
            ClipStack.Clear();
        }
    }
}
