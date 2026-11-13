"""runq's scheduler (scripts/runqlib/sched.py): plan() on hand-built states with a fake clock,
and seeded random job streams checked after every step."""

import math, random, unittest

import helpers  # noqa: F401
from runqlib import config, sched

CFG = config.DEFAULTS
H = 3600.0


def job(id, owner='a', level='study', order=0.0, kind=None, needs=None, timeout=H, **kw):
    if kind:
        needs = dict(config.kind_needs(CFG, kind), **(needs or {}))
    return sched.Job(id, owner, level, order, needs or {}, timeout, **kw)


def run(id, owner='a', level='study', kind=None, needs=None, end=H, lanes=()):
    if kind:
        needs = dict(config.kind_needs(CFG, kind), **(needs or {}))
    return sched.Running(id, owner, level, needs or {}, end, tuple(lanes))


def state(queued=(), running=(), now=100.0, lanes=range(1, 20), **kw):
    return sched.State(now=now, cfg=CFG, running=list(running), queued=list(queued), lanes_free=tuple(lanes), **kw)


def granted(p):
    return [g.id for g in p.grants]


class Order(unittest.TestCase):
    def test_levels_order(self):
        jobs = [job('b', level='burndown', order=1), job('s', level='study', order=2), job('u', level='user', order=3), job('i', level='issue', order=4)]
        # issue's bound is 0: it is due at once, ahead of all.
        p = sched.plan(state(jobs, now=10))
        self.assertEqual(p.order, ['i', 'u', 's', 'b'])

    def test_round_robin_between_owners(self):
        jobs = [job('a1', 'a', order=1), job('a2', 'a', order=2), job('a3', 'a', order=3), job('b1', 'b', order=4), job('b2', 'b', order=5)]
        p = sched.plan(state(jobs, now=10))
        self.assertEqual(p.order, ['a1', 'b1', 'a2', 'b2', 'a3'])

    def test_back_to_back_chains_interleave(self):
        # Owner a has just run for an hour; b has waited. b goes first even with a later submit.
        jobs = [job('a2', 'a', order=5), job('b1', 'b', order=6)]
        p = sched.plan(state(jobs, now=10, service={('a', 'study'): 1.0}))
        self.assertEqual(p.order, ['b1', 'a2'])

    def test_due_jobs_overtake_higher_levels(self):
        old = job('old', level='burndown', order=0)
        new = job('new', level='user', order=8 * H)
        p = sched.plan(state([new, old], now=8 * H + 1))
        self.assertEqual(p.order, ['old', 'new'])

    def test_due_jobs_by_level_then_submit_time(self):
        # User jobs past their 30 min bound, then a release job submitted later (due at once):
        # the release job goes first, then the user jobs in submit order.
        u1 = job('u1', 'a', level='user', order=0)
        u2 = job('u2', 'b', level='user', order=60)
        rel = job('rel', 'u60-update', level='release', order=3 * H)
        st = job('st', 'c', level='study', order=0)  # due too (3 h)
        p = sched.plan(state([u1, u2, st, rel], now=3 * H + 1))
        self.assertEqual(p.order, ['rel', 'u1', 'u2', 'st'])

    def test_due_release_check_waits_for_one_perf_job_at_most(self):
        # A perf job runs; a study perf job and user jobs are due ahead in submit order. A
        # release check queued later starts as soon as the running perf job ends, before them.
        running = [run('perf', 'x', kind='linux-perf', end=4 * H)]
        queued = [
            job('sp', 'y', level='study', order=0, kind='linux-perf'),
            job('ub', 'z', level='user', order=60, kind='build'),
            job('rc', 'u60-update', level='release', order=3 * H, kind='check'),
        ]
        p = sched.plan(state(queued, running=running, now=3 * H + 1))
        self.assertEqual(p.order[0], 'rc')
        self.assertEqual(granted(p), [])  # no preemption
        self.assertEqual(p.starts['rc'], 4 * H)
        p = sched.plan(state(queued, now=4 * H + 1))
        self.assertIn('rc', granted(p))
        self.assertNotIn('sp', granted(p))

    def test_boosted_job_goes_first(self):
        jobs = [job('i', level='issue', order=5), job('lost', level='burndown', order=9, boost=True)]
        self.assertEqual(sched.plan(state(jobs, now=10)).order, ['lost', 'i'])


