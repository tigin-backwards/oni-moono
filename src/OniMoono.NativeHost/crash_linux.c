/* Native crash records of the Linux player (ARCHITECTURE §7, "Native crashes").
 *
 * A crash in native code (the GPU driver, Klei's SimDLL, a mod's library, the runtime) has no
 * managed exception, so the player's log just stops. This leaves a record of it in Player.log:
 * the signal and the module and offset it happened at, the thread, a native stack, how the
 * process ended, and a dump beside the log; then the crash window (crash_window_linux.c).
 *
 * The shape is the reverse of Windows' (crash.c): this process, started by Steam, forks the game
 * before any runtime loads and stays its parent, the watcher. Only a parent learns how a process
 * ended (the signal, an end by SIGKILL), and only an ancestor may read it under the kernel's
 * default ptrace rule (Yama scope 1).
 *
 * What sees what (measured with .NET 10; ARCHITECTURE has the table):
 *  - Our handlers for SIGSEGV, SIGBUS, SIGILL, SIGFPE and SIGABRT go in before the runtime's. The
 *    runtime's own (CoreCLR's PAL) keep them as their "previous action" and call them only for a
 *    fault they do not turn into a managed exception, so a managed null reference never reaches
 *    them; then the runtime writes its dump (createdump, set up here through the DOTNET_ dump
 *    variables, which it reads only at its start) and returns. Ours note the fault and set the
 *    signal back to its default action, so the fault, run again, ends the process with it.
 *  - An unhandled managed exception, Environment.FailFast, a stack overflow on a managed thread
 *    and glibc's checks (a double free, a stack-protector check) print their text to standard
 *    error and abort(): SIGABRT, with that text, which passes through the watcher.
 *  - SIGKILL (the kernel's out-of-memory killer, kill -9) runs nothing in the game: the watcher
 *    sees the signal, and the cgroup's memory.events tells an out-of-memory kill from the rest.
 *
 * In the crashing process nothing allocates: a handler copies the fault (siginfo, registers, the
 * top 64 KB of the stack) into a page shared with the watcher and wakes a thread made at start,
 * which formats it there, as on Windows. The watcher writes the record once the game has gone,
 * after the log's own writer, and adds the stack from the runtime's crash report. */

#define _GNU_SOURCE
#include <dirent.h>
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/eventfd.h>
#include <sys/mman.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/uio.h>
#include <sys/wait.h>
#include <time.h>
#include <ucontext.h>
#include <unistd.h>
#include "crash_common.h"
#include "crash_linux.h"
#include "crash_window.h"

#define EXPORT __attribute__((visibility("default")))
#define TEXT_MOST 12288
#define PATH_MOST 1024
#define PREFIX_MOST 4
#define STACK_COPY (64 * 1024)
#define FRAMES_MOST 24

enum { SEEN_NONE, SEEN_SIGNAL };

/* The record the game and the watcher share (MAP_SHARED, made before the fork). */
typedef struct
{
    volatile int installed;          /* the player gave the log's path */
    volatile int clean;              /* the player exits on purpose, with clean_code */
    int clean_code;
    volatile int seen;               /* SEEN_*: what text holds */
    int seen_signal, seen_code, seen_tid;
    uint64_t seen_addr, seen_ip, seen_sp, seen_err, seen_ms;
    int dumps;                       /* 0: the crash report only, 1: a Mini dump, 2: a Heap dump */
    int user_dumps;                  /* the user's own DOTNET_DbgEnableMiniDump: their settings */
    int64_t started;                 /* the game's start, for the session's length (seconds) */
    uint32_t text_length;
    char text[TEXT_MOST];            /* the fault's lines, UTF-8 */
    char log_path[PATH_MOST];
    char dump_dir[PATH_MOST];        /* where the runtime writes crash-PID.dmp */
    char prefix[PREFIX_MOST][PATH_MOST / 2];   /* personal paths, as the log hides them */
    char replacement[PREFIX_MOST][32];
    char fault_module[64];           /* the noted fault's module, for the crash window */
    /* For the crash window (moono_crash_describe), as the game gives them. */
    char version[64], game_build[32], language[16], gpu[128];
    volatile int mods;               /* enabled mods; -1 unknown */
    volatile int dialog_off;         /* --crash-dialog off (or a run with --run-home) */
    uint32_t stack_length;
    unsigned char stack[STACK_COPY]; /* the faulting thread's stack from its stack pointer up */
} shared_t;

static shared_t *shared;

static uint64_t monotonic_ms(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (uint64_t)t.tv_sec * 1000 + (uint64_t)t.tv_nsec / 1000000;
}

static const char *signal_name(int sig)
{
    switch (sig)
    {
    case SIGSEGV: return "SIGSEGV";
    case SIGBUS: return "SIGBUS";
    case SIGILL: return "SIGILL";
    case SIGFPE: return "SIGFPE";
    case SIGABRT: return "SIGABRT";
    case SIGTRAP: return "SIGTRAP";
    case SIGSYS: return "SIGSYS";
    case SIGKILL: return "SIGKILL";
    case SIGTERM: return "SIGTERM";
    case SIGINT: return "SIGINT";
    case SIGHUP: return "SIGHUP";
    case SIGQUIT: return "SIGQUIT";
    case SIGPIPE: return "SIGPIPE";
    default: return NULL;
    }
}

static const char *signal_meaning(int sig)
{
    switch (sig)
    {
    case SIGSEGV: return "invalid memory access";
    case SIGBUS: return "bus error";
    case SIGILL: return "illegal instruction";
    case SIGFPE: return "arithmetic error";
    case SIGABRT: return "abort";
    case SIGTRAP: return "breakpoint";
    case SIGSYS: return "bad system call";
    case SIGKILL: return "killed";
    case SIGTERM: return "terminated";
    case SIGINT: return "interrupted (Ctrl+C)";
    case SIGHUP: return "hang-up";
    case SIGQUIT: return "quit";
    case SIGPIPE: return "broken pipe";
    default: return NULL;
    }
}

static const char *code_meaning(int sig, int code)
{
    if (sig == SIGSEGV && code == SEGV_MAPERR) return "address not mapped";
    if (sig == SIGSEGV && code == SEGV_ACCERR) return "no access";
    if (sig == SIGBUS && code == BUS_ADRALN) return "misaligned address";
    if (sig == SIGBUS && code == BUS_ADRERR) return "no such physical address (a truncated file?)";
    if (sig == SIGFPE && code == FPE_INTDIV) return "integer division by zero";
    if (sig == SIGFPE && code == FPE_INTOVF) return "integer overflow";
    if (sig == SIGILL && code == ILL_ILLOPN) return "illegal operand";
    if (sig == SIGILL && code == ILL_ILLOPC) return "illegal opcode";
    return NULL;
}

/* Faults that come with an address and an instruction (from a handler of ours). */
static int is_fault(int sig)
{
    return sig == SIGSEGV || sig == SIGBUS || sig == SIGILL || sig == SIGFPE;
}

static void put_signal(crash_out *o, int sig)
{
    const char *name = signal_name(sig);
    if (name) crash_put(o, name);
    else { crash_put(o, "signal "); crash_put_dec(o, (uint64_t)sig); }
}

/* UTF-16 (the managed side's strings) to UTF-8. */
static void from_utf16(const uint16_t *s, char *out, size_t most)
{
    size_t n = 0;
    while (s && *s && n + 4 < most)
    {
        uint32_t c = *s++;
        if (c >= 0xD800 && c < 0xDC00 && *s >= 0xDC00 && *s < 0xE000) c = 0x10000 + ((c - 0xD800) << 10) + (*s++ - 0xDC00);
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | c >> 6); out[n++] = (char)(0x80 | (c & 63)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | c >> 12); out[n++] = (char)(0x80 | (c >> 6 & 63)); out[n++] = (char)(0x80 | (c & 63)); }
        else { out[n++] = (char)(0xF0 | c >> 18); out[n++] = (char)(0x80 | (c >> 12 & 63)); out[n++] = (char)(0x80 | (c >> 6 & 63)); out[n++] = (char)(0x80 | (c & 63)); }
    }
    if (most) out[n] = 0;
}

