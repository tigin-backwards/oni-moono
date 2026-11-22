/* The part of SDL 3 and SDL3_ttf 3.2 the Linux crash window uses (crash_window_linux.c), declared
 * here so the launcher builds with any C compiler and no SDL headers (setup.sh). Both libraries
 * are loaded at run time (dlopen) from beside the executable, and only after a crash. The values
 * and the layouts are SDL's (zlib licence); sdl_min_check.c asserts them against SDL's own
 * headers in the release build (scripts/build-native-host-linux.sh). */
#ifndef MOONO_SDL_MIN_H
#define MOONO_SDL_MIN_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct MS_Window MS_Window;
typedef struct MS_IOStream MS_IOStream;
typedef struct MS_Cursor MS_Cursor;
typedef struct MS_Gamepad MS_Gamepad;
typedef struct MS_Font MS_Font;

typedef struct
{
    uint32_t flags;
    uint32_t format;
    int w, h;
    int pitch;
    void *pixels;
    int refcount;
    void *reserved;
} MS_Surface;

typedef struct { int x, y, w, h; } MS_Rect;
typedef struct { uint8_t r, g, b, a; } MS_Color;

typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t window; int32_t data1, data2; } MS_WindowEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t window, which; uint32_t scancode, key; uint16_t mod, raw; bool down, repeat; } MS_KeyboardEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t window; const char *text; } MS_TextInputEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t window, which, state; float x, y, xrel, yrel; } MS_MouseMotionEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t window, which; uint8_t button; bool down; uint8_t clicks, padding; float x, y; } MS_MouseButtonEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t window, which; float x, y; } MS_MouseWheelEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t which; uint8_t button; bool down; } MS_GamepadButtonEvent;
typedef struct { uint32_t type, reserved; uint64_t timestamp; uint32_t which; } MS_GamepadDeviceEvent;

typedef union
{
    uint32_t type;
    MS_WindowEvent window;
    MS_KeyboardEvent key;
    MS_TextInputEvent text;
    MS_MouseMotionEvent motion;
    MS_MouseButtonEvent button;
    MS_MouseWheelEvent wheel;
    MS_GamepadButtonEvent gbutton;
    MS_GamepadDeviceEvent gdevice;
    uint8_t padding[128];
} MS_Event;

#define MS_SDL_INIT_VIDEO 0x20u
#define MS_SDL_INIT_GAMEPAD 0x2000u
#define MS_SDL_EVENT_QUIT 0x100
#define MS_SDL_EVENT_SYSTEM_THEME_CHANGED 0x108
#define MS_SDL_EVENT_WINDOW_SHOWN 0x202
#define MS_SDL_EVENT_WINDOW_EXPOSED 0x204
#define MS_SDL_EVENT_WINDOW_RESIZED 0x206
#define MS_SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED 0x207
#define MS_SDL_EVENT_WINDOW_MOUSE_LEAVE 0x20d
#define MS_SDL_EVENT_WINDOW_FOCUS_GAINED 0x20e
#define MS_SDL_EVENT_WINDOW_FOCUS_LOST 0x20f
#define MS_SDL_EVENT_WINDOW_CLOSE_REQUESTED 0x210
#define MS_SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED 0x214
#define MS_SDL_EVENT_KEY_DOWN 0x300
#define MS_SDL_EVENT_KEY_UP 0x301
#define MS_SDL_EVENT_TEXT_EDITING 0x302
#define MS_SDL_EVENT_TEXT_INPUT 0x303
#define MS_SDL_EVENT_MOUSE_MOTION 0x400
#define MS_SDL_EVENT_MOUSE_BUTTON_DOWN 0x401
#define MS_SDL_EVENT_MOUSE_BUTTON_UP 0x402
#define MS_SDL_EVENT_MOUSE_WHEEL 0x403
#define MS_SDL_EVENT_GAMEPAD_BUTTON_DOWN 0x651
#define MS_SDL_EVENT_GAMEPAD_ADDED 0x653

