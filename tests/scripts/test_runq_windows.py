"""runq's Windows isolation and legacy bridge, without Windows: the job environment and folders,
the volume settings, the rules a game's command line must keep, cleanup through a fake backend
that records calls, and the shim's take of the legacy locks."""

import fcntl, json, os, subprocess, tempfile, unittest

import helpers  # noqa: F401
from runqlib import cleanup, config, legacy, shim, winenv

CFG = config.DEFAULTS
TEMP = 'C:\\Users\\u\\AppData\\Local\\Temp'


def job(kind='windows', id='j0000042', **extra):
    d = {'id': id, 'dir': f'/tmp/runs/{id}', 'owner': 'o', 'level': 'study', 'kind': kind, 'needs': config.kind_needs(CFG, kind)}
    d.update(extra)
    return d


class Environment(unittest.TestCase):
    def test_windows_job_gets_its_own_folders_and_rules(self):
        caller = {'PATH': '/usr/bin', 'WSLENV': 'FOO/p', 'ONI_WIN_MONITOR': '1', 'DISPLAY': ':0'}
        base = winenv.filter_env(caller, CFG['env_allow'])
        j = job()
        env = winenv.job_env(base, j, {'lanes': [], 'needs': j['needs']}, CFG, TEMP)
        self.assertEqual(env['ONI_RUN_HOME'], TEMP + '\\oni-runq\\j0000042\\home')
        self.assertEqual(env['ONI_WIN_STAGE'], TEMP + '\\oni-runq\\j0000042\\stage')
        self.assertEqual(env['ONI_WIN_MONITOR'], '2')  # never monitor 1, whatever the caller set
        self.assertEqual(env['ONI_HOST_NO_DIALOG'], '1')
        wslenv = env['WSLENV'].split(':')
        self.assertEqual(wslenv[0], 'FOO/p')
        for name in ('ONI_HOST_NO_DIALOG', 'ONI_RUN_HOME', 'ONI_WIN_STAGE', 'ONI_WIN_MONITOR'):
            self.assertIn(name, wslenv)
        self.assertNotIn('DISPLAY', env)
        self.assertEqual(env['SDL_AUDIODRIVER'], 'dummy')

    def test_callers_run_home_is_kept_and_matched(self):
        j = job()
        env = winenv.job_env({'ONI_RUN_HOME': 'C:\\runs\\gc79-home'}, j, {'lanes': [], 'needs': j['needs']}, CFG, TEMP)
        self.assertEqual(env['ONI_RUN_HOME'], 'C:\\runs\\gc79-home')
        self.assertEqual(winenv.run_home_matches('j0000042', env), ['\\oni-runq\\j0000042\\', 'C:\\runs\\gc79-home'])

    def test_linux_job_has_no_windows_folders(self):
        j = job('linux-player')
        env = winenv.job_env({}, j, {'lanes': [3], 'needs': j['needs']}, CFG, TEMP)
        self.assertNotIn('ONI_RUN_HOME', env)
        self.assertEqual(env['RUNQ_LANES'], '3')
        self.assertEqual((env['E2E_MEMORY_GB'], env['E2E_MAX_PLAYERS']), ('6', '1'))

    def test_wsl_path(self):
        self.assertEqual(winenv.wsl_path('C:\\Users\\u\\x'), '/mnt/c/Users/u/x')
        with self.assertRaises(ValueError):
            winenv.wsl_path('/home/u')


class Interop(unittest.TestCase):
    """Jobs start Windows processes through a WSL session's interop socket that does not give
    them PreferSystem32Images (init's does: Doorstop's winhttp.dll then never loads)."""

    def test_sockets_of_live_relays_longest_lived_first_wsls_own_last(self):
        with tempfile.TemporaryDirectory() as d:
            sleeper = subprocess.Popen(['sleep', '30'])
            try:
                for pid in (1, os.getpid(), sleeper.pid, 999999999):
                    open(os.path.join(d, f'{pid}_interop'), 'w').close()
                open(os.path.join(d, 'other'), 'w').close()
                found = [pid for _, pid in winenv.interop_sockets(d)]
                self.assertEqual(found, [os.getpid(), sleeper.pid, 1])  # the dead relay's is left out
            finally:
                sleeper.kill()
                sleeper.wait()

    def test_choose_skips_sockets_that_prefer_system32(self):
        fake = winenv.FakeWindows()
        fake.policy = {'/run/WSL/1_interop': True, '/run/WSL/750_interop': True, '/run/WSL/900_interop': False}
        sockets = [('/run/WSL/750_interop', 750), ('/run/WSL/900_interop', 900), ('/run/WSL/1_interop', 1)]
        self.assertEqual(winenv.choose_interop(sockets, fake.prefers_system32), '/run/WSL/900_interop')
        fake.policy = {'/run/WSL/750_interop': None}
        self.assertIsNone(winenv.choose_interop(sockets, fake.prefers_system32))


