/* What the native crash records of both players share (crash_common.h). */
#include "crash_common.h"
#include <string.h>

void crash_put(crash_out *o, const char *s)
{
    while (*s && o->length + 1 < o->most) o->at[o->length++] = *s++;
    if (o->most) o->at[o->length] = 0;
}

void crash_put_n(crash_out *o, const char *s, size_t n)
{
    while (n-- && *s && o->length + 1 < o->most) o->at[o->length++] = *s++;
    if (o->most) o->at[o->length] = 0;
}

void crash_put_hex(crash_out *o, uint64_t v, int digits)
{
    char b[20];
    int n = 0;
    do { b[n++] = "0123456789ABCDEF"[v & 15]; v >>= 4; } while (v || n < digits);
    crash_put(o, "0x");
    char s[2] = { 0, 0 };
    while (n) { s[0] = b[--n]; crash_put(o, s); }
}

void crash_put_dec(crash_out *o, uint64_t v)
{
    char b[24];
    int n = 0;
    do { b[n++] = (char)('0' + v % 10); v /= 10; } while (v);
    char s[2] = { 0, 0 };
    while (n) { s[0] = b[--n]; crash_put(o, s); }
}

int crash_starts_with(const char *line, const char *prefix)
{
    while (*prefix) if (*line++ != *prefix++) return 0;
    return 1;
}

static void copy_cut(char *to, size_t most, const char *from)
{
    size_t n = strlen(from);
    if (n >= most) n = most - 1;
    memcpy(to, from, n);
    to[n] = 0;
}

int crash_runtime_line(crash_runtime_text *r, const char *line, const char *const *extra)
{
    int fatal = 0;
    if (!r->keeping && (crash_starts_with(line, "Fatal error.") || crash_starts_with(line, "Process terminated.") || crash_starts_with(line, "Stack overflow.")))
    {
        r->keeping = 1;
        fatal = 1;
    }
    /* A managed exception nothing caught: the player logs it; this says it ended the process. */
    else if (!r->keeping && crash_starts_with(line, "Unhandled exception."))
    {
        r->keeping = 1;
        copy_cut(r->managed_line, sizeof r->managed_line, line);
    }
    else if (r->keeping && r->managed_line[0] && !r->managed_at[0] && crash_starts_with(line, "   at "))
        copy_cut(r->managed_at, sizeof r->managed_at, line + 6);
    else if (!r->keeping && extra)
        for (const char *const *p = extra; *p; p++)
            if ((*p)[0] == '~' ? strstr(line, *p + 1) != NULL : crash_starts_with(line, *p)) { r->keeping = 1; break; }
    size_t line_length = strlen(line);
    if (r->keeping && r->kept_lines < 40 && r->length + line_length + 32 < CRASH_RUNTIME_TEXT_MOST)
    {
        crash_out o = { r->text, r->length, CRASH_RUNTIME_TEXT_MOST };
        crash_put(&o, "[engine] native crash: runtime: ");
        crash_put(&o, line);
        crash_put(&o, r->eol ? r->eol : "\r\n");
        r->length = o.length;
        r->kept_lines++;
    }
    return fatal;
}

int crash_managed_error(const crash_runtime_text *r, crash_out *o)
{
    if (!r->managed_line[0]) return 0;
    /* "Unhandled exception. System.X: message" and "   at Namespace.Type.Method(arguments)" */
    const char *what = r->managed_line + strlen("Unhandled exception.");
    while (*what == ' ') what++;
    const char *colon = strstr(what, ": ");
    crash_put_n(o, what, colon ? (size_t)(colon - what) : strlen(what));
    if (r->managed_at[0])
    {
        const char *paren = strchr(r->managed_at, '(');
        crash_put(o, " in ");
        crash_put_n(o, r->managed_at, paren ? (size_t)(paren - r->managed_at) : strlen(r->managed_at));
    }
    if (colon) { crash_put(o, ": "); crash_put(o, colon + 2); }
    return 1;
}
