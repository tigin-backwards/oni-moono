#!/usr/bin/env python3
"""jpg-check.py: the jpg probe's files (JpgEncode.cs) from two players, decoded and compared.

  jpg-check.py ORIG_DIR OURS_DIR    one line per case: each player's JPEG against the original's
                                    PNG of the texture (mean and maximum channel error, RGB),
                                    the two JPEGs against each other, whether the TGAs are equal
                                    byte for byte and whether the PNGs decode equal

The original's PNG is lossless, so its texels are the reference; both JPEGs carry JPEG error
against it, of about the same size when the encoders agree on orientation, channels and alpha."""

import os, sys
from PIL import Image


def pixels(path, mode):
    im = Image.open(path).convert(mode)
    return im.size, list(im.get_flattened_data() if hasattr(im, "get_flattened_data") else im.getdata())


def error(a, b):
    if a[0] != b[0]:
        return f'size {a[0]} vs {b[0]}'
    diffs = [abs(x - y) for p, q in zip(a[1], b[1]) for x, y in zip(p, q)]
    return f'mean {sum(diffs) / len(diffs):5.2f} max {max(diffs):3d}'


def main(orig, ours):
    for name in sorted(os.listdir(orig)):
        if not name.endswith('.png'):
            continue
        case = name[:-4]
        ref = pixels(os.path.join(orig, name), 'RGB')
        line = [f'{case:14}']
        jo, ju = (os.path.join(d, case + '.jpg') for d in (orig, ours))
        if os.path.exists(jo) and os.path.exists(ju):
            a, b = pixels(jo, 'RGB'), pixels(ju, 'RGB')
            line.append(f'orig {error(a, ref)} | ours {error(b, ref)} | orig-ours {error(a, b)}')
        elif os.path.exists(jo) != os.path.exists(ju):
            line.append('jpg only in ' + ('orig' if os.path.exists(jo) else 'ours'))
        to, tu = (os.path.join(d, case + '.tga') for d in (orig, ours))
        if os.path.exists(to) or os.path.exists(tu):
            same = os.path.exists(to) and os.path.exists(tu) and open(to, 'rb').read() == open(tu, 'rb').read()
            line.append('tga ' + ('equal' if same else 'DIFFER'))
        pu = os.path.join(ours, name)
        if os.path.exists(pu):
            same = pixels(os.path.join(orig, name), 'RGBA') == pixels(pu, 'RGBA')
            line.append('png ' + ('equal' if same else 'DIFFER'))
        print(' | '.join(line))


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
