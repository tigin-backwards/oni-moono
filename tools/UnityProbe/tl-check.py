#!/usr/bin/env python3
"""Checks the tall-line probe's TL lines (TallLine.cs).

  tl-check.py diff ORIG OURS   the sweeps and title cases whose overflow, truncation, counts or
                               characters differ between two players' logs (line positions
                               are left out: they move with every height)
  tl-check.py rule LOG         every 400-wide sweep's firstOverflowCharacterIndex against the
                               measured rule (textmeshpro.md, "Lines that grow part-way")

The rule: a character is checked against the rect's height as its line stands when it is
placed, with the line's ascender and descender over its characters so far. Positions are taken
from the text's `metrics` line, laid out in a rect 400 x 1000 (each line's characters and
baseline); a later line's baseline there is its whole line's, so the line so far sits higher by
the difference between the whole line's ascender and its ascender so far.
"""

import re
import sys

S_LINE = re.compile(r'PROBE f\d+ TL S (.+?) (\d+) (\w+) wrap=(\d) w=(\S+) h=(\S+) "(.*)" (ov=.*?) lines=')
ONE_LINE = re.compile(r'PROBE f\d+ TL one (\S+) (\w+) wrap=(\d) w=(\S+) h=(\S+) "(.*)" (ov=.*?) lines=')
METRICS = re.compile(r'PROBE f\d+ TL metrics (.+?) (\d+) "(.*)" pw=\S+ ph=\S+ chars=(\S*) lines=(\S*)')

# Characters never checked against the height (textmeshpro.md, "Overflow at white space").
UNCHECKED = {0x20, 0xA0, 0x0A, 0x0D, 0x0B, 0x200B, 0x2028, 0x2029, 0x3000} | set(range(0x2000, 0x200B))


def parse(path):
    sweeps, metrics, ones = [], {}, []
    with open(path, encoding='utf-8', errors='replace') as f:
        for raw in f:
            m = METRICS.search(raw)
            if m:
                chars = []
                for part in filter(None, m.group(4).split(';')):
                    code, rest = part.split(':')
                    a, d, _o, _x, ln = rest.split(',')
                    chars.append((int(code, 16), float(a), float(d), int(ln[1:])))
                bases = [float(p.split(':')[1].split(',')[3]) for p in filter(None, m.group(5).split(';'))]
                metrics[(m.group(1), m.group(2), m.group(3))] = (chars, bases)
                continue
            m = S_LINE.search(raw)
            if m:
                sweeps.append(m.groups())
                continue
            m = ONE_LINE.search(raw)
            if m:
                ones.append(m.groups())
    return sweeps, metrics, ones


def predict(chars, bases, h):
    """The index of the first character whose line, as it stands up to it, ends below the rect."""
    if not chars:
        return -1
    top_all = max(a for _c, a, _d, ln in chars if ln == 0)
    top0 = None
    asc, desc = {}, {}
    for i, (code, a, d, ln) in enumerate(chars):
        b = bases[ln]
        if ln == 0:
            top0 = a if top0 is None else max(top0, a)
        asc[ln] = max(asc.get(ln, a - b), a - b)
        desc[ln] = min(desc.get(ln, d - b), d - b)
        whole = max(c[1] for c in chars if c[3] == ln) - b
        shift = whole - asc[ln] if ln > 0 else 0.0
        top = top0 if ln == 0 else top_all
        if code in UNCHECKED:
            continue
        if top - (b + shift + desc[ln]) > h + 0.0001:
            return i
    return -1


def rule(path):
    sweeps, metrics, _ = parse(path)
    ok = bad = 0
    for font, size, mode, wrap, w, h, text, rest in sweeps:
        if float(w) != 400:
            continue
        cb = metrics.get((font, size, text))
        if cb is None:
            continue
        first = int(re.search(r'first=(-?\d+)', rest).group(1))
        p = predict(cb[0], cb[1], float(h))
        if p == first:
            ok += 1
        else:
            bad += 1
            if bad <= 30:
                print(f'MISS {font} {size} {mode} wrap={wrap} h={h} "{text}": measured {first}, rule {p}')
    print(f'{ok} cases follow the rule, {bad} do not')
    return bad == 0


def diff(orig, ours):
    a_s, _, a_o = parse(orig)
    b_s, _, b_o = parse(ours)

    def table(sweeps, ones):
        # Each sweep keeps only its distinct results by height, so compare the result at every
        # height the two logs have between them by carrying the last one forward.
        out = {}
        for font, size, mode, wrap, w, h, text, rest in sweeps:
            out.setdefault((font, size, mode, wrap, w, text), []).append((float(h), rest))
        for tag, mode, wrap, w, h, text, rest in ones:
            out[('one', tag, mode, wrap, w, text)] = [(float(h), rest)]
        return out

    ta, tb = table(a_s, a_o), table(b_s, b_o)
    differ = 0
    other = 0  # heights where the overflow index agrees but the result does not
    for k in sorted(set(ta) | set(tb)):
        if k not in ta or k not in tb:
            print('ONLY IN', 'ORIG' if k in ta else 'OURS', k)
            differ += 1
            continue
        heights = sorted({h for h, _ in ta[k]} | {h for h, _ in tb[k]})

        def at(rows, h):
            r = None
            for hh, rest in rows:
                if hh <= h + 1e-6:
                    r = rest
            return r

        first = True
        for h in heights:
            x, y = at(ta[k], h), at(tb[k], h)
            if x != y:
                if first:
                    differ += 1
                    if differ <= 40:
                        print(f'DIFF {k} h={h}\n  orig {x}\n  ours {y}')
                    first = False
                index = re.compile(r'first=(-?\d+)')
                if x and y and index.search(x).group(1) == index.search(y).group(1):
                    other += 1
                    if other <= 20:
                        print(f'SAME INDEX {k} h={h}\n  orig {x}\n  ours {y}')
    print(f'{len(set(ta) | set(tb))} cases, {differ} differ; {other} heights differ with the same overflow index')
    return differ == 0


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == 'rule':
        sys.exit(0 if rule(sys.argv[2]) else 1)
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        sys.exit(0 if diff(sys.argv[2], sys.argv[3]) else 1)
    print(__doc__)
    sys.exit(2)
