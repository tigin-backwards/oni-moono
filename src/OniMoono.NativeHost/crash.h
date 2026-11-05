/* Native crash records of the Windows player (crash.c; ARCHITECTURE §7, "Native crashes"). */
#pragma once
#include <windows.h>

/* Before the runtime starts: the watcher process, the standard error pipe through it and the
 * shared record. `exe` is this executable's path. The standard error the process was started
 * with (NULL when none), for the host's own start-up failures. */
HANDLE crash_start(const wchar_t *exe);

/* The runtime returned the app's exit code: an exit the watcher does not report. */
void crash_exiting(DWORD code);

/* `OniMoono.exe --crash-watch ...`: the watcher process's own main. */
int crash_watch(int argc, wchar_t **argv);
