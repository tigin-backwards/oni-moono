#!/usr/bin/env python3
"""Summarizes a frame recorded by probe_native.dll's ctx_hook.c (UnityProbe's rdcapture scenario).
  frame-report.py PLAYER_LOG DXBC_TABLE LABEL
PLAYER_LOG: the original's Player.log; DXBC_TABLE: `ShaderSurvey dxbc DATADIR` output (checksum,
shader, pass, stage, type, keywords); LABEL: the recorded frame's label ("colony", "menu", ...).
Prints the draws by shader pair, the state objects' descriptions and every distinct draw's
detail lines, with shaders named by their DXBC checksum."""

import re, sys, collections

log, table, label = sys.argv[1], sys.argv[2], sys.argv[3]
names = collections.defaultdict(set)
for line in open(table, encoding='utf-8', errors='replace'):
    f = line.rstrip('\n').split('\t')
    if len(f) >= 5:
        names[f[0]].add(f'{f[1]} {f[2]} {f[3]}' + (f' [{f[5]}]' if len(f) > 5 and f[5] else ''))
lines = [re.sub(r'^.*PROBE f\d+ RD ', '', l.rstrip('\n')) for l in open(log, encoding='utf-8', errors='replace') if ' RD ' in l]
shader = {}
for l in lines:
    m = re.match(r'SHADER (\w+) (\S+) dxbc=(\S+) len=(\d+)', l)
    if m and m.group(1) in ('vs', 'ps', 'gs'):
        shader[m.group(2)] = m.group(3)


def name(p):
    if p in ('0000000000000000', '(nil)'):
        return '-'
    s = shader.get(p)
    if not s:
        return f'{p} (made before the hook)'
    n = names.get(s)
    return ' | '.join(sorted(n)) if n else f'dxbc {s} (not a shipped program)'


start = (
    lines.index(f'record {label} armed') if f'record {label} armed' in lines else next(i for i, l in enumerate(lines) if l.startswith(f'record {label} armed'))
)
end = next(i for i in range(start, len(lines)) if lines[i].startswith(f'record {label} done'))
frame = lines[start:end]
pairs = collections.Counter()
for l in frame:
    m = re.match(r'DRAW \d+ \w+ ctx=\S+ vs=(\S+) ps=(\S+)', l)
    if m:
        pairs[(m.group(1), m.group(2))] += 1
print(f'== draws by shader pair ({sum(pairs.values())} draws)')
for (vs, ps), n in pairs.most_common():
    print(f'{n:6d}  vs {name(vs)}\n        ps {name(ps)}')
print('== state objects')
for l in frame:
    if l.startswith(('RSDESC', 'BSDESC', 'DSDESC', 'SAMPLERDESC', 'CLEARDSV')):
        print(l)
print('== distinct draws (first 40 and each new state combination)')
detail = False
for l in frame:
    if l.startswith('DRAW '):
        m = re.match(r'DRAW \d+ \w+ ctx=\S+ vs=(\S+) ps=(\S+)', l)
        detail = l.endswith(' new')
        if detail:
            print(l + f'\n    vs = {name(m.group(1))}\n    ps = {name(m.group(2))}')
    elif l.startswith('  ') and detail:
        print(l)
