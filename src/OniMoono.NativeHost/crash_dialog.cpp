/* The crash window of the Windows player (ARCHITECTURE §7, "Native crashes", "The crash window").
 *
 * The watcher (crash.c) shows it after it has written a crash record to Player.log: the headline,
 * the error line from the record, details on request, a box for what the player was doing, the
 * dump to include or not, and "Report on GitHub", which packs Player.log (and the dump) into a zip
 * beside them, opens a new issue in the browser with the error and the details filled in, and then
 * shows the zip so it can be dragged into the page. Nothing is sent by the window itself.
 *
 * It must work when the graphics driver is what crashed, so nothing here uses the GPU: Direct2D
 * draws into a WIC bitmap with a software render target, DirectWrite sets the text (system font
 * fallback for what our fonts lack: CJK, the check mark), and the bitmap is copied to the window.
 * The same drawing writes the PNG of `--crash-dialog-render`, with a drawn title bar. The fonts
 * are the onimaxxing site's (Gabarito, Red Hat Mono: SIL OFL 1.1, NOTICE.txt), embedded as
 * resources (static weights cut from the site's subset files) and loaded from memory.
 *
 * The interactive parts are standard controls, so the keyboard, focus and screen readers work as
 * they do in any dialog: buttons, a check box and an edit box (comctl32 v6, through an activation
 * context of our own: the game's process manifest stays as it is). Their look is ours: each button
 * and static control paints by copying its rectangle from the drawn bitmap (NM_CUSTOMDRAW,
 * WM_DRAWITEM), and the drawing reads each control's state (hot, pressed, focus, checked).
 *
 * Sizes are in DIPs (96 per inch) times the monitor's scale, the thread per-monitor DPI aware (v2;
 * only the watcher's thread: the game sets its own awareness). Lines are whole pixels. */

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <windowsx.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <dwmapi.h>
#include <d2d1.h>
#include <d2d1helper.h>
#include <dwrite_3.h>
#include <wincodec.h>
#include <dxgi.h>
#include <oleacc.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <math.h>
#include <initializer_list>
#include "crash_dialog.h"

namespace {

template <class T> void release(T *&p)
{
    if (p) { p->Release(); p = nullptr; }
}

float maxf(float a, float b) { return a > b ? a : b; }

/* ---- strings -------------------------------------------------------------------------------- */

enum
{
    S_HEADLINE, S_SHOW_DETAILS, S_HIDE_DETAILS, S_NOTE, S_INCLUDE_DUMP, S_CLOSE, S_REPORT, S_REPORTED,
    S_DRAG, S_EXIT_CODE, S_GAME_BUILD, S_PLAYED, S_MODS, S_FILES, S_LIST, S_HOURS, S_MINUTES, S_SECONDS,
    S_ATTACH, S_COUNT
};

/* Every string the window shows, in English and Simplified Chinese (the game's language). */
const wchar_t *const strings[2][S_COUNT] = {
    {
        L"The game closed unexpectedly", L"Show details", L"Hide details", L"What were you doing? (optional)",
        L"Include the crash dump (%ls)", L"Close", L"Report on GitHub", L"Opened in browser \u2713",
        L"Drag this into the GitHub page", L"Exit code %lu", L", game build %ls", L"Played %ls", L"%ld mods",
        L"Files: %ls", L", ", L"%llu h %llu min", L"%llu min", L"%llu s",
        L"Please attach %ls (drag it from the crash window)",
    },
    {
        L"游戏意外关闭", L"显示详细信息", L"隐藏详细信息", L"你当时在做什么？（可选）",
        L"附上崩溃转储（%ls）", L"关闭", L"在 GitHub 上报告", L"已在浏览器中打开 \u2713",
        L"将此文件拖到 GitHub 页面中", L"退出代码 %lu", L"，游戏版本 %ls", L"游戏时长 %ls", L"%ld 个模组",
        L"文件：%ls", L"、", L"%llu 小时 %llu 分钟", L"%llu 分钟", L"%llu 秒",
        L"请附上 %ls（从崩溃窗口拖入）",
    },
};

const wchar_t *const kTitle = L"OniMoono";
const wchar_t *const kIssueUrl = L"https://github.com/tigin-backwards/oni-moono/issues/new";

/* ---- what Windows has only in newer versions, looked up ------------------------------------- */

typedef DPI_AWARENESS_CONTEXT (WINAPI *set_thread_dpi_fn)(DPI_AWARENESS_CONTEXT);
typedef UINT (WINAPI *dpi_for_window_fn)(HWND);
typedef BOOL (WINAPI *adjust_for_dpi_fn)(LPRECT, DWORD, BOOL, DWORD, UINT);
typedef int (WINAPI *metrics_for_dpi_fn)(int, UINT);
typedef HRESULT (WINAPI *dwm_set_fn)(HWND, DWORD, LPCVOID, DWORD);
typedef HRESULT (WINAPI *d2d_create_fn)(D2D1_FACTORY_TYPE, REFIID, const D2D1_FACTORY_OPTIONS *, void **);
typedef HRESULT (WINAPI *dwrite_create_fn)(DWRITE_FACTORY_TYPE, REFIID, IUnknown **);
typedef HRESULT (WINAPI *dxgi_create_fn)(REFIID, void **);
typedef BOOL (WINAPI *init_controls_fn)(const INITCOMMONCONTROLSEX *);

struct Api
{
    set_thread_dpi_fn set_thread_dpi;
    dpi_for_window_fn dpi_for_window;
    adjust_for_dpi_fn adjust_for_dpi;
    metrics_for_dpi_fn metrics_for_dpi;
    dwm_set_fn dwm_set;
};
Api api;

/* System libraries load from System32 only. */
HMODULE system_library(const wchar_t *name)
{
    return LoadLibraryExW(name, NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
}

void load_api()
{
    HMODULE user = GetModuleHandleW(L"user32.dll");
    api.set_thread_dpi = (set_thread_dpi_fn)GetProcAddress(user, "SetThreadDpiAwarenessContext");
    api.dpi_for_window = (dpi_for_window_fn)GetProcAddress(user, "GetDpiForWindow");
    api.adjust_for_dpi = (adjust_for_dpi_fn)GetProcAddress(user, "AdjustWindowRectExForDpi");
    api.metrics_for_dpi = (metrics_for_dpi_fn)GetProcAddress(user, "GetSystemMetricsForDpi");
    HMODULE dwm = system_library(L"dwmapi.dll");
    api.dwm_set = dwm ? (dwm_set_fn)GetProcAddress(dwm, "DwmSetWindowAttribute") : nullptr;
}

/* ---- colours -------------------------------------------------------------------------------- */

D2D1_COLOR_F rgb(UINT32 v, float a = 1.0f)
{
    return D2D1::ColorF(((v >> 16) & 255) / 255.0f, ((v >> 8) & 255) / 255.0f, (v & 255) / 255.0f, a);
}

D2D1_COLOR_F from_colorref(COLORREF c)
{
    return D2D1::ColorF(GetRValue(c) / 255.0f, GetGValue(c) / 255.0f, GetBValue(c) / 255.0f, 1.0f);
}

COLORREF to_colorref(D2D1_COLOR_F c)
{
    return RGB((BYTE)lroundf(c.r * 255), (BYTE)lroundf(c.g * 255), (BYTE)lroundf(c.b * 255));
}

/* `a` over `b` at `t` (0: b, 1: a): disabled controls at half opacity, as the site's. */
D2D1_COLOR_F mix(D2D1_COLOR_F a, D2D1_COLOR_F b, float t)
{
    return D2D1::ColorF(a.r * t + b.r * (1 - t), a.g * t + b.g * (1 - t), a.b * t + b.b * (1 - t), 1.0f);
}

/* The onimaxxing site's tokens (apps/onimaxxing/src/index.css, oklch converted to sRGB) as the
 * approved mockup uses them; in high-contrast mode the system's colours. */
struct Palette
{
    bool dark, contrast;
    D2D1_COLOR_F bg, caption, fg, fg2, mut, panel, border, footer, pri, prifg, secbd, secfg, hover, ring;
};

Palette palette_for(bool dark, bool contrast)
{
    Palette p = {};
    p.dark = dark;
    p.contrast = contrast;
    if (contrast)
    {
        D2D1_COLOR_F window = from_colorref(GetSysColor(COLOR_WINDOW)), text = from_colorref(GetSysColor(COLOR_WINDOWTEXT));
        p.bg = p.caption = p.panel = p.footer = window;
        p.fg = p.fg2 = p.mut = p.border = p.secbd = p.secfg = text;
        p.pri = from_colorref(GetSysColor(COLOR_HIGHLIGHT));
        p.prifg = from_colorref(GetSysColor(COLOR_HIGHLIGHTTEXT));
        p.hover = from_colorref(GetSysColor(COLOR_HOTLIGHT));
        p.ring = p.pri;
        /* "dark" for the caption: a dark window colour */
        p.dark = (p.bg.r + p.bg.g + p.bg.b) < 1.5f;
        return p;
    }
    if (dark)
    {
        p.bg = rgb(0x191512);       /* card: oklch(0.2 0.008 60) */
        p.caption = rgb(0x100C0A);  /* background: oklch(0.16 0.008 60) */
        p.fg = rgb(0xE9E8E4);       /* oklch(0.93 0.005 90) */
        p.fg2 = rgb(0xC1BDB8);      /* oklch(0.8 0.008 75) */
        p.mut = rgb(0x948E89);      /* muted-foreground: oklch(0.65 0.01 60) */
        p.panel = rgb(0x120F0C);    /* oklch(0.17 0.008 60) */
        p.border = rgb(0xFFFFFF, 0.10f);
        p.footer = rgb(0x1D1A17);   /* oklch(0.22 0.008 60) */
        p.pri = p.fg;
        p.prifg = p.caption;
        p.secbd = rgb(0xFFFFFF, 0.14f);
        p.secfg = rgb(0xADA6A2);    /* oklch(0.73 0.01 60): a step above muted, for Close */
        p.hover = rgb(0x272320);    /* accent: oklch(0.26 0.008 60) */
        p.ring = rgb(0x68625E);     /* ring: oklch(0.5 0.01 60) */
    }
    else
    {
        p.bg = rgb(0xFFFCF6);       /* card: oklch(0.992 0.008 85) */
        p.caption = rgb(0xF7F4ED);  /* background: oklch(0.968 0.01 85) */
        p.fg = rgb(0x15110D);       /* oklch(0.18 0.01 60) */
        p.fg2 = rgb(0x413C38);      /* oklch(0.36 0.01 60) */
        p.mut = rgb(0x68625E);      /* oklch(0.5 0.01 60) */
        p.panel = rgb(0xF7F4ED);
        p.border = rgb(0xDDD9CF);   /* oklch(0.885 0.014 85) */
        p.footer = rgb(0xF7F4ED);
        p.pri = p.fg;
        p.prifg = rgb(0xFAFAFA);    /* oklch(0.985 0 0) */
        p.secbd = p.border;
        p.secfg = rgb(0x514C48);    /* oklch(0.42 0.01 60): a step above muted, for Close */
        p.hover = rgb(0xEBE7DE);    /* accent: oklch(0.928 0.012 85) */
        p.ring = rgb(0x948E89);     /* ring: oklch(0.65 0.01 60) */
    }
    return p;
}

/* Windows' app mode (Settings > Personalization > Colors), light unless it says dark. */
bool system_dark()
{
    DWORD value = 1, size = sizeof value;
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                     L"AppsUseLightTheme", RRF_RT_REG_DWORD, NULL, &value, &size) != ERROR_SUCCESS) return false;
    return value == 0;
}

bool system_contrast()
{
    HIGHCONTRASTW hc = { sizeof hc };
    return SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof hc, &hc, 0) && (hc.dwFlags & HCF_HIGHCONTRASTON);
}

/* ---- the GitHub mark (Octicons mark-github, MIT) -------------------------------------------- */

const char kGithubMark[] =
    "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94"
    "-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07"
    "-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0"
    " 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25"
    ".54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z";

/* An SVG path (the commands the mark uses: M m C c L l H h V v A a Z z) as a Direct2D geometry. */
struct PathReader
{
    const char *at;
    void skip() { while (*at == ' ' || *at == ',' || *at == '\n') at++; }
    bool number_next()
    {
        skip();
        return (*at >= '0' && *at <= '9') || *at == '-' || *at == '.' || *at == '+';
    }
    float number()
    {
        skip();
        const char *start = at;
        if (*at == '-' || *at == '+') at++;
        bool dot = false;
        while ((*at >= '0' && *at <= '9') || (*at == '.' && !dot)) { if (*at == '.') dot = true; at++; }
        if (*at == 'e' || *at == 'E') { at++; if (*at == '-' || *at == '+') at++; while (*at >= '0' && *at <= '9') at++; }
        return (float)strtod(start, NULL);
    }
    int flag()
    {
        skip();
        return *at ? *at++ - '0' : 0;
    }
};

