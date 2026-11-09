#!/usr/bin/env python3
"""Reads the empty-text probe's lines (PROBE_SCENARIO=emptytext, `ET ...`).

et-check.py rule LOG          predicts every case from the rule in textmeshpro.md
                              ("Texts with nothing to lay out") and prints those it misses
et-check.py diff ORIG OURS    the cases where the two players differ
et-check.py dump LOG          the sequences after another text, condensed
"""

import collections
import re
import sys

FRESH = re.compile(r'ET fresh (.+?) (\S+) (\w+) wrap=(\d) auto=(\d) rect=95\.7x(\S+) (".*?"|null) (ov=.*)$')
AFTER = re.compile(
    r'ET after (.+?) (\S+) (\w+) auto=(\d) prior=(".*?") rect=95\.7x(\S+) then=(".*?"|null) (force|frame) '
    r'(prior|set|resize=\S+) (ov=.*)$'
)
AUTOMAX = re.compile(r'ET automax (.+?) (\S+) max=(\S+) prior=(".*?"|null) rect=95\.7x(\S+) then=(".*?"|null) (prior|set) (ov=.*)$')
STATE = re.compile(r'ov=(\d) first=(-?\d+) trunc=(\d) cc=(\d+) lc=(\d+) sc=(\d+) wc=(\d+) pc=(\d+) fs=(\S+) pw=(\S+) ph=(\S+) after=(\d)/(-?\d+)')
# What each text lays out to: characters, lines, spaces, words, pages (the counts the probe logs).
COUNTS = {
    '""': (0, 0, 0, 0, 0),
    'null': (0, 0, 0, 0, 0),
    '"<b></b>"': (0, 0, 0, 0, 0),
    '"<color=#FF0000></color>"': (0, 0, 0, 0, 0),
    '"<size=150%></size>"': (0, 0, 0, 0, 0),
    '"<i></i><u></u>"': (0, 0, 0, 0, 0),
    '"\\u0020"': (1, 1, 1, 1, 1),
    '"<b>\\u0020</b>"': (1, 1, 1, 1, 1),
    '"<br>"': (1, 1, 1, 1, 1),
    '"\\u200B"': (1, 1, 0, 1, 1),
}
EMPTY = ('""', 'null')


def norm(key):
    # The rect's height as a key: the players print "R" floats with different digits (Mono
    # "12.5827417", .NET "12.582742").
    return key[:5] + ('%.4f' % float(key[5]),) + key[6:]


def read(path):
    fresh, after, automax = {}, collections.OrderedDict(), {}
    for line in open(path, errors='replace'):
        i = line.find('ET ')
        if i < 0:
            continue
        line = line[i:].rstrip('\n')
        m = FRESH.match(line)
        if m:
            fresh[norm(m.groups()[:7])] = STATE.match(m.group(8)).groups()
            continue
        m = AUTOMAX.match(line)
        if m:
            automax[m.groups()[:7]] = STATE.match(m.group(8)).groups()
            continue
        m = AFTER.match(line)
        if m:
            after.setdefault(norm(m.groups()[:8]), []).append((m.group(9), STATE.match(m.group(10)).groups()))
    return fresh, after, automax


def predict_fresh(key):
    font, size, mode, wrap, auto, h, text = key
    cc, lc, sc, wc, pc = COUNTS[text]
    p = '0' if text in EMPTY else '0.01'
    # Nothing overflows or is cut; auto-size leaves the size as set; the preferred size is 0 for
    # an empty text and a hundredth otherwise.
    return ('0', '-1', '0', str(cc), str(lc), str(sc), str(wc), str(pc), size, p, p, '0', '-1')


def predict_after(key, prior_state, step):
    font, size, mode, auto, prior, h, text, how = key
    if step == 'prior':
        return None
    if text in EMPTY:
        # The last layout's overflow is kept, through resizes too; the truncation and the counts
        # are cleared.
        ov, first = prior_state[0], prior_state[1]
        return (ov, first, '0', '0', '0', '0', '0', '0', size, '0', '0', ov, first)
    return predict_fresh((font, size, mode, '0', auto, h, text))


def predict_automax(key):
    font, size, top, prior, h, text, step = key
    if step != 'set' or text not in COUNTS:
        return None
    cc, lc, sc, wc, pc = COUNTS[text]
    # An empty text takes the size set, no larger than fontSizeMax; a text of white space or
    # tags alone takes fontSizeMax.
    fs = min(float(size), float(top)) if text in EMPTY else float(top)
    p = '0' if text in EMPTY else '0.01'
    return ('0', '-1', '0', str(cc), str(lc), str(sc), str(wc), str(pc), '%g' % fs, p, p, '0', '-1')


def rule(path):
    fresh, after, automax = read(path)
    bad = n = 0
    for k, st in fresh.items():
        n += 1
        if predict_fresh(k) != st:
            bad += 1
            print('MISS fresh', k, st, 'predicted', predict_fresh(k))
    for k, steps in after.items():
        prior_state = steps[0][1]
        for step, st in steps[1:]:
            n += 1
            p = predict_after(k, prior_state, step)
            if p != st:
                bad += 1
                print('MISS after', k, step, st, 'predicted', p)
    for k, st in automax.items():
        p = predict_automax(k)
        if p is None:
            continue
        n += 1
        if p != st:
            bad += 1
            print('MISS automax', k, st, 'predicted', p)
    print(f'{n - bad} of {n} cases follow the rule')
    return 1 if bad else 0


def diff(a, b):
    fa, aa, ma = read(a)
    fb, ab, mb = read(b)
    bad = n = 0
    for k in fa:
        n += 1
        if fb.get(k) != fa[k]:
            bad += 1
            print('DIFF fresh', k, 'orig', fa[k], 'ours', fb.get(k))
    for k in aa:
        sa, sb = aa[k], ab.get(k, [])
        for i, (step, st) in enumerate(sa):
            n += 1
            other = sb[i][1] if i < len(sb) else None
            if other != st:
                bad += 1
                print('DIFF after', k, step, 'orig', st, 'ours', other)
    for k in ma:
        n += 1
        if mb.get(k) != ma[k]:
            bad += 1
            print('DIFF automax', k, 'orig', ma[k], 'ours', mb.get(k))
    print(f'{bad} of {n} cases differ')
    return 1 if bad else 0


def dump(path):
    _, after, _ = read(path)
    for k, steps in after.items():
        print(' '.join(k), '|', ' ; '.join(s + ':' + '/'.join(st[:3]) + ' fs' + st[8] for s, st in steps))


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    if cmd == 'rule' and len(sys.argv) == 3:
        sys.exit(rule(sys.argv[2]))
    if cmd == 'diff' and len(sys.argv) == 4:
        sys.exit(diff(sys.argv[2], sys.argv[3]))
    if cmd == 'dump' and len(sys.argv) == 3:
        sys.exit(dump(sys.argv[2]))
    print(__doc__)
    sys.exit(2)
