#!/usr/bin/env python3
"""lc-check.py: layout groups' moved children and RectMask2D culling from the probe's LC lines.

  lc-check.py summary LOG       one line per case: positions and layout counts (lay), the
                                offsets at which each kind is culled (cull), cull per phase
                                (time, fresh, row)
  lc-check.py diff ORIG OURS    the summary lines that differ; with --positions the layout
                                counts (H, Gdims, Bdims) are left out, so only where the
                                children end up and the cull states are compared

ORIG is the original's Player.log, OURS our player's stdout (PROBE_SCENARIO=layoutcull).
"""

import re
import sys

LINE = re.compile(r'PROBE f\d+ LC (.*)$')
PHASES = ('now', 'U', 'co', 'L', 'R', 'E')


def lines(path):
    for raw in open(path, errors='replace'):
        m = LINE.search(raw.rstrip('\n'))
        if m:
            yield m.group(1)


def snap(s, counts):
    b = re.search(r'B ap=(\S+) sd=(\S+)', s)
    g = re.search(r'G ap=\S+ sd=(\S+)', s)
    out = f'B {b.group(1)} sd {b.group(2)} G {g.group(1)}'
    if counts:
        c = dict(re.findall(r'(H|Gdims|Bdims)=(\d+)', s))
        out += f' H{c["H"]} gd{c["Gdims"]} bd{c["Bdims"]}'
    return out


def summary(path, counts=True):
    out = []
    lay = {}
    order = []
    timing = {}
    torder = []
    for s in lines(path):
        m = re.match(r'lay (\S+) (\S+) (.*)$', s)
        if m and m.group(1) not in ('solo', 'nested', 'poke'):
            key = m.group(1) + ' ' + m.group(2)
            if key not in lay:
                lay[key] = {}
                order.append(key)
            rest = m.group(3)
            word = rest.split(' ', 1)[0]
            r = lay[key]
            if word == 'before':
                r['before'] = snap(rest, counts)
            elif word == 'pokeA':
                r['poke'] = snap(rest, counts)
            elif word == 'left':
                r['left'] = snap(rest, counts)
            elif word in PHASES:
                r['end'] = snap(rest, counts)
            continue
        m = re.match(r'lay (poke \S+|nested \S+ \S+) (before|moved \S+|\S+) (.*)$', s)
        if m:
            key = 'lay ' + m.group(1)
            if key not in lay:
                lay[key] = {}
                order.append(key)
            v = re.sub(r' lp=\S+ am=\S+', '', m.group(3))
            v = re.sub(r'\| A ap.*?\| G', '| G', v)
            v = re.sub(r' Gdims.*', '', v)
            if not counts:
                v = re.sub(r' \| H=\d+ V=\d+', '', v)
            lay[key]['before' if m.group(2) == 'before' else 'end'] = v
            continue
        m = re.match(r'(cull \S+ \S+ \S+) culled at (.*)$', s)
        if m:
            out.append(m.group(1) + ' culled at ' + m.group(2))
            continue
        m = re.match(r'(cull (?:time|fresh|row) .*?) (before|now|L|R|E) cull=(\S+)', s)
        if m:
            key = m.group(1)
            if key not in timing:
                timing[key] = []
                torder.append(key)
            timing[key].append(m.group(2) + '=' + ('T' if m.group(3) == 'True' else 'F'))
    for key in order:
        r = lay[key]
        out.append(key + ' | ' + ' | '.join(f'{k} {r[k]}' for k in ('before', 'end', 'poke', 'left') if k in r))
    for key in torder:
        out.append(key + ' ' + ' '.join(timing[key]))
    return out


def main(argv):
    if len(argv) >= 2 and argv[0] == 'summary':
        print('\n'.join(summary(argv[1])))
        return 0
    if len(argv) >= 3 and argv[0] == 'diff':
        counts = '--positions' not in argv
        files = [a for a in argv[1:] if not a.startswith('--')]
        a, b = summary(files[0], counts), summary(files[1], counts)
        n = 0
        for x, y in zip(a, b):
            if x != y:
                n += 1
                print('- ' + x + '\n+ ' + y)
        if len(a) != len(b):
            print(f'{len(a)} lines in ORIG, {len(b)} in OURS')
        print(f'{n} of {len(a)} lines differ')
        return 1 if n else 0
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
