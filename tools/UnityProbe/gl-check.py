#!/usr/bin/env python3
"""The GUILayout probe (GuiLayout.cs, lines "PROBE f<frame> GL ..."):
  gl-check.py rule LOG          checks the GUILayout rules of docs/cleanroom/imgui.md against a log
  gl-check.py diff ORIG OURS    every measurement, window size, order step and mismatch case that differs
  gl-check.py show LOG [PART]   the log by part: size, ws, ord, mm
Exit code 0 when the rules hold (rule) or nothing differs (diff)."""

import math, re, struct, sys

LINE = re.compile(r'PROBE f(\d+) GL (.*)$')
DUMMY = '0,0,1,1'


def parse(path):
    out = []
    with open(path, errors='replace') as f:
        for line in f:
            m = LINE.search(line.rstrip('\n'))
            if m:
                out.append((int(m.group(1)), m.group(2)))
    return out


def rect(text):
    return tuple(float(v) for v in text.split(','))


def f32(v):
    return struct.unpack('f', struct.pack('f', v))[0]


def norm(text):
    """A value as the float32 numbers it holds: Mono's "R" prints a float in 9 digits where 7
    do not read back ("72.0200043"), .NET the shortest string that does ("72.020004"); the same
    value either way. Other text is left as it is."""
    try:
        return ','.join(repr(f32(float(v))) for v in text.split(','))
    except ValueError:
        return text


def kv(text):
    return dict(re.findall(r'(\S+?)=(\S+)', text))


# ----- size -----


def sizes(entries):
    """'size NAME', 'seq NAME', 'style NAME' and 'skin' -> dict of values."""
    out = {}
    for _, t in entries:
        p = t.split(' ', 2)
        if p[0] in ('size', 'seq', 'style') and len(p) > 1:
            out[p[0] + ' ' + p[1]] = kv(p[2] if len(p) > 2 else '')
            if p[1] == 'root.label':  # several 'NAME r=RECT' pairs on one line
                out['size root.label'] = dict(re.findall(r'(\S+) r=(\S+)', 'root.label ' + p[2]))
        elif p[0] == 'skin':
            out['skin'] = kv(t)
    return out


# ----- ws -----


def ws_lines(entries):
    """(case, id, event) -> dict(n, in, out); ('f', case, id, event) -> dict(first, last)."""
    out = {}
    for _, t in entries:
        p = t.split(' ')
        if p[0] == 'ws' and len(p) >= 6:
            out[(p[1], int(p[2]), p[3].lower())] = kv(' '.join(p[4:]))
        elif p[0] == 'wsf' and len(p) >= 6:
            out[('f', p[1], int(p[2]), p[3].lower())] = kv(' '.join(p[4:]))
    return out


# ----- mm -----


def mm_cases(entries):
    """case -> rows: ('call', frame, who, event, raw, [(name, value)], tail) or ('log', frame, type, text)."""
    cases = {}
    for frame, t in entries:
        if t.startswith('mm '):
            m = re.match(r'^(.*?) ::(.*) :: (end|abort)$', t)
            if not m:
                continue
            head, body, tail = m.group(1), m.group(2), m.group(3)
            p = head.split(' ')
            items = []
            for it in body.split(' ;'):
                it = it.strip()
                if it:
                    n, _, v = it.partition('=')
                    items.append((n, v))
            cases.setdefault(p[1], []).append(('call', frame, p[2], p[3], p[4][4:], items, tail))
        elif t.startswith('log '):
            p = t.split(' ', 3)
            cases.setdefault(p[1], []).append(('log', frame, p[2], p[3]))
    return cases


def mm_summary(rows):
    out = []
    for r in rows:
        if r[0] == 'log':
            out.append(f'f{r[1]} LOG {r[2]} {r[3][:200]}')
        else:
            _, frame, who, ev, raw, items, tail = r
            out.append(f'f{frame} {who} {ev}' + (f'({raw})' if raw != ev else '') + ': ' + ' '.join(f'{n}={v}' for n, v in items) + f' [{tail}]')
    return out


