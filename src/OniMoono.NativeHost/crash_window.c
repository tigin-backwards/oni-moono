/* The crash window's platform-independent part (crash_window.h; ARCHITECTURE §7, "The crash
 * window"): the strings, the palette, the layout and the drawing (through each platform's
 * canvas), the details, the zip, the issue link and the sample `--crash-dialog-render` draws.
 *
 * The layout is the approved mockup's (the onimaxxing site's dialog): sizes in DIPs (96 per
 * inch) times the monitor's scale, those that hold text also times the system's text size;
 * lines are whole pixels. */
#ifdef _WIN32
#define _CRT_SECURE_NO_WARNINGS
#endif
#include "crash_window.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

cw_state cw;

static float maxf(float a, float b) { return a > b ? a : b; }

/* ---- strings -------------------------------------------------------------------------------- */

const char *const cw_strings[CW_LANGUAGES][S_COUNT] = {
    {
        "The game closed unexpectedly", "Show details", "Hide details", "What were you doing? (optional)",
        "Include the crash dump (%s)", "Close", "Report on GitHub", "Opened in browser \xE2\x9C\x93",
        "Drag this into the GitHub page", "Exit code %lu", ", game build %s", "Played %s", "%ld mods",
        "Files: %s", ", ", "%llu h %llu min", "%llu min", "%llu s",
        "Attach this in the GitHub page (a copy is in Downloads)", "Show in folder", "Copy path", "Path copied \xE2\x9C\x93",
        "Saved to Downloads: attach it from Desktop Mode or another device",
        "Windows blocked one of Moono\xE2\x80\x99s files",
        "Smart App Control or another app control policy has blocked one of Moono\xE2\x80\x99s files. "
        "You can turn Smart App Control off in Windows Security > App & browser control > Smart App Control.",
        "Read more on GitHub",
    },
    {
        "游戏意外关闭", "显示详细信息", "隐藏详细信息", "你当时在做什么？（可选）",
        "附上崩溃转储（%s）", "关闭", "在 GitHub 上报告", "已在浏览器中打开 \xE2\x9C\x93",
        "将此文件拖到 GitHub 页面中", "退出代码 %lu", "，游戏版本 %s", "游戏时长 %s", "%ld 个模组",
        "文件：%s", "、", "%llu 小时 %llu 分钟", "%llu 分钟", "%llu 秒",
        "在 GitHub 页面中附上此文件（下载文件夹中也有一份）", "在文件夹中显示", "复制路径", "已复制路径 \xE2\x9C\x93",
        "已保存到下载文件夹：请在桌面模式或其他设备上附上它",
        "Windows 阻止了 Moono 的一个文件",
        "智能应用控制或其他应用控制策略阻止了 Moono 的一个文件。"
        "你可以在“Windows 安全中心 > 应用和浏览器控制 > 智能应用控制”中将其关闭。",
        "在 GitHub 上了解详情",
    },
    {
        "게임이 예기치 않게 종료되었습니다", "자세히 보기", "자세히 숨기기", "무엇을 하고 있었나요? (선택)",
        "크래시 덤프 포함 (%s)", "닫기", "GitHub에 보고", "브라우저에서 열림 \xE2\x9C\x93",
        "이 파일을 GitHub 페이지로 끌어다 놓으세요", "종료 코드 %lu", ", 게임 빌드 %s", "플레이 시간 %s", "모드 %ld개",
        "파일: %s", ", ", "%llu시간 %llu분", "%llu분", "%llu초",
        "GitHub 페이지에 이 파일을 첨부하세요 (다운로드 폴더에 사본이 있습니다)", "폴더에서 보기", "경로 복사", "경로 복사됨 \xE2\x9C\x93",
        "다운로드 폴더에 저장됨: 데스크톱 모드나 다른 기기에서 첨부하세요",
        "Windows가 Moono의 파일 하나를 차단했습니다",
        "스마트 앱 컨트롤 또는 다른 앱 제어 정책이 Moono의 파일 하나를 차단했습니다. "
        "Windows 보안 > 앱 및 브라우저 컨트롤 > 스마트 앱 컨트롤에서 스마트 앱 컨트롤을 끌 수 있습니다.",
        "GitHub에서 자세히 보기",
    },
    {
        "Игра неожиданно закрылась", "Показать подробности", "Скрыть подробности", "Что вы делали? (необязательно)",
        "Приложить дамп сбоя (%s)", "Закрыть", "Сообщить на GitHub", "Открыто в браузере \xE2\x9C\x93",
        "Перетащите это на страницу GitHub", "Код выхода %lu", ", сборка игры %s", "Время игры %s", "Моды: %ld",
        "Файлы: %s", ", ", "%llu ч %llu мин", "%llu мин", "%llu с",
        "Приложите это на странице GitHub (копия лежит в «Загрузках»)", "Показать в папке", "Копировать путь", "Путь скопирован \xE2\x9C\x93",
        "Сохранено в «Загрузки»: приложите из режима рабочего стола или с другого устройства",
        "Windows заблокировала один из файлов Moono",
        "Интеллектуальное управление приложениями или другая политика управления приложениями заблокировала один из файлов Moono. "
        "Его можно отключить в «Безопасность Windows > Управление приложениями/браузером > Интеллектуальное управление приложениями».",
        "Подробнее на GitHub",
    },
};

int cw_language_of(const char *code)
{
    static const char codes[CW_LANGUAGES][3] = { "en", "zh", "ko", "ru" };
    for (int i = 1; code && i < CW_LANGUAGES; i++)
        if ((code[0] | 0x20) == codes[i][0] && (code[1] | 0x20) == codes[i][1]) return i;
    return CW_ENGLISH;
}

const char *const cw_title = "OniMoono";
const char *const cw_issue_base = "https://github.com/tigin-backwards/oni-moono/issues/new";
const char *const cw_blocked_url = "https://github.com/tigin-backwards/oni-moono#unsigned-releases";

/* ---- colours -------------------------------------------------------------------------------- */

cw_color cw_rgb(uint32_t v, float a)
{
    cw_color c = { ((v >> 16) & 255) / 255.0f, ((v >> 8) & 255) / 255.0f, (v & 255) / 255.0f, a };
    return c;
}

/* `a` over `b` at `t` (0: b, 1: a): disabled controls at half opacity, as the site's. */
cw_color cw_mix(cw_color a, cw_color b, float t)
{
    cw_color c = { a.r * t + b.r * (1 - t), a.g * t + b.g * (1 - t), a.b * t + b.b * (1 - t), 1.0f };
    return c;
}

/* The onimaxxing site's tokens (apps/onimaxxing/src/index.css, oklch converted to sRGB) as the
 * approved mockup uses them. */
cw_palette cw_palette_for(int dark)
{
    cw_palette p;
    memset(&p, 0, sizeof p);
    p.dark = dark;
    if (dark)
    {
        p.bg = cw_rgb(0x191512, 1);       /* card: oklch(0.2 0.008 60) */
        p.caption = cw_rgb(0x100C0A, 1);  /* background: oklch(0.16 0.008 60) */
        p.fg = cw_rgb(0xE9E8E4, 1);       /* oklch(0.93 0.005 90) */
        p.fg2 = cw_rgb(0xC1BDB8, 1);      /* oklch(0.8 0.008 75) */
        p.mut = cw_rgb(0x948E89, 1);      /* muted-foreground: oklch(0.65 0.01 60) */
        p.panel = cw_rgb(0x120F0C, 1);    /* oklch(0.17 0.008 60) */
        p.border = cw_rgb(0xFFFFFF, 0.10f);
        p.footer = cw_rgb(0x1D1A17, 1);   /* oklch(0.22 0.008 60) */
        p.pri = p.fg;
        p.prifg = p.caption;
        p.secbd = cw_rgb(0xFFFFFF, 0.14f);
        p.secfg = cw_rgb(0xADA6A2, 1);    /* oklch(0.73 0.01 60): a step above muted, for Close */
        p.hover = cw_rgb(0x272320, 1);    /* accent: oklch(0.26 0.008 60) */
        p.ring = cw_rgb(0x68625E, 1);     /* ring: oklch(0.5 0.01 60) */
    }
    else
    {
        p.bg = cw_rgb(0xFFFCF6, 1);       /* card: oklch(0.992 0.008 85) */
        p.caption = cw_rgb(0xF7F4ED, 1);  /* background: oklch(0.968 0.01 85) */
        p.fg = cw_rgb(0x15110D, 1);       /* oklch(0.18 0.01 60) */
        p.fg2 = cw_rgb(0x413C38, 1);      /* oklch(0.36 0.01 60) */
        p.mut = cw_rgb(0x68625E, 1);      /* oklch(0.5 0.01 60) */
        p.panel = cw_rgb(0xF7F4ED, 1);
        p.border = cw_rgb(0xDDD9CF, 1);   /* oklch(0.885 0.014 85) */
        p.footer = cw_rgb(0xF7F4ED, 1);
        p.pri = p.fg;
        p.prifg = cw_rgb(0xFAFAFA, 1);    /* oklch(0.985 0 0) */
        p.secbd = p.border;
        p.secfg = cw_rgb(0x514C48, 1);    /* oklch(0.42 0.01 60): a step above muted, for Close */
        p.hover = cw_rgb(0xEBE7DE, 1);    /* accent: oklch(0.928 0.012 85) */
        p.ring = cw_rgb(0x948E89, 1);     /* ring: oklch(0.65 0.01 60) */
    }
    return p;
}

