/* What the native crash records of the Windows and the Linux player share (crash.c, crash_linux.c;
 * ARCHITECTURE §7, "Native crashes"): text into preallocated buffers, and the runtime's text on
 * standard error. Plain C, no platform types, nothing that allocates. */
#ifndef MOONO_CRASH_COMMON_H
#define MOONO_CRASH_COMMON_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Text into a preallocated buffer, always NUL-terminated, cut at its end. */
typedef struct
{
    char *at;
    uint32_t length, most;
} crash_out;

void crash_put(crash_out *o, const char *s);
void crash_put_n(crash_out *o, const char *s, size_t n);
void crash_put_hex(crash_out *o, uint64_t v, int digits);   /* 0x, upper case, at least `digits` */
void crash_put_dec(crash_out *o, uint64_t v);
int crash_starts_with(const char *line, const char *prefix);

#define CRASH_RUNTIME_TEXT_MOST 4096

/* The runtime's text on the game's standard error, a line at a time: from "Fatal error.",
 * "Process terminated.", "Stack overflow." or "Unhandled exception." on, up to 40 lines, each as
 * "[engine] native crash: runtime: LINE"; and an unhandled managed exception's first two lines. */
typedef struct
{
    int keeping;
    uint32_t kept_lines;
    char text[CRASH_RUNTIME_TEXT_MOST];
    uint32_t length;
    char managed_line[1024], managed_at[512];
    const char *eol;              /* each kept line's end: "\r\n" (Windows' log) when null */
} crash_runtime_text;

/* One line (no line break). Returns 1 when it starts the kept text with one of the runtime's
 * fatal-error lines (not "Unhandled exception."), which on Windows asks for the dump. `extra`:
 * more prefixes that start the kept text (glibc's own lines on Linux; "~TEXT": TEXT anywhere in
 * the line), NULL-terminated, or NULL. */
int crash_runtime_line(crash_runtime_text *r, const char *line, const char *const *extra);

/* An unhandled managed exception as the error line: "System.X in Type.Method: message";
 * 0 (nothing written) when there was none. */
int crash_managed_error(const crash_runtime_text *r, crash_out *o);

#ifdef __cplusplus
}
#endif

#endif
