/* The crash window of the Windows player (ARCHITECTURE §7, "Native crashes", "The crash window").
 *
 * The watcher (crash.c) shows it after it has written a crash record to Player.log: the headline,
 * the error line from the record, details on request, a box for what the player was doing, the
 * dump to include or not, and "Report on GitHub", which packs Player.log (and the dump) into a zip
 * beside them, opens a new issue in the browser with the error and the details filled in, and then
 * shows the zip so it can be dragged into the page. Nothing is sent by the window itself.
 *
 * What both players' windows share is crash_window.c: the strings, the palette, the layout and
 * the drawing, the details, the zip and the issue link. This is Windows' canvas for it and
 * Windows' window.
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
#include <richedit.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <math.h>
#include <initializer_list>
#include "crash_dialog.h"
#include "crash_window.h"

/* ---- files, for crash_window.c (UTF-8 paths) ------------------------------------------------- */

extern "C" FILE *cw_open(const char *path, const char *mode)
{
    wchar_t wide[1100], wmode[8];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, _countof(wide)) || !MultiByteToWideChar(CP_UTF8, 0, mode, -1, wmode, _countof(wmode))) return nullptr;
    FILE *f = nullptr;
    return _wfopen_s(&f, wide, wmode) == 0 ? f : nullptr;
}

extern "C" int cw_seek(FILE *f, uint64_t at) { return _fseeki64(f, (__int64)at, SEEK_SET) == 0; }

extern "C" uint64_t cw_file_size(FILE *f)
{
    __int64 here = _ftelli64(f);
    if (_fseeki64(f, 0, SEEK_END) != 0) return 0;
    __int64 size = _ftelli64(f);
    _fseeki64(f, here, SEEK_SET);
    return size > 0 ? (uint64_t)size : 0;
}

namespace {

template <class T> void release(T *&p)
{
    if (p) { p->Release(); p = nullptr; }
}

float maxf(float a, float b) { return a > b ? a : b; }

/* UTF-8 for the controls and the shell: wide, in one of a few rotating buffers. */
const wchar_t *W(const char *utf8)
{
    static wchar_t buffers[4][8192];
    static int next;
    wchar_t *out = buffers[next++ & 3];
    if (!MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out, 8192)) out[0] = 0;
    return out;
}

void to_utf8(const wchar_t *wide, char *out, size_t most)
{
    if (!WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, (int)most, NULL, NULL)) out[0] = 0;
}

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

D2D1_COLOR_F color(cw_color c)
{
    return D2D1::ColorF(c.r, c.g, c.b, c.a);
}

cw_color from_colorref(COLORREF c)
{
    cw_color x = { GetRValue(c) / 255.0f, GetGValue(c) / 255.0f, GetBValue(c) / 255.0f, 1.0f };
    return x;
}

COLORREF to_colorref(cw_color c)
{
    return RGB((BYTE)lroundf(c.r * 255), (BYTE)lroundf(c.g * 255), (BYTE)lroundf(c.b * 255));
}

