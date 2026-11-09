#!/usr/bin/env python3
"""Compares the probe's "UIS" lines (UiSemantics.cs) between two players' logs.

  uis-check.py diff ORIG.log OURS.log [SECTION] [--all]
      line-by-line comparison with the frame stamps removed (SECTION: scroll, input, depth);
      Elastic lines differ by frame timing, and "time" lines always.
  uis-check.py fit LOG
      recomputes ScrollRect's normalized position from the inputs each "scroll raw" line logs
      (the view's rect and the content's corners in the view's space), in float32, by the rule
      in docs/cleanroom/ugui.md, and counts the axes it reproduces.
  uis-check.py inputs ORIG.log OURS.log
      compares those inputs as float32 values: where they differ, the engine's transforms do.

A log is a Player.log (original) or our player's stdout.
"""

import re
import sys
from collections import Counter

import numpy as np

f32 = np.float32


def uis_lines(path, section=None):
    out = []
    for line in open(path, errors='replace'):
        m = re.search(r'PROBE f\d+ ((?:UIS|=== UIS).*)$', line.rstrip('\n'))
        if not m:
            continue
        body = m.group(1)
        if section and not (body.startswith('UIS ' + section) or body.startswith('=== UIS')):
            continue
        out.append(body)
    return out


def diff(a, b, section, show_all):
    A, B = uis_lines(a, section), uis_lines(b, section)
    differing = 0
    for i in range(max(len(A), len(B))):
        x = A[i] if i < len(A) else '<none>'
        y = B[i] if i < len(B) else '<none>'
        if x != y:
            differing += 1
            print('- ' + x)
            print('+ ' + y)
        elif show_all:
            print('  ' + x)
    print(f'{differing} of {max(len(A), len(B))} lines differ', file=sys.stderr)


def raw(path):
    out = {}
    for body in uis_lines(path, 'scroll raw'):
        m = re.match(r'UIS scroll raw (.*?) view=(\S+) world=(\S+) local=(\S+) np=(\S+)', body)
        if not m:
            continue
        points = lambda s: [tuple(f32(c) for c in p.split(',')) for p in s.split(';')]
        out.setdefault(
            m.group(1), (tuple(f32(v) for v in m.group(2).split(',')), points(m.group(3)), points(m.group(4)), tuple(f32(v) for v in m.group(5).split(',')))
        )
    return out


def pivot(label):
    # The probe's fitting layouts: "eq p0,1" (pivot 0,1) and "small c" (centred).
    return (f32(0.5), f32(0.5)) if ' c' in label else (f32(0), f32(1))


def approximately(a, b):
    # Mathf.Approximately: |b - a| < max(1e-6 * max(|a|, |b|), 8 * float.Epsilon)
    return abs(float(b) - float(a)) < max(1e-6 * max(abs(float(a)), abs(float(b))), 8 * 1.401298464324817e-45)


def normalized(view, local, piv):
    half = f32(0.5)
    xmin, ymin, w, h = view
    vsize = (w, h)
    vmin = ((xmin + w * half) - w * half, (ymin + h * half) - h * half)
    result = []
    for a in (0, 1):
        lo = min(p[a] for p in local)
        hi = max(p[a] for p in local)
        ext = (hi - lo) * half
        centre = lo + ext
        size = ext * f32(2)
        if size < vsize[a]:
            centre = centre - (vsize[a] - size) * (piv[a] - half)
            size = vsize[a]
        cmin = centre - size * half
        if size <= vsize[a] or approximately(size, vsize[a]):
            result.append(f32(1) if vmin[a] > cmin else f32(0))
        else:
            result.append(f32((vmin[a] - cmin) / (size - vsize[a])))
    return result


def fit(path):
    ok = bad = 0
    for label, (view, _, local, logged) in raw(path).items():
        got = normalized(view, local, pivot(label))
        for a in (0, 1):
            if got[a] == logged[a]:
                ok += 1
            else:
                bad += 1
                print(f'{label} axis {a}: logged {float(logged[a])!r}, rule gives {float(got[a])!r}')
    print(f'{ok} of {ok + bad} axis values reproduced')


def inputs(a, b):
    A, B = raw(a), raw(b)
    c = Counter()
    ulps = Counter()
    for label in A:
        if label not in B:
            continue
        (va, wa, la, na), (vb, wb, lb, nb) = A[label], B[label]
        c['cases'] += 1
        c['view rect differs'] += va != vb
        c['world corners differ'] += wa != wb
        c['corners in view space differ'] += la != lb
        c['normalized position differs'] += na != nb
        for pa, pb in zip(wa, wb):
            for x, y in zip(pa, pb):
                ulps[abs(int(np.array(x).view(np.int32)) - int(np.array(y).view(np.int32)))] += 1
    print(dict(c))
    print('world corner coordinates by ulp distance:', dict(sorted(ulps.items())))


if __name__ == '__main__':
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    cmd = args[0]
    if cmd == 'diff':
        rest = [x for x in args[3:] if x != '--all']
        diff(args[1], args[2], rest[0] if rest else None, '--all' in args)
    elif cmd == 'fit':
        fit(args[1])
    elif cmd == 'inputs':
        inputs(args[1], args[2])
    else:
        sys.exit(__doc__)
