using System;
using System.Collections.Generic;
using OniMoono.Engine.Assets;
using UnityEngine;

namespace OniMoono.Engine.Platform
{
    /// Unity's legacy input state. The platform reports raw events during EarlyUpdate; the
    /// per-frame flags (down / up) hold for exactly that frame.
    internal sealed class InputState
    {
        public static readonly InputState Instance = new();

        readonly HashSet<KeyCode> held = new();
        readonly HashSet<KeyCode> downThisFrame = new();
        readonly HashSet<KeyCode> upThisFrame = new();
        /// Key edges since the previous frame, in the order they happened (true: pressed).
        readonly List<(KeyCode Key, bool Down)> pending = new();
        Vector2 mousePosition, previousMousePosition, pendingScroll;
        public Vector2 MouseDelta { get; private set; }
        public Vector2 ScrollDelta { get; private set; }
        public bool MousePresent { get; set; }

        readonly Dictionary<string, AxisState> axes = new(StringComparer.Ordinal);

        sealed class AxisState
        {
            public List<ProjectSettings.InputAxis> Definitions = new();
            public float Value;
        }

        public void InstallAxes(IEnumerable<ProjectSettings.InputAxis> definitions)
        {
            axes.Clear();
            foreach (var d in definitions)
            {
                if (!axes.TryGetValue(d.Name, out var a)) axes[d.Name] = a = new AxisState();
                a.Definitions.Add(d);
            }
        }

        // ---- Platform side ----

        public void KeyPressed(KeyCode key) => pending.Add((key, true));

        public void KeyReleased(KeyCode key) => pending.Add((key, false));

        /// Bottom-left origin, in pixels.
        public void MouseMoved(Vector2 position) => mousePosition = position;
        public void Scrolled(Vector2 delta) => pendingScroll += delta;

        /// Latches the events gathered since the previous frame (EarlyUpdate).
        public void BeginFrame(float unscaledDeltaTime)
        {
            downThisFrame.Clear();
            upThisFrame.Clear();
            // In order: a release and a new press of a held key in one frame report both edges
            // and leave it held.
            foreach (var (k, down) in pending)
            {
                if (down ? held.Add(k) : held.Remove(k)) (down ? downThisFrame : upThisFrame).Add(k);
            }
            pending.Clear();
            MouseDelta = mousePosition - previousMousePosition;
            previousMousePosition = mousePosition;
            ScrollDelta = pendingScroll;
            pendingScroll = Vector2.zero;
            foreach (var a in axes.Values) a.Value = Smooth(a, unscaledDeltaTime);
        }

        public bool Key(KeyCode k) => held.Contains(k);
        public bool KeyDown(KeyCode k) => downThisFrame.Contains(k);
        public bool KeyUp(KeyCode k) => upThisFrame.Contains(k);
        public Vector3 MousePosition => new(mousePosition.x, mousePosition.y, 0f);
        public bool AnyKey => held.Count > 0;
        public bool AnyKeyDown => downThisFrame.Count > 0;

        public void Reset()
        {
            held.Clear();
            pending.Clear();
            downThisFrame.Clear();
            upThisFrame.Clear();
            foreach (var a in axes.Values) a.Value = 0f;
        }

        // ---- Axes ----

        AxisState Axis(string name)
            => axes.TryGetValue(name, out var a) ? a : throw new ArgumentException($"Input Axis {name} is not setup.\n To change the input settings use: Edit -> Settings -> Input");

        /// The unsmoothed value: -1, 0 or 1 for buttons, the scaled delta for mouse movement.
        public float GetAxisRaw(string name)
        {
            float value = 0f;
            foreach (var d in Axis(name).Definitions)
            {
                float v = RawOf(d);
                if (Math.Abs(v) > Math.Abs(value)) value = v;
            }
            return value;
        }

        public float GetAxis(string name)
        {
            var a = Axis(name);
            float value = a.Value;
            foreach (var d in a.Definitions)
                if (d.Type == 1)
                {
                    float v = RawOf(d);
                    if (Math.Abs(v) > Math.Abs(value)) value = v;
                }
            return value;
        }

        float RawOf(ProjectSettings.InputAxis d)
        {
            switch (d.Type)
            {
                case 0: // key or mouse button
                {
                    float v = 0f;
                    if (Pressed(d.PositiveButton) || Pressed(d.AltPositiveButton)) v += 1f;
                    if (Pressed(d.NegativeButton) || Pressed(d.AltNegativeButton)) v -= 1f;
                    return d.Invert ? -v : v;
                }
                case 1: // mouse movement: x, y, wheel
                {
                    float delta = d.Axis switch { 0 => MouseDelta.x, 1 => MouseDelta.y, 2 => ScrollDelta.y, _ => 0f };
                    float v = delta * d.Sensitivity;
                    return d.Invert ? -v : v;
                }
                default: // joystick axes: Unity's joystick input is not used (gamepads go through Steam Input)
                    return 0f;
            }
        }