/* The site's palette (cw_palette_for); in high-contrast mode the system's colours. */
cw_palette palette_for(bool dark, bool contrast)
{
    if (!contrast) return cw_palette_for(dark);
    cw_palette p = {};
    p.contrast = 1;
    cw_color window = from_colorref(GetSysColor(COLOR_WINDOW)), text = from_colorref(GetSysColor(COLOR_WINDOWTEXT));
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

/* ---- the GitHub mark as a Direct2D geometry --------------------------------------------------- */

struct MarkSink
{
    ID2D1GeometrySink *sink;
};

void mark_begin(void *c, float x, float y) { ((MarkSink *)c)->sink->BeginFigure(D2D1::Point2F(x, y), D2D1_FIGURE_BEGIN_FILLED); }
void mark_line(void *c, float x, float y) { ((MarkSink *)c)->sink->AddLine(D2D1::Point2F(x, y)); }

void mark_cubic(void *c, float x1, float y1, float x2, float y2, float x, float y)
{
    D2D1_BEZIER_SEGMENT b;
    b.point1.x = x1; b.point1.y = y1;
    b.point2.x = x2; b.point2.y = y2;
    b.point3.x = x; b.point3.y = y;
    ((MarkSink *)c)->sink->AddBezier(b);
}

void mark_arc(void *c, float rx, float ry, float rotation, int large, int sweep, float x, float y)
{
    D2D1_ARC_SEGMENT a;
    a.size.width = rx; a.size.height = ry;
    a.rotationAngle = rotation;
    a.arcSize = large ? D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE_SMALL;
    a.sweepDirection = sweep ? D2D1_SWEEP_DIRECTION_CLOCKWISE : D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE;
    a.point.x = x; a.point.y = y;
    ((MarkSink *)c)->sink->AddArc(a);
}

void mark_end(void *c, int closed) { ((MarkSink *)c)->sink->EndFigure(closed ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN); }

ID2D1PathGeometry *svg_path(ID2D1Factory *factory, const char *text)
{
    ID2D1PathGeometry *path = nullptr;
    ID2D1GeometrySink *sink = nullptr;
    if (FAILED(factory->CreatePathGeometry(&path)) || FAILED(path->Open(&sink))) { release(path); return nullptr; }
    sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    static const cw_path_sink calls = { mark_begin, mark_line, mark_cubic, mark_arc, mark_end };
    MarkSink context = { sink };
    cw_svg_path(text, &calls, &context);
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

/* ---- this machine --------------------------------------------------------------------------- */

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

void gather_system(const crash_report_t *r)
{
    wchar_t windows[96], gpu[192];
    windows_version(windows, _countof(windows));
    __try
    {
        gpu_line(r->gpu, gpu, _countof(gpu));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        wcsncpy_s(gpu, r->gpu, _TRUNCATE);
    }
    to_utf8(windows, cw.system.os, sizeof cw.system.os);
    to_utf8(gpu, cw.system.gpu, sizeof cw.system.gpu);
    ULONGLONG kb = 0;
    cw.system.ram_gb = GetPhysicallyInstalledSystemMemory(&kb) ? (kb + 512 * 1024) / (1024 * 1024) : 0;
}

/* ---- the window's state --------------------------------------------------------------------- */

enum { ID_TOGGLE = 101, ID_NOTE, ID_CHECK, ID_CHIP, ID_CLOSE, ID_REPORT, ID_HEAD, ID_CAUSE, ID_DETAILS };


/* Windows' own state beside crash_window.c's `cw`. */
struct Dialog
{
    crash_report_t report;          /* as the watcher gave it (wide paths) */
    wchar_t chip_path[1024];        /* cw.chip_path, wide */

    ID2D1Factory *d2d;
    IDWriteFactory *dw;
    IWICImagingFactory *wic;
    IDWriteFontCollection *fonts;   /* ours, or null (system fonts) */
    float sans_ascent, sans_descent, mono_ascent, mono_descent;   /* per em */
    IDWriteTextFormat *formats[CW_F_COUNT];
    ID2D1PathGeometry *mark;
    ID2D1StrokeStyle *dashed;

    IWICBitmap *bitmap;
    ID2D1RenderTarget *rt;
    ID2D1SolidColorBrush *brush;
    int bitmap_w, bitmap_h;
    D2D1::Matrix3x2F base;

    HWND hwnd, child[C_COUNT];
    WNDPROC note_proc, chip_proc;
    HDC memdc;
    HBITMAP dib, old_dib;
    void *dib_bits;
    cw_look drawn;
    bool drawn_valid;
    HFONT note_font, cause_font, mono_font;
    HANDLE gdi_mono;
    HBRUSH panel_brush;
    HANDLE gdi_font;
    HWND restore_focus;
    IAccPropServices *acc;
    bool forced_theme, forced_dark;
};

Dialog g;

float dip(float v) { return cw_dip(v); }
float tdip(float v) { return cw_tdip(v); }
float snap(float v) { return cw_snap(v); }

/* Settings > Accessibility > Text size, 100 to 225 percent. */
float text_scale()
{
    DWORD percent = 100, size = sizeof percent;
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Accessibility", L"TextScaleFactor", RRF_RT_REG_DWORD, NULL, &percent, &size) != ERROR_SUCCESS)
        return 1.0f;
    return percent < 100 ? 1.0f : percent > 225 ? 2.25f : percent / 100.0f;
}

RECT rect_of(cw_box b)
{
    RECT x = { (LONG)b.l, (LONG)b.t, (LONG)b.r, (LONG)b.b };
    return x;
}

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
                           cw.report.language == CW_CHINESE ? L"zh-cn" : L"en-us", &format);
    if (format) format->SetWordWrapping(DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
    return format;
}

void release_formats()
{
    for (int i = 0; i < CW_F_COUNT; i++) release(g.formats[i]);
}

/* The layout's fonts (cw_font_of): ours (sans, mono) or, for the drawn title bar, Segoe UI. */
void make_formats()
{
    release_formats();
    for (int i = 0; i < CW_F_COUNT; i++)
    {
        cw_font f = cw_font_of(i);
        DWRITE_FONT_WEIGHT weight = f.weight == CW_SEMIBOLD ? DWRITE_FONT_WEIGHT_SEMI_BOLD : f.weight == CW_MEDIUM ? DWRITE_FONT_WEIGHT_MEDIUM : DWRITE_FONT_WEIGHT_NORMAL;
        g.formats[i] = f.family == 2 ? text_format(L"Segoe UI", nullptr, weight, f.size) : text_format(f.family == 1 ? mono() : sans(), g.fonts, weight, f.size);
    }
}

} /* namespace */

/* A DirectWrite layout, crash_window.c's cw_text. */
struct cw_text
{
    IDWriteTextLayout *layout;
    UINT32 length;
};

namespace {

/* ---- the canvas: crash_window.c's drawing through Direct2D ---------------------------------- */

/* A text laid out as CSS lays out `line-height`: lines `line_height` px apart, the font's ascent
 * and descent centred in each. */
cw_text *c_make_text(void *, const char *utf8, int font, float width, float line_height, int wrap)
{
    IDWriteTextFormat *format = g.formats[font];
    const wchar_t *text = W(utf8);
    IDWriteTextLayout *layout = nullptr;
    if (!format || FAILED(g.dw->CreateTextLayout(text, (UINT32)wcslen(text), format, maxf(width, 1.0f), 100000.0f, &layout))) return nullptr;
    bool is_mono = font == CW_F_MONO;
    float size = format->GetFontSize();
    float ascent = (is_mono ? g.mono_ascent : g.sans_ascent) * size, descent = (is_mono ? g.mono_descent : g.sans_descent) * size;
    layout->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, line_height, (line_height - ascent - descent) / 2 + ascent);
    if (!wrap) layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
    cw_text *t = new cw_text;
    t->layout = layout;
    t->length = (UINT32)wcslen(text);
    return t;
}