/* A path with the personal folders replaced, as the log's own lines have them. */
static void put_path(crash_out *o, const char *path)
{
    for (int i = 0; i < PREFIX_MOST; i++)
    {
        const char *prefix = shared->prefix[i];
        size_t n = strlen(prefix);
        if (n && strncmp(path, prefix, n) == 0 && (path[n] == 0 || path[n] == '/'))
        {
            crash_put(o, shared->replacement[i]);
            crash_put(o, path + n);
            return;
        }
    }
    crash_put(o, path);
}

/* ---- the game's side ---------------------------------------------------------------------- */

static int format_wanted = -1, format_done = -1;   /* eventfds: handler to formatter and back */
static pid_t formatter_tid;
static volatile int noting;
static int main_tid;
static int watched;                                /* a watcher runs (we are its child) */

/* `n` bytes of this process's memory at `at`, read without faulting: what could be read. */
static size_t safe_read(uint64_t at, void *to, size_t n)
{
    struct iovec local = { to, n }, remote = { (void *)(uintptr_t)at, n };
    ssize_t got = process_vm_readv(getpid(), &local, 1, &remote, 1, 0);
    return got > 0 ? (size_t)got : 0;
}

/* /proc/self/maps, read once per fault into static memory (a large game has thousands of
 * mappings: the heaps, the JIT's code, every library), sorted by address as the kernel lists it. */
typedef struct
{
    uint64_t start, end, offset;
    char perms[5];
    const char *path;            /* in maps_text, NUL-terminated; "" for none */
} mapping_t;

#define MAPS_TEXT_MOST (16u << 20)
#define MAPPINGS_MOST 131072
static char maps_text[MAPS_TEXT_MOST];
static mapping_t mappings[MAPPINGS_MOST];
static int mapping_count;

static void read_maps(void)
{
    mapping_count = 0;
    int fd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return;
    size_t n = 0;
    for (ssize_t got; n + 1 < MAPS_TEXT_MOST && (got = read(fd, maps_text + n, MAPS_TEXT_MOST - 1 - n)) > 0;) n += (size_t)got;
    close(fd);
    maps_text[n] = 0;
    for (char *line = maps_text; *line && mapping_count < MAPPINGS_MOST;)
    {
        char *eol = strchr(line, '\n');
        if (eol) *eol = 0;
        mapping_t *m = &mappings[mapping_count];
        char *at;
        m->start = strtoull(line, &at, 16);
        if (*at == '-')
        {
            m->end = strtoull(at + 1, &at, 16);
            while (*at == ' ') at++;
            memcpy(m->perms, at, 4);
            m->perms[4] = 0;
            at += 4;
            m->offset = strtoull(at, &at, 16);
            for (int f = 0; f < 2; f++) { while (*at == ' ') at++; while (*at && *at != ' ') at++; }   /* dev, inode */
            while (*at == ' ') at++;
            m->path = at;
            mapping_count++;
        }
        if (!eol) break;
        line = eol + 1;
    }
}

/* The mapping holding `address` and its module's base (the start of its file's first mapping,
 * which a library's addresses are counted from); 0 when none. */
static int module_of(uint64_t address, mapping_t *found, uint64_t *base)
{
    int low = 0, high = mapping_count - 1;
    while (low <= high)
    {
        int mid = (low + high) / 2;
        mapping_t *m = &mappings[mid];
        if (address < m->start) high = mid - 1;
        else if (address >= m->end) low = mid + 1;
        else
        {
            *found = *m;
            *base = m->start - m->offset;
            if (m->path[0] == '/')
                for (int i = mid; i >= 0 && mid - i < 64; i--)
                    if (mappings[i].offset == 0 && strcmp(mappings[i].path, m->path) == 0) { *base = mappings[i].start; break; }
            return 1;
        }
    }
    return 0;
}

static const char *file_part(const char *path)
{
    const char *slash = strrchr(path, '/');
    return slash ? slash + 1 : path;
}

/* The module's GNU build id from its program headers in memory, as hex; empty when none. */
static void build_id(uint64_t base, char *out, size_t most)
{
    out[0] = 0;
    Elf64_Ehdr eh;
    if (safe_read(base, &eh, sizeof eh) != sizeof eh || memcmp(eh.e_ident, ELFMAG, SELFMAG) != 0 || eh.e_phnum > 64) return;
    for (int i = 0; i < eh.e_phnum; i++)
    {
        Elf64_Phdr ph;
        if (safe_read(base + eh.e_phoff + (uint64_t)i * sizeof ph, &ph, sizeof ph) != sizeof ph || ph.p_type != PT_NOTE) continue;
        unsigned char notes[512];
        size_t n = safe_read(base + ph.p_vaddr, notes, ph.p_memsz < sizeof notes ? ph.p_memsz : sizeof notes);
        for (size_t at = 0; at + 12 <= n;)
        {
            uint32_t namesz, descsz, type;
            memcpy(&namesz, notes + at, 4); memcpy(&descsz, notes + at + 4, 4); memcpy(&type, notes + at + 8, 4);
            size_t name_at = at + 12, desc_at = name_at + ((namesz + 3) & ~3u);
            if (desc_at + descsz > n) break;
            if (type == NT_GNU_BUILD_ID && namesz == 4 && memcmp(notes + name_at, "GNU", 4) == 0)
            {
                size_t k = 0;
                for (uint32_t j = 0; j < descsz && k + 3 < most; j++) { out[k++] = "0123456789abcdef"[notes[desc_at + j] >> 4]; out[k++] = "0123456789abcdef"[notes[desc_at + j] & 15]; }
                out[k] = 0;
                return;
            }
            at = desc_at + ((descsz + 3) & ~3u);
        }
    }
}

static void put_frame(crash_out *o, uint64_t ip)
{
    mapping_t m;
    uint64_t base;
    if (module_of(ip, &m, &base) && m.path[0] == '/')
    {
        crash_put(o, file_part(m.path));
        crash_put(o, "+");
        crash_put_hex(o, ip - base, 1);
    }
    else crash_put_hex(o, ip, 1);
}

/* Whether `ip` is in executable memory just after a call instruction: a return address. */
static int after_call(uint64_t ip, int *in_module)
{
    mapping_t m;
    uint64_t base;
    *in_module = 0;
    if (!module_of(ip, &m, &base) || m.perms[2] != 'x') return 0;
    *in_module = m.path[0] == '/';
    unsigned char code[8];
    if (ip < 8 || safe_read(ip - 8, code, 8) != 8) return 0;
    if (code[3] == 0xE8) return 1;                                        /* call rel32 */
    if (code[6] == 0xFF && ((code[7] >> 3) & 7) == 2) return 1;           /* call reg */
    if (code[5] == 0xFF && ((code[6] >> 3) & 7) == 2) return 1;           /* call [reg+d8] */
    if (code[2] == 0xFF && ((code[3] >> 3) & 7) == 2) return 1;           /* call [reg+d32], [rip+d32] */
    if (code[1] == 0xFF && ((code[2] >> 3) & 7) == 2) return 1;           /* call [reg+d32] with SIB */
    if (code[4] == 0xFF && ((code[5] >> 3) & 7) == 2) return 1;           /* call [reg+d8] with SIB */
    return 0;
}

/* The fault in the shared record as two lines: what, where, which thread; the stack, scanned
 * for return addresses (the watcher puts the runtime's crash report's in its place). */
