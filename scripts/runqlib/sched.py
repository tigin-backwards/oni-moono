"""The scheduler: plan(state) -> Plan, a pure function of the machine's state (§6).

Each pass orders the queued jobs (due jobs first by submit time, then by level, then round-robin
between owners by their service over the last 24 h, then by submit time) and walks them:

- a job starts only when every resource it needs is free now, counting the runq jobs running and
  the occupants outside runq (the legacy locks' holders), all or nothing;
- a job that cannot start gets a reservation: the earliest time its resources are free, from the
  running jobs' deadlines (their timeouts, not estimates) and the reservations before it. A later
  job starts now only if it fits beside every reservation over its own whole timeout window
  (conservative backfill), so it never delays an earlier job's reservation;
- occupants outside runq have no known end: they count now, and are left out of the future (a
  reservation assumes they end at once, the earliest it could start), so a perf job at the head
  still drains the machine of new runq work.

Resources: lane (count of lane numbers), mem (GB), cpu (cores), players (count), windows (the
slot), perf (exclusive timing: while one runs, no other job that loads the machine runs; a job
with no resources at all, kind light, still does). quiet is a flag on perf: the external CPU must
be under the configured cores, waited for at most quiet_wait_minutes, then the job starts noisy.
A win-perf job (perf and windows) never starts while the user is in ONI and holds no reservation
meanwhile (others keep running)."""

import dataclasses, math

RESOURCES = ('lane', 'mem', 'cpu', 'players', 'windows', 'perf')
EPS = 1e-9


@dataclasses.dataclass
class Job:
    """A queued request: a new job, or a running job's phase request (`runq phase`)."""

    id: str
    owner: str
    level: str
    order: float  # the original submit time: a phase keeps its job's place in line
    needs: dict
    timeout: float  # seconds
    boost: bool = False  # requeued after losing a race for a legacy lock: goes first
    quiet_since: float | None = None  # when it first fitted but for the machine's quiet
    phase: bool = False  # a running job's phase request (it already counts as running)


@dataclasses.dataclass
class Running:
    id: str
    owner: str
    level: str
    needs: dict
    end: float  # its deadline (start + timeout), in the plan's clock
    lanes: tuple = ()


@dataclasses.dataclass
class External:
    """What occupies the machine outside runq now (the legacy bridge, §3.5)."""

    cpu: float = 0.0
    mem: float = 0.0
    players: int = 0
    perf: bool = False  # an exclusive perf.lock holder or a legacy perf job
    perf_shared: int = 0  # shared perf.lock holders (spec runs): a perf job waits for them
    windows: bool = False  # windows-game.lock, a legacy run's game, a leftover game
    load: int = 0  # jobs outside runq that load the machine (players, perf jobs)
    why: tuple = ()  # what they are, in words, for the reasons


@dataclasses.dataclass
class State:
    now: float
    cfg: dict
    running: list
    queued: list
    lanes_free: tuple  # lane numbers free now (not held by runq or outside it, not reserved)
    external: External = dataclasses.field(default_factory=External)
    service: dict = dataclasses.field(default_factory=dict)  # (owner, level) -> hours in 24 h
    ext_cpu: float = 0.0  # cores busy outside runq, sampled
    user_in_oni: bool = False


@dataclasses.dataclass
class Grant:
    id: str
    lanes: tuple
    noisy: bool = False


@dataclasses.dataclass
class Plan:
    grants: list
    reasons: dict  # job id -> why it waits, in words
    starts: dict  # job id -> its reservation's start (math.inf: not known)
    quiet_started: dict  # job id -> now, for jobs that began waiting for quiet in this pass
    drain: bool  # a perf job waits for load to end: legacy spec runners should not start more
    order: list  # the queued jobs' ids in start order


def capacity(cfg, lanes_total):
    c = cfg['capacity']
    return {
        'lane': lanes_total,
        'mem': float(c['mem_gb']),
        'cpu': float(c['cpu']),
        'players': int(c['players']),
        'windows': int(c['windows']),
        'perf': int(c['perf']),
    }


KEYS = RESOURCES + ('load',)
LANE, MEM, CPU, PLAYERS, WINDOWS, PERF, LOAD = range(7)
ZERO = (0.0,) * 7