void c_set_spacing(void *, cw_text *t, float spacing)
{
    IDWriteTextLayout1 *spaced = nullptr;
    if (SUCCEEDED(t->layout->QueryInterface(__uuidof(IDWriteTextLayout1), (void **)&spaced)))
    {
        DWRITE_TEXT_RANGE all = { 0, t->length };
        spaced->SetCharacterSpacing(0, spacing, 0, all);
        spaced->Release();
    }
}

void c_set_align(void *, cw_text *t, int align)
{
    if (align == CW_ALIGN_CENTER) t->layout->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
}

cw_metrics c_metrics(void *, cw_text *t)
{
    DWRITE_TEXT_METRICS m = {};
    t->layout->GetMetrics(&m);
    cw_metrics r = { m.height, m.widthIncludingTrailingWhitespace, (int)m.lineCount };
    return r;
}

void c_free_text(void *, cw_text *t)
{
    release(t->layout);
    delete t;
}

void c_set_offset(void *, float y)
{
    g.base = D2D1::Matrix3x2F::Translation(0, y);
    g.rt->SetTransform(g.base);
}

void c_fill(void *, cw_box b, cw_color c)
{
    g.brush->SetColor(color(c));
    g.rt->FillRectangle(D2D1::RectF(b.l, b.t, b.r, b.b), g.brush);
}

void c_fill_round(void *, cw_box b, float radius, cw_color c)
{
    g.brush->SetColor(color(c));
    g.rt->FillRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(b.l, b.t, b.r, b.b), radius, radius), g.brush);
}

void c_stroke_round(void *, cw_box b, float radius, float width, cw_color c, int dashed)
{
    g.brush->SetColor(color(c));
    g.rt->DrawRoundedRectangle(D2D1::RoundedRect(D2D1::RectF(b.l, b.t, b.r, b.b), radius, radius), g.brush, width, dashed ? g.dashed : nullptr);
}

void c_draw_text(void *, cw_text *t, float x, float y, cw_color c)
{
    g.brush->SetColor(color(c));
    g.rt->DrawTextLayout(D2D1::Point2F(x, y), t->layout, g.brush, D2D1_DRAW_TEXT_OPTIONS_NONE);
}

void c_line(void *, float x0, float y0, float x1, float y1, float width, cw_color c)
{
    g.brush->SetColor(color(c));
    g.rt->DrawLine(D2D1::Point2F(x0, y0), D2D1::Point2F(x1, y1), g.brush, width);
}

void c_dot(void *, float x, float y, float radius, cw_color c)
{
    g.brush->SetColor(color(c));
    g.rt->FillEllipse(D2D1::Ellipse(D2D1::Point2F(x, y), radius, radius), g.brush);
}

void c_mark(void *, float x, float y, float k, cw_color c)
{
    if (!g.mark) return;
    g.rt->SetTransform(D2D1::Matrix3x2F::Scale(k, k) * D2D1::Matrix3x2F::Translation(x, y) * g.base);
    g.brush->SetColor(color(c));
    g.rt->FillGeometry(g.mark, g.brush);
    g.rt->SetTransform(g.base);
}

/* The title bar's icon (for the PNG): resource 32512 at `size` px. */
void c_icon(void *, float x, float y, int size)
{
    HICON icon = (HICON)LoadImageW(GetModuleHandleW(NULL), MAKEINTRESOURCEW(32512), IMAGE_ICON, size, size, LR_DEFAULTCOLOR);
    IWICBitmap *wicon = nullptr;
    IWICFormatConverter *converter = nullptr;
    ID2D1Bitmap *bitmap = nullptr;
    if (icon && SUCCEEDED(g.wic->CreateBitmapFromHICON(icon, &wicon)) && SUCCEEDED(g.wic->CreateFormatConverter(&converter))
        && SUCCEEDED(converter->Initialize(wicon, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom))
        && SUCCEEDED(g.rt->CreateBitmapFromWicBitmap(converter, NULL, &bitmap)))
        g.rt->DrawBitmap(bitmap, D2D1::RectF(x, y, x + size, y + size));
    release(bitmap);
    release(converter);
    release(wicon);
    if (icon) DestroyIcon(icon);
}

const cw_canvas canvas = {
    nullptr, c_make_text, c_set_spacing, c_set_align, c_metrics, c_free_text, c_set_offset, c_fill, c_fill_round, c_stroke_round,
    c_draw_text, c_line, c_dot, c_mark, c_icon,
};

cw_metrics metrics(cw_text *t)
{
    cw_metrics m = { 0, 0, 0 };
    return t ? c_metrics(nullptr, t) : m;
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

bool render(const cw_look &look, float caption)
{
    if (!ensure_target(cw.lay.width, cw.lay.height + (int)caption)) return false;
    g.rt->BeginDraw();
    if (caption > 0) { c_set_offset(nullptr, 0); cw_draw_caption(&canvas, caption); }
    cw_draw(&canvas, &look, caption);
    return SUCCEEDED(g.rt->EndDraw());
}

void lay_out() { cw_lay_out(&canvas); }

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
    RECT r = { 0, 0, cw.lay.width, cw.lay.height };
    if (api.adjust_for_dpi) api.adjust_for_dpi(&r, kStyle, FALSE, kExStyle, dpi);
    else AdjustWindowRectEx(&r, kStyle, FALSE, kExStyle);
    SIZE s = { r.right - r.left, r.bottom - r.top };
    return s;
}

