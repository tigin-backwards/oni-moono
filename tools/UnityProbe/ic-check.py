#!/usr/bin/env python3
"""ic-check.py: the input field's caret after an edit ends, and the caret graphic's cull, from
the probe's IC lines (PROBE_SCENARIO=inputcaret).

  ic-check.py diff ORIG OURS [end|x|cull]   the steps whose values differ, and a count by value
  ic-check.py rule LOG                      check the rules in textmeshpro.md ("The caret after
                                            an edit ends", "The caret graphic and RectMask2D"):
                                            an ended edit keeps the caret and the selection's
                                            anchor (cut to the text's length), whatever
                                            resetOnDeActivation and clearSelection say; when
                                            onEndEdit and onSubmit are sent; the caret graphic
                                            is culled only when its clip rect is invalid

ORIG is the original's Player.log, OURS our player's stdout.
"""

import re
import sys
from collections import Counter, OrderedDict

LINE = re.compile(r'PROBE f\d+ IC (end|cull) (.*)$')
END = re.compile(r'^(.*) (focused|ctrl-a|input|input\+1|end|end\+1|enabled|end\+2|again|nine) (text=.*)$')
CULL = re.compile(
    r'^(.*) (made|frame1|frame2|focused|ended|mask-moved|mask-back|field-moved|caret-cycled|caret-reenabled|caret-moved|field-cycled|Cull\(far,true\)|Cull\(all,true\)|Cull\(far,false\)|after-calls\+1) (caret: .*)$'
)
KV = re.compile(r'(\w+)=((?:[^\s"]|"(?:[^"\\]|\\.)*")+)')


def load(path):
    cases = OrderedDict()
    for raw in open(path, errors='replace'):
        m = LINE.search(raw.rstrip('\n'))
        if not m:
            continue
        kind, rest = m.groups()
        if kind == 'end':
            s = END.match(rest)
            if s:
                cases.setdefault(('end', s.group(1)), OrderedDict())[s.group(2)] = dict(KV.findall(s.group(3)))
        else:
            s = CULL.match(rest)
            if s:
                values = {}
                for part in s.group(3).split(' | '):
                    name, _, body = part.partition(': ')
                    if '=' in name:  # the trailing focused=… caretPos=…
                        values.update(KV.findall(part))
                        continue
                    for k, v in KV.findall(body):
                        values[name + '.' + k] = v
                cases.setdefault(('cull', s.group(1)), OrderedDict())[s.group(2)] = values
    return cases


def section(key):
    kind, tag = key
    return 'x' if kind == 'end' and tag.startswith('x ') else kind


def diff(a, b, only=None):
    A, B = load(a), load(b)
    n = total = 0
    by = Counter()
    for case, steps in A.items():
        if only and section(case) != only:
            continue
        for step, v in steps.items():
            total += 1
            w = B.get(case, {}).get(step)
            if w is None:
                print('missing in ours:', *case, step)
                n += 1
                continue
            # Our CanvasRenderer has no GetMesh: its meshes log as n/a.
            d = [k for k in v if v[k] != w.get(k) and not (k.endswith('.mesh') and w.get(k) == 'n/a')]
            if d:
                n += 1
                by.update(d)
                print(*case, step, '|', ' '.join(f'{k}: {v[k]} vs {w.get(k)}' for k in d))
    print(f'{total} steps, {n} differ; values: {dict(by)}')


def unesc(s):
    return re.sub(r'\\u([0-9A-F]{4})', lambda m: chr(int(m.group(1), 16)), s[1:-1])


def kinds(ev):
    """The event names in an ev= value, in order ("changed" left out)."""
    if ev == '-':
        return []
    names = [e.split(':')[0].split('@')[0] for e in re.split(r',(?=[A-Za-z-]+[:@])', ev)]
    return [n for n in names if n != 'changed']


def rule(path):
    C = load(path)
    ok = bad = 0
    for (kind, tag), steps in C.items():
        problems = []
        if kind == 'end':
            words = tag.split()
            real = words[0] == 'x'
            if real:
                words = words[1:]
            end = words[1]
            reset = 'reset=True' in tag
            before, after, at = steps.get('input+1') or steps.get('input'), steps.get('end+2'), steps.get('end')
            if before and after and at:
                # 1. The caret and the selection's anchor stay where they were, cut to the text's
                # length when the text got shorter (a listener's text, or Escape's restore).
                # Each text it passes through cuts it (Escape restoring "", then a listener's "0").
                texts = [after['text']] + re.findall(r'changed:("(?:[^"\\]|\\.)*")', at['ev'])
                n = min(len(unesc(t)) for t in texts)
                want = (str(min(int(before['caret']), n)), str(min(int(before['anchor']), n)))
                if (after['caret'], after['anchor']) != want:
                    problems.append(f"caret {before['caret']}/{before['anchor']} became {after['caret']}/{after['anchor']}, not {want[0]}/{want[1]}")
                # 2. onEndEdit: Enter and Escape (real keys) always send it; ending the edit
                # otherwise sends it only with resetOnDeActivation or clearSelection.
                ev = kinds(at['ev']) + kinds(steps.get('end+1', {}).get('ev', '-'))
                if end == 'return' and not real:
                    want_end = False  # ProcessEvent(Return) ends nothing
                    if at['focused'] != 'True':
                        problems.append('ProcessEvent(Return) unfocused the field')
                elif real and end in ('return', 'escape'):
                    want_end = True
                else:
                    want_end = reset or end == 'deactivate-clear'
                if ('endEdit' in ev) != want_end:
                    problems.append(f"onEndEdit {'sent' if 'endEdit' in ev else 'not sent'} ({' '.join(ev) or '-'})")
                # 3. OnSubmit sends onSubmit and ends the edit.
                if end == 'submit' and (ev[:1] != ['submit'] or at['focused'] != 'False'):
                    problems.append(f"OnSubmit: {' '.join(ev) or '-'}, focused={at['focused']}")
        else:
            out = ' outer-out ' in f' {tag} '
            for step, v in steps.items():
                if step.startswith('Cull(') or step == 'after-calls+1' or step == 'made':
                    continue
                if (v.get('caret.cull') == 'True') != out:
                    problems.append(f"{step}: caret cull={v.get('caret.cull')} clip={v.get('caret.clip')}")
            # A direct Cull with a valid rect never culls the caret; with an invalid one it does.
            far, nofar = steps.get('Cull(far,true)'), steps.get('Cull(far,false)')
            if far and far.get('caret.cull') != 'False':
                problems.append('Cull(far,true) culled the caret')
            if nofar and nofar.get('caret.cull') != 'True':
                problems.append('Cull(far,false) did not cull the caret')
        if problems:
            bad += 1
            print('FAIL', kind, tag, '|', '; '.join(problems))
        else:
            ok += 1
    print(f'{ok} cases follow the rule, {bad} do not')
    return bad == 0


if __name__ == '__main__':
    if len(sys.argv) >= 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else None)
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        sys.exit(0 if rule(sys.argv[2]) else 1)
    else:
        print(__doc__)
        sys.exit(2)
