#!/usr/bin/env python3
"""qa-sheet.py RUN_DIR OUT_DIR — one contact sheet per dogfood scenario (its captures in frame
order, labelled), for looking through a QA run's captures."""

import os, re, sys
from PIL import Image, ImageDraw

run, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
for name in sorted(os.listdir(os.path.join(run, 'dogfood'))):
    d = os.path.join(run, 'dogfood', name)
    shots = sorted((int(m.group(1)), f) for f in os.listdir(d) if (m := re.fullmatch(r'f(\d+)\.png', f)))
    if not shots:
        continue
    tiles = []
    for frame, f in shots:
        im = Image.open(os.path.join(d, f)).convert('RGB')
        im.thumbnail((512, 384))
        ImageDraw.Draw(im).text((6, 4), f'{name} f{frame}', fill=(255, 255, 0))
        tiles.append(im)
    cols = 2 if len(tiles) <= 4 else 3
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * 512, rows * 384), (20, 20, 20))
    for i, t in enumerate(tiles):
        sheet.paste(t, ((i % cols) * 512, (i // cols) * 384))
    sheet.save(os.path.join(out, f'{name}.png'))
    print(name, len(tiles))
