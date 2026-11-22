/* OniMoono, the Linux player's executable (ARCHITECTURE §2, "The Linux executable").
 *
 * In place of the .NET SDK's apphost, as host.c is on Windows, for the native crash records
 * (crash_linux.c): a process that sees how the game ended and writes what a crash in native code
 * leaves to Player.log, with a dump beside it, and then shows the crash window
 * (crash_window_linux.c). The apphost has no native code of ours to run before the runtime.
 *
 * Otherwise it does what the apphost does, the documented way (dotnet/runtime,
 * docs/design/features/native-hosting.md): find hostfxr, start OniMoono.dll with this process's
 * arguments, and return its exit code. Also as on Windows: the garbage collection option
 * (GcMode.cs), set as the runtime property System.GC.Server before the runtime starts, so the
 * player no longer starts itself again to apply it; and the versioned install's entry point
 * (docs/INSTALL-MODES.md), which picks the installed version before any runtime loads.
 *
 * A self-contained player (setup and releases) has libhostfxr.so beside this executable; a
 * framework-dependent one (dotnet build) finds the installed .NET where the apphost looks:
 * DOTNET_ROOT, /etc/dotnet/install_location, /usr/share/dotnet, /usr/lib/dotnet.
 *
 * Built by build-linux.sh with gcc or clang (scripts/build-native-host-linux.sh: the Steam
 * Runtime's sniper SDK, glibc 2.31), C only, no libstdc++. */

#define _GNU_SOURCE
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <sched.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>
#include "crash_linux.h"

#define APP_DLL "OniMoono.dll"
#define SWITCH_EXIT 77

/* hostfxr.h's, with char_t = char as on every platform but Windows. */
typedef void *hostfxr_handle;
struct hostfxr_initialize_parameters
{
    size_t size;
    const char *host_path;
    const char *dotnet_root;
};
typedef void (*hostfxr_error_writer_fn)(const char *message);
typedef hostfxr_error_writer_fn (*hostfxr_set_error_writer_fn)(hostfxr_error_writer_fn writer);
typedef int (*hostfxr_initialize_for_dotnet_command_line_fn)(int argc, const char **argv,
    const struct hostfxr_initialize_parameters *parameters, hostfxr_handle *context);
typedef int (*hostfxr_run_app_fn)(const hostfxr_handle context);
typedef int (*hostfxr_close_fn)(const hostfxr_handle context);
typedef int (*hostfxr_set_runtime_property_value_fn)(const hostfxr_handle context, const char *name, const char *value);

/* What hostfxr reported while starting the app, for the failure message. */
static char hostfxr_messages[4096];

static void collect_error(const char *message)
{
    size_t used = strlen(hostfxr_messages), n = strlen(message);
    if (used + n + 2 < sizeof hostfxr_messages)
    {
        memcpy(hostfxr_messages + used, message, n);
        hostfxr_messages[used + n] = '\n';
        hostfxr_messages[used + n + 1] = 0;
    }
}

static const char *run_home_of(int argc, char **argv)
{
    const char *run_home = NULL;
    for (int i = 1; i + 1 < argc; i++)
        if (strcmp(argv[i], "--run-home") == 0) run_home = argv[i + 1];
    return run_home;
}

/* The game's folder of per-user files, as Program.ConfigureLinux makes it:
 * `--run-home DIR` → DIR/config, else $XDG_CONFIG_HOME, else ~/.config, then
 * /unity3d/Klei/Oxygen Not Included. 0 when unknown. */
