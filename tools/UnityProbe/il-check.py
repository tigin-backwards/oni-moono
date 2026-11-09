#!/usr/bin/env python3
"""il-check.py: TMP_InputField's layout element values from the probe's IL lines.

  il-check.py diff ORIG OURS   the cases whose values differ, first field first
  il-check.py rule LOG         check the rule in textmeshpro.md ("The input field's layout
                               values") against a log: preferred = the text's preferred size
                               plus the viewport's offsets, min 0, flexible -1, priority 1

ORIG is the original's Player.log, OURS our player's stdout (PROBE_SCENARIO=inputlayout).
"""

import re
import sys

LINE = re.compile(r'PROBE f\d+ IL (.*)$')
PHASE = re.compile(r'^(.*?) (now|rebuilt|frame\d*|pass\d(?: row=\S+ rowrect=\S+)?) (min=.*)$')


def load(path):
    cases = {}
    for raw in open(path, errors='replace'):
        m = LINE.search(raw.rstrip('\n'))
        if not m:
            continue
        p = PHASE.match(m.group(1))
        if not p:
            continue
        phase = p.group(2).split(' ')[0]
        rest = p.group(2)[len(phase) :].strip()
        vals = dict(kv.split('=', 1) for kv in re.findall(r'\S+=(?:"[^"]*"|\S+)', rest + ' ' + p.group(3)))
        cases[(p.group(1), phase)] = vals
    return cases


ORDER = ['pref', 'min', 'flex', 'prio', 'LU', 'row', 'rowrect', 'rect', 'vp', 'vpoff', 'text', 'trect', 'label', 'ph', 'phOn']


def diff(a, b):
    A, B = load(a), load(b)
    n = 0
    first = {}
    for key in A:
        if key not in B:
            print('missing in ours:', *key)
            n += 1
            continue
        d = [k for k in ORDER if A[key].get(k) != B[key].get(k)]
        if d:
            n += 1
            first.setdefault(d[0], 0)
            first[d[0]] += 1
            print(' '.join(key), '|', ' '.join(f'{k}: {A[key].get(k)} vs {B[key].get(k)}' for k in d))
    print(f'{len(A)} lines, {n} differ; first differing value: {first}')


def rule(path):
    C = load(path)
    ok = bad = 0
    for key, v in C.items():
        if 'text' not in v:
            exp_pref = ('0', '0')
        else:
            tx, ty = map(float, v['text'].split(','))
            px = py = 0.0
            if v.get('vpoff'):
                mn, mx = v['vpoff'].split('/')
                mnx, mny = map(float, mn.split(','))
                mxx, mxy = map(float, mx.split(','))
                px, py = mnx - mxx, mny - mxy
            exp_pref = (tx + px, ty + py)
        got = tuple(map(float, v['pref'].split(',')))
        good = all(abs(float(e) - g) < 1e-3 for e, g in zip(exp_pref, got)) and v['min'] == '0,0' and v['flex'] == '-1,-1' and v['prio'] == '1'
        if good:
            ok += 1
        else:
            bad += 1
            print('miss', *key, 'pref', v['pref'], 'expected', exp_pref)
    print(f'rule: {ok} of {ok + bad} lines')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        print(__doc__)
        sys.exit(2)