float cw_snap(float v) { return floorf(v + 0.5f); }

cw_box cw_pixel_box(float l, float t, float r, float b)
{
    cw_box x = { cw_snap(l), cw_snap(t), cw_snap(r), cw_snap(b) };
    return x;
}

/* ---- the GitHub mark (Octicons mark-github, MIT) -------------------------------------------- */

const char cw_github_mark[] =
    "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94"
    "-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07"
    "-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0"
    " 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25"
    ".54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z";

typedef struct { const char *at; } path_reader;

static void path_skip(path_reader *r) { while (*r->at == ' ' || *r->at == ',' || *r->at == '\n') r->at++; }

static int path_number_next(path_reader *r)
{
    path_skip(r);
    return (*r->at >= '0' && *r->at <= '9') || *r->at == '-' || *r->at == '.' || *r->at == '+';
}

static float path_number(path_reader *r)
{
    path_skip(r);
    const char *start = r->at;
    if (*r->at == '-' || *r->at == '+') r->at++;
    int dot = 0;
    while ((*r->at >= '0' && *r->at <= '9') || (*r->at == '.' && !dot)) { if (*r->at == '.') dot = 1; r->at++; }
    if (*r->at == 'e' || *r->at == 'E') { r->at++; if (*r->at == '-' || *r->at == '+') r->at++; while (*r->at >= '0' && *r->at <= '9') r->at++; }
    return (float)strtod(start, NULL);
}

static int path_flag(path_reader *r)
{
    path_skip(r);
    return *r->at ? *r->at++ - '0' : 0;
}

static int is_letter(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }

void cw_svg_path(const char *text, const cw_path_sink *sink, void *context)
{
    path_reader r = { text };
    float pen_x = 0, pen_y = 0, start_x = 0, start_y = 0;
    int open = 0;
    char command = 0;
    for (;;)
    {
        path_skip(&r);
        if (!*r.at) break;
        if (is_letter(*r.at)) command = *r.at++;
        int relative = command >= 'a';
        float ox = relative ? pen_x : 0, oy = relative ? pen_y : 0;
        switch (command | 0x20)
        {
        case 'm':
        {
            if (open) sink->end(context, 0);
            float x = path_number(&r) + ox, y = path_number(&r) + oy;
            pen_x = start_x = x;
            pen_y = start_y = y;
            sink->begin(context, x, y);
            open = 1;
            command = relative ? 'l' : 'L';   /* further pairs are lines */
            break;
        }
        case 'l': { float x = path_number(&r) + ox, y = path_number(&r) + oy; pen_x = x; pen_y = y; sink->line(context, x, y); break; }
        case 'h': { pen_x = path_number(&r) + ox; sink->line(context, pen_x, pen_y); break; }
        case 'v': { pen_y = path_number(&r) + oy; sink->line(context, pen_x, pen_y); break; }
        case 'c':
        {
            float x1 = path_number(&r) + ox, y1 = path_number(&r) + oy;
            float x2 = path_number(&r) + ox, y2 = path_number(&r) + oy;
            float x = path_number(&r) + ox, y = path_number(&r) + oy;
            sink->cubic(context, x1, y1, x2, y2, x, y);
            pen_x = x;
            pen_y = y;
            break;
        }
        case 'a':
        {
            float rx = path_number(&r), ry = path_number(&r), rotation = path_number(&r);
            int large = path_flag(&r), sweep = path_flag(&r);
            float x = path_number(&r) + ox, y = path_number(&r) + oy;
            sink->arc(context, rx, ry, rotation, large, sweep, x, y);
            pen_x = x;
            pen_y = y;
            break;
        }
        case 'z':
            if (open) sink->end(context, 1);
            open = 0;
            pen_x = start_x;
            pen_y = start_y;
            break;
        default:
            r.at++;
            break;
        }
        if ((command | 0x20) != 'z' && !path_number_next(&r) && *r.at && !is_letter(*r.at)) r.at++;
    }
    if (open) sink->end(context, 1);
}

/* ---- fonts ---------------------------------------------------------------------------------- */

cw_font cw_font_of(int which)
{
    cw_font f = { 0, CW_REGULAR, 0 };
    switch (which)
    {
    case CW_F_HEAD: f.weight = CW_SEMIBOLD; f.size = cw_tdip(16); break;
    case CW_F_BODY: f.size = cw_tdip(13); break;
    case CW_F_SMALL: f.size = cw_tdip(12); break;
    case CW_F_MONO: f.family = 1; f.size = cw_tdip(12); break;
    case CW_F_BUTTON: f.weight = CW_MEDIUM; f.size = cw_tdip(12.5f); break;
    case CW_F_CHIP: f.weight = CW_SEMIBOLD; f.size = cw_tdip(12.5f); break;
    case CW_F_ZIP: f.weight = CW_SEMIBOLD; f.size = cw_tdip(9.5f); break;
    default: f.family = 2; f.size = cw_dip(12); break;
    }
    return f;
}

/* ---- the report's text ---------------------------------------------------------------------- */

void cw_format_size(uint64_t bytes, char *out, size_t most)
{
    if (bytes < 1000 * 1024) snprintf(out, most, "%llu KB", (unsigned long long)((bytes + 1023) / 1024));
    else if (bytes < 1000ull * 1024 * 1024) snprintf(out, most, "%.1f MB", bytes / (1024.0 * 1024.0));
    else snprintf(out, most, "%.1f GB", bytes / (1024.0 * 1024.0 * 1024.0));
}

const char *cw_file_name(const char *path)
{
    const char *a = strrchr(path, '\\'), *b = strrchr(path, '/');
    if (b > a) a = b;
    return a ? a + 1 : path;
}

static void append(char *out, size_t most, const char *text)
{
    size_t n = strlen(out), k = strlen(text);
    if (n + 1 >= most) return;
    if (k > most - n - 1) k = most - n - 1;
    memcpy(out + n, text, k);
    out[n + k] = 0;
}

static char lower(char c) { return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c; }

/* Whether `text` starts with `word`, ignoring ASCII case. */
static int starts_with(const char *text, const char *word)
{
    size_t i = 0;
    while (word[i] && lower(text[i]) == lower(word[i])) i++;
    return !word[i];
}

/* `word` in `text`, ignoring ASCII case; null when it is not there. */
static const char *find_word(const char *text, const char *word)
{
    for (; *text; text++)
        if (starts_with(text, word)) return text;
    return NULL;
}

int cw_blocked_file(const char *error, char *file, size_t most)
{
    if (file && most) file[0] = 0;
    const char *code = find_word(error, "0x800711C7");
    if (!code) return 0;
    /* The last ".dll" or ".exe" ending a word before the code, back to the path's last separator
     * or the quote or bracket before it. */
    const char *end = NULL;
    for (const char *at = error; at + 4 <= code; at++)
    {
        char next = at[4];
        int word_end = !((next >= 'a' && next <= 'z') || (next >= 'A' && next <= 'Z') || (next >= '0' && next <= '9') || next == '_');
        if (word_end && (starts_with(at, ".dll") || starts_with(at, ".exe"))) end = at + 4;
    }
    if (end && file && most)
    {
        const char *start = end - 4;
        while (start > error && !strchr("\\/'\"[( ", start[-1])) start--;
        size_t n = (size_t)(end - start);
        if (n >= most) n = most - 1;
        memcpy(file, start, n);
        file[n] = 0;
    }
    return 1;
}

