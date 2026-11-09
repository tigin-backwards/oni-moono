/* Native crash records of the Windows player (ARCHITECTURE §7, "Native crashes").
 *
 * A crash in native code (the GPU driver, Klei's SimDLL, a mod's library, the runtime) has no
 * managed exception, so the player's log just stops. This leaves a record of it in Player.log: the
 * exception and the module and offset it happened at, the thread, a short native stack, how the
 * process ended, and a small minidump beside the log.
 *
 * What sees what (measured with .NET 10; ARCHITECTURE has the table):
 *  - A fault in native code called from managed code (a P/Invoke: the GL driver on the main or GL
 *    thread, SimDLL) reaches no unhandled-exception filter: the runtime's personality routine finds
 *    a corrupting exception reaching a managed frame, prints "Fatal error." to standard error and
 *    calls TerminateProcess. Only a vectored handler sees it, at first chance, where native code
 *    may still handle it. So the vectored handler, added after the runtime's, only notes it in the
 *    shared record; the runtime's own vectored handler comes first and ends the search for faults
 *    in managed code (null references, the mods' stack probes), so those never reach it.
 *  - A fault on a thread with no managed frames reaches the unhandled-exception filter, which is
 *    the runtime's, set at its start; ours replaces it once the runtime is up and calls it after.
 *  - __fastfail, a /GS check, abort() of the static C runtime and TerminateProcess skip every
 *    handler: only the exit code tells.
 * So the record is written by a watcher, a second OniMoono.exe started before the runtime, which
 * waits for the game to end. When it ends without the player's own exit, the watcher appends what
 * the game noted (a fault noted at first chance when its exit code is that fault's), the runtime's
 * "Fatal error." text from standard error (which passes through the watcher on its way to where it
 * went before), and the exit code. Writing after the end also keeps the lines from being written
 * over: the log's own writer writes at its own offset. The watcher writes the minidump from outside
 * while the game is suspended, when the game asks from its filter, or when the runtime starts its
 * "Fatal error." text, so a fault the native code handles never costs a dump.
 *
 * In the crashing process nothing allocates: the fault is copied into the shared record and a
 * thread made at start formats it there (the faulting thread may have no stack left), behind a
 * structured exception handler of its own. */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dbghelp.h>
#include <intrin.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "crash.h"
#include "crash_dialog.h"

#define TEXT_MOST 12288
#define RUNTIME_TEXT_MOST 4096
#define PATH_MOST 1024
#define PREFIX_MOST 4
#define FRAMES_MOST 24   /* frames (or runs of one) in the stack line */
#define WALK_MOST 96

#define CLR_EXCEPTION 0xE0434352u
#define STATUS_HEAP_CORRUPTION_CODE 0xC0000374u
#define STATUS_STACK_BUFFER_OVERRUN_CODE 0xC0000409u

enum { SEEN_NONE, SEEN_FIRST_CHANCE, SEEN_UNHANDLED };

/* The record the game and the watcher share (a page-aligned view, so the CONTEXT is aligned). */
typedef struct
{
    CONTEXT context;
    EXCEPTION_RECORD record;
    EXCEPTION_POINTERS pointers;     /* the two above, at the game's addresses */
    ULONG64 pointers_in_game;        /* &pointers in the game, for MINIDUMP_EXCEPTION_INFORMATION */
    volatile LONG installed;         /* the player gave the log's path */
    volatile LONG clean;             /* the player exits on purpose, with clean_code */
    DWORD clean_code;
    volatile LONG seen;              /* SEEN_*: what text holds */
    DWORD seen_code, seen_thread;
    ULONGLONG seen_tick;
    DWORD dumps;                     /* 0: no dump, 1: a minidump, 2: one with the process's memory */
    DWORD text_length;
    char text[TEXT_MOST];            /* the fault's lines, UTF-8 */
    wchar_t log_path[PATH_MOST];
    wchar_t prefix[PREFIX_MOST][PATH_MOST / 2];   /* personal paths, as the log hides them */
    wchar_t replacement[PREFIX_MOST][32];
    wchar_t fault_module[64];        /* the noted fault's module, for the crash window */
    /* For the crash window (moono_crash_describe), as the game gives them. */
    wchar_t version[64], game_build[32], language[16], gpu[128];
    volatile LONG mods;              /* enabled mods; -1 unknown */
    volatile LONG dialog_off;        /* --crash-dialog off (or a run with --run-home) */
    volatile ULONG64 window;         /* the game's window (HWND), 0 before it opens */
} shared_t;

/* ---- text into preallocated buffers ------------------------------------------------------- */

typedef struct { char *at; DWORD length, most; } out_t;

static void put(out_t *o, const char *s)
{
    while (*s && o->length + 1 < o->most) o->at[o->length++] = *s++;
    o->at[o->length] = 0;
}

static void put_hex(out_t *o, ULONG64 v, int digits)
{
    char b[20];
    int n = 0;
    do { b[n++] = "0123456789ABCDEF"[v & 15]; v >>= 4; } while (v || n < digits);
    put(o, "0x");
    char s[2] = { 0, 0 };
    while (n) { s[0] = b[--n]; put(o, s); }
}

static void put_dec(out_t *o, ULONG64 v)
{
    char b[24];
    int n = 0;
    do { b[n++] = (char)('0' + v % 10); v /= 10; } while (v);
    char s[2] = { 0, 0 };
    while (n) { s[0] = b[--n]; put(o, s); }
}

static void put_wide(out_t *o, const wchar_t *s)
{
    if (o->length + 1 >= o->most) return;
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, o->at + o->length, (int)(o->most - o->length - 1), NULL, NULL);
    if (n > 0) o->length += (DWORD)n - 1;
    o->at[o->length] = 0;
}

static int same_char(wchar_t a, wchar_t b)
{
    if (a == L'/') a = L'\\';
    if (b == L'/') b = L'\\';
    if (a >= L'A' && a <= L'Z') a = (wchar_t)(a + 32);
    if (b >= L'A' && b <= L'Z') b = (wchar_t)(b + 32);
    return a == b;
}

/* A path with the personal folders replaced, as the log's own lines have them. */
static void put_path(out_t *o, const shared_t *s, const wchar_t *path)
{
    for (int i = 0; i < PREFIX_MOST; i++)
    {
        const wchar_t *prefix = s->prefix[i];
        if (!prefix[0]) continue;
        size_t n = 0;
        while (prefix[n] && path[n] && same_char(prefix[n], path[n])) n++;
        if (prefix[n] == 0 && (path[n] == 0 || path[n] == L'\\' || path[n] == L'/'))
        {
            put_wide(o, s->replacement[i]);
            put_wide(o, path + n);
            return;
        }
    }
    put_wide(o, path);
}

