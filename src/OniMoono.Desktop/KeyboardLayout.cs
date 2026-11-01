using System;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.GlfwApi.X11;
using UnityEngine;

namespace OniMoono.Desktop
{
    /// Which KeyCode a key reports under the active keyboard layout (issue #24).
    ///
    /// The game's Input Manager has "Use Physical Keys" off (m_UsePhysicalKeys false in both the
    /// Linux and the Windows build's globalgamemanagers, U57), so Unity's legacy Input reports the
    /// layout's keys, not positions: on AZERTY the key left of Z is KeyCode.Q. GLFW's key tokens
    /// are positions (a US layout's), so printable keys are mapped through the layout here, and
    /// the rest (arrows, F-keys, modifiers, the keypad, Space, Return, Tab, Backspace…) stay
    /// positional. A build with the flag on gets positions, as Unity gives it. The layout is read
    /// at each press (alt+shift can switch it at any time); a release reports what its press did.
    ///
    /// Measured in the original Linux player (Unity 6000.3.5f2, U57) on an Xvfb with
    /// `setxkbmap fr`, physical keys sent through XTEST by X keycode (evdev), and Input.GetKeyDown
    /// and IMGUI KeyDown events logged by E2eDriver's key log (tests/e2e/keyboard-layout.yaml):
    ///
    ///     key (US position)   fr label    KeyCode (GetKeyDown = Event.keyCode)
    ///     ` (49)              ²           none (no event, no character)
    ///     1..0 (10-19)        & é " ' ( - è _ ç à    Alpha1..Alpha0 (the digit on level 2)
    ///     - (20)              )           RightParen
    ///     = (21)              =           Equals
    ///     Q W (24, 25)        A Z         A, Z
    ///     E..P (26-33)        E..P        E..P
    ///     [ (34)              ^ (dead)    none
    ///     ] (35)              $           Dollar
    ///     A (38)              Q           Q
    ///     S..L (39-46)        S..L        S..L
    ///     ; (47)              M           M
    ///     ' (48)              ù           none
    ///     \ (51)              *           Asterisk
    ///     < > (94, pc105)     <           Less
    ///     Z (52)              W           W
    ///     X..N (53-57)        X..N        X..N
    ///     M (58)              ,           Comma
    ///     , . / (59-61)       ; : !       Semicolon, Colon, Exclaim
    ///     Shift+1, Shift+-    1, °        LeftShift then Alpha1, RightParen
    ///     Space Tab Return Backspace F1 Left KP_Multiply: as on US.
    ///
    /// So: a digit on the key's first two levels gives AlphaN; else a letter gives that letter;
    /// else the first level's ASCII character gives the KeyCode of that character; anything else
    /// (non-ASCII, dead keys) gives no key code. macOS keeps positions (not measured).
    ///
    /// Windows is different: the original maps the layout's virtual key through a fixed table,
    /// whatever character the key types. Measured in the original Windows player (same build) with
    /// key messages posted to its window (the scancode, and the virtual key the layout gives it)
    /// under the US and the French layout, read by E2eDriver's key log: VK_A..Z are the letters,
    /// VK_0..9 Alpha0..9, and the OEM keys are, on both layouts, OEM_1 Semicolon, OEM_PLUS Equals,
    /// OEM_COMMA Comma, OEM_MINUS Minus, OEM_PERIOD Period, OEM_2 Slash, OEM_3 BackQuote, OEM_4
    /// LeftBracket, OEM_5 Backslash, OEM_6 RightBracket, OEM_7 Quote, OEM_8 BackQuote, OEM_102
    /// Backslash. So on AZERTY the ) key is LeftBracket, the ; key Period, the ! key BackQuote and
    /// the ù key BackQuote, where the Linux player gives RightParen, Semicolon, Exclaim and none.
    ///
    /// Characters typed follow the layout in both players. The Linux original types none for keys
    /// it gives no key code (ù and ² on AZERTY) and does not compose dead keys (after ^ it types
    /// the next key as is); the Windows original types them and composes (^ then e is ê), as ours
    /// does on both, through GLFW's character callback. Also unrelated to layouts: with NumLock off the
    /// original reports the keypad's navigation keys (KP_1 is End), ours the keypad's positions.
    /// Key labels (the controls screen, tooltips) are the game's names for the bound KeyCode
    /// (GameUtil.GetKeycodeLocalized), the same in both players.
    internal static class KeyboardLayout
    {
        /// The KeyCode of a printable key whose layout puts `level1` there unshifted and
        /// `level2` with Shift (characters, or -1 for none or a dead key).
        public static KeyCode FromCharacters(int level1, int level2)
        {
            if (IsDigit(level1)) return KeyCode.Alpha0 + (level1 - '0');
            if (IsDigit(level2)) return KeyCode.Alpha0 + (level2 - '0');
            if (level1 is >= 'A' and <= 'Z') return KeyCode.A + (level1 - 'A');
            // ASCII from '!' to '~' are KeyCodes of the same value (letters lower case).
            if (level1 is >= '!' and <= '~') return (KeyCode)level1;
            return KeyCode.None;
        }

        static bool IsDigit(int c) => c is >= '0' and <= '9';

