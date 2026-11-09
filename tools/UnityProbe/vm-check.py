#!/usr/bin/env python3
"""vm-check.py: Vector2, Vector3, Mathf, Rect and Bounds results from the probe's VM lines
(PROBE_SCENARIO=vecmath).

  vm-check.py diff ORIG OURS    for each member, the cases whose bits differ between the players
  vm-check.py rule LOG          for the members whose float32 and Mono evaluations can differ, how
                                many cases each candidate reproduces bit for bit: f32 (every
                                operation rounded to float) and mono (the expression in double,
                                rounded to float where Unity's code would store it: see each
                                candidate's comment)

LOG is the original's Player.log or our player's stdout. Plain Python (struct), no numpy.
"""

import math
import re
import struct
import sys

LINE = re.compile(r'PROBE f\d+ (VM[23F]) (\d+) (.*)$')


def f32(x):
    try:
        return struct.unpack('<f', struct.pack('<f', x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)


def unhex(s):
    return [struct.unpack('<f', struct.pack('<I', int(h, 16)))[0] if len(h) == 8 else int(h) for h in s.split(',')]


def cases(path):
    out = {}
    with open(path, errors='replace') as f:
        for line in f:
            m = LINE.search(line)
            if m:
                out[(m.group(1), int(m.group(2)))] = dict(kv.split('=', 1) for kv in m.group(3).split())
    return out


def diff(orig, ours):
    a, b = cases(orig), cases(ours)
    if not a or not b:
        sys.exit(f'no VM lines: {len(a)} in {orig}, {len(b)} in {ours}')
    counts, inputs = {}, 0
    for key, fields in a.items():
        other = b.get(key)
        if other is None:
            continue
        for name, value in fields.items():
            if other.get(name) != value:
                if name in ('a', 'b', 't', 'd', 'v', 'r', 's', 'p', 'bc', 'be', 'q'):
                    inputs += 1
                counts.setdefault((key[0], name), []).append(key[1])
    if inputs:
        print(f'{inputs} input(s) differ: the players drew different cases')
    for (kind, name), ids in sorted(counts.items()):
        print(f'{kind} {name}: {len(ids)} of {sum(1 for k in a if k[0] == kind)} differ (first {ids[:5]})')
    if not counts:
        print('every member agrees bit for bit')


# Candidates: (member, f32 evaluation, Mono's), from the case's inputs. Mono's evaluates in
# double and rounds where a float is stored: a local, a field (a Vector's component), an
# argument or a return value.
def v2(c):
    a, b = unhex(c['a']), unhex(c['b'])
    d32x, d32y = f32(a[0] - b[0]), f32(a[1] - b[1])
    yield 'dist', f32(math.sqrt(f32(f32(d32x * d32x) + f32(d32y * d32y)))), f32(math.sqrt(d32x * d32x + d32y * d32y)), unhex(c['dist'])[0]
    yield 'mag', f32(math.sqrt(f32(f32(a[0] * a[0]) + f32(a[1] * a[1])))), f32(math.sqrt(a[0] * a[0] + a[1] * a[1])), unhex(c['mag'])[0]
    yield 'sqr', f32(f32(a[0] * a[0]) + f32(a[1] * a[1])), f32(a[0] * a[0] + a[1] * a[1]), unhex(c['sqr'])[0]
    yield 'dot', f32(f32(a[0] * b[0]) + f32(a[1] * b[1])), f32(a[0] * b[0] + a[1] * b[1]), unhex(c['dot'])[0]
    eps = f32(1e-5)
    yield 'eq', float(f32(f32(d32x * d32x) + f32(d32y * d32y)) < f32(eps * eps)), float(d32x * d32x + d32y * d32y < eps * eps), float(unhex(c['eq'])[0])


def v3(c):
    a, b = unhex(c['a']), unhex(c['b'])
    d = [f32(a[i] - b[i]) for i in range(3)]
    s32 = f32(f32(f32(d[0] * d[0]) + f32(d[1] * d[1])) + f32(d[2] * d[2]))
    s64 = d[0] * d[0] + d[1] * d[1] + d[2] * d[2]
    yield 'dist', f32(math.sqrt(s32)), f32(math.sqrt(s64)), unhex(c['dist'])[0]
    m32 = f32(f32(f32(a[0] * a[0]) + f32(a[1] * a[1])) + f32(a[2] * a[2]))
    yield 'mag', f32(math.sqrt(m32)), f32(math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])), unhex(c['mag'])[0]
    yield 'sqr', m32, f32(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]), unhex(c['sqr'])[0]
    yield 'dot', f32(f32(f32(a[0] * b[0]) + f32(a[1] * b[1])) + f32(a[2] * b[2])), f32(a[0] * b[0] + a[1] * b[1] + a[2] * b[2]), unhex(c['dot'])[0]
    eps = f32(1e-5)
    yield 'eq', float(s32 < f32(eps * eps)), float(s64 < eps * eps), float(unhex(c['eq'])[0])
    cr = unhex(c['cross'])
    yield 'cross.x', f32(f32(a[1] * b[2]) - f32(a[2] * b[1])), f32(a[1] * b[2] - a[2] * b[1]), cr[0]


def vf(c):
    a, b, t = unhex(c['a'])[0], unhex(c['b'])[0], unhex(c['t'])[0]
    tc = min(max(t, 0.0), 1.0)
    yield 'lerp', f32(a + f32(f32(b - a) * tc)), f32(a + (b - a) * tc), unhex(c['lerp'])[0]
    yield 'lerpu', f32(a + f32(f32(b - a) * t)), f32(a + (b - a) * t), unhex(c['lerpu'])[0]
    r = unhex(c['r'])
    yield 'rxmax', f32(r[0] + r[2]), f32(r[0] + r[2]), unhex(c['rxmax'])[0]
    rc = unhex(c['rc'])
    yield 'rc.x', f32(r[0] + f32(r[2] / 2)), f32(r[0] + r[2] / 2), rc[0]


def rule(path):
    tally = {}
    for (kind, _), c in cases(path).items():
        gen = {'VM2': v2, 'VM3': v3, 'VMF': vf}[kind](c)
        for name, flat, mono, got in gen:
            t = tally.setdefault(f'{kind} {name}', [0, 0, 0, 0])
            t[0] += 1
            t[1] += struct.pack('<d', flat) == struct.pack('<d', got) or f32(flat) == got
            t[2] += struct.pack('<d', mono) == struct.pack('<d', got) or f32(mono) == got
            t[3] += f32(flat) != f32(mono)
    for name, (n, flat, mono, split) in sorted(tally.items()):
        print(f'{name}: {n} cases ({split} where the candidates differ): f32 {flat}, mono {mono}')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        sys.exit(__doc__)
