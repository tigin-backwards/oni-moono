"""qa-tools.py's verify summary: every verify mode's last report, failing on any mismatch."""
import contextlib, io, os, tempfile, unittest

import helpers

qa_tools = helpers.script('qa-tools.py')

PROBES = ('[18:53:14.370] [1] [INFO] path-probe-repair: 17435 probes: 12059 repaired (0 with nothing changed, 59 us each), '
          '5376 flooded (2344 us each; first 22, moved or other abilities 5346, grid replaced 0, changes too old 0, too many '
          'changes 0, repair gave up 8), 9285 repairs verified, {repairs} otherwise than a flood, {ties} parting from a flood at a tie, '
          '14 floods verified, {floods} '
          "otherwise than the game's (the game's 3667 us each, ours 2838), 8136 not checked (the world changed meanwhile), "
          '{missed} changes the feed missed')


def verify(*lines):
    """(exit status, printed lines) of `qa-tools.py verify` on a log of these lines."""
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, 'log')
        with open(path, 'w') as f:
            f.write(''.join(line + '\n' for line in lines))
        out = io.StringIO()
        with contextlib.redirect_stdout(out), self_exit() as status:
            qa_tools.verify(path)
        return status[0], out.getvalue().splitlines()


@contextlib.contextmanager
def self_exit():
    status = [None]
    try:
        yield status
    except SystemExit as e:
        status[0] = e.code


class VerifySummary(unittest.TestCase):
    def assertVerdicts(self, result, status, *starts):
        code, lines = result
        self.assertEqual(code, status, lines)
        self.assertEqual(len(lines), len(starts), lines)
        for line, start in zip(lines, starts):
            self.assertTrue(line.startswith(start), (line, start))

    def test_clean_reports_of_every_check(self):
        result = verify(PROBES.format(repairs=0, ties=0, floods=0, missed=0),
                        "[engine] nav links verify (after the load): 19 grids, 1,573,927 links, 0 cell(s) differ from the game's arrays (50 ms)",
                        "[engine] nav links verify (in play): 19 grids, 1,598,914 links, 0 cell(s) differ from the game's arrays (47 ms)",
                        '[engine] path grids verify: 38 tables, 12,345,678 elements, 0 differ from the flat arrays, 3 only mid-write (12 ms)',
                        '[18:42:40.000] [1] [INFO] trim-anim-lists: 5923 lists trimmed, 66.5 MB freed, 0 differed',
                        '[18:42:40.000] [1] [INFO] fast-load-screen: verified 12 header reads (3 from the memo, 9 read ahead): 0 differed',
                        "[engine] fast-load-screen: 4 images from the decode memo, 8 decoded; 2 downscaled previews kept from earlier, 2 checked against the game's, 0 differed",
                        '[engine] canvas skips: 44376 verified, 0 wrong')
        self.assertVerdicts(result, 0, 'ok   canvas skips:', 'ok   fast-load-screen: 4 images', 'ok   fast-load-screen: verified',
                            'ok   nav links verify (after the load):', 'ok   nav links verify (in play):', 'ok   path grids verify:',
                            'ok   path-probe-repair:', 'ok   trim-anim-lists:')

    def test_probe_repair_counts(self):
        for counts in ({'repairs': 9, 'floods': 0, 'missed': 0}, {'repairs': 0, 'floods': 1, 'missed': 0}, {'repairs': 0, 'floods': 0, 'missed': 3}):
            self.assertVerdicts(verify(PROBES.format(ties=0, **counts)), 1, 'FAIL path-probe-repair:')

    def test_probe_repair_ties_pass(self):
        # A repair that keeps the other of two routes of the same cost parts from a flood
        # beyond it (other suit flags, another parent): expected, and not a failure.
        self.assertVerdicts(verify(PROBES.format(repairs=0, ties=4, floods=0, missed=0)), 0, 'ok   path-probe-repair:')

    def test_probe_repair_without_the_feed_audit(self):
        # Builds before the feed's audit end the summary at the checks skipped.
        line = PROBES.format(repairs=0, ties=0, floods=0, missed=0).replace(', 0 changes the feed missed', '')
        self.assertVerdicts(verify(line), 0, 'ok   path-probe-repair:')

    def test_probe_repair_incidents(self):
        result = verify(PROBES.format(repairs=1, ties=0, floods=0, missed=1),
                        '[18:51:53.456] [67] [INFO] path-probe-repair: a repair answers otherwise than a flood in 25 cells (root 72387, first 45880, 45881), '
                        'not through a tie: state 69223 is not reached, but 60732 reaches it',
                        "[18:51:53.456] [67] [INFO] path-probe-repair: a flood answers otherwise than the game's in 3 cells (root 1, first 2)",
                        '[18:51:53.456] [1] [INFO] path-probe-repair: the feed missed a change at cell 45880 (x 120, y 81, world 3): nav mask 0 to 1 (frame 9)')
        self.assertVerdicts(result, 1, 'FAIL path-probe-repair:', 'FAIL incident path-probe-repair: a repair',
                            'FAIL incident path-probe-repair: a flood', 'FAIL incident path-probe-repair: the feed missed')

    def test_probe_repair_lines_that_are_no_report(self):
        result = verify('[18:42:33.885] [1] [INFO] path-probe-repair: on (84980 cells, 7 nav types)',
                        '[18:42:33.885] [1] [INFO] path-probe-repair:   since its last result: 3 rows, 0 sources, 1 destinations, 0 voids (seq 4 to 9); differing:',
                        '[18:42:33.885] [1] [INFO] trim-anim-lists: 5923 lists trimmed, 66.5 MB freed')
        self.assertVerdicts(result, 0)

    def test_nav_links_and_path_grids_differ(self):
        result = verify("[engine] nav links verify (in play): 19 grids, 1,598,914 links, 2 cell(s) differ from the game's arrays; first: 3: cell 7 (47 ms)",
                        '[engine] path grids verify: 38 tables, 12,345,678 elements, 1 differ from the flat arrays, 0 only mid-write; first: Cells[4] (12 ms)')
        self.assertVerdicts(result, 1, 'FAIL nav links verify (in play):', 'FAIL path grids verify:')

    def test_trim_and_load_screen_differ(self):
        result = verify('[18:42:40.000] [1] [INFO] trim-anim-lists: 5923 lists trimmed, 66.5 MB freed, 4 differed',
                        '[18:42:40.000] [1] [INFO] fast-load-screen: verified 12 header reads (3 from the memo, 9 read ahead): 1 differed',
                        "[18:42:40.000] [1] [INFO] fast-load-screen: the header of a.sav differs from the game's read",
                        "[engine] fast-load-screen: a downscaled preview differs from the game's (64x64, texels differ)")
        self.assertVerdicts(result, 1, 'FAIL fast-load-screen: verified', 'FAIL trim-anim-lists:',
                            'FAIL incident fast-load-screen: the header', 'FAIL incident fast-load-screen: a downscaled')

    def test_the_last_report_of_each_shape(self):
        result = verify('[engine] canvas skips: 100 verified, 1 wrong', '[engine] canvas skips: 200 verified, 0 wrong',
                        '[engine] canvas skips: 0 graphics skipped by masks, 0 wrong')
        self.assertVerdicts(result, 0, 'ok   canvas skips: 0 graphics', 'ok   canvas skips: 200 verified')


if __name__ == '__main__':
    unittest.main()