#define MS_SDLK_RETURN 0xdu
#define MS_SDLK_ESCAPE 0x1bu
#define MS_SDLK_BACKSPACE 0x8u
#define MS_SDLK_TAB 0x9u
#define MS_SDLK_SPACE 0x20u
#define MS_SDLK_DELETE 0x7fu
#define MS_SDLK_LEFT 0x40000050u
#define MS_SDLK_RIGHT 0x4000004fu
#define MS_SDLK_UP 0x40000052u
#define MS_SDLK_DOWN 0x40000051u
#define MS_SDLK_HOME 0x4000004au
#define MS_SDLK_END 0x4000004du
#define MS_SDLK_A 0x61u
#define MS_SDLK_C 0x63u
#define MS_SDLK_V 0x76u
#define MS_SDLK_X 0x78u
#define MS_SDLK_KP_ENTER 0x40000058u
#define MS_SDL_KMOD_SHIFT 0x3u
#define MS_SDL_KMOD_CTRL 0xc0u
#define MS_SDL_KMOD_ALT 0x300u

#define MS_SDL_BUTTON_LEFT 1
#define MS_SDL_WINDOW_HIDDEN 0x8ull
#define MS_SDL_WINDOW_HIGH_PIXEL_DENSITY 0x2000ull
#define MS_SDL_WINDOWPOS_CENTERED 0x2fff0000
#define MS_SDL_PIXELFORMAT_ARGB8888 0x16362004u
#define MS_SDL_PIXELFORMAT_XRGB8888 0x16161804u
#define MS_SDL_SYSTEM_CURSOR_DEFAULT 0
#define MS_SDL_SYSTEM_CURSOR_TEXT 1
#define MS_SDL_SYSTEM_CURSOR_WAIT 2
#define MS_SDL_SYSTEM_CURSOR_POINTER 0xb
#define MS_SDL_SYSTEM_THEME_LIGHT 1
#define MS_SDL_SYSTEM_THEME_DARK 2
#define MS_SDL_GAMEPAD_BUTTON_SOUTH 0
#define MS_SDL_GAMEPAD_BUTTON_EAST 1
#define MS_SDL_GAMEPAD_BUTTON_DPAD_UP 0xb
#define MS_SDL_GAMEPAD_BUTTON_DPAD_DOWN 0xc
#define MS_SDL_GAMEPAD_BUTTON_DPAD_LEFT 0xd
#define MS_SDL_GAMEPAD_BUTTON_DPAD_RIGHT 0xe
#define MS_SDL_SCALEMODE_LINEAR 1
#define MS_TTF_HINTING_NORMAL 0
#define MS_TTF_HINTING_LIGHT 1
#define MS_TTF_HINTING_NONE 3
#define MS_TTF_HINTING_LIGHT_SUBPIXEL 4
#define MS_TTF_STYLE_BOLD 1

#define MS_SDL_HINT_FRAMEBUFFER_ACCELERATION "SDL_FRAMEBUFFER_ACCELERATION"
#define MS_SDL_HINT_VIDEO_DRIVER "SDL_VIDEO_DRIVER"
#define MS_SDL_HINT_WINDOW_ACTIVATE_WHEN_SHOWN "SDL_WINDOW_ACTIVATE_WHEN_SHOWN"
#define MS_SDL_HINT_APP_ID "SDL_APP_ID"
#define MS_SDL_HINT_APP_NAME "SDL_APP_NAME"
#define MS_SDL_HINT_VIDEO_ALLOW_SCREENSAVER "SDL_VIDEO_ALLOW_SCREENSAVER"
#define MS_SDL_HINT_IME_IMPLEMENTED_UI "SDL_IME_IMPLEMENTED_UI"
#define MS_SDL_PROP_WINDOW_X11_WINDOW_NUMBER "SDL.window.x11.window"
#define MS_SDL_PROP_WINDOW_X11_DISPLAY_POINTER "SDL.window.x11.display"

