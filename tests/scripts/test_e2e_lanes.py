"""e2e.py's lanes: ports, lane locks, the perf wait and the job queue."""
import os, shutil, signal, subprocess, tempfile, threading, time, types, unittest

import helpers  # noqa: F401
import e2e


class Lanes(unittest.TestCase):
    def test_ports(self):
        self.assertEqual(e2e.lane_ports(0), (8800, 8801))
        self.assertEqual(e2e.lane_ports(2), (8820, 8821))
        self.assertEqual(e2e.lane_ports(5), (8850, 8851))

    def test_allocation_skips_held_and_busy_lanes(self):
        with tempfile.TemporaryDirectory() as locks:
            free = lambda port: False
            quiet = dict(display_busy=lambda d: False, reserved=(set(), set()))
            a = e2e.allocate_lane(2, locks, busy=free, **quiet)
            b = e2e.allocate_lane(2, locks, busy=free, **quiet)
            self.assertEqual((a.number, b.number), (2, 3))
            self.assertEqual((b.mcp_port, b.driver_port, b.display), (8830, 8831, ':303'))
            # A lane whose port another program holds is skipped.
            c = e2e.allocate_lane(2, locks, busy=lambda port: port == 8840, **quiet)
            self.assertEqual(c.number, 5)
            a.release()
            d = e2e.allocate_lane(2, locks, busy=free, **quiet)
            self.assertEqual(d.number, 2)
            for lane in (b, c, d):
                lane.release()

    def test_all_lanes_held(self):
        with tempfile.TemporaryDirectory() as locks:
            quiet = dict(busy=lambda p: False, display_busy=lambda d: False, reserved=(set(), set()), last=6)
            held = [e2e.allocate_lane(1, locks, **quiet) for _ in range(6)]
            self.assertIsNone(e2e.allocate_lane(1, locks, wait=False, **quiet))
            # A waiting slot gives up once its queue has nothing left for it.
            calls = []
            give_up = lambda: calls.append(1) or True
            self.assertIsNone(e2e.allocate_lane(1, locks, give_up=give_up, log=lambda s: None, **quiet))
            self.assertEqual(calls, [1])
            for lane in held:
                lane.release()

    def test_displays(self):
        self.assertEqual(e2e.parse_displays(':88,89'), [':88', ':89'])
        with self.assertRaises(ValueError):
            e2e.parse_displays(':88,x')


@unittest.skipUnless(shutil.which('xvfb-run') and shutil.which('Xvfb'), 'needs xvfb-run')
class XvfbDisplay(unittest.TestCase):
    def test_the_display_xvfb_run_gave_the_command(self):
        # The runner's own DISPLAY (a desktop's) is in xvfb-run's environment, and so in its
        # Xvfb's: only the command's (with XAUTHORITY) counts.
        env = dict(os.environ, DISPLAY=':0')
        env.pop('XAUTHORITY', None)
        process = subprocess.Popen(['xvfb-run', '-a', 'sleep', '30'], env=env, start_new_session=True)
        try:
            stub = types.SimpleNamespace(process=process, display=None, xauthority=None, keyboard=None)
            e2e.Player.find_display(stub)
            self.assertRegex(stub.display, r'^:\d+$')
            self.assertNotEqual(stub.display, ':0')
            self.assertTrue(os.path.exists(stub.xauthority))
            self.assertTrue(e2e.display_ready(stub.display[1:]))
        finally:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(10)


    @unittest.skipIf(os.path.exists('/tmp/.X89-lock'), 'display :89 is in use')
    def test_an_explicit_display(self):
        with tempfile.TemporaryDirectory() as out:
            stub = types.SimpleNamespace(out=out, xvfb=None, keyboard=None, display_dpi=None)
            e2e.Player.start_xvfb(stub, ':89')
            try:
                self.assertTrue(e2e.display_ready('89'))
                with self.assertRaises(e2e.SpecFailure):
                    e2e.Player.start_xvfb(types.SimpleNamespace(out=out, xvfb=None, keyboard=None, display_dpi=None), ':89')
            finally:
                os.killpg(stub.xvfb.pid, signal.SIGTERM)
                stub.xvfb.wait(10)


