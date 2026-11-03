/* OniMoono.exe, the Windows player's executable (ARCHITECTURE §2, "The Windows executable").
 *
 * In place of the .NET SDK's apphost, for one reason: the GPU drivers of hybrid-graphics laptops
 * read two exported variables from the process's main executable, and the SDK's apphost cannot
 * export data. The original OxygenNotIncluded.exe exports both with the value 1, so the game
 * runs on the discrete GPU; this exe does the same. The player's GPU option (GpuChoice.cs) writes
 * them before the OpenGL driver loads: 1 for the discrete GPU, 0 for the integrated one.
 *
 * Otherwise it does what the apphost does, the documented way (dotnet/runtime,
 * docs/design/features/native-hosting.md): find hostfxr, start OniMoono.dll with this process's
 * arguments, and return its exit code. One thing more: the garbage collection option (GcMode.cs),
 * which the runtime fixes as it starts, set as the runtime property System.GC.Server. A self-contained player (setup and releases) has
 * hostfxr.dll beside this exe, which the apphost uses first, and so does this; a
 * framework-dependent one (scripts/build-windows.sh) finds the installed .NET through nethost,
 * which looks where the apphost looks (DOTNET_ROOT, the registered install, Program Files).
 *
 * Built by build.cmd with MSVC (static CRT, so nothing beyond Windows itself is needed). */

#define WIN32_LEAN_AND_MEAN
#define NETHOST_USE_AS_STATIC
#include <windows.h>
#include <shlobj.h>
#include <wchar.h>
#include <string.h>
#include "nethost.h"
#include "hostfxr.h"

/* NVIDIA Optimus and AMD PowerXpress: nonzero asks for the high-performance GPU, as the original
 * game's exe exports them. Nothing changes them at run time (GpuChoice). */
__declspec(dllexport) DWORD NvOptimusEnablement = 1;
__declspec(dllexport) DWORD AmdPowerXpressRequestHighPerformance = 1;

#define APP_DLL L"OniMoono.dll"
#define LONG_PATH 32768

/* What hostfxr reported while starting the app, for the failure message. */
static wchar_t hostfxr_messages[4096];

static void HOSTFXR_CALLTYPE collect_error(const wchar_t *message)
{
    size_t used = wcslen(hostfxr_messages);
    if (used + 2 < _countof(hostfxr_messages))
    {
        wcsncat_s(hostfxr_messages, _countof(hostfxr_messages), message, _TRUNCATE);
        wcsncat_s(hostfxr_messages, _countof(hostfxr_messages), L"\n", _TRUNCATE);
    }
}

/* stderr when the parent redirected it (a pipe or a file: WSL interop, `2> err.txt`). */
static HANDLE redirected_stderr(void)
{
    HANDLE handle = GetStdHandle(STD_ERROR_HANDLE);
    if (handle == NULL || handle == INVALID_HANDLE_VALUE) return NULL;
    DWORD type = GetFileType(handle);
    return type == FILE_TYPE_PIPE || type == FILE_TYPE_DISK ? handle : NULL;
}

static void write_utf8(HANDLE file, const wchar_t *text)
{
    char buffer[16384];
    int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, buffer, sizeof buffer, NULL, NULL);
    DWORD written;
    if (length > 1) WriteFile(file, buffer, (DWORD)(length - 1), &written, NULL);
}

/* The failure, where it is seen: stderr when the parent redirected it (scripts and test runs,
 * whose run homes keep the user's own files out of reach); otherwise, as Steam or Explorer
 * start it, a message box and a line appended to the game's Player.log (the player keeps that
 * as Player-prev.log on its next start). */
