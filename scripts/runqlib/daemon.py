"""runqd: owns the machine's shared resources and makes every scheduling decision (§3).

One process: a selectors event loop on a Unix socket (JSON lines), plus a worker thread for slow
probes (PowerShell takes 0.5-1 s; the CPU sample 5 s). Jobs run as transient systemd services
(runq-<id>-<attempt>.service in oni-tests.slice), started by the daemon, not by the caller's
shell; they do not depend on the daemon, which finds them again by unit name after a restart.

Protocol: one JSON object a line each way. Requests carry "op":
  submit {job fields, wait}       -> {ok, id, dir} then, with wait, events {event: started|end}
  wait {id}                       -> events until {event: end, exit, reason}
  cancel {id}                     -> {ok}
  show {id}                       -> {ok, job}
  status {owner?}                 -> {ok, status}
  history {owner?, since?}        -> {ok, jobs}
  phase {id, needs, kind, token}  -> {ok, env} once the new grant holds (from inside a job)
  ping                            -> {ok, version, pid}
"""

import contextlib, fcntl, json, math, os, selectors, shutil, signal, socket, subprocess, sys, threading, time, traceback

from . import config, legacy, sched, shim, store, winenv

EXIT_TIMEOUT, EXIT_KILLED, EXIT_REFUSED, EXIT_LOST, EXIT_NO_DAEMON = 124, 137, 75, 76, 69
TERMINAL = ('done', 'cancelled', 'lost')


def log(*parts):
    print(time.strftime('%Y-%m-%d %H:%M:%S'), *parts, file=sys.stderr, flush=True)


def boot_id():
    try:
        with open('/proc/sys/kernel/random/boot_id') as f:
            return f.read().strip()
    except OSError:
        return ''


class Conn:
    def __init__(self, sock):
        self.sock, self.buf, self.out = sock, b'', b''
        self.closed = False

    def send(self, obj):
        if self.closed:
            return
        try:
            self.sock.sendall((json.dumps(obj) + '\n').encode())
        except OSError:
            self.closed = True


class Worker(threading.Thread):
    """Slow probes off the event loop: the Windows side and the machine's CPU outside runq."""

    def __init__(self, daemon):
        super().__init__(daemon=True)
        self.d = daemon
        self.lock = threading.Lock()
        self.want_windows = threading.Event()
        self.games, self.games_at, self.user_in_oni = [], 0.0, False
        self.ext_cpu, self.win_temp = 0.0, None
        self.cgroups = []  # the runq jobs' cgroup folders, set by the loop
        self.boost_matches = []  # (job id, match) of win-perf jobs, set by the loop
        self.boosted = set()
        self.stop = False

    def cgroup_usec(self, paths):
        total = 0
        for path in paths:
            try:
                with open(os.path.join(path, 'cpu.stat')) as f:
                    for line in f:
                        if line.startswith('usage_usec'):
                            total += int(line.split()[1])
            except OSError:
                pass
        return total

    def run(self):
        w = self.d.windows
        if w is not None:
            try:
                self.win_temp = w.temp()
            except Exception as e:
                log(f'windows: no %TEMP%: {e}')
        last_win = 0.0
        while not self.stop:
            try:
                with self.lock:
                    paths = list(self.cgroups)
                b0, t0 = legacy.machine_busy_sample()
                c0 = self.cgroup_usec(paths)
                start = time.monotonic()
                self.want_windows.wait(5)
                b1, t1 = legacy.machine_busy_sample()
                c1 = self.cgroup_usec(paths)
                secs = max(time.monotonic() - start, 0.1)
                ncpu = os.cpu_count() or 1
                busy = ncpu * (b1 - b0) / max(t1 - t0, 1)
                ours = (c1 - c0) / 1e6 / secs
                with self.lock:
                    self.ext_cpu = max(0.0, busy - ours)
                now = time.monotonic()
                period = 30 if self.d.windows_relevant else 300
                if w is not None and (self.want_windows.is_set() or now - last_win >= period):
                    self.want_windows.clear()
                    last_win = now
                    games = w.games()
                    in_oni = w.user_in_oni()
                    with self.lock:
                        self.games, self.games_at, self.user_in_oni = games, time.time(), in_oni
                        boosts = list(self.boost_matches)
                    for job_id, match in boosts:
                        for g in games:
                            if match in g.get('cmdline', '') and (job_id, g.get('pid')) not in self.boosted:
                                out = w.boost(match)
                                log(f'{job_id}: {out.strip()}')
                                self.boosted.add((job_id, g.get('pid')))
                    self.d.wake()
            except Exception:
                log('worker: ' + traceback.format_exc())
                time.sleep(5)