/* The functions, as pointers loaded with dlsym (each its SDL name). */
#define MS_SDL_FUNCTIONS(X) \
    X(bool, SDL_Init, (uint32_t flags)) \
    X(void, SDL_Quit, (void)) \
    X(const char *, SDL_GetError, (void)) \
    X(bool, SDL_SetHint, (const char *name, const char *value)) \
    X(MS_Window *, SDL_CreateWindow, (const char *title, int w, int h, uint64_t flags)) \
    X(void, SDL_DestroyWindow, (MS_Window *window)) \
    X(MS_Surface *, SDL_GetWindowSurface, (MS_Window *window)) \
    X(bool, SDL_UpdateWindowSurface, (MS_Window *window)) \
    X(float, SDL_GetWindowDisplayScale, (MS_Window *window)) \
    X(float, SDL_GetWindowPixelDensity, (MS_Window *window)) \
    X(bool, SDL_SetWindowSize, (MS_Window *window, int w, int h)) \
    X(bool, SDL_SetWindowPosition, (MS_Window *window, int x, int y)) \
    X(bool, SDL_ShowWindow, (MS_Window *window)) \
    X(bool, SDL_RaiseWindow, (MS_Window *window)) \
    X(bool, SDL_SetWindowIcon, (MS_Window *window, MS_Surface *icon)) \
    X(bool, SDL_WaitEventTimeout, (MS_Event *event, int32_t ms)) \
    X(bool, SDL_StartTextInput, (MS_Window *window)) \
    X(bool, SDL_StopTextInput, (MS_Window *window)) \
    X(bool, SDL_SetTextInputArea, (MS_Window *window, const MS_Rect *rect, int cursor)) \
    X(bool, SDL_SetClipboardText, (const char *text)) \
    X(char *, SDL_GetClipboardText, (void)) \
    X(void, SDL_free, (void *p)) \
    X(bool, SDL_OpenURL, (const char *url)) \
    X(int, SDL_GetSystemTheme, (void)) \
    X(MS_Surface *, SDL_CreateSurface, (int w, int h, uint32_t format)) \
    X(void, SDL_DestroySurface, (MS_Surface *surface)) \
    X(MS_Surface *, SDL_ConvertSurface, (MS_Surface *surface, uint32_t format)) \
    X(bool, SDL_BlitSurfaceScaled, (MS_Surface *src, const MS_Rect *src_rect, MS_Surface *dst, const MS_Rect *dst_rect, int mode)) \
    X(MS_IOStream *, SDL_IOFromConstMem, (const void *mem, size_t size)) \
    X(MS_Surface *, SDL_LoadPNG_IO, (MS_IOStream *src, bool close)) \
    X(MS_Cursor *, SDL_CreateSystemCursor, (int id)) \
    X(bool, SDL_SetCursor, (MS_Cursor *cursor)) \
    X(uint16_t, SDL_GetModState, (void)) \
    X(uint32_t, SDL_GetWindowProperties, (MS_Window *window)) \
    X(int64_t, SDL_GetNumberProperty, (uint32_t props, const char *name, int64_t fallback)) \
    X(void *, SDL_GetPointerProperty, (uint32_t props, const char *name, void *fallback)) \
    X(uint32_t, SDL_GetPrimaryDisplay, (void)) \
    X(bool, SDL_GetDisplayUsableBounds, (uint32_t display, MS_Rect *rect)) \
    X(const char *, SDL_GetCurrentVideoDriver, (void)) \
    X(MS_Gamepad *, SDL_OpenGamepad, (uint32_t id)) \
    X(uint64_t, SDL_GetTicks, (void))

#define MS_TTF_FUNCTIONS(X) \
    X(bool, TTF_Init, (void)) \
    X(void, TTF_Quit, (void)) \
    X(MS_Font *, TTF_OpenFont, (const char *file, float size)) \
    X(MS_Font *, TTF_OpenFontIO, (MS_IOStream *src, bool close, float size)) \
    X(void, TTF_CloseFont, (MS_Font *font)) \
    X(void, TTF_SetFontHinting, (MS_Font *font, int hinting)) \
    X(void, TTF_SetFontStyle, (MS_Font *font, uint32_t style)) \
    X(bool, TTF_AddFallbackFont, (MS_Font *font, MS_Font *fallback)) \
    X(bool, TTF_SetFontLanguage, (MS_Font *font, const char *language)) \
    X(bool, TTF_GetStringSize, (MS_Font *font, const char *text, size_t length, int *w, int *h)) \
    X(bool, TTF_MeasureString, (MS_Font *font, const char *text, size_t length, int max_width, int *width, size_t *measured)) \
    X(MS_Surface *, TTF_RenderText_Blended, (MS_Font *font, const char *text, size_t length, MS_Color fg)) \
    X(int, TTF_GetFontAscent, (const MS_Font *font)) \
    X(int, TTF_GetFontHeight, (const MS_Font *font)) \
    X(bool, TTF_FontHasGlyph, (MS_Font *font, uint32_t ch))

#endif