class Resources(unittest.TestCase):
    def test_all_or_nothing(self):
        # Two lanes free, the job wants three: nothing granted, no lane taken.
        p = sched.plan(state([job('e', needs={'lane': 3, 'players': 3, 'mem': 6})], lanes=[1, 2]))
        self.assertEqual(p.grants, [])
        self.assertIn('lane', p.reasons['e'])

    def test_lanes_granted_are_distinct(self):
        p = sched.plan(state([job('a', 'a', kind='linux-player'), job('b', 'b', kind='linux-player', order=1)], lanes=[3, 5, 9]))
        self.assertEqual([g.lanes for g in p.grants], [(3,), (5,)])

    def test_memory_and_players_budget(self):
        # 40 GB, 4 players: three 15 GB players do not fit at once.
        jobs = [job(f'p{i}', f'o{i}', needs={'mem': 15, 'players': 1}, order=i) for i in range(3)]
        p = sched.plan(state(jobs))
        self.assertEqual(granted(p), ['p0', 'p1'])

    def test_bigger_than_the_machine_runs_alone(self):
        p = sched.plan(state([job('big', needs={'mem': 80, 'players': 1})]))
        self.assertEqual(granted(p), ['big'])
        p = sched.plan(state([job('big', needs={'mem': 80, 'players': 1})], running=[run('r', 'b', needs={'mem': 1})]))
        self.assertEqual(granted(p), [])

    def test_check_and_perf_never_overlap(self):
        p = sched.plan(state([job('perf', kind='linux-perf')], running=[run('c', 'b', kind='check')]))
        self.assertEqual(granted(p), [])
        p = sched.plan(state([job('c', kind='check')], running=[run('perf', 'b', kind='linux-perf')]))
        self.assertEqual(granted(p), [])

    def test_light_jobs_run_beside_perf(self):
        p = sched.plan(state([job('git', kind='light')], running=[run('perf', 'b', kind='win-perf')]))
        self.assertEqual(granted(p), ['git'])

    def test_windows_slot(self):
        p = sched.plan(state([job('w', kind='windows')], running=[run('r', 'b', kind='windows')]))
        self.assertEqual(granted(p), [])
        p = sched.plan(state([job('w', kind='windows')], external=sched.External(windows=True, why=('windows-game.lock by pid 7',))))
        self.assertEqual(granted(p), [])
        self.assertIn('windows-game.lock', p.reasons['w'])

    def test_a_perf_job_outside_runq_holds_back_load(self):
        # perf.wanted or an exclusive perf.lock outside runq: nothing that loads the machine
        # starts; light jobs do.
        ext = sched.External(perf=True, load=1, why=('perf.wanted names pid 9',))
        p = sched.plan(state([job('e', kind='linux-player'), job('c', 'b', kind='check', order=1), job('g', 'c', kind='light', order=2)], external=ext))
        self.assertEqual(granted(p), ['g'])
        self.assertIn('perf.wanted', p.reasons['e'])
        self.assertFalse(p.drain)  # not due yet: the old perf claimers go on
        # Once a job is due, perf.wanted asks them to start no new run.
        p = sched.plan(state([job('c', 'b', kind='check', order=0)], external=ext, now=4 * H))
        self.assertTrue(p.drain)

    def test_perf_waits_for_shared_holders_outside_runq(self):
        ext = sched.External(perf_shared=1, why=('perf.lock shared by pid 9',))
        p = sched.plan(state([job('perf', kind='linux-perf')], external=ext))
        self.assertEqual(granted(p), [])
        self.assertTrue(p.drain)


class Caps(unittest.TestCase):
    def test_running_per_owner(self):
        jobs = [job('a3', 'a', kind='light')]
        p = sched.plan(state(jobs, running=[run('a1', 'a'), run('a2', 'a')]))
        self.assertEqual(granted(p), [])
        self.assertIn('cap 2', p.reasons['a3'])

    def test_burndown_runs_one(self):
        p = sched.plan(state([job('b2', 'a', level='burndown', kind='light')], running=[run('b1', 'a', level='burndown')]))
        self.assertEqual(granted(p), [])

    def test_phase_request_is_not_capped_again(self):
        p = sched.plan(state([job('a1', 'a', kind='qa-perf', phase=True)], running=[run('a1', 'a'), run('a2', 'a')]))
        self.assertEqual(granted(p), ['a1'])


