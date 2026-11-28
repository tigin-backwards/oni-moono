/* The crash window of the Windows player (crash_dialog.cpp; ARCHITECTURE §7, "Native crashes"). */
#pragma once
#include <windows.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

/* crash_window.h's CW_ENGLISH, ... in the same order. */
enum { CRASH_ENGLISH, CRASH_CHINESE, CRASH_KOREAN, CRASH_RUSSIAN, CRASH_GERMAN, CRASH_PORTUGUESE, CRASH_JAPANESE, CRASH_FRENCH, CRASH_SPANISH };

/* The window's language from the game's language code ("zh", "ko", "ru", "en", ...), or, before
 * the game gave one (a crash or a blocked file before its main menu, #135), from Windows' display
 * language. */
static inline int crash_language(const wchar_t *code)
{
    static const wchar_t codes[][3] = { L"en", L"zh", L"ko", L"ru", L"de", L"pt", L"ja", L"fr", L"es" };
    if (code && code[0])
    {
        for (int i = CRASH_CHINESE; i < (int)(sizeof codes / sizeof codes[0]); i++)
            if (_wcsnicmp(code, codes[i], 2) == 0) return i;
        return CRASH_ENGLISH;
    }
    switch (PRIMARYLANGID(GetUserDefaultUILanguage()))
    {
    case LANG_CHINESE: return CRASH_CHINESE;
    case LANG_KOREAN: return CRASH_KOREAN;
    case LANG_RUSSIAN: return CRASH_RUSSIAN;
    case LANG_GERMAN: return CRASH_GERMAN;
    case LANG_PORTUGUESE: return CRASH_PORTUGUESE;
    case LANG_JAPANESE: return CRASH_JAPANESE;
    case LANG_FRENCH: return CRASH_FRENCH;
    case LANG_SPANISH: return CRASH_SPANISH;
    default: return CRASH_ENGLISH;
    }
}

/* What the watcher knows of a crash, for the window. */
typedef struct
{
    int language;                /* CRASH_ENGLISH, ...: the game's language */
    wchar_t error[1024];         /* the error line, from the record; empty: exit_code alone */
    DWORD exit_code;
    wchar_t version[64];         /* OniMoono's release (or commit), as the game gave it */
    wchar_t game_build[32];
    wchar_t gpu[128];            /* the renderer the game used (GL_RENDERER), or empty */
    LONG mods;                   /* enabled mods, -1 unknown */
    ULONG64 play_seconds;        /* the session's length, ~0 unknown */
    wchar_t log_path[1024];
    wchar_t dump_path[1024];     /* empty: no dump */
    ULONG64 dump_size;
    SYSTEMTIME crashed;          /* local time */
    RECT monitor;                /* the game window's monitor (screen rectangle); empty: unknown */
    int activate;                /* 0: shown without taking the focus (test runs) */
} crash_report_t;

/* Shows the window for a crash and returns once the player closes it. */
void crash_dialog_show(const crash_report_t *report);

/* `OniMoono.exe --crash-dialog-render OUT.png ...`: the window drawn to a PNG, shown nowhere. */
int crash_dialog_render(int argc, wchar_t **argv);

#ifdef __cplusplus
}
#endif