# ----- order -----


def order_steps(entries):
    steps = {}
    for frame, t in entries:
        if t.startswith('ord '):
            p = t.split(' ')
            steps.setdefault(p[1], []).append((frame, p[2:]))
    return steps


def show(path, part=None):
    e = parse(path)
    if part in (None, 'size'):
        for _, t in e:
            if t.split(' ', 1)[0] in ('size', 'seq', 'style', 'skin'):
                print(t)
    if part in (None, 'ws'):
        for f, t in e:
            if t.startswith('ws ') or t.startswith('wsf '):
                print(f'f{f}', t)
    if part in (None, 'ord'):
        for step, rows in order_steps(e).items():
            print('==', step)
            for f, p in rows:
                print(f'  f{f}', ' '.join(p))
    if part in (None, 'mm'):
        for case, rows in mm_cases(e).items():
            print('==', case)
            for s in mm_summary(rows):
                print('  ', s)


# ================= rules =================


class Checker:
    def __init__(self):
        self.fails = []
        self.count = 0

    def check(self, ok, what):
        self.count += 1
        if not ok:
            self.fails.append(what)


WRAP = {'label', 'textArea'}  # the skin's word-wrapping styles
HEIGHT_UNROUNDED = {'label': 19.01, 'button': 20, 'toggle': 17, 'textField': 20, 'box': 22, 'textArea': 19.01}


