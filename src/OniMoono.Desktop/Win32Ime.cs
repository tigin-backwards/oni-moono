using System;
using System.Runtime.InteropServices;
using OniMoono.Desktop.D3d11;

namespace OniMoono.Desktop
{
    /// The window's IME on Windows (issue #30; ImeInput decides when it is on).
    ///
    /// On and off: the same calls as Unity's player, the input context taken away
    /// (ImmAssociateContext(hwnd, NULL)) and the window's default one given back
    /// (ImmAssociateContextEx with IACE_DEFAULT). Applied at each frame's event poll when the
    /// mode changed, and again whatever it was when the window gains the focus (WM_SETFOCUS) or
    /// its input language changes (WM_INPUTLANGCHANGE), as Unity's player applies it at the
    /// window's focus, layout and key messages (measured with UnityPlayer.dll's imm32 calls
    /// logged).
    ///
    /// Composition, as in Unity's player: the text being composed is Input.compositionString
    /// (WM_IME_COMPOSITION's GCS_COMPSTR), the composed result is typed text (GCS_RESULTSTR), and
    /// the IME draws no composition window of its own (measured in the original with Microsoft
    /// Pinyin on the game's thread and ImmSetCompositionString: compositionString "ni" and no new
    /// window; ours showed the IME's own composition window, CiceroUIWndFrame, at the screen's
    /// top left, and an empty compositionString). The candidate list stays the IME's: its window
    /// is placed at Input.compositionCursorPos, which a focused field sets to its caret (client
    /// pixels, y down), through the context's composition and candidate forms.
    internal sealed class Win32Ime : IDisposable
    {
        internal const uint WM_SETFOCUS = 0x0007, WM_INPUTLANGCHANGE = 0x0051;
        internal const uint WM_IME_STARTCOMPOSITION = 0x010D, WM_IME_ENDCOMPOSITION = 0x010E, WM_IME_COMPOSITION = 0x010F;
        internal const uint WM_IME_SETCONTEXT = 0x0281;
        internal const uint GCS_COMPSTR = 0x0008, GCS_RESULTSTR = 0x0800;
        // WM_IME_SETCONTEXT's flag for the IME's composition window, cleared: the field shows it.
        internal const long ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000L;
        const int GWLP_WNDPROC = -4;
        const uint IACE_DEFAULT = 0x10;

        delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

        /// What the window's IME does, apart from the decisions: Win32's calls, or a test's.
        internal interface IContext
        {
            /// The window's input context given back (true) or taken away.
            void Associate(bool on);
            /// The composition's string at index (GCS_COMPSTR, GCS_RESULTSTR); "" without a context.
            string Read(uint index);
            /// The composition and candidate windows at (x, y), client pixels.
            void Place(int x, int y);
            /// The composition in progress dropped.
            void Cancel();
        }

        readonly Func<bool> wanted;
        readonly Func<(int x, int y)> cursor;
        readonly IContext context;
        readonly Action<string> composed;
        readonly Action<string> committed;
        readonly Action<Exception> failed;
        readonly nint hwnd;
        readonly nint glfwProc;
        readonly WndProc proc; // kept alive while the window uses it
        /// Whether the window's IME is on (null: not applied since the last focus or layout
        /// message; Windows' default is on).
        bool? applied;
        /// Where the IME's windows were placed since it was last turned on (null: not yet).
        (int x, int y)? placed;
        /// The composition shown (what `composed` was last told).
        string composition = "";

        /// The window's IME, applied as ImeInput asks; the window's procedure is subclassed for
        /// its focus, layout and IME messages. The composition goes to ImeInput, the composed
        /// text to `committed`. What handling a message throws goes to failed.
        public Win32Ime(nint hwnd, Action<string> committed, Action<Exception> failed)
            : this(() => OniMoono.Engine.Platform.ImeInput.WantedNow, () => OniMoono.Engine.Platform.ImeInput.CursorPixels,
                new Imm32(hwnd), text => OniMoono.Engine.Platform.ImeInput.Composition = text, committed, failed)
        {
            this.hwnd = hwnd;
            proc = WindowProc;
            glfwProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
        }

        /// Without a window (tests).
        internal Win32Ime(Func<bool> wanted, Func<(int x, int y)> cursor, IContext context, Action<string> composed, Action<string> committed, Action<Exception> failed)
        {
            this.wanted = wanted;
            this.cursor = cursor;
            this.context = context;
            this.composed = composed;
            this.committed = committed;
            this.failed = failed;
        }

        /// The mode ImeInput asks for now, when it differs from the one applied; while on, the
        /// IME's windows follow the field's caret.
        public void Apply()
        {
            bool on = wanted();
            if (applied != on)
            {
                // A composition left when the field lets go is dropped, not typed into the game
                // (unmeasured in Unity's player).
                if (!on && composition.Length > 0)
                {
                    context.Cancel();
                    Compose("");
                }
                applied = on;
                placed = null;
                context.Associate(on);
            }
            if (on) Place();
        }

        void Place()
        {
            var at = cursor();
            if (placed == at) return;
            placed = at;
            context.Place(at.x, at.y);
        }

