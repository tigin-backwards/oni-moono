#!/usr/bin/env python3
"""Checks the probe's "LX" lines (LegacyText.cs, PROBE_SCENARIO=legacytext).

  lx-check.py rule LOG
      checks the legacy TextGenerator's placement against the rule in docs/cleanroom/ugui.md
      ("Legacy Text glyph placement"), with the log's own CharacterInfo table (the "ci" lines)
      as the glyph metrics: the pixel size is floor(fontSize x scaleFactor); each character's
      width is its CharacterInfo advance at that size; cursors are the running sum (no
      kerning); each quad spans the cursor plus minX..maxX; the preferred width is the line's
      sum. Prints the cases each check fails, then the counts.
  lx-check.py face LOG FONT [STYLE]
      compares the "ci" lines of STYLE (default Normal) with FreeType's unhinted metrics of
      FONT (a .ttf), loaded at each integer pixel size: the advance rounded half away from
      zero, and the bitmap box grown by one pixel on each side. Needs libfreetype.so.6.
  lx-check.py diff [--no-uv] ORIG OURS [KIND...]
      the "ci", "gen", "align" and "text" lines that differ between the players, by their
      inputs, with counts per kind of line (KIND: a substring such as "cx" or "align").
      --no-uv leaves out the texture coordinates (CharacterInfo UVs, the quads' UVs), which
      depend on where each player's packer put the glyph in its font texture.

A log is a Player.log (original) or our player's stdout. Advances and positions are compared
as numbers: the "R" format differs between Mono and .NET.
"""

import collections
import math
import re
import sys


def lines(path):
    for line in open(path, errors='replace'):
        m = re.search(r'PROBE f\d+ LX (.*)$', line.rstrip('\n'))
        if m:
            yield m.group(1)


CI = re.compile(
    r'ci size=(\d+) style=(\w+) c=(\d+) ok=(\w+) adv=(-?\d+) width=(\S+) minX=(-?\d+) maxX=(-?\d+) '
    r'minY=(-?\d+) maxY=(-?\d+) gw=(\d+) gh=(\d+)'
)


def char_infos(path):
    table = {}
    for body in lines(path):
        m = CI.match(body)
        if m:
            size, style, c = int(m.group(1)), m.group(2), int(m.group(3))
            table[(size, style, c)] = dict(
                ok=m.group(4) == 'True',
                adv=int(m.group(5)),
                width=float(m.group(6)),
                minX=int(m.group(7)),
                maxX=int(m.group(8)),
                minY=int(m.group(9)),
                maxY=int(m.group(10)),
            )
    return table


def unquote(s):
    out, i = [], 0
    while i < len(s):
        if s[i] == '\\' and i + 1 < len(s):
            out.append({'n': '\n'}.get(s[i + 1], s[i + 1]))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return ''.join(out)


GEN = re.compile(
    r'gen (fontSize=(\d+) scale=(\S+) style=(\w+) anchor=(\w+) wrap=(\w+)(?: bestFit=(\S+))?(?: width=(\S+))? str="(.*)") '
    r'(pref=.*|cx=.*|cw=.*|lines=.*|quads=.*)$'
)


def generations(path):
    gens = collections.OrderedDict()
    for body in lines(path):
        m = GEN.match(body)
        if not m:
            continue
        key = m.group(1)
        g = gens.setdefault(
            key,
            dict(
                size=int(m.group(2)),
                scale=float(m.group(3)),
                style=m.group(4),
                anchor=m.group(5),
                wrap=m.group(6),
                bestFit=m.group(7),
                width=m.group(8),
                str=unquote(m.group(9)),
            ),
        )
        rest = m.group(10)
        if rest.startswith('pref='):
            for k, v in re.findall(r'(\w+)=(\S+)', rest):
                g[k] = v
        elif rest.startswith('cx='):
            g['cx'] = [float(x) for x in rest[3:].split(',') if x]
        elif rest.startswith('cw='):
            g['cw'] = [float(x) for x in rest[3:].split(',') if x]
        elif rest.startswith('lines='):
            g['lines'] = rest[6:]
        elif rest.startswith('quads='):
            g['quads'] = [[float(v) for v in q.split('/')] for q in rest[6:].split(' ') if q]
    return gens


def f32_floor_product(size, scale):
    import struct

    s = struct.unpack('f', struct.pack('f', scale))[0]
    p = struct.unpack('f', struct.pack('f', size * s))[0]
    return math.floor(p)


