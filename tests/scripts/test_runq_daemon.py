"""runq's daemon, CLI, shim and cleanup, end to end: a daemon on a temporary state folder, its
jobs as systemd user units under a test prefix in a test slice, running true, false, sleep and a
job that ignores SIGTERM. Skipped without a systemd user manager. The Windows side is off here
(RUNQ_WINDOWS=off); test_runq_windows.py covers it with a fake backend."""

import json, os, shutil, signal, subprocess, sys, tempfile, time, unittest

import helpers

RUNQ = os.path.join(helpers.SCRIPTS, 'runq')
PY = sys.executable or '/usr/bin/python3'


def systemd_ok():
    try:
        return subprocess.run(['systemctl', '--user', 'show-environment'], capture_output=True, timeout=10).returncode == 0 and shutil.which('systemd-run')
    except (OSError, subprocess.TimeoutExpired):
        return False


REAL_SOCKET_BEFORE = os.path.exists(os.path.join(os.path.expanduser('~'), '.local', 'state', 'oni-runq', 'runq.sock'))

CONFIG = """
legacy_perf_patterns = []
legacy_qa_gb = 0
quiet_cores = 1000.0
stop_grace_seconds = 3
unit_check_seconds = 1
scan_seconds = 1
idle_flag_minutes = 0.03
idle_stop_minutes = 0.08
[capacity]
lane_first = 101
lane_last = 104
"""


class Harness:
    count = 0

    def __init__(self, extra=''):
        Harness.count += 1
        self.tmp = tempfile.mkdtemp(prefix='rq', dir='/tmp')
        self.prefix = f'runqt{os.getpid()}n{Harness.count}'
        with open(os.path.join(self.tmp, 'config.toml'), 'w') as f:
            f.write(CONFIG + extra)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith(('RUNQ', 'E2E_'))}
        self.env.update(
            RUNQ_STATE_DIR=os.path.join(self.tmp, 'state'),
            RUNQ_RUNS_DIR=os.path.join(self.tmp, 'runs'),
            RUNQ_SOCKET=os.path.join(self.tmp, 's'),
            E2E_LOCK_DIR=os.path.join(self.tmp, 'locks'),
            RUNQ_UNIT_PREFIX=self.prefix,
            RUNQ_SLICE='oni-runq-test.slice',
            RUNQ_WINDOWS='off',
            RUNQ_CONFIG=os.path.join(self.tmp, 'config.toml'),
            RUNQ_OWNER='tester',
        )
        os.makedirs(self.env['E2E_LOCK_DIR'])
        self.daemon = None

    def start(self):
        if getattr(self, "log", None):
            self.log.close()
        self.log = open(os.path.join(self.tmp, "daemon.log"), "a")
        self.daemon = subprocess.Popen([PY, '-I', RUNQ, '_daemon'], env=self.env, stdout=self.log, stderr=subprocess.STDOUT)
        deadline = time.time() + 15
        while time.time() < deadline:
            if self.runq('ping', timeout=10).returncode == 0:
                return
            time.sleep(0.2)
        raise AssertionError('the daemon did not start: ' + open(os.path.join(self.tmp, 'daemon.log')).read())

    def kill_daemon(self, sig=signal.SIGKILL):
        self.daemon.send_signal(sig)
        self.daemon.wait(10)

    def runq(self, *args, timeout=60, env=None):
        return subprocess.run([PY, RUNQ, *args], env=dict(self.env, **(env or {})), capture_output=True, text=True, timeout=timeout, cwd=self.tmp)

    def submit(self, *args):
        r = self.runq('submit', *args)
        assert r.returncode == 0, r.stderr
        return r.stdout.strip()

    def show(self, job_id):
        return json.loads(self.runq('show', job_id).stdout)

    def jobdir(self, job_id):
        return os.path.join(self.env['RUNQ_RUNS_DIR'], job_id)

    def wait_for(self, pred, timeout=30, what='a condition'):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if pred():
                return
            time.sleep(0.2)
        raise AssertionError(f'timed out waiting for {what}; daemon log:\n' + open(os.path.join(self.tmp, 'daemon.log')).read()[-3000:])

    def close(self):
        if self.daemon and self.daemon.poll() is None:
            self.daemon.terminate()
            self.daemon.wait(10)
        subprocess.run(['systemctl', '--user', 'stop', f'{self.prefix}-*'], capture_output=True)
        subprocess.run(['systemctl', '--user', 'reset-failed', f'{self.prefix}-*'], capture_output=True)
        if getattr(self, 'log', None):
            self.log.close()
        shutil.rmtree(self.tmp, ignore_errors=True)