        /// What the window's procedure does for message, after GLFW's: the mode applied again.
        internal void Received(uint message)
        {
            if (!ReappliedAt(message)) return;
            applied = null;
            Apply();
        }

        internal static bool ReappliedAt(uint message) => message is WM_SETFOCUS or WM_INPUTLANGCHANGE;

        /// An IME message, before GLFW sees it: true when it is handled here, and GLFW (and
        /// DefWindowProc, which would draw the IME's own composition window or type the result
        /// a second time) does not get it. lParam is what GLFW gets otherwise.
        internal bool Intercepted(uint message, ref nint lParam)
        {
            switch (message)
            {
                case WM_IME_SETCONTEXT:
                    lParam = (nint)((long)lParam & ~ISC_SHOWUICOMPOSITIONWINDOW);
                    return false;
                case WM_IME_STARTCOMPOSITION:
                    // Placed again: the field may have moved without its caret changing.
                    placed = null;
                    if (applied == true) Place();
                    return true;
                case WM_IME_COMPOSITION:
                    uint flags = (uint)(long)lParam;
                    if ((flags & GCS_RESULTSTR) != 0)
                    {
                        string result = context.Read(GCS_RESULTSTR);
                        if (result.Length > 0) committed(result);
                    }
                    if ((flags & GCS_COMPSTR) != 0) Compose(context.Read(GCS_COMPSTR));
                    else if ((flags & GCS_RESULTSTR) != 0) Compose("");
                    return true;
                case WM_IME_ENDCOMPOSITION:
                    Compose("");
                    return true;
                default:
                    return false;
            }
        }

        void Compose(string text)
        {
            if (text == composition) return;
            composition = text;
            composed(text);
        }

        nint WindowProc(nint window, uint message, nint wParam, nint lParam)
        {
            try
            {
                if (Intercepted(message, ref lParam)) return 0;
            }
            catch (Exception e)
            {
                failed(e);
            }
            var result = CallWindowProcW(glfwProc, window, message, wParam, lParam);
            try { Received(message); }
            catch (Exception e) { failed(e); }
            return result;
        }

        public void Dispose()
        {
            if (hwnd != 0) SetWindowLongPtrW(hwnd, GWLP_WNDPROC, glfwProc);
        }

        /// IMM32 on the window.
        sealed class Imm32(nint hwnd) : IContext
        {
            const uint CFS_POINT = 0x0002, CFS_CANDIDATEPOS = 0x0040, NI_COMPOSITIONSTR = 0x0015, CPS_CANCEL = 0x0004;

            public void Associate(bool on)
            {
                if (on) ImmAssociateContextEx(hwnd, 0, IACE_DEFAULT);
                else ImmAssociateContext(hwnd, 0);
            }

            public string Read(uint index)
            {
                var himc = ImmGetContext(hwnd);
                if (himc == 0) return "";
                try
                {
                    int bytes = ImmGetCompositionStringW(himc, index, null, 0);
                    if (bytes <= 0) return "";
                    var buffer = new byte[bytes];
                    ImmGetCompositionStringW(himc, index, buffer, bytes);
                    return System.Text.Encoding.Unicode.GetString(buffer);
                }
                finally { ImmReleaseContext(hwnd, himc); }
            }

            public void Place(int x, int y)
            {
                var himc = ImmGetContext(hwnd);
                if (himc == 0) return;
                try
                {
                    var composition = new COMPOSITIONFORM { dwStyle = CFS_POINT, x = x, y = y };
                    ImmSetCompositionWindow(himc, ref composition);
                    var candidate = new CANDIDATEFORM { dwIndex = 0, dwStyle = CFS_CANDIDATEPOS, x = x, y = y };
                    ImmSetCandidateWindow(himc, ref candidate);
                }
                finally { ImmReleaseContext(hwnd, himc); }
            }

            public void Cancel()
            {
                var himc = ImmGetContext(hwnd);
                if (himc == 0) return;
                ImmNotifyIME(himc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
                ImmReleaseContext(hwnd, himc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        [OniMoono.Desktop.D3d11.NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]
        struct COMPOSITIONFORM { public uint dwStyle; public int x, y; public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        [OniMoono.Desktop.D3d11.NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]
        struct CANDIDATEFORM { public uint dwIndex, dwStyle; public int x, y; public int left, top, right, bottom; }

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern nint ImmAssociateContext(nint hwnd, nint himc);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern bool ImmAssociateContextEx(nint hwnd, nint himc, uint flags);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern nint ImmGetContext(nint hwnd);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern bool ImmReleaseContext(nint hwnd, nint himc);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern int ImmGetCompositionStringW(nint himc, uint index, byte[] buffer, int bytes);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern bool ImmSetCompositionWindow(nint himc, ref COMPOSITIONFORM form);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern bool ImmSetCandidateWindow(nint himc, ref CANDIDATEFORM form);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("imm32.dll")]
        static extern bool ImmNotifyIME(nint himc, uint action, uint index, uint value);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("user32.dll")]
        static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

        [NotNative("a window, input or IME call (user32, imm32, kernel32, shcore), outside the renderer's binding: not declared to the check")]

        [DllImport("user32.dll")]
        static extern nint CallWindowProcW(nint proc, nint hwnd, uint message, nint wParam, nint lParam);
    }
}