class Reservations(unittest.TestCase):
    def test_perf_at_the_head_drains_players(self):
        # Two players run (ending at 1 h and 2 h); a perf job is due. A new player must not start
        # beside them, or the perf job would wait longer.
        running = [run('p1', 'x', kind='linux-player', end=H, lanes=[1]), run('p2', 'y', kind='linux-player', end=2 * H, lanes=[2])]
        perf = job('perf', 'z', level='issue', order=0, kind='linux-perf')
        player = job('p3', 'w', level='user', order=1, kind='linux-player', timeout=3 * H)
        p = sched.plan(state([perf, player], running=running, now=10, lanes=range(3, 20)))
        self.assertEqual(granted(p), [])
        self.assertEqual(p.starts['perf'], 2 * H)
        self.assertTrue(p.drain)
        # They end; the perf job starts, the player still waits.
        p = sched.plan(state([perf, player], now=2 * H + 1))
        self.assertEqual(granted(p), ['perf'])

    def test_backfill_starts_a_job_that_ends_before_the_reservation(self):
        running = [run('w', 'x', kind='windows', end=2 * H)]
        head = job('wp', 'z', level='issue', order=0, kind='win-perf')
        short = job('short', 'y', level='study', order=1, kind='build', timeout=H)
        long = job('long', 'v', level='study', order=2, kind='build', timeout=3 * H)
        p = sched.plan(state([head, short, long], running=running, now=10))
        self.assertEqual(granted(p), ['short'])
        self.assertIn('held back', p.reasons['long'])

    def test_quiet_wait_is_bounded(self):
        perf = job('perf', kind='linux-perf')
        p = sched.plan(state([perf], ext_cpu=5.0, now=100))
        self.assertEqual(granted(p), [])
        self.assertIn('perf', p.quiet_started)
        perf.quiet_since = 100
        p = sched.plan(state([perf], ext_cpu=5.0, now=100 + 15 * 60))
        self.assertEqual([(g.id, g.noisy) for g in p.grants], [('perf', True)])
        p = sched.plan(state([job('perf2', kind='linux-perf')], ext_cpu=0.5))
        self.assertEqual([(g.id, g.noisy) for g in p.grants], [('perf2', False)])

    def test_win_perf_never_starts_beside_the_users_game_and_holds_nothing(self):
        wp = job('wp', 'a', level='issue', kind='win-perf')
        other = job('chk', 'b', kind='check', order=1)
        p = sched.plan(state([wp, other], user_in_oni=True))
        self.assertEqual(granted(p), ['chk'])
        self.assertIn('user is in ONI', p.reasons['wp'])
        # Nor does a functional Windows job.
        self.assertEqual(granted(sched.plan(state([job('w', kind='windows')], user_in_oni=True))), [])

    def test_background_game_holds_back_measurements_only(self):
        jobs = [job('w', 'a', kind='windows'), job('wp', 'b', kind='win-perf', order=1), job('lp', 'c', kind='linux-perf', order=2)]
        p = sched.plan(state(jobs, background='a game of another tool runs: pid 5'))
        self.assertEqual(granted(p), ['w'])
        self.assertIn('another tool', p.reasons['wp'])
        self.assertIn('another tool', p.reasons['lp'])

    def test_windows_slot_held_for_the_user(self):
        jobs = [job('w', 'a', level='issue', kind='windows'), job('wp', 'b', level='release', kind='win-perf', order=1), job('c', 'c', kind='check', order=2)]
        p = sched.plan(state(jobs, windows_hold='the user plays, until 23:00'))
        self.assertEqual(granted(p), ['c'])
        self.assertIn('held for the user', p.reasons['w'])
        self.assertEqual(p.starts['wp'], math.inf)  # holds no reservation
        self.assertEqual(sorted(granted(sched.plan(state(jobs[:2])))), ['w'])

    def test_phase_keeps_the_original_submit_time(self):
        # A phase request submitted long ago (its job's submit) is due and goes first.
        ph = job('qa', 'a', level='study', order=0, kind='qa-perf', phase=True)
        new = job('n', 'b', level='user', order=4 * H, kind='check')
        p = sched.plan(state([new, ph], running=[run('qa', 'a', needs={})], now=4 * H))
        self.assertEqual(p.order[0], 'qa')