/* The title bar: dark or light as the window, in the palette's caption colour on Windows 11. */
void apply_caption()
{
    if (!api.dwm_set || !g.hwnd) return;
    BOOL dark = cw.pal.dark;
    api.dwm_set(g.hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, &dark, sizeof dark);
    COLORREF caption = cw.pal.contrast ? 0xFFFFFFFF /* DWMWA_COLOR_DEFAULT */ : to_colorref(cw.pal.caption);
    COLORREF caption_text = cw.pal.contrast ? 0xFFFFFFFF : to_colorref(cw.pal.fg);
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
    cw.pal = palette_for(g.forced_theme ? g.forced_dark : system_dark(), !g.forced_theme && system_contrast());
    if (g.panel_brush) DeleteObject(g.panel_brush);
    g.panel_brush = CreateSolidBrush(to_colorref(cw.pal.panel));
    apply_caption();
    g.drawn_valid = false;
}

HFONT gdi_font(const wchar_t *face, float px)
{
    return CreateFontW(-(int)snap(px), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                       ANTIALIASED_QUALITY, DEFAULT_PITCH, face);
}

void make_note_font()
{
    if (g.note_font) DeleteObject(g.note_font);
    if (g.cause_font) DeleteObject(g.cause_font);
    if (g.mono_font) DeleteObject(g.mono_font);
    const wchar_t *face = cw.report.language == CW_CHINESE ? L"Microsoft YaHei UI" : g.gdi_font ? L"Gabarito" : L"Segoe UI";
    g.note_font = gdi_font(face, tdip(13));
    g.cause_font = gdi_font(g.gdi_font ? L"Gabarito" : L"Segoe UI", tdip(13));
    g.mono_font = gdi_font(g.gdi_mono ? L"Red Hat Mono" : L"Consolas", tdip(12));
}

cw_look current_look()
{
    cw_look look = {};
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
        if (info.bmWidth == cw.lay.width && info.bmHeight == cw.lay.height) return true;
        SelectObject(g.memdc, g.old_dib);
        DeleteObject(g.dib);
        g.dib = NULL;
    }
    if (!g.memdc) g.memdc = CreateCompatibleDC(NULL);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof bi.bmiHeader;
    bi.bmiHeader.biWidth = cw.lay.width;
    bi.bmiHeader.biHeight = -cw.lay.height;
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
    cw_look look = current_look();
    if (g.drawn_valid && memcmp(&look, &g.drawn, sizeof look) == 0) return;
    if (!ensure_dib() || !render(look, 0)) return;
    WICRect all = { 0, 0, cw.lay.width, cw.lay.height };
    g.bitmap->CopyPixels(&all, (UINT)cw.lay.width * 4, (UINT)cw.lay.width * 4 * cw.lay.height, (BYTE *)g.dib_bits);
    GdiFlush();
    g.drawn = look;
    g.drawn_valid = true;
    InvalidateRect(g.hwnd, NULL, FALSE);
    for (int i = 0; i < C_COUNT; i++)
        if (g.child[i] && g.child[i] != painting && i != C_NOTE && i != C_CAUSE && i != C_DETAILS) InvalidateRect(g.child[i], NULL, FALSE);
}

void copy_to(HWND control, HDC dc)
{
    refresh(control);
    RECT r;
    GetClientRect(control, &r);
    MapWindowPoints(control, g.hwnd, (POINT *)&r, 2);
    BitBlt(dc, 0, 0, r.right - r.left, r.bottom - r.top, g.memdc, r.left, r.top, SRCCOPY);
}

void style_field(HWND h, HFONT font, cw_color fg, cw_color bg, float line);
float field_raise(HWND h, HFONT font, float line);

/* The controls over their drawn parts; a little larger than the buttons, for the focus ring. */
void place_children()
{
    const cw_layout &l = cw.lay;
    float ring = snap(dip(4));
    auto place = [&](int i, cw_box b, float grow, bool show) {
        if (!g.child[i]) return;
        SetWindowPos(g.child[i], NULL, (int)(b.l - grow), (int)(b.t - grow), (int)(cw_w(b) + 2 * grow), (int)(cw_h(b) + 2 * grow),
                     SWP_NOZORDER | SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    };
    place(C_HEAD, l.head, 0, true);
    float cause_line = tdip(13 * 1.45f), details_line = tdip(12 * 1.55f);
    float raise = g.child[C_CAUSE] ? field_raise(g.child[C_CAUSE], g.cause_font, cause_line) : 0;
    place(C_CAUSE, cw_box{ l.cause.l, l.cause.t - raise, l.cause.r, l.cause.b }, 0, true);
    place(C_TOGGLE, l.toggle, ring, true);
    raise = g.child[C_DETAILS] ? field_raise(g.child[C_DETAILS], g.mono_font, details_line) : 0;
    cw_box details = { l.details.l + l.line + snap(dip(12)), l.details.t + l.line + snap(dip(10)) - raise, l.details.r - l.line - snap(dip(12)),
                       l.details.b - l.line - snap(dip(10)) };
    place(C_DETAILS, details, 0, cw.details_open != 0);
    if (g.child[C_CAUSE]) style_field(g.child[C_CAUSE], g.cause_font, cw.pal.fg2, cw.pal.bg, cause_line);
    if (g.child[C_DETAILS]) style_field(g.child[C_DETAILS], g.mono_font, cw.pal.fg2, cw.pal.panel, details_line);
    cw_box note = { l.note.l + l.line + snap(dip(10)), l.note.t + l.line + snap(dip(8)), l.note.r - l.line - snap(dip(10)), l.note.b - l.line - snap(dip(8)) };
    place(C_NOTE, note, 0, true);
    place(C_CHECK, l.check, ring, cw.report.dump_path[0] != 0);
    place(C_CHIP, l.chip, ring, cw.reported != 0);
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
        RECT area = { 0, (LONG)maxf(0, snap((dip(19.5f) - tm.tmHeight) / 2)), (LONG)cw_w(note), (LONG)cw_h(note) };
        SendMessageW(g.child[C_NOTE], EM_SETRECTNP, 0, (LPARAM)&area);
        InvalidateRect(g.child[C_NOTE], NULL, TRUE);
    }
}