def rule_sizes(c, s):
    """Entry heights, text heights, widths, gaps, margins and insets."""
    style = lambda n: s['style ' + n]
    pad = lambda n: [int(v) for v in style(n)['pad'].split(',')]
    for key, v in s.items():
        m = re.match(r'size (label|button|toggle|textField|textArea|box)\.(x|hello|2l|3l|empty|fs\d+|fs\d+\.2l|pad\.\w+|bold|rich)$', key)
        if not m or 'calc' not in v:
            continue
        kind = m.group(1)
        r = rect(v['r'])
        calc = rect(v['calc'])
        ch = float(v['ch'])
        want = round(ch) if kind in WRAP else math.ceil(calc[1] - 1e-4)
        c.check(r[3] == want, f'{key}: height {r[3]} != {"round(CalcHeight)" if kind in WRAP else "ceil(CalcSize.y)"} = {want}')
        c.check(r[2] == 400 and r[0] == 0 and r[1] == 0, f'{key}: rect {v["r"]} does not span the 400-wide area from (0,0)')
    # Text height: one line is fontSize + 0.01, each further line fontSize * 1.1; empty text is one line height.
    for fs in (8, 10, 12, 14, 16, 20, 30):
        one, two = s.get(f'size label.fs{fs}'), s.get(f'size label.fs{fs}.2l')
        if one and two:
            p = pad('label')
            c.check(abs(rect(one['calc'])[1] - p[2] - p[3] - (fs + 0.01)) < 0.02, f'label.fs{fs}: one line {one["calc"]} is not fs+0.01 plus padding')
            c.check(
                abs(rect(two['calc'])[1] - p[2] - p[3] - (fs + 0.01 + 1.1 * fs)) < 0.02,
                f'label.fs{fs}.2l: two lines {two["calc"]} is not fs+0.01+1.1*fs plus padding',
            )
    for kind in ('label', 'button', 'textField', 'box'):
        e = s.get(f'size {kind}.empty')
        if e:
            p = pad(kind)
            c.check(rect(e['calc'])[1] == float(style(kind)['lh']) + p[2] + p[3], f'{kind}.empty: height {e["calc"]} != lineHeight + vertical padding')
    for kind in ('label', 'button'):
        e = s.get(f'size {kind}.expandW.false')
        if e:
            c.check(rect(e['r'])[2] == math.ceil(rect(e['calc'])[0]), f'{kind}.expandW.false: width {e["r"]} != ceil(CalcSize.x)')
    # Gaps along a vertical group: the larger of the neighbours' margins.
    margins = {
        'label': (4, 4, 4, 4),
        'button': (4, 4, 4, 4),
        'toggle': (4, 4, 4, 4),
        'textField': (4, 4, 4, 4),
        'box': (4, 4, 4, 4),
        'textArea': (4, 4, 4, 4),
        'mA': (0, 0, 0, 10),
        'mB': (0, 0, 3, 0),
        'mC': (0, 0, 20, 2),
        'mD': (7, 3, 0, 0),
    }
    seqs = {
        'seq.labels': [('a', 'label'), ('b', 'label'), ('c', 'label')],
        'seq.buttons': [('a', 'button'), ('b', 'button'), ('c', 'button')],
        'seq.mixed': [
            ('label', 'label'),
            ('button', 'button'),
            ('label2', 'label'),
            ('toggle', 'toggle'),
            ('textField', 'textField'),
            ('box', 'box'),
            ('textArea', 'textArea'),
            ('label3', 'label'),
        ],
        'seq.margins': [('mA', 'mA'), ('mB', 'mB'), ('mC', 'mC'), ('mD', 'mD'), ('mA2', 'mA'), ('mC2', 'mC')],
    }
    for name, entries in seqs.items():
        v = s.get('seq ' + name)
        if not v:
            continue
        y = 0.0
        minleft = min(margins[k][0] for _, k in entries)
        minright = min(margins[k][1] for _, k in entries)
        for i, (n, k) in enumerate(entries):
            mg = margins[k]
            if i > 0:
                y += max(margins[entries[i - 1][1]][3], mg[2])
            r = rect(v[n])
            c.check(r[1] == round(y), f'{name}.{n}: y {r[1]} != {round(y)} (gap = larger margin, first margin absorbed)')
            c.check(
                r[0] == mg[0] - minleft and r[2] == 400 - (mg[0] - minleft) - (mg[1] - minright),
                f'{name}.{n}: x/width {v[n]} not inset by its margin less the smallest margin',
            )
            y += HEIGHT_UNROUNDED.get(k, 19.01)
    # Spaces: no gap before them; the next entry keeps its leading margin.
    v = s.get('seq seq.space')
    if v:
        c.check(rect(v['sp'])[1] == 19 and rect(v['b'])[1] == 33 and rect(v['sp0'])[1] == 52 and rect(v['c'])[1] == 56, f'seq.space: {v}')
    # A styled group or area: each child inset by max(padding, its margin).
    for name, padv, first in (('area.pad2', 2, 'a'), ('area.box', 4, 'a'), ('area.pad', 5, 'a'), ('area.window', 10, 'a')):
        v = s.get('seq ' + name)
        if v:
            c.check(rect(v[first])[0] == max(padv, 4), f'{name}: first x {v[first]} != max(padding, margin)')
    v = s.get('seq area.pad2.m8')
    if v:
        c.check(rect(v['a'])[:2] == (8, 8) and rect(v['b'])[0] == 4, f'area.pad2.m8: {v}')
    # Without a style: margins less the children's smallest; the first's leading margin is absorbed.
    v = s.get('seq area.m8')
    if v:
        c.check(rect(v['a'])[:3] == (4, 0, 392) and rect(v['b'])[:3] == (0, 27, 400), f'area.m8: {v}')
    v = s.get('seq grp.vertical.mixed')
    if v:
        c.check(
            rect(v['l0'])[0] == 4 and rect(v['ev']) == (0, 23, 400, 42) and rect(v['a'])[0] == 0 and rect(v['b'])[0] == 4 and rect(v['after'])[1] == 69,
            f'grp.vertical.mixed: {v}',
        )
    # Scroll views: the content keeps full margins; a vertical bar takes fixedWidth + its left margin.
    vb = style('verticalScrollbar')
    barw = float(vb['fw']) + int(vb['mar'].split(',')[0])
    v = s.get('seq scroll.large.h100')
    if v:
        c.check(rect(v['a0'])[:3] == (4, 4, 400 - barw - 8), f'scroll.large.h100: content {v["a0"]} != (4, 4, 400 - bar - margins)')
        c.check(rect(v['es']) == (0, 0, 400, 100), f'scroll.large.h100: view {v["es"]}')
    v = s.get('seq scroll.small.noopt')
    if v:
        c.check(rect(v['es'])[3] == 400 - 4 - 19, f'scroll.small.noopt: a scroll view without options fills the area: {v["es"]}')
    v = s.get('seq scroll.minmax')
    if v:
        c.check(rect(v['es'])[3] == 70, f'scroll.minmax: MinHeight 50 MaxHeight 70 takes 70: {v["es"]}')
    # The root group: as wide as its widest entry, from (0, 0).
    v = s.get('size root.label')
    if v:
        c.check(rect(v['root.label']) == (0, 0, 300, 19) and rect(v['root.w300'])[:3] == (0, 47, 300), f'root: {v}')
    # Windows: content at the window style's padding.
    w = [int(x) for x in style('window')['pad'].split(',')]
    for wid in ('611', '612', '614'):
        v = s.get('size win' + wid)
        if v:
            c.check(rect(v['first'])[:3] == (w[0], w[2], 300 - w[0] - w[1]), f'win{wid}: first {v["first"]} not at the window padding')


