using OniMoono.Engine.Platform;
using System;
using System.Collections.Generic;
using OniMoono.Desktop;
using UnityEngine;

namespace OniMoono.Host
{
    /// `--input FRAME:ACTION[:ARGS]` (development and test runs): input delivered through the
    /// same feed as the window's, before the given frame. Positions are screen pixels with the
    /// origin at the top left (as in captures). Actions: `move:X,Y`, `click:X,Y[,BUTTON]` (button
    /// down, up the next frame; left unless BUTTON is 1 right or 2 middle), `press:X,Y[,BUTTON]` /
    /// `release:X,Y[,BUTTON]` (for drags), `key:KEYCODE` (down, up the next frame),
    /// `keydown:KEYCODE` / `keyup:KEYCODE` (held keys, e.g. modifiers for chords), `scroll:DX,DY` (wheel
    /// steps at the pointer), `text:STRING`, `burst:X,Y[,N]` (N clicks delivered before one frame,
    /// the last held a frame), `resize:W,H[,MODE]` (Screen.SetResolution, windowed unless MODE is exclusive, borderless
    /// or maximized), `stall:MS` (the main thread held up MS milliseconds). A run
    /// with a script ignores the window's own mouse and keyboard, so it is reproducible.
    sealed class ScriptedInput
    {
        readonly SortedDictionary<int, List<Action>> byFrame = new();
        readonly InputStateFeed feed = new();

        /// A script is present: the run ignores the window's own input.
        public bool Any => byFrame.Count > 0;

        public void Add(string spec)
        {
            var parts = spec.Split(':', 3);
            if (parts.Length < 2) throw new ArgumentException($"--input expects FRAME:ACTION[:ARGS], got '{spec}'");
            int frame = int.Parse(parts[0]);
            string arg = parts.Length > 2 ? parts[2] : "";
            switch (parts[1])
            {
                case "move": At(frame, () => Move(Point(arg))); break;
                case "click":
                    At(frame, () => { Move(Point(arg)); feed.MouseDown(Button(arg), 0, Gui(Point(arg))); });
                    At(frame + 1, () => feed.MouseUp(Button(arg), 0, Gui(Point(arg))));
                    break;
                case "press": At(frame, () => { Move(Point(arg)); feed.MouseDown(Button(arg), 0, Gui(Point(arg))); }); break;
                case "release": At(frame, () => { Move(Point(arg)); feed.MouseUp(Button(arg), 0, Gui(Point(arg))); }); break;
                case "key":
                    var key = Enum.Parse<KeyCode>(arg);
                    At(frame, () => feed.KeyDown(key, Modifiers(), repeat: false, Vector2.zero));
                    At(frame + 1, () => feed.KeyUp(key, Modifiers(), Vector2.zero));
                    break;
                case "keydown":
                    var down = Enum.Parse<KeyCode>(arg);
                    At(frame, () => { held.Add(down); feed.KeyDown(down, Modifiers(), repeat: false, Vector2.zero); });
                    break;
                case "keyup":
                    var up = Enum.Parse<KeyCode>(arg);
                    At(frame, () => { held.Remove(up); feed.KeyUp(up, Modifiers(), Vector2.zero); });
                    break;
                case "scroll":
                    var delta = Point(arg);
                    At(frame, () => feed.Scrolled(delta, Gui(pointer)));
                    break;
                case "text":
                    At(frame, () => { foreach (var r in arg.EnumerateRunes()) feed.Text((uint)r.Value, 0); });
                    break;
                case "burst":
                {
                    // N clicks (down and up) delivered together before one frame, as a fast
                    // double-click lands at a low frame rate; then held down for a frame and released.
                    var at = arg.Split(',');
                    var point = Point(at[0] + "," + at[1]);
                    int n = at.Length > 2 ? int.Parse(at[2]) : 2;
                    At(frame, () =>
                    {
                        Move(point);
                        for (int i = 0; i < n; i++)
                        {
                            feed.MouseDown(0, 0, Gui(point));
                            if (i < n - 1) feed.MouseUp(0, 0, Gui(point));
                        }
                    });
                    At(frame + 1, () => feed.MouseUp(0, 0, Gui(point)));
                    break;
                }
                case "resize":
                {
                    // The window to W×H, as the game's resolution options do (Screen.SetResolution),
                    // windowed unless a mode follows (exclusive, borderless, maximized).
                    var size = Point(arg);
                    var mode = arg.Split(',') is { Length: > 2 } p ? p[2] switch
                    {
                        "exclusive" => FullScreenMode.ExclusiveFullScreen,
                        "borderless" => FullScreenMode.FullScreenWindow,
                        "maximized" => FullScreenMode.MaximizedWindow,
                        "windowed" => FullScreenMode.Windowed,
                        var m => throw new ArgumentException($"Unknown --input resize mode '{m}'"),
                    } : FullScreenMode.Windowed;
                    At(frame, () => Screen.SetResolution((int)size.x, (int)size.y, mode));
                    break;
                }
                // The main thread held up for MS milliseconds before the frame, as an autosave or
                // a load hitch holds it (repeats and the presenters' pacing; test runs).
                case "stall": At(frame, () => System.Threading.Thread.Sleep(int.Parse(arg))); break;
                default: throw new ArgumentException($"Unknown --input action '{parts[1]}'");
            }
        }

        readonly HashSet<KeyCode> held = new();

        /// Event modifiers of the keys the script holds down.
        EventModifiers Modifiers()
        {
            EventModifiers m = 0;
            if (held.Contains(KeyCode.LeftShift) || held.Contains(KeyCode.RightShift)) m |= EventModifiers.Shift;
            if (held.Contains(KeyCode.LeftControl) || held.Contains(KeyCode.RightControl)) m |= EventModifiers.Control;
            if (held.Contains(KeyCode.LeftAlt) || held.Contains(KeyCode.RightAlt)) m |= EventModifiers.Alt;
            return m;
        }

        void At(int frame, Action action)
        {
            if (!byFrame.TryGetValue(frame, out var list)) byFrame[frame] = list = new List<Action>();
            list.Add(action);
        }

        public void Apply(int frame)
        {
            if (byFrame.TryGetValue(frame, out var list))
                foreach (var a in list) a();
        }

        /// The button of a `click`/`press`/`release`: an optional third component (0 left,
        /// 1 right, 2 middle), left when absent.
        static int Button(string arg)
        {
            var xyb = arg.Split(',');
            return xyb.Length > 2 ? int.Parse(xyb[2]) : 0;
        }

        static Vector2 Point(string arg)
        {
            var xy = arg.Split(',');
            return new Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        static float Height => OniMoono.Engine.Platform.PlatformServices.Current.FramebufferHeight;

        /// Top-left pixel position → the feed's screen position (origin bottom left).
        void Move(Vector2 topLeft)
        {
            pointer = topLeft;
            feed.MouseMoved(PointerPixels.Screen(topLeft, Height));
        }

        /// Where the script last put the pointer (top-left origin).
        Vector2 pointer;

        /// The GUI position of the pixel the script names (top left origin).
        static Vector2 Gui(Vector2 topLeft) => PointerPixels.Gui(PointerPixels.Screen(topLeft, Height), Height);
    }
}
