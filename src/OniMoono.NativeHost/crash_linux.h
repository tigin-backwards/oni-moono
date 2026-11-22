/* Native crash records of the Linux player (crash_linux.c; ARCHITECTURE §7, "Native crashes"). */
#ifndef MOONO_CRASH_LINUX_H
#define MOONO_CRASH_LINUX_H

/* Before the runtime starts: forks the game, a child of this process, which returns here with
 * the signal handlers, the dump settings and standard error through the watcher in place; this
 * process stays the watcher and never returns (it exits with the game's status, after the
 * record and the crash window). With ONI_MOONO_CRASH_WATCH=off it returns at once, no fork.
 * `exe`: this executable's path; `argc`/`argv`: the command line (for --run-home, --log,
 * --crash-dumps). */
void crash_start(const char *exe, int argc, char **argv);

/* The runtime returned the app's exit code: an exit the watcher does not report. */
void crash_exiting(int code);

/* `OniMoono --crash-window FD`: the watcher, started again in place after a crash, showing the
 * crash window for the report in FD (crash_window_linux.c). */
int crash_window_main(int argc, char **argv);

/* `OniMoono --crash-dialog-render OUT.png ...` (crash_window_linux.c). */
int crash_dialog_render(int argc, char **argv);

#endif
