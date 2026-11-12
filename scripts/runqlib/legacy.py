"""The bridge to the old locks (§3.5), while scripts still take them directly.

In the lock directory (E2E_LOCK_DIR, default ~/.cache/oni-brutal-e2e; scripts/e2e.py has the
details): perf.lock (shared by spec runs, exclusive for a perf job), perf.wanted (the pid of a
perf job waiting, so no new spec starts), windows-game.lock (the Windows slot), lane-L.lock (a
runner's lane), players.json (the players' memory ledger, guarded by players.lock).

- runq jobs appear to old scripts: a job's shim takes the locks of its grant (shim.py, with the
  functions below) and writes players.json entries for its players.
- old scripts appear to runq: scan() reads /proc/locks, players.json and the process list, and
  counts what runs outside every runq unit as an occupant."""

import contextlib, fcntl, json, os, re, socket, threading, time

from . import sched


class LostRace(Exception):
    """A legacy lock the grant needs was taken in between (an old script won)."""


# ---------------------------------------------------------------- processes


def process_start(pid):
    try:
        with open(f'/proc/{pid}/stat') as f:
            return int(f.read().rsplit(')', 1)[1].split()[19])
    except (OSError, IndexError, ValueError):
        return None


def parent(pid):
    try:
        with open(f'/proc/{pid}/stat') as f:
            return int(f.read().rsplit(')', 1)[1].split()[1])
    except (OSError, IndexError, ValueError):
        return None


def alive(pid):
    return pid is not None and os.path.exists(f'/proc/{pid}')


def alive_owner(entry):
    return entry.get('pid') is not None and process_start(entry['pid']) == entry.get('start')


def runq_job_of(pid, prefix='runq'):
    """The runq job id whose unit PID runs in (its cgroup), else None."""
    try:
        with open(f'/proc/{pid}/cgroup') as f:
            text = f.read()
    except OSError:
        return None
    match = re.search(rf'/{re.escape(prefix)}-(j\d+)(?:-\d+)?\.service(?:/|$)', text, re.M)
    if match:
        return match.group(1)
    return None


def job_env_of(pid):
    """RUNQ_JOB from PID's environment (players a job starts in scopes of their own), or None."""
    try:
        with open(f'/proc/{pid}/environ', 'rb') as f:
            for item in f.read().split(b'\0'):
                if item.startswith(b'RUNQ_JOB='):
                    return item[9:].decode(errors='replace')
    except OSError:
        pass
    return None


def processes():
    """[(pid, args)] of every process this user can see."""
    found = []
    for name in os.listdir('/proc'):
        if not name.isdigit():
            continue
        try:
            with open(f'/proc/{name}/cmdline', 'rb') as f:
                args = f.read().replace(b'\0', b' ').decode(errors='replace').strip()
        except OSError:
            continue
        if args:
            found.append((int(name), args))
    return found


def strip_shell(args):
    return re.sub(r'^(\S*/)?(env )?(\S*/)?(ba)?sh ', '', re.sub(r'^/usr/bin/env ', '', args.strip()))


# ---------------------------------------------------------------- flocks


def flock_holders(path):
    """[(pid, 'shared' or 'exclusive')] holding flocks on PATH, from /proc/locks. The pid is the
    one that took the lock (an fd inherited by children keeps it held)."""
    try:
        inode = os.stat(path).st_ino
        with open('/proc/locks') as f:
            lines = f.read().splitlines()
    except OSError:
        return []
    found = []
    for line in lines:
        fields = line.split()
        if '->' in fields:
            continue  # a waiter
        if len(fields) >= 6 and fields[1] == 'FLOCK' and fields[5].rsplit(':', 1)[-1] == str(inode):
            found.append((int(fields[4]), 'exclusive' if fields[3] == 'WRITE' else 'shared'))
    return found