static const char *exception_name(DWORD code)
{
    switch (code)
    {
    case 0xC0000005: return "access violation";
    case 0xC0000006: return "in-page error";
    case 0xC000001D: return "illegal instruction";
    case 0xC0000096: return "privileged instruction";
    case 0xC0000094: return "integer division by zero";
    case 0xC0000095: return "integer overflow";
    case 0xC00000FD: return "stack overflow";
    case 0x80000002: return "misaligned data";
    case 0xC000008C: return "array bounds exceeded";
    case 0xC000008D: case 0xC000008E: case 0xC000008F: case 0xC0000090: case 0xC0000091: case 0xC0000092: case 0xC0000093:
    case 0xC00002B4: case 0xC00002B5: return "floating-point exception";
    case 0xC0000374: return "heap corruption";
    case 0xC0000409: return "fast fail: __fastfail, a /GS check, abort() or an invalid parameter";
    case 0xC0000420: return "assertion failure";
    case 0xC0000602: return "fail fast";
    case 0x80000003: return "breakpoint";
    case 0xE06D7363: return "C++ exception nothing caught";
    case 0xE0434352: return ".NET exception";
    case 0x80131623: return ".NET Environment.FailFast";
    case 0x800703E9: return ".NET stack overflow";
    case 0x80131506: return ".NET internal error";
    case 0xC000013A: return "Ctrl+C";
    case 0x40010004: return "ended by Windows (log off or shut down)";
    case 0xFFFFFFFF: return "ended from outside (Task Manager or another program)";
    case 0xCFFFFFFF: return "ended by Windows as not responding (a hang)";
    default: return NULL;
    }
}

/* Faults that end the process unless something handles them. */
static int is_fault(DWORD code)
{
    switch (code)
    {
    case 0xC0000005: case 0xC0000006: case 0xC000001D: case 0xC0000096: case 0xC0000094: case 0xC0000095:
    case 0xC00000FD: case 0x80000002: case 0xC000008C: case 0xC000008D: case 0xC000008E: case 0xC000008F:
    case 0xC0000090: case 0xC0000091: case 0xC0000092: case 0xC0000093: case 0xC00002B4: case 0xC00002B5:
    case 0xC0000374: case 0xC0000409:
        return 1;
    default:
        return 0;
    }
}

/* ---- the game's side ---------------------------------------------------------------------- */

static shared_t *shared;
static HANDLE dump_wanted, dump_written;   /* events: game to watcher, watcher to game */
static HANDLE watcher;                      /* the watcher process, once started */
static DWORD main_thread;
static HANDLE format_wanted, format_done;
static DWORD formatter_thread;
static volatile LONG noting;
static LPTOP_LEVEL_EXCEPTION_FILTER runtime_filter;
typedef HRESULT (WINAPI *get_thread_description_fn)(HANDLE, PWSTR *);
static get_thread_description_fn get_thread_description;

static wchar_t module_path[PATH_MOST];
static CONTEXT walk;
static ULONG64 frames[WALK_MOST];   /* 0: outside any module */

/* The loaded module holding an address, its file name and its offset there. */
static ULONG64 module_of(ULONG64 address, const wchar_t **name)
{
    PVOID base = NULL;
    if (!RtlPcToFileHeader((PVOID)address, &base) || base == NULL) return 0;
    DWORD n = GetModuleFileNameW((HMODULE)base, module_path, PATH_MOST);
    if (n == 0 || n >= PATH_MOST) { module_path[0] = 0; *name = L"?"; return (ULONG64)base; }
    const wchar_t *slash = wcsrchr(module_path, L'\\');
    *name = slash ? slash + 1 : module_path;
    return (ULONG64)base;
}

/* The file version from a loaded module's own version resource (no file read, no allocation). */
static int module_version(ULONG64 base, WORD version[4])
{
    HRSRC resource = FindResourceW((HMODULE)base, MAKEINTRESOURCEW(1), MAKEINTRESOURCEW(16) /* RT_VERSION */);
    if (!resource) return 0;
    DWORD size = SizeofResource((HMODULE)base, resource);
    HGLOBAL loaded = LoadResource((HMODULE)base, resource);
    const BYTE *data = loaded ? (const BYTE *)LockResource(loaded) : NULL;
    if (!data) return 0;
    for (DWORD i = 0; i + sizeof(VS_FIXEDFILEINFO) <= size; i += 4)
    {
        const VS_FIXEDFILEINFO *info = (const VS_FIXEDFILEINFO *)(data + i);
        if (info->dwSignature != 0xFEEF04BD) continue;
        version[0] = HIWORD(info->dwFileVersionMS); version[1] = LOWORD(info->dwFileVersionMS);
        version[2] = HIWORD(info->dwFileVersionLS); version[3] = LOWORD(info->dwFileVersionLS);
        return 1;
    }
    return 0;
}

static void put_frame(out_t *o, ULONG64 ip)
{
    const wchar_t *name;
    ULONG64 base = module_of(ip, &name);
    put_wide(o, name);
    put(o, "+");
    put_hex(o, ip - base, 1);
}