/* Rich edit may wrap a line more than DirectWrite: then its box grows by those lines (once per
 * layout). True when it did. */
bool fit_fields()
{
    bool grew = false;
    struct { int child; cw_text *text; float line, *extra; } fields[] = {
        { C_CAUSE, cw.lay.t_cause, tdip(13 * 1.45f), &cw.extra_cause },
        { C_DETAILS, cw.details_open ? cw.lay.t_details : nullptr, tdip(12 * 1.55f), &cw.extra_details },
    };
    for (auto &f : fields)
    {
        if (!g.child[f.child] || !f.text) continue;
        LONG lines = (LONG)SendMessageW(g.child[f.child], EM_GETLINECOUNT, 0, 0);
        float extra = maxf(0, (lines - (LONG)metrics(f.text).lines) * f.line);
        if (extra > *f.extra) { *f.extra = extra; grew = true; }
    }
    return grew;
}

/* Lays the window out again (a new DPI, the details, the zip) and sizes it; the top-left stays
 * unless `where` gives the new rectangle (WM_DPICHANGED's). */
void relayout(const RECT *where)
{
    UINT dpi = window_dpi(g.hwnd);
    cw.scale = dpi / 96.0f;
    make_formats();
    lay_out();
    make_note_font();
    SIZE size = window_size(dpi);
    RECT now;
    GetWindowRect(g.hwnd, &now);
    int x = where ? where->left : now.left, y = where ? where->top : now.top;
    SetWindowPos(g.hwnd, NULL, x, y, size.cx, size.cy, SWP_NOZORDER | SWP_NOACTIVATE);
    place_children();
    if (fit_fields()) relayout(where);
    g.drawn_valid = false;
    refresh(NULL);
    InvalidateRect(g.hwnd, NULL, FALSE);
}

void set_name(HWND h, const wchar_t *name)
{
    if (g.acc && h) g.acc->SetHwndPropStr(h, (DWORD)OBJID_CLIENT, CHILDID_SELF, PROPID_ACC_NAME, name);
}

/* What assistive technology reads of the controls, as MSAA role and state (Dynamic Annotation):
 * the controls are comctl32 v6's from an activation context of our own, whose class names come
 * out versioned ("6.0.x!Button"), so the proxies that know Button, Edit and Static by name don't
 * take them. The state is read when asked: focus, enabled, checked, and for "Show details"
 * expanded or collapsed. The rich edit fields describe themselves. */
struct ControlState : IAccPropServer
{
    ULONG refs = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **out) override
    {
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IAccPropServer)) { *out = this; AddRef(); return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }   /* static: never freed */
    HRESULT STDMETHODCALLTYPE GetPropValue(const BYTE *identity, DWORD length, MSAAPROPID id, VARIANT *value, BOOL *got) override
    {
        *got = FALSE;
        HWND h = NULL;
        DWORD object = 0, child = 0;
        if (!g.acc || FAILED(g.acc->DecomposeHwndIdentityString(identity, length, &h, &object, &child))) return S_OK;
        int which = -1;
        for (int i = 0; i < C_COUNT; i++) if (g.child[i] == h) which = i;
        if (which < 0) return S_OK;
        value->vt = VT_I4;
        if (id == PROPID_ACC_ROLE)
        {
            value->lVal = which == C_CHECK ? ROLE_SYSTEM_CHECKBUTTON : which == C_NOTE ? ROLE_SYSTEM_TEXT
                        : which == C_HEAD ? ROLE_SYSTEM_STATICTEXT : ROLE_SYSTEM_PUSHBUTTON;
            *got = TRUE;
        }
        else if (id == PROPID_ACC_STATE)
        {
            LONG state = which == C_HEAD ? STATE_SYSTEM_READONLY : STATE_SYSTEM_FOCUSABLE;
            if (GetFocus() == h) state |= STATE_SYSTEM_FOCUSED;
            if (!IsWindowEnabled(h)) state |= STATE_SYSTEM_UNAVAILABLE;
            if (which == C_CHECK && SendMessageW(h, BM_GETCHECK, 0, 0) == BST_CHECKED) state |= STATE_SYSTEM_CHECKED;
            if (which == C_TOGGLE) state |= cw.details_open ? STATE_SYSTEM_EXPANDED : STATE_SYSTEM_COLLAPSED;
            if (which == C_REPORT && !cw.reported) state |= STATE_SYSTEM_DEFAULT;
            value->lVal = state;
            *got = TRUE;
        }
        return S_OK;
    }
};
ControlState control_state;

/* The error line and the details are read-only rich edit controls over their drawn text, so they
 * can be selected, copied and read by screen readers (rich edit also finds a font for characters
 * the font lacks). Their font, colours and line spacing are the drawn text's; rich edit puts the
 * extra space of exact line spacing above the text, so the control sits that much higher. */