# LegacyRuntime's serialized kerning (m_KerningValues of "unity default resources", read from the
# game's files): 21 pairs, each -1 pixel at the font's own size (m_FontSize 13).
LEGACY_KERNING = {
    p: -1.0 for p in ('F,', 'F.', 'P,', 'P.', 'T,', 'T.', 'T:', 'T;', 'Ta', 'Tc', 'Te', 'To', 'Ts', 'V,', 'V.', 'Y,', 'Y-', 'Y.', 'Ye', 'Yo', 'Yq')
}
LEGACY_FONT_SIZE = 13
# Its serialized m_Ascent, m_Descent and m_LineSpacing (at m_FontSize).
ASCENT, DESCENT, LINE_SPACING = 11.7685546875, -2.7548828125, 14.94873046875


def round_half_up(v):
    return math.floor(v + 0.5)


def kerning(pair, px):
    k = LEGACY_KERNING.get(pair)
    return 0.0 if k is None else float(math.floor(k * px / LEGACY_FONT_SIZE + 0.5))


def layout(s, px, style, ci):
    """The measured rule for one line from x 0: (cursors, quads x, pen end)."""
    pen, cursors, quads = 0.0, [], []
    for i, c in enumerate(s):
        cursors.append(pen)
        if i > 0:
            pen += kerning(s[i - 1 : i + 1], px)
        info = ci[(px, style, ord(c))]
        if c != ' ':
            quads.append((pen + info['minX'], pen + info['maxX']))
        pen += info['adv']
    cursors.append(pen)
    return cursors, quads, pen


def rule(path):
    ci = char_infos(path)
    gens = generations(path)
    counts = collections.Counter()
    fails = collections.defaultdict(list)

    def check(name, ok, detail):
        counts[name, ok] += 1
        if not ok and len(fails[name]) < 8:
            fails[name].append(detail)

    for key, g in gens.items():
        if g['bestFit'] or '<' in g['str']:
            continue
        px = f32_floor_product(g['size'], g['scale'])
        check('pixel size = floor(fontSize x scale)', int(g.get('used', -1)) == px, (key, g.get('used'), px))
        s, style = g['str'], g['style']
        widths = [ci.get((px, style, ord(c)), {}).get('adv') for c in s]
        if None in widths:
            continue
        cw = g.get('cw', [])
        check('charWidth = CharacterInfo.advance', cw[: len(s)] == widths, (key, cw, widths))
        if g['wrap'] == 'Overflow' and '\n' not in s:
            cx = g.get('cx', [])
            start = cx[0] if cx else 0.0
            cursors, qx, end = layout(s, px, style, ci)
            run = [start + x for x in cursors]
            check('cursor = pen before kerning (advances + kerning)', cx == run, (key, cx, run))
            check('preferred width = pen at the end', float(g.get('pref', 'nan')) == end, (key, g.get('pref'), end))
            quads = g.get('quads', [])
            expect = [(start + a, start + b) for a, b in qx]
            got = [(q[0], q[1]) for q in quads]
            check('quad x = kerned pen + minX .. maxX, no quad for a space', got == expect, (key, got, expect))
            if g['anchor'] == 'UpperLeft' and g.get('lines', '').count(';') == 0 and 'lines' in g:
                # LegacyRuntime's serialized line metrics at its m_FontSize, scaled.
                asc, desc, spacing = (
                    round_half_up(ASCENT * px / LEGACY_FONT_SIZE),
                    round_half_up(-DESCENT * px / LEGACY_FONT_SIZE),
                    LINE_SPACING * px / LEGACY_FONT_SIZE,
                )
                start, height, top, leading = g['lines'].split(':')
                check('preferred height = round(ascent) + round(descent)', float(g['prefH']) == asc + desc, (key, g['prefH'], asc + desc))
                check('UILineInfo.height = floor(line spacing)', int(height) == math.floor(spacing), (key, height, spacing))
                check('UILineInfo.leading = round(line spacing) - block', float(leading) == round_half_up(spacing) - asc - desc, (key, leading))
            if g['anchor'] == 'UpperLeft' and len(quads) == len(qx):
                # Baseline: the quads' y against the CharacterInfo's minY/maxY.
                drawn = [c for c in s if c != ' ']
                offs = {q[3] - ci[(px, style, ord(c))]['maxY'] for q, c in zip(quads, drawn)}
                offs |= {q[2] - ci[(px, style, ord(c))]['minY'] for q, c in zip(quads, drawn)}
                check('quad y = baseline + minY .. baseline + maxY', len(offs) == 1, (key, sorted(offs)))
                if len(offs) == 1:
                    off = offs.pop()
                    check('baseline = line top - round(ascent)', -off == round_half_up(ASCENT * px / LEGACY_FONT_SIZE), (key, off))
                    counts['baseline', px, off] += 1
    # Alignment ("align" lines, 13 pixels, updateBounds off): the line's width L and the block's
    # height B come from the same string's UpperLeft generation at 13 pixels.
    widths = {
        g['str']: (float(g['pref']), float(g['prefH']))
        for g in gens.values()
        if g['size'] == 13
        and g['scale'] == 1.0
        and g['anchor'] == 'UpperLeft'
        and g['wrap'] == 'Overflow'
        and g['style'] == 'Normal'
        and not g['width']
        and 'pref' in g
    }
    rx = lambda v: math.floor(v + 0.5)
    ry = lambda v: math.ceil(v - 0.5)
    for body in lines(path):
        m = re.match(r'align str="(.*?)" pivot=(\S+),(\S+) anchor=(\w+) w=(\S+) h=(\S+) cx0=(\S+) cy0=\S+ topY=(\S+)', body)
        if not m or unquote(m.group(1)) not in widths:
            continue
        line_w, block_h = widths[unquote(m.group(1))]
        px_, py_, w, h, cx0, top = (float(m.group(i)) for i in (2, 3, 5, 6, 7, 8))
        anchor = m.group(4)
        W, H = w + 0.0001, h + 0.0001
        x_min, y_max = -px_ * W, (1 - py_) * H
        x = rx(x_min) if anchor.endswith('Left') else rx(x_min + W / 2) - math.ceil(line_w / 2) if anchor.endswith('Center') else rx(x_min + W) - line_w
        t = ry(y_max) if anchor.startswith('Upper') else ry(y_max - (H - block_h) / 2) if anchor.startswith('Middle') else ry(y_max - H + block_h)
        check('aligned line start x', cx0 == x, (body, x))
        check('aligned block top y', top == t, (body, t))
    for name, items in fails.items():
        print('FAIL', name)
        for d in items:
            print('   ', d)
    for (name, *rest), n in sorted(counts.items(), key=lambda kv: str(kv[0])):
        if name != 'baseline':
            print(f'{n:6d} {name} {"ok" if rest[0] else "FAILED"}')
    base = collections.defaultdict(set)
    for (name, *rest), n in counts.items():
        if name == 'baseline':
            base[rest[0]].add(rest[1])
    print(
        'baseline below the line top (UpperLeft, pivot top-left), by pixel size:',
        ' '.join(f'{px}:{",".join(str(-b) for b in sorted(v))}' for px, v in sorted(base.items())),
    )


