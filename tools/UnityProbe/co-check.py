#!/usr/bin/env python3
"""co-check.py: the draw order of the canvasorder probe's canvases, from its screenshots.

  co-check.py DIR [DIR2]    one line per co-*.png: the probe canvases bottom to top
                            (with DIR2, the two players side by side, '!' where they differ)

Canvas i draws row i and column i in its own colour (CanvasOrder.cs); at the crossing of row i
and column j the pixel shows whichever of canvases i and j is on top. The order is built from
those pairs; '?' marks a pair that shows neither colour (a canvas not drawn)."""

import glob, os, sys
from PIL import Image

ORIGIN, PITCH, WIDTH = 100, 30, 16
COLOURS = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255), (0, 255, 255)]
NAMES = 'ABCDEF'


def which(px):
    for i, c in enumerate(COLOURS):
        if all(abs(a - b) < 40 for a, b in zip(px, c)):
            return i
    return None


def order(path):
    img = Image.open(path).convert('RGB')
    h = img.height
    # Which canvases draw at all: the middle of each one's row, left of every column.
    present = [i for i in range(len(COLOURS)) if which(img.getpixel((ORIGIN - 10, h - 1 - (ORIGIN + PITCH * i + WIDTH // 2)))) == i]
    above = {i: set() for i in present}  # above[i]: the canvases i is drawn over
    odd = []
    for i in present:
        for j in present:
            if i == j:
                continue
            x = ORIGIN + PITCH * j + WIDTH // 2
            y = h - 1 - (ORIGIN + PITCH * i + WIDTH // 2)
            top = which(img.getpixel((x, y)))
            if top == i:
                above[i].add(j)
            elif top == j:
                above[j].add(i)
            else:
                odd.append(f'{NAMES[i]}{NAMES[j]}?')
    ranked = sorted(present, key=lambda i: len(above[i]))
    return ','.join(NAMES[i] for i in ranked) + (' ' + ' '.join(odd) if odd else '')


def main():
    dirs = sys.argv[1:]
    if not dirs:
        print(__doc__)
        return 2
    names = sorted({os.path.basename(p) for d in dirs for p in glob.glob(os.path.join(d, 'co-*.png'))})
    for n in names:
        cols = []
        for d in dirs:
            p = os.path.join(d, n)
            cols.append(order(p) if os.path.exists(p) else '-')
        mark = ' !' if len(set(cols)) > 1 else ''
        print(f'{n[:-4]:52} ' + '   '.join(f'{c:14}' for c in cols) + mark)
    return 0


if __name__ == '__main__':
    sys.exit(main())
