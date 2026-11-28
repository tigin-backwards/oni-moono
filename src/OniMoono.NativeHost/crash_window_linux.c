/* The crash window of the Linux player (ARCHITECTURE §7, "Native crashes", "The crash window").
 *
 * The watcher (crash_linux.c) starts itself again in place as `OniMoono --crash-window FD` once
 * it has written the record, with the report in FD: a fresh image, with no game libraries and
 * Steam's overlay left out. The window is Windows' (crash_window.c lays it out and draws it, in
 * the same palette, fonts, strings and measures), drawn here without the GPU, as the driver may
 * be what crashed: into memory by a small rasterizer of our own (coverage-accumulating, so
 * edges are antialiased as Direct2D's are), with SDL3_ttf's text (FreeType and HarfBuzz) in the
 * embedded Gabarito and Red Hat Mono and fontconfig's CJK font after them, then copied to an
 * SDL 3 window surface (X11's XShm; on Wayland through XWayland when there is one).
 *
 * Linux has no standard controls to borrow, so the controls are drawn and handled here: the
 * focus ring, hover and press, the check box, Tab order, Enter (Report on GitHub), Esc (Close),
 * text selection with the mouse and Shift+arrows, Ctrl+A and Ctrl+C in the error line and the
 * details, and the note's own editing (caret, selection, clipboard, input methods through SDL's
 * text input). There is no drag and drop of the zip: "Show in folder" and "Copy path" instead,
 * and a copy of the zip in Downloads, which a browser's file picker shows without hidden folders.
 *
 * SDL 3 and SDL3_ttf (BRUTAL's builds, beside this executable) are loaded only here. */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <math.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/utsname.h>
#include <sys/wait.h>
#include <unistd.h>
#include "crash_linux.h"
#include "crash_window.h"
#include "sdl_min.h"

extern char **environ;

/* The embedded fonts, icon and picture (fonts_linux.S). */
extern const unsigned char moono_font_regular[], moono_font_medium[], moono_font_semibold[], moono_font_mono[], moono_icon[], moono_picture[];
extern const unsigned long long moono_font_regular_size, moono_font_medium_size, moono_font_semibold_size, moono_font_mono_size, moono_icon_size,
    moono_picture_size;

/* ---- SDL, loaded ----------------------------------------------------------------------------- */

#define DECLARE(ret, name, params) static ret (*name) params;
MS_SDL_FUNCTIONS(DECLARE)
MS_TTF_FUNCTIONS(DECLARE)
#undef DECLARE

static char exe_dir[PATH_MAX];

/* libSDL3.so.0 and libSDL3_ttf.so.0 from beside the executable, SDL first, so SDL3_ttf's
 * dependency on it resolves to the one loaded. 0, with the reason on stderr, when either is
 * missing. */
static int load_sdl(void)
{
    char path[PATH_MAX + 32];
    ssize_t n = readlink("/proc/self/exe", exe_dir, sizeof exe_dir - 1);
    if (n <= 0) return 0;
    exe_dir[n] = 0;
    char *slash = strrchr(exe_dir, '/');
    if (slash) *slash = 0;
    snprintf(path, sizeof path, "%s/libSDL3.so.0", exe_dir);
    void *sdl = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    snprintf(path, sizeof path, "%s/libSDL3_ttf.so.0", exe_dir);
    void *ttf = sdl ? dlopen(path, RTLD_NOW | RTLD_LOCAL) : NULL;
    if (!sdl || !ttf)
    {
        fprintf(stderr, "[OniMoono] the crash window needs SDL 3 and SDL3_ttf beside the player: %s\n", dlerror());
        return 0;
    }
    int ok = 1;
#define LOAD(ret, name, params) if (!(*(void **)&name = dlsym(sdl, #name))) ok = 0;
#define LOAD_TTF(ret, name, params) if (!(*(void **)&name = dlsym(ttf, #name))) ok = 0;
    MS_SDL_FUNCTIONS(LOAD)
    MS_TTF_FUNCTIONS(LOAD_TTF)
#undef LOAD
#undef LOAD_TTF
    if (!ok) fprintf(stderr, "[OniMoono] the crash window's SDL is too old\n");
    return ok;
}

/* ---- files ----------------------------------------------------------------------------------- */

FILE *cw_open(const char *path, const char *mode) { return fopen(path, mode); }
int cw_seek(FILE *f, uint64_t at) { return fseeko(f, (off_t)at, SEEK_SET) == 0; }

uint64_t cw_file_size(FILE *f)
{
    struct stat s;
    return fstat(fileno(f), &s) == 0 ? (uint64_t)s.st_size : 0;
}

/* ---- fonts ----------------------------------------------------------------------------------- */

/* A font file's ascent and descent per em as DirectWrite takes them: the OS/2 table's typographic
 * values when it says to use them, else its Windows values. */
static void font_metrics(const unsigned char *data, size_t size, float *ascent, float *descent)
{
    *ascent = 0.9f;
    *descent = 0.25f;
    if (size < 12) return;
    unsigned tables = (unsigned)(data[4] << 8 | data[5]);
    unsigned em = 0;
    const unsigned char *os2 = NULL;
    for (unsigned i = 0; i < tables && 12 + 16 * (i + 1) <= size; i++)
    {
        const unsigned char *t = data + 12 + 16 * i;
        uint32_t at = (uint32_t)t[8] << 24 | (uint32_t)t[9] << 16 | (uint32_t)t[10] << 8 | t[11];
        if (at + 80 > size) continue;
        if (memcmp(t, "head", 4) == 0) em = (unsigned)(data[at + 18] << 8 | data[at + 19]);
        if (memcmp(t, "OS/2", 4) == 0) os2 = data + at;
    }
    if (!em || !os2) return;
    unsigned selection = (unsigned)(os2[62] << 8 | os2[63]);
    int16_t typo_ascent = (int16_t)(os2[68] << 8 | os2[69]), typo_descent = (int16_t)(os2[70] << 8 | os2[71]);
    uint16_t win_ascent = (uint16_t)(os2[74] << 8 | os2[75]), win_descent = (uint16_t)(os2[76] << 8 | os2[77]);
    if (selection & 0x80) { *ascent = (float)typo_ascent / em; *descent = (float)-typo_descent / em; }
    else { *ascent = (float)win_ascent / em; *descent = (float)win_descent / em; }
}

static MS_Font *fonts[CW_F_COUNT];
static float font_ascent[CW_F_COUNT], font_descent[CW_F_COUNT];
static float sans_ascent, sans_descent, mono_ascent, mono_descent;
/* Fonts for what ours lack, from fontconfig: CJK (the window's language's: Simplified Chinese,
 * Korean or Japanese glyph forms), Cyrillic (Gabarito has Latin only), then symbols (the check
 * mark). */
enum { FALLBACK_CJK, FALLBACK_CYRILLIC, FALLBACK_SYMBOLS, FALLBACK_COUNT };
static char fallback_path[FALLBACK_COUNT][PATH_MAX];
static MS_Font *fallbacks[CW_F_COUNT][FALLBACK_COUNT];

/* fontconfig's best font for `pattern` (the library loaded here only) into `out`, when it has
 * both characters; 0 otherwise. */