        /// Button axes move toward their target at `sensitivity` units per second and fall back at
        /// `gravity`; with snap, reversing direction jumps to zero first.
        float Smooth(AxisState a, float dt)
        {
            float value = a.Value;
            foreach (var d in a.Definitions)
            {
                if (d.Type != 0) continue;
                float target = RawOf(d);
                if (target != 0f)
                {
                    if (d.Snap && Math.Sign(target) != Math.Sign(value) && value != 0f) value = 0f;
                    value = Mathf.MoveTowards(value, target, d.Sensitivity * dt);
                }
                else
                {
                    value = Mathf.MoveTowards(value, 0f, d.Gravity * dt);
                }
                return value;
            }
            return value;
        }

        public bool ButtonDown(string name)
        {
            foreach (var d in Axis(name).Definitions)
                if (d.Type == 0 && (PressedDown(d.PositiveButton) || PressedDown(d.AltPositiveButton) || PressedDown(d.NegativeButton) || PressedDown(d.AltNegativeButton)))
                    return true;
            return false;
        }

        public bool ButtonUp(string name)
        {
            foreach (var d in Axis(name).Definitions)
                if (d.Type == 0 && (Released(d.PositiveButton) || Released(d.AltPositiveButton) || Released(d.NegativeButton) || Released(d.AltNegativeButton)))
                    return true;
            return false;
        }

        public bool Button(string name)
        {
            foreach (var d in Axis(name).Definitions)
                if (d.Type == 0 && (Pressed(d.PositiveButton) || Pressed(d.AltPositiveButton) || Pressed(d.NegativeButton) || Pressed(d.AltNegativeButton)))
                    return true;
            return false;
        }

        bool Pressed(string keyName) => KeyNames.TryParse(keyName, out var k) && Key(k);
        bool PressedDown(string keyName) => KeyNames.TryParse(keyName, out var k) && KeyDown(k);
        bool Released(string keyName) => KeyNames.TryParse(keyName, out var k) && KeyUp(k);
    }

    /// Unity's key names ("a", "left shift", "[1]", "mouse 0", "joystick button 0"…).
    internal static class KeyNames
    {
        static readonly Dictionary<string, KeyCode> byName = Build();

        static Dictionary<string, KeyCode> Build()
        {
            var map = new Dictionary<string, KeyCode>(StringComparer.Ordinal);
            foreach (KeyCode k in Enum.GetValues<KeyCode>())
            {
                var n = k.ToString();
                string name = n switch
                {
                    _ when n.StartsWith("Alpha") && n.Length == 6 => n[5..],
                    _ when n.StartsWith("Keypad") && n.Length == 7 && char.IsDigit(n[6]) => "[" + n[6] + "]",
                    "KeypadPeriod" => "[.]",
                    "KeypadDivide" => "[/]",
                    "KeypadMultiply" => "[*]",
                    "KeypadMinus" => "[-]",
                    "KeypadPlus" => "[+]",
                    "KeypadEnter" => "enter",
                    "KeypadEquals" => "equals",
                    _ when n.StartsWith("Mouse") && n.Length == 6 => "mouse " + n[5],
                    _ when n.StartsWith("JoystickButton") => "joystick button " + n["JoystickButton".Length..],
                    _ when n.StartsWith("Joystick") && n.Contains("Button") => "joystick " + n["Joystick".Length..n.IndexOf("Button")] + " button " + n[(n.IndexOf("Button") + 6)..],
                    "UpArrow" => "up",
                    "DownArrow" => "down",
                    "LeftArrow" => "left",
                    "RightArrow" => "right",
                    "Return" => "return",
                    "Escape" => "escape",
                    "Space" => "space",
                    "Backspace" => "backspace",
                    "Tab" => "tab",
                    _ => SplitWords(n),
                };
                map.TryAdd(name, k);
            }
            return map;
        }

        static string SplitWords(string n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(n[i]));
            }
            return sb.ToString();
        }

        public static bool TryParse(string name, out KeyCode key)
        {
            key = KeyCode.None;
            return !string.IsNullOrEmpty(name) && byName.TryGetValue(name, out key);
        }
    }
}