class PerfWait(unittest.TestCase):
    def test_perf_jobs(self):
        lines = ['bash scripts/qa.sh', '/bin/bash out/local-tools/sweep11c.sh', 'python3 scripts/e2e.py early-base',
                 '/home/x/out/bin/OniMoono.Host/release/OniMoono --game g', 'perf record -g -p 12',
                 'bash /home/x/projects/oni-brutal/scripts/qa.sh u57', 'vim scripts/qa.sh.orig',
                 'bash out/local-tools/measure.sh 3']
        # qa.sh is not a perf job: only its perf stage is, through perf.wanted and perf.lock.
        self.assertEqual(e2e.perf_jobs(lines), ['/bin/bash out/local-tools/sweep11c.sh',
                                                'perf record -g -p 12', 'bash out/local-tools/measure.sh 3'])

    def test_exclusive_lock_holds_specs_back(self):
        import fcntl, os
        with tempfile.TemporaryDirectory() as locks:
            perf = open(os.path.join(locks, 'perf.lock'), 'w')
            fcntl.flock(perf, fcntl.LOCK_EX)
            entered, messages = [], []
            original_sleep = e2e.time.sleep
            e2e.time.sleep = lambda s: original_sleep(0.01)
            try:
                def spec():
                    with e2e.machine_lock(locks, perf_wait=False, log=messages.append):
                        entered.append(True)
                t = threading.Thread(target=spec)
                t.start()
                original_sleep(0.2)
                self.assertEqual(entered, [])
                fcntl.flock(perf, fcntl.LOCK_UN)
                t.join(5)
                self.assertEqual(entered, [True])
                self.assertEqual(messages, [f'waiting for a perf job to finish: a perf job holds {locks}/perf.lock exclusively',
                                            'perf wait over after 0.0 min'])
            finally:
                e2e.time.sleep = original_sleep
                perf.close()

    def test_lanes_share_the_lock(self):
        with tempfile.TemporaryDirectory() as locks:
            with e2e.machine_lock(locks, perf_wait=False):
                with e2e.machine_lock(locks, perf_wait=False):
                    pass


class Queue(unittest.TestCase):
    def test_ours_waits_for_the_original(self):
        specs = [{'name': 'a'}, {'name': 'b'}]
        jobs = e2e.plan_jobs(specs, ['original', 'ours'], 2)
        self.assertEqual([j['id'] for j in jobs], ['a/original-1', 'a/original-2', 'a/ours-1', 'a/ours-2',
                                                   'b/original-1', 'b/original-2', 'b/ours-1', 'b/ours-2'])
        self.assertEqual(jobs[2]['after'], ['a/original-1', 'a/original-2'])
        queue = e2e.JobQueue(jobs)
        order, lock = [], threading.Lock()

        def worker():
            while True:
                job = queue.next()
                if job is None:
                    return
                with lock:
                    order.append(('start', job['id']))
                time.sleep(0.02)
                with lock:
                    order.append(('done', job['id']))
                queue.done(job)

        threads = [threading.Thread(target=worker) for _ in range(3)]
        for t in threads:
            t.start()
        for t in threads:
            t.join(10)
        started = [j for e, j in order if e == 'start']
        self.assertEqual(sorted(started), sorted(j['id'] for j in jobs))
        for spec in 'ab':
            last_original = max(order.index(('done', f'{spec}/original-{i}')) for i in (1, 2))
            first_ours = min(order.index(('start', f'{spec}/ours-{i}')) for i in (1, 2))
            self.assertLess(last_original, first_ours)
        # b's originals ran beside a's (three lanes), not after a's ours.
        self.assertLess(order.index(('start', 'b/original-1')), order.index(('done', 'a/ours-1')))

    def test_ours_alone_has_no_dependencies(self):
        jobs = e2e.plan_jobs([{'name': 'a'}], ['ours'], 2)
        self.assertEqual([j['after'] for j in jobs], [[], []])


if __name__ == '__main__':
    unittest.main()


class OriginalPlayerFolder(unittest.TestCase):
    def test_links_follow_the_install_named(self):
        # A lane's folder made for one install, then used with another (a copy whose Steam
        # double differs): every link names the second install's files.
        with tempfile.TemporaryDirectory() as d:
            installs = []
            for name in ('a', 'b'):
                install = os.path.join(d, name)
                os.makedirs(os.path.join(install, 'OxygenNotIncluded_Data'))
                with open(os.path.join(install, 'OxygenNotIncluded'), 'w') as f:
                    f.write('player ' + name)
                installs.append(install)
            root = os.path.join(d, 'out')
            first = e2e.original_player(os.path.join(installs[0], 'OxygenNotIncluded'), 3, root=root)
            data = os.path.join(root, '.original-3', 'OxygenNotIncluded_Data')
            self.assertEqual(os.readlink(data), os.path.join(installs[0], 'OxygenNotIncluded_Data'))
            second = e2e.original_player(os.path.join(installs[1], 'OxygenNotIncluded'), 3, root=root)
            self.assertEqual(first, second)
            self.assertEqual(os.readlink(data), os.path.join(installs[1], 'OxygenNotIncluded_Data'))
            with open(second) as f:
                self.assertEqual(f.read(), 'player b')