WS_OPTS = {
    706: {'w': 250},
    707: {'h': 250},
    708: {'minh': 250},
    710: {'maxh': 100},
    712: {'minw': 400},
    713: {'h': 100},
    714: {'maxh': 100},
    715: {'w': 100, 'h': 100},
}


def rule_ws(c, ws):
    for key, v in ws.items():
        if key[0] == 'f':
            continue
        case, wid, ev = key
        gin, gout = rect(v['in']), rect(v['out'])
        if ev == 'layout':
            c.check(gin == gout, f'ws {case} {wid}: Layout returns {v["out"]}, not the rect passed in {v["in"]}')
            continue
        n = int(v['n'])
        f = ws.get(('f', case, wid, ev))
        if not f:
            continue
        o = WS_OPTS.get(wid, {})
        c.check(gout[:2] == gin[:2], f'ws {case} {wid}: moved {v}')
        # Content height: padding 20 + 10, entries at their unrounded heights (one line 19.01,
        # each further 14.3), 4 between. Where the labels wrap depends on the font's widths: the
        # first one's rounded height gives the lines of labels 0 to 9, the last one's those of
        # labels 10 and up (a digit longer).
        heights = {19: 1, 33: 2, 48: 3, 62: 4}
        first_lines, last_lines = heights.get(int(rect(f['first'])[3])), heights.get(int(rect(f['last'])[3]))
        if first_lines and last_lines:
            content = 30 + sum(19.01 + ((first_lines if i < 10 else last_lines) - 1) * 14.3 for i in range(n)) + (n - 1) * 4
            h = gin[3]
            if 'h' in o:
                h = o['h']
            if 'minh' in o:
                h = max(h, o['minh'])
            if 'maxh' in o:
                h = min(h, o['maxh'])
            want = max(content, h)
            c.check(abs(gout[3] - want) < 0.01, f'ws {case} {wid}: height {gout[3]} != max(content {content:.2f}, given/options {h}) = {want:.2f}')
        w = gin[2]
        if 'w' in o:
            w = o['w']
        if 'minw' in o:
            w = max(w, o['minw'])
        if wid != 705:
            c.check(gout[2] == w, f'ws {case} {wid}: width {gout[2]} != {w}')


DECL = [400, 402, 401, 403, 404, 405, 406]