class Volume(unittest.TestCase):
    def prefs(self, text):
        with tempfile.TemporaryDirectory() as home:
            path = os.path.join(home, 'Documents', 'Klei', 'OxygenNotIncluded', 'kplayerprefs.yaml')
            if text is not None:
                os.makedirs(os.path.dirname(path))
                with open(path, 'w') as f:
                    f.write(text)
            winenv.mute_prefs(home)
            with open(path) as f:
                return f.read()

    def test_new_prefs(self):
        out = self.prefs(None)
        for v in winenv.VOLUMES:
            self.assertIn(f'  {v}: 0', out)

    def test_empty_floats(self):
        out = self.prefs('strings:\n  AutoResumeSaveFile: x\nints:\n  FullScreen: 0\nfloats: {}\n')
        self.assertIn('AutoResumeSaveFile: x', out)
        self.assertIn('FullScreen: 0', out)
        self.assertIn('  Volume_Master: 0', out)
        self.assertNotIn('{}\n', out.split('floats:')[1])

    def test_existing_volumes_replaced(self):
        out = self.prefs('strings: {}\nfloats:\n  Volume_Master: 0.8\n  UIScale: 1\nints: {}\n')
        self.assertNotIn('0.8', out)
        self.assertIn('  UIScale: 1', out)
        self.assertEqual(out.count('Volume_Master'), 1)
        self.assertIn('ints: {}', out)


class Rules(unittest.TestCase):
    def test_violations(self):
        good = 'C:\\x\\OniMoono.exe --game G --run-home H -screen-fullscreen 0 -monitor 2 --no-focus'
        self.assertEqual(winenv.violations(good, 2), [])
        self.assertTrue(winenv.violations(good.replace(' --no-focus', ''), 2))
        self.assertTrue(winenv.violations(good.replace('-monitor 2', '-monitor 1'), 2))
        self.assertTrue(winenv.violations(good + ' ' + winenv.BACKGROUND, 2))
        # The original has no --no-focus of ours.
        self.assertEqual(winenv.violations('E:\\c\\BrutalNotIncluded.exe -logFile H\\Player.log -screen-fullscreen 0 -monitor 2', 2), [])

    def test_classification(self):
        self.assertTrue(winenv.legacy_test_game('OniMoono.exe --run-home C:\\T\\oni-brutal\\run-home'))
        self.assertTrue(winenv.legacy_test_game('X.exe -logFile C:\\h\\Player.log'))
        self.assertFalse(winenv.legacy_test_game('"C:\\Program Files (x86)\\Steam\\steamapps\\common\\OxygenNotIncluded\\OxygenNotIncluded.exe"'))
        self.assertFalse(winenv.legacy_test_game('OniMoono.exe --run-home C:\\T\\oni-runq\\j0000001\\home'))

    def test_short_matches_refused(self):
        w = winenv.Windows()
        for bad in ('', 'OniMoono', '.exe'):
            with self.assertRaises(ValueError):
                w.kill(bad)
            with self.assertRaises(ValueError):
                w.boost(bad)

    def test_remove_only_job_folders(self):
        with self.assertRaises(ValueError):
            winenv.Windows().remove('C:\\Users\\u\\Documents')


class Cleanup(unittest.TestCase):
    def test_windows_cleanup_kills_by_run_home_then_removes(self):
        with tempfile.TemporaryDirectory() as d:
            j = job(dir=d, win_temp=TEMP, windows_used=True)
            with open(os.path.join(d, 'env.json'), 'w') as f:
                json.dump({'ONI_RUN_HOME': TEMP + '\\oni-runq\\j0000042\\home'}, f)
            fake = winenv.FakeWindows(TEMP)
            fake.procs = [
                {'pid': 1, 'name': 'OniMoono.exe', 'cmdline': 'OniMoono.exe --run-home ' + TEMP + '\\oni-runq\\j0000042\\home'},
                {'pid': 2, 'name': 'OniMoono.exe', 'cmdline': 'OniMoono.exe'},  # the user's
            ]
            lines = []
            cleanup.windows_cleanup(j, fake, lines.append)
            self.assertEqual(fake.calls, [('kill', '\\oni-runq\\j0000042\\'), ('remove', TEMP + '\\oni-runq\\j0000042')])
            self.assertEqual([p['pid'] for p in fake.procs], [2])

    def test_keep_home(self):
        with tempfile.TemporaryDirectory() as d:
            j = job(dir=d, win_temp=TEMP, windows_used=True, keep_home=1)
            fake = winenv.FakeWindows(TEMP)
            cleanup.windows_cleanup(j, fake, lambda s: None)
            self.assertEqual([c[0] for c in fake.calls], ['kill'])

    def test_main_clears_the_shims_legacy_entries(self):
        with tempfile.TemporaryDirectory() as d:
            locks = os.path.join(d, 'locks')
            os.makedirs(locks)
            sleeper = subprocess.Popen(['sleep', '30'])
            try:
                start = legacy.process_start(sleeper.pid)
                with open(os.path.join(locks, 'perf.wanted'), 'w') as f:
                    f.write(f'{sleeper.pid}\n')
                with open(os.path.join(locks, 'players.json'), 'w') as f:
                    json.dump(
                        [
                            {'pid': sleeper.pid, 'start': start, 'gb': 6, 'label': 'runq j1'},
                            {'pid': os.getpid(), 'start': legacy.process_start(os.getpid()), 'gb': 3, 'label': 'other'},
                        ],
                        f,
                    )
                for name, data in (('job.json', {'id': 'j1', 'dir': d}), ('grant.json', {'lock_dir': locks}), ('shim.json', {'pid': sleeper.pid})):
                    with open(os.path.join(d, name), 'w') as f:
                        json.dump(data, f)
                cleanup.main(d, environ={'SERVICE_RESULT': 'success', 'EXIT_CODE': 'exited', 'EXIT_STATUS': '0'})
                self.assertFalse(os.path.exists(os.path.join(locks, 'perf.wanted')))
                self.assertEqual([e['label'] for e in legacy.read_ledger(locks)], ['other'])
                with open(os.path.join(d, 'cleanup.json')) as f:
                    self.assertEqual(json.load(f)['exit_status'], '0')
            finally:
                sleeper.kill()
                sleeper.wait()