/* The fault in shared->record and ->context as two lines: what, where, which thread; the stack. */
static void format_fault(void)
{
    out_t o = { shared->text, 0, TEXT_MOST };
    const EXCEPTION_RECORD *r = &shared->record;
    DWORD code = r->ExceptionCode;
    ULONG64 ip = (ULONG64)r->ExceptionAddress;
    put(&o, "[engine] native crash: ");
    put_hex(&o, code, 8);
    const char *name = exception_name(code);
    if (name) { put(&o, " "); put(&o, name); }
    if ((code == 0xC0000005 || code == 0xC0000006) && r->NumberParameters >= 2)
    {
        ULONG_PTR how = r->ExceptionInformation[0];
        put(&o, how == 1 ? " (writing " : how == 8 ? " (executing " : " (reading ");
        put_hex(&o, r->ExceptionInformation[1], 1);
        put(&o, ")");
    }
    put(&o, " in ");
    const wchar_t *module;
    ULONG64 base = module_of(ip, &module);
    shared->fault_module[0] = 0;
    if (base)
    {
        wcsncpy_s(shared->fault_module, 64, module, _TRUNCATE);
        put_wide(&o, module);
        put(&o, "+");
        put_hex(&o, ip - base, 1);
        WORD v[4];
        put(&o, " (");
        if (module_version(base, v))
        {
            for (int i = 0; i < 4; i++) { if (i) put(&o, "."); put_dec(&o, v[i]); }
            put(&o, ", ");
        }
        put_path(&o, shared, module_path);
        put(&o, ")");
    }
    else
    {
        put_hex(&o, ip, 1);
        put(&o, " (no module: generated code)");
    }
    DWORD thread = shared->seen_thread;
    put(&o, " on thread ");
    put_dec(&o, thread);
    if (thread == main_thread) put(&o, " (main)");
    /* The thread's name (.NET names its threads' descriptions); the heap may be the broken thing. */
    if (get_thread_description && code != STATUS_HEAP_CORRUPTION_CODE)
    {
        HANDLE handle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, thread);
        PWSTR description = NULL;
        if (handle && SUCCEEDED(get_thread_description(handle, &description)) && description)
        {
            if (description[0]) { put(&o, " \""); put_wide(&o, description); put(&o, "\""); }
            LocalFree(description);
        }
        if (handle) CloseHandle(handle);
    }
    put(&o, "\r\n");

    /* The native stack: module+offset per frame, through the unwind data (the runtime registers
     * its generated code's). Runs of frames outside any module (managed code) are counted. */
    put(&o, "[engine] native crash stack: ");
    walk = shared->context;
    int count = 0;
    while (count < WALK_MOST && walk.Rip)
    {
        ULONG64 at = walk.Rip, sp = walk.Rsp;
        PVOID image_base = NULL;
        frames[count++] = RtlPcToFileHeader((PVOID)at, &image_base) && image_base ? at : 0;
        DWORD64 image = 0;
        PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(at, &image, NULL);
        if (function)
        {
            PVOID handler_data;
            DWORD64 frame;
            RtlVirtualUnwind(UNW_FLAG_NHANDLER, image, at, function, &walk, &handler_data, &frame, NULL);
        }
        else
        {
            walk.Rip = *(ULONG64 *)walk.Rsp;
            walk.Rsp += 8;
        }
        if (walk.Rsp <= sp) break;
    }
    /* Runs of one frame (recursion) and of frames outside any module (generated code) as one. */
    for (int i = 0, groups = 0; i < count && groups < FRAMES_MOST; groups++)
    {
        int run = 1;
        while (i + run < count && frames[i + run] == frames[i]) run++;
        if (groups) put(&o, ", ");
        if (frames[i]) put_frame(&o, frames[i]);
        else { put_dec(&o, (ULONG64)run); put(&o, run == 1 ? " frame of generated code" : " frames of generated code"); }
        if (frames[i] && run > 1) { put(&o, " (x"); put_dec(&o, (ULONG64)run); put(&o, ")"); }
        i += run;
    }
    if (count == WALK_MOST && walk.Rip) put(&o, ", ...");
    put(&o, "\r\n");
    shared->text_length = o.length;
}

static DWORD WINAPI formatter(void *unused)
{
    (void)unused;
    while (WaitForSingleObject(format_wanted, INFINITE) == WAIT_OBJECT_0)
    {
        __try
        {
            format_fault();
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            /* Keep what was written; the stack is the likely part to fail (a broken stack). */
            out_t o = { shared->text, shared->text_length ? shared->text_length : (DWORD)strlen(shared->text), TEXT_MOST };
            put(&o, " (the rest could not be read)\r\n");
            shared->text_length = o.length;
        }
        SetEvent(format_done);
    }
    return 0;
}

/* Copies the fault into the shared record and has the formatter write it out; with `how`
 * SEEN_UNHANDLED (it ends the process), the watcher then writes the minidump. */