void style_field(HWND h, HFONT font, cw_color fg, cw_color bg, float line)
{
    SendMessageW(h, WM_SETFONT, (WPARAM)font, FALSE);
    SendMessageW(h, EM_SETBKGNDCOLOR, 0, (LPARAM)to_colorref(bg));
    CHARFORMAT2W cf = {};
    cf.cbSize = sizeof cf;
    cf.dwMask = CFM_COLOR;
    cf.crTextColor = to_colorref(fg);
    SendMessageW(h, EM_SETCHARFORMAT, SCF_ALL, (LPARAM)&cf);
    HDC dc = GetDC(h);
    int ppi = GetDeviceCaps(dc, LOGPIXELSY);
    ReleaseDC(h, dc);
    PARAFORMAT2 pf = {};
    pf.cbSize = sizeof pf;
    pf.dwMask = PFM_LINESPACING;
    pf.bLineSpacingRule = 4;   /* exactly dyLineSpacing twips */
    pf.dyLineSpacing = (LONG)lroundf(line * 1440 / (ppi ? ppi : 96));
    SendMessageW(h, EM_SETSEL, 0, -1);
    SendMessageW(h, EM_SETPARAFORMAT, 0, (LPARAM)&pf);
    SendMessageW(h, EM_SETSEL, 0, 0);
    SendMessageW(h, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, 0);
}

/* How much higher than the drawn text a field sits: the half-leading. */
float field_raise(HWND h, HFONT font, float line)
{
    HDC dc = GetDC(h);
    HGDIOBJ old = SelectObject(dc, font);
    TEXTMETRICW tm;
    GetTextMetricsW(dc, &tm);
    SelectObject(dc, old);
    ReleaseDC(h, dc);
    return maxf(0, snap((line - tm.tmHeight) / 2));
}

LRESULT CALLBACK field_proc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    WNDPROC original = (WNDPROC)GetWindowLongPtrW(h, GWLP_USERDATA);
    if (m == WM_GETDLGCODE)
    {
        /* Tab, Enter and Esc go to the window, as from the note */
        LRESULT code = CallWindowProcW(original, h, m, w, l);
        return code & ~DLGC_WANTALLKEYS;
    }
    return CallWindowProcW(original, h, m, w, l);
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
    char zip[1100];
    cw_zip_path(zip, sizeof zip);
    bool dump = cw.can_include_dump && g.child[C_CHECK] && SendMessageW(g.child[C_CHECK], BM_GETCHECK, 0, 0) == BST_CHECKED;
    uint64_t size = 0;
    if (cw_write_zip(zip, dump, &size)) { strcpy_s(cw.chip_path, zip); cw.chip_size = size; }
    else
    {
        /* no zip: the log itself, to drag */
        strcpy_s(cw.chip_path, cw.report.log_path);
        WIN32_FILE_ATTRIBUTE_DATA a;
        cw.chip_size = GetFileAttributesExW(g.report.log_path, GetFileExInfoStandard, &a) ? ((ULONG64)a.nFileSizeHigh << 32) | a.nFileSizeLow : 0;
    }
    wcscpy_s(g.chip_path, W(cw.chip_path));
    static wchar_t note[4096];
    static char note_utf8[16384], url[8192];
    GetWindowTextW(g.child[C_NOTE], note, _countof(note));
    to_utf8(note, note_utf8, sizeof note_utf8);
    cw_issue_url(note_utf8, url, sizeof url);
    ShellExecuteW(g.hwnd, L"open", W(url), NULL, NULL, SW_SHOWNORMAL);
    SetCursor(old);

    cw.reported = 1;
    SetWindowTextW(g.child[C_REPORT], W(cw.t[S_REPORTED]));
    char size_text[32], name[1400];
    cw_format_size(cw.chip_size, size_text, sizeof size_text);
    snprintf(name, sizeof name, "%s, %s. %s", cw_file_name(cw.chip_path), size_text, cw.t[S_DRAG]);
    SetWindowTextW(g.child[C_CHIP], W(name));
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
        SetTextColor(dc, to_colorref(cw.pal.mut));
        SetBkMode(dc, TRANSPARENT);
        RECT r;
        SendMessageW(h, EM_GETRECT, 0, (LPARAM)&r);
        const wchar_t *placeholder = W(cw.t[S_NOTE]);
        TextOutW(dc, r.left, r.top, placeholder, (int)wcslen(placeholder));
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
    cw.details_open = !cw.details_open;
    SetWindowTextW(g.child[C_TOGGLE], W(cw.t[cw.details_open ? S_HIDE_DETAILS : S_SHOW_DETAILS]));
    relayout(NULL);
    NotifyWinEvent(EVENT_OBJECT_STATECHANGE, g.child[C_TOGGLE], OBJID_CLIENT, CHILDID_SELF);
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
        SetTextColor((HDC)w, to_colorref(cw.pal.fg));
        SetBkColor((HDC)w, to_colorref(cw.pal.panel));
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
        RECT note = rect_of(cw.lay.note);
        if (PtInRect(&note, at)) SetFocus(g.child[C_NOTE]);
        return 0;
    }
    case DM_GETDEFID:
        return cw.reported ? 0 : MAKELRESULT(ID_REPORT, DC_HASDEFID);
    case WM_COMMAND:
    {
        int id = LOWORD(w), code = HIWORD(w);
        if (id == IDCANCEL || (id == ID_CLOSE && code == BN_CLICKED)) { DestroyWindow(hwnd); return 0; }
        if ((id == IDOK || id == ID_REPORT) && code == BN_CLICKED && !cw.reported) { report_crash(); return 0; }
        if (id == ID_TOGGLE && code == BN_CLICKED) { toggle_details(); return 0; }
        if (id == ID_CHIP && code == BN_CLICKED) { open_folder(); return 0; }
        if (id == ID_NOTE && (code == EN_SETFOCUS || code == EN_KILLFOCUS || code == EN_CHANGE))
        {
            if (code == EN_CHANGE) InvalidateRect(g.child[C_NOTE], NULL, TRUE);
            refresh(NULL);
            return 0;
        }
        if ((id == ID_CAUSE || id == ID_DETAILS) && (code == EN_SETFOCUS || code == EN_KILLFOCUS)) { refresh(NULL); return 0; }
        if (id == ID_CHECK && code == BN_CLICKED) NotifyWinEvent(EVENT_OBJECT_STATECHANGE, g.child[C_CHECK], OBJID_CLIENT, CHILDID_SELF);
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
        cw.extra_cause = cw.extra_details = 0;
        relayout((RECT *)l);
        return 0;
    case WM_SETTINGCHANGE:
    case WM_SYSCOLORCHANGE:
    case WM_THEMECHANGED:
        apply_theme();
        if (text_scale() != cw.text) { cw.text = text_scale(); cw.extra_cause = cw.extra_details = 0; }
        relayout(NULL);
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
    g.mark = svg_path(g.d2d, cw_github_mark);
    float dashes[2] = { 4, 3 };
    g.d2d->CreateStrokeStyle(D2D1::StrokeStyleProperties(D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT,
        D2D1_LINE_JOIN_MITER, 10.0f, D2D1_DASH_STYLE_CUSTOM, 0.0f), dashes, 2, &g.dashed);
    return true;
}