static void format_fault(void)
{
    crash_out o = { shared->text, 0, TEXT_MOST };
    int sig = shared->seen_signal;
    read_maps();
    crash_put(&o, "[engine] native crash: ");
    put_signal(&o, sig);
    const char *meaning = signal_meaning(sig), *detail = code_meaning(sig, shared->seen_code);
    if (meaning) { crash_put(&o, " "); crash_put(&o, meaning); }
    if (is_fault(sig))
    {
        crash_put(&o, " (");
        if (detail) { crash_put(&o, detail); crash_put(&o, ", "); }
        if (sig == SIGSEGV || sig == SIGBUS)
            crash_put(&o, shared->seen_err & 0x10 ? "executing " : shared->seen_err & 2 ? "writing " : "reading ");
        else crash_put(&o, "at ");
        crash_put_hex(&o, shared->seen_addr, 1);
        crash_put(&o, ")");
    }
    crash_put(&o, " in ");
    mapping_t m;
    uint64_t base, ip = shared->seen_ip;
    shared->fault_module[0] = 0;
    if (module_of(ip, &m, &base) && m.path[0] == '/')
    {
        snprintf(shared->fault_module, sizeof shared->fault_module, "%s", file_part(m.path));
        crash_put(&o, file_part(m.path));
        crash_put(&o, "+");
        crash_put_hex(&o, ip - base, 1);
        char id[48];
        build_id(base, id, sizeof id);
        crash_put(&o, " (");
        if (id[0]) { crash_put(&o, "build-id "); crash_put(&o, id); crash_put(&o, ", "); }
        put_path(&o, m.path);
        crash_put(&o, ")");
    }
    else
    {
        crash_put_hex(&o, ip, 1);
        crash_put(&o, " (no module: generated code)");
    }
    int tid = shared->seen_tid;
    crash_put(&o, " on thread ");
    crash_put_dec(&o, (uint64_t)tid);
    if (tid == main_tid) crash_put(&o, " (main)");
    else
    {
        /* The thread's name (.NET names its threads), as the kernel keeps it (15 bytes). */
        char path[64], name[32];
        snprintf(path, sizeof path, "/proc/self/task/%d/comm", tid);
        int fd = open(path, O_RDONLY | O_CLOEXEC);
        ssize_t got = fd >= 0 ? read(fd, name, sizeof name - 1) : -1;
        if (fd >= 0) close(fd);
        if (got > 0)
        {
            name[got] = 0;
            name[strcspn(name, "\n")] = 0;
            if (name[0] && strcmp(name, "OniMoono") != 0) { crash_put(&o, " \""); crash_put(&o, name); crash_put(&o, "\""); }
        }
    }
    crash_put(&o, "\n");

    /* The stack: the faulting frame, then what looks like return addresses on the copied stack.
     * Runs of one frame (recursion) and of frames outside any module (generated code) as one. */
    static uint64_t frames[FRAMES_MOST * 4];
    static int in_module[FRAMES_MOST * 4];
    int count = 0;
    mapping_t fm;
    uint64_t fb;
    in_module[count] = module_of(ip, &fm, &fb) && fm.path[0] == '/';
    frames[count++] = ip;
    for (uint32_t at = 0; at + 8 <= shared->stack_length && count < FRAMES_MOST * 4; at += 8)
    {
        uint64_t word;
        memcpy(&word, shared->stack + at, 8);
        int module;
        if (after_call(word, &module)) { in_module[count] = module; frames[count++] = word; }
    }
    crash_put(&o, "[engine] native crash stack (scanned): ");
    int groups = 0;
    for (int i = 0; i < count && groups < FRAMES_MOST; groups++)
    {
        int run = 1;
        while (i + run < count && (in_module[i] ? frames[i + run] == frames[i] : !in_module[i + run])) run++;
        if (groups) crash_put(&o, ", ");
        if (in_module[i]) put_frame(&o, frames[i]);
        else { crash_put_dec(&o, (uint64_t)run); crash_put(&o, run == 1 ? " frame of generated code" : " frames of generated code"); }
        if (in_module[i] && run > 1) { crash_put(&o, " (x"); crash_put_dec(&o, (uint64_t)run); crash_put(&o, ")"); }
        i += run;
    }
    crash_put(&o, "\n");
    shared->text_length = o.length;
}

static void *formatter(void *unused)
{
    (void)unused;
    formatter_tid = (pid_t)syscall(SYS_gettid);
    pthread_setname_np(pthread_self(), "Crash record");
    uint64_t v;
    while (read(format_wanted, &v, sizeof v) == (ssize_t)sizeof v)
    {
        format_fault();
        v = 1;
        if (write(format_done, &v, sizeof v) < 0) { /* the handler gives up after its wait */ }
    }
    return NULL;
}

static const int handled[] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT };

/* A fault the runtime left alone (so not in managed code), or abort(): noted in the shared
 * record, formatted by the formatter thread; then the signal's default action, so the fault run
 * again (or abort's own raise) ends the process with it, after the runtime's dump. */
static void handler(int sig, siginfo_t *info, void *context)
{
    int saved = errno;
    ucontext_t *uc = (ucontext_t *)context;
    pid_t tid = (pid_t)syscall(SYS_gettid);
    if (shared && shared->installed && tid != formatter_tid && __sync_bool_compare_and_swap(&noting, 0, 1))
    {
        shared->seen_signal = sig;
        shared->seen_code = info->si_code;
        shared->seen_addr = (uint64_t)(uintptr_t)info->si_addr;
        shared->seen_tid = tid;
        shared->seen_ip = (uint64_t)uc->uc_mcontext.gregs[REG_RIP];
        shared->seen_sp = (uint64_t)uc->uc_mcontext.gregs[REG_RSP];
        shared->seen_err = (uint64_t)uc->uc_mcontext.gregs[REG_ERR];
        shared->seen_ms = monotonic_ms();
        /* The stack from the stack pointer up, a page at a time: what of it is mapped. */
        uint32_t copied = 0;
        while (copied < STACK_COPY)
        {
            size_t got = safe_read(shared->seen_sp + copied, shared->stack + copied, 4096 - ((shared->seen_sp + copied) & 4095));
            if (got == 0) break;
            copied += (uint32_t)got;
        }
        shared->stack_length = copied > STACK_COPY ? STACK_COPY : copied;
        shared->text_length = 0;
        shared->text[0] = 0;
        if (format_wanted >= 0)
        {
            uint64_t v = 1;
            if (write(format_wanted, &v, sizeof v) == (ssize_t)sizeof v)
            {
                struct pollfd done = { format_done, POLLIN, 0 };
                if (poll(&done, 1, 5000) == 1 && read(format_done, &v, sizeof v) < 0) { /* timed out or failed: the note stands */ }
            }
        }
        __sync_synchronize();
        shared->seen = SEEN_SIGNAL;
    }
    struct sigaction dfl;
    memset(&dfl, 0, sizeof dfl);
    dfl.sa_handler = SIG_DFL;
    sigaction(sig, &dfl, NULL);
    errno = saved;
}

static int env_is(const char *name, const char *value)
{
    const char *v = getenv(name);
    return v && strcmp(v, value) == 0;
}

static int make_dirs(const char *path)
{
    char p[PATH_MOST];
    snprintf(p, sizeof p, "%s", path);
    for (char *s = p + 1; *s; s++)
        if (*s == '/') { *s = 0; mkdir(p, 0755); *s = '/'; }
    return mkdir(p, 0755) == 0 || errno == EEXIST;
}

/* The game's folder of per-user files, as Program.ConfigureLinux makes it (host_linux.c's
 * persistent_dir). */
static int persistent_dir(int argc, char **argv, char *out, size_t most)
{
    const char *run_home = NULL, *config = getenv("XDG_CONFIG_HOME"), *home = getenv("HOME");
    for (int i = 1; i + 1 < argc; i++) if (strcmp(argv[i], "--run-home") == 0) run_home = argv[i + 1];
    char full[PATH_MAX];
    int n;
    if (run_home)
    {
        if (!realpath(run_home, full)) snprintf(full, sizeof full, "%s", run_home);
        n = snprintf(out, most, "%s/config/unity3d/Klei/Oxygen Not Included", full);
    }
    else if (config && config[0]) n = snprintf(out, most, "%s/unity3d/Klei/Oxygen Not Included", config);
    else if (home && home[0]) n = snprintf(out, most, "%s/.config/unity3d/Klei/Oxygen Not Included", home);
    else return 0;
    return n > 0 && (size_t)n < most;
}

/* The cgroup's memory.events, for the out-of-memory killer's count. */
static char oom_events[PATH_MOST];
static long oom_before = -1;

