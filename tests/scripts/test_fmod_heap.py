"""FMOD's quarantine allocator (src/OniMoono.NativeHost/fmod_heap.c) against its unit tests
(fmod_heap_test.c), built with this machine's C compiler: its POSIX side, under several threads,
and once more under ThreadSanitizer where the compiler has it. The Windows side runs the same
tests as OniMoono.exe is built (build.cmd). Skipped without a C compiler (cc, gcc, clang, or
Python's ziglang package)."""

import importlib.util, os, shutil, subprocess, sys, tempfile, unittest

import helpers

SOURCE = os.path.join(helpers.ROOT, 'src', 'OniMoono.NativeHost')


def compiler():
    for name in ('cc', 'gcc', 'clang'):
        if shutil.which(name):
            return [name]
    if importlib.util.find_spec('ziglang'):
        return [sys.executable, '-m', 'ziglang', 'cc']
    return None


class FmodHeapTest(unittest.TestCase):
    def setUp(self):
        self.cc = compiler()
        if not self.cc:
            self.skipTest('no C compiler')
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)

    def build_and_run(self, name, *flags):
        exe = os.path.join(self.dir.name, name)
        build = subprocess.run(
            self.cc
            + [
                '-std=c11',
                '-D_POSIX_C_SOURCE=200809L',
                '-pthread',
                *flags,
                os.path.join(SOURCE, 'fmod_heap.c'),
                os.path.join(SOURCE, 'fmod_heap_test.c'),
                '-o',
                exe,
            ],
            capture_output=True,
            text=True,
        )
        if build.returncode != 0:
            return build, None
        return build, subprocess.run([exe], capture_output=True, text=True, timeout=300)

    def test_passes(self):
        build, run = self.build_and_run('fmod_heap_test', '-O2', '-Wall', '-Wextra', '-Werror')
        self.assertEqual(build.returncode, 0, build.stderr)
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertIn('fmod_heap: all passed', run.stdout)

    def test_threads_race_free(self):
        build, run = self.build_and_run('fmod_heap_tsan', '-O1', '-g', '-fsanitize=thread', '-DCHURN_OPS=40000')
        if run is None:
            self.skipTest('no ThreadSanitizer: ' + build.stderr.strip().splitlines()[-1] if build.stderr.strip() else 'no ThreadSanitizer')
        self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
        self.assertNotIn('WARNING: ThreadSanitizer', run.stderr)


if __name__ == '__main__':
    unittest.main()
