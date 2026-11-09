#!/usr/bin/env python3
"""Luminance of FP16 monitor captures (dupcap's PFM files: linear scRGB, 1.0 = 80 nits).

    summary.py A.pfm [B.pfm ...]

For each: the median luminance of the frame (the background) and the mean of its brightest 0.5%
of pixels (the white title and the button text of the main menu), in nits. With HDR on, Windows
shows SDR white at the "SDR content brightness" setting; a player that Auto HDR or RTX HDR takes
for HDR content shows its white UI much brighter than the others.
"""

import sys
import numpy as np


def read_pfm(path):
    with open(path, 'rb') as f:
        assert f.readline().strip() == b'PF', f'{path}: not a colour PFM'
        w, h = map(int, f.readline().split())
        scale = float(f.readline())
        data = np.fromfile(f, dtype='<f4' if scale < 0 else '>f4', count=w * h * 3)
    return data.reshape(h, w, 3)[::-1]


for path in sys.argv[1:]:
    rgb = read_pfm(path)
    nits = 80.0 * (0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2])
    flat = np.sort(nits.ravel())
    top = flat[int(len(flat) * 0.995) :]
    print(f'{path.rsplit("/", 1)[-1]}: median {np.median(flat):.1f} nits, brightest 0.5% {top.mean():.1f} nits, max {flat[-1]:.1f} nits')
