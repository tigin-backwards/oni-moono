"""runq's daemon, CLI, shim and cleanup, end to end: a daemon on a temporary state folder, its
jobs as systemd user units under a test prefix in a test slice, running true, false, sleep and a
job that ignores SIGTERM. Skipped without a systemd user manager. The Windows side is off here
(RUNQ_WINDOWS=off); test_runq_windows.py covers it with a fake backend."""

import fcntl, json, os, shutil, signal, subprocess, sys, tempfile, time, unittest

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
outside_stall_minutes = 0.02
[capacity]
lane_first = 101
lane_last = 104
"""


class Harness:
    count = 0

    def __init__(self, extra='', config=CONFIG):
        Harness.count += 1
        self.tmp = tempfile.mkdtemp(prefix='rq', dir='/tmp')
        self.prefix = f'runqt{os.getpid()}n{Harness.count}'
        with open(os.path.join(self.tmp, 'config.toml'), 'w') as f:
            f.write(config + extra)
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
        self.h.wait_for(lambda: 'Windows' in (self.h.show(a)['waits'] or ''), what='the reason it waits')
        self.h.runq('cancel', a)
        self.assertEqual(self.h.show(a)['state'], 'cancelled')

    def test_cancel_records_who_cancelled(self):
        a = self.h.submit('--kind', 'windows', '--label', 'to cancel', '--', 'true')  # no Windows backend: waits
        self.assertEqual(self.h.runq('cancel', a).returncode, 0)
        job = self.h.show(a)
        self.assertEqual((job['state'], job['reason']), ('cancelled', 'cancelled'))
        by = job['cancelled_by']
        self.assertEqual((by['owner'], by['cwd']), (self.h.env['RUNQ_OWNER'], self.h.tmp))
        self.assertIsInstance(by['pid'], int)
        self.assertIn('at', by)
        line = next(x for x in self.h.runq('history', '--owner', self.h.env['RUNQ_OWNER']).stdout.splitlines() if x.startswith(a))
        self.assertIn(f'(cancelled)  by {self.h.env["RUNQ_OWNER"]} pid {by["pid"]} in {self.h.tmp}', line)

    def test_status_warns_when_only_a_process_outside_runq_holds_the_queue(self):
        # An exclusive perf.lock holder outside runq, and a due perf job: nothing can run.
        locks = self.h.env['E2E_LOCK_DIR']
        holder = subprocess.Popen(['flock', '-x', '-o', os.path.join(locks, 'perf.lock'), 'sleep', '120'])  # -o: sleep holds no fd
        try:
            job = self.h.submit('--level', 'issue', '--kind', 'perf', '--', 'true')
            self.h.wait_for(lambda: 'WARNING: nothing has run' in self.h.runq('status').stdout, what='the warning')
            out = self.h.runq('status').stdout
            self.assertIn(f'due job(s) {job} wait only on processes outside runq', out)
            self.assertIn(f'WARNING:   pid {holder.pid}: flock -x -o {locks}/perf.lock sleep 120', out)
            self.assertEqual(self.h.runq('status', '--json').returncode, 0)
        finally:
            holder.kill()
            holder.wait()
        self.assertEqual(self.h.runq('wait', job, timeout=60).returncode, 0)
        self.assertNotIn('WARNING: nothing has run', self.h.runq('status').stdout)

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
        # This process holds it, as an old script would, until the daemon has seen it.
        with open(lock, 'a') as holder:
            fcntl.flock(holder, fcntl.LOCK_EX)
            job = self.h.submit('--kind', 'linux-perf', '--', 'sh', '-c', 'cat "$E2E_LOCK_DIR/perf.wanted"')

            def seen():
                st = json.loads(self.h.runq('status', '--json').stdout)
                return any('perf.lock held exclusively' in x for x in st['external']) and self.h.show(job)['waits']

            self.h.wait_for(seen, what='the daemon to see the lock held')
            self.assertEqual(self.h.show(job)['state'], 'queued')
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
        self.h.wait_for(lambda: 'cap 2' in (self.h.show(c)['waits'] or ''), what='the third to wait for the cap')
        self.assertEqual(self.h.show(c)['state'], 'queued')
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
class Warnings(unittest.TestCase):
    """A daemon that flags an idle slot holder early and stops it late."""

    def setUp(self):
        self.h = Harness(config=CONFIG.replace('idle_stop_minutes = 0.08', 'idle_stop_minutes = 5'))
        self.h.start()

    def tearDown(self):
        self.h.close()
        no_real_daemon(self)

    def warned(self):
        return json.loads(self.h.runq('status', '--json').stdout)['warnings']

    def test_a_warning_ends_with_its_job(self):
        job = self.h.submit('--kind', 'linux-perf', '--', 'sleep', '300')
        self.h.wait_for(lambda: job in self.warned(), what='the idle warning')
        self.assertTrue(self.h.show(job)['warnings'])
        self.h.runq('cancel', job)
        self.assertEqual(self.h.runq('wait', job).returncode, 137)
        self.assertNotIn(job, self.warned())
        self.assertEqual(self.h.show(job)['warnings'], [])


class InProcess(unittest.TestCase):
    """The daemon's bookkeeping on a Daemon object in this process: no socket, no systemd (a
    job's unit is a name only), a store in a temporary folder."""

    def setUp(self):
        from unittest import mock

        from runqlib import daemon, sched

        self.sched = sched
        self.tmp = tempfile.mkdtemp(prefix='rqi')
        env = {
            'RUNQ_STATE_DIR': os.path.join(self.tmp, 'state'),
            'RUNQ_RUNS_DIR': os.path.join(self.tmp, 'runs'),
            'E2E_LOCK_DIR': os.path.join(self.tmp, 'locks'),
            'RUNQ_SOCKET': os.path.join(self.tmp, 's'),
        }
        self.environ = mock.patch.dict(os.environ, env)
        self.environ.start()
        self.quiet = mock.patch.object(daemon, 'log', lambda *parts: None)
        self.quiet.start()
        self.d = daemon.Daemon(windows=None)
        self.d.start_unit = lambda job, attempt: self.d.unit_name(job['id'], attempt)
        self.d.stop_unit = lambda unit: None
        self.d.unit_cgroup = lambda unit: os.path.join(self.tmp, 'cgroup', unit)

    def tearDown(self):
        self.d.store.close()
        for fd in (self.d.wake_r, self.d.wake_w):
            os.close(fd)
        self.quiet.stop()
        self.environ.stop()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def queue(self, job_id='j0000001', kind='linux-perf', **fields):
        from runqlib import config, shim

        folder = os.path.join(self.d.runs_dir, job_id)
        os.makedirs(folder)
        job = dict(
            id=job_id, owner='o', level='study', label='l', kind=kind, needs=config.kind_needs(self.d.cfg, kind), timeout=3600.0,
            cwd='/', argv=['true'], dir=folder, after=[], submit=time.time(), ord=time.time(), state='queued',
        )  # fmt: skip
        job.update(fields)
        shim.write_json(os.path.join(folder, 'job.json'), dict(job, caller_env={}))
        self.d.store.insert(job)
        return job_id

    def start(self, job_id):
        self.d.grant(self.d.store.get(job_id), self.sched.Grant(job_id, ()))
        return self.d.store.get(job_id)

    def cgroup(self, name, usec):
        folder = os.path.join(self.tmp, 'cgroup', name)
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, 'cpu.stat'), 'w') as f:
            f.write(f'usage_usec {usec}\nuser_usec {usec}\nsystem_usec 0\n')
        return folder

    def test_a_cgroup_that_ended_mid_sample_is_left_out_of_the_cpu_spent(self):
        worker = self.d.worker
        stays, ends = self.cgroup('stays', 5_000_000), self.cgroup('ends', 2_000_000_000)
        before = worker.cgroup_usecs([stays, ends])
        self.cgroup('stays', 8_000_000)
        shutil.rmtree(ends)  # its job ended
        after = worker.cgroup_usecs([stays, ends])
        self.assertEqual(after, {stays: 8_000_000})
        self.assertEqual(worker.spent_usec(before, after), 3_000_000)  # not 3 s less 2,000 s
        self.assertEqual(worker.cgroup_usec([stays, ends]), 8_000_000)

    def test_a_jobs_players_scopes_count_as_its_cpu(self):
        from unittest import mock

        from runqlib import cleanup

        # Three processes carry the job's id: one in its unit, two players in a scope each; a
        # fourth is another job's player, a fifth nobody's, a sixth a shell outside the test slice.
        t = f'/u/{self.d.slice}/oni-test-'
        jobs = {1: 'j0000001', 2: 'j0000001', 3: 'j0000001', 4: 'j0000009', 5: None, 6: 'j0000001'}
        cgroups = {1: None, 2: t + 'a.scope', 3: t + 'b.scope', 4: t + 'c.scope', 5: '/session-1.scope', 6: '/init.scope'}
        found = cleanup.job_scope_paths([(pid, '') for pid in jobs], job_of=jobs.get, scope=cgroups.get)
        self.assertEqual(found, {'j0000001': ['/init.scope', t + 'a.scope', t + 'b.scope'], 'j0000009': [t + 'c.scope']})
        job_id = self.queue(kind='e2e')
        unit = self.start(job_id)['unit']
        with mock.patch.object(cleanup, 'job_scope_paths', lambda: found):
            self.d.check_idle(time.monotonic())
        root = '/sys/fs/cgroup' + t
        self.assertEqual(self.d.worker.cgroups, [self.d.unit_cgroup(unit), root + 'a.scope', root + 'b.scope'])

    def test_a_scope_is_read_from_a_process_cgroup(self):
        from unittest import mock

        from runqlib import cleanup

        texts = {
            'scope': '0::/user.slice/user-1000.slice/user@1000.service/oni-tests.slice/oni-test-7-9-lane3-ours-1.scope\n',
            'unit': '0::/user.slice/user-1000.slice/user@1000.service/oni-tests.slice/runq-j0000001-1.service\n',
        }
        for name, want in (('scope', texts['scope'][3:].strip()), ('unit', None)):
            with mock.patch('builtins.open', mock.mock_open(read_data=texts[name])):
                self.assertEqual(cleanup.scope_of(1), want)

    def test_a_wait_reason_and_boosts_end_with_the_job(self):
        queued, ran = self.queue('j0000001'), self.queue('j0000002')
        self.d.reasons.update({queued: 'waits for the perf slot', ran: 'waits for memory'})
        self.d.starts.update({queued: 5.0, ran: 6.0})
        self.d.worker.boosted |= {(ran, 41), (ran, 42), ('j0000003', 43)}
        self.assertEqual(self.d.cancel(queued)['state'], 'cancelled')
        self.assertIsNone(self.d.describe(self.d.store.get(queued))['waits'])
        job = self.start(ran)
        self.d.reasons[ran] = 'a phase waits'
        for name in ('result.json', 'cleanup.json'):
            with open(os.path.join(job['dir'], name), 'w') as f:
                f.write('{"exit": 0}')
        self.d.finish(self.d.store.get(ran))
        self.assertEqual((self.d.reasons, self.d.starts, self.d.worker.boosted), ({}, {}, {('j0000003', 43)}))

    def test_a_requeued_job_drops_its_first_attempts_cgroup(self):
        from runqlib import shim

        job_id = self.queue()
        job = self.start(job_id)
        self.d.check_idle(time.monotonic())
        first = self.d.store.get(job_id)['cgroup']
        self.assertTrue(first.endswith(job['unit']))
        # The shim lost the race for a legacy lock: the job is queued again, then starts again.
        shim.write_json(os.path.join(job['dir'], 'result.json'), {'lost_race': 'perf.lock', 'attempt': job['attempt']})
        self.d.finish(self.d.store.get(job_id))
        self.assertEqual(self.d.store.get(job_id)['state'], 'queued')
        job = self.start(job_id)
        self.assertEqual((job['attempt'], job['cgroup']), (2, None))
        self.d.check_idle(time.monotonic())
        second = self.d.store.get(job_id)['cgroup']
        self.assertTrue(second.endswith(job['unit']))
        self.assertNotEqual(first, second)


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
        # The job ends while no daemon runs: its cleanup's record is the last file it writes.
        self.h.wait_for(lambda: os.path.exists(os.path.join(self.h.jobdir(job), 'cleanup.json')), what='the job to end without a daemon')
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


class Cli(unittest.TestCase):
    def test_a_daemon_of_another_version_is_said(self):
        import importlib.machinery, importlib.util

        loader = importlib.machinery.SourceFileLoader('runq_cli', RUNQ)
        cli = importlib.util.module_from_spec(importlib.util.spec_from_loader('runq_cli', loader))
        loader.exec_module(cli)
        self.assertIsNone(cli.stale_daemon(cli.version()))
        self.assertIsNone(cli.stale_daemon('dev'))  # a daemon run from a checkout (the tests')
        self.assertIsNone(cli.stale_daemon(None))
        line = cli.stale_daemon('0123456789ab')
        self.assertIn('the daemon runs runq 0123456789ab', line)
        self.assertIn(cli.version(), line)


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