def face(path, font, style='Normal'):
    import ctypes as C

    ft = C.CDLL('libfreetype.so.6')

    class Vec(C.Structure):
        _fields_ = [('x', C.c_long), ('y', C.c_long)]

    class Metrics(C.Structure):
        _fields_ = [(n, C.c_long) for n in ('w', 'h', 'bx', 'by', 'adv', 'vbx', 'vby', 'vadv')]

    class Bitmap(C.Structure):
        _fields_ = [
            ('rows', C.c_uint),
            ('width', C.c_uint),
            ('pitch', C.c_int),
            ('buffer', C.c_void_p),
            ('num_grays', C.c_ushort),
            ('pixel_mode', C.c_ubyte),
            ('palette_mode', C.c_ubyte),
            ('palette', C.c_void_p),
        ]

    class Slot(C.Structure):
        _fields_ = [
            ('library', C.c_void_p),
            ('face', C.c_void_p),
            ('next', C.c_void_p),
            ('glyph_index', C.c_uint),
            ('gdata', C.c_void_p),
            ('gfin', C.c_void_p),
            ('metrics', Metrics),
            ('linearHoriAdvance', C.c_long),
            ('linearVertAdvance', C.c_long),
            ('advance', Vec),
            ('format', C.c_uint),
            ('bitmap', Bitmap),
            ('bitmap_left', C.c_int),
            ('bitmap_top', C.c_int),
        ]

    class Face(C.Structure):
        _fields_ = [
            ('counts', C.c_long * 5),
            ('family', C.c_char_p),
            ('style', C.c_char_p),
            ('nfixed', C.c_int),
            ('sizes', C.c_void_p),
            ('ncharmaps', C.c_int),
            ('charmaps', C.c_void_p),
            ('gdata', C.c_void_p),
            ('gfin', C.c_void_p),
            ('bbox', C.c_long * 4),
            ('upem', C.c_ushort),
            ('asc', C.c_short),
            ('desc', C.c_short),
            ('height', C.c_short),
            ('maw', C.c_short),
            ('mah', C.c_short),
            ('up', C.c_short),
            ('ut', C.c_short),
            ('glyph', C.POINTER(Slot)),
        ]

    lib = C.c_void_p()
    ft.FT_Init_FreeType(C.byref(lib))
    fp = C.POINTER(Face)()
    assert ft.FT_New_Face(lib, font.encode(), 0, C.byref(fp)) == 0
    counts = collections.Counter()
    fails = []
    for (size, st, c), info in sorted(char_infos(path).items()):
        if st != style or size == 0 or not info['ok']:
            continue
        ft.FT_Set_Char_Size(fp, 0, size * 64, 72, 72)
        ft.FT_Load_Glyph(fp, ft.FT_Get_Char_Index(fp, c), 2)  # FT_LOAD_NO_HINTING
        adv = fp.contents.glyph.contents.advance.x
        ft.FT_Render_Glyph(fp.contents.glyph, 0)
        g = fp.contents.glyph.contents
        l, w, t, h = g.bitmap_left, g.bitmap.width, g.bitmap_top, g.bitmap.rows
        want = dict(adv=(adv + 32) // 64, minX=l - 1, maxX=l + w + 1, minY=t - h - 1, maxY=t + 1)
        ok = all(info[k] == v for k, v in want.items())
        counts[ok] += 1
        if not ok and len(fails) < 20:
            fails.append((size, chr(c), {k: info[k] for k in want}, want))
    for f in fails:
        print('FAIL', f)
    print(f'{counts[True]} of {counts[True] + counts[False]} {style} CharacterInfos match {font}')


def diff(orig, ours, kinds=None, uv=True):
    """Lines keyed by their inputs (everything up to the string, or the character, plus the
    field name that follows); the values are compared as numbers."""

    def keyed(path):
        d = collections.OrderedDict()
        for body in lines(path):
            m = (
                re.match(r'((?:gen|text) .*?str="(?:[^"\\]|\\.)*"(?: mesh| gen)?) (\w+=.*)$', body)
                or re.match(r'((?:ci|osglyph) .*? c=\S+) (.*)$', body)
                or re.match(r'(raster .*? style=\w+) (.*)$', body)
                or re.match(r'(align .*? h=\S+) (.*)$', body)
            )
            if not m:
                continue
            key, rest = m.group(1), m.group(2)
            if key.startswith(('gen', 'text')):
                field = rest.split('=', 1)[0]
                key += ' ' + field
            if not uv:
                rest = re.sub(r' uv(BL|TR|TL)=\S+| quadUV=\S+', '', rest)
                if rest.startswith('quads='):
                    rest = 'quads=' + ' '.join('/'.join(q.split('/')[:4]) for q in rest[6:].split(' ') if q)
            d[key] = rest
        return d

    a, b = keyed(orig), keyed(ours)
    same = differ = 0
    by_kind = collections.Counter()
    shown = collections.Counter()
    for k, v in a.items():
        if k not in b:
            continue
        kind = k.split(' ', 1)[0] + ' ' + (k.rsplit(' ', 1)[-1] if k.startswith(('gen', 'text')) else '')
        if kinds and not any(x in kind for x in kinds):
            continue
        if b[k] == v or _numeq(b[k], v):
            same += 1
            by_kind[kind, True] += 1
        else:
            differ += 1
            by_kind[kind, False] += 1
            if shown[kind] < 6:
                shown[kind] += 1
                print('DIFF', k, '\n  orig', v[:300], '\n  ours', b[k][:300])
    for (kind, eq), n in sorted(by_kind.items()):
        print(f'{n:6d} {kind} {"equal" if eq else "differ"}')
    print(f'{same} equal, {differ} differ, {len(set(a) - set(b))} only in the original, {len(set(b) - set(a))} only in ours')


def _numeq(x, y):
    tx, ty = re.split(r'([-0-9.Ee]+)', x), re.split(r'([-0-9.Ee]+)', y)
    if len(tx) != len(ty):
        return False
    for i, (p, q) in enumerate(zip(tx, ty)):
        if i % 2:
            try:
                if abs(float(p) - float(q)) > 1e-4:
                    return False
            except ValueError:
                if p != q:
                    return False
        elif p != q:
            return False
    return True


if __name__ == '__main__':
    if len(sys.argv) >= 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    elif len(sys.argv) >= 4 and sys.argv[1] == 'face':
        face(sys.argv[2], sys.argv[3], *(sys.argv[4:5]))
    elif len(sys.argv) >= 5 and sys.argv[1] == 'diff' and sys.argv[2] == '--no-uv':
        diff(sys.argv[3], sys.argv[4], sys.argv[5:], uv=False)
    elif len(sys.argv) >= 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3], sys.argv[4:])
    else:
        sys.exit(__doc__)
