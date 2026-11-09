// GUILayout ("PROBE ... GL ..."), at the main menu. With PROBE_SCENARIO=guilayout only this
// probe runs. Four parts, in this order (PROBE_GL_PART=size, wsize, order or mm runs one;
// PROBE_GL_PART=wrap runs a fifth, GuiLayoutWrap.cs, which a full run leaves out):
//
// - size: the rects GUILayout gives with the default skin. Every control kind with one and
//   several lines, style copies with other font sizes, padding and margins, sequences of
//   entries (the spacing between them), groups and areas with and without a style, scroll
//   views with and without overflow, and the content of GUILayout and GUI windows. Each value
//   is logged beside the style's metrics (padding, margin, border, overflow, font, line
//   height) and its CalcSize / CalcHeight.
// - wsize: GUILayout.Window's returned rect against the rect passed in: content smaller and
//   larger, Width / Height / MinHeight / MaxHeight / ExpandHeight / MinWidth options, the
//   result fed back or not, content and given size changing between frames.
// - order: the order of every OnGUI call and window function per event, with windows declared
//   in a frame and declared later, two behaviours, BringWindowToFront / BringWindowToBack /
//   FocusWindow, and clicks (through XTest) on one window, on an overlap of all and outside.
// - mm: what GUILayout does when an event's calls differ from those of the Layout before it.
//   Bodies add, remove or regroup entries, in OnGUI, in a scroll view, in a GUI.Window, in a
//   scroll view in a GUI.Window and in a GUILayout.Window, during a Repaint, a MouseDown, a
//   MouseDown after Use() (type Used) or a MouseUp; and a button whose handler changes what
//   follows (as Customize Duplicant traits' SAVE and DEL do). Each call's rect or exception
//   is logged; with "c" cases every call catches, with "u" cases the exception is left to
//   the player. What the player logs is recorded too.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class GuiLayout : MonoBehaviour
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal static string Phase = "none";
        internal static string Case = "-";
        internal static bool Recording;
        internal static readonly Dictionary<string, Rect> Rects = new Dictionary<string, Rect>();
        bool started;
        float sceneStart = -1f;

        internal static void L(string s) => Rec.Log("GL " + s);
        internal static string F(float v) => v.ToString("R", Inv);
        internal static string V(Vector2 v) => F(v.x) + "," + F(v.y);
        internal static string R(Rect r) => F(r.x) + "," + F(r.y) + "," + F(r.width) + "," + F(r.height);
        internal static string O(RectOffset o) => o == null ? "null" : o.left + "," + o.right + "," + o.top + "," + o.bottom;
        internal static string Msg(string s) => s == null ? "null" : s.Replace("\r", "").Replace("\n", "\\n").Replace(";", ",");

        internal static void Keep(string name, Rect r)
        {
            if (Event.current.type == EventType.Repaint) Rects[name] = GUIUtility.GUIToScreenRect(r);
        }

        void Awake()
        {
            gameObject.AddComponent<GlGui>();
            gameObject.AddComponent<GlGui2>();
            gameObject.AddComponent<GlWrap>();
            Application.logMessageReceived += OnLog;
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (!Recording || condition == null || condition.StartsWith("PROBE")) return;
            string top = "";
            if (!string.IsNullOrEmpty(stack))
            {
                var lines = stack.Split('\n');
                top = Msg(lines[0].Trim());
            }
            L("log " + Case + " " + type + " " + Msg(condition) + " @ " + top);
        }

        void Update()
        {
            if (started || SceneManager.GetActiveScene().name != "frontend") return;
            if (sceneStart < 0f) sceneStart = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - sceneStart < 10f) return;
            started = true;
            StartCoroutine(Run());
        }

        // ----- XTest (as Tooltip.cs) -----

        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern IntPtr XDefaultRootWindow(IntPtr dpy);
        [DllImport("libX11.so.6")] static extern int XQueryTree(IntPtr dpy, IntPtr w, out IntPtr root, out IntPtr parent, out IntPtr children, out uint n);
        [DllImport("libX11.so.6")] static extern int XGetWindowAttributes(IntPtr dpy, IntPtr w, int[] attributes);
        [DllImport("libX11.so.6")] static extern int XSetInputFocus(IntPtr dpy, IntPtr w, int revert, IntPtr time);
        [DllImport("libX11.so.6")] static extern int XFlush(IntPtr dpy);
        [DllImport("libX11.so.6")] static extern int XSync(IntPtr dpy, int discard);
        [DllImport("libX11.so.6")] static extern int XFree(IntPtr p);
        [DllImport("libXtst.so.6")] static extern int XTestFakeMotionEvent(IntPtr dpy, int screen, int x, int y, IntPtr delay);
        [DllImport("libXtst.so.6")] static extern int XTestFakeButtonEvent(IntPtr dpy, uint button, int down, IntPtr delay);

        static IntPtr dpy;
        static int offX, offY;

        static bool XOpen()
        {
            try
            {
                dpy = XOpenDisplay(IntPtr.Zero);
                if (dpy == IntPtr.Zero) { L("x no display"); return false; }
                IntPtr r, p, children; uint n;
                XQueryTree(dpy, XDefaultRootWindow(dpy), out r, out p, out children, out n);
                IntPtr best = IntPtr.Zero; long area = 0;
                var attrs = new int[64];
                for (int i = 0; i < n; i++)
                {
                    var w = Marshal.ReadIntPtr(children, i * IntPtr.Size);
                    if (XGetWindowAttributes(dpy, w, attrs) == 0) continue;
                    long a = (long)attrs[2] * attrs[3];
                    if (attrs[23] == 2 && a > area) { best = w; area = a; offX = attrs[0]; offY = attrs[1]; }
                }
                if (n > 0) XFree(children);
                if (best == IntPtr.Zero) { L("x no window"); return false; }
                XSetInputFocus(dpy, best, 1, IntPtr.Zero);
                XSync(dpy, 0);
                L("x window " + best.ToInt64() + " at " + offX + "," + offY + " area " + area);
                return true;
            }
            catch (Exception e) { L("x unavailable " + e.GetType().Name + " " + e.Message); return false; }
        }

        static void Move(Vector2 gui)
        {
            XTestFakeMotionEvent(dpy, -1, offX + Mathf.RoundToInt(gui.x), offY + Mathf.RoundToInt(gui.y), IntPtr.Zero);
            XFlush(dpy);
        }

        static void Button(bool down)
        {
            XTestFakeButtonEvent(dpy, 1, down ? 1 : 0, IntPtr.Zero);
            XFlush(dpy);
        }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static IEnumerator Click(Vector2 p)
        {
            Move(p);
            yield return Frames(3);
            Button(true);
            yield return Frames(3);
            Button(false);
            yield return Frames(3);
        }

        IEnumerator Run()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null) es.enabled = false; // uGUI must not take the clicks
            string part = Environment.GetEnvironmentVariable("PROBE_GL_PART") ?? "";
            bool x = XOpen();
            if (x) { Move(new Vector2(1270f, 710f)); yield return Frames(4); }
            L("start screen=" + Screen.width + "x" + Screen.height + " input=" + V(Input.mousePosition));
            if (part == "" || part == "size") yield return Sizes();
            if (part == "" || part == "wsize") yield return WindowSizes();
            if (x && (part == "" || part == "order")) yield return Order();
            if (x && (part == "" || part == "mm")) yield return Mismatch();
            if (part == "wrap") yield return Wraps();
            Phase = "none";
            L("done");
        }

        // ----- size -----

        IEnumerator Sizes()
        {
            Phase = "size";
            yield return Frames(3);
            Recording = true;
            GlGui.SizeLog = true;
            yield return Frames(1);
            GlGui.SizeLog = false;
            yield return Frames(1);
            Recording = false;
            Phase = "none";
            yield return Frames(2);
        }

        // ----- wrap (GuiLayoutWrap.cs) -----

        IEnumerator Wraps()
        {
            Phase = "wrap";
            yield return Frames(3);
            Recording = true;
            GlWrap.Log = true;
            yield return Frames(1);
            GlWrap.Log = false;
            Recording = false;
            Phase = "none";
            yield return Frames(2);
        }

        // ----- wsize -----

        IEnumerator WindowSizes()
        {
            Phase = "wsize";
            GlGui.WsFrame = 0;
            Recording = true;
            for (int i = 0; i < 12; i++) { Case = "ws" + i; GlGui.WsFrame = i; yield return null; }
            Recording = false;
            Phase = "none";
            yield return Frames(2);
        }

        // ----- order -----

        IEnumerator Order()
        {
            Phase = "order";
            GlGui.OrderSet = "a";
            Case = "o-warm";
            yield return Frames(4);
            Recording = true;
            Case = "o1-first"; GlGui.OrderSet = "a"; yield return Frames(2);
            Case = "o2-add404"; GlGui.OrderSet = "b"; yield return Frames(3);
            Case = "o3-add400first"; GlGui.OrderSet = "c"; yield return Frames(3);
            Case = "o4-front403"; GlGui.OrderCall = "front403"; yield return Frames(3);
            Case = "o5-back402"; GlGui.OrderCall = "back402"; yield return Frames(3);
            Case = "o6-focus401"; GlGui.OrderCall = "focus401"; yield return Frames(3);
            Case = "o6b-focus405"; GlGui.OrderCall = "focus405"; yield return Frames(3);
            Case = "o7-front404-repaint"; GlGui.OrderCall = "front404@Repaint"; yield return Frames(3);
            Case = "o8-front-update";
            try { GUI.BringWindowToFront(402); L("o8 update front ok"); }
            catch (Exception e) { L("o8 update front !" + e.GetType().Name + ":" + Msg(e.Message)); }
            yield return Frames(3);
            Case = "o9-click404only"; yield return Click(new Vector2(500f, 195f)); yield return Frames(2);
            Case = "o10-click400only"; yield return Click(new Vector2(70f, 70f)); yield return Frames(2);
            Case = "o11-clickall"; yield return Click(new Vector2(300f, 260f)); yield return Frames(2);
            Case = "o12-clickoutside"; yield return Click(new Vector2(900f, 650f)); yield return Frames(2);
            Case = "o13-skip402"; GlGui.OrderSkip = 402; yield return Frames(2);
            Case = "o14-again402"; GlGui.OrderSkip = 0; yield return Frames(3);
            Case = "o15-new406"; GlGui.OrderSet = "d"; yield return Frames(3);
            Case = "o16-unfocus"; GlGui.OrderCall = "unfocus"; yield return Frames(3);
            Recording = false;
            Phase = "none";
            yield return Frames(2);
        }

        // ----- mm -----

        static readonly string[] RepaintBodies = { "insert", "append", "remove", "removeEnd", "leafToGroup", "groupToLeaf", "vertToHoriz",
                                                    "groupMore", "groupFewer", "leafToScroll", "scrollToLeaf", "content", "getRect",
                                                    "spaceInsert", "areaInsert", "flexInsert" };
        static readonly string[] ClickBodies = { "insert", "remove", "leafToGroup", "groupToLeaf", "leafToScroll", "content" };
        static readonly string[] Contexts = { "root", "rootscroll", "win", "winscroll", "lwin" };

        IEnumerator MmCase(string body, string ctx, string target, bool caught)
        {
            Phase = "mm";
            GlGui.Body = body; GlGui.Ctx = ctx; GlGui.Target = target; GlGui.Catch = caught;
            GlGui.Flag = false; GlGui.Count = 0; GlGui.Armed = false;
            Case = body + "." + ctx + "." + target + "." + (caught ? "c" : "u");
            yield return Frames(3);
            Recording = true;
            yield return Frames(1);
            if (target == "Repaint")
            {
                GlGui.Armed = true;
                yield return Frames(1);
            }
            else if (target == "handler")
            {
                yield return Click(Centre("B"));
            }
            else if (target == "mod")
            {
                yield return Click(Centre("SAVE"));
                yield return Frames(2);
                L("mod rects SAVE=" + R(Rects["SAVE"]) + " DEL=" + (Rects.ContainsKey("DEL0") ? R(Rects["DEL0"]) : "none"));
                if (Rects.ContainsKey("DEL0")) yield return Click(Centre("DEL0"));
            }
            else
            {
                Vector2 p = ctx.StartsWith("root") ? new Vector2(640f, 680f) : ctx == "lwin" ? new Vector2(1100f, 90f) : new Vector2(750f, 90f);
                Move(p);
                yield return Frames(3);
                GlGui.Armed = true;
                Button(true);
                yield return Frames(3);
                Button(false);
                yield return Frames(3);
            }
            GlGui.Armed = false;
            yield return Frames(2);
            Recording = false;
            Rects.Remove("B"); Rects.Remove("SAVE"); Rects.Remove("DEL0");
        }

        static Vector2 Centre(string name) => Rects.TryGetValue(name, out var r) ? r.center : new Vector2(1270f, 710f);

        IEnumerator Mismatch()
        {
            foreach (var caught in new[] { false, true })
                foreach (var ctx in Contexts)
                    foreach (var body in RepaintBodies)
                        yield return MmCase(body, ctx, "Repaint", caught);
            foreach (var caught in new[] { false, true })
                foreach (var target in new[] { "MouseDown", "Used", "MouseUp" })
                    foreach (var ctx in new[] { "root", "win", "winscroll", "lwin" })
                        foreach (var body in ClickBodies)
                            yield return MmCase(body, ctx, target, caught);
            foreach (var caught in new[] { false, true })
                foreach (var ctx in Contexts)
                {
                    foreach (var body in new[] { "handlerGroup", "handlerLeaf", "handlerRemove" })
                        yield return MmCase(body, ctx, "handler", caught);
                    yield return MmCase("mod", ctx, "mod", caught);
                }
            Phase = "none";
        }
    }

    // One OnGUI call or window function's line.
    internal class GlLine
    {
        readonly StringBuilder sb = new StringBuilder();
        bool ended;
        public GlLine(string who)
        {
            var e = Event.current;
            sb.Append("mm ").Append(GuiLayout.Case).Append(' ').Append(who).Append(' ').Append(e.type).Append(" raw=").Append(e.rawType)
              .Append(" hot=").Append(GUIUtility.hotControl).Append(" ::");
        }
        public void Add(string s) => sb.Append(' ').Append(s).Append(" ;");
        public void End(bool abort)
        {
            if (ended) return;
            ended = true;
            sb.Append(abort ? " :: abort" : " :: end");
            if (GuiLayout.Recording) GuiLayout.L(sb.ToString());
        }
    }

    // The behaviour that draws.
    public class GlGui : MonoBehaviour
    {
        internal static bool SizeLog;
        internal static int WsFrame;
        internal static string OrderSet = "a", OrderCall = "";
        internal static int OrderSkip;
        internal static string Body = "", Ctx = "", Target = "";
        internal static bool Catch, Flag, Armed;
        internal static int Count;

        static string R(Rect r) => GuiLayout.R(r);
        static string F(float v) => GuiLayout.F(v);

        void OnGUI()
        {
            switch (GuiLayout.Phase)
            {
                case "size": SizeGui(); break;
                case "wsize": WsGui(); break;
                case "order": OrderGui("gui"); break;
                case "mm": MmGui(); break;
            }
        }

        // ================= size =================

        static void Size(string name, GUIStyle style, GUIContent content, System.Action draw, float areaW = 400f, float areaH = 400f, GUIStyle areaStyle = null)
        {
            if (areaStyle == null) GUILayout.BeginArea(new Rect(20f, 20f, areaW, areaH));
            else GUILayout.BeginArea(new Rect(20f, 20f, areaW, areaH), areaStyle);
            string r;
            try { draw(); r = R(GUILayoutUtility.GetLastRect()); }
            catch (Exception e) { r = "!" + e.GetType().Name + ":" + GuiLayout.Msg(e.Message); }
            GUILayout.EndArea();
            if (!SizeLog || Event.current.type != EventType.Repaint) return;
            var sb = new StringBuilder("size " + name + " r=" + r);
            if (style != null && content != null)
            {
                var cs = style.CalcSize(content);
                sb.Append(" calc=").Append(GuiLayout.V(cs));
                float w = 0f;
                if (r[0] != '!') w = float.Parse(r.Split(',')[2], CultureInfo.InvariantCulture);
                sb.Append(" ch=").Append(F(style.CalcHeight(content, w)));
                style.CalcMinMaxWidth(content, out var mn, out var mx);
                sb.Append(" mm=").Append(F(mn)).Append(',').Append(F(mx));
            }
            GuiLayout.L(sb.ToString());
        }

        static void StyleLog(string name, GUIStyle s)
        {
            var f = s.font;
            GuiLayout.L("style " + name + " name='" + s.name + "' font=" + (f == null ? "null" : f.name) + " fs=" + s.fontSize + " fst=" + s.fontStyle
                + " lh=" + F(s.lineHeight) + " pad=" + GuiLayout.O(s.padding) + " mar=" + GuiLayout.O(s.margin) + " bor=" + GuiLayout.O(s.border)
                + " ovf=" + GuiLayout.O(s.overflow) + " fw=" + F(s.fixedWidth) + " fh=" + F(s.fixedHeight) + " sw=" + s.stretchWidth
                + " sh=" + s.stretchHeight + " ww=" + s.wordWrap + " al=" + s.alignment + " rt=" + s.richText + " clip=" + s.clipping
                + " ip=" + s.imagePosition + " co=" + GuiLayout.V(s.contentOffset));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static string FontMetrics(Font f) =>
            f == null ? "null" : f.name + " fs=" + f.fontSize + " lh=" + F(f.lineHeight) + " asc=" + F(f.ascent) + " dyn=" + f.dynamic;

        static GUIStyle Copy(GUIStyle s, System.Action<GUIStyle> f) { var c = new GUIStyle(s); f(c); return c; }

        static Vector2 sv;


        void SizeGui()
        {
            var skin = GUI.skin;
            bool log = SizeLog && Event.current.type == EventType.Repaint;
            if (log)
            {
                string fm;
                try { fm = FontMetrics(skin.font); } catch (Exception e) { fm = skin.font.name + " !" + e.GetType().Name; }
                GuiLayout.L("skin name=" + skin.name + " font=" + fm);
                StyleLog("label", skin.label); StyleLog("button", skin.button); StyleLog("toggle", skin.toggle);
                StyleLog("textField", skin.textField); StyleLog("textArea", skin.textArea); StyleLog("box", skin.box);
                StyleLog("window", skin.window); StyleLog("scrollView", skin.scrollView);
                StyleLog("verticalScrollbar", skin.verticalScrollbar); StyleLog("verticalScrollbarThumb", skin.verticalScrollbarThumb);
                StyleLog("verticalScrollbarUpButton", skin.verticalScrollbarUpButton); StyleLog("verticalScrollbarDownButton", skin.verticalScrollbarDownButton);
                StyleLog("horizontalScrollbar", skin.horizontalScrollbar); StyleLog("horizontalScrollbarThumb", skin.horizontalScrollbarThumb);
                StyleLog("horizontalScrollbarLeftButton", skin.horizontalScrollbarLeftButton); StyleLog("horizontalScrollbarRightButton", skin.horizontalScrollbarRightButton);
                StyleLog("none", GUIStyle.none);
            }

            // At the root: the first entries of OnGUI's own layout.
            GUILayout.Label("root0"); var r0 = GUILayoutUtility.GetLastRect();
            GUILayout.Button("root1"); var r1 = GUILayoutUtility.GetLastRect();
            GUILayout.Label("root2", GUILayout.Width(300f)); var r2 = GUILayoutUtility.GetLastRect();
            GUILayout.Label("root3"); var r3 = GUILayoutUtility.GetLastRect();
            if (log) GuiLayout.L("size root.label r=" + R(r0) + " root.button r=" + R(r1) + " root.w300 r=" + R(r2) + " root.label2 r=" + R(r3));

            var texts = new[] { ("x", "x"), ("hello", "Hello world"), ("2l", "a\nb"), ("3l", "a\nb\nc"), ("empty", "") };
            foreach (var (tn, t) in texts)
            {
                var c = new GUIContent(t);
                Size("label." + tn, skin.label, c, () => GUILayout.Label(t));
                Size("button." + tn, skin.button, c, () => GUILayout.Button(t));
                Size("toggle." + tn, skin.toggle, c, () => GUILayout.Toggle(false, t));
                Size("textField." + tn, skin.textField, c, () => GUILayout.TextField(t));
                Size("textArea." + tn, skin.textArea, c, () => GUILayout.TextArea(t));
                Size("box." + tn, skin.box, c, () => GUILayout.Box(t));
            }
            var gx = new GUIContent("x");
            var rich = Copy(skin.label, s => s.richText = true);
            Size("label.rich", rich, new GUIContent("<b>x</b>"), () => GUILayout.Label("<b>x</b>", rich));
            foreach (var fs in new[] { 8, 10, 12, 14, 16, 20, 30 })
            {
                var st = Copy(skin.label, s => s.fontSize = fs);
                Size("label.fs" + fs, st, gx, () => GUILayout.Label("x", st));
                Size("label.fs" + fs + ".2l", st, new GUIContent("a\nb"), () => GUILayout.Label("a\nb", st));
                var bt = Copy(skin.button, s => s.fontSize = fs);
                Size("button.fs" + fs, bt, gx, () => GUILayout.Button("x", bt));
            }
            var bold = Copy(skin.label, s => s.fontStyle = FontStyle.Bold);
            Size("label.bold", bold, gx, () => GUILayout.Label("x", bold));
            foreach (var (pn, p) in new[] { ("p0", new RectOffset(0, 0, 0, 0)), ("p1234", new RectOffset(1, 2, 3, 4)), ("p10_20_5_15", new RectOffset(10, 20, 5, 15)) })
            {
                var st = Copy(skin.label, s => s.padding = p);
                Size("label.pad." + pn, st, gx, () => GUILayout.Label("x", st));
                var bt = Copy(skin.button, s => s.padding = p);
                Size("button.pad." + pn, bt, gx, () => GUILayout.Button("x", bt));
            }
            foreach (var (mn, m) in new[] { ("m0", new RectOffset(0, 0, 0, 0)), ("m5678", new RectOffset(5, 6, 7, 8)) })
            {
                var st = Copy(skin.label, s => s.margin = m);
                Size("label.mar." + mn, st, gx, () => GUILayout.Label("x", st));
                Size("label.mar." + mn + ".then", st, gx, () => { GUILayout.Label("x", st); GUILayout.Label("x", st); });
            }
            var bord = Copy(skin.label, s => s.border = new RectOffset(9, 9, 9, 9));
            Size("label.border9", bord, gx, () => GUILayout.Label("x", bord));
            var ovf = Copy(skin.label, s => s.overflow = new RectOffset(9, 9, 9, 9));
            Size("label.overflow9", ovf, gx, () => GUILayout.Label("x", ovf));
            var fixh = Copy(skin.label, s => s.fixedHeight = 50f);
            Size("label.fixedHeight50", fixh, gx, () => GUILayout.Label("x", fixh));
            var wrap = Copy(skin.label, s => s.wordWrap = true);
            var longText = "The quick brown fox jumps over the lazy dog again and again";
            Size("label.wrap.area100", wrap, new GUIContent(longText), () => GUILayout.Label(longText, wrap), 100f);
            Size("label.wrap.w150", wrap, new GUIContent(longText), () => GUILayout.Label(longText, wrap, GUILayout.Width(150f)));
            Size("label.nowrap.area100", skin.label, new GUIContent(longText), () => GUILayout.Label(longText), 100f);
            Size("label.height40", skin.label, gx, () => GUILayout.Label("x", GUILayout.Height(40f)));
            Size("label.width50", skin.label, gx, () => GUILayout.Label("x", GUILayout.Width(50f)));
            Size("button.width50", skin.button, gx, () => GUILayout.Button("x", GUILayout.Width(50f)));
            Size("label.expandW.false", skin.label, gx, () => GUILayout.Label("x", GUILayout.ExpandWidth(false)));
            Size("button.expandW.false", skin.button, gx, () => GUILayout.Button("x", GUILayout.ExpandWidth(false)));
            Size("box.h2.expand", skin.box, GUIContent.none, () => GUILayout.Box("", GUILayout.Height(2f), GUILayout.ExpandWidth(true)));
            Size("getRect.20x10", null, null, () => GUILayoutUtility.GetRect(20f, 10f));
            Size("getRect.20x10.label", null, null, () => GUILayoutUtility.GetRect(20f, 10f, skin.label));

            // Sequences: the spacing between entries.
            Seq("seq.labels", () => { L3("a", () => GUILayout.Label("a")); L3("b", () => GUILayout.Label("b")); L3("c", () => GUILayout.Label("c")); });
            Seq("seq.buttons", () => { L3("a", () => GUILayout.Button("a")); L3("b", () => GUILayout.Button("b")); L3("c", () => GUILayout.Button("c")); });
            Seq("seq.mixed", () =>
            {
                L3("label", () => GUILayout.Label("a")); L3("button", () => GUILayout.Button("b")); L3("label2", () => GUILayout.Label("c"));
                L3("toggle", () => GUILayout.Toggle(true, "t")); L3("textField", () => GUILayout.TextField("f")); L3("box", () => GUILayout.Box("x"));
                L3("textArea", () => GUILayout.TextArea("a")); L3("label3", () => GUILayout.Label("e"));
            });
            var mA = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 0, 10));
            var mB = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 3, 0));
            var mC = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 20, 2));
            var mD = Copy(skin.label, s => s.margin = new RectOffset(7, 3, 0, 0));
            Seq("seq.margins", () =>
            {
                L3("mA", () => GUILayout.Label("a", mA)); L3("mB", () => GUILayout.Label("b", mB)); L3("mC", () => GUILayout.Label("c", mC));
                L3("mD", () => GUILayout.Label("d", mD)); L3("mA2", () => GUILayout.Label("a", mA)); L3("mC2", () => GUILayout.Label("c", mC));
            });
            Seq("seq.space", () => { L3("a", () => GUILayout.Label("a")); L3("sp", () => GUILayout.Space(10f)); L3("b", () => GUILayout.Label("b")); L3("sp0", () => GUILayout.Space(0f)); L3("c", () => GUILayout.Label("c")); });
            Seq("seq.flex", () => { L3("a", () => GUILayout.Label("a")); L3("flex", () => GUILayout.FlexibleSpace()); L3("b", () => GUILayout.Label("b")); }, 200f);
            Seq("seq.horizontal", () =>
            {
                L3("bh", () => GUILayout.BeginHorizontal());
                L3("a", () => GUILayout.Label("a")); L3("b", () => GUILayout.Button("b")); L3("c", () => GUILayout.Label("c"));
                L3("mD", () => GUILayout.Label("d", mD)); L3("e", () => GUILayout.Button("e", GUILayout.Width(40f)));
                L3("eh", () => GUILayout.EndHorizontal());
                L3("after", () => GUILayout.Label("after"));
            });
            Seq("seq.horizontal.flex", () =>
            {
                L3("bh", () => GUILayout.BeginHorizontal());
                L3("a", () => GUILayout.Label("a", GUILayout.Width(110f))); L3("m", () => GUILayout.Button("-", GUILayout.Width(25f)));
                L3("v", () => GUILayout.Label("5", GUILayout.Width(20f))); L3("p", () => GUILayout.Button("+", GUILayout.Width(25f)));
                L3("flex", () => GUILayout.FlexibleSpace()); L3("x", () => GUILayout.Button("X", GUILayout.Width(30f)));
                L3("eh", () => GUILayout.EndHorizontal());
            }, 330f);
            Seq("seq.horizontal.row", () =>
            {
                L3("bh", () => GUILayout.BeginHorizontal());
                L3("name", () => GUILayout.Label("MyPreset", GUILayout.Width(170f))); L3("load", () => GUILayout.Button("LOAD", GUILayout.Width(60f)));
                L3("del", () => GUILayout.Button("DEL", GUILayout.Width(40f)));
                L3("eh", () => GUILayout.EndHorizontal());
                L3("bh2", () => GUILayout.BeginHorizontal());
                L3("tf", () => GUILayout.TextField("MyPreset", GUILayout.Width(220f))); L3("save", () => GUILayout.Button("SAVE", GUILayout.Width(90f)));
                L3("eh2", () => GUILayout.EndHorizontal());
            }, 330f);

            // Groups and areas.
            var pad = new GUIStyle { padding = new RectOffset(5, 6, 7, 8), margin = new RectOffset(1, 2, 3, 4) };
            Seq("grp.vertical", () => { L3("bv", () => GUILayout.BeginVertical()); L3("a", () => GUILayout.Label("a")); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Label("b")); });
            Seq("grp.vertical.box", () => { L3("bv", () => GUILayout.BeginVertical(skin.box)); L3("a", () => GUILayout.Label("a")); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Label("b")); });
            Seq("grp.vertical.box.button", () => { L3("bv", () => GUILayout.BeginVertical(skin.box)); L3("a", () => GUILayout.Button("a")); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Button("b")); });
            Seq("grp.horizontal.box", () => { L3("bh", () => GUILayout.BeginHorizontal(skin.box)); L3("a", () => GUILayout.Label("a")); L3("b", () => GUILayout.Label("b")); L3("eh", () => GUILayout.EndHorizontal()); L3("after", () => GUILayout.Label("c")); });
            Seq("grp.vertical.pad", () => { L3("bv", () => GUILayout.BeginVertical(pad)); L3("a", () => GUILayout.Label("a")); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Label("b")); });
            Seq("grp.vertical.nested", () =>
            {
                L3("bv", () => GUILayout.BeginVertical(skin.box)); L3("bv2", () => GUILayout.BeginVertical(skin.box)); L3("a", () => GUILayout.Label("a"));
                L3("ev2", () => GUILayout.EndVertical()); L3("ev", () => GUILayout.EndVertical());
            });
            Seq("grp.label.then.vertical.box", () => { L3("a", () => GUILayout.Label("a")); L3("bv", () => GUILayout.BeginVertical(skin.box)); L3("b", () => GUILayout.Button("b")); L3("ev", () => GUILayout.EndVertical()); });
            Seq("area.none", () => L3("a", () => GUILayout.Label("a")));
            Seq("area.box", () => L3("a", () => GUILayout.Label("a")), 400f, skin.box);
            Seq("area.pad", () => L3("a", () => GUILayout.Label("a")), 400f, pad);
            Seq("area.window", () => L3("a", () => GUILayout.Label("a")), 400f, skin.window);
            Seq("area.button.first", () => L3("a", () => GUILayout.Button("a")));
            Seq("area.nested", () =>
            {
                L3("ba", () => GUILayout.BeginArea(new Rect(10f, 30f, 100f, 50f)));
                L3("a", () => GUILayout.Label("a"));
                L3("ea", () => GUILayout.EndArea());
                L3("b", () => GUILayout.Label("b"));
            });

            // Margins against a group's padding, and groups without a style.
            var pad2 = new GUIStyle { padding = new RectOffset(2, 2, 2, 2) };
            var m8 = Copy(skin.label, s => s.margin = new RectOffset(8, 8, 8, 8));
            var m0 = Copy(skin.label, s => s.margin = new RectOffset(0, 0, 0, 0));
            Seq("area.pad2", () => { L3("a", () => GUILayout.Label("a")); L3("b", () => GUILayout.Label("b")); }, 400f, pad2);
            Seq("area.pad2.m8", () => { L3("a", () => GUILayout.Label("a", m8)); L3("b", () => GUILayout.Label("b")); }, 400f, pad2);
            Seq("area.box.m8", () => { L3("a", () => GUILayout.Label("a", m8)); L3("b", () => GUILayout.Label("b")); }, 400f, skin.box);
            Seq("area.m8", () => { L3("a", () => GUILayout.Label("a", m8)); L3("b", () => GUILayout.Label("b")); });
            Seq("area.m0", () => { L3("a", () => GUILayout.Label("a", m0)); L3("b", () => GUILayout.Label("b")); L3("c", () => GUILayout.Label("c", m8)); });
            Seq("grp.vertical.pad2", () => { L3("bv", () => GUILayout.BeginVertical(pad2)); L3("a", () => GUILayout.Label("a")); L3("b", () => GUILayout.Label("b", m8)); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Label("c")); });
            Seq("grp.vertical.mixed", () => { L3("l0", () => GUILayout.Label("0")); L3("bv", () => GUILayout.BeginVertical()); L3("a", () => GUILayout.Label("a", m0)); L3("b", () => GUILayout.Label("b")); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Label("c")); });
            Seq("grp.vertical.m8", () => { L3("l0", () => GUILayout.Label("0")); L3("bv", () => GUILayout.BeginVertical()); L3("a", () => GUILayout.Label("a", m8)); L3("b", () => GUILayout.Label("b", m8)); L3("ev", () => GUILayout.EndVertical()); L3("after", () => GUILayout.Label("c")); });
            Seq("grp.horizontal.m8", () => { L3("l0", () => GUILayout.Label("0")); L3("bh", () => GUILayout.BeginHorizontal()); L3("a", () => GUILayout.Label("a", m8)); L3("b", () => GUILayout.Label("b", m8)); L3("eh", () => GUILayout.EndHorizontal()); L3("after", () => GUILayout.Label("c")); });
            Seq("scroll.mixed.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.Height(100f))); L3("a", () => GUILayout.Label("a", m0)); L3("b", () => GUILayout.Label("b")); L3("c", () => GUILayout.Label("c", m8)); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("seq.toggles", () => { L3("a", () => GUILayout.Toggle(true, "a")); L3("b", () => GUILayout.Toggle(false, "b")); L3("c", () => GUILayout.Label("c")); });
            Seq("seq.wrap", () => { L3("a", () => GUILayout.Label("a\nb")); L3("b", () => GUILayout.Label("a\nb\nc")); L3("c", () => GUILayout.Label("a\nb\nc")); L3("d", () => GUILayout.Label("x")); });

            // Scroll views.
            Seq("scroll.small.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.Height(100f))); N("a", 2); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.large.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.Height(100f))); N("a", 12); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.wide.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.Height(100f))); L3("w", () => GUILayout.Label("wide", GUILayout.Width(600f))); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.both.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.Height(100f))); L3("w", () => GUILayout.Label("wide", GUILayout.Width(600f))); N("a", 12); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.always.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, true, true, GUILayout.Height(100f))); N("a", 2); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.small.noopt", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv)); N("a", 2); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.large.noopt", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv)); N("a", 30); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.minmax", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.MinHeight(50f), GUILayout.MaxHeight(70f))); N("a", 1); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.minmax.large", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.MinHeight(50f), GUILayout.MaxHeight(70f))); N("a", 5); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.box.h100", () => { L3("bs", () => sv = GUILayout.BeginScrollView(sv, false, false, skin.horizontalScrollbar, skin.verticalScrollbar, skin.box, GUILayout.Height(100f))); N("a", 2); L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after")); });
            Seq("scroll.rows.h80", () =>
            {
                L3("bs", () => sv = GUILayout.BeginScrollView(sv, GUILayout.Height(80f)));
                L3("bh", () => GUILayout.BeginHorizontal()); L3("name", () => GUILayout.Label("MyPreset", GUILayout.Width(170f)));
                L3("load", () => GUILayout.Button("LOAD", GUILayout.Width(60f))); L3("del", () => GUILayout.Button("DEL", GUILayout.Width(40f)));
                L3("eh", () => GUILayout.EndHorizontal());
                L3("es", () => GUILayout.EndScrollView()); L3("after", () => GUILayout.Label("after"));
            }, 330f);

            // Windows: the content's inset.
            GUILayout.Window(611, new Rect(500f, 100f, 300f, 200f), WinSize, "T611");
            GUI.Window(612, new Rect(850f, 100f, 300f, 200f), WinSize, "T612");
            GUILayout.Window(613, new Rect(500f, 400f, 300f, 200f), WinSize, "T613", GUIStyle.none);
            var dark = new GUIStyle(skin.window);
            GUI.Window(614, new Rect(850f, 400f, 300f, 200f), WinSize, "T614", dark);
            GUILayout.Window(615, new Rect(150f, 400f, 300f, 200f), WinSize, "", pad);
        }

        static string seqName;
        static StringBuilder seqSb;

        static void Seq(string name, System.Action body, float areaH = 400f, GUIStyle areaStyle = null, float areaW = 400f)
        {
            seqName = name;
            seqSb = new StringBuilder("seq " + name);
            if (areaStyle == null) GUILayout.BeginArea(new Rect(20f, 20f, areaW, areaH));
            else GUILayout.BeginArea(new Rect(20f, 20f, areaW, areaH), areaStyle);
            var o = GUIUtility.GUIToScreenPoint(Vector2.zero);
            seqSb.Append(" origin=").Append(GuiLayout.V(o));
            try { body(); }
            catch (Exception e) { seqSb.Append(" !").Append(e.GetType().Name).Append(':').Append(GuiLayout.Msg(e.Message)); }
            GUILayout.EndArea();
            if (SizeLog && Event.current.type == EventType.Repaint) GuiLayout.L(seqSb.ToString());
        }

        static void L3(string name, System.Action a)
        {
            a();
            string r;
            try { r = R(GUILayoutUtility.GetLastRect()); } catch (Exception e) { r = "!" + e.GetType().Name; }
            seqSb.Append(' ').Append(name).Append('=').Append(r);
            if (name == "bs" || name == "bh" || name == "bv" || name == "a" && seqName.StartsWith("scroll"))
                seqSb.Append('@').Append(GuiLayout.V(GUIUtility.GUIToScreenPoint(Vector2.zero)));
        }

        static void N(string prefix, int n)
        {
            for (int i = 0; i < n; i++) L3(prefix + i, () => GUILayout.Label(prefix + i));
        }

        void WinSize(int id)
        {
            var a = GUIUtility.GUIToScreenPoint(Vector2.zero);
            GUILayout.Label("first"); var r0 = GUILayoutUtility.GetLastRect();
            GUILayout.Button("second"); var r1 = GUILayoutUtility.GetLastRect();
            if (SizeLog && Event.current.type == EventType.Repaint)
                GuiLayout.L("size win" + id + " origin=" + GuiLayout.V(a) + " first=" + R(r0) + " second=" + R(r1));
        }

        // ================= wsize =================

        static readonly Dictionary<int, Rect> fed = new Dictionary<int, Rect>();

        void WsGui()
        {
            int f = WsFrame;
            Ws(701, new Rect(10f, 10f, 300f, 300f), 2, false);
            Ws(702, new Rect(320f, 10f, 100f, 50f), 15, false);
            Ws(703, new Rect(630f, 10f, 100f, 50f), 2, false);
            Ws(704, new Rect(10f, 10f, 300f, 300f), 15, false);
            Ws(705, new Rect(320f, 10f, 0f, 0f), 2, false);
            Ws(706, new Rect(630f, 10f, 100f, 50f), 2, false, GUILayout.Width(250f));
            Ws(707, new Rect(10f, 10f, 100f, 50f), 2, false, GUILayout.Height(250f));
            Ws(708, new Rect(320f, 10f, 100f, 50f), 2, false, GUILayout.MinHeight(250f));
            Ws(709, new Rect(630f, 10f, 100f, 50f), 2, false, GUILayout.ExpandHeight(true));
            Ws(710, new Rect(10f, 10f, 300f, 300f), 15, false, GUILayout.MaxHeight(100f));
            Ws(711, new Rect(320f, 10f, 100f, 50f), 2, false, GUILayout.ExpandWidth(true));
            Ws(712, new Rect(630f, 10f, 100f, 50f), 2, false, GUILayout.MinWidth(400f));
            Ws(713, new Rect(10f, 10f, 300f, 300f), 15, false, GUILayout.Height(100f));
            Ws(714, new Rect(320f, 10f, 300f, 300f), 2, false, GUILayout.MaxHeight(100f));
            Ws(715, new Rect(630f, 10f, 300f, 300f), 2, false, GUILayout.Width(100f), GUILayout.Height(100f));
            Ws(716, new Rect(10f, 10f, 350f, 650f), 2, false);
            // Fed back each frame, as normal use does.
            Ws(721, new Rect(320f, 10f, 300f, 300f), 2, true);
            Ws(722, new Rect(630f, 10f, 100f, 50f), 15, true);
            Ws(723, new Rect(10f, 10f, 100f, 50f), 2, true);
            // Content growing then shrinking between frames.
            int n = f < 6 ? 1 + f * 3 : 1 + (11 - f) * 3;
            Ws(731, new Rect(320f, 10f, 200f, 50f), n, true);
            Ws(732, new Rect(630f, 10f, 200f, 50f), n, false);
            // The given size changing between frames.
            Ws(733, new Rect(10f, 10f, 100f + 30f * f, 40f + 40f * f), 2, false);
            Ws(734, new Rect(320f, 10f, 400f - 30f * f, 400f - 30f * f), 2, false);
        }

        static readonly Dictionary<int, int> wsN = new Dictionary<int, int>();

        void Ws(int id, Rect given, int n, bool feed, params GUILayoutOption[] options)
        {
            if (feed && fed.TryGetValue(id, out var prev)) given = prev;
            wsN[id] = n;
            Rect r;
            try { r = GUILayout.Window(id, given, WsWin, "W" + id, options); }
            catch (Exception e) { GuiLayout.L("ws " + GuiLayout.Case + " " + id + " " + Event.current.type + " !" + e.GetType().Name + ":" + GuiLayout.Msg(e.Message)); return; }
            if (feed) fed[id] = r;
            if (GuiLayout.Recording)
                GuiLayout.L("ws " + GuiLayout.Case + " " + id + " " + Event.current.type + " n=" + n + " in=" + R(given) + " out=" + R(r));
        }

        void WsWin(int id)
        {
            int n = wsN.TryGetValue(id, out var v) ? v : 1;
            Rect first = default, last = default;
            for (int i = 0; i < n; i++)
            {
                GUILayout.Label(n > 5 ? "a fairly long label number " + i + " here" : "ab" + i);
                if (i == 0) first = GUILayoutUtility.GetLastRect();
                last = GUILayoutUtility.GetLastRect();
            }
            if (GuiLayout.Recording)
                GuiLayout.L("wsf " + GuiLayout.Case + " " + id + " " + Event.current.type + " n=" + n + " first=" + R(first) + " last=" + R(last));
        }

        // ================= order =================

        internal static void OrderGui(string who)
        {
            var e = Event.current;
            if (GuiLayout.Recording) GuiLayout.L("ord " + GuiLayout.Case + " " + who + " " + e.type + " raw=" + e.rawType + " m=" + GuiLayout.V(e.mousePosition) + " hot=" + GUIUtility.hotControl);
            if (who == "gui2")
            {
                GUI.Window(405, new Rect(260f, 210f, 300f, 250f), OrdWin, "405");
                if (OrderSet == "d") GUI.Window(406, new Rect(40f, 300f, 300f, 250f), OrdWin, "406");
                return;
            }
            if (OrderCall != "")
            {
                string call = OrderCall;
                bool atRepaint = call.EndsWith("@Repaint");
                if (atRepaint ? e.type == EventType.Repaint : e.type == EventType.Layout)
                {
                    OrderCall = "";
                    string res = "ok";
                    try
                    {
                        switch (call.Replace("@Repaint", ""))
                        {
                            case "front403": GUI.BringWindowToFront(403); break;
                            case "front404": GUI.BringWindowToFront(404); break;
                            case "back402": GUI.BringWindowToBack(402); break;
                            case "focus401": GUI.FocusWindow(401); break;
                            case "focus405": GUI.FocusWindow(405); break;
                            case "unfocus": GUI.UnfocusWindow(); break;
                        }
                    }
                    catch (Exception ex) { res = "!" + ex.GetType().Name + ":" + GuiLayout.Msg(ex.Message); }
                    if (GuiLayout.Recording) GuiLayout.L("ord " + GuiLayout.Case + " call " + call + " in " + e.type + " " + res);
                }
            }
            if (OrderSet == "c") GUI.Window(400, new Rect(60f, 60f, 300f, 250f), OrdWin, "400");
            if (OrderSkip != 402) GUI.Window(402, new Rect(140f, 120f, 300f, 250f), OrdWin, "402");
            GUI.Window(401, new Rect(100f, 90f, 300f, 250f), OrdWin, "401");
            GUI.Window(403, new Rect(180f, 150f, 300f, 250f), OrdWin, "403");
            if (OrderSet != "a") GUI.Window(404, new Rect(220f, 180f, 300f, 250f), OrdWin, "404");
            if (GuiLayout.Recording) GuiLayout.L("ord " + GuiLayout.Case + " " + who + "-end " + e.type);
        }

        static void OrdWin(int id)
        {
            var e = Event.current;
            if (GuiLayout.Recording) GuiLayout.L("ord " + GuiLayout.Case + " w" + id + " " + e.type + " raw=" + e.rawType + " m=" + GuiLayout.V(e.mousePosition) + " hot=" + GUIUtility.hotControl);
        }

        // ================= mm =================

        static Rect win501 = new Rect(600f, 50f, 300f, 300f);
        static Rect win502 = new Rect(950f, 50f, 300f, 300f);
        static Rect win599 = new Rect(600f, 400f, 250f, 120f);
        static Vector2 mmScroll, mmScroll2, mmScroll3, mmScroll4;
        static string mmText = "MyPreset";

        void MmGui()
        {
            var l = new GlLine("gui");
            bool abort = true;
            try
            {
                switch (Ctx)
                {
                    case "root": MmBody(l); break;
                    case "rootscroll":
                        Do(l, "BSV", () => mmScroll = GUILayout.BeginScrollView(mmScroll, GUILayout.Height(150f)));
                        MmBody(l);
                        Do(l, "ESV", () => GUILayout.EndScrollView());
                        Do(l, "T", () => GUILayout.Label("T"));
                        break;
                    case "win":
                    case "winscroll":
                        var r = GUI.Window(501, win501, MmWin, "W501");
                        l.Add("win=" + R(r));
                        GUI.Window(599, win599, MmControlWin, "W599");
                        break;
                    case "lwin":
                        var r2 = GUILayout.Window(502, win502, MmWin, "W502");
                        l.Add("win=" + R(r2));
                        GUI.Window(599, win599, MmControlWin, "W599");
                        break;
                }
                abort = false;
            }
            finally { l.End(abort); }
        }

        void MmWin(int id)
        {
            var l = new GlLine("w" + id);
            bool abort = true;
            try
            {
                if (Ctx == "winscroll") Do(l, "BSV", () => mmScroll2 = GUILayout.BeginScrollView(mmScroll2, GUILayout.Height(150f)));
                MmBody(l);
                if (Ctx == "winscroll") { Do(l, "ESV", () => GUILayout.EndScrollView()); Do(l, "T", () => GUILayout.Label("T")); }
                abort = false;
            }
            finally { l.End(abort); }
        }

        void MmControlWin(int id)
        {
            var l = new GlLine("w599");
            bool abort = true;
            try
            {
                Do(l, "c0", () => GUILayout.Label("c0"));
                Do(l, "c1", () => GUILayout.Label("c1"));
                abort = false;
            }
            finally { l.End(abort); }
        }

        static void Do(GlLine l, string name, System.Action a)
        {
            var before = Event.current.type;
            try { a(); }
            catch (Exception e)
            {
                l.Add(name + "=!" + e.GetType().Name + ":" + GuiLayout.Msg(e.Message));
                if (!Catch) throw;
                return;
            }
            string r = "ok";
            // GetLastRect straight after a Begin call logs an error; the Begin calls (BH, BV, BSV, BA...) are not read.
            if (!(name.Length > 1 && name[0] == 'B'))
                try { r = R(GUILayoutUtility.GetLastRect()); } catch (Exception e) { r = "!" + e.GetType().Name + ":" + GuiLayout.Msg(e.Message); }
            l.Add(name + "=" + r + (Event.current.type != before ? "[" + Event.current.type + "]" : ""));
        }

        static bool Btn(GlLine l, string name, string text, params GUILayoutOption[] o)
        {
            bool res = false;
            Do(l, name, () => res = GUILayout.Button(text, o));
            if (res) l.Add(name + ".clicked");
            try { GuiLayout.Keep(name, GUILayoutUtility.GetLastRect()); } catch (Exception) { }
            return res;
        }

        // Whether this event takes the changed calls (once per arming).
        static bool Alt(GlLine l)
        {
            if (!Armed) return false;
            var t = Event.current.type;
            if (Target == "Used" && t == EventType.MouseDown) { Event.current.Use(); l.Add("use"); t = Event.current.type; }
            bool hit = t == (EventType)Enum.Parse(typeof(EventType), Target); // ToString gives the lower-case aliases
            if (hit) { Armed = false; l.Add("alt"); }
            return hit;
        }

        static void Lab(GlLine l, string name, params GUILayoutOption[] o) => Do(l, name, () => GUILayout.Label(name, o));

        void MmBody(GlLine l)
        {
            bool alt;
            switch (Body)
            {
                case "insert": Lab(l, "L0"); if (Alt(l)) Lab(l, "X"); Lab(l, "L1"); Lab(l, "L2"); break;
                case "append": Lab(l, "L0"); Lab(l, "L1"); Lab(l, "L2"); if (Alt(l)) Lab(l, "X"); break;
                case "remove": Lab(l, "L0"); if (!Alt(l)) Lab(l, "L1"); Lab(l, "L2"); break;
                case "removeEnd": Lab(l, "L0"); Lab(l, "L1"); if (!Alt(l)) Lab(l, "L2"); break;
                case "leafToGroup":
                    Lab(l, "L0");
                    if (Alt(l)) { Do(l, "BH", () => GUILayout.BeginHorizontal()); Lab(l, "a"); Do(l, "EH", () => GUILayout.EndHorizontal()); }
                    else Lab(l, "L1");
                    Lab(l, "L2");
                    break;
                case "groupToLeaf":
                    Lab(l, "L0");
                    if (Alt(l)) Lab(l, "L1");
                    else { Do(l, "BH", () => GUILayout.BeginHorizontal()); Lab(l, "a"); Lab(l, "b"); Do(l, "EH", () => GUILayout.EndHorizontal()); }
                    Lab(l, "L2");
                    break;
                case "vertToHoriz":
                    Lab(l, "L0");
                    if (Alt(l)) { Do(l, "BH", () => GUILayout.BeginHorizontal()); Lab(l, "a"); Lab(l, "b"); Do(l, "EH", () => GUILayout.EndHorizontal()); }
                    else { Do(l, "BV", () => GUILayout.BeginVertical()); Lab(l, "a"); Lab(l, "b"); Do(l, "EV", () => GUILayout.EndVertical()); }
                    Lab(l, "L2");
                    break;
                case "groupMore":
                    Lab(l, "L0"); Do(l, "BV", () => GUILayout.BeginVertical()); Lab(l, "a"); if (Alt(l)) Lab(l, "X"); Lab(l, "b");
                    Do(l, "EV", () => GUILayout.EndVertical()); Lab(l, "L2");
                    break;
                case "groupFewer":
                    Lab(l, "L0"); Do(l, "BV", () => GUILayout.BeginVertical()); Lab(l, "a"); if (!Alt(l)) Lab(l, "b");
                    Do(l, "EV", () => GUILayout.EndVertical()); Lab(l, "L2");
                    break;
                case "leafToScroll":
                    Lab(l, "L0");
                    if (Alt(l)) { Do(l, "BSV", () => mmScroll3 = GUILayout.BeginScrollView(mmScroll3, GUILayout.Height(60f))); Lab(l, "a"); Do(l, "ESV", () => GUILayout.EndScrollView()); }
                    else Lab(l, "L1", GUILayout.Height(40f));
                    Lab(l, "L2");
                    break;
                case "scrollToLeaf":
                    Lab(l, "L0");
                    if (!Alt(l)) { Do(l, "BSV", () => mmScroll3 = GUILayout.BeginScrollView(mmScroll3, GUILayout.Height(60f))); Lab(l, "a"); Do(l, "ESV", () => GUILayout.EndScrollView()); }
                    else Lab(l, "L1", GUILayout.Height(40f));
                    Lab(l, "L2");
                    break;
                case "content":
                    Lab(l, "L0");
                    alt = Alt(l);
                    Do(l, "C", () => GUILayout.Label(alt ? "a much longer text than before\nwith a second line" : "x"));
                    Lab(l, "L2");
                    break;
                case "getRect":
                    Lab(l, "L0");
                    alt = Alt(l);
                    Do(l, "G", () => GUILayoutUtility.GetRect(alt ? 200f : 20f, alt ? 60f : 10f));
                    Lab(l, "L2");
                    break;
                case "spaceInsert": Lab(l, "L0"); if (Alt(l)) Do(l, "S", () => GUILayout.Space(10f)); Lab(l, "L1"); Lab(l, "L2"); break;
                case "flexInsert": Lab(l, "L0"); if (Alt(l)) Do(l, "FS", () => GUILayout.FlexibleSpace()); Lab(l, "L1"); Lab(l, "L2"); break;
                case "areaInsert":
                    Lab(l, "L0");
                    if (Alt(l)) { Do(l, "BA", () => GUILayout.BeginArea(new Rect(5f, 5f, 100f, 60f))); Lab(l, "a"); Do(l, "EA", () => GUILayout.EndArea()); }
                    Lab(l, "L1"); Lab(l, "L2");
                    break;
                case "handlerGroup":
                    Lab(l, "L0");
                    if (Btn(l, "B", "B")) Flag = true;
                    if (Flag) { Do(l, "BSV", () => mmScroll3 = GUILayout.BeginScrollView(mmScroll3, GUILayout.Height(60f))); Lab(l, "a"); Do(l, "ESV", () => GUILayout.EndScrollView()); }
                    else Lab(l, "none", GUILayout.Height(40f));
                    Lab(l, "L2");
                    break;
                case "handlerLeaf":
                    Lab(l, "L0");
                    if (Btn(l, "B", "B")) Flag = true;
                    if (Flag) Lab(l, "X");
                    Lab(l, "L1"); Lab(l, "L2");
                    break;
                case "handlerRemove":
                    Lab(l, "L0");
                    if (Btn(l, "B", "B")) Flag = true;
                    if (!Flag) Lab(l, "X");
                    Lab(l, "L1"); Lab(l, "L2");
                    break;
                case "mod": ModBody(l); break;
            }
        }

        // As Customize Duplicant traits' preset section.
        void ModBody(GlLine l)
        {
            Lab(l, "P");
            Do(l, "BH", () => GUILayout.BeginHorizontal());
            Do(l, "TF", () => mmText = GUILayout.TextField(mmText, GUILayout.Width(220f)));
            if (Btn(l, "SAVE", "SAVE", GUILayout.Width(90f))) Count++;
            Do(l, "EH", () => GUILayout.EndHorizontal());
            if (Count > 0)
            {
                Do(l, "BSVp", () => mmScroll3 = GUILayout.BeginScrollView(mmScroll3, GUILayout.Height(80f)));
                for (int i = 0; i < Count; i++)
                {
                    Do(l, "BHr" + i, () => GUILayout.BeginHorizontal());
                    Lab(l, "N" + i, GUILayout.Width(170f));
                    Btn(l, "LOAD" + i, "LOAD", GUILayout.Width(60f));
                    if (Btn(l, "DEL" + i, "DEL", GUILayout.Width(40f))) Count--;
                    Do(l, "EHr" + i, () => GUILayout.EndHorizontal());
                }
                Do(l, "ESVp", () => GUILayout.EndScrollView());
            }
            else Lab(l, "NoPresets", GUILayout.Height(40f));
            Do(l, "Sp4", () => GUILayout.Space(4f));
            Do(l, "Line", () => GUILayout.Box("", GUILayout.Height(2f), GUILayout.ExpandWidth(true)));
            Do(l, "Sp4b", () => GUILayout.Space(4f));
            Do(l, "BSVt", () => mmScroll4 = GUILayout.BeginScrollView(mmScroll4, GUILayout.MinHeight(50f), GUILayout.MaxHeight(70f)));
            Lab(l, "Tip");
            Do(l, "ESVt", () => GUILayout.EndScrollView());
            Do(l, "Sp5", () => GUILayout.Space(5f));
            Btn(l, "CLOSE", "[ CLOSE EDITOR ]", GUILayout.Height(30f));
        }
    }

    // A second behaviour, enabled after the first.
    public class GlGui2 : MonoBehaviour
    {
        void OnGUI()
        {
            if (GuiLayout.Phase == "order") GlGui.OrderGui("gui2");
            else if (GuiLayout.Phase == "mm" && GuiLayout.Recording) new GlLine("gui2").End(false);
        }
    }
}