ID2D1PathGeometry *svg_path(ID2D1Factory *factory, const char *text)
{
    ID2D1PathGeometry *path = nullptr;
    ID2D1GeometrySink *sink = nullptr;
    if (FAILED(factory->CreatePathGeometry(&path)) || FAILED(path->Open(&sink))) { release(path); return nullptr; }
    sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    PathReader r = { text };
    D2D1_POINT_2F pen = { 0, 0 }, start = { 0, 0 };
    bool open = false;
    char command = 0;
    for (;;)
    {
        r.skip();
        if (!*r.at) break;
        if ((*r.at >= 'A' && *r.at <= 'Z') || (*r.at >= 'a' && *r.at <= 'z')) command = *r.at++;
        bool relative = command >= 'a';
        float ox = relative ? pen.x : 0, oy = relative ? pen.y : 0;
        switch (command | 0x20)
        {
        case 'm':
        {
            if (open) sink->EndFigure(D2D1_FIGURE_END_OPEN);
            float x = r.number() + ox, y = r.number() + oy;
            pen = start = D2D1::Point2F(x, y);
            sink->BeginFigure(pen, D2D1_FIGURE_BEGIN_FILLED);
            open = true;
            command = relative ? 'l' : 'L';   /* further pairs are lines */
            break;
        }
        case 'l': { float x = r.number() + ox, y = r.number() + oy; pen = D2D1::Point2F(x, y); sink->AddLine(pen); break; }
        case 'h': { pen.x = r.number() + ox; sink->AddLine(pen); break; }
        case 'v': { pen.y = r.number() + oy; sink->AddLine(pen); break; }
        case 'c':
        {
            D2D1_BEZIER_SEGMENT b;
            b.point1.x = r.number() + ox; b.point1.y = r.number() + oy;
            b.point2.x = r.number() + ox; b.point2.y = r.number() + oy;
            b.point3.x = r.number() + ox; b.point3.y = r.number() + oy;
            sink->AddBezier(b);
            pen = b.point3;
            break;
        }
        case 'a':
        {
            D2D1_ARC_SEGMENT a;
            a.size.width = r.number(); a.size.height = r.number();
            a.rotationAngle = r.number();
            a.arcSize = r.flag() ? D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE_SMALL;
            a.sweepDirection = r.flag() ? D2D1_SWEEP_DIRECTION_CLOCKWISE : D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE;
            a.point.x = r.number() + ox; a.point.y = r.number() + oy;
            sink->AddArc(a);
            pen = a.point;
            break;
        }
        case 'z':
            if (open) sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            open = false;
            pen = start;
            break;
        default:
            r.at++;
            break;
        }
        if ((command | 0x20) != 'z' && !r.number_next() && *r.at && !((*r.at >= 'A' && *r.at <= 'Z') || (*r.at >= 'a' && *r.at <= 'z'))) r.at++;
    }
    if (open) sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    sink->Close();
    sink->Release();
    return path;
}

/* ---- fonts ---------------------------------------------------------------------------------- */

/* The embedded fonts' resource numbers (host.rc). */
enum { FONT_REGULAR = 101, FONT_MEDIUM = 102, FONT_SEMIBOLD = 103, FONT_MONO = 104 };

bool resource_data(int id, const void **data, DWORD *size)
{
    HMODULE self = GetModuleHandleW(NULL);
    HRSRC found = FindResourceW(self, MAKEINTRESOURCEW(id), MAKEINTRESOURCEW(10) /* RT_RCDATA */);
    HGLOBAL loaded = found ? LoadResource(self, found) : NULL;
    *data = loaded ? LockResource(loaded) : NULL;
    *size = found ? SizeofResource(self, found) : 0;
    return *data && *size;
}

/* ---- the report's text ---------------------------------------------------------------------- */

void format_size(ULONG64 bytes, wchar_t *out, size_t most)
{
    if (bytes < 1000 * 1024) swprintf_s(out, most, L"%llu KB", (bytes + 1023) / 1024);
    else if (bytes < 1000ull * 1024 * 1024) swprintf_s(out, most, L"%.1f MB", bytes / (1024.0 * 1024.0));
    else swprintf_s(out, most, L"%.1f GB", bytes / (1024.0 * 1024.0 * 1024.0));
}

const wchar_t *file_name(const wchar_t *path)
{
    const wchar_t *a = wcsrchr(path, L'\\'), *b = wcsrchr(path, L'/');
    if (b > a) a = b;
    return a ? a + 1 : path;
}

/* Facts of this machine the watcher finds itself: Windows, the GPU's driver, the memory. */
struct System
{
    wchar_t windows[96];
    wchar_t gpu[192];
    ULONG64 ram_gb;
};

void windows_version(wchar_t *out, size_t most)
{
    typedef LONG (WINAPI *rtl_version_fn)(RTL_OSVERSIONINFOW *);
    RTL_OSVERSIONINFOW v = { sizeof v };
    rtl_version_fn version = (rtl_version_fn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion");
    if (!version || version(&v) != 0) { wcscpy_s(out, most, L"Windows"); return; }
    wchar_t display[32] = L"";
    DWORD size = sizeof display, ubr = 0, ubr_size = sizeof ubr;
    const wchar_t *key = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"DisplayVersion", RRF_RT_REG_SZ, NULL, display, &size) != ERROR_SUCCESS)
    {
        size = sizeof display;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"ReleaseId", RRF_RT_REG_SZ, NULL, display, &size) != ERROR_SUCCESS) display[0] = 0;
    }
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"UBR", RRF_RT_REG_DWORD, NULL, &ubr, &ubr_size) != ERROR_SUCCESS) ubr = 0;
    unsigned major = v.dwMajorVersion == 10 && v.dwBuildNumber >= 22000 ? 11 : v.dwMajorVersion;
    swprintf_s(out, most, L"Windows %u%ls%ls (%lu.%lu)", major, display[0] ? L" " : L"", display, v.dwBuildNumber, ubr);
}

/* The GPU's name and driver version as DXGI has them: the adapter whose name the game's renderer
 * string starts with, else the first one with hardware. NVIDIA's version as NVIDIA numbers it. */