def rule_order(c, steps):
    prev = None
    pending = []
    for step, rows in steps.items():
        frames = {}
        calls = []
        for f, p in rows:
            if p[0] == 'call':
                calls.append((f, p[1], p[3]))
                continue
            if p[0].endswith('-end'):
                continue
            frames.setdefault(f, []).append((p[0], p[1].lower(), p[2][4:].lower() if len(p) > 2 else ''))
        for f in sorted(frames):
            seq = frames[f]
            # Events in order: split at each behaviour 'gui' line.
            events = []
            for who, ev, raw in seq:
                if not events or events[-1][-1][1] != ev:
                    events.append([])
                events[-1].append((who, ev))
            for evs in events:
                ev = evs[0][1]
                behs = [w for w, _ in evs if w.startswith('gui')]
                wins = [int(w[1:]) for w, _ in evs if w.startswith('w')]
                if ev == 'layout':
                    c.check(behs == ['gui', 'gui2'], f'{step} f{f}: Layout behaviours {behs}')
                    c.check([w for w, _ in evs][:2] == ['gui', 'gui2'], f'{step} f{f}: windows before behaviours in Layout')
                    want = None
                    if prev is not None:
                        new = [d for d in DECL if d in wins and d not in prev]
                        want = new + [w for w in prev if w in wins]
                        for cf, call, cev in pending + [x for x in calls if x[0] == f and x[2] == 'Layout']:
                            if call.startswith('front'):
                                i = int(call[5:8])
                                want = [i] + [w for w in want if w != i]
                            elif call.startswith('back'):
                                i = int(call[4:7])
                                want = [w for w in want if w != i] + [i]
                        pending = []
                        c.check(wins == want, f'{step} f{f}: window Layout order {wins} != {want}')
                    prev = wins
                    lay = wins
                elif ev == 'repaint':
                    c.check(behs == ['gui2', 'gui'], f'{step} f{f}: repaint behaviours {behs}')
                    c.check(wins == list(reversed(lay)), f'{step} f{f}: repaint windows {wins} not the Layout order reversed {lay}')
                    pending += [x for x in calls if x[0] == f and x[2] == 'repaint']
                elif ev in ('mousedown', 'mouseup'):
                    # The front window under the mouse first, then the behaviours, the event not used.
                    c.check(len(wins) <= 1, f'{step} f{f}: {ev} reached windows {wins}')
                    c.check(behs == ['gui', 'gui2'] and all(e == ev for _, e in evs), f'{step} f{f}: {ev} after the window: {evs}')
                    if wins and ev == 'mousedown':
                        i = wins[0]
                        prev = [i] + [w for w in prev if w != i]


def mm_steady(rows, who):
    for r in rows:
        if r[0] == 'call' and r[2] == who and r[3].lower() == 'repaint' and not any(n == 'alt' for n, _ in r[5]):
            first = {}
            for n, v in r[5]:
                first.setdefault(n, v)  # the first of names that repeat (ESV in a scroll view in a scroll view)
            return first
    return None


EXPECT = {
    # body: (Repaint outcome, input-event outcome) per call after the change, in terms of the steady repaint rects
    'insert': [('X', 'L1'), ('L1', 'L2'), ('L2', 'over:3')],
    'append': [('X', 'over:3')],
    'spaceInsert': [('S', 'L1'), ('L1', 'L2'), ('L2', 'over:3')],
    'flexInsert': [('FS', 'L1'), ('L1', 'L2'), ('L2', 'over:3')],
    'remove': [('L2', 'L1')],
    'removeEnd': [],
    'leafToGroup': [('BH', 'mismatch')],
    'leafToScroll': [('BSV', 'mismatch')],
    'groupToLeaf': [('L1', 'EH'), ('L2', 'L2')],
    'scrollToLeaf': [('L1', 'ESV'), ('L2', 'L2')],
    'vertToHoriz': [('a', 'a'), ('b', 'b'), ('EH', 'EV'), ('L2', 'L2')],
    'groupMore': [('X', 'b'), ('b', 'over:2')],
    'groupFewer': [('EV', 'EV'), ('L2', 'L2')],
    'content': [('C', 'C'), ('L2', 'L2')],
    'getRect': [('G', 'G'), ('L2', 'L2')],
    'areaInsert': [('BA', 'over:0')],
}