def usage(needs, cap):
    """A job's counted usage as a tuple over KEYS: cpu and mem clamped to the capacity (a job
    bigger than the machine runs alone), and load, 1 when it uses anything."""
    c = cap if isinstance(cap, tuple) else cap_vector(cap)
    u = [float(needs.get(r, 0) or 0) for r in RESOURCES]
    u[CPU] = min(u[CPU], c[CPU])
    u[MEM] = min(u[MEM], c[MEM])
    u.append(1.0 if any(x > 0 for x in u) else 0.0)
    return tuple(u)


def is_light(needs):
    return not any(float(needs.get(r, 0) or 0) > 0 for r in RESOURCES)


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def cap_vector(cap):
    return tuple(float(cap[r]) for r in RESOURCES)


def over(total, cap):
    """The first constraint TOTAL (a usage tuple) breaks, or None."""
    c = cap if isinstance(cap, tuple) else cap_vector(cap)
    for i in (WINDOWS, PERF, LANE, PLAYERS, MEM, CPU):
        if total[i] > c[i] + EPS:
            return RESOURCES[i]
    if total[PERF] > EPS and total[LOAD] > 1 + EPS:
        return 'perf-exclusive'
    return None


def external_usage(ext):
    return (0.0, ext.mem, ext.cpu, float(ext.players), 1.0 if ext.windows else 0.0, 1.0 if ext.perf else 0.0, float(ext.load))


def conflict_at(u, t, entries, cap, extra=None):
    total = list(u)
    for eu, start, end in entries:
        if start <= t + EPS and t < end - EPS:
            for i in range(7):
                total[i] += eu[i]
    if extra:
        for i in range(7):
            total[i] += extra[i]
    return over(total, cap)


def fits_window(u, start, end, entries, cap, extra_now=None, now=None):
    """None when U fits over [START, END) beside ENTRIES (usage, start, end); else the broken
    constraint. EXTRA_NOW (the occupants outside runq) counts at NOW only."""
    points = {start} | {s for _, s, _ in entries if start < s < end}
    for t in sorted(points):
        why = conflict_at(u, t, entries, cap, extra_now if (extra_now and t == now) else None)
        if why:
            return why
    return None


def earliest_start(u, duration, entries, cap, now):
    """The earliest time from NOW when U fits for DURATION beside ENTRIES; math.inf if never."""
    candidates = sorted({now} | {e for _, _, e in entries if e > now})
    for t in candidates:
        if not fits_window(u, t, t + duration, entries, cap):
            return t
    return math.inf


def ordered(state):
    """The queued jobs in start order (§6.1)."""
    cfg, now = state.cfg, state.now
    levels = {name: i for i, name in enumerate(cfg['levels'])}
    bounds = cfg['due_minutes']
    due, by_level = [], {}
    for job in state.queued:
        bound = float(bounds.get(job.level, 0)) * 60
        if job.boost:
            due.append((-1, job.order, job.id, job))
        elif now - job.order >= bound:
            due.append((0, job.order, job.id, job))
        else:
            by_level.setdefault(job.level, []).append(job)
    out = [j for *_, j in sorted(due, key=lambda x: x[:3])]
    for level in sorted(by_level, key=lambda name: levels.get(name, len(levels))):
        jobs = by_level[level]
        owners = {}
        for job in sorted(jobs, key=lambda j: (j.order, j.id)):
            owners.setdefault(job.owner, []).append(job)
        rank = sorted(owners, key=lambda o: (state.service.get((o, level), 0.0), owners[o][0].order, o))
        keyed = []
        for pos, owner in enumerate(rank):
            for k, job in enumerate(owners[owner]):
                keyed.append(((k, pos), job))
        out += [job for _, job in sorted(keyed, key=lambda x: x[0])]
    return out


def describe_ext(ext):
    return ', '.join(ext.why) if ext.why else 'occupants outside runq'


WORDS = {
    'windows': 'the Windows slot',
    'perf': 'the perf slot',
    'perf-exclusive': 'the perf job to end (nothing else that loads the machine runs beside one)',
    'lane': 'a free lane',
    'players': 'a player slot',
    'mem': 'memory',
    'cpu': 'CPU cores',
}