def take_flock(path, exclusive=True):
    """An fd holding PATH's flock, taken without blocking (close-on-exec: only the caller holds
    it); LostRace when someone else holds it."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND | os.O_CLOEXEC, 0o644)
    try:
        fcntl.flock(fd, (fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH) | fcntl.LOCK_NB)
    except BlockingIOError:
        os.close(fd)
        raise LostRace(os.path.basename(path))
    return fd


def wanted_pid(lock_dir):
    try:
        with open(os.path.join(lock_dir, 'perf.wanted')) as f:
            return int(f.read().split()[0])
    except (OSError, ValueError, IndexError):
        return None


def write_wanted(lock_dir, pid, allowed=()):
    """perf.wanted names PID, unless a live process outside ALLOWED (pids that may be replaced)
    holds it. True when it names PID afterwards."""
    os.makedirs(lock_dir, exist_ok=True)
    path = os.path.join(lock_dir, 'perf.wanted')
    current = wanted_pid(lock_dir)
    if current == pid:
        return True
    if current is not None and alive(current) and current not in allowed:
        return False
    temp = f'{path}.runq.{os.getpid()}'
    with open(temp, 'w') as f:
        f.write(f'{pid}\n')
    os.replace(temp, path)
    return True


def clear_wanted(lock_dir, pid):
    """Removes perf.wanted when it names PID."""
    if wanted_pid(lock_dir) == pid:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(os.path.join(lock_dir, 'perf.wanted'))


# ---------------------------------------------------------------- players.json


@contextlib.contextmanager
def ledger_lock(lock_dir):
    os.makedirs(lock_dir, exist_ok=True)
    with open(os.path.join(lock_dir, 'players.lock'), 'a+') as f:
        fcntl.flock(f, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(f, fcntl.LOCK_UN)


def read_ledger(lock_dir):
    try:
        with open(os.path.join(lock_dir, 'players.json')) as f:
            entries = json.load(f)
    except (OSError, ValueError):
        return []
    return [e for e in entries if isinstance(e, dict) and alive_owner(e)]


def write_ledger(lock_dir, entries):
    path = os.path.join(lock_dir, 'players.json')
    temp = f'{path}.{os.getpid()}.{threading.get_ident()}'
    with open(temp, 'w') as f:
        json.dump(entries, f, indent=1)
    os.replace(temp, path)


def add_players(lock_dir, job_id, count, gb, root):
    """COUNT entries in players.json for this process (the shim), GB in all."""
    pid = os.getpid()
    record = {'pid': pid, 'start': process_start(pid), 'root': root, 'out': '', 'since': time.strftime('%Y-%m-%dT%H:%M:%S')}
    with ledger_lock(lock_dir):
        entries = read_ledger(lock_dir)
        for i in range(count):
            entries.append(dict(record, token=f'runq-{job_id}-{i}-{time.time_ns()}', gb=round(gb / count, 3), label=f'runq {job_id}'))
        write_ledger(lock_dir, entries)


def remove_players(lock_dir, pid=None):
    pid = os.getpid() if pid is None else pid
    with ledger_lock(lock_dir):
        write_ledger(lock_dir, [e for e in read_ledger(lock_dir) if e.get('pid') != pid])


# ---------------------------------------------------------------- lanes


def lane_ports(lane):
    return 8800 + 10 * lane, 8801 + 10 * lane


def lane_display(lane):
    return int(os.environ.get('E2E_DISPLAY_BASE') or 300) + lane


def port_open(port):
    with socket.socket() as s:
        s.settimeout(0.2)
        return s.connect_ex(('127.0.0.1', port)) == 0


def display_in_use(number):
    if os.path.exists(f'/tmp/.X11-unix/X{number}'):
        try:
            with socket.socket(socket.AF_UNIX) as s:
                s.settimeout(0.2)
                s.connect(f'/tmp/.X11-unix/X{number}')
                return True
        except OSError:
            pass
    try:
        with open(f'/tmp/.X{number}-lock') as f:
            pid = int(f.read().strip())
    except FileNotFoundError:
        return False
    except (OSError, ValueError):
        return True
    return alive(pid)


def parse_ranges(text):
    numbers = set()
    for part in re.split(r'[\s,]+', text.strip()):
        if not part:
            continue
        match = re.fullmatch(r':?(\d+)(?:-:?(\d+))?', part)
        if not match:
            raise ValueError(f'bad number or range {part!r}')
        low, high = int(match.group(1)), int(match.group(2) or match.group(1))
        numbers.update(range(low, high + 1))
    return numbers


def reservations(lock_dir, extra=''):
    """(ports, displays) of the lock directory's reserved file and E2E_RESERVED."""
    lines = []
    path = os.path.join(lock_dir, 'reserved')
    if os.path.exists(path):
        with open(path) as f:
            lines += f.read().splitlines()
    lines += (extra or '').split(';')
    ports, displays = set(), set()
    for line in lines:
        line = line.split('#', 1)[0].strip()
        if not line:
            continue
        kind, _, rest = line.partition(' ')
        if kind in ('ports', 'displays'):
            with contextlib.suppress(ValueError):
                (ports if kind == 'ports' else displays).update(parse_ranges(rest))
    return ports, displays