void gpu_line(const wchar_t *renderer, wchar_t *out, size_t most)
{
    wchar_t wanted[128];
    wcsncpy_s(wanted, renderer, _TRUNCATE);
    wchar_t *slash = wcschr(wanted, L'/');
    if (slash) *slash = 0;
    out[0] = 0;
    HMODULE dxgi = system_library(L"dxgi.dll");
    dxgi_create_fn create = dxgi ? (dxgi_create_fn)GetProcAddress(dxgi, "CreateDXGIFactory1") : nullptr;
    IDXGIFactory1 *factory = nullptr;
    if (create && SUCCEEDED(create(__uuidof(IDXGIFactory1), (void **)&factory)))
    {
        IDXGIAdapter1 *adapter = nullptr, *chosen = nullptr;
        DXGI_ADAPTER_DESC1 chosen_desc = {};
        for (UINT i = 0; SUCCEEDED(factory->EnumAdapters1(i, &adapter)); i++)
        {
            DXGI_ADAPTER_DESC1 desc;
            if (SUCCEEDED(adapter->GetDesc1(&desc)) && !(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && desc.VendorId != 0x1414)
            {
                bool matches = wanted[0] && _wcsnicmp(desc.Description, wanted, wcslen(desc.Description)) == 0;
                if (!chosen || matches)
                {
                    release(chosen);
                    chosen = adapter;
                    chosen->AddRef();
                    chosen_desc = desc;
                    if (matches) { adapter->Release(); break; }
                }
            }
            adapter->Release();
        }
        if (chosen)
        {
            LARGE_INTEGER umd = {};
            wchar_t version[48] = L"";
            if (SUCCEEDED(chosen->CheckInterfaceSupport(__uuidof(IDXGIDevice), &umd)))
            {
                unsigned a = HIWORD(umd.HighPart), b = LOWORD(umd.HighPart), c = HIWORD(umd.LowPart), d = LOWORD(umd.LowPart);
                if (chosen_desc.VendorId == 0x10DE)
                {
                    unsigned n = (c % 10) * 10000 + d;
                    swprintf_s(version, L"%u.%02u", n / 100, n % 100);
                }
                else swprintf_s(version, L"%u.%u.%u.%u", a, b, c, d);
            }
            swprintf_s(out, most, version[0] ? L"%ls (%ls)" : L"%ls", chosen_desc.Description, version);
            chosen->Release();
        }
        factory->Release();
    }
    if (!out[0] && wanted[0]) wcsncpy_s(out, most, wanted, _TRUNCATE);
}

void gather_system(const crash_report_t *r, System *s)
{
    windows_version(s->windows, _countof(s->windows));
    __try
    {
        gpu_line(r->gpu, s->gpu, _countof(s->gpu));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        wcsncpy_s(s->gpu, r->gpu, _TRUNCATE);
    }
    ULONGLONG kb = 0;
    s->ram_gb = GetPhysicallyInstalledSystemMemory(&kb) ? (kb + 512 * 1024) / (1024 * 1024) : 0;
}

void append(wchar_t *out, size_t most, const wchar_t *text)
{
    wcsncat_s(out, most, text, _TRUNCATE);
}

/* The details, one fact a line, in `language`. */
void details_text(const crash_report_t *r, const System *sys, int language, wchar_t *out, size_t most)
{
    const wchar_t *const *t = strings[language];
    wchar_t part[512];
    out[0] = 0;
    swprintf_s(part, L"OniMoono %ls", r->version[0] ? r->version : L"?");
    append(out, most, part);
    if (r->game_build[0]) { swprintf_s(part, t[S_GAME_BUILD], r->game_build); append(out, most, part); }
    append(out, most, L"\n");
    append(out, most, sys->windows);
    if (sys->gpu[0]) { append(out, most, L" \u00B7 "); append(out, most, sys->gpu); }
    if (sys->ram_gb) { swprintf_s(part, L" \u00B7 %llu GB", sys->ram_gb); append(out, most, part); }
    bool line = false;
    if (r->play_seconds != ~0ull)
    {
        wchar_t played[64];
        ULONG64 s = r->play_seconds;
        if (s >= 3600) swprintf_s(played, t[S_HOURS], s / 3600, s / 60 % 60);
        else if (s >= 60) swprintf_s(played, t[S_MINUTES], s / 60);
        else swprintf_s(played, t[S_SECONDS], s);
        swprintf_s(part, t[S_PLAYED], played);
        append(out, most, L"\n");
        append(out, most, part);
        line = true;
    }
    if (r->mods >= 0)
    {
        swprintf_s(part, t[S_MODS], r->mods);
        append(out, most, line ? L" \u00B7 " : L"\n");
        append(out, most, part);
    }
    wchar_t files[300];
    wcscpy_s(files, file_name(r->log_path));
    if (r->dump_path[0]) { wcscat_s(files, t[S_LIST]); wcscat_s(files, file_name(r->dump_path)); }
    swprintf_s(part, t[S_FILES], files);
    append(out, most, L"\n");
    append(out, most, part);
}

/* ---- the window's state --------------------------------------------------------------------- */

enum { C_HEAD, C_CAUSE, C_TOGGLE, C_DETAILS, C_NOTE, C_CHECK, C_CHIP, C_CLOSE, C_REPORT, C_COUNT };
enum { ID_TOGGLE = 101, ID_NOTE, ID_CHECK, ID_CHIP, ID_CLOSE, ID_REPORT, ID_HEAD, ID_CAUSE, ID_DETAILS };

/* What the drawing depends on besides the layout: each control's state. */
struct Look
{
    bool hot[C_COUNT], pressed[C_COUNT], focus[C_COUNT];
    bool cues, checked, note_empty;
};

/* Pixel rectangles, from DIPs. */
struct Box
{
    float l, t, r, b;
    float w() const { return r - l; }
    float h() const { return b - t; }
    D2D1_RECT_F f() const { return D2D1::RectF(l, t, r, b); }
    RECT rect() const { RECT x = { (LONG)l, (LONG)t, (LONG)r, (LONG)b }; return x; }
};

struct Layout
{
    int width, height;            /* the client area, px */
    float line;                   /* a 1-DIP line in whole pixels */
    Box head, cause, toggle, details, note, check, chip, footer, close, report;
    IDWriteTextLayout *t_head, *t_cause, *t_toggle, *t_details, *t_note, *t_check, *t_chip_name, *t_chip_hint, *t_zip,
        *t_close, *t_report, *t_caption;
};

struct Dialog
{
    crash_report_t report;
    System system;
    const wchar_t *const *t;      /* strings[language] */
    wchar_t error[1024], details[2048];
    float scale;
    Palette pal;
    bool details_open, reported, include_dump, can_include_dump, render_only;
    bool forced_theme, forced_dark;
    wchar_t chip_path[1024];
    ULONG64 chip_size;

    ID2D1Factory *d2d;
    IDWriteFactory *dw;
    IWICImagingFactory *wic;
    IDWriteFontCollection *fonts;   /* ours, or null (system fonts) */
    float sans_ascent, sans_descent, mono_ascent, mono_descent;   /* per em */
    IDWriteTextFormat *f_head, *f_body, *f_small, *f_mono, *f_button, *f_chip, *f_zip, *f_caption;
    ID2D1PathGeometry *mark;
    ID2D1StrokeStyle *dashed;
    Layout lay;

    IWICBitmap *bitmap;
    ID2D1RenderTarget *rt;
    ID2D1SolidColorBrush *brush;
    int bitmap_w, bitmap_h;

    HWND hwnd, child[C_COUNT];
    WNDPROC note_proc, chip_proc;
    HDC memdc;
    HBITMAP dib, old_dib;
    void *dib_bits;
    Look drawn;
    bool drawn_valid;
    HFONT note_font;
    HBRUSH panel_brush;
    HANDLE gdi_font;
    HWND restore_focus;
    IAccPropServices *acc;
};

Dialog g;

float dip(float v) { return v * g.scale; }
float snap(float v) { return floorf(v + 0.5f); }

/* ---- text ----------------------------------------------------------------------------------- */

void font_metrics(const wchar_t *family, float *ascent, float *descent)
{
    *ascent = 0.9f;
    *descent = 0.25f;
    IDWriteFontCollection *collection = g.fonts;
    if (!collection && FAILED(g.dw->GetSystemFontCollection(&collection))) return;
    UINT32 index;
    BOOL exists = FALSE;
    IDWriteFontFamily *fam = nullptr;
    IDWriteFont *font = nullptr;
    if (SUCCEEDED(collection->FindFamilyName(family, &index, &exists)) && exists && SUCCEEDED(collection->GetFontFamily(index, &fam))
        && SUCCEEDED(fam->GetFirstMatchingFont(DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, &font)))
    {
        DWRITE_FONT_METRICS m;
        font->GetMetrics(&m);
        *ascent = (float)m.ascent / m.designUnitsPerEm;
        *descent = (float)m.descent / m.designUnitsPerEm;
    }
    release(font);
    release(fam);
    if (!g.fonts) collection->Release();
}

/* Our fonts, from the resources, as a DirectWrite collection (Windows 10 1703's in-memory
 * loader); null on older Windows, which then uses Segoe UI and Consolas. */
IDWriteFontCollection *load_fonts()
{
    IDWriteFactory5 *f5 = nullptr;
    if (FAILED(g.dw->QueryInterface(__uuidof(IDWriteFactory5), (void **)&f5))) return nullptr;
    IDWriteInMemoryFontFileLoader *loader = nullptr;
    IDWriteFontSetBuilder1 *builder = nullptr;
    IDWriteFontSet *set = nullptr;
    IDWriteFontCollection1 *collection = nullptr;
    if (SUCCEEDED(f5->CreateInMemoryFontFileLoader(&loader)) && SUCCEEDED(f5->RegisterFontFileLoader(loader))
        && SUCCEEDED(f5->CreateFontSetBuilder(&builder)))
    {
        const int ids[] = { FONT_REGULAR, FONT_MEDIUM, FONT_SEMIBOLD, FONT_MONO };
        for (int id : ids)
        {
            const void *data;
            DWORD size;
            IDWriteFontFile *file = nullptr;
            if (resource_data(id, &data, &size) && SUCCEEDED(loader->CreateInMemoryFontFileReference(f5, data, size, NULL, &file)))
            {
                builder->AddFontFile(file);
                file->Release();
            }
        }
        if (SUCCEEDED(builder->CreateFontSet(&set))) f5->CreateFontCollectionFromFontSet(set, &collection);
    }
    release(set);
    release(builder);
    release(loader);
    release(f5);
    return collection;
}

const wchar_t *sans() { return g.fonts ? L"Gabarito" : L"Segoe UI"; }
const wchar_t *mono() { return g.fonts ? L"Red Hat Mono" : L"Consolas"; }

IDWriteTextFormat *text_format(const wchar_t *family, IDWriteFontCollection *collection, DWRITE_FONT_WEIGHT weight, float size)
{
    IDWriteTextFormat *format = nullptr;
    g.dw->CreateTextFormat(family, collection, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size,
                           g.report.language == CRASH_CHINESE ? L"zh-cn" : L"en-us", &format);
    if (format) format->SetWordWrapping(DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
    return format;
}

void release_formats()
{
    release(g.f_head); release(g.f_body); release(g.f_small); release(g.f_mono);
    release(g.f_button); release(g.f_chip); release(g.f_zip); release(g.f_caption);
}

void make_formats()
{
    release_formats();
    g.f_head = text_format(sans(), g.fonts, DWRITE_FONT_WEIGHT_SEMI_BOLD, dip(16));
    g.f_body = text_format(sans(), g.fonts, DWRITE_FONT_WEIGHT_NORMAL, dip(13));
    g.f_small = text_format(sans(), g.fonts, DWRITE_FONT_WEIGHT_NORMAL, dip(12));
    g.f_mono = text_format(mono(), g.fonts, DWRITE_FONT_WEIGHT_NORMAL, dip(12));
    g.f_button = text_format(sans(), g.fonts, DWRITE_FONT_WEIGHT_MEDIUM, dip(12.5f));
    g.f_chip = text_format(sans(), g.fonts, DWRITE_FONT_WEIGHT_SEMI_BOLD, dip(12.5f));
    g.f_zip = text_format(sans(), g.fonts, DWRITE_FONT_WEIGHT_SEMI_BOLD, dip(9.5f));
    g.f_caption = text_format(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, dip(12));
}

/* A text laid out as CSS lays out `line-height`: lines `line_height` px apart, the font's ascent
 * and descent centred in each. */
IDWriteTextLayout *make_text(const wchar_t *text, IDWriteTextFormat *format, float width, float line_height, bool is_mono = false,
                             bool wrap = true)
{
    IDWriteTextLayout *layout = nullptr;
    if (!format || FAILED(g.dw->CreateTextLayout(text, (UINT32)wcslen(text), format, maxf(width, 1.0f), 100000.0f, &layout))) return nullptr;
    float size = format->GetFontSize();
    float ascent = (is_mono ? g.mono_ascent : g.sans_ascent) * size, descent = (is_mono ? g.mono_descent : g.sans_descent) * size;
    layout->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, line_height, (line_height - ascent - descent) / 2 + ascent);
    if (!wrap) layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    return layout;
}

DWRITE_TEXT_METRICS metrics(IDWriteTextLayout *layout)
{
    DWRITE_TEXT_METRICS m = {};
    if (layout) layout->GetMetrics(&m);
    return m;
}

/* ---- layout --------------------------------------------------------------------------------- */

void release_layout()
{
    Layout &l = g.lay;
    release(l.t_head); release(l.t_cause); release(l.t_toggle); release(l.t_details); release(l.t_note); release(l.t_check);
    release(l.t_chip_name); release(l.t_chip_hint); release(l.t_zip); release(l.t_close); release(l.t_report); release(l.t_caption);
}

Box pixel_box(float l, float t, float r, float b)
{
    Box x = { snap(l), snap(t), snap(r), snap(b) };
    return x;
}

/* The approved mockup's measures (CSS px = DIPs): 440 wide, 20 of padding, 4 between the
 * headline and the error, 12 to the details and the check box, 16 around the note and the zip. */
void lay_out()
{
    release_layout();
    Layout &l = g.lay;
    const wchar_t *const *t = g.t;
    float s = g.scale;
    l.line = maxf(1.0f, floorf(s));
    l.width = (int)snap(dip(440));
    float pad = dip(20), inner = l.width - 2 * pad, y = dip(20);

    l.t_head = make_text(t[S_HEADLINE], g.f_head, inner, dip(16 * 1.3f));
    IDWriteTextLayout1 *spaced = nullptr;
    if (l.t_head && SUCCEEDED(l.t_head->QueryInterface(__uuidof(IDWriteTextLayout1), (void **)&spaced)))
    {
        DWRITE_TEXT_RANGE all = { 0, (UINT32)wcslen(t[S_HEADLINE]) };
        spaced->SetCharacterSpacing(0, dip(-0.2f), 0, all);   /* letter-spacing: -0.2px */
        spaced->Release();
    }
    float h = metrics(l.t_head).height;
    l.head = pixel_box(pad, y, pad + inner, y + h);
    y += h + dip(4);

    l.t_cause = make_text(g.error, g.f_body, inner, dip(13 * 1.45f));
    h = metrics(l.t_cause).height;
    l.cause = pixel_box(pad, y, pad + inner, y + h);
    y += h + dip(12);

    l.t_toggle = make_text(t[S_SHOW_DETAILS], g.f_small, inner, dip(18), false, false);
    l.toggle = pixel_box(pad, y, pad + dip(12) + metrics(l.t_toggle).widthIncludingTrailingWhitespace, y + dip(18));
    y += dip(18);

    if (g.details_open)
    {
        y += dip(8);
        float text_w = inner - 2 * dip(12) - 2 * l.line;
        l.t_details = make_text(g.details, g.f_mono, text_w, dip(12 * 1.55f), true);
        h = metrics(l.t_details).height + 2 * dip(10) + 2 * l.line;
        l.details = pixel_box(pad, y, pad + inner, y + h);
        y += h;
    }
    else l.details = pixel_box(0, 0, 0, 0);

    y += dip(16);
    l.t_note = make_text(t[S_NOTE], g.f_body, inner - 2 * dip(10) - 2 * l.line, dip(13 * 1.5f), false, false);
    h = 2 * l.line + 2 * dip(8) + 2 * dip(13 * 1.5f);
    l.note = pixel_box(pad, y, pad + inner, y + h);
    y = l.note.b;

    if (g.report.dump_path[0])
    {
        y += dip(12);
        wchar_t size[32], label[160];
        format_size(g.report.dump_size, size, _countof(size));
        swprintf_s(label, t[S_INCLUDE_DUMP], size);
        l.t_check = make_text(label, g.f_small, inner - dip(19), dip(18), false, false);
        l.check = pixel_box(pad, y, pad + dip(13 + 6) + metrics(l.t_check).widthIncludingTrailingWhitespace, y + dip(18));
        y += dip(18);
    }
    else l.check = pixel_box(0, 0, 0, 0);

    if (g.reported)
    {
        y += dip(16);
        wchar_t size[32], name[300];
        format_size(g.chip_size, size, _countof(size));
        swprintf_s(name, L"%ls", file_name(g.chip_path));
        float text_w = inner - 2 * l.line - 2 * dip(10) - dip(28 + 10);
        l.t_chip_name = make_text(name, g.f_chip, text_w, dip(12.5f * 1.5f));
        wchar_t hint[200];
        swprintf_s(hint, L"%ls \u00B7 %ls", size, t[S_DRAG]);
        l.t_chip_hint = make_text(hint, g.f_small, text_w, dip(18));
        l.t_zip = make_text(L"ZIP", g.f_zip, dip(28), dip(14), false, false);
        if (l.t_zip) l.t_zip->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
        float content = maxf(dip(34), metrics(l.t_chip_name).height + metrics(l.t_chip_hint).height);
        h = 2 * l.line + 2 * dip(8) + content;
        l.chip = pixel_box(pad, y, pad + inner, y + h);
        y = l.chip.b;
    }
    else l.chip = pixel_box(0, 0, 0, 0);

    y += dip(16);
    float top = snap(y);
    l.footer = pixel_box(0, top, (float)l.width, top + l.line + dip(12 + 30 + 12));
    l.height = (int)l.footer.b;

    /* The site's order: Close (outlined), then Report on GitHub (filled), on the right. */
    l.t_close = make_text(t[S_CLOSE], g.f_button, 1000, dip(12.5f * 1.5f), false, false);
    l.t_report = make_text(g.reported ? t[S_REPORTED] : t[S_REPORT], g.f_button, 1000, dip(12.5f * 1.5f), false, false);
    float bt = l.footer.t + l.line + dip(12), bb = bt + dip(30);
    float report_w = 2 * l.line + dip(12 + 15 + 6 + 12) + metrics(l.t_report).widthIncludingTrailingWhitespace;
    if (g.reported) report_w -= dip(15 + 6);
    float close_w = 2 * l.line + dip(24) + metrics(l.t_close).widthIncludingTrailingWhitespace;
    float right = l.width - pad;
    l.report = pixel_box(right - report_w, bt, right, bb);
    l.close = pixel_box(l.report.l - dip(8) - close_w, bt, l.report.l - dip(8), bb);

    l.t_caption = make_text(kTitle, g.f_caption, 1000, dip(16), false, false);
}

/* ---- drawing -------------------------------------------------------------------------------- */

D2D1::Matrix3x2F base;

void fill(const Box &b, D2D1_COLOR_F c)
{
    g.brush->SetColor(c);
    g.rt->FillRectangle(b.f(), g.brush);
}

void fill_round(const Box &b, float radius, D2D1_COLOR_F c)
{
    g.brush->SetColor(c);
    g.rt->FillRoundedRectangle(D2D1::RoundedRect(b.f(), radius, radius), g.brush);
}

/* A border `width` px wide inside the box, on whole pixels. */
void stroke_round(const Box &b, float radius, float width, D2D1_COLOR_F c, ID2D1StrokeStyle *style = nullptr)
{
    float half = width / 2;
    g.brush->SetColor(c);
    D2D1_RECT_F r = D2D1::RectF(b.l + half, b.t + half, b.r - half, b.b - half);
    g.rt->DrawRoundedRectangle(D2D1::RoundedRect(r, radius - half, radius - half), g.brush, width, style);
}

void draw_text(IDWriteTextLayout *layout, float x, float y, D2D1_COLOR_F c)
{
    if (!layout) return;
    g.brush->SetColor(c);
    g.rt->DrawTextLayout(D2D1::Point2F(x, y), layout, g.brush, D2D1_DRAW_TEXT_OPTIONS_NONE);
}

/* The keyboard focus: 2 DIPs in the ink colour, 2 DIPs out from the control. */
void focus_ring(const Box &b, float radius)
{
    float out = snap(dip(2)), width = snap(dip(2));
    Box r = { b.l - out - width, b.t - out - width, b.r + out + width, b.b + out + width };
    stroke_round(r, radius + out + width, width, g.pal.contrast ? g.pal.ring : g.pal.fg);
}

void draw_chevron(float x, float cy, bool open, D2D1_COLOR_F c)
{
    /* The mockup's: a 6-DIP square's two sides, 1.5 DIPs, turned to point right (or down). */
    float k = dip(4.24f), cx = x + dip(3);
    D2D1_POINT_2F p[3];
    if (open) { p[0] = D2D1::Point2F(cx - k, cy - k / 2); p[1] = D2D1::Point2F(cx, cy + k / 2); p[2] = D2D1::Point2F(cx + k, cy - k / 2); }
    else { p[0] = D2D1::Point2F(cx - k / 2, cy - k); p[1] = D2D1::Point2F(cx + k / 2, cy); p[2] = D2D1::Point2F(cx - k / 2, cy + k); }
    g.brush->SetColor(c);
    g.rt->DrawLine(p[0], p[1], g.brush, dip(1.5f));
    g.rt->DrawLine(p[1], p[2], g.brush, dip(1.5f));
    g.rt->FillEllipse(D2D1::Ellipse(p[1], dip(0.75f), dip(0.75f)), g.brush);
}

void draw_check_box(float x, float cy, bool checked, bool hot, bool disabled)
{
    /* As a browser draws a native check box with accent-color: the foreground. */
    float size = snap(dip(13));
    Box b = { snap(x), snap(cy - size / 2), snap(x) + size, snap(cy - size / 2) + size };
    float radius = dip(2.5f);
    const Palette &p = g.pal;
    D2D1_COLOR_F ink = disabled ? mix(p.fg, p.bg, 0.5f) : p.fg;
    if (checked)
    {
        fill_round(b, radius, hot ? mix(ink, p.bg, 0.85f) : ink);
        g.brush->SetColor(p.contrast ? p.bg : p.dark ? p.caption : p.prifg);
        float w = b.w();
        D2D1_POINT_2F a = D2D1::Point2F(b.l + w * 0.22f, b.t + w * 0.52f), m = D2D1::Point2F(b.l + w * 0.42f, b.t + w * 0.72f),
                      e = D2D1::Point2F(b.l + w * 0.78f, b.t + w * 0.30f);
        g.rt->DrawLine(a, m, g.brush, dip(1.6f));
        g.rt->DrawLine(m, e, g.brush, dip(1.6f));
        g.rt->FillEllipse(D2D1::Ellipse(m, dip(0.8f), dip(0.8f)), g.brush);
    }
    else
    {
        fill_round(b, radius, p.panel);
        stroke_round(b, radius, g.lay.line, hot ? p.fg2 : p.mut);
    }
}

void draw_button(const Box &b, IDWriteTextLayout *label, bool primary, bool hot, bool pressed, bool disabled, bool icon)
{
    const Palette &p = g.pal;
    float radius = dip(8);
    D2D1_COLOR_F under = p.footer, fill_c, text_c, border_c;
    if (primary)
    {
        fill_c = p.pri;
        text_c = p.prifg;
        if ((hot || pressed) && !disabled) fill_c = mix(p.pri, under, pressed ? 0.7f : 0.8f);   /* hover:opacity-80 */
        if (disabled) { fill_c = mix(p.pri, under, 0.5f); text_c = mix(p.prifg, under, 0.5f); }
        border_c = fill_c;
        fill_round(b, radius, fill_c);
    }
    else
    {
        text_c = p.secfg;
        border_c = p.secbd;
        if ((hot || pressed) && !disabled) { fill_round(b, radius, p.hover); text_c = p.fg; }   /* hover:bg-accent text-foreground */
        if (disabled) text_c = mix(text_c, under, 0.5f);
        stroke_round(b, radius, g.lay.line, border_c);
    }
    DWRITE_TEXT_METRICS m = metrics(label);
    float icon_w = icon ? dip(15 + 6) : 0;
    float content = icon_w + m.widthIncludingTrailingWhitespace;
    float x = snap(b.l + (b.w() - content) / 2), line = dip(12.5f * 1.5f), y = b.t + (b.h() - line) / 2;
    if (icon && g.mark)
    {
        float k = dip(15) / 16;
        float iy = snap(b.t + (b.h() - dip(15)) / 2);
        g.rt->SetTransform(D2D1::Matrix3x2F::Scale(k, k) * D2D1::Matrix3x2F::Translation(x, iy) * base);
        g.brush->SetColor(text_c);
        g.rt->FillGeometry(g.mark, g.brush);
        g.rt->SetTransform(base);
    }
    draw_text(label, x + icon_w, y, text_c);
}

/* The title bar Windows draws (for the PNG): the icon, the title and the close glyph. */
void draw_caption(float height)
{
    const Palette &p = g.pal;
    fill(Box{ 0, 0, (float)g.lay.width, height }, p.caption);
    int size = (int)snap(dip(16));
    HICON icon = (HICON)LoadImageW(GetModuleHandleW(NULL), MAKEINTRESOURCEW(32512), IMAGE_ICON, size, size, LR_DEFAULTCOLOR);
    IWICBitmap *wicon = nullptr;
    IWICFormatConverter *converter = nullptr;
    ID2D1Bitmap *bitmap = nullptr;
    if (icon && SUCCEEDED(g.wic->CreateBitmapFromHICON(icon, &wicon)) && SUCCEEDED(g.wic->CreateFormatConverter(&converter))
        && SUCCEEDED(converter->Initialize(wicon, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom))
        && SUCCEEDED(g.rt->CreateBitmapFromWicBitmap(converter, NULL, &bitmap)))
    {
        float x = snap(dip(12)), y = snap((height - size) / 2);
        g.rt->DrawBitmap(bitmap, D2D1::RectF(x, y, x + size, y + size));
    }
    release(bitmap);
    release(converter);
    release(wicon);
    if (icon) DestroyIcon(icon);
    draw_text(g.lay.t_caption, snap(dip(12 + 16 + 8)), (height - dip(16)) / 2, p.fg);
    float cx = g.lay.width - dip(23), cy = height / 2, k = dip(5);
    g.brush->SetColor(p.fg);
    g.rt->DrawLine(D2D1::Point2F(cx - k, cy - k), D2D1::Point2F(cx + k, cy + k), g.brush, dip(1));
    g.rt->DrawLine(D2D1::Point2F(cx + k, cy - k), D2D1::Point2F(cx - k, cy + k), g.brush, dip(1));
}

void draw(const Look &look, float offset)
{
    const Palette &p = g.pal;
    const Layout &l = g.lay;
    base = D2D1::Matrix3x2F::Translation(0, offset);
    g.rt->SetTransform(base);
    fill(Box{ 0, 0, (float)l.width, (float)l.height }, p.bg);

    draw_text(l.t_head, l.head.l, l.head.t, p.fg);
    draw_text(l.t_cause, l.cause.l, l.cause.t, p.fg2);

    D2D1_COLOR_F toggle_c = look.hot[C_TOGGLE] ? p.fg : p.mut;
    draw_chevron(l.toggle.l, l.toggle.t + dip(9), g.details_open, toggle_c);
    draw_text(l.t_toggle, l.toggle.l + dip(12), l.toggle.t, toggle_c);
    if (look.focus[C_TOGGLE] && look.cues) focus_ring(l.toggle, dip(4));

    if (g.details_open)
    {
        fill_round(l.details, dip(8), p.panel);
        stroke_round(l.details, dip(8), l.line, p.border);
        draw_text(l.t_details, l.details.l + l.line + dip(12), l.details.t + l.line + dip(10), p.fg2);
    }

    /* The note box: an edit control sits inside it in the window; here, its placeholder. */
    fill_round(l.note, dip(8), p.panel);
    if (look.focus[C_NOTE])
    {
        Box ring = { l.note.l - l.line, l.note.t - l.line, l.note.r + l.line, l.note.b + l.line };
        stroke_round(ring, dip(8) + l.line, 2 * l.line, p.ring);   /* focus:ring-1 ring-ring, with the border */
    }
    else stroke_round(l.note, dip(8), l.line, p.border);
    if (look.note_empty) draw_text(l.t_note, l.note.l + l.line + dip(10), l.note.t + l.line + dip(8), p.mut);

    if (g.report.dump_path[0])
    {
        bool disabled = !g.can_include_dump;
        draw_check_box(l.check.l, l.check.t + dip(9), look.checked, look.hot[C_CHECK], disabled);
        draw_text(l.t_check, l.check.l + dip(13 + 6), l.check.t, disabled ? mix(p.fg2, p.bg, 0.5f) : p.fg2);
        if (look.focus[C_CHECK] && look.cues) focus_ring(l.check, dip(4));
    }

    if (g.reported)
    {
        float radius = dip(8);
        if (look.hot[C_CHIP] || look.pressed[C_CHIP]) fill_round(l.chip, radius, p.hover);
        stroke_round(l.chip, radius, l.line, look.hot[C_CHIP] ? p.mut : p.dark ? rgb(0xFFFFFF, 0.18f) : p.border, g.dashed);
        float x = l.chip.l + l.line + dip(10), inner_t = l.chip.t + l.line + dip(8), inner_h = l.chip.h() - 2 * l.line - 2 * dip(8);
        Box icon = pixel_box(x, inner_t + (inner_h - dip(34)) / 2, x + dip(28), inner_t + (inner_h - dip(34)) / 2 + dip(34));
        fill_round(icon, dip(4), p.panel);
        stroke_round(icon, dip(4), l.line, p.border);
        draw_text(l.t_zip, icon.l, icon.t + (icon.h() - dip(14)) / 2, p.fg2);
        float tx = icon.r + dip(10), th = metrics(l.t_chip_name).height + metrics(l.t_chip_hint).height;
        float ty = inner_t + (inner_h - th) / 2;
        draw_text(l.t_chip_name, tx, ty, p.fg);
        draw_text(l.t_chip_hint, tx, ty + metrics(l.t_chip_name).height, p.mut);
        if (look.focus[C_CHIP] && look.cues) focus_ring(l.chip, radius);
    }

    fill(l.footer, p.footer);
    fill(Box{ 0, l.footer.t, (float)l.width, l.footer.t + l.line }, p.border);
    draw_button(l.close, l.t_close, false, look.hot[C_CLOSE], look.pressed[C_CLOSE], false, false);
    if (look.focus[C_CLOSE] && look.cues) focus_ring(l.close, dip(8));
    draw_button(l.report, l.t_report, true, look.hot[C_REPORT], look.pressed[C_REPORT], g.reported, !g.reported);
    if (look.focus[C_REPORT] && look.cues) focus_ring(l.report, dip(8));
}

/* The software render target, `w` x `h` px (made again when the size changes). */
bool ensure_target(int w, int h)
{
    if (g.rt && g.bitmap_w == w && g.bitmap_h == h) return true;
    release(g.brush);
    release(g.rt);
    release(g.bitmap);
    if (FAILED(g.wic->CreateBitmap((UINT)w, (UINT)h, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, &g.bitmap))) return false;
    D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
    if (FAILED(g.d2d->CreateWicBitmapRenderTarget(g.bitmap, props, &g.rt))) return false;
    g.rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    g.rt->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0), &g.brush);
    g.bitmap_w = w;
    g.bitmap_h = h;
    return g.brush != nullptr;
}

