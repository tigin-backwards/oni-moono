/* The crash window's platform-independent part (crash_window.c; ARCHITECTURE §7, "The crash
 * window"): its strings, palette, layout and drawing, the details, the zip and the issue link.
 * Each platform draws through a canvas of its own (cw_canvas) and has its own window and
 * controls: Windows Direct2D/DirectWrite with standard controls (crash_dialog.cpp), Linux an
 * SDL3 window surface with SDL3_ttf text and drawn controls (crash_window_linux.c). Plain C,
 * UTF-8, no platform types. */
#ifndef MOONO_CRASH_WINDOW_H
#define MOONO_CRASH_WINDOW_H

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>

#ifdef __cplusplus
extern "C" {
#endif

/* The window's languages: English, the game's own others (Localization.PreinstalledLanguages),
 * then those of the most-used translation mods (Brazilian Portuguese for any Portuguese). */
enum { CW_ENGLISH, CW_CHINESE, CW_KOREAN, CW_RUSSIAN, CW_GERMAN, CW_PORTUGUESE, CW_JAPANESE, CW_FRENCH, CW_SPANISH, CW_LANGUAGES };

/* The language for a language code: the game's ("zh", "ko_klei", ...) or a locale's ("ru_RU.UTF-8"),
 * by its first two letters; English for any other. */
int cw_language_of(const char *code);

/* What the watcher knows of a crash, for the window. */
typedef struct
{
    int language;                /* CW_ENGLISH, ...: the game's language */
    char error[1024];            /* the error line, from the record; empty: exit_code alone */
    uint32_t exit_code;
    char version[64];            /* OniMoono's release (or commit), as the game gave it */
    char game_build[32];
    char gpu[128];               /* the renderer the game used, or empty */
    int32_t mods;                /* enabled mods, -1 unknown */
    uint64_t play_seconds;       /* the session's length, ~0 unknown */
    char log_path[1024];
    char dump_path[1024];        /* empty: no dump */
    uint64_t dump_size;
    int year, month, day, hour, minute, second;   /* local time */
    int monitor[4];              /* the game window's monitor: x, y, width, height; width 0: unknown */
    int activate;                /* 0: shown without taking the focus (test runs) */
} cw_report;

enum
{
    S_HEADLINE, S_SHOW_DETAILS, S_HIDE_DETAILS, S_NOTE, S_INCLUDE_DUMP, S_CLOSE, S_REPORT, S_REPORTED,
    S_DRAG, S_EXIT_CODE, S_GAME_BUILD, S_PLAYED, S_MODS, S_FILES, S_LIST, S_HOURS, S_MINUTES, S_SECONDS,
    /* Linux: no drag and drop; the zip is shown in its folder, its path copied, a copy put in
     * Downloads. Gaming Mode on a Steam Deck has neither a file manager nor a desktop browser. */
    S_ATTACH, S_SHOW_FOLDER, S_COPY_PATH, S_COPIED, S_DECK,
    /* Windows' application control blocked a file (cw_blocked_file): the headline, what to do, the link. */
    S_BLOCKED_HEADLINE, S_BLOCKED_TEXT, S_BLOCKED_LINK,
    S_COUNT
};

/* Every string the window shows, in each language (the game's). */
extern const char *const cw_strings[CW_LANGUAGES][S_COUNT];
extern const char *const cw_title;
extern const char *const cw_issue_base;
/* The README's "Unsigned releases" section, which the blocked window's button opens. */
extern const char *const cw_blocked_url;

/* Whether the error line is a file Windows' application control refused (#135): it holds HRESULT
 * 0x800711C7 (ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION, 4551: Smart App Control on a consumer
 * machine, or a WDAC policy), as .NET's FileLoadException, BadImageFormatException and
 * DllNotFoundException, hostfxr's reasons and the host's failures (LoadLibrary,
 * CreateProcess) give it. The file's name (the last .dll or .exe named before the code) in
 * `file`, empty when none is named. Read after a crash: nothing is checked on a normal
 * start. */
int cw_blocked_file(const char *error, char *file, size_t most);

/* Colours as Direct2D has them: 0 to 1 per channel. */
typedef struct { float r, g, b, a; } cw_color;
cw_color cw_rgb(uint32_t v, float a);
cw_color cw_mix(cw_color a, cw_color b, float t);

typedef struct
{
    int dark, contrast;
    cw_color bg, caption, fg, fg2, mut, panel, border, footer, pri, prifg, secbd, secfg, hover, ring;
} cw_palette;

/* The onimaxxing site's tokens in its light or dark palette (high contrast is each platform's). */
cw_palette cw_palette_for(int dark);

/* Pixel rectangles, from DIPs. */
typedef struct { float l, t, r, b; } cw_box;
static inline float cw_w(cw_box b) { return b.r - b.l; }
static inline float cw_h(cw_box b) { return b.b - b.t; }
float cw_snap(float v);
cw_box cw_pixel_box(float l, float t, float r, float b);

/* The GitHub mark (Octicons mark-github, MIT) in a 16 x 16 box, as SVG path commands. */
extern const char cw_github_mark[];
/* The SVG path's commands (M m C c L l H h V v A a Z z) as absolute segments. */
typedef struct
{
    void (*begin)(void *context, float x, float y);
    void (*line)(void *context, float x, float y);
    void (*cubic)(void *context, float x1, float y1, float x2, float y2, float x, float y);
    void (*arc)(void *context, float rx, float ry, float rotation, int large, int sweep, float x, float y);
    void (*end)(void *context, int closed);
} cw_path_sink;
void cw_svg_path(const char *text, const cw_path_sink *sink, void *context);

/* The texts the layout makes, by the font each is set in. */
enum { CW_F_HEAD, CW_F_BODY, CW_F_SMALL, CW_F_MONO, CW_F_BUTTON, CW_F_CHIP, CW_F_ZIP, CW_F_CAPTION, CW_F_COUNT };
enum { CW_REGULAR, CW_MEDIUM, CW_SEMIBOLD };
/* Each font's family (0 sans, 1 mono, 2 the caption's), weight and size in px. */
typedef struct { int family, weight; float size; } cw_font;
cw_font cw_font_of(int which);

typedef struct cw_text cw_text;   /* a text laid out by the canvas */
typedef struct { float height, width; int lines; } cw_metrics;   /* width with trailing white space */

enum { CW_ALIGN_LEADING, CW_ALIGN_CENTER };

/* What a platform draws with. Boxes and points in px. */
typedef struct
{
    void *context;
    /* A text in `font` (CW_F_*), wrapped to `width` (unless !wrap) in lines `line_height` px
     * apart, the font's ascent and descent centred in each, as CSS lays out line-height. */
    cw_text *(*make_text)(void *context, const char *text, int font, float width, float line_height, int wrap);
    void (*set_spacing)(void *context, cw_text *t, float spacing);    /* letter-spacing, px */
    void (*set_align)(void *context, cw_text *t, int align);
    cw_metrics (*metrics)(void *context, cw_text *t);
    void (*free_text)(void *context, cw_text *t);
    /* Everything drawn after is moved down by `y` (the PNG's drawn title bar). */
    void (*set_offset)(void *context, float y);
    void (*fill)(void *context, cw_box b, cw_color c);
    void (*fill_round)(void *context, cw_box b, float radius, cw_color c);
    /* Direct2D's DrawRoundedRectangle: a line `width` wide centred on the rounded box's edge;
     * `dashed`: 4 on, 3 off, times the width. */
    void (*stroke_round)(void *context, cw_box b, float radius, float width, cw_color c, int dashed);
    void (*draw_text)(void *context, cw_text *t, float x, float y, cw_color c);
    void (*line)(void *context, float x0, float y0, float x1, float y1, float width, cw_color c);
    void (*dot)(void *context, float x, float y, float radius, cw_color c);
    /* The GitHub mark, its 16 x 16 box scaled by k with its top left at x, y. */
    void (*mark)(void *context, float x, float y, float k, cw_color c);
    /* The application's icon, size x size px at x, y (the PNG's title bar). */
    void (*icon)(void *context, float x, float y, int size);
    /* The window's picture (a sad Gassy Moo), fitted into the box and centred; null: none. */
    void (*picture)(void *context, cw_box b);
} cw_canvas;

/* The parts of the window, in the order Tab goes through them. */
enum { C_HEAD, C_CAUSE, C_TOGGLE, C_DETAILS, C_NOTE, C_CHECK, C_CHIP, C_FOLDER, C_COPY, C_CLOSE, C_REPORT, C_COUNT };

/* What the drawing depends on besides the layout: each control's state. */
typedef struct
{
    int hot[C_COUNT], pressed[C_COUNT], focus[C_COUNT];
    int cues, checked, note_empty;
} cw_look;

typedef struct
{
    int width, height;            /* the client area, px */
    float line;                   /* a 1-DIP line in whole pixels */
    cw_box head, cause, toggle, details, note, check, chip, links, folder, copy, footer, close, report;
    cw_box picture;               /* the Gassy Moo; empty: none */
    cw_box file;                  /* the blocked window's */
    cw_text *t_head, *t_cause, *t_toggle, *t_details, *t_note, *t_check, *t_chip_name, *t_chip_hint, *t_zip,
        *t_folder, *t_copy, *t_close, *t_report, *t_caption, *t_file;
} cw_layout;

/* Facts of this machine the platform finds itself: the system, the GPU's driver, the memory. */
typedef struct
{
    char os[128];
    char gpu[192];
    uint64_t ram_gb;
} cw_system;

/* The window's state, shared by the platform's code (one window per process). */
typedef struct
{
    cw_report report;
    cw_system system;
    const char *const *t;         /* cw_strings[language] */
    char error[1024], details[2048];
    float scale;
    float text;                   /* the system's text size, 1 to 2.25 */
    float extra_cause, extra_details;   /* px the selectable text needs beyond the drawn lines */
    cw_palette pal;
    int details_open, reported, include_dump, can_include_dump, render_only;
    /* Windows blocked a file (cw_blocked_file): no report, the note or the dump; what to do and a
     * link to the README instead. */
    int blocked;
    char blocked_file[260];
    int links;                    /* Linux: Show in folder and Copy path below the zip */
    int copied;                   /* Linux: the path was copied */
    int chip_hint;                /* the zip's hint (S_DRAG, S_ATTACH or S_DECK) */
    char chip_path[1024];
    uint64_t chip_size;
    cw_layout lay;
} cw_state;

extern cw_state cw;

/* Sizes in DIPs, and those that follow the text (times the system's text size). */
static inline float cw_dip(float v) { return v * cw.scale; }
static inline float cw_tdip(float v) { return v * cw.scale * cw.text; }

/* The report's own parts: the strings, the error line (the record's, else the exit code), the
 * dump's check box, whether Windows blocked a file. */
void cw_prepare(const cw_report *r);
/* The text under the headline: the error line, or for a blocked file what to do. */
const char *cw_cause_text(void);

void cw_format_size(uint64_t bytes, char *out, size_t most);
const char *cw_file_name(const char *path);
/* The details, one fact a line, in `language`. */
void cw_details_text(const cw_report *r, const cw_system *sys, int language, char *out, size_t most);

/* The layout at cw.scale and cw.text (the texts made through the canvas, earlier ones freed),
 * and the drawing of it with `look`, `offset` px down. */
void cw_lay_out(const cw_canvas *c);
void cw_release_layout(const cw_canvas *c);
void cw_draw(const cw_canvas *c, const cw_look *look, float offset);
/* A platform's own drawing over a part, after it is drawn (Linux: the selection, the note's text
 * and caret over C_CAUSE, C_DETAILS and C_NOTE); null for none. */
extern void (*cw_overlay)(int part, cw_box b);
/* The title bar a window manager draws, for the PNG: the icon, the title, the close glyph. */
void cw_draw_caption(const cw_canvas *c, float height);
/* The keyboard focus: 2 DIPs in the ink colour, 2 DIPs out from the control. */
void cw_focus_ring(const cw_canvas *c, cw_box b, float radius);

/* ---- the zip and the issue ------------------------------------------------------------------- */

/* Files by UTF-8 path, each platform's (Windows: wide paths). */
FILE *cw_open(const char *path, const char *mode);
int cw_seek(FILE *f, uint64_t at);
uint64_t cw_file_size(FILE *f);

extern const uint64_t cw_zip_most;    /* GitHub takes attachments up to 25 MB */
extern const uint64_t cw_dump_most;   /* a larger dump (--crash-dumps full) can't fit */

/* The zip of Player.log (a log over 32 MB cut to its first 4 MB and last 28 MB) and, if
 * `with_dump` and it fits, the dump, at `path`; its size in `written`. */
int cw_write_zip(const char *path, int with_dump, uint64_t *written);
/* OniMoono-crash-YYYYMMDD-hhmm.zip beside the log. */
void cw_zip_path(char *out, size_t most);

/* The new-issue URL on the crash form (.github/ISSUE_TEMPLATE/crash.yml), under 7,500
 * characters: the note cut short with "…". */
void cw_issue_url(const char *note, char *url, size_t most);

/* ---- --crash-dialog-render ------------------------------------------------------------------- */

typedef struct
{
    const char *out, *zip_out, *log, *dump, *url_out, *note;
    int dark, details, reported;
    float scale, text;
} cw_render_options;

/* `--crash-dialog-render OUT.png [--theme light|dark] [--scale S] [--sample gpu|managed|fastfail|blocked]
 * [--language en|zh|ko|ru|de|pt|ja|fr|es] [--text-size 1-2.25] [--details-open] [--reported] [--zip OUT.zip --log FILE
 * [--dump FILE]] [--url OUT.txt --note TEXT]` (UTF-8 arguments): the options and the sample
 * report (paths as `windows`' or Linux's), 0 when they are wrong. */
int cw_render_args(int argc, char **argv, int windows, cw_render_options *o, cw_report *r);
/* The state for a render: the sample's facts, the theme, scale and the rest set. */
void cw_render_state(const cw_render_options *o, const cw_report *r, int windows);

/* PNG of 8-bit RGBA rows (stride bytes apart) with our deflate; 0 on a failure. */
int cw_write_png(const char *path, const unsigned char *rgba, int width, int height, int stride);

#ifdef __cplusplus
}
#endif

#endif