void stop_graphics()
{
    cw_release_layout(&canvas);
    release_formats();
    release(g.brush); release(g.rt); release(g.bitmap); release(g.dashed); release(g.mark);
    release(g.fonts); release(g.wic); release(g.dw); release(g.d2d);
}

/* The watcher's report as crash_window.c has it (UTF-8), and its parts (cw_prepare). */
void prepare(const crash_report_t *r)
{
    g.report = *r;
    static cw_report c;
    memset(&c, 0, sizeof c);
    c.language = r->language == CRASH_CHINESE ? CW_CHINESE : CW_ENGLISH;
    to_utf8(r->error, c.error, sizeof c.error);
    c.exit_code = r->exit_code;
    to_utf8(r->version, c.version, sizeof c.version);
    to_utf8(r->game_build, c.game_build, sizeof c.game_build);
    to_utf8(r->gpu, c.gpu, sizeof c.gpu);
    c.mods = r->mods;
    c.play_seconds = r->play_seconds;
    to_utf8(r->log_path, c.log_path, sizeof c.log_path);
    to_utf8(r->dump_path, c.dump_path, sizeof c.dump_path);
    c.dump_size = r->dump_size;
    c.year = r->crashed.wYear; c.month = r->crashed.wMonth; c.day = r->crashed.wDay;
    c.hour = r->crashed.wHour; c.minute = r->crashed.wMinute; c.second = r->crashed.wSecond;
    c.monitor[0] = r->monitor.left; c.monitor[1] = r->monitor.top;
    c.monitor[2] = r->monitor.right - r->monitor.left; c.monitor[3] = r->monitor.bottom - r->monitor.top;
    c.activate = r->activate;
    cw.chip_hint = S_DRAG;
    cw_prepare(&c);
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
    gather_system(r);
    cw_details_text(&cw.report, &cw.system, cw.report.language, cw.details, sizeof cw.details);
    CoCreateInstance(__uuidof(CAccPropServices), NULL, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g.acc));
    const void *font_data;
    DWORD font_size, font_count = 0;
    if (resource_data(FONT_REGULAR, &font_data, &font_size)) g.gdi_font = AddFontMemResourceEx((void *)font_data, font_size, NULL, &font_count);
    if (resource_data(FONT_MONO, &font_data, &font_size)) g.gdi_mono = AddFontMemResourceEx((void *)font_data, font_size, NULL, &font_count);
    cw.text = text_scale();
    bool rich = system_library(L"msftedit.dll") != NULL;

    WNDCLASSEXW wc = { sizeof wc };
    wc.lpfnWndProc = window_proc;
    wc.hInstance = GetModuleHandleW(NULL);
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    wc.hIcon = LoadIconW(wc.hInstance, MAKEINTRESOURCEW(32512));
    wc.lpszClassName = L"OniMoonoCrashWindow";
    RegisterClassExW(&wc);

    RECT area = work_area(r->monitor);
    int cx = (area.left + area.right) / 2, cy = (area.top + area.bottom) / 2;
    g.hwnd = CreateWindowExW(kExStyle, wc.lpszClassName, W(cw_title), kStyle, cx - 200, cy - 150, 400, 300, NULL, NULL, wc.hInstance, NULL);
    if (!g.hwnd) { stop_graphics(); OleUninitialize(); return; }
    UINT dpi = window_dpi(g.hwnd);
    cw.scale = dpi / 96.0f;
    apply_theme();
    set_icons(dpi);

    /* In the order Tab goes through them. */
    static wchar_t error[1024], details[2048];
    wcscpy_s(error, W(cw.error));
    wcscpy_s(details, W(cw.details));
    g.child[C_HEAD] = make_child(L"STATIC", W(cw.t[S_HEADLINE]), WS_VISIBLE | SS_OWNERDRAW, ID_HEAD);
    const DWORD field = WS_TABSTOP | ES_MULTILINE | ES_READONLY;
    g.child[C_CAUSE] = rich ? make_child(MSFTEDIT_CLASS, error, WS_VISIBLE | field, ID_CAUSE) : make_child(L"STATIC", error, WS_VISIBLE | SS_OWNERDRAW, ID_CAUSE);
    g.child[C_TOGGLE] = make_child(L"BUTTON", W(cw.t[S_SHOW_DETAILS]), WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON | BS_NOTIFY, ID_TOGGLE);
    g.child[C_DETAILS] = rich ? make_child(MSFTEDIT_CLASS, details, field, ID_DETAILS) : make_child(L"STATIC", details, SS_OWNERDRAW, ID_DETAILS);
    g.child[C_NOTE] = make_child(L"EDIT", L"", WS_VISIBLE | WS_TABSTOP | ES_MULTILINE | ES_AUTOVSCROLL, ID_NOTE);
    g.child[C_CHECK] = make_child(L"BUTTON", L"", WS_TABSTOP | BS_AUTOCHECKBOX | BS_NOTIFY, ID_CHECK);
    g.child[C_CHIP] = make_child(L"BUTTON", L"", WS_TABSTOP | BS_PUSHBUTTON | BS_NOTIFY, ID_CHIP);
    g.child[C_CLOSE] = make_child(L"BUTTON", W(cw.t[S_CLOSE]), WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON | BS_NOTIFY, ID_CLOSE);
    g.child[C_REPORT] = make_child(L"BUTTON", W(cw.t[S_REPORT]), WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON | BS_NOTIFY, ID_REPORT);
    g.note_proc = (WNDPROC)SetWindowLongPtrW(g.child[C_NOTE], GWLP_WNDPROC, (LONG_PTR)note_proc);
    g.chip_proc = (WNDPROC)SetWindowLongPtrW(g.child[C_CHIP], GWLP_WNDPROC, (LONG_PTR)chip_proc);
    if (rich)
        for (int i : { C_CAUSE, C_DETAILS })
            SetWindowLongPtrW(g.child[i], GWLP_USERDATA, SetWindowLongPtrW(g.child[i], GWLP_WNDPROC, (LONG_PTR)field_proc));
    if (g.acc)
    {
        MSAAPROPID props[2] = { PROPID_ACC_ROLE, PROPID_ACC_STATE };
        for (int i : { C_HEAD, C_TOGGLE, C_NOTE, C_CHECK, C_CHIP, C_CLOSE, C_REPORT })
            g.acc->SetHwndPropServer(g.child[i], (DWORD)OBJID_CLIENT, CHILDID_SELF, props, 2, &control_state, ANNO_THIS);
    }
    SendMessageW(g.child[C_NOTE], EM_LIMITTEXT, 3000, 0);
    set_name(g.child[C_NOTE], W(cw.t[S_NOTE]));
    if (cw.report.dump_path[0])
    {
        char size[32], label[200];
        cw_format_size(cw.report.dump_size, size, sizeof size);
        snprintf(label, sizeof label, cw.t[S_INCLUDE_DUMP], size);
        SetWindowTextW(g.child[C_CHECK], W(label));
        SendMessageW(g.child[C_CHECK], BM_SETCHECK, cw.include_dump ? BST_CHECKED : BST_UNCHECKED, 0);
        EnableWindow(g.child[C_CHECK], cw.can_include_dump);
    }

    make_formats();
    lay_out();
    make_note_font();
    SIZE size = window_size(dpi);
    SetWindowPos(g.hwnd, NULL, cx - size.cx / 2, cy - size.cy / 2, size.cx, size.cy, SWP_NOZORDER | SWP_NOACTIVATE);
    place_children();
    if (fit_fields()) relayout(NULL);
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
    if (g.gdi_mono) RemoveFontMemResourceEx(g.gdi_mono);
    if (g.cause_font) DeleteObject(g.cause_font);
    if (g.mono_font) DeleteObject(g.mono_font);
    release(g.acc);
    stop_graphics();
    OleUninitialize();
}