class Daemon:
    def __init__(self, cfg=None, windows='default', units=None):
        self.cfg = cfg or config.load()
        self.state_dir = config.state_dir()
        self.runs_dir = config.runs_dir()
        self.lock_dir = config.legacy_dir()
        self.prefix = config.unit_prefix()
        self.slice = config.slice_name()
        self.install = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        self.runq = os.path.join(self.install, 'runq')
        self.windows = winenv.backend() if windows == 'default' else windows
        self.store = store.Store(os.path.join(self.state_dir, 'runq.db'))
        self.sel = selectors.DefaultSelector()
        self.waiters = {}  # job id -> [Conn]
        self.phase_waiters = {}  # job id -> Conn
        self.reasons, self.starts, self.order = {}, {}, []
        self.deadlines = {}  # job id -> monotonic deadline
        self.idle = {}  # job id -> [(monotonic, usec)]
        self.warnings = {}  # job id -> [text]
        self.unit_checked = 0.0
        self.scanned = 0.0
        self.external, self.lanes_free = sched.External(), ()
        self.retention_at = 0.0
        self.windows_relevant = False
        self.wake_r, self.wake_w = os.pipe()
        os.set_blocking(self.wake_r, False)
        os.set_blocking(self.wake_w, False)
        self.dirty = True
        self.worker = Worker(self)
        self.stopping = False
        self.version = self.read_version()
        self.wanted_written = False

    def read_version(self):
        try:
            with open(os.path.join(self.install, 'VERSION')) as f:
                return f.read().strip()
        except OSError:
            return 'dev'

    def wake(self):
        try:
            os.write(self.wake_w, b'x')
        except OSError:
            pass

    # ------------------------------------------------------------ start-up

    def serve(self):
        os.makedirs(self.state_dir, exist_ok=True)
        os.makedirs(self.runs_dir, exist_ok=True)
        self.lockf = open(os.path.join(self.state_dir, 'runqd.lock'), 'a+')
        try:
            fcntl.flock(self.lockf, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.lockf.seek(0)
            print(f'runqd: another daemon runs (pid {self.lockf.read().strip() or "?"})', file=sys.stderr)
            return 1
        self.lockf.truncate(0)
        self.lockf.write(str(os.getpid()))
        self.lockf.flush()
        path = config.socket_path()
        if os.path.exists(path):
            os.unlink(path)
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old = os.umask(0o177)
        self.server.bind(path)
        os.umask(old)
        os.chmod(path, 0o600)
        self.server.listen(64)
        self.server.setblocking(False)
        self.sel.register(self.server, selectors.EVENT_READ, 'accept')
        self.sel.register(self.wake_r, selectors.EVENT_READ, 'wake')
        signal.signal(signal.SIGTERM, lambda *a: setattr(self, 'stopping', True))
        signal.signal(signal.SIGINT, lambda *a: setattr(self, 'stopping', True))
        self.reconcile()
        self.worker.start()
        log(f'runqd {self.version} up, pid {os.getpid()}, state {self.state_dir}, units {self.prefix}-*, slice {self.slice}')
        while not self.stopping:
            for key, _ in self.sel.select(timeout=0.25 if self.deadlines else 1.0):
                if key.data == 'accept':
                    self.accept()
                elif key.data == 'wake':
                    try:
                        while os.read(self.wake_r, 4096):
                            pass
                    except BlockingIOError:
                        pass
                    self.dirty = True
                else:
                    self.readable(key.data)
            try:
                self.tick()
            except Exception:
                log('tick: ' + traceback.format_exc())
        self.worker.stop = True
        if self.wanted_written:
            legacy.clear_wanted(self.lock_dir, os.getpid())
        self.server.close()
        with contextlib.suppress(OSError):
            os.unlink(path)
        log('runqd stopped (jobs keep running)')
        return 0

    def reconcile(self):
        """After a start: each running job's unit, and the result of those that ended meanwhile.
        A new boot (a WSL restart) lost every job that ran."""
        bid_path = os.path.join(self.state_dir, 'boot_id')
        last = open(bid_path).read().strip() if os.path.exists(bid_path) else None
        bid = boot_id()
        with open(bid_path, 'w') as f:
            f.write(bid)
        rebooted = last is not None and last != bid
        now, mono = time.time(), time.monotonic()
        for job in self.store.by_state('running'):
            active = self.unit_active(job['unit'])
            if active and not rebooted:
                left = (job['start'] or now) + job['timeout'] - now
                self.deadlines[job['id']] = mono + max(left, 0)
                continue
            if self.ended_files(job):
                continue  # tick() finishes it
            self.finish_lost(job, 'the machine restarted (WSL)' if rebooted else 'its unit is gone and it left no result', use_result=not rebooted)
        self.dirty = True

    # ------------------------------------------------------------ units

    def unit_name(self, job_id, attempt):
        return f'{self.prefix}-{job_id}-{attempt}.service'

    def unit_active(self, unit):
        if not unit:
            return False
        r = subprocess.run(['systemctl', '--user', 'show', unit, '-p', 'ActiveState', '--value'], capture_output=True, text=True)
        return r.stdout.strip() in ('active', 'activating', 'deactivating', 'reloading')

    def unit_cgroup(self, unit):
        r = subprocess.run(['systemctl', '--user', 'show', unit, '-p', 'ControlGroup', '--value'], capture_output=True, text=True)
        cg = r.stdout.strip()
        return f'/sys/fs/cgroup{cg}' if cg else None

    def start_unit(self, job, attempt):
        unit = self.unit_name(job['id'], attempt)
        grace = int(self.cfg['stop_grace_seconds'])
        py = sys.executable or '/usr/bin/python3'
        env_props = []
        for name in ('RUNQ_WINDOWS', 'RUNQ_SOCKET', 'RUNQ_STATE_DIR', 'RUNQ_RUNS_DIR', 'E2E_LOCK_DIR', 'RUNQ_UNIT_PREFIX', 'RUNQ_SLICE', 'RUNQ_CONFIG'):
            if os.environ.get(name):
                env_props += ['-E', f'{name}={os.environ[name]}']
        cmd = [
            'systemd-run',
            '--user',
            '--quiet',
            f'--unit={unit}',
            f'--slice={self.slice}',
            '-p',
            'OOMPolicy=continue',
            '-p',
            'OOMScoreAdjust=1000',
            '-p',
            'KillMode=control-group',
            '-p',
            f'TimeoutStopSec={grace}',
            '-p',
            'CollectMode=inactive-or-failed',
            '-p',
            f'RuntimeMaxSec={int(job["timeout"]) + 120}',
            '-p',
            f'ExecStopPost={py} -I {self.runq} _cleanup {job["dir"]}',
            f'--working-directory={job["dir"]}',
            *env_props,
            '--',
            py,
            '-I',
            self.runq,
            '_shim',
            job['dir'],
        ]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode:
            raise RuntimeError(f'systemd-run failed: {r.stderr.strip()}')
        return unit

    def stop_unit(self, unit):
        subprocess.run(['systemctl', '--user', 'stop', '--no-block', unit], capture_output=True)

    # ------------------------------------------------------------ connections

    def accept(self):
        try:
            sock, _ = self.server.accept()
        except OSError:
            return
        sock.setblocking(True)
        sock.settimeout(5)
        conn = Conn(sock)
        self.sel.register(sock, selectors.EVENT_READ, conn)

    def drop(self, conn):
        conn.closed = True
        with contextlib.suppress(Exception):
            self.sel.unregister(conn.sock)
        with contextlib.suppress(OSError):
            conn.sock.close()

    def readable(self, conn):
        try:
            data = conn.sock.recv(65536)
        except (OSError, socket.timeout):
            data = b''
        if not data:
            self.drop(conn)
            return
        conn.buf += data
        while b'\n' in conn.buf:
            line, conn.buf = conn.buf.split(b'\n', 1)
            try:
                req = json.loads(line)
                self.handle(conn, req)
            except Exception as e:
                log('request: ' + traceback.format_exc())
                conn.send({'ok': False, 'code': 2, 'error': f'{type(e).__name__}: {e}'})

    def handle(self, conn, req):
        op = req.get('op')
        if op == 'ping':
            conn.send({'ok': True, 'version': self.version, 'pid': os.getpid()})
        elif op == 'submit':
            self.submit(conn, req)
        elif op == 'wait':
            self.wait(conn, req['id'])
        elif op == 'cancel':
            conn.send(self.cancel(req['id']))
        elif op == 'show':
            job = self.store.get(req['id'])
            conn.send({'ok': bool(job), 'job': self.describe(job) if job else None, 'error': None if job else 'no such job'})
        elif op == 'status':
            conn.send({'ok': True, 'status': self.status(req.get('owner'))})
        elif op == 'history':
            jobs = self.store.history(req.get('owner'), float(req.get('since') or 0), int(req.get('limit') or 50))
            conn.send({'ok': True, 'jobs': [self.describe(j) for j in jobs]})
        elif op == 'phase':
            self.phase(conn, req)
        else:
            conn.send({'ok': False, 'code': 2, 'error': f'unknown op {op!r}'})

    # ------------------------------------------------------------ submit

    def refuse(self, conn, why):
        conn.send({'ok': False, 'code': EXIT_REFUSED, 'error': why})

    def slot_hours(self, job):
        if job.get('expect'):
            return job['expect'] / 3600
        median = self.store.median_duration(job['label'])
        return (median if median else job['timeout']) / 3600

    def submit(self, conn, req):
        cfg = self.cfg
        level = req.get('level') or cfg['default_level']
        if level not in cfg['levels']:
            return self.refuse(conn, f'unknown level {level!r} ({", ".join(cfg["levels"])})')
        kind = req.get('kind') or 'generic'
        if kind not in cfg['kinds']:
            return self.refuse(conn, f'unknown kind {kind!r} ({", ".join(cfg["kinds"])})')
        try:
            needs = dict(config.kind_needs(cfg, kind), **config.parse_needs(req.get('needs') or ''))
        except ValueError as e:
            return self.refuse(conn, str(e))
        owner = req.get('owner')
        if not owner:
            return self.refuse(conn, 'no owner (--owner or RUNQ_OWNER)')
        argv = req.get('argv') or []
        if not argv:
            return self.refuse(conn, 'no command')
        k = cfg['kinds'][kind]
        timeout = float(req.get('timeout') or k.get('timeout', 60) * 60)
        if timeout > cfg['max_timeout_minutes'] * 60:
            return self.refuse(conn, f'--timeout is at most {cfg["max_timeout_minutes"]} min')
        if (needs.get('windows') or needs.get('perf')) and winenv.BACKGROUND in ' '.join(argv):
            return self.refuse(conn, f'{winenv.BACKGROUND} is not allowed in Windows or perf runs: use --no-focus (it costs about 25% of the frame rate)')
        free = shutil.disk_usage(self.runs_dir).free / 1e9
        if free < cfg['min_free_gb']:
            return self.refuse(conn, f'{free:.1f} GB free under {self.runs_dir}: runq takes no jobs below {cfg["min_free_gb"]} GB')
        mine = [j for j in self.store.by_state('queued', 'running') if j['owner'] == owner]
        queued = [j for j in mine if j['state'] == 'queued']
        if len(queued) >= cfg['caps']['queued_per_owner']:
            return self.refuse(conn, f'owner {owner} has {len(queued)} jobs queued (cap {cfg["caps"]["queued_per_owner"]})')
        label = req.get('label') or ' '.join(os.path.basename(a) if i == 0 else a for i, a in enumerate(argv[:3]))
        job_id = self.store.new_id()
        job = {
            'id': job_id,
            'owner': owner,
            'level': level,
            'label': label,
            'kind': kind,
            'needs': needs,
            'timeout': timeout,
            'expect': float(req['expect']) if req.get('expect') else None,
            'cwd': req.get('cwd') or '/',
            'argv': list(argv),
            'after': req.get('after') or [],
            'after_ok': 1 if req.get('after_ok') else 0,
            'heartbeat': float(req['heartbeat']) if req.get('heartbeat') else None,
            'retry_on_lost': 1 if req.get('retry_on_lost') else 0,
            'keep_home': 1 if req.get('keep_home') else 0,
            'budget_ok': req.get('budget_ok'),
        }
        if needs.get('windows') or needs.get('perf'):
            hours = sum(self.slot_hours(j) for j in mine if j['needs'].get('windows') or j['needs'].get('perf'))
            this = self.slot_hours(job)
            limit = cfg['caps']['slot_hours_per_owner']
            if hours + this > limit and not job['budget_ok']:
                return self.refuse(
                    conn,
                    f'owner {owner}: {len(mine) + 1} job(s) with about {hours + this:.1f} h of the Windows or perf slot; cap {limit:g} h. '
                    "Ask the user, then pass --budget-ok 'REASON'",
                )
        for dep in job['after']:
            if not self.store.get(dep):
                return self.refuse(conn, f'--after {dep}: no such job')
        job_dir = os.path.join(self.runs_dir, job_id)
        os.makedirs(os.path.join(job_dir, 'out'), exist_ok=True)
        if req.get('script'):
            dest = os.path.join(job_dir, 'cmd')
            with open(dest, 'w') as f:
                f.write(req['script'])
            os.chmod(dest, 0o755)
            job['argv'] = [dest] + job['argv'][1:] if req.get('script_replaces') else job['argv']
        else:
            with open(os.path.join(job_dir, 'cmd'), 'w') as f:
                f.write(' '.join(json.dumps(a) if any(c in a for c in ' "\'$\\') else a for a in job['argv']) + '\n')
        allow = cfg['env_allow']
        env = winenv.filter_env(req.get('env') or {}, allow, req.get('env_names') or [])
        now = time.time()
        job.update(dir=job_dir, submit=now, ord=now, state='queued')
        shim.write_json(os.path.join(job_dir, 'job.json'), dict(job, caller_env=env))
        self.store.insert({k: v for k, v in job.items()})
        if level in cfg['logged_levels']:
            line = f'{time.strftime("%Y-%m-%d %H:%M:%S")} {job_id} level {level} owner {owner}: {label}\n'
            with open(os.path.join(self.state_dir, 'levels.log'), 'a') as f:
                f.write(line)
            log(f'level {level} used: {line.strip()}')
        if job['budget_ok']:
            with open(os.path.join(self.state_dir, 'levels.log'), 'a') as f:
                f.write(f'{time.strftime("%Y-%m-%d %H:%M:%S")} {job_id} budget-ok owner {owner}: {job["budget_ok"]}\n')
        log(f'{job_id} queued: {owner} {level} {kind} {label!r}')
        conn.send({'ok': True, 'id': job_id, 'dir': job_dir, 'out': os.path.join(job_dir, 'out'), 'log': os.path.join(job_dir, 'out.log')})
        if req.get('wait'):
            self.waiters.setdefault(job_id, []).append(conn)
        self.dirty = True

    # ------------------------------------------------------------ wait, cancel, show

    def end_event(self, job):
        return {'event': 'end', 'id': job['id'], 'exit': job['exit'], 'reason': job['reason'], 'state': job['state'], 'noisy': bool(job['noisy'])}

    def wait(self, conn, job_id):
        job = self.store.get(job_id)
        if not job:
            conn.send({'ok': False, 'code': 2, 'error': f'no job {job_id}'})
            return
        conn.send({'ok': True, 'id': job_id, 'dir': job['dir'], 'state': job['state']})
        if job['state'] in TERMINAL:
            conn.send(self.end_event(job))
            return
        if job['state'] == 'running':
            conn.send({'event': 'started', 'id': job_id, 'lanes': job['lanes'] or []})
        self.waiters.setdefault(job_id, []).append(conn)

    def notify(self, job_id, event):
        for conn in self.waiters.get(job_id, []):
            conn.send(event)
        if event.get('event') == 'end':
            for conn in self.waiters.pop(job_id, []):
                pass

    def cancel(self, job_id, reason='cancelled', code=EXIT_KILLED):
        job = self.store.get(job_id)
        if not job:
            return {'ok': False, 'code': 2, 'error': f'no job {job_id}'}
        if job['state'] == 'queued':
            self.store.update(job_id, state='cancelled', end=time.time(), exit=code, reason=reason)
            self.notify(job_id, self.end_event(self.store.get(job_id)))
            self.dirty = True
            return {'ok': True, 'state': 'cancelled'}
        if job['state'] == 'running':
            self.store.update(job_id, reason=reason, exit=code)
            self.stop_unit(job['unit'])
            log(f'{job_id}: stopping ({reason})')
            return {'ok': True, 'state': 'stopping'}
        return {'ok': True, 'state': job['state']}

    def describe(self, job):
        d = {
            k: job.get(k)
            for k in (
                'id',
                'owner',
                'level',
                'label',
                'kind',
                'needs',
                'timeout',
                'state',
                'submit',
                'start',
                'end',
                'exit',
                'reason',
                'lanes',
                'noisy',
                'dir',
                'cwd',
                'argv',
                'unit',
                'attempt',
                'phase',
                'after',
            )
        }
        d['waits'] = self.reasons.get(job['id'])
        start = self.starts.get(job['id'])
        d['starts_by'] = None if start is None or start == math.inf else start
        d['warnings'] = self.warnings.get(job['id'], [])
        return d

    # ------------------------------------------------------------ phases

    def phase(self, conn, req):
        job_id = req.get('id')
        job = self.store.get(job_id)
        if not job or job['state'] != 'running':
            conn.send({'ok': False, 'code': 2, 'error': f'job {job_id} is not running'})
            return
        try:
            needs = dict(config.kind_needs(self.cfg, req['kind'])) if req.get('kind') else {}
            needs.update(config.parse_needs(req.get('needs') or ''))
        except (KeyError, ValueError) as e:
            conn.send({'ok': False, 'code': 2, 'error': f'bad phase: {e}'})
            return
        token = req.get('token')
        ph = job['phase'] or {}
        if ph.get('token') == token and ph.get('state') == 'held':
            conn.send({'ok': True, 'env': self.phase_env(job)})
            return
        self.phase_waiters[job_id] = conn
        if ph.get('token') == token and ph.get('state') in ('waiting', 'granting'):
            return  # a reconnect: the reply comes when it holds
        # Release everything the job holds, then queue the request with its original place.
        now = time.time()
        self.store.hold_end(job_id, now)
        seq = (job['phase_seq'] or 0) + 1
        self.write_grant(job, {}, [], seq)
        self.store.update(
            job_id, needs={}, lanes=[], phase={'needs': needs, 'token': token, 'state': 'waiting', 'kind': req.get('kind')}, phase_seq=seq, boost=0
        )
        self.signal_shim(job)
        log(f'{job_id}: phase {req.get("kind") or req.get("needs")} requested; holds released')
        self.dirty = True

    def phase_env(self, job):
        lanes = job['lanes'] or []
        needs = job['needs'] or {}
        env = {'RUNQ_LANES': ','.join(str(x) for x in lanes)}
        if needs.get('players'):
            env['E2E_MEMORY_GB'] = f'{float(needs.get("mem", 0)):g}'
            env['E2E_MAX_PLAYERS'] = str(int(needs['players']))
        if job.get('noisy'):
            env['RUNQ_NOISY'] = '1'
        if needs.get('windows') and self.win_temp():
            _, stage, home = winenv.job_paths(self.win_temp(), job['id'])
            caller = shim.read_json(os.path.join(job['dir'], 'env.json'), {}) or {}
            env.update(ONI_WIN_STAGE=stage, ONI_RUN_HOME=caller.get('ONI_RUN_HOME') or home, ONI_WIN_MONITOR=str(self.cfg['win_monitor']))
            env['WSLENV'] = winenv.wslenv_add(caller.get('WSLENV'), ['ONI_HOST_NO_DIALOG', 'RUNQ_JOB', 'ONI_RUN_HOME', 'ONI_WIN_STAGE', 'ONI_WIN_MONITOR'])
        return env

    def signal_shim(self, job):
        info = shim.read_json(os.path.join(job['dir'], 'shim.json'), {}) or {}
        pid = info.get('pid')
        if pid and legacy.process_start(pid) == info.get('start'):
            with contextlib.suppress(OSError):
                os.kill(pid, signal.SIGUSR2)

    # ------------------------------------------------------------ grants

    def win_temp(self):
        return self.worker.win_temp

    def write_grant(self, job, needs, lanes, seq, attempt=None):
        grant = {
            'seq': seq,
            'needs': needs,
            'lanes': list(lanes),
            'lock_dir': self.lock_dir,
            'bridge': bool(self.cfg.get('legacy_bridge', True)),
            'daemon_pid': os.getpid(),
            'lane_locks': self.cfg['kinds'].get(job['kind'], {}).get('lane_locks', 'shim'),
            'attempt': attempt,
        }
        if needs.get('windows') and self.win_temp():
            _, _, home = winenv.job_paths(self.win_temp(), job['id'])
            env = shim.read_json(os.path.join(job['dir'], 'env.json'), {}) or {}
            run_home = env.get('ONI_RUN_HOME', home)
            with contextlib.suppress(ValueError):
                grant['windows_home'] = winenv.wsl_path(run_home)
        shim.write_json(os.path.join(job['dir'], 'grant.json'), grant)
        return grant

    def mark_windows(self, job):
        path = os.path.join(job['dir'], 'job.json')
        data = shim.read_json(path, {}) or {}
        data['windows_used'] = True
        data['win_temp'] = self.win_temp()
        shim.write_json(path, data)

    def grant(self, job, g):
        """Starts a queued job, or answers a phase request, with grant G."""
        now = time.time()
        needs = job['needs'] if not job['phase'] else job['phase']['needs']
        if job['state'] == 'running':  # a phase
            seq = (job['phase_seq'] or 0) + 1
            if needs.get('windows'):
                self.mark_windows(job)
            self.write_grant(job, needs, g.lanes, seq)
            ph = dict(job['phase'], state='granting', seq=seq)
            self.store.update(job['id'], needs=needs, lanes=list(g.lanes), phase=ph, phase_seq=seq, noisy=1 if (g.noisy or job['noisy']) else 0)
            self.signal_shim(job)
            log(f'{job["id"]}: phase granted {needs} lanes {list(g.lanes)}')
            return
        attempt = (job['attempt'] or 0) + 1
        caller_env = (shim.read_json(os.path.join(job['dir'], 'job.json'), {}) or {}).get('caller_env', {})
        jobd = dict(job, needs=needs)
        env = winenv.job_env(caller_env, jobd, {'lanes': list(g.lanes), 'needs': needs}, self.cfg, self.win_temp())
        # Commands inside the job (runq phase, a nested runq run) reach this daemon.
        env['RUNQ_SOCKET'] = config.socket_path()
        for name in ('RUNQ_STATE_DIR', 'RUNQ_RUNS_DIR', 'RUNQ_CONFIG'):
            if os.environ.get(name):
                env[name] = os.environ[name]
        if g.noisy:
            env['RUNQ_NOISY'] = '1'
        shim.write_json(os.path.join(job['dir'], 'env.json'), env)
        if needs.get('windows'):
            self.mark_windows(job)
        self.write_grant(job, needs, g.lanes, 0, attempt)
        for name in ('result.json', 'cleanup.json', 'locks.json', 'shim.json'):
            with contextlib.suppress(FileNotFoundError):
                os.unlink(os.path.join(job['dir'], name))
        try:
            unit = self.start_unit(job, attempt)
        except Exception as e:
            log(f'{job["id"]}: {e}')
            self.store.update(job['id'], state='done', exit=EXIT_LOST, reason=f'could not start: {e}', end=now)
            self.notify(job['id'], self.end_event(self.store.get(job['id'])))
            return
        self.store.update(job['id'], state='running', start=now, lanes=list(g.lanes), attempt=attempt, unit=unit, noisy=1 if g.noisy else 0, boost=0)
        self.store.hold_start(job['id'], needs, now)
        self.deadlines[job['id']] = time.monotonic() + job['timeout']
        self.idle.pop(job['id'], None)
        log(f'{job["id"]}: started as {unit}, lanes {list(g.lanes)}{" (noisy)" if g.noisy else ""}')
        self.notify(job['id'], {'event': 'started', 'id': job['id'], 'lanes': list(g.lanes), 'unit': unit, 'noisy': g.noisy})

    # ------------------------------------------------------------ ends

    def ended_files(self, job):
        return os.path.exists(os.path.join(job['dir'], 'cleanup.json'))

    def finish(self, job):
        result = shim.read_json(os.path.join(job['dir'], 'result.json'), {}) or {}
        cleanup = shim.read_json(os.path.join(job['dir'], 'cleanup.json'), {}) or {}
        now = time.time()
        self.deadlines.pop(job['id'], None)
        self.idle.pop(job['id'], None)
        if result.get('lost_race') and result.get('attempt') == job['attempt']:
            log(f'{job["id"]}: lost the race for {result["lost_race"]} to an old script; requeued ahead')
            self.store.hold_end(job['id'], now)
            self.store.update(job['id'], state='queued', boost=1, unit=None, lanes=[], start=None)
            self.dirty = True
            return
        if job['reason'] in ('timeout',):
            code = EXIT_TIMEOUT
        elif job['reason'] in ('cancelled', 'idle-held', 'heartbeat') or (job['reason'] or '').startswith('self-deadlock'):
            code = EXIT_KILLED
        elif 'exit' in result:
            code = int(result['exit'])
        elif cleanup.get('exit_status') not in (None, ''):
            try:
                code = int(cleanup['exit_status'])
            except ValueError:
                code = 128 + int(getattr(signal, 'SIG' + cleanup['exit_status'], signal.SIGKILL))
        else:
            code = EXIT_LOST
        reason = job['reason']
        if not reason and not result:
            reason = f'the shim left no result (systemd: {cleanup.get("service_result") or "?"})'
        self.store.hold_end(job['id'], now)
        self.store.update(job['id'], state='done', end=now, exit=code, reason=reason, phase=None)
        job = self.store.get(job['id'])
        log(f'{job["id"]}: ended, exit {code}' + (f' ({reason})' if reason else ''))
        self.notify(job['id'], self.end_event(job))
        conn = self.phase_waiters.pop(job['id'], None)
        if conn:
            conn.send({'ok': False, 'code': code, 'error': 'the job ended'})
        self.dirty = True

    def finish_lost(self, job, why, use_result=True):
        """A job whose unit vanished without a cleanup (WSL restart): its cleanup runs here."""
        now = time.time()
        env = dict(os.environ, SERVICE_RESULT='lost')
        subprocess.run([sys.executable, '-I', self.runq, '_cleanup', job['dir']], env=env, capture_output=True, timeout=300)
        if use_result and os.path.exists(os.path.join(job['dir'], 'result.json')) and self.ended_files(job):
            self.finish(job)  # it ended with a result; only its cleanup was missing
            return
        self.store.hold_end(job['id'], now)
        self.deadlines.pop(job['id'], None)
        if job['retry_on_lost']:
            log(f'{job["id"]}: lost ({why}); requeued (--retry-on-lost)')
            first = (shim.read_json(os.path.join(job['dir'], 'job.json'), {}) or {}).get('needs', job['needs'])  # before any phase
            self.store.update(job['id'], state='queued', unit=None, lanes=[], start=None, phase=None, needs=first)
        else:
            log(f'{job["id"]}: lost ({why})')
            self.store.update(job['id'], state='lost', end=now, exit=EXIT_LOST, reason=why)
            self.notify(job['id'], self.end_event(self.store.get(job['id'])))
        self.dirty = True

    # ------------------------------------------------------------ the tick

    def tick(self):
        now, mono = time.time(), time.monotonic()
        running = self.store.by_state('running')
        for job in running:
            if self.ended_files(job):
                self.finish(job)
                continue
            if job['phase'] and job['phase'].get('state') == 'granting':
                locks = shim.read_json(os.path.join(job['dir'], 'locks.json'), {}) or {}
                if locks.get('seq') == job['phase'].get('seq'):
                    if locks.get('ok'):
                        self.store.hold_start(job['id'], job['needs'], now)
                        self.store.update(job['id'], phase=dict(job['phase'], state='held'))
                        conn = self.phase_waiters.pop(job['id'], None)
                        if conn:
                            conn.send({'ok': True, 'env': self.phase_env(self.store.get(job['id']))})
                    else:
                        log(f'{job["id"]}: phase lost the race for {locks.get("lost")}; requeued ahead')
                        self.store.update(job['id'], needs={}, lanes=[], phase=dict(job['phase'], state='waiting'), boost=1)
                        self.dirty = True
            deadline = self.deadlines.get(job['id'])
            if deadline is not None and mono >= deadline and job['reason'] != 'timeout':
                self.store.update(job['id'], reason='timeout', exit=EXIT_TIMEOUT)
                self.stop_unit(job['unit'])
                log(f'{job["id"]}: timeout after {job["timeout"] / 60:.0f} min; stopping')
            if job['heartbeat'] and job['reason'] is None:
                beat = os.path.join(job['dir'], 'beat')
                last = os.path.getmtime(beat) if os.path.exists(beat) else job['start']
                if now - last > job['heartbeat']:
                    self.store.update(job['id'], reason='heartbeat', exit=EXIT_KILLED)
                    self.stop_unit(job['unit'])
                    log(f'{job["id"]}: no heartbeat for {job["heartbeat"]:.0f} s; stopping')
        if mono - self.unit_checked > float(self.cfg['unit_check_seconds']):
            self.unit_checked = mono
            for job in self.store.by_state('running'):
                if not self.ended_files(job) and job['start'] and now - job['start'] > 20 and not self.unit_active(job['unit']):
                    time.sleep(0.5)
                    if not self.ended_files(job):
                        self.finish_lost(job, 'its unit ended without a cleanup')
            self.check_idle(mono)
        if mono - self.retention_at > 3600:
            self.retention_at = mono
            self.retention()
        if self.dirty or mono - self.scanned > float(self.cfg['scan_seconds']):
            self.schedule()

    def check_idle(self, mono):
        """Jobs that hold windows or perf but do nothing (§6.3)."""
        flag, stop = self.cfg['idle_flag_minutes'] * 60, self.cfg['idle_stop_minutes'] * 60
        pct = self.cfg['idle_cpu_percent']
        with self.worker.lock:
            games = list(self.worker.games)
        paths = []
        for job in self.store.by_state('running'):
            if not job['cgroup'] and job['unit']:
                cg = self.unit_cgroup(job['unit'])
                if cg:
                    self.store.update(job['id'], cgroup=cg)
                    job['cgroup'] = cg
            if job['cgroup']:
                paths.append(job['cgroup'])
            needs = job['needs'] or {}
            if not (needs.get('windows') or needs.get('perf')):
                self.idle.pop(job['id'], None)
                continue
            env = shim.read_json(os.path.join(job['dir'], 'env.json'), {}) or {}
            has_game = any(m in g.get('cmdline', '') for g in games for m in winenv.run_home_matches(job['id'], env))
            usec = self.worker.cgroup_usec([job['cgroup']] if job['cgroup'] else []) + self.scope_usec(job['id'])
            hist = self.idle.setdefault(job['id'], [])
            hist.append((mono, usec, has_game))
            hist[:] = [h for h in hist if mono - h[0] <= stop + 120]
            if len(hist) < 2:
                continue
            idle_since = None
            for i in range(len(hist) - 1, 0, -1):
                t1, u1, g1 = hist[i]
                t0, u0, _ = hist[i - 1]
                busy = (u1 - u0) / 1e6 / max(t1 - t0, 1) * 100
                if g1 or busy >= pct:
                    break
                idle_since = t0
            warns = [w for w in self.warnings.get(job['id'], []) if not w.startswith('idle')]
            if idle_since is not None:
                idle_for = mono - idle_since
                if idle_for >= stop:
                    self.store.update(job['id'], reason='idle-held', exit=EXIT_KILLED)
                    self.stop_unit(job['unit'])
                    log(f'{job["id"]}: holds {"windows" if needs.get("windows") else "perf"} but idle for {idle_for / 60:.0f} min; stopping')
                elif idle_for >= flag:
                    warns.append(
                        f'idle: holds the {"Windows" if needs.get("windows") else "perf"} slot with no game and no CPU for {idle_for / 60:.0f} min (stopped at {stop / 60:.0f})'
                    )
            self.warnings[job['id']] = warns
        with self.worker.lock:
            self.worker.cgroups = paths

    def scope_usec(self, job_id):
        from . import cleanup

        total = 0
        for scope in cleanup.job_scopes(job_id):
            cg = self.unit_cgroup(scope)
            if cg:
                total += self.worker.cgroup_usec([cg])
        return total

    def retention(self):
        cfg = self.cfg
        now = time.time()
        live = {j['id'] for j in self.store.by_state('queued', 'running')}
        try:
            names = sorted(n for n in os.listdir(self.runs_dir) if n.startswith('j'))
        except OSError:
            return
        sizes = []
        for name in names:
            path = os.path.join(self.runs_dir, name)
            if name in live or os.path.exists(os.path.join(path, 'keep')):
                continue
            job = self.store.get(name)
            failed = job and job['exit'] not in (0, None)
            age = now - os.path.getmtime(path)
            limit = (cfg['retention_failed_days'] if failed else cfg['retention_days']) * 86400
            if age > limit:
                shutil.rmtree(path, ignore_errors=True)
                continue
            sizes.append((os.path.getmtime(path), path))
        total = 0
        for mtime, path in sorted(sizes, reverse=True):
            size = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(path) for f in fs if not os.path.islink(os.path.join(dp, f)))
            total += size
            if total > cfg['retention_total_gb'] * 1e9:
                shutil.rmtree(path, ignore_errors=True)

    # ------------------------------------------------------------ scheduling

    def schedule(self):
        self.dirty = False
        self.scanned = time.monotonic()
        now = time.time()
        cfg = self.cfg
        rows = self.store.by_state('queued', 'running')
        running_rows = [j for j in rows if j['state'] == 'running']
        runq_lanes = {lane for j in running_rows for lane in (j['lanes'] or [])}
        shims = []
        for j in running_rows:
            info = shim.read_json(os.path.join(j['dir'], 'shim.json'), {}) or {}
            if info.get('pid'):
                shims.append(info['pid'])
        ext, lanes_free = legacy.scan(cfg, self.lock_dir, self.prefix, os.getpid(), runq_lanes, shims=shims)
        # A wrapped script that takes a legacy claim its own job's shim holds waits forever.
        for job_id, name in legacy.self_waiters(self.lock_dir, self.prefix):
            j = next((r for r in running_rows if r['id'] == job_id), None)
            if j and j['reason'] is None:
                why = f'self-deadlock: a process of the job waits for {name}, which runq already holds for it (drop the script\'s own claim)'
                self.store.update(job_id, reason=why, exit=EXIT_KILLED)
                self.stop_unit(j['unit'])
                log(f'{job_id}: {why}; stopping')
        why = list(ext.why)
        with self.worker.lock:
            games, games_at, in_oni, ext_cpu = list(self.worker.games), self.worker.games_at, self.worker.user_in_oni, self.worker.ext_cpu
        wants_windows = any((j['needs'] or {}).get('windows') or ((j['phase'] or {}).get('needs') or {}).get('windows') for j in rows)
        self.windows_relevant = wants_windows
        live_ids = {j['id'] for j in running_rows}
        if self.windows is None:
            if wants_windows:
                ext.windows = True
                why.append('no Windows backend here (not WSL, or interop is off)')
        else:
            for g in games:
                cmd = g.get('cmdline', '')
                if '\\oni-runq\\j' in cmd:
                    owner_id = cmd.split('\\oni-runq\\', 1)[1].split('\\', 1)[0]
                    if owner_id not in live_ids:
                        ext.windows = True
                        why.append(f'game pid {g.get("pid")} of ended job {owner_id} (being killed)')
                        self.kill_leftover(owner_id)
                elif winenv.legacy_test_game(cmd):
                    ext.windows = True
                    why.append(f'a test game outside runq, pid {g.get("pid")} ({g.get("name")})')
            if wants_windows and now - games_at > 90:
                ext.windows = True
                why.append('the Windows side is being checked')
                self.worker.want_windows.set()
            # Violations of the Windows rules by running jobs' games.
            for j in running_rows:
                if not (j['needs'] or {}).get('windows'):
                    continue
                env = shim.read_json(os.path.join(j['dir'], 'env.json'), {}) or {}
                found = []
                for g in games:
                    if any(m in g.get('cmdline', '') for m in winenv.run_home_matches(j['id'], env)):
                        found += [f'game pid {g.get("pid")}: {v}' for v in winenv.violations(g['cmdline'], cfg['win_monitor'])]
                keep = [w for w in self.warnings.get(j['id'], []) if w.startswith('idle')]
                self.warnings[j['id']] = keep + found
            with self.worker.lock:
                self.worker.boost_matches = [
                    (j['id'], m)
                    for j in running_rows
                    if cfg['kinds'].get(j['kind'], {}).get('boost') or ((j['needs'] or {}).get('perf') and (j['needs'] or {}).get('windows'))
                    for m in winenv.run_home_matches(j['id'], shim.read_json(os.path.join(j['dir'], 'env.json'), {}) or {})
                ]
        ext.why = tuple(dict.fromkeys(why))
        self.external, self.lanes_free = ext, lanes_free
        mono = time.monotonic()
        srunning, squeued = [], []
        for j in running_rows:
            end = now + max(self.deadlines.get(j['id'], mono + j['timeout']) - mono, 0)
            srunning.append(sched.Running(j['id'], j['owner'], j['level'], j['needs'] or {}, end, tuple(j['lanes'] or ())))
            ph = j['phase'] or {}
            if ph.get('state') == 'waiting':
                squeued.append(
                    sched.Job(
                        j['id'],
                        j['owner'],
                        j['level'],
                        j['ord'],
                        ph['needs'],
                        max(self.deadlines.get(j['id'], mono) - mono, 60),
                        boost=bool(j['boost']),
                        quiet_since=j['quiet_since'],
                        phase=True,
                    )
                )
        for j in rows:
            if j['state'] != 'queued':
                continue
            blocked = self.after_blocked(j)
            if blocked:
                self.reasons[j['id']] = blocked
                if blocked.startswith('cancelled'):
                    self.store.update(j['id'], state='cancelled', end=now, exit=EXIT_KILLED, reason=blocked)
                    self.notify(j['id'], self.end_event(self.store.get(j['id'])))
                continue
            squeued.append(
                sched.Job(j['id'], j['owner'], j['level'], j['ord'], j['needs'] or {}, j['timeout'], boost=bool(j['boost']), quiet_since=j['quiet_since'])
            )
        state = sched.State(
            now=now,
            cfg=cfg,
            running=srunning,
            queued=squeued,
            lanes_free=lanes_free,
            external=ext,
            service=self.store.service(now),
            ext_cpu=ext_cpu,
            user_in_oni=in_oni,
        )
        p = sched.plan(state)
        for jid, t in p.quiet_started.items():
            self.store.update(jid, quiet_since=t)
        self.reasons.update(p.reasons)
        self.starts = p.starts
        self.order = p.order
        by_id = {j['id']: j for j in rows}
        for g in p.grants:
            job = by_id[g.id]
            self.reasons.pop(g.id, None)
            self.grant(self.store.get(g.id) if job['state'] == 'running' else job, g)
        if p.drain:
            self.wanted_written = legacy.write_wanted(self.lock_dir, os.getpid()) or self.wanted_written
        elif self.wanted_written:
            legacy.clear_wanted(self.lock_dir, os.getpid())
            self.wanted_written = False
        if p.grants:
            self.dirty = True

    def after_blocked(self, job):
        for dep in job['after'] or []:
            d = self.store.get(dep)
            if d is None:
                continue
            if d['state'] not in TERMINAL:
                return f'waits for {dep} to end (--after)'
            if job['after_ok'] and d['exit'] != 0:
                return f'cancelled: {dep} failed (exit {d["exit"]}) and --after-ok was given'
        return None

    def kill_leftover(self, job_id):
        """A game of a job that is not running any more: runq started it, so it may kill it, by
        its job folder only."""
        if not self.windows or job_id in getattr(self, '_killing', set()):
            return
        self._killing = getattr(self, '_killing', set()) | {job_id}

        def kill():
            try:
                out = self.windows.kill(winenv.job_match(job_id))
                log(f'leftover game of {job_id}: {out.strip() or "nothing to kill"}')
            finally:
                self._killing.discard(job_id)
                self.worker.want_windows.set()

        threading.Thread(target=kill, daemon=True).start()

    # ------------------------------------------------------------ status

    def status(self, owner=None):
        now = time.time()
        rows = self.store.by_state('queued', 'running')
        c = self.cfg['capacity']
        running = [self.describe(j) for j in rows if j['state'] == 'running']
        order = {jid: i for i, jid in enumerate(self.order)}
        queued = sorted(
            (self.describe(j) for j in rows if j['state'] == 'queued' or (j['phase'] or {}).get('state') == 'waiting'), key=lambda d: order.get(d['id'], 1e9)
        )
        for d in queued:
            job = next(j for j in rows if j['id'] == d['id'])
            bound = self.cfg['due_minutes'].get(job['level'], 0) * 60
            d['due_at'] = job['ord'] + bound
        used = {'lanes': sum(len(j['lanes'] or []) for j in rows if j['state'] == 'running')}
        for r in ('mem', 'cpu', 'players'):
            used[r] = sum(float((j['needs'] or {}).get(r, 0) or 0) for j in rows if j['state'] == 'running')
        holds = {}
        for (o, level), hours in self.store.service(now).items():
            holds[o] = holds.get(o, 0.0) + hours
        if owner:
            running = [d for d in running if d['owner'] == owner]
            queued = [d for d in queued if d['owner'] == owner]
        ext = self.external
        return {
            'version': self.version,
            'pid': os.getpid(),
            'now': now,
            'machine': {
                'windows': 'busy' if any((j['needs'] or {}).get('windows') for j in rows if j['state'] == 'running') or ext.windows else 'free',
                'perf': 'busy' if any((j['needs'] or {}).get('perf') for j in rows if j['state'] == 'running') or ext.perf else 'free',
                'lanes_used': used['lanes'],
                'lanes_free': len(self.lanes_free),
                'mem_used': used['mem'] + ext.mem,
                'mem_cap': c['mem_gb'],
                'cpu_used': used['cpu'] + ext.cpu,
                'cpu_cap': c['cpu'],
                'players_used': used['players'] + ext.players,
                'players_cap': c['players'],
                'ext_cpu': self.worker.ext_cpu,
                'user_in_oni': self.worker.user_in_oni,
            },
            'running': running,
            'queued': queued,
            'external': list(ext.why),
            'warnings': {k: v for k, v in self.warnings.items() if v},
            'owners': holds,
        }


def main():
    d = Daemon()
    return d.serve()