def no_real_daemon(test):
    real = os.path.join(os.path.expanduser('~'), '.local', 'state', 'oni-runq', 'runq.sock')
    test.assertFalse(not REAL_SOCKET_BEFORE and os.path.exists(real), 'a test reached for the real daemon')


@unittest.skipUnless(systemd_ok(), 'needs a systemd user manager')
class Daemon(unittest.TestCase):
    """One daemon for these tests; each uses owners of its own."""

    @classmethod
    def setUpClass(cls):
        cls.h = Harness()
        cls.h.start()

    @classmethod
    def tearDownClass(cls):
        cls.h.close()

    def setUp(self):
        self.h.env['RUNQ_OWNER'] = self.id().rsplit('.', 1)[-1][5:30]

    def tearDown(self):
        no_real_daemon(self)

    def test_run_returns_the_exit_code_and_streams_output(self):
        r = self.h.runq('run', '--kind', 'light', '--', 'sh', '-c', 'echo out-line; echo err-line >&2; exit 3')
        self.assertEqual(r.returncode, 3, r.stderr)
        self.assertIn('out-line', r.stdout)
        self.assertIn('err-line', r.stdout)
        self.assertEqual(self.h.runq('run', '--kind', 'light', '--', 'true').returncode, 0)
        self.assertEqual(self.h.runq('run', '--kind', 'light', '--', 'false').returncode, 1)

    def test_environment(self):
        env = {'DISPLAY': ':0', 'SECRET_THING': 'x', 'KEEP_ME': 'y', 'ONI_WIN_MONITOR': '1', 'WSLENV': 'FOO'}
        r = self.h.runq('run', '--kind', 'light', '--env', 'KEEP_ME', '--', 'sh', '-c', 'env | sort', env=env)
        out = dict(line.split('=', 1) for line in r.stdout.splitlines() if '=' in line)
        self.assertNotIn('DISPLAY', out)
        self.assertNotIn('SECRET_THING', out)
        self.assertEqual(out['KEEP_ME'], 'y')
        self.assertEqual(out['ONI_HOST_NO_DIALOG'], '1')
        self.assertIn('ONI_HOST_NO_DIALOG', out['WSLENV'].split(':'))
        self.assertTrue(out['WSLENV'].startswith('FOO'))
        self.assertEqual(out['ONI_CONTAINED'], '1')
        self.assertEqual(out['SDL_AUDIODRIVER'], 'dummy')
        self.assertIn('nonexistent', out['PULSE_SERVER'])
        self.assertEqual(out['RUNQ_LEVEL'], 'study')
        self.assertTrue(out['RUNQ_OUT'].endswith('/out'))

    def test_cancel_kills_the_whole_tree(self):
        job = self.h.submit('--kind', 'light', '--', 'sh', '-c', 'sleep 300 & echo $! > "$RUNQ_OUT/pid"; sleep 300')
        pidfile = os.path.join(self.h.jobdir(job), 'out', 'pid')
        self.h.wait_for(lambda: os.path.exists(pidfile) and open(pidfile).read().strip(), what='the job to start')
        pid = int(open(pidfile).read())
        self.assertEqual(self.h.runq('cancel', job).returncode, 0)
        self.assertEqual(self.h.runq('wait', job).returncode, 137)
        self.assertFalse(os.path.exists(f'/proc/{pid}'), 'a child outlived its job')
        self.assertEqual(self.h.show(job)['reason'], 'cancelled')

    def test_cancel_a_queued_job(self):
        a = self.h.submit('--owner', 'o1', '--kind', 'windows', '--', 'true')  # no Windows backend: waits
        time.sleep(1)
        self.assertIn('Windows', self.h.show(a)['waits'] or '')
        self.h.runq('cancel', a)
        self.assertEqual(self.h.show(a)['state'], 'cancelled')

    def test_timeout(self):
        t = time.time()
        r = self.h.runq('run', '--kind', 'light', '--timeout', '2s', '--', 'sleep', '60')
        self.assertEqual(r.returncode, 124, r.stderr)
        self.assertLess(time.time() - t, 20)

    def test_sigterm_ignored_then_killed(self):
        job = self.h.submit('--kind', 'light', '--', 'sh', '-c', "trap '' TERM; echo ready > \"$RUNQ_OUT/r\"; while :; do sleep 1; done")
        self.h.wait_for(lambda: os.path.exists(os.path.join(self.h.jobdir(job), 'out', 'r')), what='the job to start')
        t = time.time()
        self.h.runq('cancel', job)
        self.assertEqual(self.h.runq('wait', job, timeout=60).returncode, 137)
        self.assertGreaterEqual(time.time() - t, 2.5)  # stop_grace_seconds, then SIGKILL

    def test_idle_holder_is_stopped(self):
        job = self.h.submit('--kind', 'linux-perf', '--', 'sleep', '300')
        r = self.h.runq('wait', job, timeout=90)
        self.assertEqual(r.returncode, 137)
        self.assertEqual(self.h.show(job)['reason'], 'idle-held')

    def test_shim_killed(self):
        job = self.h.submit('--kind', 'light', '--', 'sleep', '300')
        shim_json = os.path.join(self.h.jobdir(job), 'shim.json')

        def read():
            with open(shim_json) as f:
                return f.read()

        self.h.wait_for(lambda: os.path.exists(shim_json) and 'child' in read(), what='the shim')
        os.kill(json.loads(read())['pid'], signal.SIGKILL)
        self.assertEqual(self.h.runq('wait', job).returncode, 137)

    def test_legacy_flock_holder_blocks_then_frees(self):
        lock = os.path.join(self.h.env['E2E_LOCK_DIR'], 'perf.lock')
        holder = subprocess.Popen(['flock', '-x', lock, 'sleep', '4'])
        try:
            time.sleep(0.5)
            job = self.h.submit('--kind', 'linux-perf', '--', 'sh', '-c', 'cat "$E2E_LOCK_DIR/perf.wanted"')
            time.sleep(1.5)
            d = self.h.show(job)
            self.assertEqual(d['state'], 'queued')
            st = json.loads(self.h.runq('status', '--json').stdout)
            self.assertTrue(any('perf.lock held exclusively' in x for x in st['external']), st['external'])
        finally:
            holder.wait(10)
        self.assertEqual(self.h.runq('wait', job).returncode, 0)
        # While it ran, perf.wanted named its shim (a runq unit), and it is gone now.
        self.assertFalse(os.path.exists(os.path.join(self.h.env['E2E_LOCK_DIR'], 'perf.wanted')))

    def test_job_takes_the_legacy_locks(self):
        script = (
            'import fcntl, json, os, sys\n'
            'd = os.environ["E2E_LOCK_DIR"]\n'
            'f = open(os.path.join(d, "perf.lock"), "a")\n'
            'try:\n    fcntl.flock(f, fcntl.LOCK_SH | fcntl.LOCK_NB); print("perf.lock free")\n'
            'except OSError:\n    print("perf.lock held")\n'
            'print("players", len(json.load(open(os.path.join(d, "players.json")))))\n'
            'print("lanes", os.environ["RUNQ_LANES"])\n'
            'g = open(os.path.join(d, "lane-" + os.environ["RUNQ_LANES"].split(",")[0] + ".lock"), "a")\n'
            'try:\n    fcntl.flock(g, fcntl.LOCK_EX | fcntl.LOCK_NB); print("lane free")\n'
            'except OSError:\n    print("lane held")\n'
        )
        r = self.h.runq('run', '--kind', 'linux-perf', '--needs', 'lane=1', '--', PY, '-c', script)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn('perf.lock held', r.stdout)
        self.assertIn('players 1', r.stdout)
        self.assertIn('lanes 101', r.stdout)
        self.assertIn('lane held', r.stdout)
        with open(os.path.join(self.h.env['E2E_LOCK_DIR'], 'players.json')) as f:
            self.assertEqual([e for e in json.load(f) if os.path.exists(f'/proc/{e["pid"]}')], [])

    def test_phase(self):
        script = (
            'set -e\n'
            'test ! -e "$E2E_LOCK_DIR/perf.wanted" && echo before-none\n'
            f'"{PY}" "{RUNQ}" phase --kind qa-perf > "$RUNQ_OUT/phase.env"\n'
            'cat "$RUNQ_OUT/phase.env"\n'
            'shim=$(sed -n \'s/.*"pid": \\([0-9]*\\).*/\\1/p\' "$RUNQ_DIR/shim.json")\n'
            '[ "$(cat "$E2E_LOCK_DIR/perf.wanted")" = "$shim" ] && echo wanted-is-shim\n'
            f'"{PY}" "{RUNQ}" phase --kind light\n'
            'test ! -e "$E2E_LOCK_DIR/perf.wanted" && echo after-none\n'
        )
        r = self.h.runq('run', '--kind', 'qa', '--', 'bash', '-c', script, timeout=90)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        for word in ('before-none', 'E2E_MAX_PLAYERS=1', 'wanted-is-shim', 'after-none'):
            self.assertIn(word, r.stdout)

    def test_owner_cap_and_levels(self):
        a = self.h.submit('--owner', 'busy', '--kind', 'light', '--', 'sleep', '30')
        b = self.h.submit('--owner', 'busy', '--kind', 'light', '--', 'sleep', '30')
        c = self.h.submit('--owner', 'busy', '--kind', 'light', '--', 'true')
        self.h.wait_for(lambda: self.h.show(b)['state'] == 'running', what='two jobs running')
        time.sleep(1)
        self.assertEqual(self.h.show(c)['state'], 'queued')
        self.assertIn('cap 2', self.h.show(c)['waits'])
        self.h.runq('cancel', a, b)
        self.assertEqual(self.h.runq('wait', c).returncode, 0)
        r = self.h.runq('submit', '--level', 'issue', '--kind', 'light', '--', 'true')
        self.assertEqual(r.returncode, 0)
        with open(os.path.join(self.h.env['RUNQ_STATE_DIR'], 'levels.log')) as f:
            self.assertIn('level issue', f.read())
        self.assertEqual(self.h.runq('submit', '--level', 'urgent', '--', 'true').returncode, 75)

    def test_slot_hours_cap(self):
        r = self.h.runq('submit', '--kind', 'windows', '--expect', '5h', '--timeout', '6h', '--', 'true')
        self.assertEqual(r.returncode, 75)
        self.assertIn('--budget-ok', r.stderr)
        r = self.h.runq('submit', '--kind', 'windows', '--expect', '5h', '--timeout', '6h', '--budget-ok', 'the user said so', '--', 'true')
        self.assertEqual(r.returncode, 0, r.stderr)
        self.h.runq('cancel', r.stdout.strip())

    def test_after_and_script_copy(self):
        script = os.path.join(self.h.tmp, 'job.sh')
        with open(script, 'w') as f:
            f.write('#!/bin/sh\necho "from the copy $1"\n')
        os.chmod(script, 0o755)
        a = self.h.submit('--kind', 'light', '--', 'sleep', '1')
        b = self.h.submit('--kind', 'light', '--after', a, '--script', script, '--', 'arg1')
        os.unlink(script)  # a wiped scratchpad loses nothing
        self.assertEqual(self.h.runq('wait', b).returncode, 0)
        self.assertGreaterEqual(self.h.show(b)['start'], self.h.show(a)['end'] - 0.5)
        self.assertIn('from the copy arg1', self.h.runq('log', b).stdout)

    def test_self_deadlock_is_stopped(self):
        r = self.h.runq('run', '--kind', 'linux-perf', '--', 'flock', '-x', os.path.join(self.h.env['E2E_LOCK_DIR'], 'perf.lock'), 'true', timeout=60)
        self.assertEqual(r.returncode, 137)
        self.assertIn('self-deadlock', r.stderr)

    def test_hold_windows_for_the_user(self):
        r = self.h.runq('hold', 'windows', '--for', '1h', '--reason', 'the user plays')
        self.assertEqual(r.returncode, 0, r.stderr)
        st = json.loads(self.h.runq('status', '--json').stdout)
        self.assertIn('windows', st['holds'])
        self.assertIn('HELD: the windows slot', self.h.runq('status').stdout)
        self.assertEqual(self.h.runq('release', 'windows').returncode, 0)
        self.assertEqual(json.loads(self.h.runq('status', '--json').stdout)['holds'], {})

    def test_heartbeat(self):
        r = self.h.runq('run', '--kind', 'light', '--heartbeat', '2s', '--', 'sleep', '60')
        self.assertEqual(r.returncode, 137)