def rule_mm(c, cases):
    for case, rows in cases.items():
        if case.count('.') != 3:
            continue  # log lines outside the mismatch part
        body, ctx, target, mode = case.split('.')
        who = {'root': 'gui', 'rootscroll': 'gui', 'win': 'w501', 'winscroll': 'w501', 'lwin': 'w502'}[ctx]
        calls = [r for r in rows if r[0] == 'call']
        logs = [r for r in rows if r[0] == 'log' and r[2] == 'Exception']
        if body in EXPECT:
            steady = mm_steady(rows, who)
            alt = [r for r in calls if r[2] == who and any(n == 'alt' for n, _ in r[5])]
            c.check(len(alt) == 1, f'{case}: {len(alt)} changed events')
            if not alt or not steady:
                continue
            r = alt[0]
            items = r[5]
            names = [n for n, _ in items]
            after = dict(items[names.index('alt') + 1 :])
            ev = r[3]
            if target == 'Used':
                bad = [f'{n}={v}' for n, v in after.items() if v not in (DUMMY, 'ok')]
                c.check(not bad, f'{case}: during Used, calls returned {bad} instead of {DUMMY}')
                c.check(r[6] == 'end' and not logs, f'{case}: Used event aborted or logged')
                continue
            threw = None
            for n, ref in EXPECT[body]:
                got = after.get(n)
                if ref.startswith('over:'):
                    k = ref[5:]
                    if ev.lower() == 'repaint':
                        want = f"!ArgumentException:Getting control {k}'s position in a group with only {k} controls when doing {ev}\\nAborting"
                        c.check(got == want, f'{case}: {n} got {got}, want {want}')
                        threw = 'arg'
                    else:
                        prevn = names[names.index(n) - 1]
                        prevv = dict(items).get(prevn) if prevn != 'alt' else steady.get('L2')
                        c.check(got == prevv, f'{case}: {n} past the last entry in {ev} got {got}, want the last entry again {prevv}')
                    if threw:
                        break
                elif ref == 'mismatch':
                    want = '!ExitGUIException:GUILayout: Mismatched LayoutGroup.' + ev
                    c.check(got == want, f'{case}: {n} got {got}, want {want}')
                    threw = 'exit'
                    break
                else:
                    want = steady.get(ref)
                    if n in ('BH', 'BV', 'BSV', 'BA'):
                        want = 'ok'
                    c.check(got == want, f'{case}: {n} got {got}, want {ref}\'s rect {want}')
            if mode == 'u':
                c.check(r[6] == ('abort' if threw else 'end'), f'{case}: ended {r[6]}, threw {threw}')
                c.check(
                    len(logs) == (1 if threw == 'arg' else 0),
                    f'{case}: {len(logs)} exceptions logged (an ArgumentException is logged, ExitGUIException is not)',
                )
                # The calls after it run, and later frames are as before.
                later = [x for x in calls if x[1] > r[1] and x[2] == who and x[3].lower() == 'repaint']
                c.check(later and all(x[5] == later[0][5] for x in later) and dict(later[0][5]).get('L0') == steady.get('L0'), f'{case}: later frames differ')
                same = [x for x in calls if x[1] == r[1] and x[2] != who and x[3] == r[3]]
                c.check(same, f'{case}: no other call ran in that event')
        elif body.startswith('handler') or body == 'mod':
            # After a button returns true (it uses the event), every call gets the dummy rect, and nothing throws.
            bad = []
            for x in calls:
                items = x[5]
                hit = [i for i, (n, v) in enumerate(items) if v.endswith('[Used]')]
                if hit:
                    for n, v in items[hit[0] + 1 :]:
                        if v not in (DUMMY, 'ok', '') and not n.endswith('.clicked'):
                            bad.append(f'f{x[1]} {n}={v}')
                if any(v.startswith('!') for _, v in items) or x[6] != 'end':
                    bad.append(f'f{x[1]} {x[2]} {x[3]} threw or aborted')
            c.check(not bad and not logs, f'{case}: {bad[:3]} {len(logs)} logged')
            clicked = [x for x in calls if any(n.endswith('.clicked') for n, _ in x[5])]
            if not (body == 'mod' and ctx == 'winscroll'):  # there the press lands on the outer scroll view, which takes it
                c.check(clicked, f'{case}: the button was never clicked')


