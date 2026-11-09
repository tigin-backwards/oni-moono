#!/usr/bin/env python3
"""The tooltip probe (Tooltip.cs, lines "PROBE f<frame> TT ..."):
  tt-check.py rule LOG          checks the GUI.tooltip rules of docs/cleanroom/imgui.md against a log
  tt-check.py diff ORIG OURS    every step whose calls set or read GUI.tooltip differently
  tt-check.py show LOG          per step: what each call read and which controls set the tooltip
Exit code 0 when the rules hold (rule) or nothing differs (diff)."""

import re, sys

LINE = re.compile(r'PROBE f(\d+) TT (.*)$')
TOKEN = re.compile(r"(\w+)=('(?:[^']*)'|null|<[^>]*>|\S+)")
CALLS = ('gui', 'gui2', 'w1', 'w2', 'w3', 'w4')
OUTSIDE = ('update', 'late', 'eof')
# Points that are not controls with a tooltip of their own.
NO_TIP = {'B', 'W1b', 'W2b', 'W2tail', 'areaEnd', 'groupEnd', 'scrollEnd', 'afterW1', 'afterW2', 'afterW3', 'afterW4'}
# The clip each control is drawn in, besides its window: the scroll views' visible rects.
CLIP = {'F': 'Fview', 'G': 'Fview', **{f'W2s{i}': 'W2view' for i in range(6)}}
WINDOW = {'w1': 'w1', 'w2': 'w2', 'w4': 'w4'}
# What each mode's set writes, as read straight after it.
SET_VALUE = {'reset': "'-'", 'setstart': "'S'", 'setend': "'E'", 'setwin': "'W'", 'setw2end': "'V'", 'setnull': "''"}


def rect(text):
    x, y, w, h = (float(v) for v in text.split(','))
    return x, y, w, h


def contains(r, p):
    return r[0] <= p[0] < r[0] + r[2] and r[1] <= p[1] < r[1] + r[3]


def parse(path):
    """The log's TT lines in order: ('call', frame, step, mode, who, event, raw, info, points),
    ('outside', frame, step, mode, where, value) or ('info', frame, text)."""
    out = []
    with open(path, errors='replace') as f:
        for line in f:
            m = LINE.search(line.rstrip('\n'))
            if not m:
                continue
            frame, text = int(m.group(1)), m.group(2)
            parts = text.split(' ')
            if len(parts) >= 4 and parts[2] in CALLS:
                tokens = TOKEN.findall(text)
                keys = [k for k, _ in tokens]
                at = keys.index('start') if 'start' in keys else 0
                info = dict(tokens[: at + 1])
                points = [(k, v) for k, v in tokens[at:] if k != 'rect']
                out.append(('call', frame, parts[0], parts[1], parts[2], parts[3], info.get('raw'), dict(tokens), points))
            elif len(parts) >= 4 and parts[2] in OUTSIDE:
                out.append(('outside', frame, parts[0], parts[1], parts[2], parts[3].split('=', 1)[1]))
            else:
                out.append(('info', frame, text))
    return out


def rects_of(entries):
    """The control and window rects (screen, GUI coordinates) of the 'rects' lines, by frame."""
    tables = []
    for e in entries:
        if e[0] == 'info' and e[2].startswith('rects'):
            tables.append((e[1], {k: rect(v) for k, v in TOKEN.findall(e[2])}))
    return tables


def summary(entries):
    """{(step, mode): [distinct call summaries in order]}: each call's start value, its sets and
    the controls that changed the value, with the outside reads."""
    out, order = {}, []
    for e in entries:
        if e[0] == 'info':
            continue
        key = (e[2], e[3])
        if key not in out:
            out[key] = []
            order.append(key)
        if e[0] == 'outside':
            s = f'{e[4]} {e[5]}'
        else:
            _, _, _, _, who, event, raw, info, points = e
            seq, prev = [], None
            for k, v in points:
                if k in ('h', 'tr'):
                    continue
                if k == 'set':
                    seq.append(f'set:{v}')
                    prev = v.split(':', 1)[1]
                elif k == 'start':
                    seq.append(f'start={v}')
                    prev = v
                elif v != prev:
                    seq.append(f'{k}={v}')
                    prev = v
            s = f'{who} {event}' + (f' (raw {raw})' if raw != event else '') + ' | ' + ' '.join(seq)
        if s not in out[key]:
            out[key].append(s)
    return order, out


def show(path):
    order, table = summary(parse(path))
    for key in order:
        print(f'== {key[0]} {key[1]}')
        for s in table[key]:
            print('   ' + s)
    return 0


