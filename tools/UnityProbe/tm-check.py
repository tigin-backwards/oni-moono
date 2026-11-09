#!/usr/bin/env python3
"""tm-check.py: Transform matrices from the probe's TM lines (PROBE_SCENARIO=transform).

  tm-check.py rule LOG          for the leaf's localToWorldMatrix, worldToLocalMatrix, position,
                                TransformPoint and InverseTransformPoint: how many cases each
                                candidate formula reproduces bit for bit, for cases without and
                                with rotations
  tm-check.py diff ORIG OURS    the values whose bits differ between the two players

LOG is the original's Player.log or our player's stdout. Plain Python (struct), no numpy.
Candidates (products as Matrix4x4's: each element's sum in double, rounded once):
  l2w down     parent.localToWorld x TRS(local), from the root down
  w2l composed TRS(local)^-1 x parent.worldToLocal, with TRS^-1 = S(1/s) R(q^-1) T(-p)
  pos/tp/itp   through the matrices above (MultiplyPoint3x4), or level by level:
               tp up = for each level from the leaf: p = lp + R(q) (s * p), each in float
"""

import re
import struct
import sys

LINE = re.compile(r'PROBE f\d+ TM (\d+) (.*)$')


def f32(x):
    return struct.unpack('<f', struct.pack('<f', x))[0]


def bits(x):
    return struct.unpack('<I', struct.pack('<f', x))[0]


def unhex(s):
    return [struct.unpack('<f', struct.pack('<I', int(h, 16)))[0] for h in s.split(',')]


def cases(path):
    for raw in open(path, errors='replace'):
        m = LINE.search(raw.rstrip('\n'))
        if not m:
            continue
        c = {'levels': []}
        for kv in m.group(2).split():
            k, v = kv.split('=', 1)
            if re.match(r'L\d+$', k):
                p, q, s = v.split(';')
                c['levels'].append((unhex(p), unhex(q), unhex(s)))
            else:
                c[k] = unhex(v)
        yield int(m.group(1)), c


def mat_mul(a, b, mode='f64'):
    out = []
    for r in range(4):
        for col in range(4):
            if mode == 'f64':
                acc = 0.0
                for k in range(4):
                    acc += a[r * 4 + k] * b[k * 4 + col]
                out.append(f32(acc))
            else:
                acc = None
                for k in range(4):
                    p = f32(a[r * 4 + k] * b[k * 4 + col])
                    acc = p if acc is None else f32(acc + p)
                out.append(acc)
    return out


def rot(q, variant):
    x, y, z, w = q
    if variant == 'a':  # x2 = x + x; xx = x * x2 ... all in float
        x2, y2, z2 = f32(x + x), f32(y + y), f32(z + z)
        xx, yy, zz = f32(x * x2), f32(y * y2), f32(z * z2)
        xy, xz, yz = f32(x * y2), f32(x * z2), f32(y * z2)
        wx, wy, wz = f32(w * x2), f32(w * y2), f32(w * z2)
        return [
            [f32(1 - f32(yy + zz)), f32(xy - wz), f32(xz + wy)],
            [f32(xy + wz), f32(1 - f32(xx + zz)), f32(yz - wx)],
            [f32(xz - wy), f32(yz + wx), f32(1 - f32(xx + yy))],
        ]
    # 'b': the same in double, each element rounded once
    x2, y2, z2 = x * 2, y * 2, z * 2
    return [
        [f32(1 - (y * y2 + z * z2)), f32(x * y2 - w * z2), f32(x * z2 + w * y2)],
        [f32(x * y2 + w * z2), f32(1 - (x * x2 + z * z2)), f32(y * z2 - w * x2)],
        [f32(x * z2 - w * y2), f32(y * z2 + w * x2), f32(1 - (x * x2 + y * y2))],
    ]


def trs(p, q, s, variant):
    r = rot(q, variant)
    m = [0.0] * 16
    for i in range(3):
        for j in range(3):
            m[i * 4 + j] = f32(r[i][j] * s[j])
        m[i * 4 + 3] = p[i]
    m[15] = 1.0
    return m


def trs_inv(p, q, s, variant):
    inv_s = [f32(1.0 / v) if v != 0 else 0.0 for v in s]
    sm = [0.0] * 16
    for i in range(3):
        sm[i * 5] = inv_s[i]
    sm[15] = 1.0
    r = rot([-q[0], -q[1], -q[2], q[3]], variant)
    rm = [0.0] * 16
    for i in range(3):
        for j in range(3):
            rm[i * 4 + j] = r[i][j]
    rm[15] = 1.0
    tm = [1.0, 0, 0, f32(-p[0]), 0, 1.0, 0, f32(-p[1]), 0, 0, 1.0, f32(-p[2]), 0, 0, 0, 1.0]
    return mat_mul(mat_mul(sm, rm), tm)


IDENT = [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0]


def l2w_down(levels, variant, mode='f64'):
    m = IDENT
    for p, q, s in levels:
        m = mat_mul(m, trs(p, q, s, variant), mode)
    return m


def w2l_composed(levels, variant, mode='f64'):
    m = IDENT
    for p, q, s in levels:
        m = mat_mul(trs_inv(p, q, s, variant), m, mode)
    return m


def p3x4(m, v):
    return [f32(m[r * 4] * v[0] + m[r * 4 + 1] * v[1] + m[r * 4 + 2] * v[2] + m[r * 4 + 3]) for r in range(3)]


def same(a, b):
    return len(a) == len(b) and all(bits(x) == bits(y) for x, y in zip(a, b))


def candidates(c):
    out = {}
    for v in ('a', 'b'):
        for mode in ('f64', 'f32'):
            l2w = l2w_down(c['levels'], v, mode)
            w2l = w2l_composed(c['levels'], v, mode)
            out[f'l2w down rot-{v} {mode}'] = ('l2w', l2w)
            out[f'w2l composed rot-{v} {mode}'] = ('w2l', w2l)
            out[f'pos = l2w col 3, rot-{v} {mode}'] = ('pos', [l2w[3], l2w[7], l2w[11]])
            out[f'tp = l2w p3x4, rot-{v} {mode}'] = ('tp', p3x4(l2w, c['p']))
            out[f'itp = w2l p3x4, rot-{v} {mode}'] = ('itp', p3x4(w2l, c['p']))
    return out


def rule(path):
    tallies = {}
    totals = {False: 0, True: 0}
    for _, c in cases(path):
        rotated = any(q != [0.0, 0.0, 0.0, 1.0] for _, q, _ in c['levels'])
        totals[rotated] += 1
        for name, (key, value) in candidates(c).items():
            t = tallies.setdefault(name, {False: 0, True: 0})
            t[rotated] += same(value, c[key])
    print(f'cases: {totals[False]} without rotation, {totals[True]} with')
    for name, t in tallies.items():
        print(f'  {name:34} {t[False]:4}/{totals[False]}  {t[True]:4}/{totals[True]}')


def diff(orig, ours):
    a, b = dict(cases(orig)), dict(cases(ours))
    keys = ['l2w', 'w2l', 'pos', 'tp', 'itp']
    differ = {k: 0 for k in keys}
    common = sorted(set(a) & set(b))
    for i in common:
        for k in keys:
            differ[k] += not same(a[i][k], b[i][k])
    print(f'{len(common)} cases; differing: ' + ', '.join(f'{k} {v}' for k, v in differ.items()))


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    elif len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
