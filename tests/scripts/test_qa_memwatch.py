"""qa-tools.py's memwatch (qa.sh's memory stage): frames from PERF lines, the autosave, the stop."""

import unittest

import helpers

qa_tools = helpers.script('qa-tools.py')
GB = 1024 * 1024


class MemWatch(unittest.TestCase):
    def test_frames_are_estimated_from_perf_lines(self):
        w = qa_tools.MemWatch()
        self.assertEqual(w.estimate(0), 0)
        w.line('[11:22:08.635] [1] [INFO] PERF f1505 fps 20.85 cycle 17077 time 1', 100.0)
        self.assertEqual(w.estimate(110.0), 1505 + 208)
        self.assertEqual(w.estimate(1000.0), 1505 + 600)  # never past the next PERF line

    def test_it_stops_once_the_autosave_has_passed_and_rss_has_settled(self):
        w = qa_tools.MemWatch(settle=1200, rise_kb=64 * 1024)
        w.line('PERF f1505 fps 60.00 cycle 1', 0.0)
        stops = []
        rss = 10 * GB
        for t in range(1, 200):
            now = float(t)
            if t % 10 == 0:
                w.line(f'PERF f{1505 + 60 * t} fps 60.00 cycle 1', now)
            if t == 50:
                w.line('[INFO] Saved to [/h/cloud_save_files/1/U57/auto_save/U57 Cycle 17079.sav]', now)
            if t <= 60:
                rss += 100 * 1024  # rising through the autosave
            else:
                rss += 1024  # then creeping by less than the tolerance
            why = w.sample(rss, rss - GB, GB, now)
            if why:
                stops.append((t, why))
                break
        t, why = stops[0]
        self.assertIn('autosave', why)
        self.assertEqual(w.autosave, 1505 + 60 * 50)
        # The last rise was at t=60 (frame 5105): 1200 frames later is t=80.
        self.assertEqual(t, 80)
        summary = w.summary()
        self.assertEqual(summary['peak_frame'], 1505 + 60 * 80)
        self.assertEqual(summary['rss_file_kb'], GB)

    def test_no_stop_before_an_autosave(self):
        w = qa_tools.MemWatch(settle=10)
        w.line('PERF f600 fps 60.00 cycle 1', 0.0)
        self.assertTrue(all(w.sample(GB, GB, 0, float(t)) is None for t in range(1, 11)))

    def test_the_background_save_line_counts(self):
        w = qa_tools.MemWatch()
        w.line('[engine] background save written: /h/U57/auto_save/U57 Cycle 1.sav', 5.0)
        self.assertEqual(w.autosave, 0)


if __name__ == '__main__':
    unittest.main()