bool render(const Look &look, float caption)
{
    if (!ensure_target(g.lay.width, g.lay.height + (int)caption)) return false;
    g.rt->BeginDraw();
    if (caption > 0) { base = D2D1::Matrix3x2F::Identity(); g.rt->SetTransform(base); draw_caption(caption); }
    draw(look, caption);
    return SUCCEEDED(g.rt->EndDraw());
}

/* ---- the zip -------------------------------------------------------------------------------- */

UINT32 crc_table[256];

UINT32 crc32(UINT32 crc, const BYTE *p, size_t n)
{
    if (!crc_table[1])
        for (UINT32 i = 0; i < 256; i++)
        {
            UINT32 c = i;
            for (int k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            crc_table[i] = c;
        }
    crc = ~crc;
    while (n--) crc = crc_table[(crc ^ *p++) & 255] ^ (crc >> 8);
    return ~crc;
}

/* Deflate (RFC 1951) with the fixed Huffman codes and a hash-chain LZ77: a little behind zlib on
 * text, plenty for a log and a minidump. */
struct Bits
{
    BYTE *out;
    size_t n;
    UINT64 bits;
    int count;
    void put(UINT32 value, int length)
    {
        bits |= (UINT64)value << count;
        count += length;
        while (count >= 8) { out[n++] = (BYTE)bits; bits >>= 8; count -= 8; }
    }
    void code(UINT32 c, int length)   /* Huffman codes go most significant bit first */
    {
        UINT32 r = 0;
        for (int i = 0; i < length; i++) { r = (r << 1) | (c & 1); c >>= 1; }
        put(r, length);
    }
    void literal(int symbol)
    {
        if (symbol < 144) code(0x30 + symbol, 8);
        else if (symbol < 256) code(0x190 + symbol - 144, 9);
        else if (symbol < 280) code(symbol - 256, 7);
        else code(0xC0 + symbol - 280, 8);
    }
};

const USHORT length_base[29] = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
const BYTE length_extra[29] = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
const USHORT distance_base[30] = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073,
                                   4097, 6145, 8193, 12289, 16385, 24577 };