/* --crash-dialog-render OUT.png [...] (cw_render_args): the window as it would show (at Windows'
 * text size, from Accessibility > Text size), with a drawn title bar, from fixed sample facts, to
 * a PNG; for scripts/check-native-crash.sh also the zip and the issue's URL. */
extern "C" int crash_dialog_render(int argc, wchar_t **argv)
{
    static char *args[64];
    if (argc > 64) return 2;
    for (int i = 0; i < argc; i++)
    {
        int n = WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, NULL, 0, NULL, NULL);
        args[i] = (char *)calloc(n > 0 ? (size_t)n : 1, 1);
        if (!args[i]) return 1;
        if (n > 0) WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, args[i], n, NULL, NULL);
    }
    static cw_render_options o;
    static cw_report r;
    if (!cw_render_args(argc, args, 1, &o, &r)) return 2;

    load_api();
    if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) return 1;
    cw_render_state(&o, &r, 1);
    if (!start_graphics()) return 1;
    g.forced_theme = true;
    g.forced_dark = o.dark != 0;
    cw.pal = palette_for(g.forced_dark, false);
    if (o.zip_out)
    {
        uint64_t size = 0;
        if (!cw_write_zip(o.zip_out, cw.can_include_dump, &size)) return 3;
    }
    if (o.url_out)
    {
        static char url[8192];
        cw_issue_url(o.note, url, sizeof url);
        FILE *f = cw_open(o.url_out, "wb");
        bool ok = f && fwrite(url, 1, strlen(url), f) == strlen(url);
        if (f && fclose(f) != 0) ok = false;
        if (!ok) return 3;
    }
    make_formats();
    lay_out();
    cw_look look = {};
    look.checked = cw.include_dump;
    look.note_empty = 1;
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
        if (SUCCEEDED(g.wic->CreateStream(&stream)) && SUCCEEDED(stream->InitializeFromFilename(W(o.out), GENERIC_WRITE))
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
