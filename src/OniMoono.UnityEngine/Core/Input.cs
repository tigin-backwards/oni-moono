using System;
using OniMoono.Engine.Platform;

namespace UnityEngine
{
    public partial class Input
    {
        static InputState State => InputState.Instance;

        public static bool GetKey(KeyCode key) => State.Key(key);
        public static bool GetKeyDown(KeyCode key) => State.KeyDown(key);
        public static bool GetKeyUp(KeyCode key) => State.KeyUp(key);

        public static bool GetKey(string name) => State.Key(Parse(name));
        public static bool GetKeyDown(string name) => State.KeyDown(Parse(name));
        public static bool GetKeyUp(string name) => State.KeyUp(Parse(name));

        static KeyCode Parse(string name)
            => KeyNames.TryParse(name, out var k) ? k : throw new ArgumentException($"Input Key named: {name} is unknown");

        public static bool GetMouseButton(int button) => State.Key(MouseKey(button));
        public static bool GetMouseButtonDown(int button) => State.KeyDown(MouseKey(button));
        public static bool GetMouseButtonUp(int button) => State.KeyUp(MouseKey(button));

        static KeyCode MouseKey(int button)
        {
            if (button < 0 || button > 6) throw new ArgumentException("Invalid mouse button index " + button);
            return KeyCode.Mouse0 + button;
        }

        public static float GetAxis(string axisName) => State.GetAxis(axisName);
        public static float GetAxisRaw(string axisName) => State.GetAxisRaw(axisName);
        public static bool GetButton(string buttonName) => State.Button(buttonName);
        public static bool GetButtonDown(string buttonName) => State.ButtonDown(buttonName);
        public static bool GetButtonUp(string buttonName) => State.ButtonUp(buttonName);
        public static void ResetInputAxes() => State.Reset();

        public static Vector3 mousePosition => State.MousePosition;
        public static Vector2 mouseScrollDelta => State.ScrollDelta;
        public static bool mousePresent => State.MousePresent;
        public static bool anyKey => State.AnyKey;
        public static bool anyKeyDown => State.AnyKeyDown;

        // Desktop: no touch screen, no on-screen keyboard. The mode decides whether the window
        // takes IME input at all; the text being composed is the window's IME composition, and
        // the composed text arrives as typed characters (ImeInput, issue #30).
        public static bool touchSupported => false;
        public static int touchCount => 0;
        public static Touch GetTouch(int index) => throw new ArgumentException("Index out of bounds.");
        public static bool simulateMouseWithTouches { get; set; } = true;
        public static string compositionString => OniMoono.Engine.Platform.ImeInput.Composition;
        public static IMECompositionMode imeCompositionMode { get; set; } = IMECompositionMode.Auto;
        public static Vector2 compositionCursorPos { get; set; }
        /// False in the original Windows player in every state measured (issue #30): the US and
        /// the Chinese layout, the mode Auto and On, and while composing (again with Microsoft
        /// Pinyin selected for the game's thread and a composition from ImmSetCompositionString).
        public static bool imeIsSelected => false;
    }

    public partial struct Touch
    {
        private int m_FingerId;
        private Vector2 m_Position;
        private Vector2 m_RawPosition;
        private Vector2 m_PositionDelta;
        private float m_TimeDelta;
        private int m_TapCount;
        private TouchPhase m_Phase;
        private TouchType m_Type;
        private float m_Pressure;
        private float m_maximumPossiblePressure;
        private float m_Radius;
        private float m_RadiusVariance;
        private float m_AltitudeAngle;
        private float m_AzimuthAngle;

        public int fingerId { get => m_FingerId; set => m_FingerId = value; }
        public Vector2 position { get => m_Position; set => m_Position = value; }
        public Vector2 rawPosition { get => m_RawPosition; set => m_RawPosition = value; }
        public Vector2 deltaPosition { get => m_PositionDelta; set => m_PositionDelta = value; }
        public float deltaTime { get => m_TimeDelta; set => m_TimeDelta = value; }
        public int tapCount { get => m_TapCount; set => m_TapCount = value; }
        public TouchPhase phase { get => m_Phase; set => m_Phase = value; }
        public TouchType type { get => m_Type; set => m_Type = value; }
        public float pressure { get => m_Pressure; set => m_Pressure = value; }
        public float maximumPossiblePressure { get => m_maximumPossiblePressure; set => m_maximumPossiblePressure = value; }
        public float radius { get => m_Radius; set => m_Radius = value; }
        public float radiusVariance { get => m_RadiusVariance; set => m_RadiusVariance = value; }
        public float altitudeAngle { get => m_AltitudeAngle; set => m_AltitudeAngle = value; }
        public float azimuthAngle { get => m_AzimuthAngle; set => m_AzimuthAngle = value; }
    }

    /// Desktop players have no on-screen keyboard: isSupported is false and an opened keyboard
    /// never becomes visible; it only carries the text and selection it was given.
    public partial class TouchScreenKeyboard
    {
        string m_Text;
        RangeInt m_Selection;

        TouchScreenKeyboard(string text) => m_Text = text ?? "";

        public static bool isSupported => false;
        public static bool isInPlaceEditingAllowed => false;
        public static bool hideInput { get; set; }

        public static TouchScreenKeyboard Open(string text, TouchScreenKeyboardType keyboardType, bool autocorrection, bool multiline, bool secure, bool alert, string textPlaceholder, int characterLimit)
            => new(text) { characterLimit = characterLimit };

        public string text { get => m_Text; set => m_Text = value ?? ""; }
        public bool active { get => false; set { } }
        public Status status => Status.Done;
        public int characterLimit { get; set; }
        public bool canGetSelection => true;
        public bool canSetSelection => true;
        public RangeInt selection { get => m_Selection; set => m_Selection = value; }
    }

    public partial struct RangeInt
    {
        public int start;
        public int length;

        public RangeInt(int start, int length)
        {
            this.start = start;
            this.length = length;
        }

        public int end => start + length;
    }
}
