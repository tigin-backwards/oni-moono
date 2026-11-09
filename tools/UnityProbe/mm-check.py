#!/usr/bin/env python3
"""mm-check.py: Matrix4x4's products from the probe's MM lines (PROBE_SCENARIO=matmul).

  mm-check.py rule LOG          for each product, how many cases each candidate reproduces bit
                                for bit: f32 (every multiply and add rounded to float, left to
                                right) and f64 (each component's expression in double, left to
                                right, rounded to float once)
                                and inv: Mesa's invert_matrix_general, a singular matrix
                                giving zero (also on the MMS lines, singular and near-singular
                                matrices)
  mm-check.py diff ORIG OURS    the products whose bits differ between the two players

LOG is the original's Player.log or our player's stdout. Plain Python (struct), no numpy.
"""

import math
import re
import struct
import sys

LINE = re.compile(r'PROBE f\d+ MM (\d+) (.*)$')
SINGULAR = re.compile(r'PROBE f\d+ MMS (\d+) (.*)$')


def f32(x):
    try:
        return struct.unpack('<f', struct.pack('<f', x))[0]
    except OverflowError:  # rounds beyond the largest float
        return math.copysign(math.inf, x)


def bits(x):
    return struct.unpack('<I', struct.pack('<f', x))[0]


def unhex(s):
    return [struct.unpack('<f', struct.pack('<I', int(h, 16)))[0] for h in s.split(',')]


def cases(path, line=LINE):
    for raw in open(path, errors='replace'):
        m = line.search(raw.rstrip('\n'))
        if not m:
            continue
        fields = dict(kv.split('=', 1) for kv in m.group(2).split())
        yield int(m.group(1)), {k: unhex(v) for k, v in fields.items()}


def dot(row, vec, mode):
    """sum(row[i] * vec[i]) left to right."""
    if mode == 'f64':
        acc = 0.0
        for a, b in zip(row, vec):
            acc += a * b  # a product of two floats is exact in double
        return f32(acc)
    acc = None
    for a, b in zip(row, vec):
        p = f32(a * b)
        acc = p if acc is None else f32(acc + p)
    return acc


def rows(m):
    return [m[r * 4 : r * 4 + 4] for r in range(4)]


def p3x4(m, p, mode):
    return [dot(r, p + [1.0], mode) for r in rows(m)[:3]]


def vec(m, p, mode):
    return [dot(r[:3], p, mode) for r in rows(m)[:3]]


def point(m, p, mode):
    x, y, z, w = [dot(r, p + [1.0], mode) for r in rows(m)]
    w = f32(1.0 / w)
    return [f32(x * w), f32(y * w), f32(z * w)]


def mq(m, q, mode):
    return [dot(r, q, mode) for r in rows(m)]


def mn(m, n, mode):
    cols = [[n[r * 4 + c] for r in range(4)] for c in range(4)]
    return [dot(r, col, mode) for r in rows(m) for col in cols]


