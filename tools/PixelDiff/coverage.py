#!/usr/bin/env python3
"""Which game shaders drew through their shipped DXBC on the Direct3D 11 device (done criterion 1
of docs/research/d3d11-plan.md).

    coverage.py LOG... [--list]

Each LOG is a player log of a `--renderer d3d11` run; the device logs each program's first draw
as `d3d11 coverage: NAME sSUBpPASS vs#BLOB [keywords] ps#BLOB [keywords] [gs#BLOB ...]`
(D3d11Programs.CoverageOf). It prints how many distinct shaders, passes, stage programs (a
shader's vertex, pixel or geometry entry, by its blob index) and programs (the combination drawn)
the logs hold, against the data's totals (ShaderSurvey d3d11 on the U60 764025 Windows data: 121
shaders, 176 passes, 1,070 subprograms), and with --list every shader and pass drawn."""

import re
import sys

LINE = re.compile(r'd3d11 coverage: (.+?) (s\d+p\d+|s\?p\?)((?: (?:vs|ps|gs)#\d+ \[[^\]]*\])+)\s*$')
STAGE = re.compile(r'(vs|ps|gs)#(\d+) \[([^\]]*)\]')
TOTALS = {'shaders': 121, 'passes': 176, 'subprograms': 1070}


def main(argv):
    listing = '--list' in argv
    logs = [a for a in argv if a != '--list']
    shaders, passes, stages, programs = set(), set(), set(), set()
    for path in logs:
        with open(path, errors='replace') as f:
            for line in f:
                m = LINE.search(line)
                if not m:
                    continue
                name, where, rest = m.groups()
                shaders.add(name)
                passes.add((name, where))
                found = tuple(STAGE.findall(rest))
                for kind, blob, _ in found:
                    stages.add((name, kind, int(blob)))
                programs.add((name, where, found))
    print(f'shaders drawn: {len(shaders)} of {TOTALS["shaders"]}')
    print(f'passes drawn: {len(passes)} of {TOTALS["passes"]}')
    print(f'stage programs drawn (distinct per shader): {len(stages)} of {TOTALS["subprograms"]} subprograms')
    print(f'programs drawn (vertex, pixel, geometry combinations): {len(programs)}')
    if listing:
        for name, where in sorted(passes):
            n = sum(1 for p in programs if p[0] == name and p[1] == where)
            print(f'  {name} {where}: {n} variant(s)')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
