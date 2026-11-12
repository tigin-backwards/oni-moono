"""runq-shim: the first process of every job, its unit's main process (§3.1).

It takes the legacy locks of the job's grant (legacy.py), starts the command as its child with
the job's environment (env.json), its output in out.log, and writes result.json when the command
ends. It stays the unit's main process, so the exit status survives a daemon restart, and the
locks it holds (close-on-exec fds, its own) last exactly as long as the unit: when it exits,
systemd stops what is left in the unit.

The daemon changes the grant mid-run for `runq phase`: it writes grant.json with a new seq and
sends SIGUSR2; the shim releases what it holds, takes the new grant's locks, and answers in
locks.json. A legacy lock taken by an old script in between is a lost race: before the start, the
shim exits LOST_RACE without running anything (the daemon requeues the job ahead); in a phase it
answers ok: false and holds nothing (the daemon requeues the phase).

SIGTERM, SIGINT, SIGHUP (systemd stopping the unit signals every process in it) do not end the
shim before its command: it waits for the command, then writes the result."""

import json, os, signal, subprocess, sys, time

from . import legacy, winenv

LOST_RACE = 98


def read_json(path, default=None):
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def write_json(path, data):
    temp = f'{path}.tmp{os.getpid()}'
    with open(temp, 'w') as f:
        json.dump(data, f, indent=1)
    os.replace(temp, path)


class Holds:
    """The legacy locks this process holds for its current grant."""

    def __init__(self, job, lock_dir):
        self.job, self.lock_dir = job, lock_dir
        self.fds, self.players, self.wanted = [], False, False

    def release(self):
        for fd in self.fds:
            os.close(fd)
        self.fds = []
        if self.players:
            legacy.remove_players(self.lock_dir)
            self.players = False
        if self.wanted:
            legacy.clear_wanted(self.lock_dir, os.getpid())
            self.wanted = False

    def take(self, grant):
        """The grant's locks in the one order (perf, then windows, then lanes), none blocking;
        LostRace leaves nothing held."""
        self.release()
        if not grant.get('bridge', True):
            return
        needs = grant.get('needs', {})
        try:
            if needs.get('perf'):
                self.fds.append(legacy.take_flock(os.path.join(self.lock_dir, 'perf.lock')))
                allowed = [p for p in (grant.get('daemon_pid'),) if p]
                if not legacy.write_wanted(self.lock_dir, os.getpid(), allowed):
                    raise legacy.LostRace('perf.wanted')
                self.wanted = True
            if needs.get('windows'):
                self.fds.append(legacy.take_flock(os.path.join(self.lock_dir, 'windows-game.lock')))
            if grant.get('lanes') and grant.get('lane_locks', 'shim') == 'shim':
                self.fds += legacy.take_lanes(self.lock_dir, grant['lanes'], self.job['id'])
            players = int(needs.get('players', 0) or 0)
            if players:
                legacy.add_players(self.lock_dir, self.job['id'], players, float(needs.get('mem', 0) or 0), self.job.get('cwd', ''))
                self.players = True
        except legacy.LostRace:
            self.release()
            raise


def main(job_dir):
    job = read_json(os.path.join(job_dir, 'job.json'))
    env = read_json(os.path.join(job_dir, 'env.json'))
    grant_path = os.path.join(job_dir, 'grant.json')
    grant = read_json(grant_path)
    holds = Holds(job, grant['lock_dir'])
    write_json(os.path.join(job_dir, 'shim.json'), {'pid': os.getpid(), 'start': legacy.process_start(os.getpid())})
    flags = {'regrant': False, 'stop': None}

    def on_stop(signum, frame):
        flags['stop'] = signum

    def on_regrant(signum, frame):
        flags['regrant'] = True

    for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(s, on_stop)
    signal.signal(signal.SIGUSR2, on_regrant)
    try:
        holds.take(grant)
    except legacy.LostRace as e:
        write_json(os.path.join(job_dir, 'result.json'), {'lost_race': str(e), 'end': time.time(), 'attempt': grant.get('attempt')})
        return LOST_RACE
    write_json(os.path.join(job_dir, 'locks.json'), {'seq': grant.get('seq', 0), 'ok': True})
    if grant.get('windows_home'):
        try:
            winenv.mute_prefs(grant['windows_home'])
        except OSError as e:
            print(f'runq-shim: could not mute the run home {grant["windows_home"]}: {e}', file=sys.stderr)
    os.makedirs(os.path.join(job_dir, 'out'), exist_ok=True)
    log = open(os.path.join(job_dir, 'out.log'), 'ab', buffering=0)
    started = time.time()
    try:
        child = subprocess.Popen(job['argv'], cwd=job['cwd'], env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, close_fds=True)
    except OSError as e:
        log.write(f'runq: could not start {job["argv"][0]}: {e}\n'.encode())
        holds.release()
        write_json(os.path.join(job_dir, 'result.json'), {'exit': 127, 'end': time.time(), 'error': str(e)})
        return 127
    write_json(os.path.join(job_dir, 'shim.json'), {'pid': os.getpid(), 'start': legacy.process_start(os.getpid()), 'child': child.pid})
    seq = grant.get('seq', 0)
    status = None
    while status is None:
        try:
            pid, raw = os.waitpid(child.pid, os.WNOHANG)
        except ChildProcessError:
            status = 255
            break
        if pid:
            status = os.waitstatus_to_exitcode(raw)
            status = 128 - status if status < 0 else status
            break
        if flags['regrant']:
            flags['regrant'] = False
            g = read_json(grant_path, {})
            if g.get('seq', 0) > seq:
                seq = g['seq']
                try:
                    holds.take(g)
                    write_json(os.path.join(job_dir, 'locks.json'), {'seq': seq, 'ok': True})
                except legacy.LostRace as e:
                    write_json(os.path.join(job_dir, 'locks.json'), {'seq': seq, 'ok': False, 'lost': str(e)})
                if g.get('windows_home'):
                    try:
                        winenv.mute_prefs(g['windows_home'])
                    except OSError:
                        pass
        time.sleep(0.2)
    holds.release()
    result = {'exit': status, 'start': started, 'end': time.time()}
    if flags['stop']:
        result['signal'] = flags['stop']
    write_json(os.path.join(job_dir, 'result.json'), result)
    return status