@unittest.skipUnless(systemd_ok(), 'needs a systemd user manager')
class Restarts(unittest.TestCase):
    """A daemon of their own: they kill it."""

    def setUp(self):
        self.h = Harness()
        self.h.start()

    def tearDown(self):
        self.h.close()
        no_real_daemon(self)

    def test_daemon_killed_mid_job(self):
        job = self.h.submit('--kind', 'light', '--', 'sh', '-c', 'sleep 3; exit 5')
        self.h.wait_for(lambda: self.h.show(job)['state'] == 'running', what='the start')
        self.h.kill_daemon()
        time.sleep(4)
        self.h.start()
        self.assertEqual(self.h.runq('wait', job).returncode, 5)

    def test_wsl_restart_loses_running_jobs(self):
        job = self.h.submit('--kind', 'light', '--', 'sleep', '300')
        self.h.wait_for(lambda: self.h.show(job)['state'] == 'running', what='the start')
        self.h.kill_daemon()
        subprocess.run(['systemctl', '--user', 'stop', f'{self.h.prefix}-*'], capture_output=True)
        with open(os.path.join(self.h.env['RUNQ_STATE_DIR'], 'boot_id'), 'w') as f:
            f.write('another-boot')
        # The unit's cleanup ran on stop; a real WSL restart leaves none.
        for name in ('cleanup.json', 'result.json'):
            if os.path.exists(os.path.join(self.h.jobdir(job), name)):
                os.unlink(os.path.join(self.h.jobdir(job), name))
        self.h.start()
        self.assertEqual(self.h.runq('wait', job).returncode, 76)
        self.assertEqual(self.h.show(job)['state'], 'lost')


