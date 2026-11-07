/* The crash window of the Windows player (crash_dialog.cpp; ARCHITECTURE §7, "Native crashes"). */
#pragma once
#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

enum { CRASH_ENGLISH, CRASH_CHINESE };

/* What the watcher knows of a crash, for the window. */
typedef struct
{
    int language;                /* CRASH_ENGLISH or CRASH_CHINESE: the game's language */
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
