"""e2e.py's `save` step: waiting until a save is on disk whole before it is copied."""

import json, os, struct, tempfile, unittest, zlib

import helpers  # noqa: F401  (puts scripts/ on the path)
import e2e


def save_bytes(compressed=True, body=b'Klei.SaveFileRoot' * 4000):
    header = json.dumps({'baseName': 'Orbit', 'isAutoSave': False}).encode()
    # The type directory before the body: bytes that look like zlib headers must not fool the check.
    directory = b'\x11\x00\x00\x00Klei.SaveFileRoot' + b'\x78\x9c\x78\x01\x08\x1d' * 50
    prefix = struct.pack('<IiIi', 744825, len(header), 1, 1 if compressed else 0)
    return prefix + header + directory, (zlib.compress(body, 1) if compressed else body)


class SaveWritten(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, 'e2e.sav')
        self.log = os.path.join(self.dir.name, 'stdout.txt')
        open(self.log, 'w').close()

    def tearDown(self):
        self.dir.cleanup()

    def write(self, data):
        with open(self.path, 'wb') as f:
            f.write(data)

    def test_whole_save(self):
        head, body = save_bytes()
        self.write(head + body)
        self.assertTrue(e2e.save_written(self.path))

    def test_header_only_while_the_background_write_runs(self):
        head, _ = save_bytes()
        self.write(head)
        self.assertFalse(e2e.save_written(self.path))

    def test_truncated_or_trailing_body(self):
        head, body = save_bytes()
        self.write(head + body[: len(body) // 2])
        self.assertFalse(e2e.save_written(self.path))
        self.write(head + body[:-1])
        self.assertFalse(e2e.save_written(self.path))
        self.write(head + body + b'\0')
        self.assertFalse(e2e.save_written(self.path))

    def test_uncompressed_save_and_missing_file(self):
        head, body = save_bytes(compressed=False)
        self.write(head + body)
        self.assertTrue(e2e.save_written(self.path))
        self.assertFalse(e2e.save_written(os.path.join(self.dir.name, 'none.sav')))

    def wait(self, ticks, offset=0, timeout=10.0):
        """wait_save_written on a fake clock: TICKS(n) runs before the n-th poll's sleep."""
        now = [0.0]
        polls = [0]

        def sleep(seconds):
            ticks(polls[0])
            polls[0] += 1
            now[0] += seconds

        result = e2e.wait_save_written(self.path, self.log, offset, timeout=timeout, poll=1.0, clock=lambda: now[0], sleep=sleep)
        return result, polls[0]

    def test_waits_for_the_background_write(self):
        head, body = save_bytes()
        self.write(head)
        # Ours moves the whole file over the header-only one, then logs it.
        result, polls = self.wait(lambda n: n == 2 and self.write(head + body))
        self.assertEqual((result, polls), (True, 3))

    def test_the_log_line_ends_the_wait(self):
        head, _ = save_bytes()
        self.write(head)
        with open(self.log, 'w') as f:
            f.write(e2e.BACKGROUND_SAVE_WRITTEN + self.path + '\n')  # an earlier save's line
        offset = os.path.getsize(self.log)

        def ticks(n):
            if n == 1:
                with open(self.log, 'a') as f:
                    f.write(e2e.BACKGROUND_SAVE_WRITTEN + self.path + '\n')

        result, polls = self.wait(ticks, offset)
        self.assertEqual((result, polls), (True, 2))

    def test_the_original_save_is_whole_at_once(self):
        head, body = save_bytes()
        self.write(head + body)
        self.assertEqual(self.wait(lambda n: None), (True, 0))

    def test_times_out(self):
        head, _ = save_bytes()
        self.write(head)
        self.assertEqual(self.wait(lambda n: None, timeout=5.0), (False, 5))


if __name__ == '__main__':
    unittest.main()