void cw_details_text(const cw_report *r, const cw_system *sys, int language, char *out, size_t most)
{
    const char *const *t = cw_strings[language];
    char part[512];
    out[0] = 0;
    /* A blocked file's window shows what to do in place of the error: the error heads the details. */
    if (r->error[0] && cw_blocked_file(r->error, NULL, 0))
    {
        append(out, most, r->error);
        append(out, most, "\n");
    }
    snprintf(part, sizeof part, "OniMoono %s", r->version[0] ? r->version : "?");
    append(out, most, part);
    if (r->game_build[0]) { snprintf(part, sizeof part, t[S_GAME_BUILD], r->game_build); append(out, most, part); }
    append(out, most, "\n");
    append(out, most, sys->os);
    if (sys->gpu[0]) { append(out, most, " \xC2\xB7 "); append(out, most, sys->gpu); }
    if (sys->ram_gb) { snprintf(part, sizeof part, " \xC2\xB7 %llu GB", (unsigned long long)sys->ram_gb); append(out, most, part); }
    int line = 0;
    if (r->play_seconds != ~0ull)
    {
        char played[64];
        unsigned long long s = (unsigned long long)r->play_seconds;
        if (s >= 3600) snprintf(played, sizeof played, t[S_HOURS], s / 3600, s / 60 % 60);
        else if (s >= 60) snprintf(played, sizeof played, t[S_MINUTES], s / 60);
        else snprintf(played, sizeof played, t[S_SECONDS], s);
        snprintf(part, sizeof part, t[S_PLAYED], played);
        append(out, most, "\n");
        append(out, most, part);
        line = 1;
    }
    if (r->mods >= 0)
    {
        snprintf(part, sizeof part, t[S_MODS], (long)r->mods);
        append(out, most, line ? " \xC2\xB7 " : "\n");
        append(out, most, part);
    }
    char files[600];
    snprintf(files, sizeof files, "%s", cw_file_name(r->log_path));
    if (r->dump_path[0]) { append(files, sizeof files, t[S_LIST]); append(files, sizeof files, cw_file_name(r->dump_path)); }
    snprintf(part, sizeof part, t[S_FILES], files);
    append(out, most, "\n");
    append(out, most, part);
}

void cw_prepare(const cw_report *r)
{
    cw.report = *r;
    cw.t = cw_strings[r->language];
    if (r->error[0]) snprintf(cw.error, sizeof cw.error, "%s", r->error);
    else snprintf(cw.error, sizeof cw.error, cw.t[S_EXIT_CODE], (unsigned long)r->exit_code);
    cw.blocked = cw_blocked_file(cw.error, cw.blocked_file, sizeof cw.blocked_file);
    cw.can_include_dump = !cw.blocked && r->dump_path[0] && r->dump_size <= cw_dump_most;
    cw.include_dump = cw.can_include_dump;
    if (!cw.chip_hint) cw.chip_hint = S_DRAG;
}

const char *cw_cause_text(void) { return cw.blocked ? cw.t[S_BLOCKED_TEXT] : cw.error; }

/* ---- layout --------------------------------------------------------------------------------- */

static cw_text *text_of(const cw_canvas *c, const char *text, int font, float width, float line_height, int wrap)
{
    return c->make_text(c->context, text, font, width, line_height, wrap);
}

static cw_metrics metrics(const cw_canvas *c, cw_text *t)
{
    cw_metrics m = { 0, 0, 0 };
    return t ? c->metrics(c->context, t) : m;
}

void cw_release_layout(const cw_canvas *c)
{
    cw_layout *l = &cw.lay;
    cw_text **all[] = { &l->t_head, &l->t_cause, &l->t_toggle, &l->t_details, &l->t_note, &l->t_check, &l->t_chip_name, &l->t_chip_hint,
                        &l->t_zip, &l->t_folder, &l->t_copy, &l->t_close, &l->t_report, &l->t_caption, &l->t_file };
    for (size_t i = 0; i < sizeof all / sizeof all[0]; i++)
    {
        if (*all[i]) c->free_text(c->context, *all[i]);
        *all[i] = NULL;
    }
}

static void lay_out_footer(const cw_canvas *c, float y, const char *primary);
static void lay_out_details(const cw_canvas *c, float *y, float pad, float inner);

/* The sad Gassy Moo left of the headline (88 x 56 DIPs, the picture's proportions), and where
 * the text beside it starts and how wide it is. No picture (Linux): no column. */
static void picture_column(const cw_canvas *c, float pad, float inner, float y, float *x, float *text_w)
{
    float side = c->picture ? cw_dip(88) : 0, gap = c->picture ? cw_dip(16) : 0;
    cw.lay.picture = c->picture ? cw_pixel_box(pad, y, pad + side, y + cw_dip(56)) : cw_pixel_box(0, 0, 0, 0);
    *x = pad + side + gap;
    *text_w = inner - side - gap;
}

/* Windows blocked a file (#135): the picture on the left as in the crash window; the headline, the file and what to do
 * beside it; Show details under them; Close and the README's link. */
static void lay_out_blocked(const cw_canvas *c)
{
    cw_layout *l = &cw.lay;
    const char *const *t = cw.t;
    float pad = cw_dip(20), inner = l->width - 2 * pad, y = cw_dip(20);
    float x, text_w;
    picture_column(c, pad, inner, y, &x, &text_w);

    l->t_head = text_of(c, t[S_BLOCKED_HEADLINE], CW_F_HEAD, text_w, cw_tdip(16 * 1.3f), 1);
    if (l->t_head) c->set_spacing(c->context, l->t_head, cw_tdip(-0.2f));
    float h = metrics(c, l->t_head).height;
    l->head = cw_pixel_box(x, y, x + text_w, y + h);
    y += h + cw_dip(4);

    l->file = cw_pixel_box(0, 0, 0, 0);
    if (cw.blocked_file[0])
    {
        l->t_file = text_of(c, cw.blocked_file, CW_F_MONO, text_w, cw_tdip(12 * 1.55f), 1);
        h = metrics(c, l->t_file).height;
        l->file = cw_pixel_box(x, y, x + text_w, y + h);
        y += h;
    }
    y += cw_dip(10);

    l->t_cause = text_of(c, cw_cause_text(), CW_F_BODY, text_w, cw_tdip(13 * 1.45f), 1);
    h = metrics(c, l->t_cause).height + cw.extra_cause;
    l->cause = cw_pixel_box(x, y, x + text_w, y + h);
    y = maxf(y + h, l->picture.b) + cw_dip(16);

    lay_out_details(c, &y, pad, inner);
    l->note = l->check = l->chip = l->links = l->folder = l->copy = cw_pixel_box(0, 0, 0, 0);
    lay_out_footer(c, y + cw_dip(16), t[S_BLOCKED_LINK]);
}

/* Show details and, when open, the details, from `y` on. */
static void lay_out_details(const cw_canvas *c, float *at, float pad, float inner)
{
    cw_layout *l = &cw.lay;
    float y = *at, h;
    l->t_toggle = text_of(c, cw.t[S_SHOW_DETAILS], CW_F_SMALL, inner, cw_tdip(18), 0);
    l->toggle = cw_pixel_box(pad, y, pad + cw_tdip(12) + metrics(c, l->t_toggle).width, y + cw_tdip(18));
    y += cw_tdip(18);

    if (cw.details_open)
    {
        y += cw_dip(8);
        float text_w = inner - 2 * cw_dip(12) - 2 * l->line;
        l->t_details = text_of(c, cw.details, CW_F_MONO, text_w, cw_tdip(12 * 1.55f), 1);
        h = metrics(c, l->t_details).height + cw.extra_details + 2 * cw_dip(10) + 2 * l->line;
        l->details = cw_pixel_box(pad, y, pad + inner, y + h);
        y += h;
    }
    else l->details = cw_pixel_box(0, 0, 0, 0);
    *at = y;
}

/* The approved mockup's measures (CSS px = DIPs): 440 wide, 20 of padding, 4 between the
 * headline and the error, 12 to the details and the check box, 16 around the note and the zip.
 * What holds text grows with the system's text size, and the window widens by half as much. */