static void note(EXCEPTION_POINTERS *e, LONG how)
{
    if (GetCurrentThreadId() == formatter_thread) return;
    if (InterlockedCompareExchange(&noting, 1, 0) != 0) return; /* another thread's fault is being noted */
    shared->record = *e->ExceptionRecord;
    shared->record.ExceptionRecord = NULL;
    shared->context = *e->ContextRecord;
    shared->seen_code = e->ExceptionRecord->ExceptionCode;
    shared->seen_thread = GetCurrentThreadId();
    shared->seen_tick = GetTickCount64();
    shared->text_length = 0;
    shared->text[0] = 0;
    SetEvent(format_wanted);
    WaitForSingleObject(format_done, how == SEEN_UNHANDLED ? 10000 : 2000);
    InterlockedExchange(&shared->seen, how);
    if (how == SEEN_UNHANDLED && watcher && shared->dumps)
    {
        HANDLE waits[2] = { dump_written, watcher };
        SetEvent(dump_wanted);
        WaitForMultipleObjects(2, waits, FALSE, 60000);
    }
    else if (how == SEEN_UNHANDLED && !watcher)
    {
        /* No watcher to write it after the end: written now, as the log's last lines. */
        HANDLE log = CreateFileW(shared->log_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (log != INVALID_HANDLE_VALUE)
        {
            DWORD done;
            WriteFile(log, shared->text, shared->text_length, &done, NULL);
            FlushFileBuffers(log);
            CloseHandle(log);
        }
    }
    InterlockedExchange(&noting, 0);
}

/* After the runtime's vectored handler: a fault it left alone, so not in managed code. */
static LONG CALLBACK first_chance(EXCEPTION_POINTERS *e)
{
    DWORD code = e->ExceptionRecord->ExceptionCode;
    if (!is_fault(code)) return EXCEPTION_CONTINUE_SEARCH;
    /* Heap corruption is raised just before the heap ends the process; nothing handles it. */
    note(e, code == STATUS_HEAP_CORRUPTION_CODE || code == STATUS_STACK_BUFFER_OVERRUN_CODE ? SEEN_UNHANDLED : SEEN_FIRST_CHANCE);
    return EXCEPTION_CONTINUE_SEARCH;
}

/* Nothing handled it: noted, then the runtime's filter (which passes it on to Windows). A managed
 * exception is the player's to log. */
static LONG WINAPI unhandled(EXCEPTION_POINTERS *e)
{
    if (e->ExceptionRecord->ExceptionCode != CLR_EXCEPTION) note(e, SEEN_UNHANDLED);
    return runtime_filter ? runtime_filter(e) : EXCEPTION_CONTINUE_SEARCH;
}

/* ONI_MOONO_CRASH_WATCH=off: no watcher (for debugging the host). */
static int watcher_refused(void)
{
    wchar_t value[8];
    return GetEnvironmentVariableW(L"ONI_MOONO_CRASH_WATCH", value, 8) && wcscmp(value, L"off") == 0;
}

HANDLE crash_start(const wchar_t *exe)
{
    HANDLE err = GetStdHandle(STD_ERROR_HANDLE);
    if (err == INVALID_HANDLE_VALUE) err = NULL;
    /* A player App.Restart started through the Restarter has the first one's pipe as its
     * standard error, which went nowhere before it. */
    wchar_t marker[8];
    if (GetEnvironmentVariableW(L"ONI_MOONO_STDERR", marker, 8) && wcscmp(marker, L"none") == 0 && err && GetFileType(err) == FILE_TYPE_PIPE)
    {
        SetStdHandle(STD_ERROR_HANDLE, NULL);
        err = NULL;
    }
    if (watcher_refused()) return err;

    SECURITY_ATTRIBUTES inherit = { sizeof inherit, NULL, TRUE };
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, &inherit, PAGE_READWRITE, 0, sizeof(shared_t), NULL);
    if (!map) return err;
    shared = (shared_t *)MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(shared_t));
    if (!shared) { CloseHandle(map); return err; }
    shared->pointers.ExceptionRecord = &shared->record;
    shared->pointers.ContextRecord = &shared->context;
    shared->pointers_in_game = (ULONG64)&shared->pointers;
    shared->mods = -1;
    dump_wanted = CreateEventW(&inherit, FALSE, FALSE, NULL);
    dump_written = CreateEventW(&inherit, FALSE, FALSE, NULL);
    HANDLE self = NULL;
    DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), &self,
                    SYNCHRONIZE | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_SUSPEND_RESUME | PROCESS_DUP_HANDLE, TRUE, 0);

    /* Standard error through the watcher (to where it went, if anywhere), for the runtime's
     * "Fatal error." text. Not a console's: a player started from a terminal keeps writing there. */
    /* A pipe with no buffer: a write returns once the watcher has read it, so the runtime, which
     * ends the process right after its text, waits at its second write while the watcher, which
     * reads on only after the minidump, writes it. */
    HANDLE pipe_read = NULL, pipe_write = NULL, err_copy = NULL;
    DWORD type = err ? GetFileType(err) : FILE_TYPE_UNKNOWN;
    if (!err || type == FILE_TYPE_PIPE || type == FILE_TYPE_DISK)
    {
        wchar_t name[96];
        swprintf_s(name, _countof(name), L"\\\\.\\pipe\\oni-moono-stderr-%lu-%llu", GetCurrentProcessId(), GetTickCount64());
        pipe_read = CreateNamedPipeW(name, PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_TYPE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                     1, 0, 0, 0, &inherit);
        if (pipe_read == INVALID_HANDLE_VALUE) pipe_read = NULL;
        if (pipe_read)
        {
            pipe_write = CreateFileW(name, GENERIC_WRITE, 0, &inherit, OPEN_EXISTING, 0, NULL);
            if (pipe_write == INVALID_HANDLE_VALUE) { pipe_write = NULL; CloseHandle(pipe_read); pipe_read = NULL; }
        }
        if (pipe_read && err) DuplicateHandle(GetCurrentProcess(), err, GetCurrentProcess(), &err_copy, 0, TRUE, DUPLICATE_SAME_ACCESS);
    }

    HANDLE list[6];
    int n = 0;
    list[n++] = map; list[n++] = self; list[n++] = dump_wanted; list[n++] = dump_written;
    if (pipe_read) list[n++] = pipe_read;
    if (err_copy) list[n++] = err_copy;
    static wchar_t command[PATH_MOST * 2];
    swprintf_s(command, _countof(command), L"\"%ls\" --crash-watch %llx %llx %llx %llx %llx %llx", exe,
               (ULONG64)(ULONG_PTR)map, (ULONG64)(ULONG_PTR)self, (ULONG64)(ULONG_PTR)dump_wanted, (ULONG64)(ULONG_PTR)dump_written,
               (ULONG64)(ULONG_PTR)pipe_read, (ULONG64)(ULONG_PTR)err_copy);
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &size);
    static BYTE attributes[256];
    STARTUPINFOEXW start = { 0 };
    start.StartupInfo.cb = sizeof start;
    start.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    start.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)attributes;
    PROCESS_INFORMATION process;
    if (size <= sizeof attributes && InitializeProcThreadAttributeList(start.lpAttributeList, 1, 0, &size)
        && UpdateProcThreadAttribute(start.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, list, n * sizeof(HANDLE), NULL, NULL)
        && CreateProcessW(exe, command, NULL, NULL, TRUE, EXTENDED_STARTUPINFO_PRESENT, NULL, NULL, &start.StartupInfo, &process))
    {
        watcher = process.hProcess;
        CloseHandle(process.hThread);
        if (pipe_write)
        {
            SetStdHandle(STD_ERROR_HANDLE, pipe_write);
            /* The runtime writes "Fatal error." with the shared C runtime's _write to stderr's
             * descriptor, which took its handle when ucrtbase.dll loaded (the system libraries
             * this executable links load it first); started without one, stderr has none, so it
             * is opened (on NUL) to have a descriptor to put the pipe in. */
            HMODULE ucrt = GetModuleHandleW(L"ucrtbase.dll");
            typedef FILE *(__cdecl *iob_fn)(unsigned);
            typedef int (__cdecl *fileno_fn)(FILE *);
            typedef errno_t (__cdecl *wfreopen_s_fn)(FILE **, const wchar_t *, const wchar_t *, FILE *);
            typedef int (__cdecl *open_osfhandle_fn)(intptr_t, int);
            typedef int (__cdecl *dup2_fn)(int, int);
            iob_fn iob = ucrt ? (iob_fn)GetProcAddress(ucrt, "__acrt_iob_func") : NULL;
            fileno_fn file_number = ucrt ? (fileno_fn)GetProcAddress(ucrt, "_fileno") : NULL;
            wfreopen_s_fn reopen = ucrt ? (wfreopen_s_fn)GetProcAddress(ucrt, "_wfreopen_s") : NULL;
            open_osfhandle_fn open_handle = ucrt ? (open_osfhandle_fn)GetProcAddress(ucrt, "_open_osfhandle") : NULL;
            dup2_fn dup2 = ucrt ? (dup2_fn)GetProcAddress(ucrt, "_dup2") : NULL;
            if (iob && file_number && reopen && open_handle && dup2)
            {
                FILE *ucrt_stderr = iob(2), *reopened = NULL;
                int target = file_number(ucrt_stderr);
                if (target < 0 && reopen(&reopened, L"NUL", L"w", ucrt_stderr) == 0) target = file_number(ucrt_stderr);
                int fd = target >= 0 ? open_handle((intptr_t)pipe_write, 0x0001 /* _O_WRONLY */ | 0x8000 /* _O_BINARY */) : -1;
                if (fd >= 0) dup2(fd, target);
            }
            if (!err) SetEnvironmentVariableW(L"ONI_MOONO_STDERR", L"none");
        }
    }
    else if (pipe_write) CloseHandle(pipe_write);
    if (size <= sizeof attributes) DeleteProcThreadAttributeList(start.lpAttributeList);
    /* The watcher's own copies are all it needs; the game's later children inherit none of them. */
    if (pipe_read) CloseHandle(pipe_read);
    if (err_copy) CloseHandle(err_copy);
    if (self) CloseHandle(self);
    SetHandleInformation(map, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(dump_wanted, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(dump_written, HANDLE_FLAG_INHERIT, 0);
    return err;
}

void crash_exiting(DWORD code)
{
    if (!shared) return;
    shared->clean_code = code;
    InterlockedExchange(&shared->clean, 1);
}

/* Called by the player (NativeCrashes.cs) once Player.log is open, with the runtime up: the log's
 * path, the main thread, the dump to write (0 none, 1 a minidump, 2 with memory: --crash-dumps
 * full), and the personal folders to hide as
 * "PATH\0REPLACEMENT\0...\0" (empty with --log-paths full). 1 when the watcher runs. */
__declspec(dllexport) int moono_crash_install(const wchar_t *log_path, DWORD main, int dumps, const wchar_t *prefixes)
{
    static int installed;
    if (!shared || installed) return 0;
    installed = 1;
    main_thread = main;
    wcsncpy_s(shared->log_path, PATH_MOST, log_path, _TRUNCATE);
    for (int i = 0; prefixes && *prefixes && i < PREFIX_MOST; i++)
    {
        wcsncpy_s(shared->prefix[i], PATH_MOST / 2, prefixes, _TRUNCATE);
        prefixes += wcslen(prefixes) + 1;
        wcsncpy_s(shared->replacement[i], 32, prefixes, _TRUNCATE);
        prefixes += wcslen(prefixes) + 1;
    }
    shared->dumps = dumps == 2 ? 2 : dumps ? 1 : 0;
    get_thread_description = (get_thread_description_fn)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetThreadDescription");
    format_wanted = CreateEventW(NULL, FALSE, FALSE, NULL);
    format_done = CreateEventW(NULL, FALSE, FALSE, NULL);
    HANDLE thread = CreateThread(NULL, 256 * 1024, formatter, NULL, 0, &formatter_thread);
    if (!thread) return 0;
    CloseHandle(thread);
    /* After the runtime's: its vectored handler runs first, and its filter is called from ours. */
    AddVectoredExceptionHandler(0, first_chance);
    runtime_filter = SetUnhandledExceptionFilter(unhandled);
    InterlockedExchange(&shared->installed, 1);
    return watcher != NULL;
}

/* What the crash window shows, from the player (NativeCrashes.Describe) as it learns it: `version`
 * (the release or commit), `build` (the game's), `language` (its code), `mods` (enabled), `gpu`
 * (GL_RENDERER), `window` (the HWND, hexadecimal) and `dialog` (`off`: no window). */
__declspec(dllexport) void moono_crash_describe(const wchar_t *key, const wchar_t *value)
{
    if (!shared || !key || !value) return;
    if (wcscmp(key, L"version") == 0) wcsncpy_s(shared->version, 64, value, _TRUNCATE);
    else if (wcscmp(key, L"build") == 0) wcsncpy_s(shared->game_build, 32, value, _TRUNCATE);
    else if (wcscmp(key, L"language") == 0) wcsncpy_s(shared->language, 16, value, _TRUNCATE);
    else if (wcscmp(key, L"gpu") == 0) wcsncpy_s(shared->gpu, 128, value, _TRUNCATE);
    else if (wcscmp(key, L"mods") == 0) shared->mods = _wtoi(value);
    else if (wcscmp(key, L"window") == 0) shared->window = _wcstoui64(value, NULL, 16);
    else if (wcscmp(key, L"dialog") == 0) shared->dialog_off = wcscmp(value, L"off") == 0;
}

/* The player's own exit (Player.ProcessExit, AppDomain.ProcessExit), with its exit code. */
__declspec(dllexport) void moono_crash_exiting(DWORD code)
{
    crash_exiting(code);
}

/* ---- the test hook (--test-native-crash) ---------------------------------------------------- */

static volatile int *volatile nowhere;

#pragma warning(push)
#pragma warning(disable: 4717)  /* recursive on all control paths: the point */
static int __declspec(noinline) recurse(volatile int depth)
{
    volatile char frame[1024];
    frame[0] = (char)depth;
    return recurse(depth + 1) + frame[0];
}
#pragma warning(pop)

static void __declspec(noinline) overrun(int n)
{
    char buffer[16];
    for (volatile int i = 0; i < n; i++) buffer[i] = 'x';
    OutputDebugStringA(buffer);
}

static DWORD WINAPI faulting_thread(void *what)
{
    if (what) recurse(0);
    else *nowhere = 1;
    return 0;
}

/* Faults on purpose, in native code: `access-violation` and `recursion` (a stack overflow) on the
 * calling thread, `thread` on a new native thread, `stack-overflow` there too, `fastfail`, `heap`,
 * `gs`, `abort`, `terminate`.
 * Returns 0 for an unknown name. No error dialog either way. */
__declspec(dllexport) int moono_crash_test(const wchar_t *what)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (wcscmp(what, L"access-violation") == 0) *nowhere = 1;
    else if (wcscmp(what, L"thread") == 0 || wcscmp(what, L"stack-overflow") == 0)
    {
        HANDLE thread = CreateThread(NULL, 0, faulting_thread, (void *)(ULONG_PTR)(what[0] == L's'), 0, NULL);
        if (thread) WaitForSingleObject(thread, INFINITE);
    }
    else if (wcscmp(what, L"recursion") == 0) recurse(0);
    else if (wcscmp(what, L"fastfail") == 0) __fastfail(7 /* FAST_FAIL_FATAL_APP_EXIT */);
    else if (wcscmp(what, L"heap") == 0)
    {
        HANDLE heap = GetProcessHeap();
        void *block = HeapAlloc(heap, 0, 64);
        HeapFree(heap, 0, block);
        HeapFree(heap, 0, block);
        HeapFree(heap, 0, block);
    }
    else if (wcscmp(what, L"gs") == 0) overrun(64);
    else if (wcscmp(what, L"abort") == 0) abort();
    else if (wcscmp(what, L"terminate") == 0) TerminateProcess(GetCurrentProcess(), 1);
    else return 0;
    return 1;
}

