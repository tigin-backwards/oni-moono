#!/usr/bin/env python3
"""Pixel comparison of two images of the same frame from two renderers (the Direct3D 11 device
against the OpenGL device: docs/research/d3d11-plan.md, M2 and done criterion 3).

    pixel_diff.py REFERENCE NEW [--stable AGAIN.png ... [--grow N]] [--diff DIFF.png] [--mask X,Y,W,H ...] [--json OUT.json]

--stable: another image of the frame from the reference's renderer (another run); the pixels
where its RGB differs from REFERENCE's differ between runs whatever the renderer (an animation
not in step), and are masked as well; --grow N masks the pixels within N of those too (content
that changes between runs matches in two runs here and there by chance: the random dig cracks
of a colony's tiles, a status icon's pulse). RGB, as the fractions and PSNR below: alpha is reported
per channel only (the back buffer's alpha is not shown).

For the unmasked pixels it prints, and writes as JSON:
- the size of each image (a difference fails at once);
- per channel (R, G, B, A) the largest absolute difference and the mean one;
- the fraction of pixels whose largest channel difference exceeds 0, 1, 2, 4, 8, 16 and 40 (40 is
  the e2e `shot` level: scripts/qa-tools.py compare_images);
- PSNR over RGB;
- the bounding box of the pixels differing by more than 8.
DIFF gets an image of the largest channel difference per pixel, scaled by 4 (clipped at 255),
masked pixels mid grey, so small differences show."""

import argparse
import json
import sys

import numpy as np
from PIL import Image

LEVELS = (0, 1, 2, 4, 8, 16, 40)


def rect(text):
    x, y, w, h = (int(round(float(v))) for v in text.split(','))
    return x, y, w, h


def grow(unstable, radius):
    """The pixels within `radius` (a square, Chebyshev distance) of an unstable one."""
    out = unstable.copy()
    h, w = unstable.shape
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            out[max(0, dy) : h + min(0, dy), max(0, dx) : w + min(0, dx)] |= unstable[max(0, -dy) : h + min(0, -dy), max(0, -dx) : w + min(0, -dx)]
    return out


def compare(reference, new, masks=(), stable=(), grow_by=0, stable_pairs=()):
    a = np.asarray(Image.open(reference).convert('RGBA'), dtype=np.int16)
    b = np.asarray(Image.open(new).convert('RGBA'), dtype=np.int16)
    result = {'reference': reference, 'new': new, 'size': [a.shape[1], a.shape[0]], 'new_size': [b.shape[1], b.shape[0]]}
    if a.shape != b.shape:
        result['note'] = 'sizes differ'
        return result, None
    counted = np.ones(a.shape[:2], dtype=bool)
    for x, y, w, h in masks:
        counted[max(0, y) : max(0, y + h), max(0, x) : max(0, x + w)] = False
    for other in stable:
        c = np.asarray(Image.open(other).convert('RGBA'), dtype=np.int16)
        if c.shape != a.shape:
            result['note'] = f'{other}: size differs'
            return result, None
        unstable = np.abs(a - c)[..., :3].max(axis=2) != 0
        counted &= ~(grow(unstable, grow_by) if grow_by else unstable)
    # Two runs of another renderer: the pixels where they differ (and those within grow_by).
    for first, second in stable_pairs:
        x = np.asarray(Image.open(first).convert('RGBA'), dtype=np.int16)
        y = np.asarray(Image.open(second).convert('RGBA'), dtype=np.int16)
        if x.shape != a.shape or y.shape != a.shape:
            result['note'] = f'{first} or {second}: size differs'
            return result, None
        unstable = np.abs(x - y)[..., :3].max(axis=2) != 0
        counted &= ~(grow(unstable, grow_by) if grow_by else unstable)
    d = np.abs(a - b)
    largest = d[..., :3].max(axis=2)
    n = int(counted.sum())
    result['pixels'] = n
    result['masked'] = int(counted.size - n)
    sel = d[counted]
    result['max'] = {c: int(sel[:, i].max()) if n else 0 for i, c in enumerate('RGBA')}
    result['mean'] = {c: round(float(sel[:, i].mean()), 4) if n else 0.0 for i, c in enumerate('RGBA')}
    result['fraction_over'] = {str(level): round(float((largest[counted] > level).sum()) / n, 6) if n else 0.0 for level in LEVELS}
    mse = float((sel[:, :3].astype(np.float64) ** 2).mean()) if n else 0.0
    result['psnr_rgb'] = None if mse == 0 else round(10 * np.log10(255.0**2 / mse), 2)
    ys, xs = np.nonzero((largest > 8) & counted)
    result['box_over_8'] = None if len(xs) == 0 else [int(xs.min()), int(ys.min()), int(xs.max() - xs.min() + 1), int(ys.max() - ys.min() + 1)]
    image = np.where(counted, np.clip(largest * 4, 0, 255), 128).astype('uint8')
    return result, image


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument('reference')
    p.add_argument('new')
    p.add_argument('--diff')
    p.add_argument('--mask', action='append', default=[], type=rect)
    p.add_argument('--stable', action='append', default=[])
    p.add_argument('--grow', type=int, default=0)
    p.add_argument('--json')
    args = p.parse_args()
    result, image = compare(args.reference, args.new, args.mask, args.stable, args.grow)
    if image is not None and args.diff:
        Image.fromarray(image).save(args.diff, format='PNG')
    text = json.dumps(result, indent=1)
    if args.json:
        with open(args.json, 'w') as f:
            f.write(text + '\n')
    print(text)
    return 1 if 'note' in result else 0


if __name__ == '__main__':
    sys.exit(main())
