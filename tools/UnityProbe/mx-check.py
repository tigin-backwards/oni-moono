#!/usr/bin/env python3
"""Reads the modtext probe's logs (lines "PROBE f<frame> MX ...").
  mx-check.py diff ORIG OURS [PREFIX]   lines that differ (PREFIX: font, ellipsis, bold, lines, pref)
  mx-check.py rule LOG                  the line-interval and preferred-height rules of
                                        textmeshpro.md ("Line gaps and preferred heights with
                                        fallback glyphs") against the pref lines of a run with
                                        PROBE_MX_PART=pref PROBE_MX_PREF=2
Numbers are compared as float32 values, since Mono's and .NET's "R" formats print the same float
with different digits (482.328125 and 482.32812)."""

import math, re, struct, sys

NUM = re.compile(r'-?\d+(?:\.\d+)?(?:E[-+]?\d+)?')
# pointSize, lineHeight, ascentLine, descentLine of the primary fonts the pref cases use (the
# probe's "face" lines).
FACES = {'GRAYSTROKE REGULAR SDF': (124, 130.0625, 117.6875, -12.4375), 'LiberationSans SDF': (86, 98.8916, 77.8535156, -18.22461)}
WHITE = ('20', 'A', 'D', 'A0')


def load(path):
    out = []
    with open(path, errors='replace') as f:
        for line in f:
            m = re.search(r'PROBE f\d+ MX (.*)', line)
            if m:
                out.append(m.group(1).rstrip())
    return out


def f32(s):
    return struct.unpack('f', struct.pack('f', float(s)))[0]


def same(a, b):
    na, nb = NUM.findall(a), NUM.findall(b)
    if NUM.sub('#', a) != NUM.sub('#', b) or len(na) != len(nb):
        return False
    return all(f32(x) == f32(y) for x, y in zip(na, nb))


def diff(orig, ours, prefix):
    orig, ours = load(orig), load(ours)
    if len(orig) != len(ours):
        print(f'{len(orig)} lines in the original, {len(ours)} in ours')
    n = shown = 0
    for a, b in zip(orig, ours):
        if not a.startswith(prefix):
            continue
        n += 1
        if not same(a, b):
            shown += 1
            print('- ' + a)
            print('+ ' + b)
    print(f'{shown} of {n} lines differ')


def pref_rows(path):
    for line in load(path):
        m = re.match(r'pref (.*?) (\S+) wrap=(\d) "(.*)" ph0=(\S+) ph1=(\S+) ext=(\S+) lines=(\S*) chars=(\S*)$', line)
        if not m:
            continue
        font, size, wrap, text, ph0, ph1, ext, lines, chars = m.groups()
        ls = []
        for part in lines.split(';'):
            rng, vals = part.split(':', 1)
            a, b = rng.split('-')
            v = vals.split(',')
            ls.append(dict(first=int(a), last=int(b), asc=float(v[0]), desc=float(v[1]), base=float(v[3])))
        cs = []
        for c in chars.split(';'):
            code, rest = c.split(':')
            a, d, b, ln = rest.split(',')
            cs.append(dict(code=code, asc=float(a), desc=float(d), base=float(b)))
        yield font, float(size), wrap, text, float(ph0), ls, cs


def rule(path):
    gap_ok = gap_bad = ph_ok = ph_bad = 0
    for font, size, wrap, text, ph, ls, cs in pref_rows(path):
        pt, lh, asc, desc = FACES[font]
        gap = (lh - asc + desc) * size / pt
        # Line intervals: b[k-1] - b[k] = H[k-1] - A[k-1] + A[k].
        for k in range(1, len(ls)):
            p, ln = ls[k - 1], ls[k]
            height = max(cs[i]['asc'] - cs[i]['desc'] for i in range(p['first'], p['last'] + 1)) + gap
            want = p['base'] - ln['base']
            got = height - (p['asc'] - p['base']) + (ln['asc'] - ln['base'])
            if abs(want - got) < 2e-4:
                gap_ok += 1
            else:
                gap_bad += 1
                print(f'interval {font} "{text}" line {k}: {want:.5f}, rule {got:.5f}')
        # Preferred height: the first line's top down to the lowest of the last line's descender
        # and, on each later line with a visible character after its first, the first
        # character's descender below a baseline lowered by the line before's ascender less that
        # character's ascender.
        bottom = ls[-1]['desc']
        for k in range(1, len(ls)):
            ln = ls[k]
            if not any(cs[i]['code'] not in WHITE for i in range(ln['first'] + 1, ln['last'] + 1)):
                continue
            first = cs[ln['first']]
            lift = (ls[k - 1]['asc'] - ls[k - 1]['base']) - (first['asc'] - first['base'])
            if lift > 0:
                bottom = min(bottom, ln['base'] - lift + (first['desc'] - first['base']))
        h = ls[0]['asc'] - bottom
        pred = (math.floor(h * 100) + 1) / 100
        if abs(pred - ph) < 0.004:
            ph_ok += 1
        else:
            ph_bad += 1
            print(f'preferred {font} wrap={wrap} "{text}": {ph}, rule {pred:.2f}')
    print(f'intervals: {gap_ok} match, {gap_bad} differ; preferred heights: {ph_ok} match, {ph_bad} differ')


def main():
    if len(sys.argv) >= 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else '')
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