def rules(path):
    e = parse(path)
    c = Checker()
    s = sizes(e)
    if s:
        rule_sizes(c, s)
    ws = ws_lines(e)
    if ws:
        rule_ws(c, ws)
    st = order_steps(e)
    if st:
        rule_order(c, st)
    mm = mm_cases(e)
    if mm:
        rule_mm(c, mm)
    for f in c.fails:
        print('FAIL', f)
    print(f'{c.count - len(c.fails)}/{c.count} checks hold ({len(s)} size lines, {len(ws)} window lines, {len(st)} order steps, {len(mm)} mismatch cases)')
    return 0 if not c.fails and c.count else 1


# ================= diff =================


def diff(orig, ours):
    a, b = parse(orig), parse(ours)
    n = 0
    sa, sb = sizes(a), sizes(b)
    for k in sa:
        if k.startswith('style') or k == 'skin':
            if sa[k] != sb.get(k):
                n += 1
                print('STYLE', k, '\n  orig', sa[k], '\n  ours', sb.get(k))
            continue
        va, vb = sa[k], sb.get(k)
        if vb is None:
            n += 1
            print('SIZE', k, 'missing in ours')
            continue
        da = {x: norm(y) for x, y in va.items() if x not in ('calc', 'ch', 'mm')}
        db = {x: norm(y) for x, y in vb.items() if x not in ('calc', 'ch', 'mm')}
        if da != db:
            n += 1
            print('SIZE', k)
            for x in da:
                if da[x] != db.get(x):
                    print(f'   {x}: orig {da[x]}  ours {db.get(x)}')
    wa, wb = ws_lines(a), ws_lines(b)

    def nv(v):
        return {x: norm(y) for x, y in v.items()} if v is not None else None

    for k in wa:
        if k[0] != 'f' and nv(wa[k]) != nv(wb.get(k)):
            n += 1
            print('WS', k, 'orig', wa[k].get('out'), 'ours', (wb.get(k) or {}).get('out'))
    oa, ob = order_steps(a), order_steps(b)
    for k in oa:

        def seq(rows):
            return [' '.join(p[:2]).lower() for _, p in rows if not p[0].endswith('-end')]

        if seq(oa[k]) != seq(ob.get(k, [])):
            n += 1
            print('ORDER', k)
            print('   orig', ' | '.join(seq(oa[k]))[:600])
            print('   ours', ' | '.join(seq(ob.get(k, [])))[:600])
    ma, mb = mm_cases(a), mm_cases(b)
    for k in ma:

        def key(rows):
            out = []
            for r in rows:
                if r[0] == 'call':
                    out.append(f'{r[2]} {r[3]}: ' + ' '.join(f'{x}={norm(y)}' for x, y in r[5]) + f' [{r[6]}]')
                else:
                    out.append(f'LOG {r[2]} {r[3][:120]}')
            # frames differ between players; compare the distinct lines in order of first appearance
            seen, res = set(), []
            for o in out:
                if o not in seen:
                    seen.add(o)
                    res.append(o)
            return res

        ka, kb = key(ma[k]), key(mb.get(k, []))
        if ka != kb:
            n += 1
            print('MM', k)
            for x in ka:
                if x not in kb:
                    print('   orig', x[:400])
            for x in kb:
                if x not in ka:
                    print('   ours', x[:400])
    print(f'{n} differences')
    return 0 if n == 0 else 1


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == 'show':
        show(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
        return 0
    if len(sys.argv) == 3 and sys.argv[1] == 'rule':
        return rules(sys.argv[2])
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        return diff(sys.argv[2], sys.argv[3])
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main())