const BYTE distance_extra[30] = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

/* `out` holds at least n + n / 8 + 64 bytes; returns the compressed length. */
size_t deflate(const BYTE *in, size_t n, BYTE *out)
{
    const int window = 32768, chain_most = 48;
    static int head[1 << 15], prev[1 << 15];
    for (int i = 0; i < (1 << 15); i++) head[i] = -1;
    Bits o = { out, 0, 0, 0 };
    o.put(1, 1);   /* the last block */
    o.put(1, 2);   /* fixed codes */
    auto hash = [&](size_t at) { return (int)(((in[at] << 10) ^ (in[at + 1] << 5) ^ in[at + 2]) & 0x7FFF); };
    auto insert = [&](size_t at) {
        if (at + 3 > n) return;
        int h = hash(at);
        prev[at & (window - 1)] = head[h];
        head[h] = (int)at;
    };
    size_t i = 0;
    while (i < n)
    {
        int best = 0, distance = 0;
        if (i + 3 <= n)
        {
            int limit = (int)(n - i < 258 ? n - i : 258);
            int candidate = head[hash(i)], chain = chain_most, last = (int)i;
            while (candidate >= 0 && candidate < last && (int)i - candidate <= window && chain-- > 0)
            {
                if (in[candidate + best] == in[i + best])
                {
                    int length = 0;
                    while (length < limit && in[candidate + length] == in[i + length]) length++;
                    if (length > best) { best = length; distance = (int)i - candidate; if (length == limit) break; }
                }
                last = candidate;
                candidate = prev[candidate & (window - 1)];
            }
        }
        if (best >= 3)
        {
            int c = 28;
            while (length_base[c] > best) c--;
            o.literal(257 + c);
            if (length_extra[c]) o.put((UINT32)(best - length_base[c]), length_extra[c]);
            int d = 29;
            while (distance_base[d] > distance) d--;
            o.code((UINT32)d, 5);
            if (distance_extra[d]) o.put((UINT32)(distance - distance_base[d]), distance_extra[d]);
            for (int k = 0; k < best; k++) insert(i + k);
            i += best;
        }
        else
        {
            o.literal(in[i]);
            insert(i);
            i++;
        }
    }
    o.literal(256);
    if (o.count) o.put(0, 8 - o.count);
    return o.n;
}

struct ZipEntry
{
    char name[300];
    UINT32 crc, packed, size, offset;
    USHORT method, time, date;
};

void put16(BYTE *&p, UINT32 v) { *p++ = (BYTE)v; *p++ = (BYTE)(v >> 8); }
void put32(BYTE *&p, UINT32 v) { put16(p, v & 0xFFFF); put16(p, v >> 16); }

/* A file's bytes, or for a log over 32 MB its first 4 MB and last 28 MB with a line between. */
BYTE *read_file(const wchar_t *path, size_t *n, bool trim)
{
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return nullptr;
    LARGE_INTEGER size;
    BYTE *data = nullptr;
    const ULONG64 head = 4ull << 20, tail = 28ull << 20;
    if (GetFileSizeEx(f, &size))
    {
        ULONG64 total = (ULONG64)size.QuadPart;
        bool cut = trim && total > head + tail;
        char marker[128];
        int marker_n = cut ? sprintf_s(marker, "\r\n[... %llu bytes of the log left out by the crash window ...]\r\n", total - head - tail) : 0;
        ULONG64 want = cut ? head + tail + marker_n : total;
        data = want < (1ull << 31) ? (BYTE *)VirtualAlloc(NULL, (SIZE_T)want + 1, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE) : nullptr;
        DWORD got;
        bool ok = data != nullptr;
        if (ok && cut)
        {
            ok = ReadFile(f, data, (DWORD)head, &got, NULL) && got == head;
            memcpy(data + head, marker, marker_n);
            LARGE_INTEGER at;
            at.QuadPart = (LONGLONG)(total - tail);
            ok = ok && SetFilePointerEx(f, at, NULL, FILE_BEGIN) && ReadFile(f, data + head + marker_n, (DWORD)tail, &got, NULL) && got == tail;
        }
        else if (ok)
        {
            ULONG64 done = 0;
            while (ok && done < want)
            {
                DWORD chunk = (DWORD)(want - done > (1u << 30) ? (1u << 30) : want - done);
                ok = ReadFile(f, data + done, chunk, &got, NULL) && got > 0;
                done += got;
            }
        }
        if (!ok && data) { VirtualFree(data, 0, MEM_RELEASE); data = nullptr; }
        else *n = (size_t)want;
    }
    CloseHandle(f);
    return data;
}

const ULONG64 kZipMost = 24ull << 20;   /* GitHub takes attachments up to 25 MB */
const ULONG64 kDumpMost = 96ull << 20;  /* a larger dump (--crash-dumps full) can't fit */

/* OniMoono-crash-YYYYMMDD-hhmm.zip beside the log: Player.log and, if `with_dump`, the dump. */
bool write_zip(const wchar_t *path, bool with_dump, ULONG64 *written)
{
    const wchar_t *sources[2] = { g.report.log_path, with_dump ? g.report.dump_path : nullptr };
    ZipEntry entries[2];
    int count = 0;
    BYTE *packed[2] = {};
    ULONG64 total = 0;
    const SYSTEMTIME &t = g.report.crashed;
    USHORT dos_time = (USHORT)((t.wHour << 11) | (t.wMinute << 5) | (t.wSecond / 2));
    USHORT dos_date = (USHORT)(((t.wYear - 1980) << 9) | (t.wMonth << 5) | t.wDay);
    for (int i = 0; i < 2; i++)
    {
        if (!sources[i] || !sources[i][0]) continue;
        size_t n = 0;
        BYTE *data = read_file(sources[i], &n, i == 0);
        if (!data) continue;
        BYTE *out = (BYTE *)VirtualAlloc(NULL, n + n / 8 + 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!out) { VirtualFree(data, 0, MEM_RELEASE); continue; }
        ZipEntry &e = entries[count];
        WideCharToMultiByte(CP_UTF8, 0, file_name(sources[i]), -1, e.name, sizeof e.name, NULL, NULL);
        e.crc = crc32(0, data, n);
        e.size = (UINT32)n;
        size_t z = deflate(data, n, out);
        if (z < n) { e.method = 8; e.packed = (UINT32)z; }
        else { e.method = 0; e.packed = (UINT32)n; memcpy(out, data, n); }
        e.time = dos_time;
        e.date = dos_date;
        VirtualFree(data, 0, MEM_RELEASE);
        if (i == 1 && total + e.packed > kZipMost) { VirtualFree(out, 0, MEM_RELEASE); break; }
        packed[count++] = out;
        total += e.packed;
    }
    if (count == 0) return false;
    size_t most = (size_t)total + 4096;
    BYTE *zip = (BYTE *)VirtualAlloc(NULL, most, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE), *p = zip;
    bool ok = zip != nullptr;
    if (ok)
    {
        for (int i = 0; i < count; i++)
        {
            ZipEntry &e = entries[i];
            UINT32 name_n = (UINT32)strlen(e.name);
            e.offset = (UINT32)(p - zip);
            put32(p, 0x04034B50); put16(p, 20); put16(p, 0x0800); put16(p, e.method); put16(p, e.time); put16(p, e.date);
            put32(p, e.crc); put32(p, e.packed); put32(p, e.size); put16(p, name_n); put16(p, 0);
            memcpy(p, e.name, name_n); p += name_n;
            memcpy(p, packed[i], e.packed); p += e.packed;
        }
        BYTE *directory = p;
        for (int i = 0; i < count; i++)
        {
            ZipEntry &e = entries[i];
            UINT32 name_n = (UINT32)strlen(e.name);
            put32(p, 0x02014B50); put16(p, 20); put16(p, 20); put16(p, 0x0800); put16(p, e.method); put16(p, e.time); put16(p, e.date);
            put32(p, e.crc); put32(p, e.packed); put32(p, e.size); put16(p, name_n); put16(p, 0); put16(p, 0); put16(p, 0); put16(p, 0);
            put32(p, 0x20); put32(p, e.offset);
            memcpy(p, e.name, name_n); p += name_n;
        }
        UINT32 directory_n = (UINT32)(p - directory), directory_at = (UINT32)(directory - zip);
        put32(p, 0x06054B50); put16(p, 0); put16(p, 0); put16(p, (UINT32)count); put16(p, (UINT32)count);
        put32(p, directory_n); put32(p, directory_at); put16(p, 0);
        HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        DWORD done = 0;
        ok = f != INVALID_HANDLE_VALUE && WriteFile(f, zip, (DWORD)(p - zip), &done, NULL) && done == (DWORD)(p - zip);
        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        if (!ok) DeleteFileW(path);
        *written = (ULONG64)(p - zip);
        VirtualFree(zip, 0, MEM_RELEASE);
    }
    for (int i = 0; i < count; i++) VirtualFree(packed[i], 0, MEM_RELEASE);
    return ok;
}

/* ---- the GitHub issue ----------------------------------------------------------------------- */

void url_encode(const wchar_t *text, char *out, size_t most, size_t *n)
{
    static char utf8[32768];
    int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof utf8, NULL, NULL);
    for (int i = 0; i + 1 < length && *n + 4 < most; i++)
    {
        BYTE c = (BYTE)utf8[i];
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            out[(*n)++] = (char)c;
        else
        {
            out[(*n)++] = '%';
            out[(*n)++] = "0123456789ABCDEF"[c >> 4];
            out[(*n)++] = "0123456789ABCDEF"[c & 15];
        }
    }
    out[*n] = 0;
}