/* ---- the watcher ---------------------------------------------------------------------------- */

typedef BOOL (WINAPI *mini_dump_write_dump_fn)(HANDLE, DWORD, HANDLE, MINIDUMP_TYPE, PMINIDUMP_EXCEPTION_INFORMATION,
                                               PMINIDUMP_USER_STREAM_INFORMATION, PMINIDUMP_CALLBACK_INFORMATION);
typedef LONG (NTAPI *process_fn)(HANDLE);

static HANDLE game;
static HANDLE stderr_copy;
static CRITICAL_SECTION dump_lock;
static int dumped;
static wchar_t dump_path[PATH_MOST];
static ULONG64 dump_size;
static char runtime_text[RUNTIME_TEXT_MOST];
static DWORD runtime_length;
static char managed_line[1024], managed_at[512];   /* an unhandled managed exception's first lines */

/* A heap failure (0xC0000374): the exception's first parameter points at ntdll's record of it
 * (the heap, the damaged block, the blocks before and after it, the stack that found it), which
 * the minidump otherwise leaves out with the rest of ntdll's data. The dump gets the record and
 * 1 KB around each address in it that points into the game's private read-write memory. */
static struct { ULONG64 at; ULONG size; } extra[16];
static int extra_count, extra_next;

static void heap_failure_memory(void)
{
    if (shared->record.ExceptionCode != STATUS_HEAP_CORRUPTION_CODE || shared->record.NumberParameters < 1) return;
    ULONG64 info = shared->record.ExceptionInformation[0], words[12];
    SIZE_T got = 0;
    if (!info || !ReadProcessMemory(game, (void *)info, words, sizeof words, &got) || got != sizeof words) return;
    extra[extra_count].at = info;
    extra[extra_count++].size = 0x200;
    for (int i = 0; i < 12 && extra_count < 16; i++)
    {
        MEMORY_BASIC_INFORMATION m;
        if (words[i] < 0x10000 || !VirtualQueryEx(game, (void *)words[i], &m, sizeof m)) continue;
        if (m.State != MEM_COMMIT || m.Type != MEM_PRIVATE || (m.Protect & PAGE_GUARD)
            || !(m.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) continue;
        ULONG64 base = (ULONG64)m.BaseAddress, end = base + m.RegionSize;
        ULONG64 from = words[i] - base > 512 ? words[i] - 512 : base, to = end - words[i] > 512 ? words[i] + 512 : end;
        extra[extra_count].at = from;
        extra[extra_count++].size = (ULONG)(to - from);
    }
}

/* Adds the memory above to the dump; the rest as without a callback. */
static BOOL CALLBACK dump_callback(void *context, PMINIDUMP_CALLBACK_INPUT in, PMINIDUMP_CALLBACK_OUTPUT out)
{
    (void)context;
    switch (in->CallbackType)
    {
    case IncludeModuleCallback: case IncludeThreadCallback: case ModuleCallback: case ThreadCallback: case ThreadExCallback:
        return TRUE;
    case MemoryCallback:
        if (extra_next >= extra_count) return FALSE;
        out->MemoryBase = extra[extra_next].at;
        out->MemorySize = extra[extra_next++].size;
        return TRUE;
    default:
        return FALSE;
    }
}

/* Our dump's name, crash-YYYYMMDD-hhmmss.dmp, and nothing else. */
static int is_dump_name(const wchar_t *name)
{
    static const wchar_t shape[] = L"crash-########-######.dmp";
    for (int i = 0; shape[i]; i++, name++)
        if (shape[i] == L'#' ? *name < L'0' || *name > L'9' : *name != shape[i]) return 0;
    return *name == 0;
}

/* Before the dump `name` is written (`folder` its folder, with the separator): our older dumps
 * there deleted but the newest (last written; the name breaks a tie), so two remain with the new
 * one. Before, not after: a full dump is GBs, and the space comes back first; a write that fails
 * still leaves the newest older dump. Plain files only (no directories, links or other reparse
 * points); one in use or read-only stays. */
static void prune_dumps(const wchar_t *folder, const wchar_t *name)
{
    static wchar_t path[PATH_MOST], newest[64];
    size_t length = wcslen(folder);
    if (length + 64 >= PATH_MOST) return;
    for (int pass = 0; pass < 2; pass++)
    {
        FILETIME newest_time = { 0, 0 };
        if (pass == 0) newest[0] = 0;
        swprintf_s(path, PATH_MOST, L"%lscrash-*.dmp", folder);
        WIN32_FIND_DATAW found;
        HANDLE find = FindFirstFileExW(path, FindExInfoBasic, &found, FindExSearchNameMatch, NULL, 0);
        if (find == INVALID_HANDLE_VALUE) return;
        do
        {
            if ((found.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))
                || !is_dump_name(found.cFileName) || !wcscmp(found.cFileName, name)) continue;
            if (pass == 0)
            {
                LONG later = CompareFileTime(&found.ftLastWriteTime, &newest_time);
                if (!newest[0] || later > 0 || (later == 0 && wcscmp(found.cFileName, newest) > 0))
                {
                    newest_time = found.ftLastWriteTime;
                    wcscpy_s(newest, 64, found.cFileName);
                }
            }
            else if (wcscmp(found.cFileName, newest))
            {
                swprintf_s(path, PATH_MOST, L"%ls%ls", folder, found.cFileName);
                DeleteFileW(path);
            }
        } while (FindNextFileW(find, &found));
        FindClose(find);
        if (!newest[0]) return;
    }
}

