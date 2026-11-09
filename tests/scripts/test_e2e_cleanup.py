"""e2e.py's cleanup: what a runner that died left on its lane (lane_leftovers), the teardown of a
runner's own processes, and the watcher. Fake process tables for the rules; real child processes
(sleeping Pythons, one named Xvfb) in a temporary lock directory and X folder for the kills."""

import json, os, socket, subprocess, sys, tempfile, time, unittest

import helpers  # noqa: F401
import e2e

DEAD, LIVE = '111:1000', '222:2000'
alive = lambda tag: tag == LIVE


def proc(pid, comm='OniMoono', env=None, args=None, ppid=1, start=None):
    return {'pid': pid, 'ppid': ppid, 'pgid': pid, 'start': start or pid * 10, 'comm': comm, 'args': args or [comm], 'env': env or {}}


class Rules(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.locks = os.path.join(self.temp.name, 'locks')
        self.tmp = os.path.join(self.temp.name, 'x')
        os.makedirs(os.path.join(self.tmp, '.X11-unix'))

    def tearDown(self):
        self.temp.cleanup()

    def leftovers(self, procs, record=None, scopes=(), machine=False, display=-1):
        return e2e.lane_leftovers(14, record, self.locks, procs=procs, scopes=list(scopes), alive=alive, display=display, machine=machine, tmp=self.tmp)

    def pids(self, found):
        return sorted(t['pid'] for k, t, _ in found if k == 'process')

    def test_a_dead_runners_processes_on_the_lane(self):
        lane = dict(E2E_LANE='14', E2E_LOCK_DIR=self.locks)
        procs = [
            proc(1, env=dict(lane, E2E_OWNER=DEAD)),
            proc(2, env=dict(lane, E2E_OWNER=LIVE)),  # its runner lives
            proc(3, env=dict(lane, E2E_OWNER=DEAD, E2E_LANE='4')),  # another lane
            proc(4, env=dict(lane, E2E_OWNER=DEAD, E2E_LOCK_DIR='/x')),  # another lock directory
            proc(5, comm='bash', env=dict(lane, E2E_OWNER=DEAD)),
        ]  # a wrapper: any process it started
        self.assertEqual(self.pids(self.leftovers(procs)), [1, 5])

    def test_nothing_while_the_lanes_last_runner_lives(self):
        procs = [proc(1, env=dict(E2E_LANE='14', E2E_LOCK_DIR=self.locks, E2E_OWNER=DEAD))]
        self.assertEqual(self.leftovers(procs, record={'pid': 222, 'start': 2000}), [])
        self.assertEqual(self.pids(self.leftovers(procs, record={'pid': 111, 'start': 1000})), [1])

    def test_recorded_children_by_start_time(self):
        procs = [proc(7, comm='Xvfb', start=70), proc(8, start=80)]
        record = {'pid': 111, 'start': 1000, 'children': [{'role': 'xvfb', 'pid': 7, 'start': 70}, {'role': 'player', 'pid': 8, 'start': 81}]}  # pid reused
        self.assertEqual(self.pids(self.leftovers(procs, record)), [7])

    def test_runners_from_before_owner_marks_only_in_the_machines_directory(self):
        runner = proc(50, comm='python3', args=['python3', 'scripts/e2e.py', 'x'])
        procs = [
            runner,
            proc(51, env={'E2E_LANE': '14'}),  # orphaned
            proc(52, env={'E2E_LANE': '14'}, ppid=50),  # its runner lives
            proc(53, comm='bash', env={'E2E_LANE': '14'}),
        ]  # not a player
        self.assertEqual(self.pids(self.leftovers(procs, machine=True)), [51])
        self.assertEqual(self.leftovers(procs, machine=False), [])

    def test_scopes_of_dead_runners_on_the_lane(self):
        scopes = ['oni-test-111-1000-lane14-ours-1.scope', 'oni-test-111-1000-lane1-ours-2.scope', 'oni-test-222-2000-lane14-ours-1.scope', 'run-r1234.scope']
        found = self.leftovers([], scopes=scopes)
        self.assertEqual([t for k, t, _ in found], ['oni-test-111-1000-lane14-ours-1.scope'])

    def test_the_display(self):
        def lock(pid):
            with open(os.path.join(self.tmp, '.X314-lock'), 'w') as f:
                f.write(f'{pid:10d}\n')
            open(os.path.join(self.tmp, '.X11-unix', 'X314'), 'w').close()

        files = lambda found: sorted(os.path.basename(t) for k, t, _ in found if k == 'file')
        # A dead runner's Xvfb, then its lock file and socket.
        lock(9)
        xvfb = proc(9, comm='Xvfb', args=['Xvfb', ':314', '-screen', '0'], env={'E2E_OWNER': DEAD})
        found = self.leftovers([xvfb], display=314)
        self.assertEqual((self.pids(found), files(found)), ([9], ['.X314-lock', 'X314']))
        # A live runner's: nothing.
        xvfb['env'] = {'E2E_OWNER': LIVE}
        self.assertEqual(self.leftovers([xvfb], display=314), [])
        # Another program's X server on that display (not an Xvfb started for it): nothing.
        self.assertEqual(self.leftovers([proc(9, comm='Xorg', args=['Xorg', ':314'])], display=314), [])
        # An Xvfb from before owner marks, with no runner: in the machine's directory only.
        xvfb['env'] = {}
        self.assertEqual(self.pids(self.leftovers([xvfb], display=314, machine=True)), [9])
        self.assertEqual(self.leftovers([xvfb], display=314), [])
        # A lock file whose process has gone.
        self.assertEqual(files(self.leftovers([], display=314)), ['.X314-lock', 'X314'])


def start_time_of_a_dead_process():
    """A PID:START tag of a process that has ended."""
    p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(0.3)'])
    tag = e2e.owner_tag(p.pid)
    p.wait()
    return tag


SLEEPER = 'import time, sys; time.sleep(120)'


class RealProcesses(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.locks = os.path.join(self.temp.name, 'locks')
        self.tmp = os.path.join(self.temp.name, 'x')
        os.makedirs(self.locks)
        os.makedirs(os.path.join(self.tmp, '.X11-unix'))
        self.saved = {k: os.environ.get(k) for k in ('E2E_LOCK_DIR', 'E2E_X_TMP', 'E2E_DISPLAY_BASE')}
        os.environ.update(E2E_LOCK_DIR=self.locks, E2E_X_TMP=self.tmp, E2E_DISPLAY_BASE='1000')
        self.children = []
        # A Python named Xvfb (its /proc comm), as e2e.py's Xvfbs are found.
        self.xvfb = os.path.join(self.temp.name, 'Xvfb')
        os.symlink(os.path.realpath(sys.executable), self.xvfb)

    def tearDown(self):
        for c in self.children:
            if c.poll() is None:
                c.kill()
            c.wait(10)
            if c.stdout:
                c.stdout.close()
        for k, v in self.saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
        self.temp.cleanup()

    def spawn(self, owner, lane='914', xvfb=None):
        env = dict(os.environ, E2E_OWNER=owner, E2E_LANE=lane, E2E_LOCK_DIR=self.locks)
        args = [self.xvfb, '-c', SLEEPER, xvfb] if xvfb else [sys.executable, '-c', SLEEPER]
        p = subprocess.Popen(args, env=env, start_new_session=True)
        self.children.append(p)
        deadline = time.time() + 10
        while not (e2e.read_proc(p.pid) or {}).get('env') and time.time() < deadline:
            time.sleep(0.05)  # until exec has happened
        while xvfb and e2e.read_proc(p.pid)['comm'] != 'Xvfb' and time.time() < deadline:
            time.sleep(0.05)
        return p

    def spawn_player(self, owner, report, quits=True, lane='914'):
        """A Python named OniMoono. On SIGTERM it writes to REPORT whether its X server (the
        pid it is told about later, in REPORT.xvfb) still runs, then exits 0 after 0.3 s, as our
        player quits; with QUITS false it ignores SIGTERM."""
        code = f'''
import os, signal, sys, time
def quit(*_):
    with open({report + '.xvfb'!r}) as f:
        pid = int(f.read())
    try:
        os.kill(pid, 0)
        alive = 'xvfb alive'
    except OSError:
        alive = 'xvfb gone'
    with open({report!r}, 'w') as f:
        f.write(alive)
    time.sleep(0.3)
    os._exit(0)
signal.signal(signal.SIGTERM, quit if {quits!r} else signal.SIG_IGN)
open({report + '.ready'!r}, 'w').close()
time.sleep(120)
'''
        player = os.path.join(self.temp.name, 'OniMoono')
        if not os.path.exists(player):
            os.symlink(os.path.realpath(sys.executable), player)
        env = dict(os.environ, E2E_OWNER=owner, E2E_LANE=lane, E2E_LOCK_DIR=self.locks)
        p = subprocess.Popen([player, '-c', code], env=env, start_new_session=True)
        self.children.append(p)
        deadline = time.time() + 10
        while not os.path.exists(report + '.ready') and time.time() < deadline:
            time.sleep(0.05)
        self.assertEqual(e2e.read_proc(p.pid)['comm'], 'OniMoono')
        return p

    def test_a_teardown_quits_players_before_killing_their_x_servers(self):
        tag = '1:2'
        report = os.path.join(self.temp.name, 'quit.txt')
        xvfb = self.spawn(tag, xvfb=':1915')
        player = self.spawn_player(tag, report)
        with open(report + '.xvfb', 'w') as f:
            f.write(str(xvfb.pid))
        e2e.teardown_owner(tag)
        self.assertEqual(player.wait(10), 0)  # it quit by itself
        with open(report) as f:
            self.assertEqual(f.read(), 'xvfb alive')
        self.assertTrue(self.gone(xvfb))

    def test_a_player_that_does_not_quit_is_killed_after_its_grace(self):
        from unittest import mock

        report = os.path.join(self.temp.name, 'quit.txt')
        player = self.spawn_player(e2e.owner_tag(), report, quits=False)
        with mock.patch.dict(os.environ, E2E_QUIT_GRACE='0.5'):
            began = time.time()
            e2e.kill_verified([e2e.read_proc(player.pid)], grace=0.2)
        self.assertEqual(player.wait(10), -9)
        self.assertGreaterEqual(time.time() - began, 0.5)

    def test_quit_grace(self):
        from unittest import mock

        for value, grace in ((None, e2e.QUIT_GRACE), ('soon', e2e.QUIT_GRACE), ('2.5', 2.5), ('0', 0)):
            with mock.patch.dict(os.environ, {} if value is None else {'E2E_QUIT_GRACE': value}):
                if value is None:
                    os.environ.pop('E2E_QUIT_GRACE', None)
                self.assertEqual(e2e.quit_grace(), grace)

    def gone(self, p, timeout=10):
        try:
            p.wait(timeout)
            return True
        except subprocess.TimeoutExpired:
            return False

    def test_taking_a_dead_runners_lane_cleans_only_its_leftovers(self):
        dead = start_time_of_a_dead_process()
        player = self.spawn(dead)
        xvfb = self.spawn(dead, xvfb=':1914')
        mine = self.spawn(e2e.owner_tag())  # a live runner's (this test's) on the same lane
        other = self.spawn(dead, lane='915')  # the dead runner's on another lane
        with open(os.path.join(self.tmp, '.X1914-lock'), 'w') as f:
            f.write(f'{xvfb.pid:10d}\n')
        open(os.path.join(self.tmp, '.X11-unix', 'X1914'), 'w').close()
        pid, start = dead.split(':')
        with open(os.path.join(self.locks, 'lane-914.lock'), 'w') as f:
            json.dump(
                {
                    'pid': int(pid),
                    'start': int(start),
                    'lane': 914,
                    'state': 'running x',
                    'children': [{'role': 'player', 'pid': player.pid, 'start': e2e.process_start(player.pid)}],
                },
                f,
            )
        self.assertEqual(
            sorted(p['pid'] for k, p, _ in e2e.lane_leftovers(914, {'pid': int(pid), 'start': int(start)}, self.locks, scopes=[]) if k == 'process'),
            sorted([player.pid, xvfb.pid]),
        )
        logged = []
        lane = e2e.try_lane(914, self.locks, busy=lambda p: False, display_busy=lambda d: False, reserved=(set(), set()), log=logged.append)
        try:
            self.assertIsNotNone(lane)
            self.assertTrue(self.gone(player) and self.gone(xvfb))
            self.assertIsNone(mine.poll())
            self.assertIsNone(other.poll())
            self.assertFalse(os.path.exists(os.path.join(self.tmp, '.X1914-lock')))
            self.assertFalse(os.path.exists(os.path.join(self.tmp, '.X11-unix', 'X1914')))
            self.assertTrue(any('has gone' in line for line in logged))
            with open(os.path.join(self.locks, 'lane-914.lock')) as f:
                record = json.load(f)
            self.assertEqual((record['pid'], record['children']), (os.getpid(), []))
            # The lane's record follows what it runs.
            lane.track('player', mine.pid, 'oni-test-1-2-lane914-ours-1.scope')
            with open(os.path.join(self.locks, 'lane-914.lock')) as f:
                self.assertEqual(json.load(f)['children'][0]['pid'], mine.pid)
            lane.untrack(mine.pid)
        finally:
            if lane:
                lane.release()

    def test_a_live_xvfb_on_the_display_is_left_alone(self):
        xvfb = self.spawn(e2e.owner_tag(), xvfb=':1914')
        with open(os.path.join(self.tmp, '.X1914-lock'), 'w') as f:
            f.write(f'{xvfb.pid:10d}\n')
        self.assertEqual(e2e.lane_leftovers(914, {}, self.locks, scopes=[]), [])
        e2e.clean_leftovers([('file', os.path.join(self.tmp, '.X1914-lock'), 'stale X lock')], log=lambda s: None)
        self.assertTrue(os.path.exists(os.path.join(self.tmp, '.X1914-lock')))  # checked again before removal

    def test_a_socket_a_server_answers_on_is_kept(self):
        path = os.path.join(self.tmp, '.X11-unix', 'X1914')
        with socket.socket(socket.AF_UNIX) as server:
            server.bind(path)
            server.listen(1)
            e2e.clean_leftovers([('file', path, 'stale X socket')], log=lambda s: None)
            self.assertTrue(os.path.exists(path))

    def test_a_kill_checks_the_start_time(self):
        p = self.spawn(e2e.owner_tag())
        e2e.kill_verified([{'pid': p.pid, 'start': e2e.process_start(p.pid) + 1}], grace=0.2)  # another process
        self.assertIsNone(p.poll())
        e2e.kill_verified([e2e.read_proc(p.pid)], grace=0.2)
        self.assertTrue(self.gone(p))

    def test_teardown_ends_a_runners_processes_and_the_watcher_does_when_it_dies(self):
        # A runner (a child Python holding a lane and starting a player) killed with SIGKILL: its
        # watcher ends the player and clears the lane's record.
        code = f'''
import os, subprocess, sys, time
sys.path.insert(0, {os.path.dirname(e2e.__file__)!r})
import e2e
e2e.start_reaper()
lane = e2e.try_lane(916, busy=lambda p: False, display_busy=lambda d: False, reserved=(set(), set()))
env = dict(os.environ, **e2e.child_marks(lane))
p = subprocess.Popen([sys.executable, '-c', {SLEEPER!r}], env=env, start_new_session=True)
lane.track('player', p.pid)
print(p.pid, flush=True)
time.sleep(120)
'''
        runner = subprocess.Popen([sys.executable, '-c', code], stdout=subprocess.PIPE, text=True)
        self.children.append(runner)
        player = int(runner.stdout.readline())
        self.assertIsNotNone(e2e.process_start(player))
        runner.kill()
        runner.wait(10)
        deadline = time.time() + 20
        while e2e.process_start(player) is not None and time.time() < deadline:
            time.sleep(0.2)
        self.assertIsNone(e2e.process_start(player))
        deadline = time.time() + 10
        while time.time() < deadline:
            with open(os.path.join(self.locks, 'lane-916.lock')) as f:
                record = json.load(f)
            if record.get('state') == 'ended':
                break
            time.sleep(0.2)
        self.assertEqual((record.get('state'), record.get('children', [])), ('ended', []))

    def test_teardown_on_sigterm(self):
        code = f'''
import os, subprocess, sys, time
sys.path.insert(0, {os.path.dirname(e2e.__file__)!r})
import e2e
e2e.install_teardown()
p = subprocess.Popen([sys.executable, '-c', {SLEEPER!r}], env=dict(os.environ, **e2e.child_marks(None)),
                     start_new_session=True)
print(p.pid, flush=True)
time.sleep(120)
'''
        runner = subprocess.Popen([sys.executable, '-c', code], stdout=subprocess.PIPE, text=True)
        self.children.append(runner)
        player = int(runner.stdout.readline())
        runner.terminate()
        self.assertEqual(runner.wait(20), 128 + 15)
        self.assertIsNone(e2e.process_start(player))


class SignalTeardown(unittest.TestCase):
    def test_teardown_runs_when_the_signal_lands_inside_print(self):
        # A signal that arrives while the main thread is inside print() makes a print from the
        # handler raise RuntimeError (reentrant call into the buffered stdout). The handler must
        # still tear down and exit 128 + signum: here every print raises, as it would then.
        import builtins, signal as sig
        from unittest import mock

        saved = {s: sig.getsignal(s) for s in (sig.SIGTERM, sig.SIGINT, sig.SIGHUP)}
        try:
            e2e.install_teardown()
            handler = sig.getsignal(sig.SIGTERM)
            exits, torn = [], []

            def reentrant(*a, **k):
                raise RuntimeError("reentrant call inside <_io.BufferedWriter name='<stdout>'>")

            with (
                mock.patch.object(builtins, 'print', reentrant),
                mock.patch.object(e2e, 'teardown_all', lambda log=None: torn.append(log)),
                mock.patch.object(e2e.os, '_exit', exits.append),
            ):
                handler(sig.SIGTERM, None)
            self.assertEqual((len(torn), exits), (1, [128 + sig.SIGTERM]))
        finally:
            for s, h in saved.items():
                sig.signal(s, h)


class Status(unittest.TestCase):
    def test_compress_numbers(self):
        self.assertEqual(e2e.compress_numbers([5, 1, 2, 3, 9, 10]), '1-3, 5, 9-10')
        self.assertEqual(e2e.compress_numbers([]), '')

    def test_scope_names(self):
        unit = e2e.scope_name('lane3-ours-1', '12:34')
        self.assertEqual(unit, 'oni-test-12-34-lane3-ours-1.scope')
        self.assertEqual(e2e.scope_owner(unit), ('12:34', 'lane3-ours-1'))
        self.assertIsNone(e2e.scope_owner('run-r12.scope'))

    def test_status_shows_a_dead_runners_lane(self):
        with tempfile.TemporaryDirectory() as locks:
            dead = start_time_of_a_dead_process()
            pid, start = map(int, dead.split(':'))
            with open(os.path.join(locks, 'lane-3.lock'), 'w') as f:
                json.dump({'pid': pid, 'start': start, 'root': '/w', 'since': 'then', 'state': 'running x'}, f)
            fake = proc(4242, env={'E2E_OWNER': dead, 'E2E_LANE': '3', 'E2E_LOCK_DIR': locks}, start=1)
            lines = e2e.status(locks, procs=[fake])
            text = '\n'.join(lines)
            self.assertIn(f'lane 3 (:8830, :8831, {e2e.lane_display(3)}): FREE but busy; last held by pid {pid} (gone)', text)
            self.assertIn('would clean: pid 4242', text)


if __name__ == '__main__':
    unittest.main()
