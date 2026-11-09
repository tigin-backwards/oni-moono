#!/usr/bin/env python3
"""ct-check.py: the coroutine traces probe's CT lines (PROBE_SCENARIO=cotraces).

  ct-check.py show LOG          per case: what each log callback got (condition, stack trace)
                                and the log's text for it
  ct-check.py diff ORIG OURS    the cases whose callbacks' strings or log text differ

LOG, ORIG and OURS are Player.log files (the original's, or ours from scripts/e2e.py). Module ids
("<mvid>:0"), our frames' source locations ("(at FILE:LINE)", "in FILE:LINE") and the log's IL
offsets ("[0x0001f]") are compared as equal; everything else, separators and line ends included,
must match.
"""

import re
import sys

CASE = re.compile(r'PROBE f\d+ CT case (\S+) (begin|end|got .*)$')
STAMP = re.compile(r'^\[\d\d:\d\d:\d\d\.\d+\] \[\d+\] \[\w+\] ')


def norm(s):
    s = re.sub(r'<[0-9a-f]{32}>:0', '<mvid>:0', s)
    s = re.sub(r'\(at /[^)]*:\d+\)', '(at <mvid>:0)', s)
    s = re.sub(r' in /\S+:\d+ ', ' in <mvid>:0 ', s)
    # CoreCLR's IL offsets of the throwing instruction differ from Mono's (a known difference of
    # the log's text that log signatures ignore).
    return re.sub(r' \[0x[0-9a-f]{5}\] in ', ' [0x?????] in ', s)


def cases(path):
    """{case: {'got': [...], 'log': [...]}} in the order the cases ran."""
    out, current = {}, None
    for raw in open(path, errors='replace'):
        line = raw.rstrip('\n')
        m = CASE.search(line)
        if m:
            name, what = m.group(1), m.group(2)
            case = out.setdefault(name, {'got': [], 'log': []})
            if what == 'begin':
                current = name
            elif what == 'end':
                current = None
            else:
                case['got'].append(norm(what[4:]))
            continue
        # The log's text for the case: its exception lines, not other components' messages.
        if current and not STAMP.match(line) and not line.startswith('[OniMcp]') and not line.startswith('[E2eDriver]'):
            out[current]['log'].append(norm(line))
    for case in out.values():
        while case['log'] and case['log'][-1] == '':
            case['log'].pop()
    return out


def main(argv):
    if len(argv) == 2 and argv[0] == 'show':
        for name, case in cases(argv[1]).items():
            print(f'== {name}')
            for g in case['got']:
                print('  got ' + g)
            for line in case['log']:
                print('  | ' + line)
        return 0
    if len(argv) == 3 and argv[0] == 'diff':
        orig, ours = cases(argv[1]), cases(argv[2])
        bad = set()
        for name, case in orig.items():
            other = ours.get(name)
            if other is None:
                print(f'{name}: missing in ours')
                bad.add(name)
                continue
            for part in ('got', 'log'):
                if case[part] != other[part]:
                    bad.add(name)
                    print(f'{name} ({part}):')
                    for line in case[part]:
                        print('  orig ' + line)
                    for line in other[part]:
                        print('  ours ' + line)
        print(f'{len(orig) - len(bad)} of {len(orig)} cases match')
        return 1 if bad else 0
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