/* The new-issue URL: "Crash: <error>", and the note, the details and where the zip is. Under
 * 7,500 characters (what browsers and GitHub take in a link): the note is cut short with "…". */
void issue_url(const wchar_t *note, const wchar_t *attach_name, wchar_t *url, size_t most)
{
    static wchar_t title[1200], body[16384], english[2048], cut_note[8192];
    static char encoded[8000];
    swprintf_s(title, L"Crash: %ls", g.error);
    details_text(&g.report, &g.system, CRASH_ENGLISH, english, _countof(english));
    wchar_t attach[400];
    swprintf_s(attach, g.t[S_ATTACH], attach_name);
    size_t note_n = wcslen(note);
    for (;;)
    {
        wcsncpy_s(cut_note, note, note_n < _countof(cut_note) - 2 ? note_n : _countof(cut_note) - 2);
        if (note_n < wcslen(note)) wcscat_s(cut_note, L"\u2026");
        swprintf_s(body, L"%ls%ls```\n%ls\n%ls\n```\n\n%ls", cut_note, cut_note[0] ? L"\n\n" : L"", g.error, english, attach);
        size_t n = 0;
        int prefix = sprintf_s(encoded, "%ls?title=", kIssueUrl);
        n = (size_t)prefix;
        url_encode(title, encoded, sizeof encoded, &n);
        n += sprintf_s(encoded + n, sizeof encoded - n, "&body=");
        url_encode(body, encoded, sizeof encoded, &n);
        if (n <= 7500 || note_n == 0) break;
        note_n = note_n > 200 ? note_n - note_n / 4 : note_n - 1;
    }
    MultiByteToWideChar(CP_UTF8, 0, encoded, -1, url, (int)most);
}

/* ---- the window ----------------------------------------------------------------------------- */

UINT window_dpi(HWND hwnd)
{
    UINT dpi = api.dpi_for_window ? api.dpi_for_window(hwnd) : 96;
    return dpi ? dpi : 96;
}

const DWORD kStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;
const DWORD kExStyle = WS_EX_APPWINDOW;

SIZE window_size(UINT dpi)
{
    RECT r = { 0, 0, g.lay.width, g.lay.height };
    if (api.adjust_for_dpi) api.adjust_for_dpi(&r, kStyle, FALSE, kExStyle, dpi);
    else AdjustWindowRectEx(&r, kStyle, FALSE, kExStyle);
    SIZE s = { r.right - r.left, r.bottom - r.top };
    return s;
}

/* The title bar: dark or light as the window, in the palette's caption colour on Windows 11. */
void apply_caption()
{
    if (!api.dwm_set || !g.hwnd) return;
    BOOL dark = g.pal.dark;
    api.dwm_set(g.hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof dark);
    COLORREF caption = g.pal.contrast ? 0xFFFFFFFF /* DWMWA_COLOR_DEFAULT */ : to_colorref(g.pal.caption);
    COLORREF caption_text = g.pal.contrast ? 0xFFFFFFFF : to_colorref(g.pal.fg);
    api.dwm_set(g.hwnd, 35 /* DWMWA_CAPTION_COLOR */, &caption, sizeof caption);
    api.dwm_set(g.hwnd, 36 /* DWMWA_TEXT_COLOR */, &caption_text, sizeof caption_text);
}

void set_icons(UINT dpi)
{
    HINSTANCE self = GetModuleHandleW(NULL);
    int small_size = api.metrics_for_dpi ? api.metrics_for_dpi(SM_CXSMICON, dpi) : GetSystemMetrics(SM_CXSMICON);
    int large_size = api.metrics_for_dpi ? api.metrics_for_dpi(SM_CXICON, dpi) : GetSystemMetrics(SM_CXICON);
    HICON s = (HICON)LoadImageW(self, MAKEINTRESOURCEW(32512), IMAGE_ICON, small_size, small_size, LR_DEFAULTCOLOR);
    HICON l = (HICON)LoadImageW(self, MAKEINTRESOURCEW(32512), IMAGE_ICON, large_size, large_size, LR_DEFAULTCOLOR);
    HICON old_s = (HICON)SendMessageW(g.hwnd, WM_SETICON, ICON_SMALL, (LPARAM)s);
    HICON old_l = (HICON)SendMessageW(g.hwnd, WM_SETICON, ICON_BIG, (LPARAM)l);
    if (old_s) DestroyIcon(old_s);
    if (old_l) DestroyIcon(old_l);
}

void apply_theme()
{
    g.pal = palette_for(g.forced_theme ? g.forced_dark : system_dark(), !g.forced_theme && system_contrast());
    if (g.panel_brush) DeleteObject(g.panel_brush);
    g.panel_brush = CreateSolidBrush(to_colorref(g.pal.panel));
    apply_caption();
    g.drawn_valid = false;
}

void make_note_font()
{
    if (g.note_font) DeleteObject(g.note_font);
    const wchar_t *face = g.report.language == CRASH_CHINESE ? L"Microsoft YaHei UI" : g.gdi_font ? L"Gabarito" : L"Segoe UI";
    g.note_font = CreateFontW(-(int)snap(dip(13)), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                              ANTIALIASED_QUALITY, DEFAULT_PITCH, face);
}

Look current_look()
{
    Look look = {};
    HWND focus = GetFocus();
    for (int i = 0; i < C_COUNT; i++)
    {
        HWND h = g.child[i];
        if (!h) continue;
        if (i == C_TOGGLE || i == C_CHECK || i == C_CHIP || i == C_CLOSE || i == C_REPORT)
        {
            LRESULT state = SendMessageW(h, BM_GETSTATE, 0, 0);
            look.hot[i] = (state & BST_HOT) != 0 && IsWindowEnabled(h);
            look.pressed[i] = (state & BST_PUSHED) != 0;
        }
        look.focus[i] = focus == h;
    }
    look.cues = !(SendMessageW(g.hwnd, WM_QUERYUISTATE, 0, 0) & UISF_HIDEFOCUS);
    look.checked = g.child[C_CHECK] && SendMessageW(g.child[C_CHECK], BM_GETCHECK, 0, 0) == BST_CHECKED;
    look.note_empty = !g.child[C_NOTE] || GetWindowTextLengthW(g.child[C_NOTE]) == 0;
    /* the edit control draws its own placeholder over the drawn one */
    return look;
}

/* The DIB the window and its controls copy from. */
bool ensure_dib()
{
    if (g.dib)
    {
        BITMAP info;
        GetObjectW(g.dib, sizeof info, &info);
        if (info.bmWidth == g.lay.width && info.bmHeight == g.lay.height) return true;
        SelectObject(g.memdc, g.old_dib);
        DeleteObject(g.dib);
        g.dib = NULL;
    }
    if (!g.memdc) g.memdc = CreateCompatibleDC(NULL);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof bi.bmiHeader;
    bi.bmiHeader.biWidth = g.lay.width;
    bi.bmiHeader.biHeight = -g.lay.height;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    g.dib = CreateDIBSection(g.memdc, &bi, DIB_RGB_COLORS, &g.dib_bits, NULL, 0);
    if (!g.dib) return false;
    g.old_dib = (HBITMAP)SelectObject(g.memdc, g.dib);
    return true;
}

/* Draws again when anything the drawing depends on changed; then the window and the controls
 * (but `painting`, which is copying now) copy their parts again. */
void refresh(HWND painting)
{
    Look look = current_look();
    if (g.drawn_valid && memcmp(&look, &g.drawn, sizeof look) == 0) return;
    if (!ensure_dib() || !render(look, 0)) return;
    WICRect all = { 0, 0, g.lay.width, g.lay.height };
    g.bitmap->CopyPixels(&all, (UINT)g.lay.width * 4, (UINT)g.lay.width * 4 * g.lay.height, (BYTE *)g.dib_bits);
    GdiFlush();
    g.drawn = look;
    g.drawn_valid = true;
    InvalidateRect(g.hwnd, NULL, FALSE);
    for (int i = 0; i < C_COUNT; i++)
        if (g.child[i] && g.child[i] != painting && i != C_NOTE) InvalidateRect(g.child[i], NULL, FALSE);
}

void copy_to(HWND control, HDC dc)
{
    refresh(control);
    RECT r;
    GetClientRect(control, &r);
    MapWindowPoints(control, g.hwnd, (POINT *)&r, 2);
    BitBlt(dc, 0, 0, r.right - r.left, r.bottom - r.top, g.memdc, r.left, r.top, SRCCOPY);
}

/* The controls over their drawn parts; a little larger than the buttons, for the focus ring. */
void place_children()
{
    const Layout &l = g.lay;
    float ring = snap(dip(4));
    auto place = [&](int i, Box b, float grow, bool show) {
        if (!g.child[i]) return;
        SetWindowPos(g.child[i], NULL, (int)(b.l - grow), (int)(b.t - grow), (int)(b.w() + 2 * grow), (int)(b.h() + 2 * grow),
                     SWP_NOZORDER | SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    };
    place(C_HEAD, l.head, 0, true);
    place(C_CAUSE, l.cause, 0, true);
    place(C_TOGGLE, l.toggle, ring, true);
    place(C_DETAILS, l.details, 0, g.details_open);
    Box note = { l.note.l + l.line + snap(dip(10)), l.note.t + l.line + snap(dip(8)), l.note.r - l.line - snap(dip(10)), l.note.b - l.line - snap(dip(8)) };
    place(C_NOTE, note, 0, true);
    place(C_CHECK, l.check, ring, g.report.dump_path[0] != 0);
    place(C_CHIP, l.chip, ring, g.reported);
    place(C_CLOSE, l.close, ring, true);
    place(C_REPORT, l.report, ring, true);
    if (g.child[C_NOTE])
    {
        SendMessageW(g.child[C_NOTE], WM_SETFONT, (WPARAM)g.note_font, FALSE);
        SendMessageW(g.child[C_NOTE], EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, 0);
        /* the text where the drawn placeholder is: centred in CSS's 19.5-DIP lines */
        HDC dc = GetDC(g.child[C_NOTE]);
        HGDIOBJ old = SelectObject(dc, g.note_font);
        TEXTMETRICW tm;
        GetTextMetricsW(dc, &tm);
        SelectObject(dc, old);
        ReleaseDC(g.child[C_NOTE], dc);
        RECT area = { 0, (LONG)maxf(0, snap((dip(19.5f) - tm.tmHeight) / 2)), (LONG)note.w(), (LONG)note.h() };
        SendMessageW(g.child[C_NOTE], EM_SETRECTNP, 0, (LPARAM)&area);
        InvalidateRect(g.child[C_NOTE], NULL, TRUE);
    }
}

/* Lays the window out again (a new DPI, the details, the zip) and sizes it; the top-left stays
 * unless `where` gives the new rectangle (WM_DPICHANGED's). */
void relayout(const RECT *where)
{
    UINT dpi = window_dpi(g.hwnd);
    g.scale = dpi / 96.0f;
    make_formats();
    lay_out();
    make_note_font();
    SIZE size = window_size(dpi);
    RECT now;
    GetWindowRect(g.hwnd, &now);
    int x = where ? where->left : now.left, y = where ? where->top : now.top;
    SetWindowPos(g.hwnd, NULL, x, y, size.cx, size.cy, SWP_NOZORDER | SWP_NOACTIVATE);
    place_children();
    g.drawn_valid = false;
    refresh(NULL);
    InvalidateRect(g.hwnd, NULL, FALSE);
}

void set_name(HWND h, const wchar_t *name)
{
    if (g.acc && h) g.acc->SetHwndPropStr(h, (DWORD)OBJID_CLIENT, CHILDID_SELF, PROPID_ACC_NAME, name);
}

void open_folder()
{
    PIDLIST_ABSOLUTE item = ILCreateFromPathW(g.chip_path);
    if (item)
    {
        SHOpenFolderAndSelectItems(item, 0, NULL, 0);
        ILFree(item);
    }
}

/* The zip as Explorer gives a file to drag: a shell data object (CF_HDROP and the shell's
 * formats), which browsers take as a file dropped on the page. */
void drag_chip()
{
    PIDLIST_ABSOLUTE item = ILCreateFromPathW(g.chip_path);
    if (!item) return;
    IShellFolder *folder = nullptr;
    PCUITEMID_CHILD child = nullptr;
    IDataObject *data = nullptr;
    if (SUCCEEDED(SHBindToParent(item, IID_PPV_ARGS(&folder), &child))
        && SUCCEEDED(folder->GetUIObjectOf(g.hwnd, 1, &child, IID_IDataObject, NULL, (void **)&data)))
    {
        DWORD effect = 0;
        SHDoDragDrop(g.hwnd, data, NULL, DROPEFFECT_COPY | DROPEFFECT_LINK, &effect);
    }
    release(data);
    release(folder);
    ILFree(item);
}

void report_crash()
{
    HCURSOR old = SetCursor(LoadCursorW(NULL, IDC_WAIT));
    wchar_t zip[1024];
    wcscpy_s(zip, g.report.log_path);
    wchar_t *slash = (wchar_t *)file_name(zip);
    const SYSTEMTIME &t = g.report.crashed;
    swprintf_s(slash, _countof(zip) - (size_t)(slash - zip), L"OniMoono-crash-%04u%02u%02u-%02u%02u.zip", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute);
    bool dump = g.can_include_dump && g.child[C_CHECK] && SendMessageW(g.child[C_CHECK], BM_GETCHECK, 0, 0) == BST_CHECKED;
    ULONG64 size = 0;
    if (write_zip(zip, dump, &size)) { wcscpy_s(g.chip_path, zip); g.chip_size = size; }
    else
    {
        /* no zip: the log itself, to drag */
        wcscpy_s(g.chip_path, g.report.log_path);
        WIN32_FILE_ATTRIBUTE_DATA a;
        g.chip_size = GetFileAttributesExW(g.report.log_path, GetFileExInfoStandard, &a) ? ((ULONG64)a.nFileSizeHigh << 32) | a.nFileSizeLow : 0;
    }
    static wchar_t note[4096], url[8192];
    GetWindowTextW(g.child[C_NOTE], note, _countof(note));
    issue_url(note, file_name(g.chip_path), url, _countof(url));
    ShellExecuteW(g.hwnd, L"open", url, NULL, NULL, SW_SHOWNORMAL);
    SetCursor(old);

    g.reported = true;
    SetWindowTextW(g.child[C_REPORT], g.t[S_REPORTED]);
    wchar_t size_text[32], name[1200];
    format_size(g.chip_size, size_text, _countof(size_text));
    swprintf_s(name, L"%ls, %ls. %ls", file_name(g.chip_path), size_text, g.t[S_DRAG]);
    SetWindowTextW(g.child[C_CHIP], name);
    relayout(NULL);
    SetFocus(g.child[C_CHIP]);
    EnableWindow(g.child[C_REPORT], FALSE);
}

LRESULT CALLBACK note_proc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_GETDLGCODE)
    {
        /* Tab, Enter and Esc go to the window (Enter reports); Ctrl+Enter is a new line. */
        LRESULT code = CallWindowProcW(g.note_proc, h, m, w, l);
        code &= ~DLGC_WANTALLKEYS;
        MSG *msg = (MSG *)l;
        if (msg && msg->message == WM_KEYDOWN && msg->wParam == VK_RETURN && GetKeyState(VK_CONTROL) < 0) code |= DLGC_WANTMESSAGE;
        return code;
    }
    LRESULT result = CallWindowProcW(g.note_proc, h, m, w, l);
    if (m == WM_PAINT && GetWindowTextLengthW(h) == 0)
    {
        HDC dc = GetDC(h);
        HGDIOBJ old = SelectObject(dc, g.note_font);
        SetTextColor(dc, to_colorref(g.pal.mut));
        SetBkMode(dc, TRANSPARENT);
        RECT r;
        SendMessageW(h, EM_GETRECT, 0, (LPARAM)&r);
        TextOutW(dc, r.left, r.top, g.t[S_NOTE], (int)wcslen(g.t[S_NOTE]));
        SelectObject(dc, old);
        ReleaseDC(h, dc);
    }
    return result;
}

