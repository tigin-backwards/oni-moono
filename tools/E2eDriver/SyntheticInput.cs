using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

#pragma warning disable CS0618 // FindObjectsOfType: the call both players have had longest

namespace E2eDriver
{
    /// Input made inside the game, for runs whose window must not take the desktop's focus or
    /// pointer (Windows test runs: README.md, "Synthetic input"). The event system reads the
    /// pointer through a BaseInput override, the game's world tools through KInputManager's
    /// locked mouse position, and keys and mouse buttons reach the game as its KInputController
    /// queues a real key's KeyDef. Both players go through the same calls; the platform's own
    /// input (window messages, X11 events) is not involved.
    public sealed class SyntheticInput : BaseInput
    {
        static SyntheticInput instance;
        static Vector2 position;
        static readonly bool[] held = new bool[3];
        static readonly bool[] pendingDown = new bool[3], pendingUp = new bool[3];
        static readonly int[] downFrame = { -1, -1, -1 }, upFrame = { -1, -1, -1 };
        static float pendingScroll;
        static Vector2 scroll;
        static int scrollFrame = -1;

        public static bool Active => instance != null;

        public override Vector2 mousePosition => position;
        public override bool mousePresent => true;
        public override Vector2 mouseScrollDelta
        {
            get
            {
                if (pendingScroll != 0) { scroll = new Vector2(0, pendingScroll); pendingScroll = 0; scrollFrame = Time.frameCount; }
                return scrollFrame == Time.frameCount ? scroll : Vector2.zero;
            }
        }
        public override bool GetMouseButton(int button) => button >= 0 && button < 3 && held[button];
        public override bool GetMouseButtonDown(int button)
        {
            if (button < 0 || button >= 3) return false;
            if (pendingDown[button]) { pendingDown[button] = false; downFrame[button] = Time.frameCount; }
            return downFrame[button] == Time.frameCount;
        }
        public override bool GetMouseButtonUp(int button)
        {
            if (button < 0 || button >= 3) return false;
            // An up in the frame of its down waits a frame, as a real click's release does.
            if (pendingUp[button] && !pendingDown[button] && downFrame[button] != Time.frameCount)
            {
                pendingUp[button] = false;
                upFrame[button] = Time.frameCount;
            }
            return upFrame[button] == Time.frameCount;
        }
        // No keyboard navigation and no IME composition: keys reach the game through KInput only.
        public override float GetAxisRaw(string axisName) => 0f;
        public override bool GetButtonDown(string buttonName) => false;
        public override string compositionString => string.Empty;
        public override int touchCount => 0;

        void Update()
        {
            // The game unlocks the position only from its debug keys; it is set again each frame.
            KInputManager.isMousePosLocked = true;
            KInputManager.lockedMousePos = new Vector3(position.x, position.y, 0f);
            // FindObjectsOfType walks every live object: on a late-game colony that was about
            // 190 ms a frame in both players. Once a second is enough here; each input request
            // attaches again first (Begin).
            if (Time.frameCount - attachedFrame >= 60) Attach();
        }

        static int attachedFrame = -1000;

        /// The override on every enabled input module (the game switches between two).
        static void Attach()
        {
            attachedFrame = Time.frameCount;
            foreach (var module in UnityEngine.Object.FindObjectsOfType<BaseInputModule>())
                if (module.inputOverride != instance) module.inputOverride = instance;
        }

