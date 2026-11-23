"""tools/PixelDiff/pixel_diff.py: the stable mask (--stable) and its growth (--grow), and the
fractions over each level, as the D3D11 scene set (tools/PixelDiff/scenes.py) uses them."""

import os, sys, tempfile, unittest

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'tools', 'PixelDiff'))
import pixel_diff  # noqa: E402


def png(path, boxes=(), size=(40, 20)):
    a = np.zeros((size[1], size[0], 4), dtype='uint8')
    a[..., 3] = 255
    for x, y, w, h, value in boxes:
        a[y : y + h, x : x + w, :3] = value
    Image.fromarray(a).save(path)
    return path


class PixelDiff(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.temp.cleanup()

    def path(self, name):
        return os.path.join(self.temp.name, name)

    def test_levels(self):
        a = png(self.path('a.png'))
        b = png(self.path('b.png'), [(0, 0, 4, 2, 10), (10, 10, 2, 2, 50)])
        r, _ = pixel_diff.compare(a, b)
        self.assertEqual(r['pixels'], 800)
        self.assertAlmostEqual(r['fraction_over']['8'], 12 / 800, places=6)
        self.assertAlmostEqual(r['fraction_over']['40'], 4 / 800, places=6)
        self.assertEqual(r['max']['R'], 50)

    def test_stable_masks_what_differs_between_runs(self):
        a = png(self.path('a.png'))
        c = png(self.path('c.png'), [(10, 10, 2, 2, 90)])
        b = png(self.path('b.png'), [(10, 10, 3, 2, 50)])
        r, _ = pixel_diff.compare(a, b, stable=[c])
        self.assertEqual(r['masked'], 4)
        # The column next to the unstable box differs only by chance in a: still counted.
        self.assertAlmostEqual(r['fraction_over']['40'], 2 / 796, places=6)
        grown, _ = pixel_diff.compare(a, b, stable=[c], grow_by=1)
        # 2x2 grown by 1 on every side: 4x4.
        self.assertEqual(grown['masked'], 16)
        self.assertEqual(grown['fraction_over']['0'], 0)

    def test_grow_at_the_edges(self):
        unstable = np.zeros((5, 5), dtype=bool)
        unstable[0, 0] = True
        grown = pixel_diff.grow(unstable, 2)
        self.assertEqual(int(grown.sum()), 9)
        self.assertTrue(grown[2, 2])
        self.assertFalse(grown[3, 0])


if __name__ == '__main__':
    unittest.main()
