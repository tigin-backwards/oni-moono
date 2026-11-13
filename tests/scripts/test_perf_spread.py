"""scripts/perf-spread.py: a launch's windows from its log (counters as differences, frame stats
lines to the window they fall in) and a job's A/B summary."""

import contextlib, io, json, os, shutil, tempfile, unittest

import helpers

ps = helpers.script('perf-spread.py')


def launch_log(path, update_ms, windows=5, pause=2, draws=75):
    lines = ['[x] PERF colony f100 cycle 5\n']
    mono, n, rt, gt, other = 10**9, 0, 0.0, 0.0, 0
    for w in range(windows):
        lines.append(
            '[engine] frame stats (ms/frame over 60): early+fixed 1.00, update '
            f'{update_ms:.2f}, late 2.00, post-late 0.40, particles 0.00, render 5.00, gui 0.10, end of frame 0.01, present 9.95, gpu -1.00\n'
        )
        lines.append(
            '[engine] frame counts (per frame over 60): gl thread busy 13.87: draws 0.29, texture uploads 52.98; '
            f'draws {draws}, program binds 51, texture uploads 62.0 (1000 KiB), unchanged 0.0; presenter per picture (60): frame wait 1.04\n'
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
        with open(os.path.join(out, 'summary.json')) as f:
            s = json.load(f)
        self.assertEqual(s['counts']['paused']['alloc B a frame']['A']['between_launches_pct'], 0.0)
        main = s['timing']['running']['main_ms']
        self.assertAlmostEqual(main['ratio_of_medians'], 19.51 / 18.51, places=4)
        self.assertEqual(main['pair_ratio_sd_pct'], 0.0)

    def summarise(self, launches):
        runs = []
        for i, (label, ms, draws) in enumerate(launches):
            d = os.path.join(self.dir, str(i))
            os.makedirs(d)
            launch_log(os.path.join(d, 'Player.log'), ms, draws=draws)
            r = ps.parse(os.path.join(d, 'Player.log'), 2)
            r.update(label=label, status='ok', seconds=1, to_colony_s=1)
            with open(os.path.join(d, 'result.json'), 'w') as f:
                json.dump(r, f)
            runs.append(d)
        out = os.path.join(self.dir, 'sum')
        with contextlib.redirect_stdout(io.StringIO()):
            ps.summary([out, *runs])
        return os.path.join(out, 'summary.json')

    def gate(self, summary):
        text = io.StringIO()
        with contextlib.redirect_stdout(text):
            status = ps.gate([summary])
        return status, text.getvalue()

    def report(self, summary):
        with open(os.path.join(os.path.dirname(summary), 'summary.md')) as f:
            return f.read()

    def test_gate_passes_the_same_counts(self):
        summary = self.summarise([('A', 10.0, 75), ('B', 10.0, 75), ('A', 10.0, 75), ('B', 10.0, 75)])
        status, text = self.gate(summary)
        self.assertEqual(status, 0, text)
        self.assertIn('Passed', text)
        report = self.report(summary)
        # The verdict first, a line a gate, then the tables, the detail folded away last.
        self.assertLess(report.index('Passed'), report.index('### Paused colony'))
        self.assertIn('| ✅ | Draws a frame, paused | 75 | 75 | no more than A | Hard gate |', report)
        self.assertIn('| Game.Update | method | 500 | 500 | 0 | 0.0% |', report)
        self.assertLess(report.index('### Running at speed 3'), report.index('<details>'))

    def test_gate_fails_on_more_draws_and_warns_on_time(self):
        summary = self.summarise([('A', 10.0, 75), ('B', 13.0, 76), ('A', 10.0, 75), ('B', 13.0, 76)])
        status, text = self.gate(summary)
        self.assertEqual(status, 1)
        report = self.report(summary)
        self.assertIn('| ❌ | Draws a frame, paused | 75 | 76 | no more than A | Hard gate |', report)
        # The main thread's time only warns.
        self.assertIn('| ⚠️ | Main-thread ms a frame, running (B/A) |', report)

    def test_section_names(self):
        self.assertEqual(ps.section_parts('bucket OrbitalObject RENDER_1000ms'), ('OrbitalObject', 'bucket, render 1,000 ms'))
        self.assertEqual(ps.section_parts('bucket DiagnosticRow SIM_4000ms'), ('DiagnosticRow', 'bucket, sim 4,000 ms'))
        self.assertEqual(ps.section_parts('Game.Update'), ('Game.Update', 'method'))
        self.assertEqual(ps.section_parts('(other)')[1], '–')
        self.assertEqual(ps.num(1234567), '1,234,567')


if __name__ == '__main__':
    unittest.main()
