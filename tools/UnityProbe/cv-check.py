#!/usr/bin/env python3
"""Checks the probe's "CV" lines (Conversions.cs, PROBE_SCENARIO=convert).
  cv-check.py rule LOG          the unchecked conversions against x86 truncation (cvttsd2si) rules
  cv-check.py diff ORIG OURS    the lines whose results differ between two players, by operation
Inputs are matched by their bits, since the players format "R" differently (ARCHITECTURE D2)."""

import collections, math, re, struct, sys


def value(bits):
    if len(bits) == 16:
        return struct.unpack('<d', struct.pack('<Q', int(bits, 16)))[0]
    return struct.unpack('<f', struct.pack('<I', int(bits, 16)))[0]


def cvt(x, n):
    """cvttsd2si to n bits: the truncation, or the minimum ("integer indefinite") out of range."""
    if not math.isfinite(x):
        return -(1 << (n - 1))
    t = math.trunc(x)
    return t if -(1 << (n - 1)) <= t < (1 << (n - 1)) else -(1 << (n - 1))


def wrap(v, n, signed):
    v &= (1 << n) - 1
    return v - (1 << n) if signed and v >> (n - 1) else v


def u8(x):
    if x < 2.0**63:
        return cvt(x, 64) & (2**64 - 1)
    return (cvt(x - 2.0**63, 64) & (2**64 - 1)) ^ (1 << 63)


def whole(f, x):
    return f(x) if math.isfinite(x) else x


RULES = {
    'conv.i1': lambda x: wrap(cvt(x, 32), 8, True),
    'conv.i2': lambda x: wrap(cvt(x, 32), 16, True),
    'conv.i4': lambda x: cvt(x, 32),
    'conv.i8': lambda x: cvt(x, 64),
    'conv.u1': lambda x: wrap(cvt(x, 32), 8, False),
    'conv.u2': lambda x: wrap(cvt(x, 32), 16, False),
    'conv.u4': lambda x: wrap(cvt(x, 64), 32, False),
    'conv.u8': u8,
    'conv.u': u8,
    'conv.i': lambda x: wrap(cvt(x, 32), 32, False),
    'Mathf.RoundToInt': lambda x: cvt(whole(lambda v: float(round(v)), x), 32),
    'Math.Round>conv.i4': lambda x: cvt(whole(lambda v: float(round(v)), x), 32),
    'Mathf.FloorToInt': lambda x: cvt(whole(math.floor, x), 32),
    'Math.Floor>conv.i4': lambda x: cvt(whole(math.floor, x), 32),
    'Mathf.CeilToInt': lambda x: cvt(whole(math.ceil, x), 32),
}
# The inline lines: (int) (long) (uint) (ulong) (short) (byte).
INLINE = [RULES['conv.i4'], RULES['conv.i8'], RULES['conv.u4'], u8, RULES['conv.i2'], RULES['conv.u1']]


def rule(log):
    ok = bad = 0
    for line in open(log, errors='replace'):
        m = re.search(r'CV (\S+) (\S+) \S*?\(([0-9A-F]+)\) = (.*)$', line.rstrip())
        if not m:
            continue
        source, op, bits, result = m.groups()
        x = value(bits)
        if source in ('f32', 'f64') and op in RULES:
            want, got = [RULES[op](x)], [int(result)]
        elif source in ('f32i', 'f64i') and op == 'conv':
            want, got = [r(x) for r in INLINE], [int(v) for v in result.split()]
        elif source == 'f32i' and op == 'product':
            want, got = [r(x) for r in INLINE[:3]], [int(v) for v in result.split()]
        else:
            continue
        if want == got:
            ok += 1
        else:
            bad += 1
            print('differs', source, op, bits, x, 'logged', got, 'rule', want)
    print(f'{ok} lines follow the rules, {bad} do not')
    return 1 if bad else 0


def read(log):
    out, seen = {}, collections.Counter()
    for line in open(log, errors='replace'):
        m = re.search(r'PROBE f\d+ (CV .*?) = (.*)$', line.rstrip())
        if not m:
            continue
        key = re.sub(r'\S*\(([0-9A-F]+)\)', r'\1', m.group(1))
        seen[key] += 1
        out[(key, seen[key])] = re.sub(r'\S*\(([0-9A-F]+)\)', r'\1', m.group(2))
    return out


def diff(orig, ours):
    a, b = read(orig), read(ours)
    groups = collections.OrderedDict()
    for k, v in a.items():
        if b.get(k) != v:
            parts = k[0].split(' ')
            groups.setdefault(' '.join(parts[1:3]), []).append((parts[3] if len(parts) > 3 else '', v, b.get(k)))
    print(f'{len(a)} lines, {sum(len(g) for g in groups.values())} differ, {len(set(b) - set(a))} only in ours')
    for g, rows in groups.items():
        print(f'== {g}: {len(rows)}')
        for x, v, w in rows[:4]:
            print(f'   {x}: original {v} / ours {w}')
    return 1 if groups else 0


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == 'rule':
        sys.exit(rule(sys.argv[2]))
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        sys.exit(diff(sys.argv[2], sys.argv[3]))
    sys.exit(__doc__)
