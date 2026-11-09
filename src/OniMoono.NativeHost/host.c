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
 * which the runtime fixes as it starts, set as the runtime property System.GC.Server; and the
 * native crash records (crash.c): a watcher process, started first, that writes what a crash in
 * native code leaves to Player.log and, after a crash, shows the crash window (crash_dialog.cpp),
which `--crash-dialog-render` also draws to a PNG without showing it. A self-contained player (setup and releases) has
 * hostfxr.dll beside this exe, which the apphost uses first, and so does this; a
 * framework-dependent one (scripts/build-windows.sh) finds the installed .NET through nethost,
 * which looks where the apphost looks (DOTNET_ROOT, the registered install, Program Files).
 *
 * Built by build.cmd with MSVC (static CRT, so nothing beyond Windows itself is needed). */

#define WIN32_LEAN_AND_MEAN
#define NETHOST_USE_AS_STATIC
#include <windows.h>
#include <shlobj.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>
#include "nethost.h"
#include "hostfxr.h"
#include "crash.h"
#include "crash_dialog.h"

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
static HANDLE startup_stderr = INVALID_HANDLE_VALUE;  /* as started, before crash_start's pipe */

static HANDLE redirected_stderr(void)
{
    HANDLE handle = startup_stderr != INVALID_HANDLE_VALUE ? startup_stderr : GetStdHandle(STD_ERROR_HANDLE);
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
    /* Test runs (ONI_HOST_NO_DIALOG=1): never a message box nor the user's Player.log; the line
     * goes to stderr whatever it is, and to ONI_HOST_MESSAGES' file when that names one. */
    wchar_t quiet[8], messages[MAX_PATH * 2];
    if (GetEnvironmentVariableW(L"ONI_HOST_NO_DIALOG", quiet, _countof(quiet)) && wcscmp(quiet, L"1") == 0)
    {
        HANDLE std_err = startup_stderr != INVALID_HANDLE_VALUE ? startup_stderr : GetStdHandle(STD_ERROR_HANDLE);
        if (std_err && std_err != INVALID_HANDLE_VALUE) write_utf8(std_err, line);
        if (GetEnvironmentVariableW(L"ONI_HOST_MESSAGES", messages, _countof(messages)))
        {
            HANDLE file = CreateFileW(messages, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (file != INVALID_HANDLE_VALUE)
            {
                write_utf8(file, line);
                CloseHandle(file);
            }
        }
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

/* The versioned install (docs/INSTALL-MODES.md, src/OniMoono.Host/Install): ROOT\OniMoono.exe is
 * this exe as the entry point, beside ROOT\player\VERSION\ folders; it starts the version
 * ROOT\state\current names (else the newest), with ONI_MOONO_INSTALL_ROOT set. Started from a
 * source folder (a Workshop item: moono.json and windows-x64\player beside it), it copies that
 * player to ROOT\player\.bootstrap-PID and starts the copy with ONI_MOONO_INSTALL_SOURCE set,
 * which installs the version for the game and asks for it; the source folder itself never runs.
 * The player's exit code SWITCH_EXIT, with ROOT\state\switch written, asks for the version
 * state\current names now: this starts ROOT\OniMoono.exe again with the same arguments. */
#define SWITCH_EXIT 77

static int is_dir(const wchar_t *path)
{
    DWORD a = GetFileAttributesW(path);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

static int is_file(const wchar_t *path)
{
    DWORD a = GetFileAttributesW(path);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

/* A version folder's name: letters, digits, dots and dashes, not starting with a dot. */
static int is_version_name(const wchar_t *name)
{
    if (name[0] == 0 || name[0] == L'.' || wcslen(name) > 64) return 0;
    for (const wchar_t *c = name; *c; c++)
        if (!((*c >= L'0' && *c <= L'9') || (*c >= L'a' && *c <= L'z') || (*c >= L'A' && *c <= L'Z') || *c == L'.' || *c == L'-')) return 0;
    return 1;
}

/* ROOT\state\current's version, else the newest version folder by name; 0 when none. */
static int pick_version(const wchar_t *root, wchar_t *version, size_t size)
{
    wchar_t path[MAX_PATH * 2];
    version[0] = 0;
    swprintf_s(path, _countof(path), L"%ls\\state\\current", root);
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE)
    {
        char text[80];
        DWORD read = 0;
        if (ReadFile(file, text, sizeof text - 1, &read, NULL))
        {
            text[read] = 0;
            size_t n = 0;
            for (DWORD i = 0; i < read && n + 1 < size && text[i] != '\r' && text[i] != '\n'; i++) version[n++] = (wchar_t)(unsigned char)text[i];
            version[n] = 0;
        }
        CloseHandle(file);
        swprintf_s(path, _countof(path), L"%ls\\player\\%ls\\OniMoono.dll", root, version);
        if (is_version_name(version) && is_file(path)) return 1;
        version[0] = 0;
    }
    swprintf_s(path, _countof(path), L"%ls\\player\\*", root);
    WIN32_FIND_DATAW found;
    HANDLE find = FindFirstFileW(path, &found);
    if (find == INVALID_HANDLE_VALUE) return 0;
    do
    {
        if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && is_version_name(found.cFileName) && wcscmp(found.cFileName, version) > 0)
            wcscpy_s(version, size, found.cFileName);
    } while (FindNextFileW(find, &found));
    FindClose(find);
    return version[0] != 0;
}

/* Copies the folder `from` into `to` (made), every file and folder in it; 0 on a failure. */
static int copy_tree(const wchar_t *from, const wchar_t *to)
{
    if (!CreateDirectoryW(to, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) return 0;
    wchar_t *a = (wchar_t *)HeapAlloc(GetProcessHeap(), 0, sizeof(wchar_t) * LONG_PATH * 2);
    if (a == NULL) return 0;
    wchar_t *b = a + LONG_PATH;
    swprintf_s(a, LONG_PATH, L"%ls\\*", from);
    WIN32_FIND_DATAW found;
    HANDLE find = FindFirstFileW(a, &found);
    int ok = find != INVALID_HANDLE_VALUE;
    if (ok)
    {
        do
        {
            if (wcscmp(found.cFileName, L".") == 0 || wcscmp(found.cFileName, L"..") == 0) continue;
            swprintf_s(a, LONG_PATH, L"%ls\\%ls", from, found.cFileName);
            swprintf_s(b, LONG_PATH, L"%ls\\%ls", to, found.cFileName);
            ok = (found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? copy_tree(a, b) : CopyFileW(a, b, FALSE) != 0;
        } while (ok && FindNextFileW(find, &found));
        FindClose(find);
    }
    HeapFree(GetProcessHeap(), 0, a);
    return ok;
}

/* ROOT\state\switch, deleted when there: the player asked for another version. */
static int take_switch(const wchar_t *root)
{
    wchar_t path[MAX_PATH * 2];
    swprintf_s(path, _countof(path), L"%ls\\state\\switch", root);
    if (!is_file(path)) return 0;
    DeleteFileW(path);
    return 1;
}

/* ROOT\OniMoono.exe started with this process's arguments; waited for, its exit code, when
 * `wait` (Steam or a shortcut started this, and waits on this process), else 0 at once (a start
 * from the Workshop folder: its bootstrap copy is then free to be removed). */
static int start_entry(const wchar_t *root, int wait)
{
    wchar_t count[16];
    int switches = GetEnvironmentVariableW(L"ONI_MOONO_SWITCHES", count, _countof(count)) ? _wtoi(count) : 0;
    if (switches >= 3) return fail(1, L"the installed versions keep asking for each other", root);
    swprintf_s(count, _countof(count), L"%d", switches + 1);
    SetEnvironmentVariableW(L"ONI_MOONO_SWITCHES", count);

    /* The arguments: the command line after its first word, the executable, as Windows
     * splits it (quoted, or up to the first space or tab). */
    const wchar_t *rest = GetCommandLineW();
    if (*rest == L'"') { rest++; while (*rest && *rest != L'"') rest++; if (*rest) rest++; }
    else while (*rest && *rest != L' ' && *rest != L'\t') rest++;
    size_t length = wcslen(root) + wcslen(rest) + 32;
    wchar_t *line = (wchar_t *)HeapAlloc(GetProcessHeap(), 0, sizeof(wchar_t) * length);
    if (line == NULL) return fail(1, L"out of memory", NULL);
    swprintf_s(line, length, L"\"%ls\\OniMoono.exe\"%ls", root, rest);
    /* It inherits only the stdout and stderr this process was started with, when they are a
     * pipe or a file (scripts, test runs): stderr is the crash watcher's pipe by now, which must
     * see this process's end, not the child's. */
    HANDLE handles[2];
    int n = 0;
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE), err = redirected_stderr();
    HANDLE out_copy = NULL, err_copy = NULL;
    if (out && out != INVALID_HANDLE_VALUE && (GetFileType(out) == FILE_TYPE_PIPE || GetFileType(out) == FILE_TYPE_DISK)
        && DuplicateHandle(GetCurrentProcess(), out, GetCurrentProcess(), &out_copy, 0, TRUE, DUPLICATE_SAME_ACCESS)) handles[n++] = out_copy;
    if (err && DuplicateHandle(GetCurrentProcess(), err, GetCurrentProcess(), &err_copy, 0, TRUE, DUPLICATE_SAME_ACCESS)) handles[n++] = err_copy;
    static BYTE attributes[256];
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &size);
    STARTUPINFOEXW startup = { 0 };
    startup.StartupInfo.cb = sizeof startup;
    BOOL listed = FALSE;
    if (n > 0 && size <= sizeof attributes)
    {
        startup.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)attributes;
        listed = InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &size)
            && UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, n * sizeof(HANDLE), NULL, NULL);
        if (listed)
        {
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdOutput = out_copy;
            startup.StartupInfo.hStdError = err_copy;
        }
    }
    PROCESS_INFORMATION process;
    BOOL started = CreateProcessW(NULL, line, NULL, NULL, listed, listed ? EXTENDED_STARTUPINFO_PRESENT : 0, NULL, NULL, &startup.StartupInfo, &process);
    DWORD error = GetLastError();
    if (listed) DeleteProcThreadAttributeList(startup.lpAttributeList);
    if (out_copy) CloseHandle(out_copy);
    if (err_copy) CloseHandle(err_copy);
    if (!started) return fail((int)error, L"the installed entry point could not be started", line);
    DWORD code = 0;
    if (wait)
    {
        WaitForSingleObject(process.hProcess, INFINITE);
        code = 1;
        GetExitCodeProcess(process.hProcess, &code);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    HeapFree(GetProcessHeap(), 0, line);
    return (int)code;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc >= 2 && wcscmp(argv[1], L"--crash-watch") == 0) return crash_watch(argc, argv);
    if (argc >= 2 && wcscmp(argv[1], L"--crash-dialog-render") == 0) return crash_dialog_render(argc, argv);
    static wchar_t exe[LONG_PATH], dir[LONG_PATH], app[LONG_PATH], fxr[LONG_PATH];
    DWORD length = GetModuleFileNameW(NULL, exe, LONG_PATH);
    if (length == 0 || length >= LONG_PATH) return fail((int)GetLastError(), L"the executable's own path is unknown", NULL);
    startup_stderr = crash_start(exe);
    wcscpy_s(dir, LONG_PATH, exe);
    wchar_t *slash = wcsrchr(dir, L'\\');
    if (slash == NULL) return fail(1, L"the executable's own path is unknown", exe);
    *slash = 0;
    swprintf_s(app, LONG_PATH, L"%ls\\%ls", dir, APP_DLL);
    /* The versioned install's entry point, or a start from a source folder (see SWITCH_EXIT). */
    static wchar_t root[LONG_PATH];
    int versioned = 0, bootstrap = 0;
    if (!is_file(app))
    {
        static wchar_t probe[LONG_PATH], source[LONG_PATH];
        swprintf_s(probe, LONG_PATH, L"%ls\\player", dir);
        if (is_dir(probe))
        {
            wcscpy_s(root, LONG_PATH, dir);
            swprintf_s(probe, LONG_PATH, L"%ls\\OniMoono.exe.old", root);
            DeleteFileW(probe);
            wchar_t version[80];
            if (!pick_version(root, version, _countof(version))) return fail(1, L"no version of Moono is installed here; start it once from its Workshop folder", root);
            swprintf_s(dir, LONG_PATH, L"%ls\\player\\%ls", root, version);
            /* Its source folder is state\\source from now on, not a bootstrap's. */
            SetEnvironmentVariableW(L"ONI_MOONO_INSTALL_SOURCE", NULL);
            versioned = 1;
        }
        else
        {
            swprintf_s(probe, LONG_PATH, L"%ls\\windows-x64\\player\\%ls", dir, APP_DLL);
            swprintf_s(source, LONG_PATH, L"%ls\\moono.json", dir);
            if (is_file(probe) && is_file(source))
            {
                wcscpy_s(source, LONG_PATH, dir);
                if (!GetEnvironmentVariableW(L"ONI_MOONO_INSTALL_ROOT", root, LONG_PATH))
                {
                    PWSTR local = NULL;
                    if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData, 0, NULL, &local))) return fail(1, L"the local application data folder is unknown", NULL);
                    swprintf_s(root, LONG_PATH, L"%ls\\oni-moono\\versioned", local);
                    CoTaskMemFree(local);
                }
                SHCreateDirectoryExW(NULL, root, NULL);
                swprintf_s(probe, LONG_PATH, L"%ls\\player", root);
                CreateDirectoryW(probe, NULL);
                swprintf_s(dir, LONG_PATH, L"%ls\\player\\.bootstrap-%lu", root, GetCurrentProcessId());
                swprintf_s(probe, LONG_PATH, L"%ls\\windows-x64\\player", source);
                if (!copy_tree(probe, dir)) return fail((int)GetLastError(), L"the player could not be copied from its Workshop folder", probe);
                SetEnvironmentVariableW(L"ONI_MOONO_INSTALL_SOURCE", source);
                versioned = bootstrap = 1;
            }
        }
        if (versioned)
        {
            SetEnvironmentVariableW(L"ONI_MOONO_INSTALL_ROOT", root);
            swprintf_s(app, LONG_PATH, L"%ls\\%ls", dir, APP_DLL);
        }
    }
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
    crash_exiting((DWORD)rc);
    close(context);
    if (versioned && rc == SWITCH_EXIT && take_switch(root)) return start_entry(root, !bootstrap);
    if (((unsigned)rc & 0xFFFFFF00u) == 0x80008000u && hostfxr_messages[0]) return fail(rc, L"the .NET runtime could not start", app);
    return rc;
}