def rule(path):
    entries = parse(path)
    tables = rects_of(entries)
    if not tables:
        print(f'no TT rects line in {path}')
        return 1
    bad, checked, sets = [], 0, 0
    cur = None
    hot_owner = {}

    def fail(e, text):
        bad.append(f'f{e[1]} {e[2]} {e[3]} {" ".join(str(x) for x in e[4:6])}: {text}')

    last_frame = None
    for e in entries:
        # Frames the probe did not record lie between: the value may have changed there.
        if last_frame is not None and e[1] > last_frame + 1:
            cur = None
        last_frame = e[1]
        if e[0] == 'info':
            m = re.search(r"tip=('.*'|<.*>|null)$", e[2])
            if m:
                if cur is not None and m.group(1) != cur:
                    fail(e, f'read {m.group(1)} outside OnGUI where the last value was {cur}')
                cur = m.group(1)
            continue
        if e[0] == 'outside':
            # Rule 1: the value persists outside OnGUI.
            if cur is not None and e[5] != cur:
                fail(e, f'read {e[5]} outside OnGUI where the last value was {cur}')
            cur = e[5]
            continue
        _, frame, step, mode, who, event, raw, info, points = e
        rects = [t for f, t in tables if f <= frame]
        rects = rects[-1] if rects else tables[0][1]
        checked += 1
        start = info.get('start')
        # Rule 1: a call starts with the value the previous one ended with.
        if cur is not None and start != cur:
            fail(e, f'starts with {start}, the last value was {cur}')
        cur = start
        m = [float(v) for v in info['m'].split(',')]
        origin = rects.get(WINDOW.get(who), (0, 0, 0, 0)) if who in WINDOW else (0, 0, 0, 0)
        mouse = (m[0] + origin[0], m[1] + origin[1])
        hot = info.get('hot')
        last = None
        for k, v in points:
            if k == 'start' or k == 'tr' or k == 'rect':
                continue
            if k == 'h':
                if v != '0':
                    hot_owner[v] = last
                hot = v
                continue
            if k == 'set':
                # Rule 5: a set reads back at once (null as ""), and sticks.
                cur = v.split(':', 1)[1]
                want = SET_VALUE.get(mode, SET_VALUE['reset'] if mode.startswith('reset') else None)
                if cur != want:
                    fail(e, f'a set in mode {mode} read back {cur}, expected {want}')
                continue
            last = k
            if k == 'end':
                if v != cur:
                    fail(e, f'ends with {v}, the last value was {cur}')
                continue
            expected_set = False
            if k not in NO_TIP and event == 'repaint' and k in rects:
                r = rects[k]
                clip = rects.get(CLIP.get(k)) if k in CLIP else None
                window = rects.get(WINDOW.get(who)) if who in WINDOW else None
                over = contains(r, mouse) and (clip is None or contains(clip, mouse)) and (window is None or contains(window, mouse))
                held = hot_owner.get(hot) == k and hot != '0'
                expected_set = over or held
            if expected_set:
                sets += 1
                want = f"'t{k}'"
                if v != want:
                    fail(e, f'{k} under the mouse (or holding it) left {v}, expected {want}')
                cur = v
            elif v != cur:
                fail(e, f'{k} changed the value to {v} (from {cur}) in {event}')
                cur = v
    for b in bad[:60]:
        print(b)
    print(f'{checked} calls, {sets} expected sets; {len(bad)} failures')
    return 1 if bad else 0


def diff(orig_path, ours_path):
    o_order, orig = summary(parse(orig_path))
    _, ours = summary(parse(ours_path))
    if not orig:
        print(f'no TT lines in {orig_path}')
        return 1
    bad = 0
    for key in o_order:
        a, b = orig[key], ours.get(key)
        if a == b:
            continue
        bad += 1
        print(f'== {key[0]} {key[1]}')
        for s in a:
            if b is None or s not in b:
                print('   original: ' + s)
        for s in b or []:
            if s not in a:
                print('   ours:     ' + s)
    print(f'{len(o_order)} steps in the original, {len(ours)} in ours, {bad} differ')
    return 1 if bad else 0


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == 'rule':
        sys.exit(rule(sys.argv[2]))
    if len(sys.argv) == 3 and sys.argv[1] == 'show':
        sys.exit(show(sys.argv[2]))
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        sys.exit(diff(sys.argv[2], sys.argv[3]))
    print(__doc__)
    sys.exit(2)