static int persistent_dir(int argc, char **argv, char *out, size_t most)
{
    const char *run_home = run_home_of(argc, argv), *config = getenv("XDG_CONFIG_HOME"), *home = getenv("HOME");
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

/* The failure, on standard error (a terminal, Steam's log, a script's redirection), and as a
 * line appended to the game's Player.log, where a player who started it from Steam looks (the
 * player keeps that as Player-prev.log on its next start). Test runs (ONI_HOST_NO_DIALOG=1)
 * leave the log alone and write the line to ONI_HOST_MESSAGES' file when that names one. */
static int fail_args_count;
static char **fail_args;

static int fail(int status, const char *what, const char *detail)
{
    char message[8192];
    snprintf(message, sizeof message, "Moono could not start: %s%s%s (status 0x%08x)%s%s", what, detail ? ": " : "", detail ? detail : "",
             (unsigned)status, hostfxr_messages[0] ? "\n\n" : "", hostfxr_messages);
    char line[8300];
    int n = snprintf(line, sizeof line, "[OniMoono] %s\n", message);
    if (n < 0) n = 0;
    if ((size_t)n >= sizeof line) n = sizeof line - 1;
    if (write(2, line, (size_t)n) < 0) { /* nowhere to say it */ }
    const char *quiet = getenv("ONI_HOST_NO_DIALOG");
    char log[PATH_MAX + 32];
    const char *messages = getenv("ONI_HOST_MESSAGES");
    int fd = -1;
    if (quiet && strcmp(quiet, "1") == 0)
    {
        if (messages && messages[0]) fd = open(messages, O_WRONLY | O_APPEND | O_CREAT | O_CLOEXEC, 0644);
    }
    else if (persistent_dir(fail_args_count, fail_args, log, PATH_MAX))
    {
        strcat(log, "/Player.log");
        fd = open(log, O_WRONLY | O_APPEND | O_CREAT | O_CLOEXEC, 0644);
    }
    if (fd >= 0)
    {
        if (write(fd, line, (size_t)n) < 0) { /* nothing more to do */ }
        close(fd);
    }
    return status > 0 && status < 256 ? status : 1;
}

/* The garbage collection option (GcMode.cs, docs/TWEAKS.md): true for low-memory (Workstation
 * GC). As the player reads it: the last `--gc VALUE` on the command line, else the settings
 * file's "gc", PERSISTENT/oni-moono/settings.json (PlayerSettings.cs). The file is the player's
 * own, written by its JSON writer, so a plain scan for the key is enough; when the two read
 * differently, the log's `gc:` line shows it. */
static int wants_workstation_gc(int argc, char **argv)
{
    const char *gc = NULL;
    for (int i = 1; i + 1 < argc; i++)
        if (strcmp(argv[i], "--gc") == 0) gc = argv[i + 1];
    if (gc) return strcmp(gc, "low-memory") == 0;
    char path[PATH_MAX + 64];
    if (!persistent_dir(argc, argv, path, PATH_MAX)) return 0;
    strcat(path, "/oni-moono/settings.json");
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    static char text[65536];
    ssize_t got = read(fd, text, sizeof text - 1);
    close(fd);
    if (got <= 0) return 0;
    text[got] = 0;
    const char *at = strstr(text, "\"gc\"");
    if (at == NULL) return 0;
    at += 4;
    while (*at == ' ' || *at == '\t' || *at == '\r' || *at == '\n') at++;
    if (*at++ != ':') return 0;
    while (*at == ' ' || *at == '\t' || *at == '\r' || *at == '\n') at++;
    return strncmp(at, "\"low-memory\"", 12) == 0;
}

static int is_dir(const char *path)
{
    struct stat s;
    return stat(path, &s) == 0 && S_ISDIR(s.st_mode);
}

static int is_file(const char *path)
{
    struct stat s;
    return stat(path, &s) == 0 && S_ISREG(s.st_mode);
}

/* ---- the versioned install's entry point ---------------------------------------------------- */

/* A version folder's name: letters, digits, dots and dashes, not starting with a dot. */
static int is_version_name(const char *name)
{
    if (name[0] == 0 || name[0] == '.' || strlen(name) > 64) return 0;
    for (const char *c = name; *c; c++)
        if (!((*c >= '0' && *c <= '9') || (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || *c == '.' || *c == '-')) return 0;
    return 1;
}

/* ROOT/state/current's version, else the newest version folder by name; 0 when none. */
static int pick_version(const char *root, char *version, size_t most)
{
    char path[PATH_MAX];
    version[0] = 0;
    snprintf(path, sizeof path, "%s/state/current", root);
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd >= 0)
    {
        char text[80];
        ssize_t got = read(fd, text, sizeof text - 1);
        close(fd);
        size_t n = 0;
        for (ssize_t i = 0; i < got && n + 1 < most && text[i] != '\r' && text[i] != '\n'; i++) version[n++] = text[i];
        version[n] = 0;
        snprintf(path, sizeof path, "%s/player/%s/OniMoono", root, version);
        if (is_version_name(version) && access(path, X_OK) == 0) return 1;
        version[0] = 0;
    }
    snprintf(path, sizeof path, "%s/player", root);
    DIR *dir = opendir(path);
    if (!dir) return 0;
    for (struct dirent *e; (e = readdir(dir)) != NULL;)
        if (is_version_name(e->d_name) && strcmp(e->d_name, version) > 0 && strlen(e->d_name) < most)
        {
            snprintf(path, sizeof path, "%s/player/%s/OniMoono", root, e->d_name);
            if (access(path, X_OK) == 0) strcpy(version, e->d_name);
        }
    closedir(dir);
    return version[0] != 0;
}

static volatile pid_t entry_child;

static void forward_signal(int sig)
{
    if (entry_child > 0) kill(entry_child, sig);
}

/* ROOT/OniMoono, with no OniMoono.dll beside it and a player/ folder: the entry point. It starts
 * ROOT/player/<state/current>/OniMoono (else the newest) with ONI_MOONO_INSTALL_ROOT set, as a
 * child it waits for, and starts the then current one again when that exits with SWITCH_EXIT
 * having written ROOT/state/switch (at most three times). It stays a small parent rather than
 * hosting the runtime itself: the game's App.Restart then names the version's executable, as the
 * entry-point script's did (RestartEntry.cs), and any version's player works under it. Its exit
 * status is the player's (128 + the signal for a signal). */
static int entry_main(const char *root, int argc, char **argv)
{
    setenv("ONI_MOONO_INSTALL_ROOT", root, 1);
    unsetenv("ONI_MOONO_INSTALL_SOURCE");
    struct sigaction ignore = { 0 }, forward = { 0 };
    ignore.sa_handler = SIG_IGN;
    forward.sa_handler = forward_signal;
    sigemptyset(&forward.sa_mask);
    /* A terminal's Ctrl+C reaches the whole group, the player too, which handles it. */
    sigaction(SIGINT, &ignore, NULL);
    sigaction(SIGQUIT, &ignore, NULL);
    sigaction(SIGTERM, &forward, NULL);
    sigaction(SIGHUP, &forward, NULL);
    for (int switches = 0;; switches++)
    {
        char version[80], exe[PATH_MAX];
        if (!pick_version(root, version, sizeof version)) return fail(1, "no version of Moono is installed here", root);
        snprintf(exe, sizeof exe, "%s/player/%s/OniMoono", root, version);
        pid_t child = fork();
        if (child < 0) return fail(errno, "the installed player could not be started", exe);
        if (child == 0)
        {
            struct sigaction dfl = { 0 };
            dfl.sa_handler = SIG_DFL;
            sigaction(SIGINT, &dfl, NULL);
            sigaction(SIGQUIT, &dfl, NULL);
            sigaction(SIGTERM, &dfl, NULL);
            sigaction(SIGHUP, &dfl, NULL);
            argv[0] = exe;
            execv(exe, argv);
            _exit(fail(errno, "the installed player could not be started", exe));
        }
        entry_child = child;
        int status = 0;
        while (waitpid(child, &status, 0) < 0 && errno == EINTR) { }
        entry_child = 0;
        if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
        int code = WIFEXITED(status) ? WEXITSTATUS(status) : 1;
        char path[PATH_MAX];
        snprintf(path, sizeof path, "%s/state/switch", root);
        if (code == SWITCH_EXIT && is_file(path) && switches < 3)
        {
            unlink(path);
            continue;
        }
        (void)argc;
        return code;
    }
}

/* ---- hostfxr --------------------------------------------------------------------------------- */

/* Compares two version folder names as versions (1.10.0 after 1.9.0; a pre-release before its
 * release). */
static int compare_versions(const char *a, const char *b)
{
    for (;;)
    {
        char *ea, *eb;
        unsigned long na = strtoul(a, &ea, 10), nb = strtoul(b, &eb, 10);
        if (na != nb) return na < nb ? -1 : 1;
        a = ea;
        b = eb;
        if (*a == '.' && *b == '.') { a++; b++; continue; }
        if (*a == *b) return *a == 0 ? 0 : strcmp(a, b);
        if (*a == 0) return 1;     /* 10.0.0 after 10.0.0-rc.1 */
        if (*b == 0) return -1;
        return strcmp(a, b);
    }
}

/* ROOT/host/fxr/<newest>/libhostfxr.so; 0 when none. */
static int fxr_in(const char *root, char *out, size_t most)
{
    char path[PATH_MAX], best[128] = "";
    snprintf(path, sizeof path, "%s/host/fxr", root);
    DIR *dir = opendir(path);
    if (!dir) return 0;
    for (struct dirent *e; (e = readdir(dir)) != NULL;)
    {
        if (e->d_name[0] < '0' || e->d_name[0] > '9' || strlen(e->d_name) >= sizeof best) continue;
        snprintf(path, sizeof path, "%s/host/fxr/%s/libhostfxr.so", root, e->d_name);
        if (is_file(path) && (!best[0] || compare_versions(e->d_name, best) > 0)) strcpy(best, e->d_name);
    }
    closedir(dir);
    if (!best[0]) return 0;
    return snprintf(out, most, "%s/host/fxr/%s/libhostfxr.so", root, best) < (int)most;
}

/* The first line of an install_location file. */
static int read_location(const char *file, char *out, size_t most)
{
    int fd = open(file, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return 0;
    ssize_t got = read(fd, out, most - 1);
    close(fd);
    if (got <= 0) return 0;
    out[got] = 0;
    out[strcspn(out, "\r\n")] = 0;
    return out[0] != 0;
}

/* An installed .NET's hostfxr, where the apphost looks for one. */
static int installed_fxr(char *fxr, size_t most, char *root, size_t root_most)
{
    const char *names[] = { "DOTNET_ROOT_X64", "DOTNET_ROOT" };
    for (int i = 0; i < 2; i++)
    {
        const char *v = getenv(names[i]);
        if (v && v[0] && fxr_in(v, fxr, most)) { snprintf(root, root_most, "%s", v); return 1; }
    }
    char location[PATH_MAX];
    const char *files[] = { "/etc/dotnet/install_location_x64", "/etc/dotnet/install_location" };
    for (int i = 0; i < 2; i++)
        if (read_location(files[i], location, sizeof location) && fxr_in(location, fxr, most)) { snprintf(root, root_most, "%s", location); return 1; }
    const char *defaults[] = { "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet" };
    for (int i = 0; i < 3; i++)
        if (fxr_in(defaults[i], fxr, most)) { snprintf(root, root_most, "%s", defaults[i]); return 1; }
    return 0;
}

int main(int argc, char **argv)
{
    fail_args_count = argc;
    fail_args = argv;
    if (argc >= 2 && strcmp(argv[1], "--crash-window") == 0) return crash_window_main(argc, argv);
    if (argc >= 2 && strcmp(argv[1], "--crash-dialog-render") == 0) return crash_dialog_render(argc, argv);
    /* ONI_AFFINITY=HEXMASK (tests of small machines): the logical processors this process may
       run on, set before the runtime starts so that it counts only those. */
    const char *affinity = getenv("ONI_AFFINITY");
    if (affinity && affinity[0])
    {
        unsigned long long mask = strtoull(affinity, NULL, 16);
        cpu_set_t set;
        CPU_ZERO(&set);
        for (int i = 0; i < 64; i++) if (mask & (1ull << i)) CPU_SET(i, &set);
        if (mask) sched_setaffinity(0, sizeof set, &set);
    }
    static char exe[PATH_MAX], dir[PATH_MAX], app[PATH_MAX + 32], fxr[PATH_MAX], dotnet_root[PATH_MAX];
    ssize_t length = readlink("/proc/self/exe", exe, sizeof exe - 1);
    if (length <= 0) return fail(errno, "the executable's own path is unknown", NULL);
    exe[length] = 0;
    strcpy(dir, exe);
    char *slash = strrchr(dir, '/');
    if (slash == NULL) return fail(1, "the executable's own path is unknown", exe);
    *slash = 0;
    snprintf(app, sizeof app, "%s/%s", dir, APP_DLL);
    /* The versioned install's entry point (see entry_main). */
    if (!is_file(app))
    {
        char player[PATH_MAX + 16];
        snprintf(player, sizeof player, "%s/player", dir);
        if (is_dir(player)) return entry_main(dir, argc, argv);
        return fail(1, "the player is incomplete; reinstall it (run setup again)", app);
    }

    /* From here on this is the game, a child of the watcher (or the process itself, with
     * ONI_MOONO_CRASH_WATCH=off). */
    crash_start(exe, argc, argv);

    /* hostfxr: the app's own (self-contained), else the installed .NET's. */
    const char *root = NULL;
    snprintf(fxr, sizeof fxr, "%s/libhostfxr.so", dir);
    if (is_file(fxr)) root = dir;
    else if (installed_fxr(fxr, sizeof fxr, dotnet_root, sizeof dotnet_root)) root = dotnet_root;
    else return fail(1, "no .NET runtime was found (libhostfxr.so is neither beside OniMoono nor in an installed .NET 10; set DOTNET_ROOT)", NULL);
    void *lib = dlopen(fxr, RTLD_NOW | RTLD_LOCAL);
    if (lib == NULL) return fail(1, "libhostfxr.so could not be loaded", dlerror());
    hostfxr_set_error_writer_fn set_error_writer = (hostfxr_set_error_writer_fn)dlsym(lib, "hostfxr_set_error_writer");
    hostfxr_initialize_for_dotnet_command_line_fn initialize =
        (hostfxr_initialize_for_dotnet_command_line_fn)dlsym(lib, "hostfxr_initialize_for_dotnet_command_line");
    hostfxr_run_app_fn run_app = (hostfxr_run_app_fn)dlsym(lib, "hostfxr_run_app");
    hostfxr_close_fn close_context = (hostfxr_close_fn)dlsym(lib, "hostfxr_close");
    hostfxr_set_runtime_property_value_fn set_property = (hostfxr_set_runtime_property_value_fn)dlsym(lib, "hostfxr_set_runtime_property_value");
    if (!set_error_writer || !initialize || !run_app || !close_context || !set_property)
        return fail(1, "libhostfxr.so is too old (no hostfxr_initialize_for_dotnet_command_line)", fxr);

    /* The app's command line: the dll, then this process's arguments, unchanged. */
    const char **args = calloc((size_t)(argc > 0 ? argc : 1) + 1, sizeof *args);
    if (args == NULL) return fail(1, "out of memory", NULL);
    args[0] = app;
    for (int i = 1; i < argc; i++) args[i] = argv[i];

    set_error_writer(collect_error);
    struct hostfxr_initialize_parameters parameters = { sizeof parameters, exe, root };
    hostfxr_handle context = NULL;
    int rc = initialize(argc > 0 ? argc : 1, args, &parameters, &context);
    if (rc < 0 || context == NULL)
    {
        if (context) close_context(context);
        return fail(rc, "the .NET runtime could not be initialized", app);
    }
    /* Low memory: Workstation GC over the runtimeconfig's Server GC (a DOTNET_gcServer in the
     * environment still wins, as the runtime reads it first). */
    if (wants_workstation_gc(argc, argv))
    {
        rc = set_property(context, "System.GC.Server", "false");
        if (rc < 0)
        {
            close_context(context);
            return fail(rc, "the garbage collection option could not be set", NULL);
        }
    }
    /* The app's exit code; or a hosting status (0x8000808x) with hostfxr's reasons when the
     * runtime itself could not start (the app writes its own errors to Player.log). */
    rc = run_app(context);
    crash_exiting(rc);
    close_context(context);
    if (((unsigned)rc & 0xFFFFFF00u) == 0x80008000u && hostfxr_messages[0]) return fail(rc, "the .NET runtime could not start", app);
    return rc;
}