/* The minidump beside the log, the game suspended meanwhile, once. `with_fault`: the fault the
 * game noted, as the dump's exception. */
static void write_dump(int with_fault)
{
    EnterCriticalSection(&dump_lock);
    if (!dumped && shared->installed && shared->dumps)
    {
        dumped = 1;
        HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        process_fn suspend = (process_fn)GetProcAddress(ntdll, "NtSuspendProcess");
        process_fn resume = (process_fn)GetProcAddress(ntdll, "NtResumeProcess");
        wchar_t system[MAX_PATH], dll[MAX_PATH];
        HMODULE dbghelp = NULL;
        if (GetSystemDirectoryW(system, MAX_PATH))
        {
            swprintf_s(dll, MAX_PATH, L"%ls\\dbghelp.dll", system);
            dbghelp = LoadLibraryW(dll);
        }
        mini_dump_write_dump_fn write = dbghelp ? (mini_dump_write_dump_fn)GetProcAddress(dbghelp, "MiniDumpWriteDump") : NULL;
        wcscpy_s(dump_path, PATH_MOST, shared->log_path);
        wchar_t *slash = wcsrchr(dump_path, L'\\'), *other = wcsrchr(dump_path, L'/');
        if (other > slash) slash = other;
        SYSTEMTIME now;
        GetLocalTime(&now);
        if (slash && write)
        {
            static wchar_t folder[PATH_MOST];
            slash[1] = 0;
            wcscpy_s(folder, PATH_MOST, dump_path);
            swprintf_s(slash + 1, PATH_MOST - (slash + 1 - dump_path), L"crash-%04u%02u%02u-%02u%02u%02u.dmp",
                       now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
            prune_dumps(folder, slash + 1);
            HANDLE file = CreateFileW(dump_path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (file != INVALID_HANDLE_VALUE)
            {
                if (suspend) suspend(game);
                MINIDUMP_EXCEPTION_INFORMATION fault = { shared->seen_thread, (PEXCEPTION_POINTERS)shared->pointers_in_game, TRUE };
                /* The threads' registers and stacks, 1 KB around each value on them that points
                 * into readable memory (a heap block, an object: MiniDumpWithIndirectly-
                 * ReferencedMemory) and a heap failure's record with the blocks it names
                 * (heap_failure_memory). --crash-dumps full: the process's private read-write memory
                 * (every heap, the runtime's included; not the mapped images and files, which the
                 * debugger reads from disk) with the modules' data sections (ntdll's heap globals
                 * and its record of a heap failure, which !heap needs to decode the heaps), and the
                 * layout of all memory (!address). */
                MINIDUMP_TYPE type = MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules
                                   | MiniDumpWithIndirectlyReferencedMemory;
                if (shared->dumps == 2)
                    type |= MiniDumpWithPrivateReadWriteMemory | MiniDumpWithDataSegs | MiniDumpWithFullMemoryInfo
                            | MiniDumpWithHandleData | MiniDumpIgnoreInaccessibleMemory;
                extra_count = extra_next = 0;
                if (with_fault) heap_failure_memory();
                MINIDUMP_CALLBACK_INFORMATION callback = { dump_callback, NULL };
                BOOL ok = write(game, GetProcessId(game), file, type, with_fault ? &fault : NULL, NULL, &callback);
                if (resume) resume(game);
                LARGE_INTEGER size;
                if (ok && GetFileSizeEx(file, &size)) dump_size = (ULONG64)size.QuadPart;
                CloseHandle(file);
                if (!ok) { DeleteFileW(dump_path); dump_path[0] = 0; }
            }
            else dump_path[0] = 0;
        }
        else dump_path[0] = 0;
    }
    LeaveCriticalSection(&dump_lock);
}

static int starts_with(const char *line, const char *prefix)
{
    while (*prefix) if (*line++ != *prefix++) return 0;
    return 1;
}

/* The game's standard error: passed on as it comes; the runtime's fatal-error text kept (up to
 * 40 lines) and, as it starts, the minidump (before the runtime ends the process). */
static DWORD WINAPI pass_on(void *pipe)
{
    static char chunk[4096], line[1024];
    DWORD line_length = 0, kept_lines = 0;
    int keeping = 0;
    for (int open = 1; open;)
    {
        DWORD read = 0;
        if (!ReadFile((HANDLE)pipe, chunk, sizeof chunk, &read, NULL) || read == 0)
        {
            /* The end: a last line without its line break still counts. */
            if (!line_length) break;
            chunk[0] = '\n';
            read = 1;
            open = 0;
        }
        else if (stderr_copy)
        {
            DWORD written;
            WriteFile(stderr_copy, chunk, read, &written, NULL);
        }
        for (DWORD i = 0; i < read; i++)
        {
            char c = chunk[i];
            if (c != '\n' && line_length + 1 < sizeof line) { if (c != '\r') line[line_length++] = c; continue; }
            line[line_length] = 0;
            if (!keeping && (starts_with(line, "Fatal error.") || starts_with(line, "Process terminated.") || starts_with(line, "Stack overflow.")))
            {
                keeping = 1;
                write_dump(shared->seen != SEEN_NONE && GetTickCount64() - shared->seen_tick < 10000);
            }
            /* A managed exception nothing caught: the player logs it; this says it ended the process. */
            else if (!keeping && starts_with(line, "Unhandled exception."))
            {
                keeping = 1;
                strncpy_s(managed_line, sizeof managed_line, line, _TRUNCATE);
            }
            else if (keeping && managed_line[0] && !managed_at[0] && starts_with(line, "   at "))
                strncpy_s(managed_at, sizeof managed_at, line + 6, _TRUNCATE);
            if (keeping && kept_lines < 40 && runtime_length + line_length + 32 < RUNTIME_TEXT_MOST)
            {
                out_t o = { runtime_text, runtime_length, RUNTIME_TEXT_MOST };
                put(&o, "[engine] native crash: runtime: ");
                put(&o, line);
                put(&o, "\r\n");
                runtime_length = o.length;
                kept_lines++;
            }
            line_length = 0;
        }
    }
    return 0;
}

/* Where the game's window is, while it is there: the crash window opens on that monitor. */
static void game_monitor(RECT *monitor)
{
    HWND window = (HWND)(ULONG_PTR)shared->window;
    if (!window || !IsWindow(window)) return;
    MONITORINFO info = { sizeof info };
    if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &info)) *monitor = info.rcMonitor;
}

