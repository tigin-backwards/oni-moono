using System;
using System.Collections.Generic;

namespace UnityEngine.UIElements
{
    /// UI Toolkit's player runtime state that uGUI reads. The panel registry is permanently empty
    /// (see README-scope.cs); the event-system registration is ordinary state.
    internal static partial class UIElementsRuntimeUtility
    {
        static readonly List<BaseRuntimePanel> noPanels = new List<BaseRuntimePanel>();
        static Action<BaseRuntimePanel> createPanelHandlers;

        /// The highest sorting index resolved over the player panels; -1 with no panels.
        internal static int s_ResolvedSortingIndexMax = -1;

        /// Raised by Unity when a player panel is created; no panel is ever created here.
        internal static event Action<BaseRuntimePanel> onCreatePanel
        {
            add => createPanelHandlers += value;
            remove => createPanelHandlers -= value;
        }

        /// The uGUI EventSystem that drives UI Toolkit input, or null.
        internal static Object activeEventSystem { get; private set; }

        /// Makes eventSystem the active one. Only uGUI's EventSystem registers, so a different
        /// registered object still alive is a second event system.
        internal static void RegisterEventSystem(Object eventSystem)
        {
            if (activeEventSystem && !ReferenceEquals(activeEventSystem, eventSystem))
                Debug.LogWarning("There can be only one active Event System.", eventSystem);
            activeEventSystem = eventSystem;
        }

        internal static void UnregisterEventSystem(Object eventSystem)
        {
            if (ReferenceEquals(activeEventSystem, eventSystem))
                activeEventSystem = null;
        }

        /// The player panels in sorting order: none exist.
        internal static List<BaseRuntimePanel> GetSortedPlayerPanels() => noPanels;
    }

    /// Per-pointer device state, kept separately for the player and editor contexts: the last
    /// position and its panel, the pressed buttons, and the soft capture (the runtime panel and
    /// camera of the element a press started on).
    internal static partial class PointerDeviceState
    {
        sealed class PointerRecord
        {
            public Vector3 Position;
            public IPanel Panel;
            public int Buttons;
            public BaseRuntimePanel CapturePanel;
            public Camera CaptureCamera;
        }

        static readonly PointerRecord[][] records = CreateRecords();

        static PointerRecord[][] CreateRecords()
        {
            var contexts = new PointerRecord[2][];
            for (int c = 0; c < contexts.Length; c++)
            {
                contexts[c] = new PointerRecord[PointerId.maxPointers];
                for (int i = 0; i < PointerId.maxPointers; i++)
                    contexts[c][i] = new PointerRecord();
            }
            return contexts;
        }

        static PointerRecord For(int pointerId, ContextType context = ContextType.Player) => records[(int)context][pointerId];

        // Buttons and soft capture are device state, shared by both contexts.
        static PointerRecord Device(int pointerId) => records[(int)ContextType.Player][pointerId];

        /// Records the pointer at position over panel.
        internal static void RecordPosition(int pointerId, Vector3 position, IPanel panel, ContextType context)
        {
            var r = For(pointerId, context);
            r.Position = position;
            r.Panel = panel;
        }

        internal static Vector3 LastPosition(int pointerId, ContextType context) => For(pointerId, context).Position;

        internal static IPanel LastPanel(int pointerId, ContextType context) => For(pointerId, context).Panel;

        /// The movement from the last recorded position to newPosition; zero when the pointer
        /// was last over no panel.
        internal static Vector3 GetPointerDeltaPosition(int pointerId, ContextType context, Vector3 newPosition)
        {
            var r = For(pointerId, context);
            return r.Panel == null ? Vector3.zero : newPosition - r.Position;
        }

        internal static int GetPressedButtons(int pointerId) => Device(pointerId).Buttons;

        internal static void PressButton(int pointerId, int buttonId) => Device(pointerId).Buttons |= ButtonBit(buttonId);

        internal static void ReleaseButton(int pointerId, int buttonId) => Device(pointerId).Buttons &= ~ButtonBit(buttonId);

        internal static void ReleaseAllButtons(int pointerId) => Device(pointerId).Buttons = 0;

        /// Whether a button other than buttonId is held on the pointer.
        internal static bool OtherButtonsPressed(int pointerId, int buttonId) => (Device(pointerId).Buttons & ~ButtonBit(buttonId)) != 0;

