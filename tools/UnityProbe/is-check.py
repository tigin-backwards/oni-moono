#!/usr/bin/env python3
"""is-check.py: the invscale scenario's IS lines (InverseScale.cs).

  is-check.py diff ORIG OURS    each logged value of IS1-IS6 compared bit for bit, per tag and
                                key, with the first differences shown
  is-check.py position LOG      IS7 (PROBE_IS_PART=position): the position setter's rule, per level
                                from the root down (p - localPosition) + 0, times the inverse
                                scale (the engine's InverseScale.bin), against every logged
                                setter result and InverseTransformPoint, bit for bit
  is-check.py table LOG OUT     from an original run with PROBE_IS_PART=full, the engine's
                                inverse-scale table (src/OniMoono.UnityEngine/Math/
                                InverseScale.bin): each mantissa's bits(f) - bits(1/s) + 1, two
                                bits each, four to a byte with the first in the high bits, raw
                                deflate

LOG is the original's Player.log or our player's stdout. Plain Python.
"""
import os
import re
import struct
import sys
import zlib
from collections import Counter


def lines(path):
    out = {}
    for raw in open(path, errors='replace'):
        m = re.search(r' (IS[1-46-8]) (.*)$', raw.rstrip())
        if m:
            tag, rest = m.groups()
            parts = rest.split(' ')
            key = tag + ' ' + (' '.join(p for p in parts if p.startswith('s=') or '=' not in p) if tag == 'IS6' else parts[0])
            out.setdefault(key, []).append(rest)
    return out


def diff(orig, ours):
    a, b = lines(orig), lines(ours)
    total, bad, shown = Counter(), Counter(), 0
    for key, values in a.items():
        tag = key.split()[0]
        if key not in b:
            total[tag + ' missing'] += 1
            continue
        for x, y in zip(values, b[key]):
            kx = dict(p.split('=', 1) for p in x.split(' ') if '=' in p)
            ky = dict(p.split('=', 1) for p in y.split(' ') if '=' in p)
            for field, value in kx.items():
                total[tag + ' ' + field] += 1
                if value != ky.get(field):
                    bad[tag + ' ' + field] += 1
                    if shown < 10:
                        print('differs', key, field, value[:100], '|', str(ky.get(field))[:100])
                        shown += 1
    for k in sorted(total):
        print(f'{k:14s} {total[k] - bad[k]}/{total[k]}')


def f32(x):
    return struct.unpack('<f', struct.pack('<f', x))[0]


def from_bits(h):
    return struct.unpack('<f', struct.pack('<I', h))[0]


def to_bits(x):
    return struct.unpack('<I', struct.pack('<f', x))[0]


def hexv(text):
    return [from_bits(int(h, 16)) for h in text.split(',')]


class InverseScale:
    """The engine's inverse scale (src/OniMoono.UnityEngine/Math/InverseScale.cs)."""

    def __init__(self):
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'OniMoono.UnityEngine', 'Math', 'InverseScale.bin')
        self.table = zlib.decompress(open(path, 'rb').read(), -15)

    def of(self, s):
        bits = to_bits(s)
        magnitude = bits & 0x7fffffff
        if magnitude >= 0x7e800000:
            return s if magnitude > 0x7f800000 else (-0.0 if bits >> 31 else 0.0)
        if magnitude < 0x3089705f:
            return 0.0
        rb = to_bits(f32(1.0 / s))
        k = bits & 0x7fffff
        d = (self.table[k >> 2] >> (6 - 2 * (k & 3))) & 3
        return from_bits((rb + d - 1) & 0xffffffff)


def position(log):
    inv = InverseScale()
    cases = lanes = n = shown = 0
    for raw in open(log, errors='replace'):
        m = re.search(r' IS7 (\d+) n=\d+ (.*)$', raw.rstrip())
        if not m:
            continue
        fields = m.group(2).split(' ')
        levels = [f.split('=', 1)[1].split(';') for f in fields if f.startswith('L')]
        kv = dict(f.split('=', 1) for f in fields if not f.startswith('L'))
        p = hexv(kv['v'])
        for lp, _, ls in levels:
            lp, ls = hexv(lp), hexv(ls)
            # The identity inverse rotation adds +0 (-0 becomes +0) before the scale.
            p = [f32(f32(f32(p[k] - lp[k]) + 0.0) * inv.of(ls[k])) for k in range(3)]
        got = [to_bits(x) for x in p]
        n += 1
        for name in ('set', 'itp'):
            want = [to_bits(x) for x in hexv(kv[name])]
            lanes += sum(a == b for a, b in zip(got, want))
            if got == want:
                cases += 1
            elif shown < 10:
                shown += 1
                print('differs', m.group(1), name, [hex(x) for x in got], [hex(x) for x in want])
    print(f'IS7 rule: {cases}/{2 * n} values (setter and InverseTransformPoint), {lanes}/{6 * n} lanes')


def table(log, out):
    classes = {}
    for raw in open(log, errors='replace'):
        m = re.search(r' IS5 (\d+) ([0-9a-f]+)', raw)
        if m:
            classes[int(m.group(1))] = bytes.fromhex(m.group(2))
    if sorted(classes) != list(range(2048)):
        sys.exit(f'{log}: {len(classes)} of 2048 IS5 lines')
    data = b''.join(classes[i] for i in range(2048))
    if any(((byte >> s) & 3) == 3 for byte in data for s in (0, 2, 4, 6)):
        sys.exit('a mantissa more than one ulp from 1/s')
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    open(out, 'wb').write(c.compress(data) + c.flush())


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'position':
        position(sys.argv[2])
    elif len(sys.argv) == 4 and sys.argv[1] == 'table':
        table(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