static int find_font(const char *pattern_text, uint32_t a, uint32_t b, char *out, size_t most)
{
    static void *fc;
    if (!fc) fc = dlopen("libfontconfig.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!fc) return 0;
    void *(*init)(void) = (void *(*)(void))dlsym(fc, "FcInitLoadConfigAndFonts");
    void *(*parse)(const unsigned char *) = (void *(*)(const unsigned char *))dlsym(fc, "FcNameParse");
    int (*substitute)(void *, void *, int) = (int (*)(void *, void *, int))dlsym(fc, "FcConfigSubstitute");
    void (*defaults)(void *) = (void (*)(void *))dlsym(fc, "FcDefaultSubstitute");
    void *(*match)(void *, void *, int *) = (void *(*)(void *, void *, int *))dlsym(fc, "FcFontMatch");
    int (*get_string)(void *, const char *, int, unsigned char **) = (int (*)(void *, const char *, int, unsigned char **))dlsym(fc, "FcPatternGetString");
    void (*destroy)(void *) = (void (*)(void *))dlsym(fc, "FcPatternDestroy");
    if (!init || !parse || !substitute || !defaults || !match || !get_string || !destroy) return 0;
    static void *config;
    if (!config) config = init();
    void *pattern = parse((const unsigned char *)pattern_text);
    if (!config || !pattern) return 0;
    substitute(config, pattern, 0 /* FcMatchPattern */);
    defaults(pattern);
    int result = 0;
    void *font = match(config, pattern, &result);
    destroy(pattern);
    if (!font) return 0;
    unsigned char *file = NULL;
    int ok = get_string(font, "file", 0, &file) == 0 && file;
    if (ok) snprintf(out, most, "%s", file);
    destroy(font);
    /* Only a font that has them: fontconfig answers with its best, which may have neither. */
    if (ok)
    {
        MS_Font *probe = TTF_OpenFont(out, 12);
        ok = probe && TTF_FontHasGlyph(probe, a) && TTF_FontHasGlyph(probe, b);
        if (probe) TTF_CloseFont(probe);
    }
    if (!ok) out[0] = 0;
    return ok;
}

/* Whether the window's language has a font for its text. */
static int find_fallbacks(void)
{
    int language = cw.report.language;
    find_font("sans-serif:charset=2713", 0x2713, 0x00B7, fallback_path[FALLBACK_SYMBOLS], PATH_MAX);
    int cyrillic = find_font("sans-serif:lang=ru", 0x0418, 0x0436, fallback_path[FALLBACK_CYRILLIC], PATH_MAX);   /* Иж */
    int cjk = language == CW_KOREAN ? find_font("sans-serif:lang=ko", 0xAC8C, 0xC784, fallback_path[FALLBACK_CJK], PATH_MAX)   /* 게임 */
            : language == CW_JAPANESE ? find_font("sans-serif:lang=ja", 0x30B2, 0x7D42, fallback_path[FALLBACK_CJK], PATH_MAX)   /* ゲ終 */
            : find_font("sans-serif:lang=zh-cn", 0x6E38, 0x620F, fallback_path[FALLBACK_CJK], PATH_MAX);   /* 游戏 */
    return language == CW_RUSSIAN ? cyrillic : language == CW_CHINESE || language == CW_KOREAN || language == CW_JAPANESE ? cjk : 1;
}

/* The text's language for shaping (the CJK fonts' glyph forms). */
static const char *font_language(void)
{
    switch (cw.report.language)
    {
    case CW_CHINESE: return "zh-hans";
    case CW_KOREAN: return "ko";
    case CW_RUSSIAN: return "ru";
    case CW_JAPANESE: return "ja";
    default: return NULL;
    }
}

static void close_fonts(void)
{
    for (int i = 0; i < CW_F_COUNT; i++)
    {
        if (fonts[i]) TTF_CloseFont(fonts[i]);
        fonts[i] = NULL;
        for (int k = 0; k < FALLBACK_COUNT; k++)
        {
            if (fallbacks[i][k]) TTF_CloseFont(fallbacks[i][k]);
            fallbacks[i][k] = NULL;
        }
    }
}

/* Each of the layout's fonts at its size (they change with the scale). */
static int open_fonts(void)
{
    close_fonts();
    for (int i = 0; i < CW_F_COUNT; i++)
    {
        cw_font f = cw_font_of(i);
        const unsigned char *data = f.family == 1 ? moono_font_mono : f.weight == CW_SEMIBOLD ? moono_font_semibold : f.weight == CW_MEDIUM ? moono_font_medium : moono_font_regular;
        size_t size = (size_t)(f.family == 1 ? moono_font_mono_size : f.weight == CW_SEMIBOLD ? moono_font_semibold_size : f.weight == CW_MEDIUM ? moono_font_medium_size : moono_font_regular_size);
        fonts[i] = TTF_OpenFontIO(SDL_IOFromConstMem(data, size), true, f.size);
        if (!fonts[i]) return 0;
        TTF_SetFontHinting(fonts[i], MS_TTF_HINTING_NONE);
        float a, d;
        font_metrics(data, size, &a, &d);
        font_ascent[i] = a;
        font_descent[i] = d;
        /* Russian takes its Cyrillic from the Cyrillic font first, not from a CJK font's. */
        static const int usual[FALLBACK_COUNT] = { FALLBACK_CJK, FALLBACK_CYRILLIC, FALLBACK_SYMBOLS };
        static const int russian[FALLBACK_COUNT] = { FALLBACK_CYRILLIC, FALLBACK_CJK, FALLBACK_SYMBOLS };
        for (int n = 0; n < FALLBACK_COUNT; n++)
        {
            int k = (cw.report.language == CW_RUSSIAN ? russian : usual)[n];
            if (!fallback_path[k][0]) continue;
            fallbacks[i][k] = TTF_OpenFont(fallback_path[k], f.size);
            if (!fallbacks[i][k]) continue;
            TTF_SetFontHinting(fallbacks[i][k], MS_TTF_HINTING_NONE);
            if (f.weight == CW_SEMIBOLD && k != FALLBACK_SYMBOLS) TTF_SetFontStyle(fallbacks[i][k], MS_TTF_STYLE_BOLD);
            TTF_AddFallbackFont(fonts[i], fallbacks[i][k]);
        }
        if (font_language()) TTF_SetFontLanguage(fonts[i], font_language());
    }
    font_metrics(moono_font_regular, (size_t)moono_font_regular_size, &sans_ascent, &sans_descent);
    font_metrics(moono_font_mono, (size_t)moono_font_mono_size, &mono_ascent, &mono_descent);
    return 1;
}

/* ---- text ------------------------------------------------------------------------------------ */

#define LINES_MOST 64

typedef struct
{
    int start, length;     /* bytes of the text */
    float width;           /* with its trailing white space */
    MS_Surface *image;     /* its coverage, white on clear, made when first drawn */
} text_line;

struct cw_text
{
    char *text;
    int font;
    float width, line_height, spacing;
    int wrap, align, count;
    text_line lines[LINES_MOST];
};

static float string_width(int font, const char *s, size_t n)
{
    int w = 0, h = 0;
    if (n == 0) return 0;
    TTF_GetStringSize(fonts[font], s, n, &w, &h);
    return (float)w;
}

static int utf8_next(const char *s, int at)
{
    if (!s[at]) return at;
    at++;
    while ((s[at] & 0xC0) == 0x80) at++;
    return at;
}

static uint32_t utf8_at(const char *s, int at)
{
    const unsigned char *p = (const unsigned char *)s + at;
    if (p[0] < 0x80) return p[0];
    if ((p[0] & 0xE0) == 0xC0) return (uint32_t)(p[0] & 0x1F) << 6 | (p[1] & 0x3F);
    if ((p[0] & 0xF0) == 0xE0) return (uint32_t)(p[0] & 0x0F) << 12 | (uint32_t)(p[1] & 0x3F) << 6 | (p[2] & 0x3F);
    return (uint32_t)(p[0] & 0x07) << 18 | (uint32_t)(p[1] & 0x3F) << 12 | (uint32_t)(p[2] & 0x3F) << 6 | (p[3] & 0x3F);
}

/* CJK, kana and Hangul: DirectWrite breaks a line between any two of these. */
static int is_cjk(uint32_t c) { return (c >= 0x1100 && c <= 0x11FF) || (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xA960 && c <= 0xA97F) || (c >= 0xAC00 && c <= 0xD7FF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x20000 && c <= 0x3FFFF); }

/* Closing punctuation, which no line starts with; opening, which none ends with. */
static int is_closing(uint32_t c) { return c == 0xFF0C || c == 0x3002 || c == 0x3001 || c == 0xFF1B || c == 0xFF1A || c == 0xFF1F || c == 0xFF01 || c == 0xFF09 || c == 0x300D || c == 0x300F || c == 0x3011 || c == 0x3009 || c == 0x300B || c == ')' || c == ',' || c == '.'; }
static int is_opening(uint32_t c) { return c == 0xFF08 || c == 0x300C || c == 0x300E || c == 0x3010 || c == 0x3008 || c == 0x300A || c == '('; }

/* Whether a line may break before the character at `at` (after the one before it), as
 * DirectWrite's line breaking does for these texts: after spaces and hyphens, around CJK. */
static int may_break(const char *s, int prev, int at)
{
    uint32_t a = utf8_at(s, prev), b = utf8_at(s, at);
    if (b == ' ' || is_closing(b) || is_opening(a)) return 0;
    if (a == ' ' || a == '-' || a == '/') return 1;
    return is_cjk(a) || is_cjk(b);
}

static void add_line(cw_text *t, int start, int end)
{
    if (t->count >= LINES_MOST) return;
    text_line *l = &t->lines[t->count++];
    l->start = start;
    l->length = end - start;
    int characters = 0;
    for (int at = start; at < end; at = utf8_next(t->text, at)) characters++;
    l->width = string_width(t->font, t->text + start, (size_t)(end - start)) + t->spacing * (float)characters;
    l->image = NULL;
}

/* The text broken into lines `width` px wide at most: at the last break that fits, else
 * (a word longer than a line) at the last character that fits. */
static void break_lines(cw_text *t)
{
    const char *s = t->text;
    int n = (int)strlen(s);
    t->count = 0;
    for (int para = 0; para <= n;)
    {
        int end = para;
        while (end < n && s[end] != '\n') end++;
        int start = para;
        if (!t->wrap) { add_line(t, start, end); para = end + 1; continue; }
        while (start < end || (start == end && start == para))
        {
            size_t fit = 0;
            int measured = 0;
            int limit = (int)floorf(t->width + 0.5f);
            TTF_MeasureString(fonts[t->font], s + start, (size_t)(end - start), limit, &measured, &fit);
            int cut = start + (int)fit;
            if (cut >= end) { add_line(t, start, end); break; }
            /* the spaces after a line belong to it */
            if (s[cut] == ' ')
            {
                while (cut < end && s[cut] == ' ') cut++;
                add_line(t, start, cut);
                start = cut;
                continue;
            }
            int best = -1;
            for (int at = utf8_next(s, start), prev = start; at <= cut; prev = at, at = utf8_next(s, at))
                if (may_break(s, prev, at)) best = at;
            if (best <= start) best = cut > start ? cut : utf8_next(s, start);
            add_line(t, start, best);
            start = best;
            while (start < end && s[start] == ' ') start++;
        }
        para = end + 1;
    }
    if (t->count == 0) add_line(t, 0, 0);
}

static cw_text *make_text(void *context, const char *text, int font, float width, float line_height, int wrap)
{
    (void)context;
    cw_text *t = (cw_text *)calloc(1, sizeof *t);
    if (!t) return NULL;
    t->text = strdup(text);
    t->font = font;
    t->width = width < 1 ? 1 : width;
    t->line_height = line_height;
    t->wrap = wrap;
    break_lines(t);
    return t;
}

static void free_images(cw_text *t)
{
    for (int i = 0; i < t->count; i++)
        if (t->lines[i].image) { SDL_DestroySurface(t->lines[i].image); t->lines[i].image = NULL; }
}

static void set_spacing(void *context, cw_text *t, float spacing)
{
    (void)context;
    free_images(t);
    t->spacing = spacing;
    break_lines(t);
}

static void set_align(void *context, cw_text *t, int align)
{
    (void)context;
    t->align = align;
}

static cw_metrics text_metrics(void *context, cw_text *t)
{
    (void)context;
    cw_metrics m = { t->line_height * (float)t->count, 0, t->count };
    for (int i = 0; i < t->count; i++) if (t->lines[i].width > m.width) m.width = t->lines[i].width;
    return m;
}

static void free_text(void *context, cw_text *t)
{
    (void)context;
    free_images(t);
    free(t->text);
    free(t);
}

/* ---- the rasterizer -------------------------------------------------------------------------- */

/* The drawing, 0xAARRGGBB (opaque), and the coverage accumulation buffer the shapes are filled
 * through: each edge adds its signed area to the cells it crosses, a row's running sum is the
 * coverage (as font-rs and stb_truetype do). */
static uint32_t *image;
static float *cover;
static int image_w, image_h;
static float offset_y;
static int clip_l, clip_t, clip_r, clip_b;   /* drawing is kept inside this rectangle */

static int ensure_image(int w, int h)
{
    if (image && image_w == w && image_h == h) return 1;
    free(image);
    free(cover);
    image = (uint32_t *)calloc((size_t)w * (size_t)h, 4);
    cover = (float *)calloc((size_t)(w + 2) * (size_t)(h + 1), sizeof(float));
    image_w = w;
    image_h = h;
    return image && cover;
}

static void no_clip(void) { clip_l = 0; clip_t = 0; clip_r = image_w; clip_b = image_h; }

static float minf(float a, float b) { return a < b ? a : b; }
static float maxf(float a, float b) { return a > b ? a : b; }

/* The path being filled, its bounds, and the current point. */
static float path_l, path_t, path_r, path_b;
static float pen_x, pen_y, start_x, start_y;

static void path_reset(void)
{
    path_l = path_t = 1e9f;
    path_r = path_b = -1e9f;
}

static void edge(float x0, float y0, float x1, float y1)
{
    path_l = minf(path_l, minf(x0, x1)); path_r = maxf(path_r, maxf(x0, x1));
    path_t = minf(path_t, minf(y0, y1)); path_b = maxf(path_b, maxf(y0, y1));
    if (y0 == y1) return;
    /* x kept to the drawing: what is left of it counts at its left edge */
    float limit = (float)image_w;
    x0 = minf(maxf(x0, 0), limit);
    x1 = minf(maxf(x1, 0), limit);
    float dir = 1;
    if (y0 > y1) { float t = x0; x0 = x1; x1 = t; t = y0; y0 = y1; y1 = t; dir = -1; }
    float dxdy = (x1 - x0) / (y1 - y0);
    float x = x0;
    if (y0 < 0) { x -= y0 * dxdy; }
    int w = image_w + 2;
    int first = y0 < 0 ? 0 : (int)y0, last = (int)ceilf(y1);
    if (last > image_h) last = image_h;
    for (int row = first; row < last; row++)
    {
        float *line = cover + (size_t)row * (size_t)w;
        float dy = minf((float)row + 1, y1) - maxf((float)row, y0);
        float xnext = x + dxdy * dy;
        float d = dy * dir;
        float a = x < xnext ? x : xnext, b = x < xnext ? xnext : x;
        float a_floor = floorf(a), b_ceil = ceilf(b);
        int ai = (int)a_floor, bi = (int)b_ceil;
        if (ai < 0) ai = 0;
        if (bi > w - 1) bi = w - 1;
        if (bi <= ai + 1)
        {
            float xm = 0.5f * (x + xnext) - a_floor;
            line[ai] += d - d * xm;
            line[ai + 1] += d * xm;
        }
        else
        {
            float s = 1.0f / (b - a);
            float af = a - a_floor;
            float a0 = 0.5f * s * (1 - af) * (1 - af);
            float bf = b - b_ceil + 1;
            float am = 0.5f * s * bf * bf;
            line[ai] += d * a0;
            if (bi == ai + 2) line[ai + 1] += d * (1 - a0 - am);
            else
            {
                float a1 = s * (1.5f - af);
                line[ai + 1] += d * (a1 - a0);
                for (int k = ai + 2; k < bi - 1; k++) line[k] += d * s;
                float a2 = a1 + (float)(bi - ai - 3) * s;
                line[bi - 1] += d * (1 - a2 - am);
            }
            line[bi] += d * am;
        }
        x = xnext;
    }
}

static void move_to(float x, float y) { pen_x = start_x = x; pen_y = y + offset_y; start_y = pen_y; }
static void line_to(float x, float y) { y += offset_y; edge(pen_x, pen_y, x, y); pen_x = x; pen_y = y; }
static void close_path(void) { if (pen_x != start_x || pen_y != start_y) edge(pen_x, pen_y, start_x, start_y); pen_x = start_x; pen_y = start_y; }

static uint32_t blend(uint32_t dst, cw_color c, float coverage)
{
    float a = coverage * c.a;
    if (a <= 0) return dst;
    if (a > 1) a = 1;
    float dr = (float)((dst >> 16) & 255), dg = (float)((dst >> 8) & 255), db = (float)(dst & 255);
    float r = dr + (c.r * 255.0f - dr) * a, g = dg + (c.g * 255.0f - dg) * a, b = db + (c.b * 255.0f - db) * a;
    return 0xFF000000u | (uint32_t)(r + 0.5f) << 16 | (uint32_t)(g + 0.5f) << 8 | (uint32_t)(b + 0.5f);
}

/* The path's coverage composited in `c`, and the accumulator cleared where it was used. */
static void path_fill(cw_color c)
{
    int w = image_w + 2;
    int top = (int)floorf(path_t), bottom = (int)ceilf(path_b), left = (int)floorf(path_l), right = (int)ceilf(path_r) + 2;
    if (top < 0) top = 0;
    if (bottom > image_h) bottom = image_h;
    if (left < 0) left = 0;
    if (right > w) right = w;
    for (int y = top; y < bottom; y++)
    {
        float *line = cover + (size_t)y * (size_t)w;
        float sum = 0;
        int inside = y >= clip_t && y < clip_b;
        for (int x = left; x < right; x++)
        {
            sum += line[x];
            line[x] = 0;
            if (!inside || x < clip_l || x >= clip_r || x >= image_w) continue;
            float coverage = fabsf(sum);
            if (coverage > 1) coverage = 1;
            if (coverage > 0.0005f) image[(size_t)y * (size_t)image_w + (size_t)x] = blend(image[(size_t)y * (size_t)image_w + (size_t)x], c, coverage);
        }
    }
    path_reset();
}

/* A rounded rectangle's outline as a path (clockwise, or counter-clockwise for a hole). */
static void rounded(cw_box b, float r, int reverse)
{
    float w = cw_w(b), h = cw_h(b);
    if (r > w / 2) r = w / 2;
    if (r > h / 2) r = h / 2;
    if (r < 0) r = 0;
    int steps = r <= 0 ? 0 : r < 4 ? 4 : r < 12 ? 8 : 16;
    float pts[4 * 17 + 4][2];
    int n = 0;
    /* corners: top-right, bottom-right, bottom-left, top-left */
    float cx[4] = { b.r - r, b.r - r, b.l + r, b.l + r }, cy[4] = { b.t + r, b.b - r, b.b - r, b.t + r };
    float from[4] = { -1.5707963f, 0, 1.5707963f, 3.1415927f };
    for (int k = 0; k < 4; k++)
    {
        if (steps == 0) { pts[n][0] = k == 0 || k == 1 ? b.r : b.l; pts[n][1] = k == 1 || k == 2 ? b.b : b.t; n++; continue; }
        for (int i = 0; i <= steps; i++)
        {
            float a = from[k] + 1.5707963f * (float)i / (float)steps;
            pts[n][0] = cx[k] + r * cosf(a);
            pts[n][1] = cy[k] + r * sinf(a);
            n++;
        }
    }
    if (reverse)
    {
        move_to(pts[n - 1][0], pts[n - 1][1]);
        for (int i = n - 2; i >= 0; i--) line_to(pts[i][0], pts[i][1]);
    }
    else
    {
        move_to(pts[0][0], pts[0][1]);
        for (int i = 1; i < n; i++) line_to(pts[i][0], pts[i][1]);
    }
    close_path();
}

/* A filled polygon of a thick line (flat caps, as Direct2D's default). */
static void thick(float x0, float y0, float x1, float y1, float width)
{
    float dx = x1 - x0, dy = y1 - y0, length = sqrtf(dx * dx + dy * dy);
    if (length <= 0) return;
    float nx = -dy / length * width / 2, ny = dx / length * width / 2;
    move_to(x0 + nx, y0 + ny);
    line_to(x1 + nx, y1 + ny);
    line_to(x1 - nx, y1 - ny);
    line_to(x0 - nx, y0 - ny);
    close_path();
}


static void set_offset(void *context, float y) { (void)context; offset_y = y; }

static void fill(void *context, cw_box b, cw_color c)
{
    (void)context;
    path_reset();
    move_to(b.l, b.t); line_to(b.r, b.t); line_to(b.r, b.b); line_to(b.l, b.b); close_path();
    path_fill(c);
}

static void fill_round(void *context, cw_box b, float radius, cw_color c)
{
    (void)context;
    path_reset();
    rounded(b, radius, 0);
    path_fill(c);
}

/* A point `s` px along a rounded rectangle's outline from the end of its top-left corner,
 * clockwise. */
static void along(cw_box b, float r, float s, float *x, float *y)
{
    float w = cw_w(b) - 2 * r, h = cw_h(b) - 2 * r, arc = 1.5707963f * r;
    float segments[8] = { w, arc, h, arc, w, arc, h, arc };
    float total = 2 * w + 2 * h + 4 * arc;
    s = fmodf(s, total);
    if (s < 0) s += total;
    for (int i = 0; i < 8; i++)
    {
        if (s <= segments[i] || i == 7)
        {
            float t = segments[i] > 0 ? s / segments[i] : 0;
            switch (i)
            {
            case 0: *x = b.l + r + w * t; *y = b.t; return;
            case 2: *x = b.r; *y = b.t + r + h * t; return;
            case 4: *x = b.r - r - w * t; *y = b.b; return;
            case 6: *x = b.l; *y = b.b - r - h * t; return;
            default:
            {
                float cxs[4] = { b.r - r, b.r - r, b.l + r, b.l + r }, cys[4] = { b.t + r, b.b - r, b.b - r, b.t + r };
                float from = -1.5707963f + 1.5707963f * (float)(i / 2);
                float a = from + 1.5707963f * t;
                *x = cxs[i / 2] + r * cosf(a);
                *y = cys[i / 2] + r * sinf(a);
                return;
            }
            }
        }
        s -= segments[i];
    }
}

static void stroke_round(void *context, cw_box b, float radius, float width, cw_color c, int dashed)
{
    (void)context;
    path_reset();
    float half = width / 2;
    if (!dashed)
    {
        cw_box outer = { b.l - half, b.t - half, b.r + half, b.b + half }, inner = { b.l + half, b.t + half, b.r - half, b.b - half };
        rounded(outer, radius + half, 0);
        if (cw_w(inner) > 0 && cw_h(inner) > 0) rounded(inner, maxf(0, radius - half), 1);
        path_fill(c);
        return;
    }
    /* Dashes of 4 and gaps of 3 times the width, each a strip along the outline. */
    float r = maxf(0, minf(radius, minf(cw_w(b), cw_h(b)) / 2));
    float total = 2 * (cw_w(b) - 2 * r) + 2 * (cw_h(b) - 2 * r) + 4 * 1.5707963f * r;
    float on = 4 * width, off = 3 * width;
    for (float s = 0; s < total; s += on + off)
    {
        float e = minf(s + on, total);
        int steps = (int)ceilf((e - s) / 1.0f);
        if (steps < 1) steps = 1;
        float lx[64][2], rx[64][2];
        if (steps > 62) steps = 62;
        for (int i = 0; i <= steps; i++)
        {
            float u = s + (e - s) * (float)i / (float)steps, x, y, x2, y2;
            along(b, r, u, &x, &y);
            along(b, r, u + 0.01f, &x2, &y2);
            float dx = x2 - x, dy = y2 - y, length = sqrtf(dx * dx + dy * dy);
            if (length <= 0) { dx = 1; dy = 0; length = 1; }
            float nx = -dy / length * half, ny = dx / length * half;
            lx[i][0] = x + nx; lx[i][1] = y + ny;
            rx[i][0] = x - nx; rx[i][1] = y - ny;
        }
        move_to(lx[0][0], lx[0][1]);
        for (int i = 1; i <= steps; i++) line_to(lx[i][0], lx[i][1]);
        for (int i = steps; i >= 0; i--) line_to(rx[i][0], rx[i][1]);
        close_path();
    }
    path_fill(c);
}

static void draw_line(void *context, float x0, float y0, float x1, float y1, float width, cw_color c)
{
    (void)context;
    path_reset();
    thick(x0, y0, x1, y1, width);
    path_fill(c);
}

static void circle(float x, float y, float radius)
{
    int steps = 24;
    move_to(x + radius, y);
    for (int i = 1; i < steps; i++) line_to(x + radius * cosf(6.2831853f * (float)i / (float)steps), y + radius * sinf(6.2831853f * (float)i / (float)steps));
    close_path();
}

static void dot(void *context, float x, float y, float radius, cw_color c)
{
    (void)context;
    path_reset();
    circle(x, y, radius);
    path_fill(c);
}

/* The GitHub mark through cw_svg_path: curves flattened, at k times its 16 x 16 box. */
typedef struct { float x, y, k, px, py; } mark_at;

static void mark_begin(void *context, float x, float y) { mark_at *m = (mark_at *)context; m->px = x; m->py = y; move_to(m->x + x * m->k, m->y + y * m->k); }
static void mark_line(void *context, float x, float y) { mark_at *m = (mark_at *)context; m->px = x; m->py = y; line_to(m->x + x * m->k, m->y + y * m->k); }

static void mark_cubic(void *context, float x1, float y1, float x2, float y2, float x, float y)
{
    mark_at *m = (mark_at *)context;
    float x0 = m->px, y0 = m->py;
    for (int i = 1; i <= 8; i++)
    {
        float t = (float)i / 8, u = 1 - t;
        float bx = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x;
        float by = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y;
        line_to(m->x + bx * m->k, m->y + by * m->k);
    }
    m->px = x;
    m->py = y;
}

/* An SVG arc (to its centre form, SVG 1.1 F.6.5), flattened. */
static void mark_arc(void *context, float rx, float ry, float rotation, int large, int sweep, float x, float y)
{
    mark_at *m = (mark_at *)context;
    float x0 = m->px, y0 = m->py, phi = rotation * 3.1415927f / 180, c = cosf(phi), s = sinf(phi);
    float dx = (x0 - x) / 2, dy = (y0 - y) / 2, x1 = c * dx + s * dy, y1 = -s * dx + c * dy;
    rx = fabsf(rx); ry = fabsf(ry);
    float lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
    if (lambda > 1) { rx *= sqrtf(lambda); ry *= sqrtf(lambda); }
    float num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1, den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
    float coef = den > 0 ? sqrtf(maxf(0, num / den)) : 0;
    if (large == sweep) coef = -coef;
    float cxp = coef * rx * y1 / ry, cyp = -coef * ry * x1 / rx;
    float cx = c * cxp - s * cyp + (x0 + x) / 2, cy = s * cxp + c * cyp + (y0 + y) / 2;
    float a0 = atan2f((y1 - cyp) / ry, (x1 - cxp) / rx), a1 = atan2f((-y1 - cyp) / ry, (-x1 - cxp) / rx), da = a1 - a0;
    if (sweep && da < 0) da += 6.2831853f;
    if (!sweep && da > 0) da -= 6.2831853f;
    for (int i = 1; i <= 24; i++)
    {
        float a = a0 + da * (float)i / 24;
        float px = cx + rx * cosf(a) * c - ry * sinf(a) * s, py = cy + rx * cosf(a) * s + ry * sinf(a) * c;
        line_to(m->x + px * m->k, m->y + py * m->k);
    }
    m->px = x;
    m->py = y;
}

static void mark_end(void *context, int closed) { (void)context; (void)closed; close_path(); }

static void mark(void *context, float x, float y, float k, cw_color c)
{
    (void)context;
    static const cw_path_sink sink = { mark_begin, mark_line, mark_cubic, mark_arc, mark_end };
    mark_at m = { x, y, k, 0, 0 };
    path_reset();
    cw_svg_path(cw_github_mark, &sink, &m);
    path_fill(c);
}

/* Moo.ico's image of `size` px (its PNG entries), else its nearest larger one scaled down. */
static MS_Surface *icon_image(int size)
{
    const unsigned char *d = moono_icon;
    size_t n = (size_t)moono_icon_size;
    if (n < 6) return NULL;
    int count = d[4] | d[5] << 8, best = -1, best_size = 0;
    for (int i = 0; i < count && 6 + 16 * (size_t)(i + 1) <= n; i++)
    {
        int s = d[6 + 16 * i] ? d[6 + 16 * i] : 256;
        if ((best < 0) || (s >= size && (best_size < size || s < best_size)) || (best_size < size && s > best_size)) { best = i; best_size = s; }
    }
    if (best < 0) return NULL;
    const unsigned char *e = d + 6 + 16 * best;
    uint32_t length = (uint32_t)e[8] | (uint32_t)e[9] << 8 | (uint32_t)e[10] << 16 | (uint32_t)e[11] << 24;
    uint32_t at = (uint32_t)e[12] | (uint32_t)e[13] << 8 | (uint32_t)e[14] << 16 | (uint32_t)e[15] << 24;
    if (at + length > n) return NULL;
    MS_Surface *png = SDL_LoadPNG_IO(SDL_IOFromConstMem(d + at, length), true);
    if (!png) return NULL;
    MS_Surface *argb = SDL_ConvertSurface(png, MS_SDL_PIXELFORMAT_ARGB8888);
    SDL_DestroySurface(png);
    if (!argb || argb->w == size) return argb;
    MS_Surface *scaled = SDL_CreateSurface(size, size, MS_SDL_PIXELFORMAT_ARGB8888);
    if (scaled) SDL_BlitSurfaceScaled(argb, NULL, scaled, NULL, MS_SDL_SCALEMODE_LINEAR);
    SDL_DestroySurface(argb);
    return scaled;
}

static void icon(void *context, float x, float y, int size)
{
    (void)context;
    MS_Surface *s = icon_image(size);
    if (!s) return;
    int ox = (int)x, oy = (int)(y + offset_y);
    for (int j = 0; j < s->h; j++)
        for (int i = 0; i < s->w; i++)
        {
            int px = ox + i, py = oy + j;
            if (px < clip_l || px >= clip_r || py < clip_t || py >= clip_b) continue;
            uint32_t v = ((const uint32_t *)((const unsigned char *)s->pixels + (size_t)j * (size_t)s->pitch))[i];
            cw_color c = { (float)((v >> 16) & 255) / 255, (float)((v >> 8) & 255) / 255, (float)(v & 255) / 255, 1 };
            image[(size_t)py * (size_t)image_w + (size_t)px] = blend(image[(size_t)py * (size_t)image_w + (size_t)px], c, (float)(v >> 24) / 255);
        }
    SDL_DestroySurface(s);
}

/* The window's picture (crash-moo.png, a sad Gassy Moo), fitted into the box and centred: SDL's
 * PNG loader, as for the icon, then halved with linear filtering down to twice the size and scaled
 * linearly from there, which comes close to WIC's Fant scaling on Windows. */
static void picture(void *context, cw_box b)
{
    (void)context;
    static MS_Surface *source;
    if (!source)
    {
        MS_Surface *png = SDL_LoadPNG_IO(SDL_IOFromConstMem(moono_picture, (size_t)moono_picture_size), true);
        if (!png) return;
        source = SDL_ConvertSurface(png, MS_SDL_PIXELFORMAT_ARGB8888);
        SDL_DestroySurface(png);
        if (!source) return;
    }
    float k = fminf(cw_w(b) / (float)source->w, cw_h(b) / (float)source->h);
    int w = (int)fmaxf(1, cw_snap((float)source->w * k)), h = (int)fmaxf(1, cw_snap((float)source->h * k));
    MS_Surface *s = source;
    while (s->w >= 2 * w * 2 && s->h >= 2 * h * 2)
    {
        MS_Surface *half = SDL_CreateSurface(s->w / 2, s->h / 2, MS_SDL_PIXELFORMAT_ARGB8888);
        if (!half) break;
        SDL_BlitSurfaceScaled(s, NULL, half, NULL, MS_SDL_SCALEMODE_LINEAR);
        if (s != source) SDL_DestroySurface(s);
        s = half;
    }
    MS_Surface *scaled = SDL_CreateSurface(w, h, MS_SDL_PIXELFORMAT_ARGB8888);
    if (scaled) SDL_BlitSurfaceScaled(s, NULL, scaled, NULL, MS_SDL_SCALEMODE_LINEAR);
    if (s != source) SDL_DestroySurface(s);
    if (!scaled) return;
    int ox = (int)cw_snap(b.l + (cw_w(b) - (float)w) / 2), oy = (int)cw_snap(b.t + (cw_h(b) - (float)h) / 2 + offset_y);
    for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
        {
            int px = ox + i, py = oy + j;
            if (px < clip_l || px >= clip_r || py < clip_t || py >= clip_b) continue;
            uint32_t v = ((const uint32_t *)((const unsigned char *)scaled->pixels + (size_t)j * (size_t)scaled->pitch))[i];
            if (!(v >> 24)) continue;
            cw_color c = { (float)((v >> 16) & 255) / 255, (float)((v >> 8) & 255) / 255, (float)(v & 255) / 255, 1 };
            image[(size_t)py * (size_t)image_w + (size_t)px] = blend(image[(size_t)py * (size_t)image_w + (size_t)px], c, (float)(v >> 24) / 255);
        }
    SDL_DestroySurface(scaled);
}

/* Text coverage as it is: unhinted (as DirectWrite's natural rendering is), FreeType's coverage
 * blended plainly comes closest to DirectWrite's grayscale text (compared on the renders). */
static float text_alpha(float a) { return a; }

static float line_baseline(const cw_text *t)
{
    float size = cw_font_of(t->font).size, ascent = font_ascent[t->font] * size, descent = font_descent[t->font] * size;
    return (t->line_height - ascent - descent) / 2 + ascent;
}

static void draw_text(void *context, cw_text *t, float x, float y, cw_color c)
{
    (void)context;
    if (!t) return;
    MS_Color white = { 255, 255, 255, 255 };
    float baseline = line_baseline(t);
    int font_ascent_px = TTF_GetFontAscent(fonts[t->font]);
    for (int i = 0; i < t->count; i++)
    {
        text_line *l = &t->lines[i];
        if (l->length == 0) continue;
        float lx = x + (t->align == CW_ALIGN_CENTER ? (t->width - l->width) / 2 : 0);
        float ly = y + offset_y + t->line_height * (float)i + baseline - (float)font_ascent_px;
        if (!l->image)
        {
            MS_Surface *s = TTF_RenderText_Blended(fonts[t->font], t->text + l->start, (size_t)l->length, white);
            if (s && s->format != MS_SDL_PIXELFORMAT_ARGB8888)
            {
                MS_Surface *converted = SDL_ConvertSurface(s, MS_SDL_PIXELFORMAT_ARGB8888);
                SDL_DestroySurface(s);
                s = converted;
            }
            l->image = s;
        }
        MS_Surface *g = l->image;
        if (!g) continue;
        int ox = (int)floorf(lx + 0.5f), oy = (int)floorf(ly + 0.5f);
        /* letter-spacing: the line as shaped, each character's columns moved by the spacing
         * before it (the characters' boundaries from the widths of the line's beginnings) */
        static int shift[8192];
        int columns = g->w < 8192 ? g->w : 8192;
        for (int m = 0; m < columns; m++) shift[m] = 0;
        if (t->spacing != 0)
        {
            const char *s = t->text + l->start;
            int k = 0, from = 0;
            for (int at = utf8_next(s, 0); at <= l->length; at = utf8_next(s, at))
            {
                int to = (int)string_width(t->font, s, (size_t)at);
                for (int m = from; m < to && m < columns; m++) shift[m] = (int)floorf(t->spacing * (float)k + 0.5f);
                from = to > from ? to : from;
                k++;
                if (at >= l->length) break;
            }
            for (int m = from; m < columns; m++) shift[m] = (int)floorf(t->spacing * (float)k + 0.5f);
        }
        for (int j = 0; j < g->h; j++)
        {
            int py = oy + j;
            if (py < clip_t || py >= clip_b) continue;
            const uint32_t *row = (const uint32_t *)((const unsigned char *)g->pixels + (size_t)j * (size_t)g->pitch);
            for (int m = 0; m < columns; m++)
            {
                int px = ox + m + shift[m];
                if (px < clip_l || px >= clip_r) continue;
                float a = (float)(row[m] >> 24) / 255;
                if (a > 0) image[(size_t)py * (size_t)image_w + (size_t)px] = blend(image[(size_t)py * (size_t)image_w + (size_t)px], c, text_alpha(a));
            }
        }
    }
}

static const cw_canvas canvas = {
    NULL, make_text, set_spacing, set_align, text_metrics, free_text, set_offset, fill, fill_round, stroke_round, draw_text, draw_line, dot, mark, icon, picture,
};

/* ---- this machine ---------------------------------------------------------------------------- */

/* The distribution and the kernel: PRETTY_NAME from os-release (the host's, inside Steam's
 * container), uname's release. */
static void os_line(char *out, size_t most)
{
    char name[96] = "Linux";
    const char *files[] = { "/run/host/os-release", "/etc/os-release", "/usr/lib/os-release" };
    for (int i = 0; i < 3; i++)
    {
        FILE *f = fopen(files[i], "r");
        if (!f) continue;
        char line[256];
        int found = 0;
        while (fgets(line, sizeof line, f))
            if (strncmp(line, "PRETTY_NAME=", 12) == 0)
            {
                char *v = line + 12;
                v[strcspn(v, "\r\n")] = 0;
                size_t n = strlen(v);
                if (n >= 2 && (v[0] == '"' || v[0] == '\'')) { v[n - 1] = 0; v++; }
                snprintf(name, sizeof name, "%s", v);
                found = 1;
                break;
            }
        fclose(f);
        if (found) break;
    }
    struct utsname u;
    if (uname(&u) == 0) snprintf(out, most, "%s (Linux %s)", name, u.release);
    else snprintf(out, most, "%s", name);
}

/* The game's renderer as far as its first '/' (as on Windows), with NVIDIA's driver version from
 * its kernel module when that is loaded. */
static void gpu_line(const char *renderer, char *out, size_t most)
{
    char wanted[128];
    snprintf(wanted, sizeof wanted, "%s", renderer);
    char *slash = strchr(wanted, '/');
    if (slash) *slash = 0;
    out[0] = 0;
    if (!wanted[0]) return;
    char version[48] = "";
    FILE *f = strstr(wanted, "NVIDIA") ? fopen("/proc/driver/nvidia/version", "r") : NULL;
    if (f)
    {
        char line[256];
        if (fgets(line, sizeof line, f))
        {
            char *at = strstr(line, "Kernel Module");
            if (at)
            {
                at += 13;
                while (*at == ' ') at++;
                size_t n = strcspn(at, " \t\r\n");
                if (n < sizeof version) { memcpy(version, at, n); version[n] = 0; }
            }
        }
        fclose(f);
    }
    snprintf(out, most, version[0] ? "%s (%s)" : "%s", wanted, version);
}

static void gather_system(void)
{
    os_line(cw.system.os, sizeof cw.system.os);
    gpu_line(cw.report.gpu, cw.system.gpu, sizeof cw.system.gpu);
    long pages = sysconf(_SC_PHYS_PAGES), page = sysconf(_SC_PAGESIZE);
    cw.system.ram_gb = pages > 0 && page > 0 ? ((uint64_t)pages * (uint64_t)page + (512ull << 20)) >> 30 : 0;
}

/* ---- rendering to a PNG ---------------------------------------------------------------------- */

/* The drawing as RGBA rows, for the PNG. */
static int save_png(const char *path)
{
    unsigned char *rgba = (unsigned char *)malloc((size_t)image_w * (size_t)image_h * 4);
    if (!rgba) return 0;
    for (size_t i = 0; i < (size_t)image_w * (size_t)image_h; i++)
    {
        uint32_t v = image[i];
        rgba[4 * i] = (unsigned char)(v >> 16); rgba[4 * i + 1] = (unsigned char)(v >> 8); rgba[4 * i + 2] = (unsigned char)v; rgba[4 * i + 3] = 255;
    }
    int ok = cw_write_png(path, rgba, image_w, image_h, image_w * 4);
    free(rgba);
    return ok;
}

static int start_text(void)
{
    if (!load_sdl() || !TTF_Init()) return 0;
    if (!find_fallbacks())
    {
        /* No font for the language's text: the English strings rather than boxes. */
        cw.report.language = CW_ENGLISH;
        cw.t = cw_strings[0];
    }
    return 1;
}

int crash_dialog_render(int argc, char **argv)
{
    static cw_render_options o;
    static cw_report r;
    if (!cw_render_args(argc, argv, 0, &o, &r)) return 2;
    cw_render_state(&o, &r, 0);
    cw.links = 1;
    cw.chip_hint = S_ATTACH;
    if (getenv("ONI_MOONO_CRASH_DECK")) { cw.links = 0; cw.chip_hint = S_DECK; }
    if (!start_text()) return 1;
    cw_details_text(&cw.report, &cw.system, cw.report.language, cw.details, sizeof cw.details);
    if (o.zip_out)
    {
        uint64_t size = 0;
        if (!cw_write_zip(o.zip_out, cw.can_include_dump, &size)) return 3;
    }
    if (o.url_out)
    {
        static char url[8192];
        if (cw.blocked) snprintf(url, sizeof url, "%s", cw_blocked_url);   /* no issue: the README's link */
        else cw_issue_url(o.note, url, sizeof url);
        FILE *f = fopen(o.url_out, "wb");
        int ok = f && fwrite(url, 1, strlen(url), f) == strlen(url);
        if (f && fclose(f) != 0) ok = 0;
        if (!ok) return 3;
    }
    if (!open_fonts()) return 1;
    cw_lay_out(&canvas);
    cw_look look;
    memset(&look, 0, sizeof look);
    look.checked = cw.include_dump;
    look.note_empty = 1;
    float caption = cw_snap(cw_dip(32));
    if (!ensure_image(cw.lay.width, cw.lay.height + (int)caption)) return 1;
    no_clip();
    path_reset();
    offset_y = 0;
    cw_draw_caption(&canvas, caption);
    cw_draw(&canvas, &look, caption);
    int status = save_png(o.out) ? 0 : 1;
    cw_release_layout(&canvas);
    close_fonts();
    return status;
}

/* ---- the window ------------------------------------------------------------------------------ */

static MS_Window *window;
static cw_look look;
static int focus = C_NOTE, pressed_part = -1, hot_part = -1, keyboard_used;
static int gaming;                     /* Steam Deck's Gaming Mode (gamescope) */
static int closing;

/* The note, and the selection in the focused text (the note, the error line or the details):
 * byte offsets, the anchor where it started. */
static char note[12288];
static int caret, anchor, dragging;
static float note_scroll;
static cw_text *note_layout;
static uint64_t caret_shown;

static int selectable(int part) { return part == C_CAUSE || part == C_DETAILS || part == C_NOTE; }

static cw_text *text_for(int part)
{
    return part == C_CAUSE ? cw.lay.t_cause : part == C_DETAILS ? cw.lay.t_details : part == C_NOTE ? note_layout : NULL;
}

/* Where a part's text starts. */
static void text_origin(int part, float *x, float *y)
{
    const cw_layout *l = &cw.lay;
    if (part == C_CAUSE) { *x = l->cause.l; *y = l->cause.t; }
    else if (part == C_DETAILS) { *x = l->details.l + l->line + cw_dip(12); *y = l->details.t + l->line + cw_dip(10); }
    else { *x = l->note.l + l->line + cw_dip(10); *y = l->note.t + l->line + cw_dip(8) - note_scroll; }
}

/* The byte offset in `t` nearest the point. */
static int hit(cw_text *t, float x, float y)
{
    if (!t || t->count == 0) return 0;
    int line = (int)floorf(y / t->line_height);
    if (line < 0) return 0;
    if (line >= t->count) return t->lines[t->count - 1].start + t->lines[t->count - 1].length;
    text_line *l = &t->lines[line];
    int best = l->start;
    float best_d = 1e9f;
    for (int at = l->start;; at = utf8_next(t->text, at))
    {
        if (at > l->start + l->length) break;
        float w = string_width(t->font, t->text + l->start, (size_t)(at - l->start));
        float d = fabsf(w - x);
        if (d < best_d) { best_d = d; best = at; }
        if (at >= l->start + l->length) break;
    }
    /* a line that wrapped: its trailing spaces are not a place to stop */
    return best;
}

/* The line holding byte `at` and its x there. */
static int line_of(cw_text *t, int at, float *x)
{
    for (int i = 0; i < t->count; i++)
    {
        text_line *l = &t->lines[i];
        if (at <= l->start + l->length && (i + 1 == t->count || at < t->lines[i + 1].start))
        {
            *x = string_width(t->font, t->text + l->start, (size_t)(at - l->start));
            return i;
        }
    }
    *x = 0;
    return 0;
}

static void draw_selection(int part, cw_text *t)
{
    if (focus != part || caret == anchor || !t) return;
    float ox, oy;
    text_origin(part, &ox, &oy);
    int a = caret < anchor ? caret : anchor, b = caret < anchor ? anchor : caret;
    cw_color highlight = cw.pal.dark ? cw_rgb(0x6C8EBF, 0.45f) : cw_rgb(0x3D6FB8, 0.28f);
    for (int i = 0; i < t->count; i++)
    {
        text_line *l = &t->lines[i];
        int s = a > l->start ? a : l->start, e = b < l->start + l->length ? b : l->start + l->length;
        if (s >= e) continue;
        float x0 = string_width(t->font, t->text + l->start, (size_t)(s - l->start)), x1 = string_width(t->font, t->text + l->start, (size_t)(e - l->start));
        cw_box box = { ox + x0, oy + t->line_height * (float)i, ox + x1, oy + t->line_height * (float)(i + 1) };
        fill(NULL, box, highlight);
    }
}

/* The overlay crash_window.c calls: the selection, and the note's own text and caret. */
static void overlay(int part, cw_box b)
{
    if (part == C_NOTE)
    {
        const cw_layout *l = &cw.lay;
        int saved[4] = { clip_l, clip_t, clip_r, clip_b };
        clip_l = (int)(b.l + l->line); clip_r = (int)(b.r - l->line);
        clip_t = (int)(b.t + l->line + offset_y); clip_b = (int)(b.b - l->line + offset_y);
        if (note_layout && note[0])
        {
            float x, y;
            text_origin(C_NOTE, &x, &y);
            draw_selection(C_NOTE, note_layout);
            draw_text(NULL, note_layout, x, y, cw.pal.fg);
        }
        if (focus == C_NOTE && note_layout && ((SDL_GetTicks() - caret_shown) / 530) % 2 == 0)
        {
            float x, y, cx;
            text_origin(C_NOTE, &x, &y);
            int line = line_of(note_layout, caret, &cx);
            float size = cw_font_of(CW_F_BODY).size, top = y + note_layout->line_height * (float)line + line_baseline(note_layout) - font_ascent[CW_F_BODY] * size;
            cw_box bar = { cw_snap(x + cx), cw_snap(top), cw_snap(x + cx) + maxf(1, floorf(cw.scale)), cw_snap(top + (font_ascent[CW_F_BODY] + font_descent[CW_F_BODY]) * size) };
            fill(NULL, bar, cw.pal.fg);
        }
        clip_l = saved[0]; clip_t = saved[1]; clip_r = saved[2]; clip_b = saved[3];
    }
    else draw_selection(part, text_for(part));
}

static void layout_note(void)
{
    if (note_layout) free_text(NULL, note_layout);
    const cw_layout *l = &cw.lay;
    note_layout = make_text(NULL, note, CW_F_BODY, cw_w(l->note) - 2 * l->line - 2 * cw_dip(10), cw_tdip(13 * 1.5f), 1);
    /* keep the caret's line in view */
    float cx;
    int line = line_of(note_layout, caret, &cx);
    float view = cw_h(l->note) - 2 * l->line - 2 * cw_dip(8), top = note_layout->line_height * (float)line;
    if (top < note_scroll) note_scroll = top;
    if (top + note_layout->line_height > note_scroll + view) note_scroll = top + note_layout->line_height - view;
    if (note_scroll < 0) note_scroll = 0;
}

static float pixel_density = 1;

static void present(void)
{
    MS_Surface *s = SDL_GetWindowSurface(window);
    if (!s || !ensure_image(cw.lay.width, cw.lay.height)) return;
    no_clip();
    path_reset();
    look.focus[C_HEAD] = 0;
    for (int i = 0; i < C_COUNT; i++) look.focus[i] = i == focus;
    look.cues = keyboard_used;
    look.checked = cw.include_dump;
    look.note_empty = note[0] == 0;
    for (int i = 0; i < C_COUNT; i++) { look.hot[i] = i == hot_part; look.pressed[i] = i == pressed_part && i == hot_part; }
    cw_draw(&canvas, &look, 0);
    int w = s->w < image_w ? s->w : image_w, h = s->h < image_h ? s->h : image_h;
    for (int y = 0; y < h; y++) memcpy((unsigned char *)s->pixels + (size_t)y * (size_t)s->pitch, image + (size_t)y * (size_t)image_w, (size_t)w * 4);
    SDL_UpdateWindowSurface(window);
}

/* Gaming Mode: gamescope stretches the focused window to fill the screen, so a window drawn at
 * scale 1 (440 px wide on a Deck's 1280×800) comes out about 2.5 times as large and blurred. There
 * the window is drawn at the largest scale, in quarter steps, at which its tallest state (details
 * open, the zip shown) still fits the screen, so gamescope scales it little. Worked out once. */
static float gaming_scale(void)
{
    static float chosen;
    if (chosen > 0) return chosen;
    chosen = 1;
    MS_Rect bounds;
    if (!SDL_GetDisplayUsableBounds(SDL_GetPrimaryDisplay(), &bounds) || bounds.w <= 0 || bounds.h <= 0) return chosen;
    int details = cw.details_open, reported = cw.reported;
    cw.details_open = cw.reported = 1;
    cw.scale = 1;
    open_fonts();
    cw_lay_out(&canvas);
    float w = (float)cw.lay.width, h = (float)cw.lay.height;
    cw.details_open = details;
    cw.reported = reported;
    if (w <= 0 || h <= 0) return chosen;
    float fit = fminf((float)bounds.w * 0.95f / w, (float)bounds.h * 0.95f / h);
    fit = floorf(fit * 4) / 4;
    chosen = fit < 1 ? 1 : fit > 3 ? 3 : fit;
    return chosen;
}

static void relayout(void)
{
    cw.scale = SDL_GetWindowDisplayScale(window);
    if (cw.scale <= 0) cw.scale = 1;
    if (gaming) cw.scale = gaming_scale();
    const char *forced = getenv("ONI_MOONO_CRASH_WINDOW_SCALE");
    if (forced) cw.scale = (float)atof(forced);
    pixel_density = SDL_GetWindowPixelDensity(window);
    if (pixel_density <= 0) pixel_density = 1;
    open_fonts();
    cw_lay_out(&canvas);
    layout_note();
    SDL_SetWindowSize(window, (int)ceilf((float)cw.lay.width / pixel_density), (int)ceilf((float)cw.lay.height / pixel_density));
    MS_Rect area = { (int)(cw.lay.note.l / pixel_density), (int)(cw.lay.note.t / pixel_density), (int)(cw_w(cw.lay.note) / pixel_density), (int)(cw_h(cw.lay.note) / pixel_density) };
    SDL_SetTextInputArea(window, &area, 0);
}

static cw_box part_box(int part)
{
    const cw_layout *l = &cw.lay;
    cw_box none = { 0, 0, 0, 0 };
    switch (part)
    {
    case C_CAUSE: return l->cause;
    case C_TOGGLE: return l->toggle;
    case C_DETAILS: return cw.details_open ? l->details : none;
    case C_NOTE: return l->note;
    case C_CHECK: return cw.report.dump_path[0] ? l->check : none;
    case C_CHIP: return cw.reported ? l->chip : none;
    case C_FOLDER: return cw.reported && cw.links ? l->folder : none;
    case C_COPY: return cw.reported && cw.links ? l->copy : none;
    case C_CLOSE: return l->close;
    case C_REPORT: return l->report;
    default: return none;
    }
}

static int focusable(int part)
{
    cw_box b = part_box(part);
    if (cw_w(b) <= 0) return 0;
    if (part == C_CHECK && !cw.can_include_dump) return 0;
    if (part == C_REPORT && cw.reported) return 0;
    return 1;
}

static int part_at(float x, float y)
{
    for (int i = C_COUNT - 1; i >= 0; i--)
    {
        cw_box b = part_box(i);
        if (cw_w(b) > 0 && x >= b.l && x < b.r && y >= b.t && y < b.b) return i;
    }
    return -1;
}

static void set_focus(int part)
{
    if (part == focus) return;
    focus = part;
    caret = anchor = 0;
    if (part == C_NOTE) { caret = anchor = (int)strlen(note); SDL_StartTextInput(window); }
    else SDL_StopTextInput(window);
    caret_shown = SDL_GetTicks();
    layout_note();
}

static void move_focus(int step)
{
    for (int i = 1; i <= C_COUNT; i++)
    {
        int part = ((focus + step * i) % C_COUNT + C_COUNT) % C_COUNT;
        if (focusable(part)) { set_focus(part); return; }
    }
}

/* ---- opening things -------------------------------------------------------------------------- */

/* The environment for programs the window starts: the user's library path and preloads, as Steam
 * keeps them in SYSTEM_* (Steam's runtime libraries can break a desktop's browser), and none of
 * the game's dump or overlay settings. */
static char **child_environment(void)
{
    static char *kept[512];
    static char library[8192], preload[8192];
    int n = 0;
    const char *system_library = getenv("SYSTEM_LD_LIBRARY_PATH"), *system_preload = getenv("SYSTEM_LD_PRELOAD");
    for (char **e = environ; *e && n < 500; e++)
    {
        if (strncmp(*e, "LD_LIBRARY_PATH=", 16) == 0 || strncmp(*e, "LD_PRELOAD=", 11) == 0 || strncmp(*e, "DOTNET_DbgEnableMiniDump=", 25) == 0
            || strncmp(*e, "DOTNET_EnableCrashReport", 24) == 0 || strncmp(*e, "DOTNET_DbgMiniDump", 18) == 0 || strncmp(*e, "ONI_MOONO_DUMP_ENV=", 19) == 0) continue;
        kept[n++] = *e;
    }
    if (system_library && system_library[0]) { snprintf(library, sizeof library, "LD_LIBRARY_PATH=%s", system_library); kept[n++] = library; }
    if (system_preload && system_preload[0]) { snprintf(preload, sizeof preload, "LD_PRELOAD=%s", system_preload); kept[n++] = preload; }
    kept[n] = NULL;
    return kept;
}

/* A program started and left to run (its end is collected later); 1 when it started. */
static int start_program(char *const args[])
{
    pid_t pid;
    if (posix_spawnp(&pid, args[0], NULL, NULL, args, child_environment()) != 0) return 0;
    return 1;
}

static int on_path(const char *name)
{
    const char *path = getenv("PATH");
    if (!path) return 0;
    char full[PATH_MAX];
    for (const char *at = path; *at;)
    {
        size_t n = strcspn(at, ":");
        snprintf(full, sizeof full, "%.*s/%s", (int)n, at, name);
        if (access(full, X_OK) == 0) return 1;
        at += n;
        if (*at) at++;
    }
    return 0;
}

/* ONI_MOONO_CRASH_OPEN=FILE (tests): what would be opened, a line each, instead of opening it. */
static int recorded(const char *what, const char *value)
{
    const char *file = getenv("ONI_MOONO_CRASH_OPEN");
    if (!file || !file[0]) return 0;
    FILE *f = fopen(file, "a");
    if (f) { fprintf(f, "%s %s\n", what, value); fclose(f); }
    return 1;
}

/* The issue in the browser: Steam's own in Gaming Mode (steam://openurl/), else the Steam
 * Runtime's URL opener inside its container, else xdg-open, else SDL's. */
static void open_url(const char *url)
{
    static char steam[9000];
    if (gaming) { snprintf(steam, sizeof steam, "steam://openurl/%s", url); url = steam; }
    if (recorded("url", url)) return;
    char *runtime[] = { (char *)"steam-runtime-urlopen", (char *)url, NULL };
    char *xdg[] = { (char *)"xdg-open", (char *)url, NULL };
    if (on_path("steam-runtime-urlopen") && start_program(runtime)) return;
    if (on_path("xdg-open") && start_program(xdg)) return;
    SDL_OpenURL(url);
}

/* The zip in the file manager, selected (FileManager1.ShowItems over D-Bus: Nautilus, Dolphin,
 * Nemo, Thunar), else its folder opened. */
static void show_in_folder(const char *path)
{
    if (recorded("folder", path)) return;
    static char uri[PATH_MAX * 3 + 16], items[PATH_MAX * 3 + 32], folder[PATH_MAX];
    size_t n = (size_t)snprintf(uri, sizeof uri, "file://");
    for (const unsigned char *c = (const unsigned char *)path; *c && n + 4 < sizeof uri; c++)
    {
        if ((*c >= 'A' && *c <= 'Z') || (*c >= 'a' && *c <= 'z') || (*c >= '0' && *c <= '9') || strchr("/-_.~", *c)) uri[n++] = (char)*c;
        else n += (size_t)snprintf(uri + n, sizeof uri - n, "%%%02X", *c);
    }
    uri[n] = 0;
    snprintf(items, sizeof items, "['%s']", uri);
    char *gdbus[] = { (char *)"gdbus", (char *)"call", (char *)"--session", (char *)"--dest", (char *)"org.freedesktop.FileManager1",
                      (char *)"--object-path", (char *)"/org/freedesktop/FileManager1", (char *)"--method", (char *)"org.freedesktop.FileManager1.ShowItems",
                      items, (char *)"", NULL };
    static char dbus_items[PATH_MAX * 3 + 32];
    snprintf(dbus_items, sizeof dbus_items, "array:string:%s", uri);
    char *dbus_send[] = { (char *)"dbus-send", (char *)"--session", (char *)"--dest=org.freedesktop.FileManager1", (char *)"--type=method_call",
                          (char *)"/org/freedesktop/FileManager1", (char *)"org.freedesktop.FileManager1.ShowItems", dbus_items, (char *)"string:", NULL };
    if (on_path("gdbus") && start_program(gdbus)) return;
    if (on_path("dbus-send") && start_program(dbus_send)) return;
    snprintf(folder, sizeof folder, "%s", path);
    char *slash = strrchr(folder, '/');
    if (slash) *slash = 0;
    char *xdg[] = { (char *)"xdg-open", folder, NULL };
    if (on_path("xdg-open")) start_program(xdg);
}

/* The user's Downloads folder (xdg-user-dirs' XDG_DOWNLOAD_DIR, else ~/Downloads); 0 when none. */
static int downloads(char *out, size_t most)
{
    const char *home = getenv("HOME"), *config = getenv("XDG_CONFIG_HOME");
    if (!home) return 0;
    char dirs[PATH_MAX];
    if (config && config[0]) snprintf(dirs, sizeof dirs, "%s/user-dirs.dirs", config);
    else snprintf(dirs, sizeof dirs, "%s/.config/user-dirs.dirs", home);
    out[0] = 0;
    FILE *f = fopen(dirs, "r");
    if (f)
    {
        char line[PATH_MAX];
        while (fgets(line, sizeof line, f))
            if (strncmp(line, "XDG_DOWNLOAD_DIR=\"", 18) == 0)
            {
                char *v = line + 18;
                v[strcspn(v, "\"")] = 0;
                if (strncmp(v, "$HOME", 5) == 0) snprintf(out, most, "%s%s", home, v + 5);
                else snprintf(out, most, "%s", v);
            }
        fclose(f);
    }
    if (!out[0]) snprintf(out, most, "%s/Downloads", home);
    struct stat s;
    return stat(out, &s) == 0 && S_ISDIR(s.st_mode) && access(out, W_OK) == 0;
}

static int copy_file(const char *from, const char *to)
{
    FILE *in = fopen(from, "rb"), *out = in ? fopen(to, "wb") : NULL;
    int ok = in && out;
    static char buffer[1 << 16];
    for (size_t got; ok && (got = fread(buffer, 1, sizeof buffer, in)) > 0;) ok = fwrite(buffer, 1, got, out) == got;
    if (in) fclose(in);
    if (out && fclose(out) != 0) ok = 0;
    if (!ok) remove(to);
    return ok;
}

static void report_crash(void)
{
    MS_Cursor *wait = SDL_CreateSystemCursor(MS_SDL_SYSTEM_CURSOR_WAIT);
    if (wait) SDL_SetCursor(wait);
    char zip[1100];
    cw_zip_path(zip, sizeof zip);
    uint64_t size = 0;
    if (cw_write_zip(zip, cw.can_include_dump && cw.include_dump, &size))
    {
        snprintf(cw.chip_path, sizeof cw.chip_path, "%s", zip);
        cw.chip_size = size;
        /* A copy in Downloads, where a browser's file picker looks first. */
        char folder[PATH_MAX], copy[PATH_MAX + 128];
        if (downloads(folder, sizeof folder))
        {
            snprintf(copy, sizeof copy, "%s/%s", folder, cw_file_name(zip));
            copy_file(zip, copy);
        }
    }
    else
    {
        /* no zip: the log itself */
        snprintf(cw.chip_path, sizeof cw.chip_path, "%s", cw.report.log_path);
        struct stat s;
        cw.chip_size = stat(cw.report.log_path, &s) == 0 ? (uint64_t)s.st_size : 0;
    }
    static char url[8192];
    cw_issue_url(note, url, sizeof url);
    open_url(url);
    MS_Cursor *arrow = SDL_CreateSystemCursor(MS_SDL_SYSTEM_CURSOR_DEFAULT);
    if (arrow) SDL_SetCursor(arrow);
    cw.reported = 1;
    relayout();
    set_focus(C_CHIP);
}

/* ---- input ----------------------------------------------------------------------------------- */

static void activate(int part)
{
    switch (part)
    {
    case C_TOGGLE: cw.details_open = !cw.details_open; relayout(); break;
    case C_CHECK: if (cw.can_include_dump) cw.include_dump = !cw.include_dump; break;
    case C_CHIP: case C_FOLDER: if (!gaming) show_in_folder(cw.chip_path); break;
    case C_COPY:
        if (!recorded("copy", cw.chip_path)) SDL_SetClipboardText(cw.chip_path);
        cw.copied = 1;
        relayout();
        break;
    case C_CLOSE: closing = 1; break;
    case C_REPORT: if (cw.blocked) open_url(cw_blocked_url); else if (!cw.reported) report_crash(); break;
    default: break;
    }
}

static void selection_text(char *out, size_t most)
{
    cw_text *t = text_for(focus);
    out[0] = 0;
    if (!t || caret == anchor) return;
    int a = caret < anchor ? caret : anchor, b = caret < anchor ? anchor : caret;
    size_t n = (size_t)(b - a) < most - 1 ? (size_t)(b - a) : most - 1;
    memcpy(out, t->text + a, n);
    out[n] = 0;
}

static void note_replace(const char *text)
{
    int a = caret < anchor ? caret : anchor, b = caret < anchor ? anchor : caret;
    size_t n = strlen(text), length = strlen(note);
    /* EM_LIMITTEXT's 3,000 characters, as Windows' note box */
    if (length - (size_t)(b - a) + n >= 3000 * 3 || length - (size_t)(b - a) + n >= sizeof note) return;
    memmove(note + a + n, note + b, length - (size_t)b + 1);
    memcpy(note + a, text, n);
    caret = anchor = a + (int)n;
}

static int previous_char(const char *s, int at)
{
    if (at <= 0) return 0;
    at--;
    while (at > 0 && (s[at] & 0xC0) == 0x80) at--;
    return at;
}

static void key(const MS_KeyboardEvent *k)
{
    int ctrl = (k->mod & MS_SDL_KMOD_CTRL) != 0, shift = (k->mod & MS_SDL_KMOD_SHIFT) != 0;
    uint32_t code = k->key;
    if (code == MS_SDLK_TAB) { keyboard_used = 1; move_focus(shift ? -1 : 1); return; }
    if (code == MS_SDLK_ESCAPE) { closing = 1; return; }
    if ((code == MS_SDLK_RETURN || code == MS_SDLK_KP_ENTER) && !(focus == C_NOTE && ctrl))
    {
        keyboard_used = 1;
        /* Enter: the focused button, else Report on GitHub (the default button) */
        if (focus == C_TOGGLE || focus == C_CHIP || focus == C_FOLDER || focus == C_COPY || focus == C_CLOSE) activate(focus);
        else if (!cw.reported) activate(C_REPORT);
        return;
    }
    if (code == MS_SDLK_SPACE && focus != C_NOTE) { keyboard_used = 1; activate(focus); return; }
    if (!selectable(focus)) return;
    cw_text *t = text_for(focus);
    const char *s = t ? t->text : "";
    int length = (int)strlen(s), moved = -1;
    if (ctrl && code == MS_SDLK_A) { anchor = 0; caret = length; return; }
    if (ctrl && (code == MS_SDLK_C || (code == MS_SDLK_X && focus == C_NOTE)))
    {
        static char copied[12288];
        selection_text(copied, sizeof copied);
        if (copied[0]) SDL_SetClipboardText(copied);
        if (code == MS_SDLK_X) { note_replace(""); layout_note(); }
        return;
    }
    if (focus == C_NOTE && ctrl && code == MS_SDLK_V)
    {
        char *pasted = SDL_GetClipboardText();
        if (pasted) { note_replace(pasted); SDL_free(pasted); layout_note(); }
        return;
    }
    if (code == MS_SDLK_LEFT) moved = caret != anchor && !shift ? (caret < anchor ? caret : anchor) : previous_char(s, caret);
    else if (code == MS_SDLK_RIGHT) moved = caret != anchor && !shift ? (caret > anchor ? caret : anchor) : utf8_next(s, caret);
    else if (code == MS_SDLK_HOME || code == MS_SDLK_END || code == MS_SDLK_UP || code == MS_SDLK_DOWN)
    {
        float x;
        int line = t ? line_of(t, caret, &x) : 0;
        if (!t) moved = 0;
        else if (code == MS_SDLK_HOME) moved = ctrl ? 0 : t->lines[line].start;
        else if (code == MS_SDLK_END) moved = ctrl ? length : t->lines[line].start + t->lines[line].length - (line + 1 < t->count && t->lines[line].length && s[t->lines[line].start + t->lines[line].length - 1] == ' ' ? 1 : 0);
        else
        {
            int to = line + (code == MS_SDLK_UP ? -1 : 1);
            moved = to < 0 ? 0 : to >= t->count ? length : hit(t, x, t->line_height * ((float)to + 0.5f));
        }
    }
    else if (focus == C_NOTE && code == MS_SDLK_BACKSPACE)
    {
        if (caret == anchor) anchor = previous_char(note, caret);
        note_replace("");
        layout_note();
        caret_shown = SDL_GetTicks();
        return;
    }
    else if (focus == C_NOTE && code == MS_SDLK_DELETE)
    {
        if (caret == anchor) anchor = utf8_next(note, caret);
        note_replace("");
        layout_note();
        return;
    }
    else if (focus == C_NOTE && (code == MS_SDLK_RETURN || code == MS_SDLK_KP_ENTER) && ctrl)
    {
        note_replace("\n");
        layout_note();
        return;
    }
    if (moved >= 0)
    {
        caret = moved;
        if (!shift) anchor = caret;
        caret_shown = SDL_GetTicks();
        if (focus == C_NOTE) layout_note();
    }
}

static void pointer_moved(float x, float y)
{
    int part = part_at(x, y);
    int pointer = part == C_TOGGLE || part == C_CHIP || part == C_FOLDER || part == C_COPY || part == C_CLOSE || (part == C_REPORT && !cw.reported)
                  || (part == C_CHECK && cw.can_include_dump);
    if (part == C_CHECK && !cw.can_include_dump) part = -1;
    if (part == C_REPORT && cw.reported) part = -1;
    hot_part = selectable(part) ? -1 : part;
    static int shown = -1;
    int want = selectable(part) ? MS_SDL_SYSTEM_CURSOR_TEXT : pointer ? MS_SDL_SYSTEM_CURSOR_POINTER : MS_SDL_SYSTEM_CURSOR_DEFAULT;
    if (want != shown) { MS_Cursor *c = SDL_CreateSystemCursor(want); if (c) SDL_SetCursor(c); shown = want; }
    if (dragging && selectable(focus))
    {
        float ox, oy;
        text_origin(focus, &ox, &oy);
        caret = hit(text_for(focus), x - ox, y - oy);
        if (focus == C_NOTE) layout_note();
    }
}

static int word_char(char c) { return c && c != ' ' && c != '\n'; }

static void pointer_down(float x, float y, int clicks)
{
    int part = part_at(x, y);
    keyboard_used = 0;
    if (selectable(part))
    {
        set_focus(part);
        float ox, oy;
        text_origin(part, &ox, &oy);
        cw_text *t = text_for(part);
        caret = anchor = hit(t, x - ox, y - oy);
        if (t && clicks == 2)
        {
            while (anchor > 0 && word_char(t->text[anchor - 1])) anchor--;
            while (word_char(t->text[caret])) caret++;
        }
        else if (t && clicks >= 3) { anchor = 0; caret = (int)strlen(t->text); }
        dragging = clicks < 2;
        caret_shown = SDL_GetTicks();
        return;
    }
    if (part >= 0 && focusable(part)) { set_focus(part); pressed_part = part; }
}

static void pointer_up(float x, float y)
{
    dragging = 0;
    int part = part_at(x, y);
    if (pressed_part >= 0 && part == pressed_part) activate(part);
    pressed_part = -1;
}

/* Gaming Mode on a Steam Deck (gamescope): its window manager shows only windows that name a
 * Steam game. */
static void mark_steam_game(void)
{
    void *display = SDL_GetPointerProperty(SDL_GetWindowProperties(window), MS_SDL_PROP_WINDOW_X11_DISPLAY_POINTER, NULL);
    int64_t id = SDL_GetNumberProperty(SDL_GetWindowProperties(window), MS_SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
    void *x11 = display && id ? dlopen("libX11.so.6", RTLD_NOW | RTLD_LOCAL) : NULL;
    if (!x11) return;
    unsigned long (*intern)(void *, const char *, int) = (unsigned long (*)(void *, const char *, int))dlsym(x11, "XInternAtom");
    int (*change)(void *, unsigned long, unsigned long, unsigned long, int, int, const unsigned char *, int) =
        (int (*)(void *, unsigned long, unsigned long, unsigned long, int, int, const unsigned char *, int))dlsym(x11, "XChangeProperty");
    int (*flush)(void *) = (int (*)(void *))dlsym(x11, "XFlush");
    if (!intern || !change || !flush) return;
    const char *app = getenv("SteamAppId");
    long value = app && atol(app) > 0 && atol(app) != 769 ? atol(app) : 457140;   /* Oxygen Not Included */
    change(display, (unsigned long)id, intern(display, "STEAM_GAME", 0), 6 /* XA_CARDINAL */, 32, 0 /* PropModeReplace */, (const unsigned char *)&value, 1);
    flush(display);
}

static int system_dark(void)
{
    const char *forced = getenv("ONI_MOONO_CRASH_THEME");
    if (forced) return strcmp(forced, "dark") == 0;
    return SDL_GetSystemTheme() == MS_SDL_SYSTEM_THEME_DARK;
}

/* High contrast: GNOME's accessibility setting (or a high-contrast GTK theme): black or white
 * with yellow or blue, Windows' high-contrast palettes. */
static int system_contrast(void)
{
    const char *theme = getenv("GTK_THEME");
    if (theme && strstr(theme, "HighContrast")) return 1;
    if (!on_path("gsettings")) return 0;
    FILE *p = popen("gsettings get org.gnome.desktop.a11y.interface high-contrast 2>/dev/null", "r");
    if (!p) return 0;
    char answer[16] = "";
    if (!fgets(answer, sizeof answer, p)) answer[0] = 0;
    pclose(p);
    return strncmp(answer, "true", 4) == 0;
}

/* GNOME's text-scaling-factor (KDE's forced font DPI shows in the display scale), 100 to 225%. */
static float text_scale(void)
{
    const char *forced = getenv("ONI_MOONO_CRASH_TEXT_SIZE");
    if (forced) { float v = (float)atof(forced); return v < 1 ? 1 : v > 2.25f ? 2.25f : v; }
    if (!on_path("gsettings")) return 1;
    FILE *p = popen("gsettings get org.gnome.desktop.interface text-scaling-factor 2>/dev/null", "r");
    if (!p) return 1;
    char answer[32] = "";
    if (!fgets(answer, sizeof answer, p)) answer[0] = 0;
    pclose(p);
    float v = (float)atof(answer);
    return v < 1 ? 1 : v > 2.25f ? 2.25f : v;
}

static void apply_theme(void)
{
    int dark = system_dark();
    cw.pal = cw_palette_for(dark);
    if (!getenv("ONI_MOONO_CRASH_THEME") && system_contrast())
    {
        cw_palette p = cw.pal;
        cw_color back = dark ? cw_rgb(0x000000, 1) : cw_rgb(0xFFFFFF, 1), ink = dark ? cw_rgb(0xFFFFFF, 1) : cw_rgb(0x000000, 1);
        p.contrast = 1;
        p.bg = p.caption = p.panel = p.footer = back;
        p.fg = p.fg2 = p.mut = p.border = p.secbd = p.secfg = ink;
        p.pri = dark ? cw_rgb(0xFFFF00, 1) : cw_rgb(0x37006E, 1);
        p.prifg = dark ? cw_rgb(0x000000, 1) : cw_rgb(0xFFFFFF, 1);
        p.hover = dark ? cw_rgb(0x1AEBFF, 1) : cw_rgb(0x00009F, 1);
        p.ring = p.pri;
        cw.pal = p;
    }
}

int crash_window_main(int argc, char **argv)
{
    if (argc < 3) return 1;
    static cw_report r;
    int fd = atoi(argv[2]);
    ssize_t got = read(fd, &r, sizeof r);
    close(fd);
    if (got != (ssize_t)sizeof r) return 1;
    int exit_code = (int)(r.exit_code & 255);
    gaming = (getenv("XDG_CURRENT_DESKTOP") && strcmp(getenv("XDG_CURRENT_DESKTOP"), "gamescope") == 0) || getenv("GAMESCOPE_WAYLAND_DISPLAY") != NULL;
    cw_prepare(&r);
    cw.links = !gaming;
    cw.chip_hint = gaming ? S_DECK : S_ATTACH;
    if (!start_text()) return exit_code;
    gather_system();
    cw_details_text(&cw.report, &cw.system, cw.report.language, cw.details, sizeof cw.details);
    cw_overlay = overlay;

    /* No GPU: SDL's window surface in memory (X11's XShm), not its texture framebuffer. On
     * Wayland through XWayland when there is one; Wayland itself has no software surface, so
     * there Mesa's software OpenGL presents it. */
    SDL_SetHint(MS_SDL_HINT_FRAMEBUFFER_ACCELERATION, "0");
    if (getenv("DISPLAY") && getenv("DISPLAY")[0]) SDL_SetHint(MS_SDL_HINT_VIDEO_DRIVER, "x11");
    else if (getenv("WAYLAND_DISPLAY"))
    {
        SDL_SetHint(MS_SDL_HINT_FRAMEBUFFER_ACCELERATION, "opengles2");
        setenv("LIBGL_ALWAYS_SOFTWARE", "1", 1);
    }
    SDL_SetHint(MS_SDL_HINT_APP_ID, "oni-moono");
    SDL_SetHint(MS_SDL_HINT_APP_NAME, "Moono");
    SDL_SetHint(MS_SDL_HINT_VIDEO_ALLOW_SCREENSAVER, "1");
    SDL_SetHint(MS_SDL_HINT_IME_IMPLEMENTED_UI, "none");
    if (!r.activate) SDL_SetHint(MS_SDL_HINT_WINDOW_ACTIVATE_WHEN_SHOWN, "0");
    if (!SDL_Init(MS_SDL_INIT_VIDEO | (gaming || getenv("SteamDeck") ? MS_SDL_INIT_GAMEPAD : 0)))
    {
        fprintf(stderr, "[OniMoono] the crash window could not open: %s\n", SDL_GetError());
        return exit_code;
    }
    cw.text = text_scale();
    apply_theme();
    cw.scale = 1;
    open_fonts();
    cw_lay_out(&canvas);
    window = SDL_CreateWindow(cw_title, cw.lay.width, cw.lay.height, MS_SDL_WINDOW_HIDDEN | MS_SDL_WINDOW_HIGH_PIXEL_DENSITY);
    if (!window)
    {
        fprintf(stderr, "[OniMoono] the crash window could not open: %s\n", SDL_GetError());
        SDL_Quit();
        return exit_code;
    }
    MS_Surface *icon48 = icon_image(48);
    if (icon48) { SDL_SetWindowIcon(window, icon48); SDL_DestroySurface(icon48); }
    relayout();
    const char *at = getenv("ONI_MOONO_CRASH_WINDOW_AT");
    int x, y;
    if (at && sscanf(at, "%d,%d", &x, &y) == 2) SDL_SetWindowPosition(window, x, y);
    else if (r.monitor[2] > 0) SDL_SetWindowPosition(window, r.monitor[0] + (r.monitor[2] - (int)(cw.lay.width / pixel_density)) / 2, r.monitor[1] + (r.monitor[3] - (int)(cw.lay.height / pixel_density)) / 2);
    else SDL_SetWindowPosition(window, MS_SDL_WINDOWPOS_CENTERED, MS_SDL_WINDOWPOS_CENTERED);
    if (gaming) mark_steam_game();
    SDL_ShowWindow(window);
    if (r.activate) SDL_RaiseWindow(window);
    focus = -1;
    set_focus(cw.blocked ? C_REPORT : C_NOTE);
    present();

    while (!closing)
    {
        MS_Event e;
        int any = SDL_WaitEventTimeout(&e, 265);
        int redraw = !any && focus == C_NOTE;   /* the caret's blink */
        while (any)
        {
            switch (e.type)
            {
            case MS_SDL_EVENT_QUIT:
            case MS_SDL_EVENT_WINDOW_CLOSE_REQUESTED: closing = 1; break;
            case MS_SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED: relayout(); redraw = 1; break;
            case MS_SDL_EVENT_SYSTEM_THEME_CHANGED: apply_theme(); redraw = 1; break;
            case MS_SDL_EVENT_WINDOW_EXPOSED: case MS_SDL_EVENT_WINDOW_SHOWN: case MS_SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
            case MS_SDL_EVENT_WINDOW_RESIZED: case MS_SDL_EVENT_WINDOW_FOCUS_GAINED: case MS_SDL_EVENT_WINDOW_FOCUS_LOST: redraw = 1; break;
            case MS_SDL_EVENT_WINDOW_MOUSE_LEAVE: hot_part = -1; redraw = 1; break;
            case MS_SDL_EVENT_MOUSE_MOTION: pointer_moved(e.motion.x * pixel_density, e.motion.y * pixel_density); redraw = 1; break;
            case MS_SDL_EVENT_MOUSE_BUTTON_DOWN:
                if (e.button.button == MS_SDL_BUTTON_LEFT) pointer_down(e.button.x * pixel_density, e.button.y * pixel_density, e.button.clicks);
                redraw = 1;
                break;
            case MS_SDL_EVENT_MOUSE_BUTTON_UP:
                if (e.button.button == MS_SDL_BUTTON_LEFT) pointer_up(e.button.x * pixel_density, e.button.y * pixel_density);
                redraw = 1;
                break;
            case MS_SDL_EVENT_KEY_DOWN: key(&e.key); redraw = 1; break;
            case MS_SDL_EVENT_TEXT_INPUT:
                if (focus == C_NOTE && e.text.text) { note_replace(e.text.text); layout_note(); caret_shown = SDL_GetTicks(); }
                redraw = 1;
                break;
            case MS_SDL_EVENT_GAMEPAD_ADDED: SDL_OpenGamepad(e.gdevice.which); break;
            case MS_SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                keyboard_used = 1;
                if (e.gbutton.button == MS_SDL_GAMEPAD_BUTTON_DPAD_UP || e.gbutton.button == MS_SDL_GAMEPAD_BUTTON_DPAD_LEFT) move_focus(-1);
                else if (e.gbutton.button == MS_SDL_GAMEPAD_BUTTON_DPAD_DOWN || e.gbutton.button == MS_SDL_GAMEPAD_BUTTON_DPAD_RIGHT) move_focus(1);
                else if (e.gbutton.button == MS_SDL_GAMEPAD_BUTTON_SOUTH) activate(focus == C_NOTE || focus == C_CAUSE || focus == C_DETAILS ? C_REPORT : focus);
                else if (e.gbutton.button == MS_SDL_GAMEPAD_BUTTON_EAST) closing = 1;
                redraw = 1;
                break;
            default: break;
            }
            any = SDL_WaitEventTimeout(&e, 0);
        }
        if (redraw && !closing) present();
    }
    SDL_DestroyWindow(window);
    cw_release_layout(&canvas);
    close_fonts();
    TTF_Quit();
    SDL_Quit();
    /* the programs it started, collected */
    while (waitpid(-1, NULL, WNOHANG) > 0) { }
    return exit_code;
}