        /// GLFW's printable keys (the ones a layout moves): the US main block's characters and
        /// the two non-US keys (World1 is the pc105 key beside left Shift).
        public static bool IsPrintable(GlfwKey key) => key is >= GlfwKey.Apostrophe and <= GlfwKey.World2;

        /// An X keysym as the character it types, for the ASCII range (Latin-1 keysyms are their
        /// code points; Unicode keysyms are 0x1000000 + the code point); -1 otherwise.
        public static int KeysymCharacter(ulong keysym)
        {
            if (keysym is >= 0x20 and <= 0x7e) return (int)keysym;
            if (keysym is >= 0x1000020 and <= 0x100007e) return (int)(keysym - 0x1000000);
            return -1;
        }

        /// Windows: the KeyCode of a layout's virtual key (the table above); none for a key the
        /// table does not name.
        public static KeyCode FromVirtualKey(uint vk) => vk switch
        {
            >= 'A' and <= 'Z' => KeyCode.A + (int)(vk - 'A'),
            >= '0' and <= '9' => KeyCode.Alpha0 + (int)(vk - '0'),
            0xBA => KeyCode.Semicolon,
            0xBB => KeyCode.Equals,
            0xBC => KeyCode.Comma,
            0xBD => KeyCode.Minus,
            0xBE => KeyCode.Period,
            0xBF => KeyCode.Slash,
            0xC0 or 0xDF => KeyCode.BackQuote,
            0xDB => KeyCode.LeftBracket,
            0xDC or 0xE2 => KeyCode.Backslash,
            0xDD => KeyCode.RightBracket,
            0xDE => KeyCode.Quote,
            _ => KeyCode.None,
        };

        /// The KeyCode of a printable key under the layout active now, or null where the layout
        /// cannot be read (the caller keeps the position).
        public static KeyCode? Current(GlfwKey key, int scancode)
        {
            if (unavailable) return null;
            try
            {
                if (OperatingSystem.IsWindows()) return Windows(scancode);
                if (OperatingSystem.IsLinux()) return Glfw.GetPlatform() == Brutal.GlfwApi.GlfwPlatform.X11 ? X11(scancode) : Named(key, scancode);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                unavailable = true;
            }
            return null;
        }

        /// Set once the platform's layout functions turned out to be missing: positions from then.
        /// A guard only: libX11 and user32 have always been there (GLFW's X11 backend loads
        /// libX11 itself), so it is not known to happen.
        static bool unavailable;

        // ---- X11: the keysyms on the key's first two levels in the active group.

        const uint XkbUseCoreKbd = 0x0100;

        static KeyCode? X11(int scancode)
        {
            if (scancode is < 8 or > 255) return null;
            var display = X11Extensions.GetX11Display();
            if (display == 0) return null; // a guard: GLFW's X11 backend always has a display
            // XkbStateRec starts with the effective group (one byte); the rest is not read.
            Span<byte> state = stackalloc byte[64];
            int group = XkbGetState(display, XkbUseCoreKbd, ref MemoryMarshal.GetReference(state)) == 0 ? state[0] : 0;
            int level1 = KeysymCharacter((ulong)XkbKeycodeToKeysym(display, (byte)scancode, group, 0));
            int level2 = KeysymCharacter((ulong)XkbKeycodeToKeysym(display, (byte)scancode, group, 1));
            return FromCharacters(level1, level2);
        }

        [DllImport("libX11.so.6")]
        static extern int XkbGetState(nint display, uint device, ref byte state);

        [DllImport("libX11.so.6")]
        static extern nuint XkbKeycodeToKeysym(nint display, byte keycode, int group, int level);

        // ---- Other Linux backends (Wayland): GLFW's name for the key, its first level only.

        static KeyCode? Named(GlfwKey key, int scancode)
        {
            var name = Glfw.GetKeyName(key, scancode);
            if (string.IsNullOrEmpty(name) || name.Length != 1) return KeyCode.None;
            int c = name[0];
            if (c is >= 'a' and <= 'z') c -= 32;
            // The digit row keeps its digits, as X11's second level gives them on AZERTY.
            if (key is >= GlfwKey.Number0 and <= GlfwKey.Number9 && c is not (>= 'A' and <= 'Z'))
                return KeyCode.Alpha0 + (key - GlfwKey.Number0);
            return FromCharacters(c is >= '!' and <= '~' ? c : -1, -1);
        }

        // ---- Windows: the active layout's virtual key for the scancode.

        const uint MAPVK_VSC_TO_VK_EX = 3;

        static KeyCode? Windows(int scancode)
        {
            // GLFW's scancode is the make code with KF_EXTENDED (0x100); printable keys have none.
            if ((scancode & 0x100) != 0) return null;
            var layout = GetKeyboardLayout(0);
            return FromVirtualKey(MapVirtualKeyExW((uint)scancode & 0xff, MAPVK_VSC_TO_VK_EX, layout));
        }

        [DllImport("user32.dll")]
        static extern nint GetKeyboardLayout(uint thread);

        [DllImport("user32.dll")]
        static extern uint MapVirtualKeyExW(uint code, uint mapType, nint layout);
    }
}