def take_lanes(lock_dir, lanes, job_id):
    """fds holding lane-L.lock for each lane, with a record in the lock as e2e.py writes one."""
    fds = []
    try:
        for lane in lanes:
            fd = take_flock(os.path.join(lock_dir, f'lane-{lane}.lock'))
            fds.append(fd)
            record = {
                'pid': os.getpid(),
                'start': process_start(os.getpid()),
                'root': f'runq {job_id}',
                'out': '',
                'since': time.strftime('%Y-%m-%dT%H:%M:%S'),
                'lane': lane,
                'state': f'runq job {job_id}',
                'children': [],
            }
            os.ftruncate(fd, 0)
            os.write(fd, json.dumps(record).encode())
    except LostRace:
        for fd in fds:
            os.close(fd)
        raise
    return fds


# ---------------------------------------------------------------- the scan


def scan(cfg, lock_dir, prefix='runq', daemon_pid=None, runq_lanes=(), procs=None, port_busy=port_open, display_busy=display_in_use, shims=()):
    """What runs outside runq now: (External, free lanes). RUNQ_LANES: lanes runq jobs hold
    (their ports and displays are theirs to use). SHIMS: the running jobs' shim pids.

    A legacy lock or ledger entry counts as runq's only when the daemon or a job's shim holds it:
    an old script run inside a runq job (taking the locks itself) is still an occupant runq did
    not grant. A process found by its command line (qa.sh, a perf job) is runq's when it runs in
    any runq unit."""
    ext_why = []
    ext = sched.External()
    shims = set(shims)
    ours = lambda pid: pid == os.getpid() or pid == daemon_pid or pid in shims
    in_unit = lambda pid: ours(pid) or runq_job_of(pid, prefix) is not None
    if not cfg.get('legacy_bridge', True):
        lock_dir = None
    if lock_dir:
        for pid, mode in flock_holders(os.path.join(lock_dir, 'perf.lock')):
            if ours(pid):
                continue
            if mode == 'exclusive':
                ext.perf = True
                ext.load += 1
                ext_why.append(f'perf.lock held exclusively by pid {pid}')
            else:
                ext.perf_shared += 1
                ext_why.append(f'perf.lock shared by pid {pid}')
        wanted = wanted_pid(lock_dir)
        if wanted is not None and alive(wanted) and not ours(wanted):
            ext.perf = True
            ext_why.append(f'perf.wanted names pid {wanted} (a perf job outside runq waits)')
        for pid, _ in flock_holders(os.path.join(lock_dir, 'windows-game.lock')):
            if not ours(pid):
                ext.windows = True
                ext_why.append(f'windows-game.lock held by pid {pid}')
        for e in read_ledger(lock_dir):
            if ours(e['pid']):
                continue
            ext.mem += float(e.get('gb', 0))
            ext.players += 1
            ext.cpu += float(cfg['legacy_player_cpu'])
            ext.load += 1
            ext_why.append(f'player {e.get("label", "?")} ({float(e.get("gb", 0)):g} GB, pid {e["pid"]})')
    procs = processes() if procs is None else procs
    patterns = [re.compile(p) for p in cfg['legacy_perf_patterns']]
    qa = {}
    for pid, args in procs:
        if pid == os.getpid():
            continue
        bare = strip_shell(args)
        is_qa = re.search(r'(^|/)scripts/qa\.sh\b', bare) and not bare.startswith(('flock', 'grep'))
        is_perf = any(p.search(bare) for p in patterns)
        if not (is_qa or is_perf) or in_unit(pid):
            continue
        if is_perf:
            ext.perf = True
            ext.load += 1
            ext_why.append(f'perf job pid {pid}: {bare[:80]}')
        elif is_qa:
            qa[pid] = bare
    # A qa.sh's subshells carry its command line: only the top one counts.
    for pid in qa:
        if parent(pid) in qa or float(cfg['legacy_qa_gb']) <= 0:
            continue
        ext.mem += float(cfg['legacy_qa_gb'])
        ext.players += 1
        ext.load += 1
        ext_why.append(f'qa.sh pid {pid}')
    ports, displays = reservations(lock_dir or '/nonexistent', os.environ.get('E2E_RESERVED', ''))
    c = cfg['capacity']
    free = []
    for lane in range(int(c['lane_first']), int(c['lane_last']) + 1):
        if lane in runq_lanes:
            continue
        p = lane_ports(lane)
        if any(x in ports for x in p) or lane_display(lane) in displays:
            continue
        if lock_dir and any(not ours(pid) for pid, _ in flock_holders(os.path.join(lock_dir, f'lane-{lane}.lock'))):
            continue
        if any(port_busy(x) for x in p) or display_busy(lane_display(lane)):
            continue
        free.append(lane)
    ext.why = tuple(dict.fromkeys(ext_why))
    return ext, tuple(free)


