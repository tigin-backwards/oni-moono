#!/usr/bin/env python3
"""Checks the probe's "OV" lines (TextOverflow.cs, PROBE_SCENARIO=textoverflow).

  ov-check.py diff ORIG.log OURS.log
      the cases whose isTextOverflowing, firstOverflowCharacterIndex, isTextTruncated, line count
      or characters (code, isVisible, lineNumber) differ between the players, by text and
      setting, and the preferred sizes that differ.
  ov-check.py rule LOG [N]
      predicts every case of LOG from the rule in docs/cleanroom/textmeshpro.md ("Overflow at
      white space") and counts the cases it reproduces, with up to N misses. Which line each
      character is on is taken from the Overflow-mode case of the same rect and setting; the
      advances and line bottoms from the text's "metrics" line (400 x 100, no wrapping).

A log is a Player.log (original) or our player's stdout. Rect sizes are compared as float32:
the "R" format differs between Mono and .NET.
"""

import re
import sys
from collections import Counter, defaultdict

import numpy as np

EPS = 1e-4
# White space as the texts here use it; a tab is not counted (it is checked, but not visible).
WHITE = {32, 160, 10}
FIELDS = ('ovf', 'idx', 'trunc', 'cc', 'lc', 'chars')
CASE = re.compile(
    r'PROBE f\d+ OV (.*?) "(.*)" (\w+) (\w+) (nowrap|wrap) rect=(\S+)x(\S+) ovf=(\w+) idx=(-?\d+) '
    r'trunc=(\w+) cc=(\d+) sc=\d+ wc=\d+ lc=(\d+) pc=\d+ chars(.*)$'
)


def f32(s):
    return np.format_float_positional(np.float32(float(s)), trim='-')


def load(path):
    cases, pref, met = {}, {}, {}
    for line in open(path, errors='replace'):
        m = CASE.search(line)
        if m:
            g = m.groups()
            cases[g[:5] + (f32(g[5]), f32(g[6]))] = dict(
                ovf=g[7] == 'True', idx=int(g[8]), trunc=g[9] == 'True', cc=int(g[10]), lc=int(g[11]), chars=g[12].strip()
            )
            continue
        m = re.search(r'PROBE f\d+ OV (.*?) "(.*)" preferred=(\S+)x(\S+)', line)
        if m:
            pref[m.group(1), m.group(2)] = (f32(m.group(3)), f32(m.group(4)))
            continue
        m = re.search(r'PROBE f\d+ OV (.*?) "(.*)" metrics(.*)$', line)
        if m:
            chars, lines = [], []
            for tok in m.group(3).split():
                vals = [float(v) for v in tok.split(':', 1)[1].split('/')]
                if tok.startswith('line'):
                    lines.append(vals)
                else:
                    chars.append(dict(code=int(tok.split(':')[0]), adv=vals[1] - vals[0]))
            met[m.group(1), m.group(2)] = (chars, lines)
    return cases, pref, met


def checked(code):
    return code not in WHITE


def visible(code):
    return code not in WHITE and code != 9


def fmt(lay):
    return ' '.join(f'{c}:{"v" if v else "-"}{ln}' for c, v, ln in lay)