SHIM_SCRIPT = '''#!/usr/bin/env bash
set -euo pipefail
source "{scripts}/runq-submit.sh"
runq_self_submit --kind light --label shim-test -- "$0" "$@"
echo "job=${{RUNQ_JOB:-none}} args=$*"
'''


@unittest.skipUnless(systemd_ok(), 'needs a systemd user manager')
class Shims(unittest.TestCase):
    """scripts/runq-submit.sh: a script queues itself once, the kill switch and a missing daemon
    send it down the old path."""

    @classmethod
    def setUpClass(cls):
        cls.h = Harness()
        cls.h.start()
        cls.script = os.path.join(cls.h.tmp, 'self.sh')
        with open(cls.script, 'w') as f:
            f.write(SHIM_SCRIPT.format(scripts=helpers.SCRIPTS))
        os.chmod(cls.script, 0o755)

    @classmethod
    def tearDownClass(cls):
        cls.h.close()

    def sh(self, env=None):
        return subprocess.run([self.script, 'a', 'b c'], env=dict(self.h.env, **(env or {})), capture_output=True, text=True, timeout=60)

    def test_queues_itself_once(self):
        r = self.sh()
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertRegex(r.stdout, r'job=j\d+ args=a b c')

    def test_kill_switch(self):
        r = self.sh({'RUNQ': 'off'})
        self.assertIn('job=none', r.stdout)
        cfg = os.path.join(self.h.tmp, 'off.toml')
        with open(cfg, 'w') as f:
            f.write('enabled = false\n')
        r = self.sh({'RUNQ_CONFIG': cfg})
        self.assertIn('job=none', r.stdout)
        self.assertIn('enabled = false', r.stderr)

    def test_old_style_wrapper_runs_it_directly(self):
        lock = os.path.join(self.h.env['E2E_LOCK_DIR'], 'windows-game.lock')
        r = subprocess.run(['flock', '-x', lock, self.script, 'a'], env=self.h.env, capture_output=True, text=True, timeout=60)
        self.assertIn('job=none', r.stdout)
        self.assertIn('the caller holds windows-game.lock', r.stderr)

    def test_no_daemon(self):
        r = self.sh({'RUNQ_SOCKET': os.path.join(self.h.tmp, 'nothing')})
        self.assertIn('job=none', r.stdout)
        self.assertIn('running directly with the old locks', r.stderr)


