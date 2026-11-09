#!/usr/bin/env python3
"""Checks the probe's "TW" lines (TextWidth.cs, PROBE_SCENARIO=textwidth).

  tw-check.py diff ORIG.log OURS.log
      the preferred widths and heights that differ between the players (as float32 bits), and
      the texts whose preferred width under the HorizontalLayoutGroup is not their width alone.
  tw-check.py rule LOG
      recomputes each regular-style text's preferred width from its pen (the last visible
      character's xAdvance in the rect whose left edge is at 0, the "@0" lines) by the rule in
      docs/cleanroom/textmeshpro.md (preferred sizes to the hundredth), and counts the texts it
      reproduces.

A log is a Player.log (original) or our player's stdout. Values are compared as float32: the
"R" format differs between Mono and .NET.
"""

import math
import re
import sys

import numpy as np

f32 = np.float32
WHITE = (9, 10, 32)


def lines(path):
    for line in open(path, errors='replace'):
        m = re.search(r'PROBE f\d+ TW (.*)$', line.rstrip('\n'))
        if m:
            yield m.group(1)


def parse(path):
    alone, row, chars = {}, {}, {}
    for body in lines(path):
        m = re.match(r'(.*?") alone preferredWidth=\S+ bits=(\w+) dump=\S+ preferredHeight=\S+ bits=(\w+)', body)
        if m:
            alone[m.group(1)] = (m.group(2), m.group(3))
            continue
        m = re.match(r'(.*?") row hlg.preferredWidth=\S+ bits=\w+ dump=\S+ text.preferredWidth=\S+ bits=(\w+)', body)
        if m:
            row[m.group(1)] = m.group(2)
            continue
        m = re.match(r'(.*?") chars(.*)$', body)
        if m:
            chars[m.group(1)] = [(int(c), *(f32(float(v)) for v in rest.split('/'))) for c, rest in (x.split(':', 1) for x in m.group(2).split())]
    return alone, row, chars


def bits_value(b):
    return float(np.frombuffer(bytes.fromhex(b)[::-1], dtype=np.float32)[0])


def diff(a, b):
    A, Arow, _ = parse(a)
    B, Brow, _ = parse(b)
    n = 0
    for k, (w, h) in A.items():
        if k not in B:
            continue
        bw, bh = B[k]
        if (w, h) != (bw, bh):
            n += 1
            print(f'{k}: width {bits_value(w):.6g} / {bits_value(bw):.6g}, height {bits_value(h):.6g} / {bits_value(bh):.6g}')
    print(f'{n} of {len(A)} texts differ (original / ours)')
    for name, alone, row in (('original', A, Arow), ('ours', B, Brow)):
        moved = [k for k in row if alone.get(k, (None,))[0] != row[k]]
        print(f'{name}: {len(moved)} texts change their preferred width under the layout group')


def rule(path):
    alone, _, chars = parse(path)
    ok, bad = 0, []
    for k, cs in chars.items():
        m = re.match(r'.* Normal@0 "(.*)"$', k)
        if not m or not m.group(1):
            continue
        vis = [c for c in cs if c[0] not in WHITE]
        x = float(vis[-1][2]) if vis else 0.0
        want = f32((math.floor(x * 100.0) + 1) / 100.0)
        if want == f32(bits_value(alone[k][0])):
            ok += 1
        else:
            bad.append((k, x, float(want), bits_value(alone[k][0])))
    for k, x, want, got in bad[:20]:
        print(f'  {k}: pen {x!r}, rule {want:.6g}, preferred {got:.6g}')
    print(f'the rule reproduces {ok} of {ok + len(bad)} regular-style texts')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        sys.exit(__doc__)
