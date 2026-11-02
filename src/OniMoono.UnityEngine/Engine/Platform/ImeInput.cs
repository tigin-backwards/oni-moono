using UnityEngine;

namespace OniMoono.Engine.Platform
{
    /// Whether the window takes IME input (issue #30). Unity's Windows player turns the window's
    /// IME off unless Input.imeCompositionMode asks for it, so with an IME such as Microsoft
    /// Pinyin on, the game's keys still reach the game outside text fields, and only a focused
    /// field composes. Measured in the original Windows player (U57) with the Chinese layout on
    /// the game window and UnityPlayer.dll's imm32 calls logged: with the mode Auto and no field
    /// focused the window has no input context (ImmAssociateContext(hwnd, NULL)); with a
    /// TextMeshPro field focused the mode is On (the field sets it) and the window has its
    /// default context again (ImmAssociateContextEx(hwnd, NULL, IACE_DEFAULT)), whose
    /// composition reaches Input.compositionString. Unity applies the mode at the window's next
    /// focus, layout or key message; ours at the next frame's event poll, and again at the
    /// window's focus and input language messages (OniMoono.Desktop.Win32Ime).
    ///
    /// Auto with an IMGUI text field holding the keyboard focus takes IME input, as Unity's
    /// documentation describes Auto (not measured: the game has no IMGUI text fields).
    internal static class ImeInput
    {
        public static bool Wanted(IMECompositionMode mode, bool textFieldFocused) => mode switch
        {
            IMECompositionMode.On => true,
            IMECompositionMode.Off => false,
            _ => textFieldFocused,
        };

        /// For the current frame's mode and IMGUI focus.
        public static bool WantedNow => Wanted(Input.imeCompositionMode, ImguiTextFieldFocused);

        static bool ImguiTextFieldFocused
        {
            get
            {
                int id = GUIUtility.keyboardControl;
                return id != 0 && GUIUtility.QueryStateObject(typeof(TextEditor), id) != null;
            }
        }
    }
}