LRESULT CALLBACK chip_proc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_LBUTTONDOWN)
    {
        POINT at = { GET_X_LPARAM(l), GET_Y_LPARAM(l) };
        ClientToScreen(h, &at);
        SetFocus(h);
        if (DragDetect(h, at)) drag_chip();
        else open_folder();
        return 0;
    }
    if (m == WM_LBUTTONDBLCLK) return 0;
    return CallWindowProcW(g.chip_proc, h, m, w, l);
}

void toggle_details()
{
    g.details_open = !g.details_open;
    SetWindowTextW(g.child[C_TOGGLE], g.t[g.details_open ? S_HIDE_DETAILS : S_SHOW_DETAILS]);
    relayout(NULL);
}

LRESULT CALLBACK window_proc(HWND hwnd, UINT m, WPARAM w, LPARAM l)
{
    switch (m)
    {
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT:
    {
        refresh(NULL);
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(hwnd, &ps);
        BitBlt(dc, ps.rcPaint.left, ps.rcPaint.top, ps.rcPaint.right - ps.rcPaint.left, ps.rcPaint.bottom - ps.rcPaint.top, g.memdc,
               ps.rcPaint.left, ps.rcPaint.top, SRCCOPY);
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_NOTIFY:
    {
        NMHDR *n = (NMHDR *)l;
        if (n->code == NM_CUSTOMDRAW)
        {
            NMCUSTOMDRAW *cd = (NMCUSTOMDRAW *)l;
            if (cd->dwDrawStage == CDDS_PREPAINT) { copy_to(n->hwndFrom, cd->hdc); return CDRF_SKIPDEFAULT; }
            return CDRF_DODEFAULT;
        }
        break;
    }
    case WM_DRAWITEM:
    {
        DRAWITEMSTRUCT *d = (DRAWITEMSTRUCT *)l;
        copy_to(d->hwndItem, d->hDC);
        return TRUE;
    }
    case WM_CTLCOLOREDIT:
        SetTextColor((HDC)w, to_colorref(g.pal.fg));
        SetBkColor((HDC)w, to_colorref(g.pal.panel));
        return (LRESULT)g.panel_brush;
    case WM_SETCURSOR:
    {
        HWND over = (HWND)w;
        if (over != hwnd && over != g.child[C_NOTE] && LOWORD(l) == HTCLIENT && IsWindowEnabled(over))
            for (int i : { C_TOGGLE, C_CHECK, C_CHIP, C_CLOSE, C_REPORT })
                if (over == g.child[i]) { SetCursor(LoadCursorW(NULL, IDC_HAND)); return TRUE; }
        break;
    }
    case WM_LBUTTONDOWN:
    {
        /* a click in the note box's padding goes to the text */
        POINT at = { GET_X_LPARAM(l), GET_Y_LPARAM(l) };
        RECT note = g.lay.note.rect();
        if (PtInRect(&note, at)) SetFocus(g.child[C_NOTE]);
        return 0;
    }
    case DM_GETDEFID:
        return g.reported ? 0 : MAKELRESULT(ID_REPORT, DC_HASDEFID);
    case WM_COMMAND:
    {
        int id = LOWORD(w), code = HIWORD(w);
        if (id == IDCANCEL || (id == ID_CLOSE && code == BN_CLICKED)) { DestroyWindow(hwnd); return 0; }
        if ((id == IDOK || id == ID_REPORT) && code == BN_CLICKED && !g.reported) { report_crash(); return 0; }
        if (id == ID_TOGGLE && code == BN_CLICKED) { toggle_details(); return 0; }
        if (id == ID_CHIP && code == BN_CLICKED) { open_folder(); return 0; }
        if (id == ID_NOTE && (code == EN_SETFOCUS || code == EN_KILLFOCUS || code == EN_CHANGE))
        {
            if (code == EN_CHANGE) InvalidateRect(g.child[C_NOTE], NULL, TRUE);
            refresh(NULL);
            return 0;
        }
        if (code == BN_SETFOCUS || code == BN_KILLFOCUS || code == BN_CLICKED) { refresh(NULL); return 0; }
        break;
    }
    case WM_UPDATEUISTATE:
    {
        LRESULT r = DefWindowProcW(hwnd, m, w, l);
        refresh(NULL);
        return r;
    }
    case WM_ACTIVATE:
        if (LOWORD(w) == WA_INACTIVE) g.restore_focus = GetFocus();
        else if (g.restore_focus && IsWindow(g.restore_focus)) SetFocus(g.restore_focus);
        return 0;
    case WM_DPICHANGED:
        set_icons(HIWORD(w));
        relayout((RECT *)l);
        return 0;
    case WM_SETTINGCHANGE:
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
        apply_theme();
        refresh(NULL);
        RedrawWindow(hwnd, NULL, NULL, RDW_INVALIDATE | RDW_ALLCHILDREN);
        break;
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, m, w, l);
}

/* comctl32 v6 for the controls, through an activation context from our own manifest resource 3
 * (the process's manifest stays the .NET SDK's, which the game runs with). */
void common_controls()
{
    static wchar_t exe[MAX_PATH];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    ACTCTXW context = { sizeof context };
    context.dwFlags = ACTCTX_FLAG_HMODULE_VALID | ACTCTX_FLAG_RESOURCE_NAME_VALID;
    context.hModule = GetModuleHandleW(NULL);
    context.lpSource = exe;
    context.lpResourceName = MAKEINTRESOURCEW(3);
    HANDLE act = CreateActCtxW(&context);
    ULONG_PTR cookie;
    if (act != INVALID_HANDLE_VALUE) ActivateActCtx(act, &cookie);
    HMODULE comctl = LoadLibraryW(L"comctl32.dll");
    init_controls_fn init = comctl ? (init_controls_fn)GetProcAddress(comctl, "InitCommonControlsEx") : nullptr;
    INITCOMMONCONTROLSEX icc = { sizeof icc, ICC_STANDARD_CLASSES };
    if (init) init(&icc);
}

bool start_graphics()
{
    HMODULE d2d = system_library(L"d2d1.dll"), dw = system_library(L"dwrite.dll");
    d2d_create_fn d2d_create = d2d ? (d2d_create_fn)GetProcAddress(d2d, "D2D1CreateFactory") : nullptr;
    dwrite_create_fn dw_create = dw ? (dwrite_create_fn)GetProcAddress(dw, "DWriteCreateFactory") : nullptr;
    if (!d2d_create || !dw_create) return false;
    D2D1_FACTORY_OPTIONS options = {};
    if (FAILED(d2d_create(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory), &options, (void **)&g.d2d))) return false;
    if (FAILED(dw_create(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory), (IUnknown **)&g.dw))) return false;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g.wic)))) return false;
    g.fonts = load_fonts();
    font_metrics(sans(), &g.sans_ascent, &g.sans_descent);
    font_metrics(mono(), &g.mono_ascent, &g.mono_descent);
    g.mark = svg_path(g.d2d, kGithubMark);
    float dashes[2] = { 4, 3 };
    g.d2d->CreateStrokeStyle(D2D1::StrokeStyleProperties(D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT,
        D2D1_LINE_JOIN_MITER, 10.0f, D2D1_DASH_STYLE_CUSTOM, 0.0f), dashes, 2, &g.dashed);
    return true;
}

void stop_graphics()
{
    release_layout();
    release_formats();
    release(g.brush); release(g.rt); release(g.bitmap); release(g.dashed); release(g.mark);
    release(g.fonts); release(g.wic); release(g.dw); release(g.d2d);
}

/* The report's own parts: the error line (the record's, else the exit code), the details. */
void prepare(const crash_report_t *r)
{
    g.report = *r;
    g.t = strings[r->language == CRASH_CHINESE ? 1 : 0];
    if (r->error[0]) wcscpy_s(g.error, r->error);
    else swprintf_s(g.error, g.t[S_EXIT_CODE], r->exit_code);
    g.can_include_dump = r->dump_path[0] && r->dump_size <= kDumpMost;
    g.include_dump = g.can_include_dump;
}

HWND make_child(const wchar_t *cls, const wchar_t *text, DWORD style, int id)
{
    return CreateWindowExW(0, cls, text, WS_CHILD | style, 0, 0, 1, 1, g.hwnd, (HMENU)(INT_PTR)id, GetModuleHandleW(NULL), NULL);
}

/* The rectangle of the monitor the game's window was on, else of the primary monitor. */
RECT work_area(const RECT &game)
{
    RECT probe = game;
    if (IsRectEmpty(&probe)) SetRect(&probe, 0, 0, 1, 1);
    HMONITOR monitor = MonitorFromRect(&probe, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO info = { sizeof info };
    GetMonitorInfoW(monitor, &info);
    return info.rcWork;
}

} /* namespace */

