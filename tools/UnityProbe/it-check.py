#!/usr/bin/env python3
"""it-check.py: text set from code on TMP_InputField, from the probe's IT lines.

  it-check.py diff ORIG OURS   the steps whose values differ, and a count by value
  it-check.py rule LOG         check the rule in textmeshpro.md ("Text set from code"):
                               text = and SetTextWithoutNotify keep the value whole, whatever
                               the limit, line type and content type, and nothing applies
                               them later; only text = reports onValueChanged; an edit at or
                               over the limit refuses a typed character

ORIG is the original's Player.log, OURS our player's stdout (PROBE_SCENARIO=inputtext).
"""

import re
import sys
from collections import Counter, OrderedDict

LINE = re.compile(r'PROBE f\d+ IT (.*)$')
STEP = re.compile(r'^(.*) (pre|set|setting|frame|enabled|enabled\+1|activate|activate\+1|activate\+2|typed|typed\+1|backspace|deselect|deselect\+1) (text=.*)$')
KV = re.compile(r'(\w+)=((?:[^\s"]|"(?:[^"\\]|\\.)*")+)')
CASE = re.compile(r'^(\S+) ("(?:[^"\\]|\\.)*") (text|notify-less|label) (inactive|active|focused)')


def load(path):
    cases = OrderedDict()
    for raw in open(path, errors='replace'):
        m = LINE.search(raw.rstrip('\n'))
        if not m:
            continue
        s = STEP.match(m.group(1))
        if s:
            cases.setdefault(s.group(1), OrderedDict())[s.group(2)] = dict(KV.findall(s.group(3)))
    return cases


def diff(a, b):
    A, B = load(a), load(b)
    n = 0
    by = Counter()
    for case, steps in A.items():
        for step, v in steps.items():
            w = B.get(case, {}).get(step)
            if w is None:
                print('missing in ours:', case, step)
                n += 1
                continue
            d = [k for k in v if v[k] != w.get(k)]
            if d:
                n += 1
                by.update(d)
                print(case, step, '|', ' '.join(f'{k}: {v[k]} vs {w.get(k)}' for k in d))
    print(f'{sum(len(s) for s in A.values())} steps, {n} differ; values: {dict(by)}')


def unesc(s):
    return re.sub(r'\\u([0-9A-F]{4})', lambda m: chr(int(m.group(1), 16)), s[1:-1])


def rule(path):
    C = load(path)
    ok = bad = 0
    for case, steps in C.items():
        m = CASE.match(case)
        if not m:
            continue
        given, setter = unesc(m.group(2)), m.group(3)
        expect = '' if setter == 'label' else given
        problems = []
        s = steps['set']
        if unesc(s['text']) != expect:
            problems.append('set text ' + s['text'])
        if setter == 'text' and s['ev'] != 'changed:' + m.group(2):
            problems.append('set ev ' + s['ev'])
        if setter != 'text' and s['ev'] != '-':
            problems.append('set ev ' + s['ev'])
        for step in ('setting', 'frame', 'enabled', 'enabled+1', 'activate', 'activate+1', 'activate+2'):
            if step in steps and (unesc(steps[step]['text']) != expect or steps[step]['ev'] != '-'):
                problems.append(step + ' ' + steps[step]['text'] + ' ' + steps[step]['ev'])
        limit = int(steps['typed']['limit'])
        typed = unesc(steps['typed']['text'])
        if limit and len(expect) >= limit and typed != expect:
            problems.append('typed past the limit ' + steps['typed']['text'])
        if problems:
            bad += 1
            print('miss', case, '; '.join(problems))
        else:
            ok += 1
    print(f'rule: {ok} of {ok + bad} cases')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        print(__doc__)
        sys.exit(2)