def predict(key, laid_out, met, ellipsis):
    """The shipped package's outcome for case key, given the Overflow-mode case of its rect."""
    font, text, _, mode, _, w, h = key
    w, h = float(w), float(h)
    chars, mlines = met[font, text]
    lay = [(int(c), r[0] == 'v', int(r[1:])) for c, r in (t.split(':', 1) for t in laid_out['chars'].split())]
    n = len(lay)
    if n == 0:
        return dict(ovf=False, idx=-1, trunc=False, cc=0, lc=0, chars='')
    line = [ln for _, _, ln in lay]
    nlines = line[-1] + 1
    xs, x = [], 0.0
    for i in range(n):
        if i == 0 or line[i] != line[i - 1]:
            x = 0.0
        xs.append(x)
        x += 0.0 if chars[i]['code'] == 10 else chars[i]['adv']
    # A line that began by wrapping, rather than after a line feed.
    wrapped = [False] + [line[i] != line[i - 1] and chars[i - 1]['code'] != 10 for i in range(1, n)]
    top = mlines[0][0]
    if nlines == len(mlines):
        bottom = [top - ln[1] for ln in mlines]
    else:  # lines made by wrapping: one font, one line interval
        step = mlines[0][2] - mlines[1][2] if len(mlines) > 1 else INTERVAL[font]
        bottom = [top - mlines[0][1] + k * step for k in range(nlines)]
    vert = next((i for i in range(n) if checked(chars[i]['code']) and bottom[line[i]] > h + EPS), -1)
    if mode in ('Overflow', 'Masking'):
        return dict(ovf=vert >= 0, idx=vert, trunc=False, cc=n, lc=nlines, chars=laid_out['chars'])
    width = next((i for i in range(n) if visible(chars[i]['code']) and xs[i] + chars[i]['adv'] > w + EPS), -1)
    cuts = [c for c in (vert, width) if c >= 0]
    if not cuts:
        return dict(ovf=False, idx=-1, trunc=False, cc=n, lc=nlines, chars=laid_out['chars'])
    c = min(cuts)
    idx = vert if vert == c else -1
    empty = dict(ovf=idx >= 0, idx=idx, trunc=True, cc=0, lc=0, chars='')
    if c == 0:
        return empty
    if mode == 'Truncate':
        ln = line[c] - 1 if wrapped[c] else line[c]
        return dict(ovf=idx >= 0, idx=idx, trunc=True, cc=c + 1, lc=ln + 1, chars=(fmt(lay[:c]) + f' 3:-{ln}').strip())
    e = next((j for j in range(c - 1, -1, -1) if bottom[line[j]] <= h + EPS and xs[j] + ellipsis[font] <= w + EPS), -1)
    if e < 0:
        return empty
    ln = line[e]
    return dict(ovf=idx >= 0, idx=idx, trunc=True, cc=e + 2, lc=ln + 1, chars=(fmt(lay[:e]) + f' 8230:v{ln} 3:-{ln}').strip())


INTERVAL = {}


def rule(path, show):
    cases, _, met = load(path)
    ellipsis = {font: chars[0]['adv'] for (font, text), (chars, _) in met.items() if text in ('…', '…')}
    for (font, text), (_, lines) in met.items():
        if len(lines) > 1:
            INTERVAL[font] = lines[0][2] - lines[1][2]
    by, misses = defaultdict(Counter), []
    for key, v in cases.items():
        ref = cases.get(key[:3] + ('Overflow',) + key[4:])
        if ref is None or key[:2] not in met:
            continue
        p = predict(key, ref, met, ellipsis)
        ok = all(p[f] == v[f] for f in FIELDS)
        by[key[3]][ok] += 1
        if not ok:
            misses.append((key, v, p))
    print(
        f'{sum(c[True] for c in by.values())} of {sum(sum(c.values()) for c in by.values())} cases reproduced: '
        + ', '.join(f'{m} {c[True]}/{sum(c.values())}' for m, c in by.items())
    )
    for key, v, p in misses[:show]:
        d = [f for f in FIELDS if p[f] != v[f]]
        print(' | '.join(key), {f: v[f] for f in d}, 'rule', {f: p[f] for f in d})


def diff(a, b, show):
    A, pa, _ = load(a)
    B, pb, _ = load(b)
    for k in sorted(set(pa) | set(pb)):
        if pa.get(k) != pb.get(k):
            print('preferred', ' '.join(k), pa.get(k), pb.get(k))
    groups = defaultdict(list)
    common = 0
    for k, v in A.items():
        if k in B:
            common += 1
            if any(v[f] != B[k][f] for f in FIELDS):
                groups[k[:5]].append((k[5:], v, B[k]))
    print(f'{sum(map(len, groups.values()))} of {common} common cases differ (original / ours)')
    for g, rows in groups.items():
        print(' | '.join(g), len(rows))
        for (w, h), v, u in rows[:show]:
            print(f'   {w}x{h}: ' + ' / '.join(f'{x["idx"]} trunc={x["trunc"]} cc={x["cc"]} lc={x["lc"]} [{x["chars"]}]' for x in (v, u)))


if __name__ == '__main__':
    if len(sys.argv) >= 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 3)
    elif len(sys.argv) >= 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 10)
    else:
        sys.exit(__doc__)