void cw_lay_out(const cw_canvas *c)
{
    cw_release_layout(c);
    cw_layout *l = &cw.lay;
    const char *const *t = cw.t;
    float s = cw.scale;
    l->line = maxf(1.0f, floorf(s));
    l->width = (int)cw_snap(cw_dip(440 * (1 + (cw.text - 1) / 2)));
    l->file = cw_pixel_box(0, 0, 0, 0);
    if (cw.blocked) { lay_out_blocked(c); return; }
    float pad = cw_dip(20), inner = l->width - 2 * pad, y = cw_dip(20), head_x, head_w;
    picture_column(c, pad, inner, y, &head_x, &head_w);

    l->t_head = text_of(c, t[S_HEADLINE], CW_F_HEAD, head_w, cw_tdip(16 * 1.3f), 1);
    if (l->t_head) c->set_spacing(c->context, l->t_head, cw_tdip(-0.2f));   /* letter-spacing: -0.2px */
    float h = metrics(c, l->t_head).height;
    l->head = cw_pixel_box(head_x, y, head_x + head_w, y + h);
    y += h + cw_dip(4);

    l->t_cause = text_of(c, cw.error, CW_F_BODY, head_w, cw_tdip(13 * 1.45f), 1);
    h = metrics(c, l->t_cause).height + cw.extra_cause;
    l->cause = cw_pixel_box(head_x, y, head_x + head_w, y + h);
    y = maxf(y + h, l->picture.b) + cw_dip(12);

    lay_out_details(c, &y, pad, inner);

    y += cw_dip(16);
    l->t_note = text_of(c, t[S_NOTE], CW_F_BODY, inner - 2 * cw_dip(10) - 2 * l->line, cw_tdip(13 * 1.5f), 1);
    h = 2 * l->line + 2 * cw_dip(8) + maxf(2 * cw_tdip(13 * 1.5f), metrics(c, l->t_note).height);
    l->note = cw_pixel_box(pad, y, pad + inner, y + h);
    y = l->note.b;

    if (cw.report.dump_path[0])
    {
        y += cw_dip(12);
        char size[32], label[200];
        cw_format_size(cw.report.dump_size, size, sizeof size);
        snprintf(label, sizeof label, t[S_INCLUDE_DUMP], size);
        l->t_check = text_of(c, label, CW_F_SMALL, inner - cw_tdip(13) - cw_dip(6), cw_tdip(18), 1);
        h = metrics(c, l->t_check).height;
        l->check = cw_pixel_box(pad, y, pad + cw_tdip(13) + cw_dip(6) + metrics(c, l->t_check).width, y + h);
        y += h;
    }
    else l->check = cw_pixel_box(0, 0, 0, 0);

    l->links = l->folder = l->copy = cw_pixel_box(0, 0, 0, 0);
    if (cw.reported)
    {
        y += cw_dip(16);
        char size[32], hint[400];
        cw_format_size(cw.chip_size, size, sizeof size);
        float text_w = inner - 2 * l->line - 2 * cw_dip(10) - cw_tdip(28) - cw_dip(10);
        l->t_chip_name = text_of(c, cw_file_name(cw.chip_path), CW_F_CHIP, text_w, cw_tdip(12.5f * 1.5f), 1);
        snprintf(hint, sizeof hint, "%s \xC2\xB7 %s", size, t[cw.chip_hint]);
        l->t_chip_hint = text_of(c, hint, CW_F_SMALL, text_w, cw_tdip(18), 1);
        l->t_zip = text_of(c, "ZIP", CW_F_ZIP, cw_tdip(28), cw_tdip(14), 0);
        if (l->t_zip) c->set_align(c->context, l->t_zip, CW_ALIGN_CENTER);
        float content = maxf(cw_tdip(34), metrics(c, l->t_chip_name).height + metrics(c, l->t_chip_hint).height);
        h = 2 * l->line + 2 * cw_dip(8) + content;
        l->chip = cw_pixel_box(pad, y, pad + inner, y + h);
        y = l->chip.b;
        if (cw.links)
        {
            /* Show in folder, Copy path: text buttons as "Show details" is. */
            y += cw_dip(8);
            l->t_folder = text_of(c, t[S_SHOW_FOLDER], CW_F_SMALL, inner, cw_tdip(18), 0);
            l->t_copy = text_of(c, t[cw.copied ? S_COPIED : S_COPY_PATH], CW_F_SMALL, inner, cw_tdip(18), 0);
            l->folder = cw_pixel_box(pad, y, pad + metrics(c, l->t_folder).width, y + cw_tdip(18));
            l->copy = cw_pixel_box(l->folder.r + cw_dip(16), y, l->folder.r + cw_dip(16) + metrics(c, l->t_copy).width, y + cw_tdip(18));
            l->links = cw_pixel_box(pad, y, pad + inner, y + cw_tdip(18));
            y += cw_tdip(18);
        }
    }
    else l->chip = cw_pixel_box(0, 0, 0, 0);

    lay_out_footer(c, y + cw_dip(16), cw.reported ? t[S_REPORTED] : t[S_REPORT]);
}

/* The footer from `y` down (the window's height), its buttons, and the title bar's text. */
static void lay_out_footer(const cw_canvas *c, float y, const char *primary)
{
    cw_layout *l = &cw.lay;
    const char *const *t = cw.t;
    float pad = cw_dip(20);
    float top = cw_snap(y);
    l->footer = cw_pixel_box(0, top, (float)l->width, top + l->line + cw_dip(24) + cw_tdip(30));
    l->height = (int)l->footer.b;

    /* The site's order: Close (outlined), then Report on GitHub (filled), on the right. */
    l->t_close = text_of(c, t[S_CLOSE], CW_F_BUTTON, 10000, cw_tdip(12.5f * 1.5f), 0);
    l->t_report = text_of(c, primary, CW_F_BUTTON, 10000, cw_tdip(12.5f * 1.5f), 0);
    float bt = l->footer.t + l->line + cw_dip(12), bb = bt + cw_tdip(30);
    float report_w = 2 * l->line + cw_dip(24) + (cw.reported ? 0 : cw_tdip(15) + cw_dip(6)) + metrics(c, l->t_report).width;
    float close_w = 2 * l->line + cw_dip(24) + metrics(c, l->t_close).width;
    float right = l->width - pad;
    l->report = cw_pixel_box(right - report_w, bt, right, bb);
    l->close = cw_pixel_box(l->report.l - cw_dip(8) - close_w, bt, l->report.l - cw_dip(8), bb);

    l->t_caption = text_of(c, cw_title, CW_F_CAPTION, 1000, cw_dip(16), 0);
}

/* ---- drawing -------------------------------------------------------------------------------- */

/* A border `width` px wide inside the box, on whole pixels. */
static void stroke_round(const cw_canvas *c, cw_box b, float radius, float width, cw_color color, int dashed)
{
    float half = width / 2;
    cw_box r = { b.l + half, b.t + half, b.r - half, b.b - half };
    c->stroke_round(c->context, r, radius - half, width, color, dashed);
}

void cw_focus_ring(const cw_canvas *c, cw_box b, float radius)
{
    float out = cw_snap(cw_dip(2)), width = cw_snap(cw_dip(2));
    cw_box r = { b.l - out - width, b.t - out - width, b.r + out + width, b.b + out + width };
    stroke_round(c, r, radius + out + width, width, cw.pal.contrast ? cw.pal.ring : cw.pal.fg, 0);
}

static void draw_chevron(const cw_canvas *c, float x, float cy, int open, cw_color color)
{
    /* The mockup's: a 6-DIP square's two sides, 1.5 DIPs, turned to point right (or down). */
    float k = cw_tdip(4.24f), cx = x + cw_tdip(3);
    float p[3][2];
    if (open) { p[0][0] = cx - k; p[0][1] = cy - k / 2; p[1][0] = cx; p[1][1] = cy + k / 2; p[2][0] = cx + k; p[2][1] = cy - k / 2; }
    else { p[0][0] = cx - k / 2; p[0][1] = cy - k; p[1][0] = cx + k / 2; p[1][1] = cy; p[2][0] = cx - k / 2; p[2][1] = cy + k; }
    c->line(c->context, p[0][0], p[0][1], p[1][0], p[1][1], cw_tdip(1.5f), color);
    c->line(c->context, p[1][0], p[1][1], p[2][0], p[2][1], cw_tdip(1.5f), color);
    c->dot(c->context, p[1][0], p[1][1], cw_tdip(0.75f), color);
}