/* The crash window: -1 none (ONI_MOONO_CRASH_DIALOG=off, --crash-dialog off, a run with
 * --run-home, an end by Ctrl+C, by Windows or from outside with -1), 1 shown and activated, 0
 * shown without taking the focus (ONI_MOONO_CRASH_DIALOG=inactive, for test runs). The
 * environment wins over the option. */
static int dialog_wanted(DWORD code)
{
    if (code == 0xC000013A || code == 0x40010004 || code == 0xFFFFFFFF) return -1;
    wchar_t value[16];
    if (GetEnvironmentVariableW(L"ONI_MOONO_CRASH_DIALOG", value, 16))
    {
        if (wcscmp(value, L"off") == 0) return -1;
        if (wcscmp(value, L"inactive") == 0) return 0;
        if (wcscmp(value, L"on") == 0) return 1;
    }
    return shared->dialog_off ? -1 : 1;
}

/* The error line from the record: the fault and its module, the managed exception's type, method
 * and message, else the exit code with its name (an exit code alone: the window says so). */
static void error_line(DWORD code, int used_fault, wchar_t *out, size_t most)
{
    char text[1600];
    out_t o = { text, 0, sizeof text };
    char name[96] = "";
    const char *full = exception_name(code);
    if (full)
    {
        strncpy_s(name, sizeof name, full, _TRUNCATE);
        char *colon = strchr(name, ':');
        if (colon) *colon = 0;
    }
    if (used_fault && is_fault(shared->seen_code))
    {
        const char *fault = exception_name(shared->seen_code);
        char fault_name[96] = "";
        if (fault) { strncpy_s(fault_name, sizeof fault_name, fault, _TRUNCATE); char *c = strchr(fault_name, ':'); if (c) *c = 0; }
        put_hex(&o, shared->seen_code, 8);
        if (fault_name[0]) { put(&o, " ("); put(&o, fault_name); put(&o, ")"); }
        if (shared->fault_module[0]) { put(&o, " in "); put_wide(&o, shared->fault_module); }
    }
    else if (managed_line[0])
    {
        /* "Unhandled exception. System.X: message" and "   at Namespace.Type.Method(arguments)" */
        const char *what = managed_line + strlen("Unhandled exception.");
        while (*what == ' ') what++;
        const char *colon = strstr(what, ": ");
        char type[256];
        size_t type_n = colon ? (size_t)(colon - what) : strlen(what);
        strncpy_s(type, sizeof type, what, type_n < sizeof type - 1 ? type_n : sizeof type - 1);
        put(&o, type);
        if (managed_at[0])
        {
            char method[512];
            strncpy_s(method, sizeof method, managed_at, _TRUNCATE);
            char *paren = strchr(method, '(');
            if (paren) *paren = 0;
            put(&o, " in ");
            put(&o, method);
        }
        if (colon) { put(&o, ": "); put(&o, colon + 2); }
    }
    else if (code < 0x10000)
    {
        out[0] = 0;
        return;
    }
    else
    {
        put_hex(&o, code, 8);
        if (name[0]) { put(&o, " ("); put(&o, name); put(&o, ")"); }
    }
    if (!MultiByteToWideChar(CP_UTF8, 0, text, -1, out, (int)most)) out[0] = 0;
}