def mesa_inverse(mm, mode):
    """Mesa's invert_matrix_general (m_matrix.c, Mesa 20.3.5) of a row-major matrix: Gauss-Jordan
    with partial pivoting; f32 rounds every operation, f64 none. A zero pivot gives zero."""
    R = f32 if mode == 'f32' else (lambda x: x)
    r0, r1, r2, r3 = [list(mm[r * 4 : r * 4 + 4]) + [1.0 if k == r else 0.0 for k in range(4)] for r in range(4)]
    zero = [0.0] * 16
    if abs(r3[0]) > abs(r2[0]):
        r3, r2 = r2, r3
    if abs(r2[0]) > abs(r1[0]):
        r2, r1 = r1, r2
    if abs(r1[0]) > abs(r0[0]):
        r1, r0 = r0, r1
    if r0[0] == 0:
        return zero
    m1, m2, m3 = R(r1[0] / r0[0]), R(r2[0] / r0[0]), R(r3[0] / r0[0])
    for k in range(1, 8):
        s = r0[k]
        if k >= 4 and s == 0:
            continue
        r1[k] = R(r1[k] - R(m1 * s))
        r2[k] = R(r2[k] - R(m2 * s))
        r3[k] = R(r3[k] - R(m3 * s))
    if abs(r3[1]) > abs(r2[1]):
        r3, r2 = r2, r3
    if abs(r2[1]) > abs(r1[1]):
        r2, r1 = r1, r2
    if r1[1] == 0:
        return zero
    m2, m3 = R(r2[1] / r1[1]), R(r3[1] / r1[1])
    for k in range(2, 8):
        s = r1[k]
        if k >= 4 and s == 0:
            continue
        r2[k] = R(r2[k] - R(m2 * s))
        r3[k] = R(r3[k] - R(m3 * s))
    if abs(r3[2]) > abs(r2[2]):
        r3, r2 = r2, r3
    if r2[2] == 0:
        return zero
    m3 = R(r3[2] / r2[2])
    for k in range(3, 8):
        r3[k] = R(r3[k] - R(m3 * r2[k]))
    if r3[3] == 0:
        return zero
    s = R(1.0 / r3[3])
    for k in range(4, 8):
        r3[k] = R(r3[k] * s)
    m2, s = r2[3], R(1.0 / r2[2])
    for k in range(4, 8):
        r2[k] = R(s * R(r2[k] - R(r3[k] * m2)))
    for row in (r1, r0):
        m = row[3]
        for k in range(4, 8):
            row[k] = R(row[k] - R(r3[k] * m))
    m1, s = r1[2], R(1.0 / r1[1])
    for k in range(4, 8):
        r1[k] = R(s * R(r1[k] - R(r2[k] * m1)))
    m0 = r0[2]
    for k in range(4, 8):
        r0[k] = R(r0[k] - R(r2[k] * m0))
    m0, s = r0[1], R(1.0 / r0[0])
    for k in range(4, 8):
        r0[k] = R(s * R(r0[k] - R(r1[k] * m0)))
    return [f32(v) for r in (r0, r1, r2, r3) for v in r[4:8]]


PRODUCTS = {
    'p3x4': lambda c, mode: p3x4(c['m'], c['p'], mode),
    'vec': lambda c, mode: vec(c['m'], c['p'], mode),
    'pt': lambda c, mode: point(c['m'], c['p'], mode),
    'mq': lambda c, mode: mq(c['m'], c['q'], mode),
    'mn': lambda c, mode: mn(c['m'], c['n'], mode),
    'inv': lambda c, mode: mesa_inverse(c['m'], mode),
}


def same(a, b):
    return all(bits(x) == bits(y) or (x != x and y != y) for x, y in zip(a, b))


def rule(path):
    total = 0
    hits = {(k, mode): 0 for k in PRODUCTS for mode in ('f32', 'f64')}
    for _, c in cases(path):
        total += 1
        for k, fn in PRODUCTS.items():
            for mode in ('f32', 'f64'):
                hits[(k, mode)] += same(fn(c, mode), c[k])
    for k in PRODUCTS:
        print(f'{k:5} f32 {hits[(k, "f32")]}/{total}  f64 {hits[(k, "f64")]}/{total}')
    singular = list(cases(path, SINGULAR))
    if singular:
        ok = {mode: sum(same(mesa_inverse(c['m'], mode), c['inv']) and same(c['sinv'], c['inv']) for _, c in singular) for mode in ('f32', 'f64')}
        print(f'MMS   f32 {ok["f32"]}/{len(singular)}  f64 {ok["f64"]}/{len(singular)} (inv, with Inverse(m) the same)')


def diff(orig, ours):
    a, b = dict(cases(orig)), dict(cases(ours))
    keys = ['p3x4', 'pt', 'vec', 'mq', 'mn', 'inv']
    differ = {k: 0 for k in keys}
    for i in sorted(set(a) & set(b)):
        for k in keys:
            if not same(a[i][k], b[i][k]):
                differ[k] += 1
    print(f'{len(set(a) & set(b))} cases; differing: ' + ', '.join(f'{k} {v}' for k, v in differ.items()))
    a, b = dict(cases(orig, SINGULAR)), dict(cases(ours, SINGULAR))
    common = sorted(set(a) & set(b))
    if common:
        inputs = [i for i in common if not same(a[i]['m'], b[i]['m'])]
        outputs = [i for i in common if i not in inputs and not (same(a[i]['inv'], b[i]['inv']) and same(a[i]['sinv'], b[i]['sinv']))]
        print(f'{len(common)} MMS cases; differing inverses: {outputs or 0}; inputs that differ: {inputs or 0}')


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    elif len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