static void draw_check_box(const cw_canvas *c, float x, float cy, int checked, int hot, int disabled)
{
    /* As a browser draws a native check box with accent-color: the foreground. */
    float size = cw_snap(cw_tdip(13));
    cw_box b = { cw_snap(x), cw_snap(cy - size / 2), cw_snap(x) + size, cw_snap(cy - size / 2) + size };
    float radius = cw_dip(2.5f);
    const cw_palette *p = &cw.pal;
    cw_color ink = disabled ? cw_mix(p->fg, p->bg, 0.5f) : p->fg;
    if (checked)
    {
        c->fill_round(c->context, b, radius, hot ? cw_mix(ink, p->bg, 0.85f) : ink);
        cw_color mark = p->contrast ? p->bg : p->dark ? p->caption : p->prifg;
        float w = cw_w(b);
        float ax = b.l + w * 0.22f, ay = b.t + w * 0.52f, mx = b.l + w * 0.42f, my = b.t + w * 0.72f, ex = b.l + w * 0.78f, ey = b.t + w * 0.30f;
        c->line(c->context, ax, ay, mx, my, cw_tdip(1.6f), mark);
        c->line(c->context, mx, my, ex, ey, cw_tdip(1.6f), mark);
        c->dot(c->context, mx, my, cw_tdip(0.8f), mark);
    }
    else
    {
        c->fill_round(c->context, b, radius, p->panel);
        stroke_round(c, b, radius, cw.lay.line, hot ? p->fg2 : p->mut, 0);
    }
}

static void draw_button(const cw_canvas *c, cw_box b, cw_text *label, int primary, int hot, int pressed, int disabled, int icon)
{
    const cw_palette *p = &cw.pal;
    float radius = cw_dip(8);
    cw_color under = p->footer, fill_c, text_c, border_c;
    if (primary)
    {
        fill_c = p->pri;
        text_c = p->prifg;
        if ((hot || pressed) && !disabled) fill_c = cw_mix(p->pri, under, pressed ? 0.7f : 0.8f);   /* hover:opacity-80 */
        if (disabled) { fill_c = cw_mix(p->pri, under, 0.5f); text_c = cw_mix(p->prifg, under, 0.5f); }
        border_c = fill_c;
        (void)border_c;
        c->fill_round(c->context, b, radius, fill_c);
    }
    else
    {
        text_c = p->secfg;
        border_c = p->secbd;
        if ((hot || pressed) && !disabled) { c->fill_round(c->context, b, radius, p->hover); text_c = p->fg; }   /* hover:bg-accent text-foreground */
        if (disabled) text_c = cw_mix(text_c, under, 0.5f);
        stroke_round(c, b, radius, cw.lay.line, border_c, 0);
    }
    cw_metrics m = metrics(c, label);
    float icon_w = icon ? cw_tdip(15) + cw_dip(6) : 0;
    float content = icon_w + m.width;
    float x = cw_snap(b.l + (cw_w(b) - content) / 2), line = cw_tdip(12.5f * 1.5f), y = b.t + (cw_h(b) - line) / 2;
    if (icon)
    {
        float k = cw_tdip(15) / 16;
        float iy = cw_snap(b.t + (cw_h(b) - cw_tdip(15)) / 2);
        c->mark(c->context, x, iy, k, text_c);
    }
    if (label) c->draw_text(c->context, label, x + icon_w, y, text_c);
}

static void draw_text(const cw_canvas *c, cw_text *t, float x, float y, cw_color color)
{
    if (t) c->draw_text(c->context, t, x, y, color);
}

static void fill(const cw_canvas *c, cw_box b, cw_color color) { c->fill(c->context, b, color); }

void cw_draw_caption(const cw_canvas *c, float height)
{
    const cw_palette *p = &cw.pal;
    cw_box all = { 0, 0, (float)cw.lay.width, height };
    fill(c, all, p->caption);
    int size = (int)cw_snap(cw_dip(16));
    float x = cw_snap(cw_dip(12)), y = cw_snap((height - size) / 2);
    c->icon(c->context, x, y, size);
    draw_text(c, cw.lay.t_caption, cw_snap(cw_dip(12 + 16 + 8)), (height - cw_dip(16)) / 2, p->fg);
    float cx = cw.lay.width - cw_dip(23), cy = height / 2, k = cw_dip(5);
    c->line(c->context, cx - k, cy - k, cx + k, cy + k, cw_dip(1), p->fg);
    c->line(c->context, cx + k, cy - k, cx - k, cy + k, cw_dip(1), p->fg);
}

static void overlay(const cw_canvas *c, int part, cw_box b);

void cw_draw(const cw_canvas *c, const cw_look *look, float offset)
{
    const cw_palette *p = &cw.pal;
    const cw_layout *l = &cw.lay;
    c->set_offset(c->context, offset);
    cw_box all = { 0, 0, (float)l->width, (float)l->height };
    fill(c, all, p->bg);

    draw_text(c, l->t_head, l->head.l, l->head.t, p->fg);
    if (c->picture) c->picture(c->context, l->picture);
    if (cw.blocked) draw_text(c, l->t_file, l->file.l, l->file.t, p->fg);
    draw_text(c, l->t_cause, l->cause.l, l->cause.t, p->fg2);
    overlay(c, C_CAUSE, l->cause);
    if (look->focus[C_CAUSE] && look->cues) cw_focus_ring(c, l->cause, cw_dip(4));

    cw_color toggle_c = look->hot[C_TOGGLE] ? p->fg : p->mut;
    draw_chevron(c, l->toggle.l, l->toggle.t + cw_tdip(9), cw.details_open, toggle_c);
    draw_text(c, l->t_toggle, l->toggle.l + cw_tdip(12), l->toggle.t, toggle_c);
    if (look->focus[C_TOGGLE] && look->cues) cw_focus_ring(c, l->toggle, cw_dip(4));

    if (cw.details_open)
    {
        c->fill_round(c->context, l->details, cw_dip(8), p->panel);
        stroke_round(c, l->details, cw_dip(8), l->line, p->border, 0);
        draw_text(c, l->t_details, l->details.l + l->line + cw_dip(12), l->details.t + l->line + cw_dip(10), p->fg2);
        overlay(c, C_DETAILS, l->details);
        if (look->focus[C_DETAILS] && look->cues) cw_focus_ring(c, l->details, cw_dip(8));
    }

    /* The note box: Windows' edit control sits inside it in the window; here, its placeholder. */
    if (!cw.blocked)
    {
        c->fill_round(c->context, l->note, cw_dip(8), p->panel);
        if (look->focus[C_NOTE])
        {
            cw_box ring = { l->note.l - l->line, l->note.t - l->line, l->note.r + l->line, l->note.b + l->line };
            stroke_round(c, ring, cw_dip(8) + l->line, 2 * l->line, p->ring, 0);   /* focus:ring-1 ring-ring, with the border */
        }
        else stroke_round(c, l->note, cw_dip(8), l->line, p->border, 0);
        if (look->note_empty) draw_text(c, l->t_note, l->note.l + l->line + cw_dip(10), l->note.t + l->line + cw_dip(8), p->mut);
        overlay(c, C_NOTE, l->note);
    }

    if (cw.report.dump_path[0] && !cw.blocked)
    {
        int disabled = !cw.can_include_dump;
        draw_check_box(c, l->check.l, l->check.t + cw_tdip(9), look->checked, look->hot[C_CHECK], disabled);
        draw_text(c, l->t_check, l->check.l + cw_tdip(13) + cw_dip(6), l->check.t, disabled ? cw_mix(p->fg2, p->bg, 0.5f) : p->fg2);
        if (look->focus[C_CHECK] && look->cues) cw_focus_ring(c, l->check, cw_dip(4));
    }

    if (cw.reported)
    {
        float radius = cw_dip(8);
        if (look->hot[C_CHIP] || look->pressed[C_CHIP]) c->fill_round(c->context, l->chip, radius, p->hover);
        stroke_round(c, l->chip, radius, l->line, look->hot[C_CHIP] ? p->mut : p->dark ? cw_rgb(0xFFFFFF, 0.18f) : p->border, 1);
        float x = l->chip.l + l->line + cw_dip(10), inner_t = l->chip.t + l->line + cw_dip(8), inner_h = cw_h(l->chip) - 2 * l->line - 2 * cw_dip(8);
        cw_box icon = cw_pixel_box(x, inner_t + (inner_h - cw_tdip(34)) / 2, x + cw_tdip(28), inner_t + (inner_h - cw_tdip(34)) / 2 + cw_tdip(34));
        c->fill_round(c->context, icon, cw_dip(4), p->panel);
        stroke_round(c, icon, cw_dip(4), l->line, p->border, 0);
        draw_text(c, l->t_zip, icon.l, icon.t + (cw_h(icon) - cw_tdip(14)) / 2, p->fg2);
        float tx = icon.r + cw_dip(10), th = metrics(c, l->t_chip_name).height + metrics(c, l->t_chip_hint).height;
        float ty = inner_t + (inner_h - th) / 2;
        draw_text(c, l->t_chip_name, tx, ty, p->fg);
        draw_text(c, l->t_chip_hint, tx, ty + metrics(c, l->t_chip_name).height, p->mut);
        if (look->focus[C_CHIP] && look->cues) cw_focus_ring(c, l->chip, radius);
        if (cw.links)
        {
            draw_text(c, l->t_folder, l->folder.l, l->folder.t, look->hot[C_FOLDER] ? p->fg : p->mut);
            if (look->focus[C_FOLDER] && look->cues) cw_focus_ring(c, l->folder, cw_dip(4));
            draw_text(c, l->t_copy, l->copy.l, l->copy.t, look->hot[C_COPY] ? p->fg : p->mut);
            if (look->focus[C_COPY] && look->cues) cw_focus_ring(c, l->copy, cw_dip(4));
        }
    }

    fill(c, l->footer, p->footer);
    cw_box rule = { 0, l->footer.t, (float)l->width, l->footer.t + l->line };
    fill(c, rule, p->border);
    draw_button(c, l->close, l->t_close, 0, look->hot[C_CLOSE], look->pressed[C_CLOSE], 0, 0);
    if (look->focus[C_CLOSE] && look->cues) cw_focus_ring(c, l->close, cw_dip(8));
    draw_button(c, l->report, l->t_report, 1, look->hot[C_REPORT], look->pressed[C_REPORT], cw.reported, !cw.reported);
    if (look->focus[C_REPORT] && look->cues) cw_focus_ring(c, l->report, cw_dip(8));
}

