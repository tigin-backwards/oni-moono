"""test-skips.py: the skips of xunit reports by reason, and the gate on skips for want of the game."""
import contextlib, io, os, tempfile, unittest

import helpers

test_skips = helpers.script('test-skips.py')

REPORT = '''<?xml version="1.0" encoding="utf-8"?>
<assemblies>
  <assembly name="/out/bin/S/debug/S.dll">
    <collection>
      <test name="A.One" type="A" method="One" result="Skip"><reason><![CDATA[needs game data: /g/OxygenNotIncluded_Data]]></reason></test>
      <test name="A.Two" type="A" method="Two" result="Skip"><reason><![CDATA[needs game data: /g/OxygenNotIncluded_Data]]></reason></test>
      <test name="B.One" type="B" method="One" result="Skip"><reason><![CDATA[needs game assembly: 0Harmony (a reference assembly)]]></reason></test>
      <test name="C.One" type="C" method="One" result="Skip"><reason><![CDATA[DejaVu Sans is not installed.]]></reason></test>
      <test name="C.Two" type="C" method="Two" result="Pass" />
    </collection>
  </assembly>
</assemblies>
'''


class TestSkips(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.temp.name, 'S.xml')
        with open(self.path, 'w') as f:
            f.write(REPORT)

    def tearDown(self):
        self.temp.cleanup()

    def run_main(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            status = test_skips.main([*args, self.path])
        return status, out.getvalue()

    def test_the_report_groups_by_reason_the_games_first(self):
        status, out = self.run_main()
        self.assertEqual(0, status)
        self.assertEqual([
            'skipped: 4 tests',
            '  needs game assembly: 0Harmony (a reference assembly) (1 tests, 1 classes)',
            '    B (1)',
            '  needs game data: /g/OxygenNotIncluded_Data (2 tests, 1 classes)',
            '    A (2)',
            '  DejaVu Sans is not installed. (1 tests, 1 classes)',
            '    C (1)',
        ], out.splitlines())

    def test_the_gate_names_the_tests_skipped_for_want_of_the_game(self):
        status, out = self.run_main('--fail-on-game')
        self.assertEqual(1, status)
        self.assertIn('S.dll: A.One: needs game data: /g/OxygenNotIncluded_Data', out)
        self.assertIn('S.dll: B.One: needs game assembly: 0Harmony', out)
        self.assertNotIn('C.One', out)

    def test_the_gate_passes_other_skips(self):
        with open(self.path, 'w') as f:
            f.write(REPORT.replace('needs game', 'wants'))
        self.assertEqual((0, ''), self.run_main('--fail-on-game'))


if __name__ == '__main__':
    unittest.main()