extern "C" void crash_dialog_show(const crash_report_t *r)
{
    load_api();
    if (api.set_thread_dpi && !api.set_thread_dpi(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        api.set_thread_dpi(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
    if (FAILED(OleInitialize(NULL))) return;
    common_controls();
    prepare(r);
    if (!start_graphics()) { OleUninitialize(); return; }
    gather_system(r, &g.system);
    details_text(&g.report, &g.system, r->language, g.details, _countof(g.details));
    CoCreateInstance(__uuidof(CAccPropServices), NULL, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g.acc));
    const void *font_data;
    DWORD font_size, font_count = 0;
    if (resource_data(FONT_REGULAR, &font_data, &font_size)) g.gdi_font = AddFontMemResourceEx((void *)font_data, font_size, NULL, &font_count);

    WNDCLASSEXW wc = { sizeof wc };
    wc.lpfnWndProc = window_proc;
    wc.hInstance = GetModuleHandleW(NULL);
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    wc.hIcon = LoadIconW(wc.hInstance, MAKEINTRESOURCEW(32512));
    wc.lpszClassName = L"OniMoonoCrashWindow";
    RegisterClassExW(&wc);

    RECT area = work_area(r->monitor);
    int cx = (area.left + area.right) / 2, cy = (area.top + area.bottom) / 2;
    g.hwnd = CreateWindowExW(kExStyle, wc.lpszClassName, kTitle, kStyle, cx - 200, cy - 150, 400, 300, NULL, NULL, wc.hInstance, NULL);
    if (!g.hwnd) { stop_graphics(); OleUninitialize(); return; }
    UINT dpi = window_dpi(g.hwnd);
    g.scale = dpi / 96.0f;
    apply_theme();
    set_icons(dpi);

    /* In the order Tab goes through them. */
    g.child[C_HEAD] = make_child(L"STATIC", g.t[S_HEADLINE], WS_VISIBLE | SS_OWNERDRAW, ID_HEAD);
    g.child[C_CAUSE] = make_child(L"STATIC", g.error, WS_VISIBLE | SS_OWNERDRAW, ID_CAUSE);
    g.child[C_TOGGLE] = make_child(L"BUTTON", g.t[S_SHOW_DETAILS], WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON | BS_NOTIFY, ID_TOGGLE);
    g.child[C_DETAILS] = make_child(L"STATIC", g.details, SS_OWNERDRAW, ID_DETAILS);
    g.child[C_NOTE] = make_child(L"EDIT", L"", WS_VISIBLE | WS_TABSTOP | ES_MULTILINE | ES_AUTOVSCROLL, ID_NOTE);
    g.child[C_CHECK] = make_child(L"BUTTON", L"", WS_TABSTOP | BS_AUTOCHECKBOX | BS_NOTIFY, ID_CHECK);
    g.child[C_CHIP] = make_child(L"BUTTON", L"", WS_TABSTOP | BS_PUSHBUTTON | BS_NOTIFY, ID_CHIP);
    g.child[C_CLOSE] = make_child(L"BUTTON", g.t[S_CLOSE], WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON | BS_NOTIFY, ID_CLOSE);
    g.child[C_REPORT] = make_child(L"BUTTON", g.t[S_REPORT], WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON | BS_NOTIFY, ID_REPORT);
    g.note_proc = (WNDPROC)SetWindowLongPtrW(g.child[C_NOTE], GWLP_WNDPROC, (LONG_PTR)note_proc);
    g.chip_proc = (WNDPROC)SetWindowLongPtrW(g.child[C_CHIP], GWLP_WNDPROC, (LONG_PTR)chip_proc);
    SendMessageW(g.child[C_NOTE], EM_LIMITTEXT, 3000, 0);
    set_name(g.child[C_NOTE], g.t[S_NOTE]);
    if (r->dump_path[0])
    {
        wchar_t size[32], label[160];
        format_size(r->dump_size, size, _countof(size));
        swprintf_s(label, g.t[S_INCLUDE_DUMP], size);
        SetWindowTextW(g.child[C_CHECK], label);
        SendMessageW(g.child[C_CHECK], BM_SETCHECK, g.include_dump ? BST_CHECKED : BST_UNCHECKED, 0);
        EnableWindow(g.child[C_CHECK], g.can_include_dump);
    }

    make_formats();
    lay_out();
    make_note_font();
    SIZE size = window_size(dpi);
    SetWindowPos(g.hwnd, NULL, cx - size.cx / 2, cy - size.cy / 2, size.cx, size.cy, SWP_NOZORDER | SWP_NOACTIVATE);
    place_children();
    SendMessageW(g.hwnd, WM_CHANGEUISTATE, MAKEWPARAM(UIS_INITIALIZE, 0), 0);
    SetFocus(g.child[C_NOTE]);
    g.restore_focus = g.child[C_NOTE];
    refresh(NULL);
    if (r->activate)
    {
        ShowWindow(g.hwnd, SW_SHOW);
        SetForegroundWindow(g.hwnd);
    }
    else ShowWindow(g.hwnd, SW_SHOWNOACTIVATE);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0)
    {
        if (IsDialogMessageW(g.hwnd, &msg)) continue;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (g.memdc) { SelectObject(g.memdc, g.old_dib); DeleteDC(g.memdc); }
    if (g.dib) DeleteObject(g.dib);
    if (g.note_font) DeleteObject(g.note_font);
    if (g.panel_brush) DeleteObject(g.panel_brush);
    if (g.gdi_font) RemoveFontMemResourceEx(g.gdi_font);
    release(g.acc);
    stop_graphics();
    OleUninitialize();
}

/* --crash-dialog-render OUT.png [--theme light|dark] [--scale S] [--sample gpu|managed|fastfail]
 * [--language en|zh] [--details-open] [--reported]: the window as it would show, with a drawn title
 * bar, from fixed sample facts, to a PNG. For scripts/check-native-crash.sh also [--zip OUT.zip
 * --log FILE [--dump FILE]]: the zip "Report on GitHub" writes, of these files; [--url OUT.txt
 * --note TEXT]: the issue's URL, UTF-8. */
extern "C" int crash_dialog_render(int argc, wchar_t **argv)
{
    if (argc < 3) return 2;
    const wchar_t *out = argv[2], *sample = L"gpu";
    static crash_report_t r;
    bool dark = true, details = false, reported = false;
    float scale = 1.0f;
    const wchar_t *zip_out = nullptr, *log = nullptr, *dump = nullptr, *url_out = nullptr, *note = L"";
    r.language = CRASH_ENGLISH;
    for (int i = 3; i < argc; i++)
    {
        const wchar_t *a = argv[i], *v = i + 1 < argc ? argv[i + 1] : L"";
        if (wcscmp(a, L"--theme") == 0) { dark = wcscmp(v, L"light") != 0; i++; }
        else if (wcscmp(a, L"--scale") == 0) { scale = (float)_wtof(v); i++; }
        else if (wcscmp(a, L"--sample") == 0) { sample = v; i++; }
        else if (wcscmp(a, L"--language") == 0) { r.language = wcscmp(v, L"zh") == 0 ? CRASH_CHINESE : CRASH_ENGLISH; i++; }
        else if (wcscmp(a, L"--zip") == 0) { zip_out = v; i++; }
        else if (wcscmp(a, L"--log") == 0) { log = v; i++; }
        else if (wcscmp(a, L"--dump") == 0) { dump = v; i++; }
        else if (wcscmp(a, L"--url") == 0) { url_out = v; i++; }
        else if (wcscmp(a, L"--note") == 0) { note = v; i++; }
        else if (wcscmp(a, L"--details-open") == 0) details = true;
        else if (wcscmp(a, L"--reported") == 0) reported = true;
        else return 2;
    }
    if (scale < 0.5f || scale > 4.0f) return 2;
    /* the mockup's sample */
    wcscpy_s(r.version, L"v2026.11.06.01");
    wcscpy_s(r.game_build, L"762230");
    r.mods = 214;
    r.play_seconds = 2 * 3600 + 14 * 60 + 9;
    wcscpy_s(r.log_path, L"C:\\Users\\Player\\AppData\\LocalLow\\Klei\\Oxygen Not Included\\Player.log");
    r.crashed.wYear = 2026; r.crashed.wMonth = 11; r.crashed.wDay = 7; r.crashed.wHour = 14; r.crashed.wMinute = 15; r.crashed.wSecond = 2;
    if (wcscmp(sample, L"gpu") == 0)
    {
        wcscpy_s(r.error, L"0xC0000005 (access violation) in nvoglv64.dll");
        wcscpy_s(r.dump_path, L"C:\\Users\\Player\\AppData\\LocalLow\\Klei\\Oxygen Not Included\\crash-20261107-141502.dmp");
        r.dump_size = 5976883;
    }
    else if (wcscmp(sample, L"managed") == 0)
        wcscpy_s(r.error, L"System.NullReferenceException in BetterInfoCards.InfoCardWidgets.Update: Object reference not set to an instance of an object.");
    else if (wcscmp(sample, L"fastfail") == 0) wcscpy_s(r.error, L"0xC0000409 (fast fail)");
    else return 2;
    if (log) wcscpy_s(r.log_path, log);
    if (dump)
    {
        wcscpy_s(r.dump_path, dump);
        WIN32_FILE_ATTRIBUTE_DATA a;
        r.dump_size = GetFileAttributesExW(dump, GetFileExInfoStandard, &a) ? ((ULONG64)a.nFileSizeHigh << 32) | a.nFileSizeLow : 0;
    }

    load_api();
    if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) return 1;
    g.render_only = true;
    prepare(&r);
    if (!start_graphics()) return 1;
    wcscpy_s(g.system.windows, L"Windows 11 24H2 (26100.4061)");
    wcscpy_s(g.system.gpu, L"NVIDIA GeForce RTX 3090 (581.29)");
    g.system.ram_gb = 32;
    details_text(&g.report, &g.system, r.language, g.details, _countof(g.details));
    g.scale = scale;
    g.forced_theme = true;
    g.forced_dark = dark;
    g.pal = palette_for(dark, false);
    g.details_open = details;
    if (reported)
    {
        g.reported = true;
        swprintf_s(g.chip_path, L"C:\\Users\\Player\\AppData\\LocalLow\\Klei\\Oxygen Not Included\\OniMoono-crash-20261107-1415.zip");
        g.chip_size = 1993728;
    }
    if (zip_out)
    {
        ULONG64 size = 0;
        if (!write_zip(zip_out, g.can_include_dump, &size)) return 3;
    }
    if (url_out)
    {
        static wchar_t url[8192];
        static char utf8[16384];
        issue_url(note, L"OniMoono-crash-20261107-1415.zip", url, _countof(url));
        int n = WideCharToMultiByte(CP_UTF8, 0, url, -1, utf8, sizeof utf8, NULL, NULL);
        HANDLE f = CreateFileW(url_out, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        DWORD done;
        bool ok = f != INVALID_HANDLE_VALUE && n > 1 && WriteFile(f, utf8, (DWORD)(n - 1), &done, NULL);
        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        if (!ok) return 3;
    }
    make_formats();
    lay_out();
    Look look = {};
    look.checked = g.include_dump;
    look.note_empty = true;
    float caption = snap(dip(32));
    int status = 1;
    if (render(look, caption))
    {
        IWICStream *stream = nullptr;
        IWICBitmapEncoder *encoder = nullptr;
        IWICBitmapFrameEncode *frame = nullptr;
        IWICBitmapLock *lock = nullptr;
        UINT w = (UINT)g.bitmap_w, h = (UINT)g.bitmap_h, stride = 0, bytes = 0;
        BYTE *pixels = nullptr;
        WICRect all = { 0, 0, (INT)w, (INT)h };
        WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
        if (SUCCEEDED(g.wic->CreateStream(&stream)) && SUCCEEDED(stream->InitializeFromFilename(out, GENERIC_WRITE))
            && SUCCEEDED(g.wic->CreateEncoder(GUID_ContainerFormatPng, NULL, &encoder))
            && SUCCEEDED(encoder->Initialize(stream, WICBitmapEncoderNoCache)) && SUCCEEDED(encoder->CreateNewFrame(&frame, NULL))
            && SUCCEEDED(frame->Initialize(NULL)) && SUCCEEDED(frame->SetSize(w, h)) && SUCCEEDED(frame->SetPixelFormat(&format))
            && IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA)
            && SUCCEEDED(g.bitmap->Lock(&all, WICBitmapLockRead, &lock)) && SUCCEEDED(lock->GetStride(&stride))
            && SUCCEEDED(lock->GetDataPointer(&bytes, &pixels))
            /* opaque, so premultiplied is the same as straight */
            && SUCCEEDED(frame->WritePixels(h, stride, bytes, pixels)) && (release(lock), SUCCEEDED(frame->Commit()))
            && SUCCEEDED(encoder->Commit()))
            status = 0;
        release(lock);
        release(frame);
        release(encoder);
        release(stream);
    }
    stop_graphics();
    CoUninitialize();
    return status;
}