/* A platform's own drawing over a part (Linux: the selection, the note's text and caret). */
void (*cw_overlay)(int part, cw_box b);

static void overlay(const cw_canvas *c, int part, cw_box b)
{
    (void)c;
    if (cw_overlay) cw_overlay(part, b);
}

/* ---- the zip -------------------------------------------------------------------------------- */

static uint32_t crc_table[256];

static uint32_t crc32_of(uint32_t crc, const unsigned char *p, size_t n)
{
    if (!crc_table[1])
        for (uint32_t i = 0; i < 256; i++)
        {
            uint32_t c = i;
            for (int k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            crc_table[i] = c;
        }
    crc = ~crc;
    while (n--) crc = crc_table[(crc ^ *p++) & 255] ^ (crc >> 8);
    return ~crc;
}

/* Deflate (RFC 1951) with the fixed Huffman codes and a hash-chain LZ77: a little behind zlib on
 * text, plenty for a log and a dump. */
typedef struct
{
    unsigned char *out;
    size_t n;
    uint64_t bits;
    int count;
} bits_t;

static void bits_put(bits_t *b, uint32_t value, int length)
{
    b->bits |= (uint64_t)value << b->count;
    b->count += length;
    while (b->count >= 8) { b->out[b->n++] = (unsigned char)b->bits; b->bits >>= 8; b->count -= 8; }
}

static void bits_code(bits_t *b, uint32_t c, int length)   /* Huffman codes go most significant bit first */
{
    uint32_t r = 0;
    for (int i = 0; i < length; i++) { r = (r << 1) | (c & 1); c >>= 1; }
    bits_put(b, r, length);
}

static void bits_literal(bits_t *b, int symbol)
{
    if (symbol < 144) bits_code(b, 0x30 + symbol, 8);
    else if (symbol < 256) bits_code(b, 0x190 + symbol - 144, 9);
    else if (symbol < 280) bits_code(b, symbol - 256, 7);
    else bits_code(b, 0xC0 + symbol - 280, 8);
}

static const unsigned short length_base[29] = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
static const unsigned char length_extra[29] = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
static const unsigned short distance_base[30] = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073,
                                                  4097, 6145, 8193, 12289, 16385, 24577 };
static const unsigned char distance_extra[30] = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

static int hash_head[1 << 15], hash_prev[1 << 15];

static int hash3(const unsigned char *in, size_t at) { return (int)(((in[at] << 10) ^ (in[at + 1] << 5) ^ in[at + 2]) & 0x7FFF); }

/* `out` holds at least n + n / 8 + 64 bytes; returns the compressed length. */
static size_t deflate_fixed(const unsigned char *in, size_t n, unsigned char *out)
{
    const int window = 32768, chain_most = 48;
    for (int i = 0; i < (1 << 15); i++) hash_head[i] = -1;
    bits_t o = { out, 0, 0, 0 };
    bits_put(&o, 1, 1);   /* the last block */
    bits_put(&o, 1, 2);   /* fixed codes */
    size_t i = 0;
    while (i < n)
    {
        int best = 0, distance = 0;
        if (i + 3 <= n)
        {
            int limit = (int)(n - i < 258 ? n - i : 258);
            int candidate = hash_head[hash3(in, i)], chain = chain_most, last = (int)i;
            while (candidate >= 0 && candidate < last && (int)i - candidate <= window && chain-- > 0)
            {
                if (in[candidate + best] == in[i + best])
                {
                    int length = 0;
                    while (length < limit && in[candidate + length] == in[i + length]) length++;
                    if (length > best) { best = length; distance = (int)i - candidate; if (length == limit) break; }
                }
                last = candidate;
                candidate = hash_prev[candidate & (window - 1)];
            }
        }
        int step = best >= 3 ? best : 1;
        if (best >= 3)
        {
            int c = 28;
            while (length_base[c] > best) c--;
            bits_literal(&o, 257 + c);
            if (length_extra[c]) bits_put(&o, (uint32_t)(best - length_base[c]), length_extra[c]);
            int d = 29;
            while (distance_base[d] > distance) d--;
            bits_code(&o, (uint32_t)d, 5);
            if (distance_extra[d]) bits_put(&o, (uint32_t)(distance - distance_base[d]), distance_extra[d]);
        }
        else bits_literal(&o, in[i]);
        for (int k = 0; k < step; k++)
        {
            size_t at = i + (size_t)k;
            if (at + 3 > n) continue;
            int h = hash3(in, at);
            hash_prev[at & (size_t)(window - 1)] = hash_head[h];
            hash_head[h] = (int)at;
        }
        i += (size_t)step;
    }
    bits_literal(&o, 256);
    if (o.count) bits_put(&o, 0, 8 - o.count);
    return o.n;
}

typedef struct
{
    char name[300];
    uint32_t crc, packed, size, offset;
    unsigned short method, time, date;
} zip_entry;

static void put16(unsigned char **p, uint32_t v) { *(*p)++ = (unsigned char)v; *(*p)++ = (unsigned char)(v >> 8); }
static void put32(unsigned char **p, uint32_t v) { put16(p, v & 0xFFFF); put16(p, v >> 16); }

/* A file's bytes, or for a log over 32 MB its first 4 MB and last 28 MB with a line between. */
static unsigned char *read_file(const char *path, size_t *n, int trim)
{
    FILE *f = cw_open(path, "rb");
    if (!f) return NULL;
    unsigned char *data = NULL;
    const uint64_t head = 4ull << 20, tail = 28ull << 20;
    uint64_t total = cw_file_size(f);
    int cut = trim && total > head + tail;
    char marker[128];
    int marker_n = cut ? snprintf(marker, sizeof marker, "\r\n[... %llu bytes of the log left out by the crash window ...]\r\n", (unsigned long long)(total - head - tail)) : 0;
    uint64_t want = cut ? head + tail + (uint64_t)marker_n : total;
    data = want < (1ull << 31) ? (unsigned char *)malloc((size_t)want + 1) : NULL;
    int ok = data != NULL;
    if (ok && cut)
    {
        ok = fread(data, 1, (size_t)head, f) == head;
        memcpy(data + head, marker, (size_t)marker_n);
        ok = ok && cw_seek(f, total - tail) && fread(data + head + marker_n, 1, (size_t)tail, f) == tail;
    }
    else if (ok) ok = fread(data, 1, (size_t)want, f) == want;
    fclose(f);
    if (!ok) { free(data); return NULL; }
    *n = (size_t)want;
    return data;
}