static int fail(int status, const wchar_t *what, const wchar_t *detail)
{
    wchar_t message[8192];
    swprintf_s(message, _countof(message), L"Moono could not start: %ls%ls%ls (status 0x%08x)%ls%ls",
               what, detail ? L": " : L"", detail ? detail : L"", (unsigned)status,
               hostfxr_messages[0] ? L"\n\n" : L"", hostfxr_messages);
    wchar_t line[8300];
    swprintf_s(line, _countof(line), L"[OniMoono.exe] %ls\n", message);
    OutputDebugStringW(line);

    HANDLE err = redirected_stderr();
    if (err)
    {
        write_utf8(err, line);
        return status != 0 ? status : 1;
    }
    PWSTR low = NULL;
    if (SUCCEEDED(SHGetKnownFolderPath(&FOLDERID_LocalAppDataLow, 0, NULL, &low)))
    {
        wchar_t log[MAX_PATH * 2];
        swprintf_s(log, _countof(log), L"%ls\\Klei\\Oxygen Not Included\\Player.log", low);
        HANDLE file = CreateFileW(log, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (file != INVALID_HANDLE_VALUE)
        {
            write_utf8(file, line);
            CloseHandle(file);
        }
        CoTaskMemFree(low);
    }

    MessageBoxW(NULL, message, L"Moono", MB_OK | MB_ICONERROR);
    return status != 0 ? status : 1;
}

/* The garbage collection option (GcMode.cs, docs/TWEAKS.md): true for low-memory (Workstation GC).
 * As the player reads it: the last `--gc VALUE` on the command line, else the settings file's
 * "gc", `<persistent data path>\oni-moono\settings.json` (PlayerSettings.cs), the persistent data
 * path being Unity's, LocalLow\Klei\Oxygen Not Included (`--run-home DIR`: DIR\AppData\LocalLow).
 * The file is the player's own, written by its JSON writer, so a plain scan for the key is enough;
 * when the two read differently, the log's `gc:` line shows it. */
static int wants_workstation_gc(int argc, wchar_t **argv)
{
    const wchar_t *gc = NULL, *run_home = NULL;
    for (int i = 1; i + 1 < argc; i++)
    {
        if (wcscmp(argv[i], L"--gc") == 0) gc = argv[i + 1];
        else if (wcscmp(argv[i], L"--run-home") == 0) run_home = argv[i + 1];
    }
    if (gc) return wcscmp(gc, L"low-memory") == 0;

    static wchar_t path[LONG_PATH];
    if (run_home)
    {
        static wchar_t full[LONG_PATH];
        if (_wfullpath(full, run_home, LONG_PATH) == NULL) return 0;
        swprintf_s(path, LONG_PATH, L"%ls\\AppData\\LocalLow", full);
    }
    else
    {
        PWSTR low = NULL;
        if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppDataLow, 0, NULL, &low))) return 0;
        swprintf_s(path, LONG_PATH, L"%ls", low);
        CoTaskMemFree(low);
    }
    wcscat_s(path, LONG_PATH, L"\\Klei\\Oxygen Not Included\\oni-moono\\settings.json");

    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return 0;
    static char text[65536];
    DWORD read = 0;
    BOOL ok = ReadFile(file, text, sizeof text - 1, &read, NULL);
    CloseHandle(file);
    if (!ok) return 0;
    text[read] = 0;
    const char *at = strstr(text, "\"gc\"");
    if (at == NULL) return 0;
    at += 4;
    while (*at == ' ' || *at == '\t' || *at == '\r' || *at == '\n') at++;
    if (*at++ != ':') return 0;
    while (*at == ' ' || *at == '\t' || *at == '\r' || *at == '\n') at++;
    return strncmp(at, "\"low-memory\"", 12) == 0;
}