        static readonly FieldInfo HasFocus = typeof(KInputManager).GetField("hasFocus", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo EventSystemFocus = typeof(UnityEngine.EventSystems.EventSystem).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// The game and the event system told they have the focus, as a focused window does: both
        /// drop input while unfocused (KInputManager.isFocused, EventSystem.isFocused).
        static void Focus()
        {
            HasFocus?.SetValue(null, true);
            foreach (var system in UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>())
                if (!system.isFocused) EventSystemFocus?.Invoke(system, new object[] { true });
        }

        static void Begin()
        {
            if (instance == null)
            {
                var keeper = new GameObject("E2eDriverSyntheticInput");
                UnityEngine.Object.DontDestroyOnLoad(keeper);
                instance = keeper.AddComponent<SyntheticInput>();
            }
            Focus();
            Attach();
        }

        /// input.* requests (README.md). x, y are screen pixels from the top left, as ui.find's
        /// screen rects.
        public static JToken Run(string command, JObject a)
        {
            Begin();
            switch (command)
            {
                case "input.move":
                    position = new Vector2((float)a["x"], Screen.height - (float)a["y"]);
                    KInputManager.lockedMousePos = new Vector3(position.x, position.y, 0f);
                    return Where();
                case "input.button":
                {
                    int b = Button((string)a["button"] ?? "left");
                    bool down = (bool?)a["down"] ?? true;
                    if (b < 3)
                    {
                        if (down) { held[b] = true; pendingDown[b] = true; }
                        else { held[b] = false; pendingUp[b] = true; }
                    }
                    var queued = Queue((KKeyCode)((int)KKeyCode.Mouse0 + b), Modifier.None, down);
                    return new JObject { ["button"] = b, ["down"] = down, ["queued"] = queued, ["pos"] = Where() };
                }
                case "input.key":
                {
                    var code = (KKeyCode)Enum.Parse(typeof(KKeyCode), (string)a["code"] ?? throw new DriverError("input.key needs code"), true);
                    var modifier = Modifier.None;
                    foreach (var m in (a["mods"] as JArray) ?? new JArray())
                        modifier |= (Modifier)Enum.Parse(typeof(Modifier), (string)m, true);
                    bool down = (bool?)a["down"] ?? true;
                    return new JObject { ["code"] = code.ToString(), ["mods"] = modifier.ToString(), ["down"] = down, ["queued"] = Queue(code, modifier, down) };
                }
                case "input.scroll":
                {
                    int notches = (int?)a["notches"] ?? 0;
                    pendingScroll += notches;
                    var code = notches > 0 ? KKeyCode.MouseScrollUp : KKeyCode.MouseScrollDown;
                    bool queued = true;
                    for (int i = 0; i < Math.Abs(notches); i++) queued &= Queue(code, Modifier.None, true) & Queue(code, Modifier.None, false);
                    return new JObject { ["notches"] = notches, ["queued"] = queued };
                }
                case "input.text":
                {
                    // The selected input field's text set whole (onValueChanged runs as for typing):
                    // keys typed into a field arrive as IMGUI events, which nothing here can make.
                    var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                    var field = selected != null ? selected.GetComponent<TMPro.TMP_InputField>() : null;
                    if (field == null) throw new DriverError("input.text: no input field is selected");
                    field.text = (string)a["text"] ?? "";
                    return new JObject { ["field"] = selected.name, ["text"] = field.text };
                }
                case "input.submit":
                {
                    // Enter in the selected input field: its onSubmit, then the field deactivated
                    // (which sends onEndEdit), as TMP_InputField does for Enter in a single line.
                    var selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                    var field = selected != null ? selected.GetComponent<TMPro.TMP_InputField>() : null;
                    if (field == null) throw new DriverError("input.submit: no input field is selected");
                    field.onSubmit?.Invoke(field.text);
                    field.DeactivateInputField();
                    return new JObject { ["field"] = selected.name, ["text"] = field.text };
                }
                case "input.focus":
                    return new JObject { ["focused"] = KInputManager.isFocused };
                default:
                    throw new DriverError("unknown command " + command);
            }
        }

        static JObject Where() => new JObject { ["x"] = position.x, ["y"] = Screen.height - position.y };

        static int Button(string name)
        {
            switch (name)
            {
                case "left": return 0;
                case "right": return 1;
                case "middle": return 2;
                case "back": return 3;
                case "forward": return 4;
                default: throw new DriverError("input.button: no button " + name);
            }
        }

        static readonly FieldInfo Lookup = typeof(KInputController).GetField("mKeyDefLookup", BindingFlags.Instance | BindingFlags.NonPublic);

        /// The key's KeyDef queued in the keyboard and mouse controller, as its Update queues it
        /// for a real key; false when no binding uses the key with these modifiers.
        static bool Queue(KKeyCode code, Modifier modifier, bool down)
        {
            Focus();
            var controller = Global.GetInputManager()?.GetDefaultController() ?? throw new DriverError("no input controller");
            var table = Lookup?.GetValue(controller) as IDictionary ?? throw new DriverError("no KeyDef table");
            var def = table[new KInputController.KeyDefEntry(code, modifier)] as KInputController.KeyDef;
            if (def == null) return false;
            controller.QueueButtonEvent(def, down);
            return true;
        }
    }

    /// screen.capture {name}: the frame as a PNG through ScreenCapture, in the run's temporary
    /// folder (Path.GetTempPath()/e2e-shots); answers with its path once the file is complete.
    static class ScreenShot
    {
        public static void Capture(Request request)
        {
            var name = (string)request.Args["name"] ?? throw new DriverError("screen.capture needs name");
            var folder = Path.Combine(Path.GetTempPath(), "e2e-shots");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{Driver.Port}-{name}.png");
            if (File.Exists(path)) File.Delete(path);
            ScreenCapture.CaptureScreenshot(path);
            long size = -1;
            request.Pending = r =>
            {
                if (!File.Exists(path)) return Time.frameCount - r.StartFrame > 600 ? throw new DriverError("screen.capture wrote no file") : false;
                long now = new FileInfo(path).Length;
                if (now == 0 || now != size) { size = now; return false; }
                r.State = new JObject { ["path"] = path, ["w"] = Screen.width, ["h"] = Screen.height };
                return true;
            };
        }
    }
}