const uint64_t cw_zip_most = 24ull << 20;
const uint64_t cw_dump_most = 96ull << 20;

int cw_write_zip(const char *path, int with_dump, uint64_t *written)
{
    const char *sources[2] = { cw.report.log_path, with_dump ? cw.report.dump_path : NULL };
    zip_entry entries[2];
    int count = 0;
    unsigned char *packed[2] = { NULL, NULL };
    uint64_t total = 0;
    const cw_report *t = &cw.report;
    unsigned short dos_time = (unsigned short)((t->hour << 11) | (t->minute << 5) | (t->second / 2));
    unsigned short dos_date = (unsigned short)(((t->year - 1980) << 9) | (t->month << 5) | t->day);
    for (int i = 0; i < 2; i++)
    {
        if (!sources[i] || !sources[i][0]) continue;
        size_t n = 0;
        unsigned char *data = read_file(sources[i], &n, i == 0);
        if (!data) continue;
        unsigned char *out = (unsigned char *)malloc(n + n / 8 + 64);
        if (!out) { free(data); continue; }
        zip_entry *e = &entries[count];
        snprintf(e->name, sizeof e->name, "%s", cw_file_name(sources[i]));
        e->crc = crc32_of(0, data, n);
        e->size = (uint32_t)n;
        size_t z = deflate_fixed(data, n, out);
        if (z < n) { e->method = 8; e->packed = (uint32_t)z; }
        else { e->method = 0; e->packed = (uint32_t)n; memcpy(out, data, n); }
        e->time = dos_time;
        e->date = dos_date;
        free(data);
        if (i == 1 && total + e->packed > cw_zip_most) { free(out); break; }
        packed[count++] = out;
        total += e->packed;
    }
    if (count == 0) return 0;
    size_t most = (size_t)total + 4096;
    unsigned char *zip = (unsigned char *)malloc(most), *p = zip;
    int ok = zip != NULL;
    if (ok)
    {
        for (int i = 0; i < count; i++)
        {
            zip_entry *e = &entries[i];
            uint32_t name_n = (uint32_t)strlen(e->name);
            e->offset = (uint32_t)(p - zip);
            put32(&p, 0x04034B50); put16(&p, 20); put16(&p, 0x0800); put16(&p, e->method); put16(&p, e->time); put16(&p, e->date);
            put32(&p, e->crc); put32(&p, e->packed); put32(&p, e->size); put16(&p, name_n); put16(&p, 0);
            memcpy(p, e->name, name_n); p += name_n;
            memcpy(p, packed[i], e->packed); p += e->packed;
        }
        unsigned char *directory = p;
        for (int i = 0; i < count; i++)
        {
            zip_entry *e = &entries[i];
            uint32_t name_n = (uint32_t)strlen(e->name);
            put32(&p, 0x02014B50); put16(&p, 20); put16(&p, 20); put16(&p, 0x0800); put16(&p, e->method); put16(&p, e->time); put16(&p, e->date);
            put32(&p, e->crc); put32(&p, e->packed); put32(&p, e->size); put16(&p, name_n); put16(&p, 0); put16(&p, 0); put16(&p, 0); put16(&p, 0);
            put32(&p, 0x20); put32(&p, e->offset);
            memcpy(p, e->name, name_n); p += name_n;
        }
        uint32_t directory_n = (uint32_t)(p - directory), directory_at = (uint32_t)(directory - zip);
        put32(&p, 0x06054B50); put16(&p, 0); put16(&p, 0); put16(&p, (uint32_t)count); put16(&p, (uint32_t)count);
        put32(&p, directory_n); put32(&p, directory_at); put16(&p, 0);
        FILE *f = cw_open(path, "wb");
        ok = f && fwrite(zip, 1, (size_t)(p - zip), f) == (size_t)(p - zip);
        if (f && fclose(f) != 0) ok = 0;
        if (!ok) remove(path);
        *written = (uint64_t)(p - zip);
        free(zip);
    }
    for (int i = 0; i < count; i++) free(packed[i]);
    return ok;
}

void cw_zip_path(char *out, size_t most)
{
    snprintf(out, most, "%s", cw.report.log_path);
    char *name = (char *)cw_file_name(out);
    const cw_report *t = &cw.report;
    snprintf(name, most - (size_t)(name - out), "OniMoono-crash-%04d%02d%02d-%02d%02d.zip", t->year, t->month, t->day, t->hour, t->minute);
}

/* ---- the PNG -------------------------------------------------------------------------------- */

static void png_chunk(FILE *f, const char *kind, const unsigned char *data, uint32_t n, int *ok)
{
    unsigned char head[8] = { (unsigned char)(n >> 24), (unsigned char)(n >> 16), (unsigned char)(n >> 8), (unsigned char)n,
                              (unsigned char)kind[0], (unsigned char)kind[1], (unsigned char)kind[2], (unsigned char)kind[3] };
    uint32_t crc = crc32_of(0, head + 4, 4);
    crc = crc32_of(crc, data, n);
    unsigned char tail[4] = { (unsigned char)(crc >> 24), (unsigned char)(crc >> 16), (unsigned char)(crc >> 8), (unsigned char)crc };
    if (fwrite(head, 1, 8, f) != 8 || (n && fwrite(data, 1, n, f) != n) || fwrite(tail, 1, 4, f) != 4) *ok = 0;
}

