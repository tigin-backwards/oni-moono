using System;
using System.Runtime.InteropServices;

namespace OniMoono.Desktop
{
    /// Whether the window takes IME input on Windows (issue #30; ImeInput decides when). The same
    /// calls as Unity's player: the input context taken away (ImmAssociateContext(hwnd, NULL)),
    /// and the window's default one given back (ImmAssociateContextEx with IACE_DEFAULT).
    ///
    /// Applied at each frame's event poll when the mode changed, and again whatever it was when
    /// the window gains the focus (WM_SETFOCUS) or its input language changes
    /// (WM_INPUTLANGCHANGE), as Unity's player applies it at the window's focus, layout and key
    /// messages (measured with UnityPlayer.dll's imm32 calls logged). When ours applied it only
    /// as the mode changed, a player with a Chinese IME reported W, A, S and D doing nothing
    /// until Caps Lock was toggled (unmeasured here: no Chinese IME to test with).
    internal sealed class Win32Ime : IDisposable
    {
        internal const uint WM_SETFOCUS = 0x0007, WM_INPUTLANGCHANGE = 0x0051;
        const int GWLP_WNDPROC = -4;
        const uint IACE_DEFAULT = 0x10;

        delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

        readonly Func<bool> wanted;
        readonly Action<bool> associate;
        readonly Action<Exception> failed;
        readonly nint hwnd;
        readonly nint glfwProc;
        readonly WndProc proc; // kept alive while the window uses it
        /// Whether the window's IME is on (null: not applied since the last focus or layout
        /// message; Windows' default is on).
        bool? applied;

        /// The window's IME, applied as ImeInput asks; the window's procedure is subclassed for
        /// its focus and layout messages. What applying it throws there goes to failed.
        public Win32Ime(nint hwnd, Action<Exception> failed)
            : this(() => OniMoono.Engine.Platform.ImeInput.WantedNow, on => Associate(hwnd, on), failed)
        {
            this.hwnd = hwnd;
            proc = WindowProc;
            glfwProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
        }

        /// Without a window (tests): associate is called with whether the IME is to be on.
        internal Win32Ime(Func<bool> wanted, Action<bool> associate, Action<Exception> failed)
        {
            this.wanted = wanted;
            this.associate = associate;
            this.failed = failed;
        }

        /// The mode ImeInput asks for now, when it differs from the one applied.
        public void Apply()
        {
            bool on = wanted();
            if (applied == on) return;
            applied = on;
            associate(on);
        }

        /// What the window's procedure does for message, after GLFW's: the mode applied again.
        internal void Received(uint message)
        {
            if (!ReappliedAt(message)) return;
            applied = null;
            Apply();
        }

        internal static bool ReappliedAt(uint message) => message is WM_SETFOCUS or WM_INPUTLANGCHANGE;

        nint WindowProc(nint window, uint message, nint wParam, nint lParam)
        {
            var result = CallWindowProcW(glfwProc, window, message, wParam, lParam);
            try { Received(message); }
            catch (Exception e) { failed(e); }
            return result;
        }

        static void Associate(nint hwnd, bool on)
        {
            if (on) ImmAssociateContextEx(hwnd, 0, IACE_DEFAULT);
            else ImmAssociateContext(hwnd, 0);
        }

        public void Dispose()
        {
            if (hwnd != 0) SetWindowLongPtrW(hwnd, GWLP_WNDPROC, glfwProc);
        }

        [DllImport("imm32.dll")]
        static extern nint ImmAssociateContext(nint hwnd, nint himc);

        [DllImport("imm32.dll")]
        static extern bool ImmAssociateContextEx(nint hwnd, nint himc, uint flags);

        [DllImport("user32.dll")]
        static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

        [DllImport("user32.dll")]
        static extern nint CallWindowProcW(nint proc, nint hwnd, uint message, nint wParam, nint lParam);
    }
}
