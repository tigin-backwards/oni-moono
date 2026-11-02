using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace E2eDriver
{
    /// What the player reports for keys, from `keys.log` on (README.md): the key codes
    /// Input.GetKeyDown is true for, the key codes of IMGUI KeyDown events, and the characters
    /// they type. A spec reads them as state (`type:E2eDriver.KeyLog::Down`), so the keyboard
    /// layout's mapping in both players compares like any other value.
    public static class KeyLog
    {
        static KeyCode[] codes;
        static int scannedFrame = -1;
        static readonly List<string> down = new List<string>();
        static readonly List<string> events = new List<string>();
        static readonly StringBuilder chars = new StringBuilder();
        static readonly List<string> wheel = new List<string>();
        static readonly List<string> wheelEvents = new List<string>();
        static readonly List<string> mouse = new List<string>();
        static readonly List<string> mouseHeld = new List<string>();
        static readonly List<string> mouseEvents = new List<string>();
        static readonly KeyCode[] mouseCodes =
            { KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2, KeyCode.Mouse3, KeyCode.Mouse4, KeyCode.Mouse5, KeyCode.Mouse6 };
        static GameObject listener;

        /// The key codes Input.GetKeyDown held for, one entry a frame (codes in one frame joined
        /// by '+'), space-separated.
        public static string Down => string.Join(" ", down.ToArray());
        /// The key code of each KeyDown event that names a key (repeats included), space-separated.
        public static string Events => string.Join(" ", events.ToArray());
        /// The characters of KeyDown events that type one, in order.
        public static string Chars => chars.ToString();
        /// Each frame's Input.mouseScrollDelta and Input.GetAxis("Mouse ScrollWheel") where either
        /// is not zero, "x,y/axis", space-separated.
        public static string Wheel => string.Join(" ", wheel.ToArray());
        /// The delta of each IMGUI ScrollWheel event, "x,y", space-separated.
        public static string WheelEvents => string.Join(" ", wheelEvents.ToArray());

        /// The mouse key codes (Mouse0 to Mouse6) Input.GetKeyDown held for, one entry a frame
        /// (codes in one frame joined by '+'), space-separated.
        public static string MouseDown => string.Join(" ", mouse.ToArray());
        /// The mouse key codes Input.GetKey held for, one entry a frame where any did ('+'
        /// between codes), space-separated.
        public static string MouseHeld => string.Join(" ", mouseHeld.ToArray());
        /// The button of each IMGUI MouseDown event, space-separated.
        public static string MouseEvents => string.Join(" ", mouseEvents.ToArray());

        public static bool Recording => listener != null;

        public static JToken Start(JObject a)
        {
            bool on = (bool?)a["on"] ?? true;
            down.Clear();
            events.Clear();
            chars.Length = 0;
            wheel.Clear();
            wheelEvents.Clear();
            mouse.Clear();
            mouseHeld.Clear();
            mouseEvents.Clear();
            if (on && listener == null)
            {
                if (codes == null)
                {
                    var all = (KeyCode[])Enum.GetValues(typeof(KeyCode));
                    var seen = new HashSet<int>();
                    var list = new List<KeyCode>();
                    // Mouse buttons and joysticks are not keys.
                    foreach (var c in all)
                        if (c != KeyCode.None && c < KeyCode.Mouse0 && seen.Add((int)c)) list.Add(c);
                    codes = list.ToArray();
                }
                listener = new GameObject("E2eDriverKeyLog");
                UnityEngine.Object.DontDestroyOnLoad(listener);
                listener.AddComponent<KeyLogGui>();
            }
            else if (!on && listener != null)
            {
                UnityEngine.Object.Destroy(listener);
                listener = null;
            }
            return new JObject { ["recording"] = Recording };
        }

        /// Once a frame, from Driver.Update.
        public static void Scan()
        {
            if (listener == null || scannedFrame == Time.frameCount) return;
            scannedFrame = Time.frameCount;
            string frame = null;
            foreach (var c in codes)
                if (Input.GetKeyDown(c)) frame = frame == null ? c.ToString() : frame + "+" + c;
            if (frame != null) down.Add(frame);
            string pressed = null, held = null;
            foreach (var c in mouseCodes)
            {
                if (Input.GetKeyDown(c)) pressed = pressed == null ? c.ToString() : pressed + "+" + c;
                if (Input.GetKey(c)) held = held == null ? c.ToString() : held + "+" + c;
            }
            if (pressed != null) mouse.Add(pressed);
            if (held != null) mouseHeld.Add(held);
            var scroll = Input.mouseScrollDelta;
            float axis = Input.GetAxis("Mouse ScrollWheel");
            if (scroll != Vector2.zero || axis != 0f) wheel.Add(Number(scroll.x) + "," + Number(scroll.y) + "/" + Number(axis));
        }

        static string Number(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        internal static void Gui(Event e)
        {
            if (e.type == EventType.ScrollWheel) wheelEvents.Add(Number(e.delta.x) + "," + Number(e.delta.y));
            if (e.type == EventType.MouseDown) mouseEvents.Add(e.button.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (e.type != EventType.KeyDown) return;
            if (e.keyCode != KeyCode.None) events.Add(e.keyCode.ToString());
            if (e.character != '\0') chars.Append(e.character);
        }
    }

    sealed class KeyLogGui : MonoBehaviour
    {
        void OnGUI() => KeyLog.Gui(Event.current);
    }
}