static void show_dialog(DWORD code, int used_fault, const RECT *monitor, int activate)
{
    static crash_report_t r;
    r.language = _wcsnicmp(shared->language, L"zh", 2) == 0 ? CRASH_CHINESE : CRASH_ENGLISH;
    error_line(code, used_fault, r.error, _countof(r.error));
    r.exit_code = code;
    wcscpy_s(r.version, _countof(r.version), shared->version);
    wcscpy_s(r.game_build, _countof(r.game_build), shared->game_build);
    wcscpy_s(r.gpu, _countof(r.gpu), shared->gpu);
    r.mods = shared->mods;
    FILETIME created, ended, kernel, user;
    r.play_seconds = ~0ull;
    if (GetProcessTimes(game, &created, &ended, &kernel, &user))
    {
        ULONG64 from = ((ULONG64)created.dwHighDateTime << 32) | created.dwLowDateTime, to = ((ULONG64)ended.dwHighDateTime << 32) | ended.dwLowDateTime;
        if (to > from) r.play_seconds = (to - from) / 10000000;
    }
    wcscpy_s(r.log_path, _countof(r.log_path), shared->log_path);
    wcscpy_s(r.dump_path, _countof(r.dump_path), dump_path);
    r.dump_size = dump_path[0] ? dump_size : 0;
    GetLocalTime(&r.crashed);
    r.monitor = *monitor;
    r.activate = activate;
    crash_dialog_show(&r);
}

static ULONG64 parse_handle(const wchar_t *text)
{
    return _wcstoui64(text, NULL, 16);
}

int crash_watch(int argc, wchar_t **argv)
{
    if (argc < 8) return 2;
    HANDLE map = (HANDLE)(ULONG_PTR)parse_handle(argv[2]);
    game = (HANDLE)(ULONG_PTR)parse_handle(argv[3]);
    HANDLE wanted = (HANDLE)(ULONG_PTR)parse_handle(argv[4]), written = (HANDLE)(ULONG_PTR)parse_handle(argv[5]);
    HANDLE pipe = (HANDLE)(ULONG_PTR)parse_handle(argv[6]);
    stderr_copy = (HANDLE)(ULONG_PTR)parse_handle(argv[7]);
    shared = (shared_t *)MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(shared_t));
    if (!shared) return 1;
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    InitializeCriticalSection(&dump_lock);
    HANDLE reader = pipe ? CreateThread(NULL, 64 * 1024, pass_on, pipe, 0, NULL) : NULL;

    HANDLE waits[2] = { game, wanted };
    RECT monitor = { 0, 0, 0, 0 };
    for (;;)
    {
        DWORD woke = WaitForMultipleObjects(2, waits, FALSE, 1000);
        if (woke == WAIT_OBJECT_0 + 1)
        {
            write_dump(1);
            SetEvent(written);
        }
        else if (woke == WAIT_TIMEOUT) game_monitor(&monitor);
        else break;
    }
    DWORD code = 0;
    GetExitCodeProcess(game, &code);
    if (!shared->installed) return 0;
    if (shared->clean && shared->clean_code == code) return 0;
    /* The rest of the game's standard error (its children may still hold the pipe). */
    if (reader) WaitForSingleObject(reader, 2000);

    static char text[TEXT_MOST + RUNTIME_TEXT_MOST + 2048];
    out_t o = { text, 0, sizeof text };
    LONG seen = shared->seen;
    ULONGLONG ago = GetTickCount64() - shared->seen_tick;
    if (seen == SEEN_UNHANDLED || (seen == SEEN_FIRST_CHANCE && shared->seen_code == code))
    {
        put(&o, shared->text);
        if (seen == SEEN_FIRST_CHANCE && ago > 10000)
        {
            put(&o, "[engine] native crash: (seen ");
            put_dec(&o, ago / 1000);
            put(&o, " s before the end; native code may have handled it)\r\n");
        }
    }
    if (runtime_length) put(&o, runtime_text);
    put(&o, "[engine] native crash: the process ended with ");
    if (code < 0x10000) put_dec(&o, code); else put_hex(&o, code, 8);
    const char *name = exception_name(code);
    if (name) { put(&o, " ("); put(&o, name); put(&o, ")"); }
    if (shared->clean)
    {
        put(&o, " while exiting with ");
        put_dec(&o, shared->clean_code);
    }
    else if (seen == SEEN_NONE && !runtime_length)
        put(&o, code == STATUS_STACK_BUFFER_OVERRUN_CODE ? "; a fast fail skips every handler, so nothing more is known"
                : code < 0x10000 ? " without the player's own exit (ExitProcess or TerminateProcess from native code, or from outside)"
                : is_fault(code) ? "; no handler saw it" : "");
    put(&o, "\r\n");
    if (dump_path[0])
    {
        put(&o, "[engine] native crash dump: ");
        put_path(&o, shared, dump_path);
        put(&o, " (");
        put_dec(&o, (dump_size + 1023) / 1024);
        put(&o, " KB)\r\n");
    }
    HANDLE log = CreateFileW(shared->log_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (log != INVALID_HANDLE_VALUE)
    {
        DWORD done;
        WriteFile(log, text, o.length, &done, NULL);
        FlushFileBuffers(log);
        CloseHandle(log);
    }
    int activate = dialog_wanted(code);
    if (activate >= 0) show_dialog(code, seen == SEEN_UNHANDLED || (seen == SEEN_FIRST_CHANCE && shared->seen_code == code), &monitor, activate);
    return 0;
}
