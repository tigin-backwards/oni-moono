"""scripts/perf-spread.py: a launch's windows from its log (counters as differences, frame stats
lines to the window they fall in) and a job's A/B summary."""

import contextlib, io, json, os, shutil, tempfile, unittest

import helpers

ps = helpers.script('perf-spread.py')


def launch_log(path, update_ms, windows=5, pause=2):
    lines = ['[x] PERF colony f100 cycle 5\n']
    mono, n, rt, gt, other = 10**9, 0, 0.0, 0.0, 0
    for w in range(windows):
        lines.append(
            '[engine] frame stats (ms/frame over 60): early+fixed 1.00, update '
            f'{update_ms:.2f}, late 2.00, post-late 0.40, particles 0.00, render 5.00, gui 0.10, end of frame 0.01, present 9.95, gpu -1.00\n'
        )
        lines.append(
            '[engine] frame counts (per frame over 60): gl thread busy 13.87: draws 0.29, texture uploads 52.98; '
            'draws 75, program binds 51, texture uploads 62.0 (1000 KiB), unchanged 0.0; presenter per picture (60): frame wait 1.04\n'
        )
        lines.append('[engine] sim (ms/frame over 600): SimEveryTick 1.00 (SimDLL 0.1), UnsafeSim200ms 2.00 (SimDLL 0.1), StepTheSim 3.00 (SimDLL 0.1); x\n')
        n, rt, mono, other = n + 600, rt + 40, mono + 600_000, other + 300_000
        gt += 0 if w < pause else 100
        lines.append(f'[x] PERF f{1000 + 600 * w} fps 15.00 cycle 5 time 3 paused False\n')
        lines.append(f'[x] PERF alloc f1 n {n} rt {rt:.3f} gt {gt:.2f} mono {mono} gcno 4 fullms -1 hg 1 dnpause 5 dc {w}/0/0\n')
        lines.append(f'[x] PERF sites f1 (other)={other}/{w} | Game.Update={other}/{w}\n')
    with open(path, 'w') as f:
        f.writelines(lines)


class PerfSpreadTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(lambda: shutil.rmtree(self.dir))

    def test_windows(self):
        log = os.path.join(self.dir, 'Player.log')
        launch_log(log, 10.0)
        ws = ps.parse(log, 2)['windows']
        # The first window has no start for its counters.
        self.assertEqual([w['index'] for w in ws], [1, 2, 3, 4])
        self.assertEqual([w['phase'] for w in ws], ['paused', 'running', 'running', 'running'])
        w = ws[0]
        self.assertEqual((w['frames'], w['alloc_bytes'], w['gc']), (600, 600_000, [1, 0, 0]))
        self.assertAlmostEqual(w['main_ms'], 18.51)
        self.assertEqual((w['draws'], w['uploads'], w['upload_kb']), (75, 62.0, 1024.0))
        self.assertEqual((w['step_ms'], w['sim_ms']), (3.0, 6.0))
        self.assertEqual(w['sites'], {'(other)': 300_000, 'Game.Update': 300_000})

    def test_summary_pairs(self):
        runs = []
        for i, (label, ms) in enumerate((('A', 10.0), ('B', 11.0), ('A', 10.0), ('B', 11.0))):
            d = os.path.join(self.dir, str(i))
            os.makedirs(d)
            launch_log(os.path.join(d, 'Player.log'), ms)
            r = ps.parse(os.path.join(d, 'Player.log'), 2)
            r.update(label=label, status='ok', seconds=1, to_colony_s=1)
            with open(os.path.join(d, 'result.json'), 'w') as f:
                json.dump(r, f)
            runs.append(d)
        out = os.path.join(self.dir, 'sum')
        with contextlib.redirect_stdout(io.StringIO()):
            ps.summary([out, *runs])
        s = json.load(open(os.path.join(out, 'summary.json')))
        self.assertEqual(s['counts']['paused']['alloc B a frame']['A']['between_launches_pct'], 0.0)
        main = s['timing']['running']['main_ms']
        self.assertAlmostEqual(main['ratio_of_medians'], 19.51 / 18.51, places=4)
        self.assertEqual(main['pair_ratio_sd_pct'], 0.0)


if __name__ == '__main__':
    unittest.main()