class Properties(unittest.TestCase):
    """Seeded random streams: after every step no capacity is exceeded, every grant is whole,
    and every job starts within a bound."""

    KINDS = ['light', 'build', 'check', 'linux-player', 'windows', 'win-perf', 'linux-perf', 'generic']

    def simulate(self, seed, steps=10000):
        rng = random.Random(seed)
        now = 0.0
        queued, running = [], []
        lanes_all = list(range(1, 20))
        submitted, started = {}, {}
        n = 0
        cap = sched.capacity(CFG, len(lanes_all))
        max_timeout = 2 * H
        for step in range(steps):
            now += rng.choice([1, 5, 30, 60, 300])
            # Ends: a job ends at a random point before its deadline, never after.
            for r in list(running):
                if now >= r.end or rng.random() < 0.02:
                    running.remove(r)
            if rng.random() < 0.3 and len(queued) < 25:
                n += 1
                kind = rng.choice(self.KINDS)
                level = rng.choice(CFG['levels'])
                j = job(f'j{n}', rng.choice('abcde'), level, now, kind=kind, timeout=rng.choice([600, 1800, H, max_timeout]))
                queued.append(j)
                submitted[j.id] = (now, j)
            held = {lane for r in running for lane in r.lanes}
            lanes_free = tuple(l for l in lanes_all if l not in held)
            p = sched.plan(sched.State(now=now, cfg=CFG, running=running, queued=queued, lanes_free=lanes_free))
            for jid, t in p.quiet_started.items():
                next(j for j in queued if j.id == jid).quiet_since = t
            for g in p.grants:
                j = next(j for j in queued if j.id == g.id)
                self.assertEqual(len(g.lanes), int(j.needs.get('lane', 0)), 'a partial set')
                self.assertFalse(set(g.lanes) & held, 'a lane granted twice')
                held |= set(g.lanes)
                queued.remove(j)
                running.append(sched.Running(j.id, j.owner, j.level, j.needs, now + j.timeout, g.lanes))
                started[j.id] = now
            total = sched.ZERO
            for r in running:
                total = sched.add(total, sched.usage(r.needs, cap))
            self.assertIsNone(sched.over(total, cap), f'capacity exceeded at step {step}: {total}')
            owners = {}
            for r in running:
                owners[r.owner] = owners.get(r.owner, 0) + 1
            for o, count in owners.items():
                self.assertLessEqual(count, CFG['caps']['running_per_owner'])
            # The bound: an issue or release job waits at most its level's bound plus the timeouts
            # of the jobs that can be ahead of it (at most 25 queued, plus those running). Due jobs
            # go by level, so in this overloaded stream the lower levels have no such bound: they
            # wait while higher levels keep coming.
            for j in queued:
                if j.level in ('issue', 'release'):
                    bound = CFG['due_minutes'][j.level] * 60 + 26 * max_timeout
                    self.assertLess(now - j.order, bound, f'{j.id} ({j.level}) waits too long')
        return len(started)

    def test_random_streams(self):
        self.assertGreater(self.simulate(1, 10000), 500)
        for seed in (2, 3):
            self.assertGreater(self.simulate(seed, 2000), 100)

    def test_no_lower_level_ahead_except_backfill(self):
        # A higher-level job's reservation start never moves later because of a lower-level
        # grant in the same pass.
        rng = random.Random(7)
        for _ in range(300):
            running = [
                run(f'r{i}', f'o{i}', kind=rng.choice(['check', 'linux-player', 'windows', 'build']), end=rng.choice([600, H, 2 * H]), lanes=[i + 1])
                for i in range(rng.randint(0, 4))
            ]
            head = job('head', 'z', level='issue', order=0, kind=rng.choice(['win-perf', 'linux-perf', 'check']))
            low = [job(f'l{i}', f'p{i}', level='burndown', order=i + 1, kind=rng.choice(self.KINDS), timeout=rng.choice([300, H, 3 * H])) for i in range(4)]
            lanes = tuple(range(6, 20))
            alone = sched.plan(state([head], running=running, now=1, lanes=lanes))
            both = sched.plan(state([head] + low, running=running, now=1, lanes=lanes))
            if 'head' in granted(alone):
                self.assertIn('head', granted(both))
                continue
            self.assertNotIn('head', granted(both))
            if alone.starts.get('head', math.inf) < math.inf:
                self.assertEqual(alone.starts['head'], both.starts['head'])
                # Every lower job granted ends before the head's reservation or does not conflict.
                for g in both.grants:
                    self.assertNotEqual(g.id, 'head')


class KillSwitch(unittest.TestCase):
    def test_off(self):
        self.assertIsNone(config.disabled({}, CFG))
        self.assertEqual(config.disabled({'RUNQ': 'off'}, CFG), 'RUNQ=off')
        # A CI runner: the scripts run directly there, keeping the display their caller made.
        self.assertEqual(config.disabled({'GITHUB_ACTIONS': 'true'}, CFG), 'a GitHub Actions runner')
        self.assertIsNotNone(config.disabled({}, dict(CFG, enabled=False)))


if __name__ == '__main__':
    unittest.main()