int cw_write_png(const char *path, const unsigned char *rgba, int width, int height, int stride)
{
    size_t row = (size_t)width * 4 + 1, n = row * (size_t)height;
    unsigned char *raw = (unsigned char *)malloc(n), *z = (unsigned char *)malloc(n + n / 8 + 64 + 6);
    if (!raw || !z) { free(raw); free(z); return 0; }
    for (int y = 0; y < height; y++)
    {
        unsigned char *line = raw + row * (size_t)y;
        const unsigned char *src = rgba + (size_t)stride * (size_t)y;
        line[0] = 1;   /* the Sub filter: a pixel less its left neighbour */
        for (int x = 0; x < width * 4; x++) line[1 + x] = (unsigned char)(src[x] - (x >= 4 ? src[x - 4] : 0));
    }
    /* zlib: its header, deflate, Adler-32 */
    z[0] = 0x78;
    z[1] = 0x01;
    size_t zn = 2 + deflate_fixed(raw, n, z + 2);
    uint32_t a = 1, b = 0;
    for (size_t i = 0; i < n; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
    uint32_t adler = (b << 16) | a;
    z[zn++] = (unsigned char)(adler >> 24); z[zn++] = (unsigned char)(adler >> 16); z[zn++] = (unsigned char)(adler >> 8); z[zn++] = (unsigned char)adler;
    FILE *f = cw_open(path, "wb");
    int ok = f != NULL;
    if (f)
    {
        static const unsigned char signature[8] = { 0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n' };
        unsigned char header[13] = { (unsigned char)(width >> 24), (unsigned char)(width >> 16), (unsigned char)(width >> 8), (unsigned char)width,
                                     (unsigned char)(height >> 24), (unsigned char)(height >> 16), (unsigned char)(height >> 8), (unsigned char)height,
                                     8, 6, 0, 0, 0 };
        if (fwrite(signature, 1, 8, f) != 8) ok = 0;
        png_chunk(f, "IHDR", header, 13, &ok);
        png_chunk(f, "IDAT", z, (uint32_t)zn, &ok);
        png_chunk(f, "IEND", NULL, 0, &ok);
        if (fclose(f) != 0) ok = 0;
    }
    free(raw);
    free(z);
    return ok;
}

/* ---- the GitHub issue ----------------------------------------------------------------------- */

static void url_encode(const char *text, char *out, size_t most, size_t *n)
{
    for (const unsigned char *c = (const unsigned char *)text; *c && *n + 4 < most; c++)
    {
        if ((*c >= 'A' && *c <= 'Z') || (*c >= 'a' && *c <= 'z') || (*c >= '0' && *c <= '9') || *c == '-' || *c == '_' || *c == '.' || *c == '~')
            out[(*n)++] = (char)*c;
        else
        {
            out[(*n)++] = '%';
            out[(*n)++] = "0123456789ABCDEF"[*c >> 4];
            out[(*n)++] = "0123456789ABCDEF"[*c & 15];
        }
    }
    out[*n] = 0;
}

/* The new-issue URL on the crash form (.github/ISSUE_TEMPLATE/crash.yml): the title
 * "Crash: <error>", its "details" field (the error and the details, which the form shows as a code
 * block) and its "doing" field (the note). Its "zip" field is left empty: the form takes no
 * report until the player adds the zip there. Under 7,500 characters (what browsers and GitHub
 * take in a link): the note is cut short with "…". */
void cw_issue_url(const char *note, char *url, size_t most)
{
    static char title[1200], details[4096], english[2048], cut_note[8192];
    static char encoded[8000];
    snprintf(title, sizeof title, "Crash: %s", cw.error);
    cw_details_text(&cw.report, &cw.system, CW_ENGLISH, english, sizeof english);
    snprintf(details, sizeof details, "%s\n%s", cw.error, english);
    size_t note_length = strlen(note), note_n = note_length;
    for (;;)
    {
        size_t keep = note_n < sizeof cut_note - 4 ? note_n : sizeof cut_note - 4;
        while (keep > 0 && keep < note_length && (note[keep] & 0xC0) == 0x80) keep--;   /* whole characters */
        memcpy(cut_note, note, keep);
        cut_note[keep] = 0;
        if (keep < note_length) strcat(cut_note, "\xE2\x80\xA6");
        size_t n = (size_t)snprintf(encoded, sizeof encoded, "%s?template=crash.yml&title=", cw_issue_base);
        url_encode(title, encoded, sizeof encoded, &n);
        n += (size_t)snprintf(encoded + n, sizeof encoded - n, "&details=");
        url_encode(details, encoded, sizeof encoded, &n);
        if (cut_note[0])
        {
            n += (size_t)snprintf(encoded + n, sizeof encoded - n, "&doing=");
            url_encode(cut_note, encoded, sizeof encoded, &n);
        }
        if (n <= 7500 || note_n == 0) break;
        note_n = note_n > 200 ? note_n - note_n / 4 : note_n - 1;
    }
    snprintf(url, most, "%s", encoded);
}

/* ---- --crash-dialog-render ------------------------------------------------------------------- */

int cw_render_args(int argc, char **argv, int windows, cw_render_options *o, cw_report *r)
{
    if (argc < 3) return 0;
    memset(o, 0, sizeof *o);
    memset(r, 0, sizeof *r);
    o->out = argv[2];
    o->note = "";
    o->dark = 1;
    o->scale = 1.0f;
    o->text = 1.0f;
    const char *sample = "gpu";
    r->language = CW_ENGLISH;
    for (int i = 3; i < argc; i++)
    {
        const char *a = argv[i], *v = i + 1 < argc ? argv[i + 1] : "";
        if (strcmp(a, "--theme") == 0) { o->dark = strcmp(v, "light") != 0; i++; }
        else if (strcmp(a, "--scale") == 0) { o->scale = (float)atof(v); i++; }
        else if (strcmp(a, "--text-size") == 0) { o->text = (float)atof(v); i++; }
        else if (strcmp(a, "--sample") == 0) { sample = v; i++; }
        else if (strcmp(a, "--language") == 0) { r->language = cw_language_of(v); i++; }
        else if (strcmp(a, "--zip") == 0) { o->zip_out = v; i++; }
        else if (strcmp(a, "--log") == 0) { o->log = v; i++; }
        else if (strcmp(a, "--dump") == 0) { o->dump = v; i++; }
        else if (strcmp(a, "--url") == 0) { o->url_out = v; i++; }
        else if (strcmp(a, "--note") == 0) { o->note = v; i++; }
        else if (strcmp(a, "--details-open") == 0) o->details = 1;
        else if (strcmp(a, "--reported") == 0) o->reported = 1;
        else return 0;
    }
    if (o->scale < 0.5f || o->scale > 4.0f || o->text < 1.0f || o->text > 2.25f) return 0;
    /* the mockup's sample */
    const char *folder = windows ? "C:\\Users\\Player\\AppData\\LocalLow\\Klei\\Oxygen Not Included\\"
                                 : "/home/player/.config/unity3d/Klei/Oxygen Not Included/";
    snprintf(r->version, sizeof r->version, "v2026.11.06.01");
    snprintf(r->game_build, sizeof r->game_build, "762230");
    r->mods = 214;
    r->play_seconds = 2 * 3600 + 14 * 60 + 9;
    snprintf(r->log_path, sizeof r->log_path, "%sPlayer.log", folder);
    r->year = 2026; r->month = 11; r->day = 7; r->hour = 14; r->minute = 15; r->second = 2;
    if (strcmp(sample, "gpu") == 0)
    {
        snprintf(r->error, sizeof r->error, windows ? "0xC0000005 (access violation) in nvoglv64.dll" : "SIGSEGV (invalid memory access) in libnvidia-glcore.so.580.95.05");
        snprintf(r->dump_path, sizeof r->dump_path, "%scrash-20261107-141502.dmp", folder);
        r->dump_size = 5976883;
    }
    else if (strcmp(sample, "managed") == 0)
        snprintf(r->error, sizeof r->error, "System.NullReferenceException in BetterInfoCards.InfoCardWidgets.Update: Object reference not set to an instance of an object.");
    else if (strcmp(sample, "blocked") == 0)
        /* #135's: Smart App Control refused a player assembly the JIT loads */
        snprintf(r->error, sizeof r->error, "System.IO.FileLoadException in OniMoono.Host.Player.Run: Could not load file or assembly '%s'. "
                 "An Application Control policy has blocked this file. (0x800711C7)",
                 windows ? "C:\\Users\\Player\\AppData\\Local\\oni-moono\\player\\Brutal.Vulkan.dll" : "/home/player/.local/share/oni-moono/player/Brutal.Vulkan.dll");
    else if (strcmp(sample, "fastfail") == 0)
        snprintf(r->error, sizeof r->error, windows ? "0xC0000409 (fast fail)" : "SIGABRT (abort): free(): double free detected in tcache 2");
    else return 0;
    if (o->log) snprintf(r->log_path, sizeof r->log_path, "%s", o->log);
    if (o->dump)
    {
        snprintf(r->dump_path, sizeof r->dump_path, "%s", o->dump);
        FILE *f = cw_open(o->dump, "rb");
        r->dump_size = f ? cw_file_size(f) : 0;
        if (f) fclose(f);
    }
    return 1;
}

void cw_render_state(const cw_render_options *o, const cw_report *r, int windows)
{
    cw.render_only = 1;
    cw_prepare(r);
    if (windows)
    {
        snprintf(cw.system.os, sizeof cw.system.os, "Windows 11 24H2 (26100.4061)");
        snprintf(cw.system.gpu, sizeof cw.system.gpu, "NVIDIA GeForce RTX 3090 (581.29)");
    }
    else
    {
        snprintf(cw.system.os, sizeof cw.system.os, "Ubuntu 24.04.3 LTS (Linux 6.14.0-35)");
        snprintf(cw.system.gpu, sizeof cw.system.gpu, "NVIDIA GeForce RTX 3090 (580.95.05)");
    }
    cw.system.ram_gb = 32;
    cw_details_text(&cw.report, &cw.system, r->language, cw.details, sizeof cw.details);
    cw.scale = o->scale;
    cw.text = o->text;
    cw.pal = cw_palette_for(o->dark);
    cw.details_open = o->details;
    if (o->reported)
    {
        cw.reported = 1;
        snprintf(cw.chip_path, sizeof cw.chip_path, "%sOniMoono-crash-20261107-1415.zip",
                 windows ? "C:\\Users\\Player\\AppData\\LocalLow\\Klei\\Oxygen Not Included\\" : "/home/player/.config/unity3d/Klei/Oxygen Not Included/");
        cw.chip_size = 1993728;
    }
}
