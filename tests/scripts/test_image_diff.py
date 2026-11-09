"""qa-tools.py image's crop, mask and size tolerance; ui-diff.py's --tolerance and --mask-text."""

import os, subprocess, sys, tempfile, unittest

import helpers
import numpy as np
from PIL import Image

qa_tools = helpers.script('qa-tools.py')
ui_diff = helpers.script('ui-diff.py')


def png(path, size=(100, 50), fill=(0, 0, 0), boxes=()):
    a = np.zeros((size[1], size[0], 3), dtype='uint8')
    a[:, :] = fill
    for x, y, w, h, colour in boxes:
        a[y : y + h, x : x + w] = colour
    Image.fromarray(a).save(path)
    return path


class Images(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = self.temp.name

    def tearDown(self):
        self.temp.cleanup()

    def path(self, name):
        return os.path.join(self.dir, name)

    def test_fraction_and_level(self):
        a = png(self.path('a.png'))
        b = png(self.path('b.png'), boxes=[(0, 0, 10, 10, (255, 255, 255)), (50, 0, 10, 10, (30, 30, 30))])
        result = qa_tools.compare_images(a, b, self.path('d.png'))
        self.assertAlmostEqual(result['fraction'], 100 / 5000)  # the faint box is under the level
        diff = np.asarray(Image.open(self.path('d.png')))
        self.assertEqual((diff[5, 5], diff[5, 55]), (255, 0))

    def test_crop_each_image_and_masks_relative_to_it(self):
        # The same 40x20 panel, at (10, 10) in the reference and at (30, 20) in the new one; a
        # clock in its corner differs.
        a = png(self.path('a.png'), boxes=[(10, 10, 40, 20, (200, 0, 0)), (12, 12, 5, 5, (0, 0, 255))])
        b = png(self.path('b.png'), boxes=[(30, 20, 40, 20, (200, 0, 0)), (32, 22, 5, 5, (0, 255, 0))])
        unmasked = qa_tools.compare_images(a, b, None, crop=[10, 10, 40, 20], crop_new=[30, 20, 40, 20])
        self.assertAlmostEqual(unmasked['fraction'], 25 / 800)
        masked = qa_tools.compare_images(a, b, None, crop=[10, 10, 40, 20], crop_new=[30, 20, 40, 20], masks=[[2, 2, 5, 5]])
        self.assertEqual((masked['fraction'], masked['masked'], masked['size']), (0.0, 25, [40, 20]))

    def test_size_tolerance(self):
        a = png(self.path('a.png'), size=(100, 50))
        b = png(self.path('b.png'), size=(101, 50))
        self.assertEqual(qa_tools.compare_images(a, b, None)['fraction'], 0.0)
        c = png(self.path('c.png'), size=(110, 50))
        self.assertEqual(qa_tools.compare_images(a, c, None), {'fraction': 1.0, 'note': 'size (100, 50) against (110, 50)'})
        self.assertEqual(qa_tools.compare_images(a, self.path('none.png'), None)['note'], 'missing')
        self.assertIn('off the image', qa_tools.compare_images(a, a, None, crop=[200, 200, 5, 5])['note'])

    def test_command_line(self):
        a = png(self.path('a.png'), boxes=[(0, 0, 10, 10, (255, 255, 255))])
        b = png(self.path('b.png'))
        tool = os.path.join(helpers.SCRIPTS, 'qa-tools.py')
        run = lambda *args: subprocess.run([sys.executable, tool, 'image', a, b, self.path('d.png'), *args], capture_output=True, text=True).stdout.strip()
        self.assertEqual(run(), '0.0200')
        self.assertEqual(run('--mask', '0,0,10,10'), '0.0000')
        self.assertEqual(run('--crop', '0,0,20,10'), '0.5000')
        self.assertEqual(run('--crop', '0,0,20,10', '--mask', '0,0,5,10'), '0.3333')  # 50 of 150


DUMP_A = '''/Canvas#0/Panel#1\trect -100,-50,200,100 world 540,310,740,410 | Image color=255,255,255,255 raycastTarget=True
/Canvas#0/Panel#1/Label#0\trect 0,0,120,20 world 550,320,670,340 | LocText text="Cycle 12, 3:45" fontSize=14
/Canvas#0/Panel#1/Button#1\trect 0,0,40,20 world 600,350,640,370 | KButton interactable=True
/Canvas#0/Panel#1/Gone#2\trect 0,0,40,20 world 600,350,640,370
'''
DUMP_B = '''/Canvas#0/Panel#1\trect -101,-50,201,100 world 541,311,741,411 | Image color=255,255,255,255 raycastTarget=True
/Canvas#0/Panel#1/Label#0\trect 0,0,120,20 world 550,320,670,340 | LocText text="Cycle 13, 4:02" fontSize=14
/Canvas#0/Panel#1/Button#1\trect 0,0,40,20 world 600,350,640,370 | KButton interactable=False
/Canvas#0/Panel#1/New#3\trect 0,0,40,20 world 600,350,640,370
'''


class UiDiff(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.a, self.b = os.path.join(self.temp.name, 'a.txt'), os.path.join(self.temp.name, 'b.txt')
        open(self.a, 'w').write(DUMP_A)
        open(self.b, 'w').write(DUMP_B)

    def tearDown(self):
        self.temp.cleanup()

    def differences(self, **options):
        return ui_diff.diff(ui_diff.parse(self.a), ui_diff.parse(self.b), **options)

    def test_exact(self):
        lines = self.differences()
        self.assertEqual(len(lines), 5)  # only-in twice, the panel's rect, the label's text, interactable

    def test_tolerance_and_masks(self):
        lines = self.differences(tolerance=2, mask_text=[r'\d+'])
        self.assertEqual(
            [l.split('\n')[0] for l in lines],
            ['only in first: /Canvas#0/Panel#1/Gone#2', 'only in second: /Canvas#0/Panel#1/New#3', '/Canvas#0/Panel#1/Button#1 [KButton]'],
        )
        # Masks touch text only: interactable and the rect stay compared.
        self.assertEqual(len(self.differences(mask_text=[r'\d+'])), 4)

    def test_geometry(self):
        self.assertFalse(ui_diff.geometry_differs('rect 0,0,100,20 world 1,1,101,21', 'rect 1,-1,102,20 world 2,0,102,22', 2))
        self.assertTrue(ui_diff.geometry_differs('rect 0,0,100,20', 'rect 3,0,100,20', 2))
        self.assertFalse(ui_diff.geometry_differs('rect 0,0,500,20', 'rect 0,0,509,20', 2))  # size within 2 %
        self.assertTrue(ui_diff.geometry_differs('rect 0,0,500,20', 'rect 0,0,511,20', 2))
        self.assertTrue(ui_diff.geometry_differs('rect 0,0,1,20', 'rect 0,0,0,20', 2))  # 0-sized in one only
        self.assertTrue(ui_diff.geometry_differs('rect 0,0,1,20', 'corners 0,0,1,20', 2))

    def test_command_line(self):
        tool = os.path.join(helpers.SCRIPTS, 'ui-diff.py')
        out = subprocess.run(
            [sys.executable, tool, self.a, self.b, '--tolerance', '2', '--mask-text', r'\d+', '--ignore', 'KButton'], capture_output=True, text=True
        )
        self.assertEqual(out.returncode, 1)
        self.assertTrue(out.stdout.endswith('4 vs 4 elements, 2 differences\n'))
        open(self.b, 'w').write(DUMP_A)
        same = subprocess.run([sys.executable, tool, self.a, self.b], capture_output=True, text=True)
        self.assertEqual((same.returncode, same.stdout), (0, '4 vs 4 elements, 0 differences\n'))


if __name__ == '__main__':
    unittest.main()