class Bridge(unittest.TestCase):
    def test_holds_take_in_order_and_lose_races_whole(self):
        with tempfile.TemporaryDirectory() as locks:
            h = shim.Holds({'id': 'j7', 'cwd': '/'}, locks)
            h.take({'needs': {'perf': 1, 'windows': 1, 'players': 1, 'mem': 6}, 'lanes': [5], 'lane_locks': 'shim'})
            self.assertEqual(legacy.wanted_pid(locks), os.getpid())
            self.assertEqual(len(legacy.read_ledger(locks)), 1)
            self.assertEqual(len(h.fds), 3)
            h.release()
            self.assertIsNone(legacy.wanted_pid(locks))
            self.assertEqual(legacy.read_ledger(locks), [])
            # An old script holds windows-game.lock: nothing stays held.
            other = open(os.path.join(locks, 'windows-game.lock'), 'a')
            fcntl.flock(other, fcntl.LOCK_EX)
            with self.assertRaises(legacy.LostRace):
                h.take({'needs': {'perf': 1, 'windows': 1}, 'lanes': []})
            self.assertEqual(h.fds, [])
            self.assertIsNone(legacy.wanted_pid(locks))
            f = open(os.path.join(locks, 'perf.lock'), 'a')
            fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)  # free again
            f.close()
            other.close()

    def test_perf_wanted_of_a_live_old_script_is_a_lost_race(self):
        with tempfile.TemporaryDirectory() as locks:
            sleeper = subprocess.Popen(['sleep', '30'])
            try:
                with open(os.path.join(locks, 'perf.wanted'), 'w') as f:
                    f.write(f'{sleeper.pid}\n')
                h = shim.Holds({'id': 'j8'}, locks)
                with self.assertRaises(legacy.LostRace):
                    h.take({'needs': {'perf': 1}, 'lanes': []})
                # The daemon's own drain claim is replaced.
                h.take({'needs': {'perf': 1}, 'lanes': [], 'daemon_pid': sleeper.pid})
                self.assertEqual(legacy.wanted_pid(locks), os.getpid())
                h.release()
            finally:
                sleeper.kill()
                sleeper.wait()

    def test_scan_counts_occupants_outside_runq(self):
        with tempfile.TemporaryDirectory() as locks:
            cfg = config.merge(CFG, {'legacy_perf_patterns': [r'^sleep 4242'], 'capacity': {'lane_first': 101, 'lane_last': 103}})
            holder = subprocess.Popen(['flock', '-s', os.path.join(locks, 'perf.lock'), 'sleep', '30'])
            lane = subprocess.Popen(['flock', os.path.join(locks, 'lane-102.lock'), 'sleep', '30'])
            win = subprocess.Popen(['flock', os.path.join(locks, 'windows-game.lock'), 'sleep', '30'])
            try:
                import time

                time.sleep(0.5)
                procs = [(1, 'sleep 4242'), (2, '/bin/bash scripts/qa.sh perf')]
                ext, free = legacy.scan(cfg, locks, procs=procs, port_busy=lambda p: False, display_busy=lambda d: False)
                self.assertEqual(ext.perf_shared, 1)
                self.assertTrue(ext.windows)
                self.assertTrue(ext.perf)  # the perf pattern
                sleeper = subprocess.Popen(['sleep', '30'])
                with open(os.path.join(locks, 'perf.wanted'), 'w') as f:
                    f.write(f'{sleeper.pid}\n')
                ext2, _ = legacy.scan(cfg, locks, procs=[], port_busy=lambda p: False, display_busy=lambda d: False)
                sleeper.kill()
                sleeper.wait()
                self.assertTrue(ext2.perf)
                self.assertGreaterEqual(ext2.load, 1)
                self.assertEqual(ext.mem, CFG['legacy_qa_gb'])  # qa.sh
                self.assertEqual(free, (101, 103))
            finally:
                for p in (holder, lane, win):
                    p.kill()
                    p.wait()


if __name__ == '__main__':
    unittest.main()