def plan(state):
    cfg, now = state.cfg, state.now
    caps = cfg['caps']
    lanes_free = list(state.lanes_free)
    lanes_total = len(lanes_free) + sum(len(r.lanes) for r in state.running)
    cap = cap_vector(capacity(cfg, lanes_total))
    ext = external_usage(state.external)
    # A perf job also waits for the shared perf.lock holders (spec runs outside runq).
    entries = [(usage(r.needs, cap), now, max(r.end, now + EPS)) for r in state.running]
    running_per_owner = {}
    for r in state.running:
        running_per_owner[r.owner] = running_per_owner.get(r.owner, 0) + 1
    grants, reasons, starts, quiet_started = [], {}, {}, {}
    reservations = []
    drain = False
    order = ordered(state)
    max_reservations = 8
    quiet_wait = float(cfg['quiet_wait_minutes']) * 60
    for job in order:
        u = usage(job.needs, cap)
        # A phase request's job already counts as running: its old holds are released before the
        # request queues (the daemon removes them), so only the cap is not counted again.
        if not job.phase:
            limit = caps['running_per_owner_by_level'].get(job.level, caps['running_per_owner'])
            if running_per_owner.get(job.owner, 0) >= limit:
                reasons[job.id] = f'owner {job.owner} already runs {running_per_owner[job.owner]} job(s) (cap {limit})'
                continue
        end = now + job.timeout
        blocked = None
        if u[LANE] > len(lanes_free):
            blocked = 'lane'
        # Now beside what runs (in runq and outside it), and over its whole window beside the
        # reservations (which assume the occupants outside runq are gone by then).
        blocked = blocked or fits_window(u, now, end, entries, cap, ext, now) or fits_window(u, now, end, entries + reservations, cap)
        if not blocked and u[PERF] and state.external.perf_shared:
            blocked = 'perf-shared'
        gate = None
        noisy = False
        if not blocked and u[PERF] and job.needs.get('windows') and state.user_in_oni:
            gate = 'user-in-oni'
        elif not blocked and u[PERF] and job.needs.get('quiet') and state.ext_cpu >= float(cfg['quiet_cores']):
            since = job.quiet_since if job.quiet_since is not None else now
            if job.quiet_since is None:
                quiet_started[job.id] = now
            if now - since >= quiet_wait:
                noisy = True
            else:
                gate = 'quiet'
        if not blocked and not gate:
            lanes = tuple(lanes_free[: int(u[LANE])])
            del lanes_free[: int(u[LANE])]
            grants.append(Grant(job.id, lanes, noisy))
            entries.append((u, now, end))
            if not job.phase:
                running_per_owner[job.owner] = running_per_owner.get(job.owner, 0) + 1
            continue
        if gate == 'user-in-oni':
            reasons[job.id] = 'the user is in ONI: a perf number beside their game is worthless (waits, holds nothing)'
            starts[job.id] = math.inf
            continue
        if gate == 'quiet':
            since = job.quiet_since if job.quiet_since is not None else now
            left = max(0.0, quiet_wait - (now - since))
            reasons[job.id] = (
                f'{state.ext_cpu:.1f} cores busy outside runq (quiet is under {float(cfg["quiet_cores"]):g}); starts noisy in {left / 60:.0f} min at the latest'
            )
        elif blocked == 'perf-shared':
            reasons[job.id] = f'waits for {state.external.perf_shared} spec run(s) outside runq holding perf.lock shared ({describe_ext(state.external)})'
        elif blocked != 'lane' and reservations and not fits_window(u, now, end, entries, cap, ext, now):
            reasons[job.id] = 'held back: it would delay an earlier job\'s reservation (backfill only fits jobs that end before it)'
        else:
            what = WORDS.get(blocked, blocked)
            outside = ''
            if conflict_at(u, now, [], cap, ext) or (blocked == 'lane' and u[LANE] > len(lanes_free)):
                outside = f' (outside runq: {describe_ext(state.external)})' if state.external.why else ''
            reasons[job.id] = f'waits for {what}{outside}'
        if len(reservations) < max_reservations:
            t = now if gate == 'quiet' else earliest_start(u, job.timeout, entries + reservations, cap, now)
            if t < math.inf:
                reservations.append((u, t, t + job.timeout))
            starts[job.id] = t
            if u[PERF] and not (job.needs.get('windows') and (state.external.windows or any(e[0][WINDOWS] for e in entries))):
                drain = True
        # A due job held up by a perf job outside runq: perf.wanted asks the old perf claimers to
        # start no new run, so the wait ends with the current one instead of a chain of them.
        due = job.boost or now - job.order >= float(cfg['due_minutes'].get(job.level, 0)) * 60
        if gate is None and due and u[LOAD] and state.external.perf:
            fits_alone = fits_window(u, now, end, entries, cap) is None
            if fits_alone and fits_window(u, now, end, entries, cap, ext, now):
                drain = True
    return Plan(grants, reasons, starts, quiet_started, drain, [j.id for j in order])
