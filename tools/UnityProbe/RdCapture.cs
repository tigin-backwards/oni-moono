// PROBE_SCENARIO=rdcapture (lines "PROBE ... RD ..."): frames of the original's Direct3D 11 player
// for the D3D11 plan's captures (docs/research/d3d11-captures.md), with the native helper
// tools/D3d11Native/probe_native.dll (PROBE_NATIVE, a Windows path; default: probe_native.bin
// next to this DLL, as the mod loader loads every .dll of a mod folder). The steps:
//   1. the main menu, 10 s in: the helper hooks the swap chain's Present (its descriptions, and
//      each Present's sync interval, flags and thread) and records one frame's Direct3D 11 calls
//      (ctx_hook.c: lines "RD FRAME", "RD DRAW", "RD SHADER", "RD LAYOUT" ...);
//   2. the depth-bias frame: a command buffer draws Hidden/Internal-Colored (`Offset [_ZBias],
//      [_ZBias]`, `Cull [_Cull]`, `ZTest [_ZTest]`, `ZWrite [_ZWrite]`) into render textures with
//      24- and 16-bit depth, one draw per case, and after each draw a render-thread event
//      (IssuePluginEvent) logs the context's rasterizer, depth-stencil and blend state ("RD STATE");
//   3. the colony (PROBE_SAVE, resumed from the menu as its Resume button does), unpaused, then
//      the temperature overlay, a frame recorded at each; last the Present arguments with vsync
//      off.
// PROBE_PACING=1 (D3D11 plan M4) runs a pacing scenario instead: the helper also hooks the wait
// functions on the swap chain's frame-latency waitable (pn_wait_hook) and records whether the
// frame's render target at Present is swap-chain buffer 0; the menu frame is recorded, then 300
// frames each at vSyncCount 1 (as set), 0 and 2 are tallied (pn_pace_report), then the colony
// (PROBE_SAVE) is resumed and the load's Present gaps reported, and a colony frame recorded. No
// depth-bias frame, no overlay.
// RenderDoc: launching the original under `renderdoccmd capture` is NOT supported. Its injection
// bypassed the run's Doorstop isolation (job j0008144, docs/research/d3d11-captures.md), and the
// launch mode was removed. The in-application API (renderdoc_app.h) is kept for a future launch
// that keeps the isolation: it is used only with PROBE_RD_CAPTURE=1 and a PROBE_RD_TEMPLATE inside
// ONI_RUN_HOME. With renderdoc.dll in the process otherwise, the scenario ends at once.
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SemanticsProbe
{
    public class RdCapture : MonoBehaviour
    {
        static void L(string s) => Rec.Log("RD " + s);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LogFn(IntPtr buf, int len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate int InitFn(string template);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeviceFn(IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate void TitleFn(string title);

        LogFn pnLog, pnCaptures;
        InitFn pnInit;
        IntFn pnRdPresent;
        VoidFn pnTrigger, pnReport;
        IntFn pnHook, pnCtxHook, pnCtxDone;
        VoidFn pnCtxArm;
        PtrFn pnEvent;
        DeviceFn pnDevice;
        TitleFn pnTitle, pnPace;
        IntFn pnWaitHook;
        IntPtr logBuf;
        const int LogBufSize = 4 << 20;
        int renderdoc;

        T Fn<T>(IntPtr m, string name) where T : class
        {
            IntPtr p = GetProcAddress(m, name);
            if (p == IntPtr.Zero) throw new Exception("no export " + name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        void Start() => StartCoroutine(Run());

        void Update() => Drain();

        void Drain()
        {
            if (pnLog == null) return;
            int n;
            while ((n = pnLog(logBuf, LogBufSize)) > 0)
                foreach (var line in Marshal.PtrToStringAnsi(logBuf, n).Split('\n'))
                    if (line.Length > 0) L(line);
        }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        IEnumerator Run()
        {
            while (SceneManager.GetActiveScene().name != "frontend") yield return null;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 10f) yield return null;
            L("=== start f" + Time.frameCount + " device " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion + " " + SystemInfo.graphicsDeviceName);
            string native = Environment.GetEnvironmentVariable("PROBE_NATIVE");
            if (string.IsNullOrEmpty(native)) native = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(RdCapture).Assembly.Location), "probe_native.bin");
            IntPtr m = LoadLibraryW(native);
            if (m == IntPtr.Zero) { L("LoadLibrary " + native + " failed " + Marshal.GetLastWin32Error()); yield break; }
            try
            {
                pnLog = Fn<LogFn>(m, "pn_log");
                pnCaptures = Fn<LogFn>(m, "pn_rd_captures");
                pnInit = Fn<InitFn>(m, "pn_rd_init");
                pnRdPresent = Fn<IntFn>(m, "pn_rd_present");
                pnTrigger = Fn<VoidFn>(m, "pn_rd_trigger");
                pnReport = Fn<VoidFn>(m, "pn_sc_report");
                pnHook = Fn<IntFn>(m, "pn_sc_hook");
                pnCtxHook = Fn<IntFn>(m, "pn_ctx_hook");
                pnCtxDone = Fn<IntFn>(m, "pn_ctx_done");
                pnCtxArm = Fn<VoidFn>(m, "pn_ctx_arm");
                pnEvent = Fn<PtrFn>(m, "pn_render_event_func");
                pnDevice = Fn<DeviceFn>(m, "pn_device");
                pnTitle = Fn<TitleFn>(m, "pn_rd_title");
                pnPace = Fn<TitleFn>(m, "pn_pace_report");
                pnWaitHook = Fn<IntFn>(m, "pn_wait_hook");
            }
            catch (Exception e) { L("EXCEPTION " + e.Message); yield break; }
            logBuf = Marshal.AllocHGlobal(LogBufSize);

            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            tex.Apply();
            L("pn_device " + pnDevice(tex.GetNativeTexturePtr()));
            string template = Environment.GetEnvironmentVariable("PROBE_RD_TEMPLATE") ?? "";
            string runHome = Environment.GetEnvironmentVariable("ONI_RUN_HOME") ?? "";
            bool optIn = Environment.GetEnvironmentVariable("PROBE_RD_CAPTURE") == "1" && InsideRunHome(template, runHome);
            if (pnRdPresent() != 0 && !optIn)
            {
                L("renderdoc.dll is in the process without PROBE_RD_CAPTURE=1 and a PROBE_RD_TEMPLATE inside ONI_RUN_HOME: scenario ended");
                Finish();
                yield break;
            }
            renderdoc = optIn ? pnInit(template) : 0;
            L("renderdoc api " + renderdoc + " template '" + template + "'");
            if (renderdoc <= 0) { L("hook " + pnHook()); L("ctx hook " + pnCtxHook()); }

            if (renderdoc <= 0 && Environment.GetEnvironmentVariable("PROBE_MODES") == "1")
            {
                yield return Environment.GetEnvironmentVariable("PROBE_FOCUS") == "1" ? FocusedExclusive() : Modes();
                Finish();
                yield break;
            }

            if (renderdoc <= 0 && Environment.GetEnvironmentVariable("PROBE_PACING") == "1")
            {
                yield return Pacing();
                Finish();
                yield break;
            }

            // 1. The main menu.
            yield return Frames(30);
            if (renderdoc > 0) { pnTrigger(); L("trigger menu f" + Time.frameCount); }
            else yield return Record("menu");
            yield return Frames(30);

            // 2. The depth-bias frame.
            DepthBias();
            yield return Frames(30);
            if (renderdoc <= 0) { yield return Frames(240); pnReport(); }

            // 3. The colony.
            string save = Environment.GetEnvironmentVariable("PROBE_SAVE");
            if (string.IsNullOrEmpty(save)) { L("no PROBE_SAVE: done"); Finish(); yield break; }
            KPlayerPrefs.SetString("AutoResumeSaveFile", save);
            L("resume " + save);
            try { typeof(MainMenu).GetMethod("ResumeGame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(MainMenu.Instance, null); }
            catch (Exception e) { L("ResumeGame EXCEPTION " + e); yield break; }
            while (Game.Instance == null || SpeedControlScreen.Instance == null || OverlayScreen.Instance == null) yield return null;
            L("colony up f" + Time.frameCount);
            yield return Frames(300);
            foreach (var d in UnityEngine.Object.FindObjectsByType<KModalScreen>(FindObjectsSortMode.None))
                if (d.GetType() != typeof(PauseScreen)) { L("dismiss " + d.GetType().Name); d.Deactivate(); }
            SpeedControlScreen.Instance.Unpause(false);
            yield return Frames(300);
            yield return Timed("colony");
            if (renderdoc > 0) { pnTrigger(); L("trigger colony f" + Time.frameCount); }
            else { pnReport(); yield return Record("colony"); }
            yield return Frames(60);
            OverlayScreen.Instance.ToggleOverlay(OverlayModes.Temperature.ID, false);
            L("overlay " + OverlayScreen.Instance.GetMode());
            yield return Frames(240);
            yield return Timed("temperature overlay");
            if (renderdoc > 0) { pnTrigger(); L("trigger overlay f" + Time.frameCount); }
            else { pnReport(); yield return Record("temperature overlay"); }
            yield return Frames(60);
            if (renderdoc <= 0)
            {
                // The Present arguments with vsync off (as PerfProbe sets it for frame-rate runs).
                L("vSyncCount " + QualitySettings.vSyncCount + " -> 0");
                QualitySettings.vSyncCount = 0;
                yield return Frames(240);
                yield return Timed("vsync off");
                pnReport();
            }
            Finish();
        }

        /// The M4 full-screen scenario (PROBE_MODES=1; the user's OK, 2026-11-24): started in a
        /// borderless full-screen window, Screen.SetResolution through borderless at a lower
        /// resolution, exclusive at the monitor's mode and at a lower one, then windowed; each
        /// held for 240 frames. The helper logs every ResizeBuffers, SetFullscreenState and
        /// ResizeTarget with the chain's descriptions after it, and a new chain's on its first
        /// Present.
        IEnumerator Modes()
        {
            yield return Frames(30);
            int w = Screen.currentResolution.width, h = Screen.currentResolution.height;
            void State(string what) => L("MODE " + what + ": fullScreenMode " + Screen.fullScreenMode + " " + Screen.width + "x" + Screen.height
                                          + " current " + Screen.currentResolution.width + "x" + Screen.currentResolution.height + " f" + Time.frameCount);
            State("start");
            pnReport();
            var steps = new (int W, int H, FullScreenMode M)[]
            {
                (1920, 1080, FullScreenMode.FullScreenWindow),
                (w, h, FullScreenMode.ExclusiveFullScreen),
                (1920, 1080, FullScreenMode.ExclusiveFullScreen),
                (1280, 720, FullScreenMode.Windowed),
            };
            foreach (var st in steps)
            {
                L("MODE SetResolution(" + st.W + ", " + st.H + ", " + st.M + ") f" + Time.frameCount);
                Screen.SetResolution(st.W, st.H, st.M);
                yield return Frames(240);
                State("after " + st.M + " " + st.W + "x" + st.H);
                pnReport();
            }
        }

        [DllImport("user32.dll")] static extern int SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        IntPtr otherWindow;

        /// PROBE_FOCUS=1 (the user's word for M4's desktop runs): the game's window asks for the
        /// foreground before exclusive full screen; then a small window of this process's own
        /// takes it (an alt-tab without keys) and the game's window asks for it back. No key is
        /// sent and the pointer is not moved.
        string Focus(bool other)
        {
            IntPtr game = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            IntPtr target = game;
            if (other)
            {
                if (otherWindow == IntPtr.Zero)
                    otherWindow = CreateWindowExW(0x80, "STATIC", "probe focus test", unchecked((int)0x90000000), 2600, 40, 160, 90, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                target = otherWindow;
            }
            int ok = SetForegroundWindow(target);
            IntPtr fg = GetForegroundWindow();
            return $"SetForegroundWindow({(other ? "own popup" : "game window")}) {ok}, foreground now {(fg == game ? "the game window" : fg == otherWindow ? "the popup" : "another process's window")}";
        }

        /// The M4 focused exclusive scenario (PROBE_MODES=1 PROBE_FOCUS=1).
        IEnumerator FocusedExclusive()
        {
            yield return Frames(30);
            int w = Screen.currentResolution.width, h = Screen.currentResolution.height;
            void State(string what) => L("MODE " + what + ": fullScreenMode " + Screen.fullScreenMode + " " + Screen.width + "x" + Screen.height + " focused " + Application.isFocused + " f" + Time.frameCount);
            State("start");
            L("FOCUS " + Focus(false));
            yield return Frames(30);
            L("MODE SetResolution(" + w + ", " + h + ", ExclusiveFullScreen) f" + Time.frameCount);
            Screen.SetResolution(w, h, FullScreenMode.ExclusiveFullScreen);
            yield return Frames(240);
            State("exclusive");
            pnReport();
            L("FOCUS away: " + Focus(true));
            yield return Frames(240);
            State("after the focus went away");
            L("FOCUS back: " + Focus(false));
            yield return Frames(240);
            State("after the focus came back");
            pnReport();
            L("MODE SetResolution(1280, 720, Windowed) f" + Time.frameCount);
            Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
            yield return Frames(240);
            State("windowed");
            pnReport();
        }

        /// The M4 pacing scenario (PROBE_PACING=1).
        IEnumerator Pacing()
        {
            yield return Frames(30);
            L("wait hook " + pnWaitHook());
            L("vSyncCount " + QualitySettings.vSyncCount + " targetFrameRate " + Application.targetFrameRate + " maxQueuedFrames " + QualitySettings.maxQueuedFrames
              + " fullScreenMode " + Screen.fullScreenMode + " " + Screen.width + "x" + Screen.height);
            yield return Frames(30);
            pnPace("menu start");
            yield return Record("menu");
            yield return Frames(10);
            pnPace("menu record");
            int asSet = QualitySettings.vSyncCount;
            foreach (int v in new[] { asSet, 0, 2 })
            {
                QualitySettings.vSyncCount = v;
                yield return Frames(30);
                pnPace("settle vsync " + v);
                yield return Timed("vsync " + v);
                pnPace("menu vsync " + v);
                pnReport();
            }
            QualitySettings.vSyncCount = asSet;
            yield return Frames(30);
            pnPace("settle back to vsync " + asSet);
            string save = Environment.GetEnvironmentVariable("PROBE_SAVE");
            if (string.IsNullOrEmpty(save)) { L("no PROBE_SAVE: done"); yield break; }
            KPlayerPrefs.SetString("AutoResumeSaveFile", save);
            L("resume " + save + " f" + Time.frameCount + " t " + Time.realtimeSinceStartup.ToString("F3"));
            try { typeof(MainMenu).GetMethod("ResumeGame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(MainMenu.Instance, null); }
            catch (Exception e) { L("ResumeGame EXCEPTION " + e); yield break; }
            while (Game.Instance == null || SpeedControlScreen.Instance == null || OverlayScreen.Instance == null) yield return null;
            L("colony up f" + Time.frameCount + " t " + Time.realtimeSinceStartup.ToString("F3"));
            pnPace("load");
            yield return Frames(300);
            pnPace("colony first 300 frames");
            yield return Record("colony");
            yield return Frames(10);
            pnPace("colony record");
            pnReport();
        }

        /// Whether PATH is an absolute path inside the run home HOME (both Windows paths).
        static bool InsideRunHome(string path, string home)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(home)) return false;
            try
            {
                string full = System.IO.Path.GetFullPath(path), root = System.IO.Path.GetFullPath(home).TrimEnd('\\', '/') + "\\";
                return System.IO.Path.IsPathRooted(path) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// One frame's Direct3D 11 calls, recorded by the helper between two Presents.
        IEnumerator Record(string what)
        {
            L("record " + what + " armed f" + Time.frameCount);
            pnCtxArm();
            for (int i = 0; i < 600; i++)
            {
                yield return null;
                if (pnCtxDone() != 0) { Drain(); L("record " + what + " done f" + Time.frameCount); yield break; }
            }
            L("record " + what + " TIMEOUT");
        }

        /// The main thread's frame time over 300 frames (Time.unscaledDeltaTime).
        static IEnumerator Timed(string what)
        {
            float sum = 0, max = 0;
            for (int i = 0; i < 300; i++)
            {
                yield return null;
                sum += Time.unscaledDeltaTime;
                max = Mathf.Max(max, Time.unscaledDeltaTime);
            }
            L($"frametime {what}: mean {sum / 300 * 1000:F2} ms max {max * 1000:F2} ms over 300 frames");
        }

        void Finish()
        {
            Drain();
            if (renderdoc > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal(1 << 14);
                int n = pnCaptures(buf, 1 << 14);
                foreach (var line in Marshal.PtrToStringAnsi(buf, n).Split('\n'))
                    if (line.Length > 0) L("capture " + line);
            }
            L("done");
            new GameObject("RdCaptureDone");
        }

        void DepthBias()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            L("shader " + (shader != null ? shader.name : "missing"));
            if (shader == null) return;
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.6f), new Vector3(0.5f, -0.5f, 0.6f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            // (zbias, cull, ztest, zwrite): Unity's CullMode 0 Off 1 Front 2 Back; CompareFunction
            // 2 Less 3 Equal 4 LessEqual 5 Greater 6 NotEqual 7 GreaterEqual 8 Always.
            var cases = new (float bias, int cull, int ztest, int zwrite)[]
            {
                (0, 0, 8, 0), (1, 2, 4, 1), (-1, 2, 4, 1), (2, 2, 4, 1), (0.5f, 2, 4, 1), (-3.25f, 2, 4, 1), (10, 2, 4, 1), (100, 2, 4, 1),
                // Rounding of a fractional units value: half-way cases both signs, and 0.75.
                (1.5f, 2, 4, 1), (-1.5f, 2, 4, 1), (2.5f, 2, 4, 1), (-2.5f, 2, 4, 1), (0.75f, 2, 4, 1), (-0.75f, 2, 4, 1),
                (0, 1, 2, 1), (0, 2, 5, 0), (0, 0, 7, 1), (0, 0, 3, 0), (0, 0, 6, 0),
            };
            var cb = new CommandBuffer { name = "probe depth bias" };
            IntPtr ev = pnEvent();
            pnTitle("probe depth bias");
            cb.IssuePluginEvent(ev, 1);
            int k = 0;
            foreach (int depth in new[] { 24, 16 })
            {
                var rt = new RenderTexture(64, 64, depth, RenderTextureFormat.ARGB32);
                rt.Create();
                L($"rt depth {depth}: {rt.depthStencilFormat}");
                cb.SetRenderTarget(rt);
                cb.ClearRenderTarget(true, true, Color.black, 1f);
                cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-1, 1, -1, 1, 0.1f, 10f));
                foreach (var c in cases)
                {
                    var mat = new Material(shader);
                    mat.SetFloat("_ZBias", c.bias);
                    mat.SetFloat("_Cull", c.cull);
                    mat.SetFloat("_ZTest", c.ztest);
                    mat.SetFloat("_ZWrite", c.zwrite);
                    mat.SetColor("_Color", Color.white);
                    cb.DrawMesh(mesh, Matrix4x4.TRS(new Vector3(0, 0, -1), Quaternion.identity, Vector3.one), mat, 0, 0);
                    cb.IssuePluginEvent(ev, 100 + k);
                    L($"draw {k}: depth {depth} _ZBias {c.bias.ToString("R")} _Cull {c.cull} _ZTest {c.ztest} _ZWrite {c.zwrite}");
                    k++;
                }
            }
            cb.IssuePluginEvent(ev, 2);
            Graphics.ExecuteCommandBuffer(cb);
            L("depth bias frame submitted f" + Time.frameCount);
        }
    }
}
