#!/usr/bin/env python3
"""Draws a performance document's "Where a frame goes" table as a flame graph (SVG).
  scripts/flamegraph.py FPS [DOC OUT COLONY]
      defaults: docs/PERFORMANCE.md, docs/media/frame-flamegraph.svg, U57
Rows are read from the table: `·` prefixes give the nesting, the second column the milliseconds.
The top bar is the whole frame at FPS; what the listed entry points leave of it is drawn as the
rest of the frame, and each bar's own time is what its children leave of it."""

import html, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(ROOT, 'docs', 'PERFORMANCE.md')
OUT = os.path.join(ROOT, 'docs', 'media', 'frame-flamegraph.svg')
COLONY = 'U57'

WIDTH, ROW, PAD, TOP = 1000, 26, 1, 46
# Warm flame colours by depth, light enough for dark text.
COLOURS = ['#f2a65a', '#f4b86e', '#f6c983', '#f8d898', '#fae5ad']
REST = '#d9d2c5'


def rows():
    text = open(DOC, encoding='utf-8').read()
    section = text.split('## Where a frame goes', 1)[1]
    found = []
    for line in section.splitlines():
        m = re.match(r'\|\s*((?:·\s*)*)(.+?)\s*\|\s*([\d.]+)\s*\|', line)
        if m:
            found.append((m.group(1).count('·'), m.group(2).replace('`', ''), float(m.group(3))))
        elif found and not line.startswith('|'):
            break
    return found


def tree(flat):
    root = {'name': '', 'ms': 0.0, 'children': []}
    stack = [(-1, root)]
    for depth, name, ms in flat:
        while stack[-1][0] >= depth:
            stack.pop()
        node = {'name': name, 'ms': ms, 'children': []}
        stack[-1][1]['children'].append(node)
        stack.append((depth, node))
    return root


def svg(root, fps):
    frame = 1000.0 / fps
    listed = sum(c['ms'] for c in root['children'])
    root['name'] = f'Main thread, one frame at {fps:g} fps'
    root['ms'] = frame
    root['children'].append({'name': 'rest of the frame', 'ms': max(0.0, frame - listed), 'children': [], 'rest': True})
    scale = (WIDTH - 2) / frame
    depth = 0

    def measure(n, d):
        nonlocal depth
        depth = max(depth, d)
        for c in n['children']:
            measure(c, d + 1)

    measure(root, 0)
    height = TOP + (depth + 1) * ROW + 30
    out = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{height}" viewBox="0 0 {WIDTH} {height}" font-family="Segoe UI, Helvetica, Arial, sans-serif">',
        f'<rect width="{WIDTH}" height="{height}" fill="#fffdf8"/>',
        f'<text x="{WIDTH / 2}" y="22" text-anchor="middle" font-size="16" font-weight="600" fill="#2b2b2b">Where a frame goes ({COLONY}, every tweak, about {fps:g} fps)</text>',
        f'<text x="{WIDTH / 2}" y="38" text-anchor="middle" font-size="11" fill="#666">bar width = milliseconds of main-thread time a frame; a bar under another is part of it</text>',
    ]

    keyed = []

    def draw(n, x, d):
        w = n['ms'] * scale
        y = TOP + d * ROW
        colour = REST if n.get('rest') else ('#e8875a' if d == 0 else COLOURS[min(d - 1, len(COLOURS) - 1)])
        name = re.sub(r'\s*\([^)]*\)', '', n['name'])
        label = f"{name} {n['ms']:.1f} ms"
        out.append(
            f'<g><title>{html.escape(n["name"])} {n["ms"]:.1f} ms</title>'
            f'<rect x="{x + PAD:.1f}" y="{y}" width="{max(0.0, w - 2 * PAD):.1f}" height="{ROW - 2}" rx="3" fill="{colour}" stroke="#ffffff" stroke-width="0.5"/>'
        )
        # The label when it fits (about 6.6 px a character at 12 px), else a number in the key.
        if len(label) * 6.6 + 10 <= w:
            out.append(f'<text x="{x + 6:.1f}" y="{y + ROW / 2 + 3}" font-size="12" fill="#2b2b2b">{html.escape(label)}</text>')
        else:
            keyed.append((colour, n['name'], n['ms']))
            out.append(
                f'<text x="{x + w / 2:.1f}" y="{y + ROW / 2 + 3}" text-anchor="middle" font-size="11" font-weight="600" fill="#2b2b2b">{len(keyed)}</text>'
            )
        out.append('</g>')
        cx = x
        for c in n['children']:
            draw(c, cx, d + 1)
            cx += c['ms'] * scale

    draw(root, 1, 0)
    # The key for bars too narrow to name, in two columns.
    key_top = TOP + (depth + 1) * ROW + 10
    for i, (colour, name, ms) in enumerate(keyed):
        kx = 8 + (i % 2) * (WIDTH / 2)
        ky = key_top + (i // 2) * 20
        out.append(
            f'<rect x="{kx:.1f}" y="{ky}" width="16" height="14" rx="2" fill="{colour}"/>'
            f'<text x="{kx + 8:.1f}" y="{ky + 11}" text-anchor="middle" font-size="10" font-weight="600" fill="#2b2b2b">{i + 1}</text>'
            f'<text x="{kx + 24:.1f}" y="{ky + 11}" font-size="12" fill="#2b2b2b">{html.escape(name)} <tspan fill="#666">{ms:.1f} ms</tspan></text>'
        )
    height = key_top + ((len(keyed) + 1) // 2) * 20 + 22
    out[0] = re.sub(r'height="\d+" viewBox="0 0 (\d+) \d+"', lambda m: f'height="{height}" viewBox="0 0 {m.group(1)} {height}"', out[0])
    out[1] = f'<rect width="{WIDTH}" height="{height}" fill="#fffdf8"/>'
    out.append(
        f'<text x="{WIDTH - 6}" y="{height - 8}" text-anchor="end" font-size="10" fill="#999">scripts/flamegraph.py from {html.escape(os.path.relpath(DOC, ROOT))}</text>'
    )
    out.append('</svg>')
    return '\n'.join(out) + '\n'


if __name__ == '__main__':
    fps = float(sys.argv[1])
    if len(sys.argv) > 4:
        DOC, OUT, COLONY = os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3]), sys.argv[4]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, 'w', encoding='utf-8').write(svg(tree(rows()), fps))
    print('wrote', OUT)