def machine_busy_sample():
    """(busy jiffies, total jiffies) from /proc/stat's cpu line."""
    with open('/proc/stat') as f:
        fields = [int(x) for x in f.readline().split()[1:]]
    idle = fields[3] + (fields[4] if len(fields) > 4 else 0)
    return sum(fields[:8]) - idle, sum(fields[:8])


def self_waiters(lock_dir, prefix='runq'):
    """[(job id, lock name)]: processes in a runq job blocked on a legacy lock (perf.lock,
    windows-game.lock) that the job's own shim holds: a wrapped script taking the claim itself,
    which can never get it."""
    found = []
    for name in ('perf.lock', 'windows-game.lock'):
        path = os.path.join(lock_dir, name)
        try:
            inode = str(os.stat(path).st_ino)
            with open('/proc/locks') as f:
                lines = f.read().splitlines()
        except OSError:
            continue
        holders, waiters = set(), set()
        for line in lines:
            fields = line.split()
            blocked = '->' in fields
            if blocked:
                fields = [x for x in fields if x != '->']
            if len(fields) >= 6 and fields[1] == 'FLOCK' and fields[5].rsplit(':', 1)[-1] == inode:
                (waiters if blocked else holders).add(int(fields[4]))
        held_by = {runq_job_of(pid, prefix) for pid in holders} - {None}
        for pid in waiters:
            job = runq_job_of(pid, prefix)
            if job and job in held_by:
                found.append((job, name))
    return sorted(set(found))


def ancestors(pid=None):
    """PID (default this process) and its parents up to init."""
    pid = os.getpid() if pid is None else pid
    chain = []
    while pid and pid > 1 and pid not in chain:
        chain.append(pid)
        pid = parent(pid)
    return chain


def caller_holds(lock_dir, pid=None):
    """The legacy claim (perf.lock, windows-game.lock, perf.wanted) that this process or one of
    its parents holds, or None: a wrapper of the old kind calling a migrated script, which must
    then run directly (runq would wait for the wrapper's own lock forever)."""
    chain = set(ancestors(pid))
    for name in ('perf.lock', 'windows-game.lock'):
        if any(p in chain for p, _ in flock_holders(os.path.join(lock_dir, name))):
            return name
    wanted = wanted_pid(lock_dir)
    if wanted in chain:
        return 'perf.wanted'
    return None