static long oom_kills(void)
{
    if (!oom_events[0]) return -1;
    int fd = open(oom_events, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    char text[1024];
    ssize_t got = read(fd, text, sizeof text - 1);
    close(fd);
    if (got <= 0) return -1;
    text[got] = 0;
    const char *at = strstr(text, "oom_kill ");
    return at ? strtol(at + 9, NULL, 10) : -1;
}

static void find_oom_events(void)
{
    int fd = open("/proc/self/cgroup", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return;
    char text[2048];
    ssize_t got = read(fd, text, sizeof text - 1);
    close(fd);
    if (got <= 0) return;
    text[got] = 0;
    const char *line = strstr(text, "0::");   /* the unified hierarchy (cgroup v2) */
    if (!line) return;
    line += 3;
    size_t n = strcspn(line, "\n");
    snprintf(oom_events, sizeof oom_events, "/sys/fs/cgroup%.*s/memory.events", (int)n, line);
    oom_before = oom_kills();
    if (oom_before < 0) oom_events[0] = 0;
}

static void watch(pid_t game, int pipe_read, int err_out, const char *exe, int argc, char **argv);

void crash_start(const char *exe, int argc, char **argv)
{
    /* Standard error as this process was started with it: a player that App.Restart started
     * (through the Restarter, a child of the game) inherits the first game's pipe to its
     * watcher, which has gone; ONI_MOONO_STDERR names the descriptor the first one kept of
     * where standard error went before (or none). */
    const char *marker = getenv("ONI_MOONO_STDERR");
    struct stat err_stat;
    if (marker && fstat(2, &err_stat) == 0 && S_ISFIFO(err_stat.st_mode))
    {
        unsigned long long dev = 0, ino = 0;
        int fd = -1;
        struct stat kept;
        if (strcmp(marker, "none") == 0)
        {
            int null = open("/dev/null", O_WRONLY | O_CLOEXEC);
            if (null >= 0) { dup2(null, 2); close(null); }
        }
        else if (sscanf(marker, "%llu:%llu:%d", &dev, &ino, &fd) == 3 && fd > 2 && fstat(fd, &kept) == 0
                 && (unsigned long long)kept.st_dev == dev && (unsigned long long)kept.st_ino == ino)
            dup2(fd, 2);
    }
    unsetenv("ONI_MOONO_STDERR");
    if (env_is("ONI_MOONO_CRASH_WATCH", "off")) return;

    shared = (shared_t *)mmap(NULL, sizeof(shared_t), PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    if (shared == MAP_FAILED) { shared = NULL; return; }
    shared->mods = -1;
    shared->started = (int64_t)time(NULL);

    /* The dump: the runtime's own createdump, which reads these when it starts. --crash-dumps
     * (ONI_CRASH_DUMPS in its place) as NativeCrashes.Install reads it: on, a Mini dump (the
     * threads' stacks, the modules); full, a Heap dump (all memory but the mapped images); off,
     * the crash report alone. The user's own DOTNET_DbgEnableMiniDump wins (with its settings).
     * Ours are marked, so a player App.Restart starts sets them again. */
    const char *dumps = "on";
    for (int i = 1; i + 1 < argc; i++) if (strcmp(argv[i], "--crash-dumps") == 0) dumps = argv[i + 1];
    const char *forced = getenv("ONI_CRASH_DUMPS");
    if (forced && (strcmp(forced, "on") == 0 || strcmp(forced, "off") == 0 || strcmp(forced, "full") == 0)) dumps = forced;
    shared->dumps = strcmp(dumps, "full") == 0 ? 2 : strcmp(dumps, "off") == 0 ? 0 : 1;
    shared->user_dumps = getenv("DOTNET_DbgEnableMiniDump") != NULL && !env_is("ONI_MOONO_DUMP_ENV", "1");
    if (!persistent_dir(argc, argv, shared->dump_dir, sizeof shared->dump_dir)) shared->dump_dir[0] = 0;
    find_oom_events();

    /* Standard error through the watcher (to where it went, if anywhere), for the runtime's and
     * glibc's text. Not a terminal's: a player started from one keeps writing there. */
    int fds[2] = { -1, -1 };
    int err_valid = fcntl(2, F_GETFD) >= 0;
    if ((!err_valid || !isatty(2)) && pipe2(fds, O_CLOEXEC) != 0) fds[0] = fds[1] = -1;
    int err_copy = -1;
    if (fds[0] >= 0 && err_valid) err_copy = fcntl(2, F_DUPFD_CLOEXEC, 3);

    fflush(NULL);
    pid_t game = fork();
    if (game < 0)
    {
        /* No watcher: the game runs as before, unrecorded. */
        if (fds[0] >= 0) { close(fds[0]); close(fds[1]); }
        if (err_copy >= 0) close(err_copy);
        munmap(shared, sizeof(shared_t));
        shared = NULL;
        return;
    }
    if (game > 0)
    {
        if (fds[1] >= 0) close(fds[1]);
        watch(game, fds[0], err_copy, exe, argc, argv);
        _exit(1);   /* not reached */
    }

    /* The game. */
    watched = 1;
    main_tid = (int)getpid();
    if (fds[1] >= 0)
    {
        /* A copy of where standard error went, which this process's children inherit, for a
         * player App.Restart starts (above). */
        char value[96];
        int kept = err_copy >= 0 ? fcntl(err_copy, F_DUPFD, 3) : -1;
        struct stat s;
        if (kept >= 0 && fstat(kept, &s) == 0) snprintf(value, sizeof value, "%llu:%llu:%d", (unsigned long long)s.st_dev, (unsigned long long)s.st_ino, kept);
        else snprintf(value, sizeof value, "none");
        setenv("ONI_MOONO_STDERR", value, 1);
        dup2(fds[1], 2);
        close(fds[1]);
        close(fds[0]);
    }
    if (err_copy >= 0) close(err_copy);
    /* The runtime keeps its handlers off SIGINT and SIGQUIT when they are ignored at its start. */
    struct sigaction dfl;
    memset(&dfl, 0, sizeof dfl);
    dfl.sa_handler = SIG_DFL;
    sigaction(SIGINT, &dfl, NULL);
    sigaction(SIGQUIT, &dfl, NULL);
    if (!shared->user_dumps)
    {
        char name[PATH_MOST + 32];
        snprintf(name, sizeof name, "%s/crash-%%p.dmp", shared->dump_dir[0] ? shared->dump_dir : "/tmp");
        if (shared->dump_dir[0]) make_dirs(shared->dump_dir);
        setenv("DOTNET_DbgEnableMiniDump", "1", 1);
        setenv("DOTNET_DbgMiniDumpName", name, 1);
        setenv("DOTNET_DbgMiniDumpType", shared->dumps == 2 ? "2" : "1", 1);
        if (shared->dumps) { setenv("DOTNET_EnableCrashReport", "1", 1); unsetenv("DOTNET_EnableCrashReportOnly"); }
        else { setenv("DOTNET_EnableCrashReportOnly", "1", 1); unsetenv("DOTNET_EnableCrashReport"); }
        setenv("ONI_MOONO_DUMP_ENV", "1", 1);
        /* No core file of the kernel's beside ours (systemd-coredump's can be GBs, and slows
         * the end); a piped core handler may not heed it. */
        if (shared->dumps)
        {
            struct rlimit core;
            if (getrlimit(RLIMIT_CORE, &core) == 0) { core.rlim_cur = 0; setrlimit(RLIMIT_CORE, &core); }
        }
    }
    struct sigaction ours;
    memset(&ours, 0, sizeof ours);
    ours.sa_sigaction = handler;
    ours.sa_flags = SA_SIGINFO | SA_ONSTACK;
    sigemptyset(&ours.sa_mask);
    for (size_t i = 0; i < sizeof handled / sizeof handled[0]; i++) sigaction(handled[i], &ours, NULL);
    (void)exe;
}

void crash_exiting(int code)
{
    if (!shared) return;
    shared->clean_code = code;
    __sync_synchronize();
    shared->clean = 1;
}

/* Called by the player (NativeCrashes.cs) once Player.log is open, with the runtime up: the log's
 * path, the main thread, the dump it asked for (0 none, 1 a dump, 2 with memory: --crash-dumps
 * full; the launcher has set it up already) and the personal folders to hide as
 * "PATH\0REPLACEMENT\0...\0" (empty with --log-paths full), in UTF-16. 1 when the watcher runs,
 * with 2 added when the user's own DOTNET_DbgEnableMiniDump settings write the dump. */
EXPORT int moono_crash_install(const uint16_t *log_path, uint32_t main, int dumps, const uint16_t *prefixes)
{
    static int installed;
    if (!shared || installed) return 0;
    installed = 1;
    (void)dumps;
    main_tid = (int)main;
    from_utf16(log_path, shared->log_path, sizeof shared->log_path);
    for (int i = 0; prefixes && *prefixes && i < PREFIX_MOST; i++)
    {
        from_utf16(prefixes, shared->prefix[i], sizeof shared->prefix[i]);
        while (*prefixes) prefixes++;
        prefixes++;
        from_utf16(prefixes, shared->replacement[i], sizeof shared->replacement[i]);
        while (*prefixes) prefixes++;
        prefixes++;
    }
    format_wanted = eventfd(0, EFD_CLOEXEC);
    format_done = eventfd(0, EFD_CLOEXEC);
    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    pthread_attr_setstacksize(&attributes, 512 * 1024);
    pthread_t thread;
    int started = format_wanted >= 0 && format_done >= 0 && pthread_create(&thread, &attributes, formatter, NULL) == 0;
    pthread_attr_destroy(&attributes);
    if (!started) return 0;
    pthread_detach(thread);
    __sync_synchronize();
    shared->installed = 1;
    return watched ? 1 | (shared->user_dumps ? 2 : 0) : 0;
}

static int key_is(const uint16_t *key, const char *name)
{
    while (*name && *key == (uint16_t)(unsigned char)*name) { key++; name++; }
    return *name == 0 && *key == 0;
}

/* What the crash window shows, from the player (NativeCrashes.Describe) as it learns it: `version`
 * (the release or commit), `build` (the game's), `language` (its code), `mods` (enabled), `gpu`
 * (its renderer) and `dialog` (`off`: no window). */
EXPORT void moono_crash_describe(const uint16_t *key, const uint16_t *value)
{
    if (!shared || !key || !value) return;
    char v[256];
    from_utf16(value, v, sizeof v);
    if (key_is(key, "version")) snprintf(shared->version, sizeof shared->version, "%s", v);
    else if (key_is(key, "build")) snprintf(shared->game_build, sizeof shared->game_build, "%s", v);
    else if (key_is(key, "language")) snprintf(shared->language, sizeof shared->language, "%s", v);
    else if (key_is(key, "gpu")) snprintf(shared->gpu, sizeof shared->gpu, "%s", v);
    else if (key_is(key, "mods")) shared->mods = atoi(v);
    else if (key_is(key, "dialog")) shared->dialog_off = strcmp(v, "off") == 0;
}

/* The player's own exit (Player.ProcessExit, AppDomain.ProcessExit), with its exit code. */
EXPORT void moono_crash_exiting(uint32_t code)
{
    crash_exiting((int)code);
}

/* ---- the test hook (--test-native-crash) ---------------------------------------------------- */

static volatile int *volatile nowhere;

__attribute__((noinline)) static int recurse(volatile int depth)
{
    volatile char frame[1024];
    frame[0] = (char)depth;
    return recurse(depth + 1) + frame[0];
}

static void *faulting_thread(void *what)
{
    if (what)
    {
        /* An alternate stack, as the runtime gives its own threads: a handler can run after the
         * thread's own stack has run out. */
        static char alternate[64 * 1024];
        stack_t s = { alternate, 0, sizeof alternate };
        sigaltstack(&s, NULL);
        recurse(0);
    }
    else *nowhere = 1;
    return NULL;
}

/* Faults on purpose, in native code: `access-violation` (a write to address 0) and `recursion`
 * (a stack overflow) on the calling thread, `thread` on a new native thread, `stack-overflow`
 * there too, `abort`, `heap` (a double free glibc finds), `exit` (_exit(3) from native code)
 * and `kill9` (SIGKILL, as from outside). Returns 0 for an unknown name. */
EXPORT int moono_crash_test(const uint16_t *what)
{
    if (key_is(what, "access-violation")) *nowhere = 1;
    else if (key_is(what, "thread") || key_is(what, "stack-overflow"))
    {
        pthread_t thread;
        pthread_attr_t attributes;
        pthread_attr_init(&attributes);
        pthread_attr_setstacksize(&attributes, 256 * 1024);
        if (pthread_create(&thread, &attributes, faulting_thread, (void *)(uintptr_t)(what[0] == 's')) == 0) pthread_join(thread, NULL);
        pthread_attr_destroy(&attributes);
    }
    else if (key_is(what, "recursion")) recurse(0);
    else if (key_is(what, "abort")) abort();
    else if (key_is(what, "heap"))
    {
        void *volatile block = malloc(64);
        free(block);
        free(block);
    }
    else if (key_is(what, "exit")) _exit(3);
    else if (key_is(what, "kill9")) kill(getpid(), SIGKILL);
    else return 0;
    return 1;
}

/* ---- the watcher ---------------------------------------------------------------------------- */

static crash_runtime_text runtime = { .eol = "\n" };
static int stderr_out = -1;
static volatile int reader_done;

/* glibc's own lines before an abort(): malloc's checks, the fortify and stack-protector checks,
 * assert. */
static const char *const glibc_lines[] = {
    "malloc(): ", "free(): ", "realloc(): ", "calloc(): ", "malloc_consolidate(): ", "munmap_chunk(): ", "double free or corruption",
    "corrupted ", "*** ", "~: Assertion `", NULL,
};

/* The game's standard error: passed on as it comes; the runtime's fatal-error text and glibc's
 * kept (up to 40 lines). */
static void *pass_on(void *pipe_fd)
{
    int fd = (int)(intptr_t)pipe_fd;
    static char chunk[4096], line[1024];
    size_t line_length = 0;
    for (int open = 1; open;)
    {
        ssize_t got = read(fd, chunk, sizeof chunk);
        if (got < 0 && errno == EINTR) continue;
        if (got <= 0)
        {
            /* The end: a last line without its line break still counts. */
            if (!line_length) break;
            chunk[0] = '\n';
            got = 1;
            open = 0;
        }
        else if (stderr_out >= 0)
        {
            for (ssize_t done = 0; done < got;)
            {
                ssize_t w = write(stderr_out, chunk + done, (size_t)(got - done));
                if (w <= 0) { if (w < 0 && errno == EINTR) continue; stderr_out = -1; break; }
                done += w;
            }
        }
        for (ssize_t i = 0; i < got; i++)
        {
            char c = chunk[i];
            if (c != '\n' && line_length + 1 < sizeof line) { if (c != '\r') line[line_length++] = c; continue; }
            line[line_length] = 0;
            crash_runtime_line(&runtime, line, glibc_lines);
            line_length = 0;
        }
    }
    __sync_synchronize();
    reader_done = 1;
    return NULL;
}

static volatile pid_t watched_game;

static void forward(int sig)
{
    if (watched_game > 0) kill(watched_game, sig);
}

/* The watcher's command line, as `ps` shows it: OniMoono --crash-watch, not the game's (scripts
 * find the game by its run home). */
static void set_title(int argc, char **argv)
{
    char *start = argv[0], *end = argv[argc - 1] + strlen(argv[argc - 1]);
    const char title[] = "OniMoono\0--crash-watch";
    if ((size_t)(end - start) >= sizeof title)
    {
        memset(start, 0, (size_t)(end - start));
        memcpy(start, title, sizeof title);
    }
    prctl(PR_SET_NAME, "crash-watch", 0, 0, 0);
}

/* ---- the runtime's crash report (createdump's .crashreport.json) ---------------------------- */

/* A little JSON reader: the report's threads and their frames are all it needs. */
typedef struct { const char *at, *end; } json_t;

static void json_space(json_t *j) { while (j->at < j->end && (*j->at == ' ' || *j->at == '\n' || *j->at == '\r' || *j->at == '\t')) j->at++; }

/* A string's text (escapes kept but \" and \\ undone) into out; 1 when it was a string. */
static int json_string(json_t *j, char *out, size_t most)
{
    json_space(j);
    if (j->at >= j->end || *j->at != '"') return 0;
    j->at++;
    size_t n = 0;
    while (j->at < j->end && *j->at != '"')
    {
        char c = *j->at++;
        if (c == '\\' && j->at < j->end) c = *j->at++;
        if (n + 1 < most) out[n++] = c;
    }
    if (most) out[n] = 0;
    if (j->at < j->end) j->at++;
    return 1;
}

static void json_skip(json_t *j)
{
    json_space(j);
    if (j->at >= j->end) return;
    char c = *j->at;
    if (c == '"') { char none[1]; json_string(j, none, 0); return; }
    if (c == '{' || c == '[')
    {
        int depth = 0;
        do
        {
            c = *j->at;
            if (c == '"') { char none[1]; json_string(j, none, 0); continue; }
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
            j->at++;
        } while (depth > 0 && j->at < j->end);
        return;
    }
    while (j->at < j->end && *j->at != ',' && *j->at != '}' && *j->at != ']') j->at++;
}

/* Into the object at j: calls `field` with each key, which reads or skips the value. */
typedef void (*json_field_fn)(json_t *j, const char *key, void *context);

static void json_object(json_t *j, json_field_fn field, void *context)
{
    json_space(j);
    if (j->at >= j->end || *j->at != '{') { json_skip(j); return; }
    j->at++;
    for (;;)
    {
        char key[64];
        json_space(j);
        if (j->at < j->end && *j->at == '}') { j->at++; return; }
        if (!json_string(j, key, sizeof key)) { json_skip(j); return; }
        json_space(j);
        if (j->at < j->end && *j->at == ':') j->at++;
        const char *before = j->at;
        field(j, key, context);
        if (j->at == before) json_skip(j);
        json_space(j);
        if (j->at < j->end && *j->at == ',') { j->at++; continue; }
        if (j->at < j->end && *j->at == '}') { j->at++; return; }
        return;
    }
}

static void json_array(json_t *j, json_field_fn element, void *context)
{
    json_space(j);
    if (j->at >= j->end || *j->at != '[') { json_skip(j); return; }
    j->at++;
    for (;;)
    {
        json_space(j);
        if (j->at < j->end && *j->at == ']') { j->at++; return; }
        const char *before = j->at;
        element(j, NULL, context);
        if (j->at == before) json_skip(j);
        json_space(j);
        if (j->at < j->end && *j->at == ',') { j->at++; continue; }
        if (j->at < j->end && *j->at == ']') j->at++;
        return;
    }
}

#define REPORT_FRAMES 128

typedef struct
{
    int managed;
    uint64_t address, offset;
    char module[64], method[160];
} report_frame_t;

typedef struct
{
    int crashed;
    int count;
    report_frame_t frames[REPORT_FRAMES];
} report_thread_t;

typedef struct
{
    report_thread_t current, crashed;
    int found;
} report_t;

static uint64_t json_number_text(const char *text) { return strtoull(text, NULL, 0); }

static void frame_field(json_t *j, const char *key, void *context)
{
    report_frame_t *f = (report_frame_t *)context;
    char value[256];
    if (strcmp(key, "is_managed") == 0) { json_space(j); if (json_string(j, value, sizeof value)) f->managed = strcmp(value, "true") == 0; else { f->managed = j->at < j->end && *j->at == 't'; json_skip(j); } }
    else if (strcmp(key, "native_address") == 0 && json_string(j, value, sizeof value)) f->address = json_number_text(value);
    else if (strcmp(key, "native_image_offset") == 0 && json_string(j, value, sizeof value)) f->offset = json_number_text(value);
    else if (strcmp(key, "native_module") == 0 && json_string(j, value, sizeof value)) snprintf(f->module, sizeof f->module, "%s", file_part(value));
    else if (strcmp(key, "method_name") == 0 && json_string(j, value, sizeof value)) snprintf(f->method, sizeof f->method, "%s", value);
    else if (strcmp(key, "class_name") == 0 && json_string(j, value, sizeof value) && !f->method[0])
    {
        /* method_name comes after class_name in the report: keep the class to prefix it */
        snprintf(f->method, sizeof f->method, "%s.", value);
    }
    else json_skip(j);
}

static void frame_element(json_t *j, const char *key, void *context)
{
    (void)key;
    report_thread_t *t = (report_thread_t *)context;
    report_frame_t f;
    memset(&f, 0, sizeof f);
    json_object(j, frame_field, &f);
    if (t->count < REPORT_FRAMES) t->frames[t->count++] = f;
}

static void thread_field(json_t *j, const char *key, void *context)
{
    report_thread_t *t = (report_thread_t *)context;
    char value[32];
    if (strcmp(key, "crashed") == 0)
    {
        json_space(j);
        if (json_string(j, value, sizeof value)) t->crashed = strcmp(value, "true") == 0;
        else { t->crashed = j->at < j->end && *j->at == 't'; json_skip(j); }
    }
    else if (strcmp(key, "stack_frames") == 0) json_array(j, frame_element, t);
    else json_skip(j);
}

static void thread_element(json_t *j, const char *key, void *context)
{
    (void)key;
    report_t *r = (report_t *)context;
    memset(&r->current, 0, sizeof r->current);
    json_object(j, thread_field, &r->current);
    if (r->current.crashed && !r->found) { r->crashed = r->current; r->found = 1; }
}

static void payload_field(json_t *j, const char *key, void *context)
{
    if (strcmp(key, "threads") == 0) json_array(j, thread_element, context);
    else json_skip(j);
}

static void top_field(json_t *j, const char *key, void *context)
{
    if (strcmp(key, "payload") == 0) json_object(j, payload_field, context);
    else json_skip(j);
}

/* The crashed thread's frames from the report at `path`; 0 when none. */
static report_t report;

static int read_report(const char *path)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    struct stat s;
    if (fstat(fd, &s) != 0 || s.st_size <= 0 || s.st_size > 64 * 1024 * 1024) { close(fd); return 0; }
    char *text = malloc((size_t)s.st_size);
    ssize_t got = text ? read(fd, text, (size_t)s.st_size) : -1;
    close(fd);
    if (got <= 0) { free(text); return 0; }
    json_t j = { text, text + got };
    memset(&report, 0, sizeof report);
    json_object(&j, top_field, &report);
    free(text);
    return report.found && report.crashed.count > 0;
}

/* The stack line and the managed frames from the report: native frames as module+offset, runs
 * of managed ones counted, from the noted fault's frame on (the frames above it are the
 * runtime's signal handler writing the dump). */
static void put_report_stack(crash_out *o, uint64_t fault_ip)
{
    report_thread_t *t = &report.crashed;
    int from = 0;
    if (fault_ip)
        for (int i = 0; i < t->count; i++)
            if (t->frames[i].address == fault_ip) { from = i; break; }
    crash_put(o, "[engine] native crash stack: ");
    int groups = 0, i = from;
    for (; i < t->count && groups < FRAMES_MOST; groups++)
    {
        report_frame_t *f = &t->frames[i];
        int run = 1;
        if (f->managed) while (i + run < t->count && t->frames[i + run].managed) run++;
        else while (i + run < t->count && !t->frames[i + run].managed && t->frames[i + run].address == f->address) run++;
        if (groups) crash_put(o, ", ");
        if (f->managed) { crash_put_dec(o, (uint64_t)run); crash_put(o, run == 1 ? " frame of generated code" : " frames of generated code"); }
        else
        {
            if (f->module[0]) { crash_put(o, f->module); crash_put(o, "+"); crash_put_hex(o, f->offset, 1); }
            else crash_put_hex(o, f->address, 1);
            if (run > 1) { crash_put(o, " (x"); crash_put_dec(o, (uint64_t)run); crash_put(o, ")"); }
        }
        i += run;
    }
    if (i < t->count) crash_put(o, ", ...");
    crash_put(o, "\n");
    int managed = 0;
    for (i = from; i < t->count && managed < 12; i++)
        if (t->frames[i].managed && t->frames[i].method[0])
        {
            crash_put(o, "[engine] native crash: managed:    at ");
            crash_put(o, t->frames[i].method);
            crash_put(o, "\n");
            managed++;
        }
}

/* ---- dumps ---------------------------------------------------------------------------------- */

/* Our dumps' names: crash-YYYYMMDD-hhmmss.dmp and crash-YYYYMMDD-hhmmss.crashreport.json; the
 * stem's length (21) when `name` is one, else 0. */
static int dump_stem(const char *name)
{
    static const char shape[] = "crash-########-######";
    for (int i = 0; shape[i]; i++)
        if (shape[i] == '#' ? name[i] < '0' || name[i] > '9' : name[i] != shape[i]) return 0;
    return strcmp(name + 21, ".dmp") == 0 || strcmp(name + 21, ".crashreport.json") == 0 ? 21 : 0;
}

/* The two newest dumps in `folder` kept (by their names, which are their times), with their
 * crash reports; the others' files deleted. Plain files only. */
static void prune_dumps(const char *folder)
{
    char newest[2][32] = { "", "" };
    DIR *dir = opendir(folder);
    if (!dir) return;
    for (struct dirent *e; (e = readdir(dir)) != NULL;)
    {
        if (!dump_stem(e->d_name)) continue;
        char stem[32];
        snprintf(stem, sizeof stem, "%.21s", e->d_name);
        if (strcmp(stem, newest[0]) == 0 || strcmp(stem, newest[1]) == 0) continue;
        if (strcmp(stem, newest[0]) > 0) { strcpy(newest[1], newest[0]); strcpy(newest[0], stem); }
        else if (strcmp(stem, newest[1]) > 0) strcpy(newest[1], stem);
    }
    rewinddir(dir);
    char path[PATH_MOST + 64];
    for (struct dirent *e; (e = readdir(dir)) != NULL;)
    {
        if (!dump_stem(e->d_name) || strncmp(e->d_name, newest[0], 21) == 0 || strncmp(e->d_name, newest[1], 21) == 0) continue;
        snprintf(path, sizeof path, "%s/%s", folder, e->d_name);
        struct stat s;
        if (lstat(path, &s) == 0 && S_ISREG(s.st_mode)) unlink(path);
    }
    closedir(dir);
}

/* `from` moved to `to` (renamed, or copied across file systems); 1 when it is there. */
static int move_file(const char *from, const char *to)
{
    if (rename(from, to) == 0) return 1;
    if (errno != EXDEV) return 0;
    int in = open(from, O_RDONLY | O_CLOEXEC), out = in >= 0 ? open(to, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644) : -1;
    int ok = in >= 0 && out >= 0;
    static char buffer[1 << 16];
    for (ssize_t got; ok && (got = read(in, buffer, sizeof buffer)) > 0;) ok = write(out, buffer, (size_t)got) == got;
    if (in >= 0) close(in);
    if (out >= 0) close(out);
    if (ok) unlink(from); else unlink(to);
    return ok;
}

static int ptrace_scope(void)
{
    int fd = open("/proc/sys/kernel/yama/ptrace_scope", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    char text[8] = "";
    ssize_t got = read(fd, text, sizeof text - 1);
    close(fd);
    return got > 0 ? atoi(text) : -1;
}

/* ---- the record ------------------------------------------------------------------------------ */

static char dump_path[PATH_MOST + 64], report_path[PATH_MOST + 64];
static uint64_t dump_size;

/* The error line for the window: the noted fault and its module; a managed exception's type,
 * method and message; glibc's reason with the signal; else the signal; empty for an exit code
 * alone (the window says so). */
static void error_line(int sig, int used_fault, int oom, char *out, size_t most)
{
    crash_out o = { out, 0, (uint32_t)most };
    out[0] = 0;
    if (used_fault)
    {
        put_signal(&o, shared->seen_signal);
        const char *meaning = signal_meaning(shared->seen_signal);
        if (meaning) { crash_put(&o, " ("); crash_put(&o, meaning); crash_put(&o, ")"); }
        if (shared->fault_module[0]) { crash_put(&o, " in "); crash_put(&o, shared->fault_module); }
    }
    else if (crash_managed_error(&runtime, &o)) { }
    else if (sig == SIGKILL && oom) crash_put(&o, "SIGKILL: the system ran out of memory");
    else if (sig)
    {
        put_signal(&o, sig);
        const char *meaning = signal_meaning(sig);
        if (meaning) { crash_put(&o, " ("); crash_put(&o, meaning); crash_put(&o, ")"); }
        /* glibc's reason, as the first kept line says it */
        const char *kept = strstr(runtime.text, "runtime: ");
        if (sig == SIGABRT && kept && !crash_starts_with(kept + 9, "Fatal error.") && !crash_starts_with(kept + 9, "Process terminated.")
            && !crash_starts_with(kept + 9, "Stack overflow."))
        {
            crash_put(&o, ": ");
            const char *end = strchr(kept, '\n');
            crash_put_n(&o, kept + 9, end ? (size_t)(end - kept - 9) : strlen(kept + 9));
        }
    }
}

/* The crash window: -1 none (ONI_MOONO_CRASH_DIALOG=off, --crash-dialog off, a run with
 * --run-home, an end from outside: SIGTERM, SIGINT, SIGHUP, SIGQUIT, a SIGKILL that was not the
 * out-of-memory killer's), 1 shown and activated, 0 shown without taking the focus
 * (ONI_MOONO_CRASH_DIALOG=inactive, for test runs). The environment wins over the option. */
static int dialog_wanted(int sig, int oom)
{
    if (sig == SIGTERM || sig == SIGINT || sig == SIGHUP || sig == SIGQUIT || sig == SIGPIPE || (sig == SIGKILL && !oom)) return -1;
    const char *value = getenv("ONI_MOONO_CRASH_DIALOG");
    if (value && strcmp(value, "off") == 0) return -1;
    if (value && strcmp(value, "inactive") == 0) return 0;
    if (value && strcmp(value, "on") == 0) return 1;
    return shared->dialog_off ? -1 : 1;
}

/* LD_PRELOAD without Steam's overlay, which would hook the crash window. */
static void drop_overlay(void)
{
    const char *preload = getenv("LD_PRELOAD");
    if (!preload) return;
    char kept[4096] = "";
    size_t n = 0;
    for (const char *at = preload; *at;)
    {
        size_t length = strcspn(at, ": ");
        if (length && !(length >= 22 && strstr(at, "gameoverlayrenderer.so") && (size_t)(strstr(at, "gameoverlayrenderer.so") - at) < length) && n + length + 2 < sizeof kept)
        {
            if (n) kept[n++] = ':';
            memcpy(kept + n, at, length);
            n += length;
            kept[n] = 0;
        }
        at += length;
        while (*at == ':' || *at == ' ') at++;
    }
    if (n) setenv("LD_PRELOAD", kept, 1); else unsetenv("LD_PRELOAD");
}

static void show_dialog(const char *exe, int status, int sig, int used_fault, int oom, int activate)
{
    static cw_report r;
    memset(&r, 0, sizeof r);
    /* Before the game gave its language (a crash before its main menu), the locale's. */
    const char *lang = shared->language;
    if (!lang[0])
    {
        lang = getenv("LC_ALL");
        if (!lang || !lang[0]) lang = getenv("LC_MESSAGES");
        if (!lang || !lang[0]) lang = getenv("LANG");
        if (!lang) lang = "";
    }
    r.language = strncmp(lang, "zh", 2) == 0 ? CW_CHINESE : CW_ENGLISH;
    error_line(sig, used_fault, oom, r.error, sizeof r.error);
    r.exit_code = (uint32_t)status;
    snprintf(r.version, sizeof r.version, "%s", shared->version);
    snprintf(r.game_build, sizeof r.game_build, "%s", shared->game_build);
    snprintf(r.gpu, sizeof r.gpu, "%s", shared->gpu);
    r.mods = shared->mods;
    int64_t now = (int64_t)time(NULL);
    r.play_seconds = now >= shared->started ? (uint64_t)(now - shared->started) : ~0ull;
    snprintf(r.log_path, sizeof r.log_path, "%s", shared->log_path);
    snprintf(r.dump_path, sizeof r.dump_path, "%s", dump_path);
    r.dump_size = dump_path[0] ? dump_size : 0;
    time_t t = (time_t)now;
    struct tm local;
    localtime_r(&t, &local);
    r.year = local.tm_year + 1900; r.month = local.tm_mon + 1; r.day = local.tm_mday;
    r.hour = local.tm_hour; r.minute = local.tm_min; r.second = local.tm_sec;
    r.activate = activate;
    /* The window in a fresh image of this executable: no game libraries, no watcher state. */
    int fd = memfd_create("oni-moono-crash", 0);
    if (fd < 0 || write(fd, &r, sizeof r) != (ssize_t)sizeof r || lseek(fd, 0, SEEK_SET) != 0) return;
    char number[16];
    snprintf(number, sizeof number, "%d", fd);
    drop_overlay();
    char *args[] = { (char *)exe, (char *)"--crash-window", number, NULL };
    execv(exe, args);
}

static void watch(pid_t game, int pipe_read, int err_out, const char *exe, int argc, char **argv)
{
    static char exe_copy[PATH_MAX];
    snprintf(exe_copy, sizeof exe_copy, "%s", exe);
    stderr_out = err_out;
    watched_game = game;
    struct sigaction ignore, pass;
    memset(&ignore, 0, sizeof ignore);
    memset(&pass, 0, sizeof pass);
    ignore.sa_handler = SIG_IGN;
    pass.sa_handler = forward;
    sigemptyset(&pass.sa_mask);
    /* A terminal's Ctrl+C reaches the whole group, the game too, which handles it. */
    sigaction(SIGINT, &ignore, NULL);
    sigaction(SIGQUIT, &ignore, NULL);
    sigaction(SIGPIPE, &ignore, NULL);
    sigaction(SIGTERM, &pass, NULL);
    sigaction(SIGHUP, &pass, NULL);
    set_title(argc, argv);
    pthread_t reader;
    int reading = pipe_read >= 0 && pthread_create(&reader, NULL, pass_on, (void *)(intptr_t)pipe_read) == 0;
    /* No reader: close the pipe, so the game's writes fail (the runtime ignores SIGPIPE) rather
       than block once it fills. */
    if (pipe_read >= 0 && !reading) close(pipe_read);

    int status = 0;
    while (waitpid(game, &status, 0) < 0 && errno == EINTR) { }
    watched_game = 0;
    int sig = WIFSIGNALED(status) ? WTERMSIG(status) : 0;
    int code = sig ? 128 + sig : WIFEXITED(status) ? WEXITSTATUS(status) : 1;
    __sync_synchronize();
    if (!shared->installed) _exit(code);
    if (!sig && shared->clean && (shared->clean_code & 255) == code) _exit(code);
    /* The rest of the game's standard error (its children may still hold the pipe). */
    if (reading)
        for (int i = 0; i < 200 && !reader_done; i++) usleep(10000);

    long oom_after = sig == SIGKILL ? oom_kills() : -1;
    int oom = sig == SIGKILL && oom_before >= 0 && oom_after > oom_before;
    int seen = shared->seen;
    int used_fault = seen == SEEN_SIGNAL && shared->seen_signal == (sig ? sig : -1);

    /* The dump the runtime wrote (crash-PID.dmp and its .crashreport.json), named by its time
     * beside the log, the two newest kept. */
    char written[PATH_MOST + 64], written_report[PATH_MOST + 96], folder[PATH_MOST];
    dump_path[0] = report_path[0] = 0;
    snprintf(folder, sizeof folder, "%s", shared->log_path);
    char *slash = strrchr(folder, '/');
    if (slash) *slash = 0; else snprintf(folder, sizeof folder, "%s", shared->dump_dir);
    int have_report = 0;
    if (!shared->user_dumps && shared->dump_dir[0])
    {
        snprintf(written, sizeof written, "%s/crash-%d.dmp", shared->dump_dir, (int)game);
        snprintf(written_report, sizeof written_report, "%s.crashreport.json", written);
        time_t now = time(NULL);
        struct tm local;
        localtime_r(&now, &local);
        char stem[64];
        strftime(stem, sizeof stem, "crash-%Y%m%d-%H%M%S", &local);
        struct stat s;
        if (stat(written, &s) == 0 && S_ISREG(s.st_mode))
        {
            snprintf(dump_path, sizeof dump_path, "%s/%s.dmp", folder, stem);
            if (move_file(written, dump_path)) dump_size = (uint64_t)s.st_size;
            else dump_path[0] = 0;
        }
        if (stat(written_report, &s) == 0 && S_ISREG(s.st_mode))
        {
            snprintf(report_path, sizeof report_path, "%s/%s.crashreport.json", folder, stem);
            if (!move_file(written_report, report_path)) snprintf(report_path, sizeof report_path, "%s", written_report);
            have_report = read_report(report_path);
        }
        if (dump_path[0] || report_path[0]) prune_dumps(folder);
    }

    static char text[TEXT_MOST + CRASH_RUNTIME_TEXT_MOST + 8192];
    crash_out o = { text, 0, sizeof text };
    if (used_fault)
    {
        /* The fault line; the stack from the crash report when there is one, else the scan. */
        const char *stack = strstr(shared->text, "[engine] native crash stack (scanned): ");
        if (have_report && stack)
        {
            crash_put_n(&o, shared->text, (size_t)(stack - shared->text));
            put_report_stack(&o, shared->seen_ip);
        }
        else crash_put(&o, shared->text);
    }
    else if (have_report && sig && sig != SIGKILL) put_report_stack(&o, 0);
    if (runtime.length) crash_put(&o, runtime.text);
    crash_put(&o, "[engine] native crash: the process ended with ");
    if (sig)
    {
        put_signal(&o, sig);
        const char *meaning = signal_meaning(sig);
        if (meaning) { crash_put(&o, " ("); crash_put(&o, meaning); crash_put(&o, ")"); }
    }
    else crash_put_dec(&o, (uint64_t)code);
    if (shared->clean)
    {
        crash_put(&o, " while exiting with ");
        crash_put_dec(&o, (uint64_t)(unsigned)shared->clean_code);
    }
    else if (sig == SIGKILL)
        crash_put(&o, oom ? "; the system ran out of memory: its out-of-memory killer ended it" : "; from outside (kill -9, a debugger, the system)");
    else if (!sig) crash_put(&o, " without the player's own exit (exit or _exit from native code, or from outside)");
    else if (is_fault(sig) && seen == SEEN_NONE) crash_put(&o, "; no handler of ours saw it");
    crash_put(&o, "\n");
    if (dump_path[0])
    {
        crash_put(&o, "[engine] native crash dump: ");
        put_path(&o, dump_path);
        crash_put(&o, " (");
        crash_put_dec(&o, (dump_size + 1023) / 1024);
        crash_put(&o, " KB)\n");
    }
    else if (shared->user_dumps && sig && sig != SIGKILL && is_fault(sig))
        crash_put(&o, "[engine] native crash dump: the runtime writes it as DOTNET_DbgEnableMiniDump in the environment says\n");
    else if (sig && sig != SIGKILL && shared->dumps && (is_fault(sig) || sig == SIGABRT) && !shared->user_dumps)
    {
        int scope = ptrace_scope();
        crash_put(&o, "[engine] native crash dump: none was written (the runtime's createdump failed or did not run");
        if (scope >= 2) { crash_put(&o, "; kernel.yama.ptrace_scope is "); crash_put_dec(&o, (uint64_t)scope); crash_put(&o, ", which keeps it from reading the game"); }
        crash_put(&o, ")\n");
    }
    if (report_path[0] && !dump_path[0])
    {
        crash_put(&o, "[engine] native crash report: ");
        put_path(&o, report_path);
        crash_put(&o, "\n");
    }
    int log = open(shared->log_path, O_WRONLY | O_APPEND | O_CLOEXEC);
    if (log >= 0)
    {
        if (write(log, text, o.length) < 0) { /* nowhere to write it */ }
        fsync(log);
        close(log);
    }
    int activate = dialog_wanted(sig, oom);
    if (activate >= 0) show_dialog(exe_copy, code, sig, used_fault, oom, activate);
    _exit(code);
}
