#!/usr/bin/env python3
"""do-check.py: which of each draworder case's two quads drew on top (DrawOrder.cs).

  do-check.py LOG DIR [LOG2 DIR2]   one line per case: P (red) or Q (green) on top; with a
                                    second player's log and captures, '!' where they differ

The case lines in the log give the overlap's pixel (from the bottom left); DIR holds
do-cases.png."""

import os, re, sys
from PIL import Image


def read(log, folder):
    img = Image.open(os.path.join(folder, 'do-cases.png')).convert('RGB')
    out = {}
    for line in open(log, encoding='utf-8', errors='replace'):
        m = re.search(r'DO case (\d+) (\S+) .* pixel (-?\d+),(-?\d+)', line)
        if not m:
            continue
        x, y = int(m.group(3)), img.height - 1 - int(m.group(4))
        r, g, b = img.getpixel((x, y))
        top = 'P' if r > 200 and g < 80 else 'Q' if g > 200 and r < 80 else f'?{r},{g},{b}'
        out[m.group(2)] = top
    return out


def main():
    a = sys.argv[1:]
    if len(a) not in (2, 4):
        print(__doc__)
        return 2
    runs = [read(a[i], a[i + 1]) for i in range(0, len(a), 2)]
    for name in runs[0]:
        tops = [r.get(name, '-') for r in runs]
        print(f'{name:48} ' + '  '.join(tops) + (' !' if len(set(tops)) > 1 else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