def wsl_interop_ok():
    try:
        sys.path.insert(0, helpers.SCRIPTS)
        from runqlib import winenv

        return systemd_ok() and winenv.Windows().available()
    except Exception:
        return False


@unittest.skipUnless(wsl_interop_ok(), 'needs systemd and WSL interop')
class WindowsInterop(unittest.TestCase):
    """A Windows process a job starts runs without PreferSystem32Images, as from a terminal: a
    DLL beside an executable (Doorstop's winhttp.dll beside the original) wins over System32's.
    Read-only on the Windows side: PowerShell reports its own mitigation policy."""

    def setUp(self):
        self.h = Harness()
        self.h.env['RUNQ_WINDOWS'] = 'real'
        self.h.start()

    def tearDown(self):
        self.h.close()
        no_real_daemon(self)

    def test_windows_processes_without_prefer_system32(self):
        def chosen():
            return json.loads(self.h.runq('status', '--json').stdout)['machine'].get('interop')

        self.h.wait_for(chosen, timeout=90, what='an interop socket')
        ps = '/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe'
        r = self.h.runq(
            'run',
            '--kind',
            'light',
            '--',
            ps,
            '-NoProfile',
            '-NonInteractive',
            '-Command',
            '(Get-ProcessMitigation -Id $PID).ImageLoad.PreferSystem32',
            timeout=120,
        )
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(r.stdout.strip(), 'OFF')


class Rules(unittest.TestCase):
    """runq's sources never kill by pattern or name, never move the pointer, never pass the
    flag that pins the pointer over a Windows game."""

    def test_sources(self):
        names = ['runq'] + [os.path.join('runqlib', n) for n in os.listdir(os.path.join(helpers.SCRIPTS, 'runqlib')) if n.endswith(('.py', '.ps1'))]
        bad = ['pkill', 'killall', 'taskkill /IM', 'Get-Process -Name', 'SetCursorPos', 'SendInput', '--' + 'background', '-Action focus', 'AppActivate']
        for name in names:
            with open(os.path.join(helpers.SCRIPTS, name)) as f:
                text = f.read()
            for word in bad:
                self.assertNotIn(word, text, f'{name} contains {word!r}')


if __name__ == '__main__':
    unittest.main()
