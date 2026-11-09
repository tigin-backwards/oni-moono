#!/usr/bin/env python3
"""Checks the probe's "TH" lines (TextHeight.cs, PROBE_SCENARIO=textheight).

  th-check.py diff ORIG.log OURS.log
      the texts whose preferred height or width differs between the players (as float32 bits),
      with each player's line ascender and descender, grouped by font and size.
  th-check.py rule LOG
      recomputes each one-line text's preferred height from its first line's ascender and
      descender by the rule of docs/cleanroom/textmeshpro.md (preferred sizes to the hundredth):
      (floor(h * 100) + 1) / 100 with h = ascender - descender as a float, and counts the texts
      it reproduces.

A log is a Player.log (original) or our player's stdout. Values are compared as float32: the
"R" format differs between Mono and .NET.
"""

import math
import re
import sys

import numpy as np

f32 = np.float32


def lines(path):
    for line in open(path, errors='replace', encoding='utf-8'):
        m = re.search(r'PROBE f\d+ TH (.*)$', line.rstrip('\n'))
        if m:
            yield m.group(1)


def parse(path):
    pref, line0, faces = {}, {}, []
    for body in lines(path):
        m = re.match(r'(.*?") preferredHeight=\S+ bits=(\w+) dump=(\S+) preferredWidth=\S+ bits=(\w+) dump=(\S+)$', body)
        if m:
            pref[m.group(1)] = (m.group(2), m.group(3), m.group(4), m.group(5))
            continue
        m = re.match(r'(.*?") line0 ascender=(\S+) bits=(\w+) dump=\S+ descender=(\S+) bits=(\w+) dump=\S+ .* lines=(\d+)', body)
        if m:
            line0[m.group(1)] = (m.group(3), m.group(5), int(m.group(6)))
            continue
        if body.startswith('face '):
            faces.append(body)
    return pref, line0, faces


def value(bits):
    return float(np.frombuffer(int(bits, 16).to_bytes(4, 'little', signed=False), dtype=np.float32)[0])


def diff(orig, ours):
    a, al, af = parse(orig)
    b, bl, bf = parse(ours)
    for x, y in zip(af, bf):
        if x != y:
            print('face differs:\n  original', x, '\n  ours    ', y)
    n = bad = 0
    for key in a:
        if key not in b:
            print('only in the original:', key)
            continue
        n += 1
        if a[key][0] != b[key][0] or a[key][2] != b[key][2]:
            bad += 1
            asc = [round(value(v), 6) for v in al.get(key, ('0', '0'))[:2]]
            bsc = [round(value(v), 6) for v in bl.get(key, ('0', '0'))[:2]]
            print(
                f'{key}: height {a[key][1]} / {b[key][1]} ({value(a[key][0])!r} / {value(b[key][0])!r}), '
                f'width {a[key][3]} / {b[key][3]}; line ascender, descender {asc} / {bsc}'
            )
    print(f'{bad} of {n} texts differ')


def rule(path):
    pref, line0, _ = parse(path)
    n = ok = 0
    for key, (hbits, _, _, _) in pref.items():
        if key not in line0 or line0[key][2] != 1:
            continue
        asc, desc = value(line0[key][0]), value(line0[key][1])
        h = float(f32(asc) - f32(desc))
        if h <= 0:
            continue
        want = f32((math.floor(h * 100) + 1) / 100)
        n += 1
        if want == f32(value(hbits)):
            ok += 1
        else:
            print('miss', key, 'h', repr(h), 'rule', want, 'logged', value(hbits))
    print(f'rule reproduces {ok} of {n} one-line texts')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        sys.exit(__doc__)