int wmain(int argc, wchar_t **argv)
{
    static wchar_t exe[LONG_PATH], dir[LONG_PATH], app[LONG_PATH], fxr[LONG_PATH];
    DWORD length = GetModuleFileNameW(NULL, exe, LONG_PATH);
    if (length == 0 || length >= LONG_PATH) return fail((int)GetLastError(), L"the executable's own path is unknown", NULL);
    wcscpy_s(dir, LONG_PATH, exe);
    wchar_t *slash = wcsrchr(dir, L'\\');
    if (slash == NULL) return fail(1, L"the executable's own path is unknown", exe);
    *slash = 0;
    swprintf_s(app, LONG_PATH, L"%ls\\%ls", dir, APP_DLL);
    if (GetFileAttributesW(app) == INVALID_FILE_ATTRIBUTES) return fail(1, L"the player is incomplete; reinstall it (run setup again)", app);

    /* hostfxr: the app's own (self-contained), else the installed .NET's. */
    const wchar_t *dotnet_root = NULL;
    swprintf_s(fxr, LONG_PATH, L"%ls\\hostfxr.dll", dir);
    if (GetFileAttributesW(fxr) != INVALID_FILE_ATTRIBUTES) dotnet_root = dir;
    else
    {
        struct get_hostfxr_parameters where = { sizeof where, app, NULL };
        size_t size = LONG_PATH;
        int rc = get_hostfxr_path(fxr, &size, &where);
        if (rc != 0) return fail(rc, L"no .NET runtime was found (hostfxr.dll is neither beside OniMoono.exe nor in an installed .NET 10)", NULL);
    }
    HMODULE lib = LoadLibraryExW(fxr, NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (lib == NULL) return fail((int)GetLastError(), L"hostfxr.dll could not be loaded", fxr);
    hostfxr_set_error_writer_fn set_error_writer = (hostfxr_set_error_writer_fn)GetProcAddress(lib, "hostfxr_set_error_writer");
    hostfxr_initialize_for_dotnet_command_line_fn initialize =
        (hostfxr_initialize_for_dotnet_command_line_fn)GetProcAddress(lib, "hostfxr_initialize_for_dotnet_command_line");
    hostfxr_run_app_fn run_app = (hostfxr_run_app_fn)GetProcAddress(lib, "hostfxr_run_app");
    hostfxr_close_fn close = (hostfxr_close_fn)GetProcAddress(lib, "hostfxr_close");
    hostfxr_set_runtime_property_value_fn set_property =
        (hostfxr_set_runtime_property_value_fn)GetProcAddress(lib, "hostfxr_set_runtime_property_value");
    if (!set_error_writer || !initialize || !run_app || !close || !set_property) return fail(1, L"hostfxr.dll is too old (no hostfxr_initialize_for_dotnet_command_line)", fxr);

    /* The app's command line: the dll, then this process's arguments as the C runtime split
     * them (as the apphost has them), unchanged. */
    const wchar_t **args = (const wchar_t **)HeapAlloc(GetProcessHeap(), 0, sizeof(wchar_t *) * (argc + 1));
    if (args == NULL) return fail(1, L"out of memory", NULL);
    args[0] = app;
    for (int i = 1; i < argc; i++) args[i] = argv[i];
    args[argc > 0 ? argc : 1] = NULL;

    set_error_writer(collect_error);
    struct hostfxr_initialize_parameters parameters = { sizeof parameters, exe, dotnet_root };
    hostfxr_handle context = NULL;
    int rc = initialize(argc > 0 ? argc : 1, args, &parameters, &context);
    if (rc < 0 || context == NULL)
    {
        if (context) close(context);
        return fail(rc, L"the .NET runtime could not be initialized", app);
    }
    /* Low memory: Workstation GC over the runtimeconfig's Server GC (a DOTNET_gcServer in the
     * environment still wins, as the runtime reads it first). */
    if (wants_workstation_gc(argc, argv))
    {
        rc = set_property(context, L"System.GC.Server", L"false");
        if (rc < 0)
        {
            close(context);
            return fail(rc, L"the garbage collection option could not be set", NULL);
        }
    }
    /* The app's exit code; or a hosting status (0x8000808x) with hostfxr's reasons when the
     * runtime itself could not start (the app writes its own errors to Player.log). */
    rc = run_app(context);
    close(context);
    if (((unsigned)rc & 0xFFFFFF00u) == 0x80008000u && hostfxr_messages[0]) return fail(rc, L"the .NET runtime could not start", app);
    return rc;
}