        static int ButtonBit(int buttonId)
        {
            if (buttonId < 0 || buttonId > 31)
                throw new ArgumentOutOfRangeException(nameof(buttonId), buttonId, "Button ids range from 0 to 31.");
            return 1 << buttonId;
        }

        /// Records the element a press started on: its runtime panel and the camera it was seen
        /// through.
        internal static void SetElementWithSoftPointerCapture(int pointerId, VisualElement element, Camera camera)
        {
            var r = Device(pointerId);
            r.CapturePanel = element?.panel as BaseRuntimePanel;
            r.CaptureCamera = camera;
        }

        internal static BaseRuntimePanel PanelWithSoftPointerCapture(int pointerId) => Device(pointerId).CapturePanel;

        internal static Camera GetCameraWithSoftPointerCapture(int pointerId) => Device(pointerId).CaptureCamera;

        /// Forgets the positions and soft captures that refer to a removed panel.
        internal static void ForgetPanel(IPanel panel)
        {
            foreach (var context in records)
            {
                foreach (var r in context)
                {
                    if (r.Panel == panel)
                    {
                        r.Panel = null;
                        r.Position = Vector3.zero;
                    }
                    if (r.CapturePanel == panel)
                    {
                        r.CapturePanel = null;
                        r.CaptureCamera = null;
                    }
                }
            }
        }
    }

    internal static partial class UIRUtility
    {
        /// Destroys an object UI Toolkit created; the player is always in play mode.
        internal static void Destroy(Object obj)
        {
            if (obj)
                Object.Destroy(obj);
        }
    }

    internal partial class ClickDetector
    {
        /// The double-click interval in milliseconds; -1 means not read yet (a panel's click
        /// detector reads the system setting, and none exists here).
        internal static int s_DoubleClickTime { get; set; } = -1;

        // Click detectors belong to panels, and none exists here.
        private ClickDetector()
        {
        }
    }

    /// The scene-wide settings of UI Toolkit input. The component's lifecycle drives UI
    /// Toolkit's own event system, which is not implemented (the game has no such component),
    /// so no configuration is ever current.
    public partial class PanelInputConfiguration
    {
        [SerializeField] private Settings m_Settings = Settings.Default;

        /// The configuration in effect: none.
        internal static PanelInputConfiguration current { get; private set; }

        /// Raised by Unity when a configuration is applied.
        internal static Action<PanelInputConfiguration> onApply = null;

        internal Settings settings => m_Settings;

        internal partial struct Settings
        {
            [SerializeField] private bool m_ProcessWorldSpaceInput;
            [SerializeField] private LayerMask m_InteractionLayers;
            [SerializeField] private float m_MaxInteractionDistance;
            [SerializeField] private bool m_DefaultEventCameraIsMainCamera;
            [SerializeField] private Camera[] m_EventCameras;
            [SerializeField] private PanelInputRedirection m_PanelInputRedirection;
            [SerializeField] private bool m_AutoCreatePanelComponents;

            public bool processWorldSpaceInput => m_ProcessWorldSpaceInput;
            public LayerMask interactionLayers => m_InteractionLayers;
            public float maxInteractionDistance => m_MaxInteractionDistance;
            public bool defaultEventCameraIsMainCamera => m_DefaultEventCameraIsMainCamera;
            public Camera[] eventCameras => m_EventCameras;
            public PanelInputRedirection panelInputRedirection => m_PanelInputRedirection;
            public bool autoCreatePanelComponents => m_AutoCreatePanelComponents;

            /// World-space input on every layer but Ignore Raycast at any distance, through the main
            /// camera, with automatic input redirection and panel components.
            public static Settings Default => new Settings
            {
                m_ProcessWorldSpaceInput = true,
                m_InteractionLayers = ~(1 << 2),
                m_MaxInteractionDistance = Mathf.Infinity,
                m_DefaultEventCameraIsMainCamera = true,
                m_EventCameras = Array.Empty<Camera>(),
                m_PanelInputRedirection = PanelInputRedirection.AutoSwitch,
                m_AutoCreatePanelComponents = true,
            };
        }
    }

    /// Only the static state uGUI reads: UIDocument's lifecycle creates panels, which this
    /// runtime does not have.
    public partial class UIDocument
    {
        /// The number of enabled documents: none is ever enabled.
        internal static int EnabledDocumentCount = 0;
    }
}
