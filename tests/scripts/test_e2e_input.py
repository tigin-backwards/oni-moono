"""e2e.py's XTEST input on a real X server, read back with XQueryPointer and XQueryKeymap. It
sends real input, so it runs only on a display named in E2E_XTEST_DISPLAY (never the desktop's
DISPLAY):
  xvfb-run -a sh -c 'E2E_XTEST_DISPLAY=$DISPLAY python3 -m unittest discover -s tests/scripts -p test_e2e_input.py'
"""

import ctypes, os, unittest

import helpers  # noqa: F401
import e2e


@unittest.skipUnless(os.environ.get('E2E_XTEST_DISPLAY'), 'needs E2E_XTEST_DISPLAY (a scratch display, xvfb-run -a)')
class Xtest(unittest.TestCase):
    def setUp(self):
        self.input = e2e.Input(os.environ['E2E_XTEST_DISPLAY'], os.environ.get('XAUTHORITY'), frames=lambda n: None)
        x11 = self.x11 = self.input.x11
        x11.XDefaultRootWindow.argtypes, x11.XDefaultRootWindow.restype = [ctypes.c_void_p], ctypes.c_ulong
        x11.XQueryPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong] + [ctypes.c_void_p] * 7
        x11.XQueryKeymap.argtypes = [ctypes.c_void_p, ctypes.c_char_p]

    def tearDown(self):
        self.input.close()

    def pointer(self):
        root, child = ctypes.c_ulong(), ctypes.c_ulong()
        rx, ry, wx, wy, mask = ctypes.c_int(), ctypes.c_int(), ctypes.c_int(), ctypes.c_int(), ctypes.c_uint()
        self.x11.XQueryPointer(
            self.input.dpy,
            self.x11.XDefaultRootWindow(self.input.dpy),
            ctypes.byref(root),
            ctypes.byref(child),
            ctypes.byref(rx),
            ctypes.byref(ry),
            ctypes.byref(wx),
            ctypes.byref(wy),
            ctypes.byref(mask),
        )
        return rx.value, ry.value, mask.value

    def pressed(self, name):
        keys = ctypes.create_string_buffer(32)
        self.x11.XQueryKeymap(self.input.dpy, keys)
        keycode, _ = self.input.keycode(name)
        return bool(keys.raw[keycode // 8] & (1 << (keycode % 8)))

    def test_motion_and_buttons(self):
        self.input.move(123, 45)
        self.assertEqual(self.pointer()[:2], (123, 45))
        self.input.button('left', True)
        self.assertTrue(self.pointer()[2] & (1 << 8))  # Button1Mask
        self.input.button('left', False)
        self.assertFalse(self.pointer()[2] & (1 << 8))
        self.input.drag((10, 10), (90, 50), steps=4)
        self.assertEqual(self.pointer()[:2], (90, 50))
        self.input.drag((10, 10), (90, 50), steps=2, via=[(10, 50)])
        self.assertEqual(self.pointer()[:2], (90, 50))
        self.input.click(200, 100, mods=['Shift'])
        self.assertEqual(self.pointer(), (200, 100, 0))

    def test_scaled_screen(self):
        """`scaled_screen`: the game's 960x720 stretched over a 1280x720 display; positions are
        the game's pixels."""
        self.input.scale = (1280 / 960, 1.0)
        self.input.move(480, 373)
        self.assertEqual(self.pointer()[:2], (640, 373))
        self.input.click(703.6, 139)
        self.assertEqual(self.pointer()[:2], (938, 139))

    def test_keys(self):
        for name in ('b', 'Shift_L', 'Control_L', 'Escape', 'Return', 'F5', 'Up', 'KP_Add', '7', 'bracketleft'):
            self.assertTrue(self.input.keycode(name)[0], name)
        self.input.key_event('Shift_L', True)
        self.assertTrue(self.pressed('Shift_L'))
        self.input.key_event('Shift_L', False)
        self.assertFalse(self.pressed('Shift_L'))
        self.input.key('Ctrl+Shift+B')
        self.input.type_text('e2e-Blueprint_1!')
        self.assertFalse(any(self.pressed(n) for n in ('Shift_L', 'Control_L', 'b')))

    def test_grab(self):
        """The screen read back as a PNG of its size: the root window painted one colour first
        (a 24-bit screen: xvfb-run -a -s '-screen 0 320x200x24')."""
        import tempfile
        from PIL import Image

        x11, dpy = self.x11, self.input.dpy
        x11.XSetWindowBackground.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong]
        x11.XClearWindow.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        x11.XDefaultScreen.argtypes = [ctypes.c_void_p]
        x11.XDefaultDepth.argtypes = [ctypes.c_void_p, ctypes.c_int]
        if x11.XDefaultDepth(dpy, x11.XDefaultScreen(dpy)) != 24:
            self.skipTest('needs a 24-bit screen')
        root = x11.XDefaultRootWindow(dpy)
        x11.XSetWindowBackground(dpy, root, 0xFF8020)
        x11.XClearWindow(dpy, root)
        with tempfile.TemporaryDirectory() as folder:
            path = self.input.grab(os.path.join(folder, 'screen.png'))
            with Image.open(path) as image:
                x11.XDisplayWidth.argtypes = x11.XDisplayHeight.argtypes = [ctypes.c_void_p, ctypes.c_int]
                screen = x11.XDefaultScreen(dpy)
                self.assertEqual(image.size, (x11.XDisplayWidth(dpy, screen), x11.XDisplayHeight(dpy, screen)))
                self.assertEqual(image.convert('RGB').getpixel((5, 5)), (0xFF, 0x80, 0x20))


if __name__ == '__main__':
    unittest.main()
