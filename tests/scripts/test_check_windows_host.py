"""check-windows-host.py's PE reading: the GPU exports' values found at their RVA's section
offset, in a writable section, on a small PE32+ built here (no Windows needed)."""

import contextlib, io, os, struct, sys, tempfile, unittest

import helpers

check = helpers.script('check-windows-host.py')

READ, WRITE, DATA = 0x40000000, 0x80000000, 0x40


def pe(exports, data_flags=READ | WRITE | DATA):
    """A PE32+ with .rdata (the export directory) at RVA 0x1000 and .data at 0x2000, holding
    `exports` ({name: DWORD}) in that order."""
    names = list(exports)
    # .rdata: directory, function RVAs, name RVAs, ordinals, then the names.
    funcs_at, names_at = 0x1000 + 40, 0x1000 + 40 + 4 * len(names)
    ords_at = names_at + 4 * len(names)
    strings_at = ords_at + 2 * len(names)
    strings, name_rvas = b'', []
    for n in names:
        name_rvas.append(strings_at + len(strings))
        strings += n.encode() + b'\0'
    rdata = struct.pack('<IIHHIIIIIII', 0, 0, 0, 0, 0, 1, len(names), len(names), funcs_at, names_at, ords_at)
    rdata += b''.join(struct.pack('<I', 0x2000 + 4 * i) for i in range(len(names)))
    rdata += b''.join(struct.pack('<I', r) for r in name_rvas)
    rdata += b''.join(struct.pack('<H', i) for i in range(len(names)))
    rdata += strings
    data = b''.join(struct.pack('<I', v) for v in exports.values())
    dos = b'MZ' + b'\0' * 0x3A + struct.pack('<I', 0x40)
    coff = b'PE\0\0' + struct.pack('<HHIIIHH', 0x8664, 2, 0, 0, 0, 240, 0x22)
    opt = bytearray(240)
    struct.pack_into('<H', opt, 0, 0x20B)
    struct.pack_into('<HH', opt, 68, 2, 0xC160)
    struct.pack_into('<I', opt, 108, 16)
    struct.pack_into('<II', opt, 112, 0x1000, len(rdata))
    section = lambda name, va, size, raw, flags: name.ljust(8, b'\0') + struct.pack('<IIIIIIHHI', size, va, 0x200, raw, 0, 0, 0, 0, flags)
    headers = dos + coff + bytes(opt) + section(b'.rdata', 0x1000, len(rdata), 0x200, READ | DATA) + section(b'.data', 0x2000, len(data), 0x400, data_flags)
    image = headers.ljust(0x200, b'\0') + rdata.ljust(0x200, b'\0') + data.ljust(0x200, b'\0')
    return image


def run(image, *args):
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, 'OniMoono.exe')
        with open(path, 'wb') as f:
            f.write(image)
        out = io.StringIO()
        argv = sys.argv
        sys.argv = ['check-windows-host.py', path, *args]
        try:
            with contextlib.redirect_stdout(out):
                code = check.main()
        finally:
            sys.argv = argv
        return code, out.getvalue()


class CheckWindowsHost(unittest.TestCase):
    def test_exports_of_value_one_in_a_writable_section_pass(self):
        code, out = run(pe({'AmdPowerXpressRequestHighPerformance': 1, 'NvOptimusEnablement': 1}), '--exports-only')
        self.assertEqual(code, 0, out)
        self.assertIn('ok   export NvOptimusEnablement: RVA 0x2004 in .data (file offset 0x404, writable), value 1', out)

    def test_a_zero_a_missing_export_or_a_read_only_section_fail(self):
        code, out = run(pe({'AmdPowerXpressRequestHighPerformance': 1, 'NvOptimusEnablement': 0}), '--exports-only')
        self.assertEqual(code, 1)
        self.assertIn('FAIL export NvOptimusEnablement', out)
        code, out = run(pe({'NvOptimusEnablement': 1}), '--exports-only')
        self.assertIn('FAIL export AmdPowerXpressRequestHighPerformance missing', out)
        code, out = run(pe({'AmdPowerXpressRequestHighPerformance': 1, 'NvOptimusEnablement': 1}, data_flags=READ | DATA), '--exports-only')
        self.assertEqual(code, 1)
        self.assertIn('read-only', out)

    def test_without_resources_the_full_check_fails(self):
        code, out = run(pe({'AmdPowerXpressRequestHighPerformance': 1, 'NvOptimusEnablement': 1}))
        self.assertEqual(code, 1)
        self.assertIn('ok   subsystem 2 (Windows GUI)', out)
        self.assertIn('FAIL icon group(s) none', out)


if __name__ == '__main__':
    unittest.main()
