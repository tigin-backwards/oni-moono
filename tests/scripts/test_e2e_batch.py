"""e2e.py for batch agents: machine-wide lanes and players' budget shared by runners in several
worktrees, the perf wait's visibility and limit, unique run ids, known findings, the committed
results record, and modtable.py reading it."""
import json, os, signal, subprocess, sys, tempfile, textwrap, threading, time, unittest

import helpers
import e2e

# A runner in another worktree: its own out dir and root, the test's lock directory. It
# allocates a lane (and, with a size, a player's memory), prints what it got, and holds both
# until killed.
CHILD = textwrap.dedent('''
    import json, os, sys, time
    sys.path.insert(0, {scripts!r})
    import e2e
    e2e.ROOT, e2e.OUT = sys.argv[1], os.path.join(sys.argv[1], 'out', 'e2e')
    reserved = e2e.reservations()
    lane = e2e.allocate_lane(1, busy=lambda p: False, display_busy=lambda d: False, last=4, wait=False,
                             reserved=reserved)
    token = None
    if len(sys.argv) > 2:
        token, why = e2e.Budget().try_acquire(float(sys.argv[2]), 'child ' + os.path.basename(sys.argv[1]))
    print(json.dumps({{'lane': lane and lane.number, 'display': lane and lane.display, 'token': token}}), flush=True)
    time.sleep(60)
''').format(scripts=helpers.SCRIPTS)


class SharedLocks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.locks = os.path.join(self.temp.name, 'locks')
        self.saved = {k: os.environ.get(k) for k in ('E2E_LOCK_DIR', 'E2E_RESERVED', 'E2E_MEMORY_GB', 'E2E_MAX_PLAYERS',
                                                       'E2E_QA_GB')}
        os.environ['E2E_LOCK_DIR'] = self.locks
        os.environ.pop('E2E_RESERVED', None)
        os.environ['E2E_QA_GB'] = '0'  # a qa.sh running on the machine must not change these budgets
        self.children = []

    def tearDown(self):
        for child in self.children:
            if child.poll() is None:
                child.kill()
            child.wait(10)
            child.stdout.close()
        for k, v in self.saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
        self.temp.cleanup()

    def worktree(self, name, gb=None):
        root = os.path.join(self.temp.name, name)
        os.makedirs(os.path.join(root, 'out', 'e2e'))
        child = subprocess.Popen([sys.executable, '-c', CHILD, root] + ([str(gb)] if gb else []),
                                 stdout=subprocess.PIPE, text=True, env=dict(os.environ))
        self.children.append(child)
        return child, json.loads(child.stdout.readline())

    def test_two_worktrees_get_different_lanes(self):
        a, got_a = self.worktree('wt-a')
        b, got_b = self.worktree('wt-b')
        self.assertEqual((got_a['lane'], got_b['lane']), (1, 2))
        self.assertEqual((got_a['display'], got_b['display']), (e2e.lane_display(1), e2e.lane_display(2)))
        held = {s['lane']: s for s in e2e.lane_status(self.locks)}
        self.assertEqual(sorted(held), [1, 2])
        self.assertEqual((held[1]['root'], held[1]['pid']), (os.path.join(self.temp.name, 'wt-a'), a.pid))
        self.assertEqual(held[2]['root'], os.path.join(self.temp.name, 'wt-b'))
        # This process, a third runner, gets the next lane; and lane 1 again once wt-a has gone
        # (its lock goes with its process, however it ended).
        c = e2e.allocate_lane(1, busy=lambda p: False, display_busy=lambda d: False, reserved=(set(), set()), last=4)
        self.assertEqual(c.number, 3)
        a.send_signal(signal.SIGKILL)
        a.wait(10)
        d = e2e.allocate_lane(1, busy=lambda p: False, display_busy=lambda d: False, reserved=(set(), set()), last=4)
        self.assertEqual(d.number, 1)
        for lane in (c, d):
            lane.release()

    def test_reserved_ports_and_displays_are_skipped(self):
        os.makedirs(self.locks)
        with open(os.path.join(self.locks, 'reserved'), 'w') as f:
            f.write('# other agents\nports 8788, 8810-8811\n')
        os.environ['E2E_RESERVED'] = f'displays {e2e.lane_display(2)}'
        self.assertEqual(e2e.reservations(), ({8788, 8810, 8811}, {e2e.DISPLAY_BASE + 2}))
        _, got = self.worktree('wt-a')
        self.assertEqual(got['lane'], 3)   # 1's ports and 2's display are reserved
        self.assertEqual(e2e.lane_problem(1, reserved=e2e.reservations()), 'port 8810 is reserved')
        with self.assertRaises(ValueError):
            e2e.reservations(text='sockets 3')

    def test_a_lane_whose_display_runs_is_skipped(self):
        lane = e2e.allocate_lane(1, self.locks, busy=lambda p: False, reserved=(set(), set()), last=4,
                                 display_busy=lambda d: d == str(e2e.DISPLAY_BASE + 1))
        self.assertEqual(lane.number, 2)
        lane.release()
        lane = e2e.allocate_lane(1, self.locks, busy=lambda p: False, reserved=(set(), set()), last=4, display=False,
                                 display_busy=lambda d: True)
        self.assertEqual((lane.number, lane.display), (1, None))   # --xvfb-run: no lane display
        lane.release()

    def test_the_players_budget_is_machine_wide(self):
        os.environ['E2E_MEMORY_GB'] = '20'
        a, got = self.worktree('wt-a', gb=11)
        self.assertTrue(got['token'])
        budget = e2e.Budget()
        self.assertEqual(budget.gb, 20)
        token, why = budget.try_acquire(15, 'mine')
        self.assertIsNone(token)
        self.assertIn('11 of 20 GB', why)
        self.assertIn('child wt-a (11 GB, pid', why)
        small, _ = budget.try_acquire(6, 'small')
        self.assertTrue(small)
        # wt-a's runner dies without releasing: its entry goes with it.
        a.send_signal(signal.SIGKILL)
        a.wait(10)
        token, _ = budget.try_acquire(11, 'mine')
        self.assertTrue(token)
        self.assertEqual(sorted(e['label'] for e in budget.entries()), ['mine', 'small'])
        budget.release(token)
        budget.release(small)
        self.assertEqual(budget.entries(), [])

    def test_player_count_and_a_player_bigger_than_the_budget(self):
        budget = e2e.Budget(self.locks, gb=10, players=2)
        big, _ = budget.try_acquire(15, 'U57')
        self.assertTrue(big)                                  # alone it may start
        self.assertIsNone(budget.try_acquire(1, 'small')[0])
        budget.release(big)
        one, two = budget.try_acquire(1, 'a')[0], budget.try_acquire(1, 'b')[0]
        self.assertTrue(one and two)
        token, why = budget.try_acquire(1, 'c')
        self.assertIsNone(token)
        self.assertIn('2 of 2 players', why)

    def test_hold_waits_and_gives_up(self):
        budget = e2e.Budget(self.locks, gb=10, players=1)
        first, _ = budget.try_acquire(6, 'first')
        messages = []
        sleep = e2e.time.sleep
        e2e.time.sleep = lambda s: sleep(0.01)
        try:
            with self.assertRaises(e2e.WaitTimeout) as caught:
                with budget.hold(6, 'second', messages.append, max_wait=0.05):
                    pass
        finally:
            e2e.time.sleep = sleep
        self.assertIn('waiting for memory for a player (6 GB)', caught.exception.args[0])
        self.assertTrue(messages[0].startswith('waiting for memory for a player (6 GB): 6 of 10 GB'))
        budget.release(first)
        with budget.hold(6, 'second', messages.append):
            self.assertEqual([e['label'] for e in budget.entries()], ['second'])

    def fast_sleep(self):
        sleep = e2e.time.sleep
        e2e.time.sleep = lambda s: sleep(0.01)
        self.addCleanup(setattr, e2e.time, 'sleep', sleep)

    def test_waiters_get_players_first_come_first_served(self):
        self.fast_sleep()
        budget = e2e.Budget(self.locks, gb=10, players=1)
        first, _ = budget.try_acquire(6, 'first')
        order, got = [], {}

        def wait(label):
            with budget.hold(6, label, lambda s: None):
                order.append(label)
                got[label].wait(5)
        threads = {}
        for label in ('a', 'b'):
            got[label] = threading.Event()
            threads[label] = threading.Thread(target=wait, args=(label,))
            threads[label].start()
            deadline = time.time() + 5
            while label not in [w['label'] for w in budget.waiters()] and time.time() < deadline:
                time.sleep(0.01)
        self.assertEqual([w['label'] for w in budget.waiters()], ['a', 'b'])
        # A runner that frees its player and asks again gets nothing while others wait.
        budget.release(first)
        token, why = budget.try_acquire(6, 'first again')
        self.assertIsNone(token)
        self.assertIn('waiter(s) ahead in the queue, first', why)
        deadline = time.time() + 5
        while order != ['a'] and time.time() < deadline:
            time.sleep(0.01)
        self.assertEqual(order, ['a'])
        self.assertEqual([w['label'] for w in budget.waiters()], ['b'])
        got['a'].set()
        threads['a'].join(5)
        got['b'].set()
        threads['b'].join(5)
        self.assertEqual(order, ['a', 'b'])
        self.assertEqual(budget.waiters(), [])
        self.assertEqual(budget.entries(), [])

    def test_a_dead_waiter_leaves_the_queue(self):
        child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdout=subprocess.PIPE)
        self.children.append(child)
        budget = e2e.Budget(self.locks, gb=10, players=2)
        os.makedirs(self.locks, exist_ok=True)
        with open(os.path.join(self.locks, 'waiters.json'), 'w') as f:
            json.dump([dict(pid=child.pid, start=e2e.process_start(child.pid), ticket='t', gb=6, label='dead')], f)
        self.assertEqual([w['label'] for w in budget.waiters()], ['dead'])
        self.assertIsNone(budget.try_acquire(6, 'mine')[0])
        child.send_signal(signal.SIGKILL)
        child.wait(10)
        self.assertEqual(budget.waiters(), [])
        token, _ = budget.try_acquire(6, 'mine')
        self.assertTrue(token)
        budget.release(token)

    def test_a_waiter_that_gives_up_leaves_the_queue(self):
        self.fast_sleep()
        budget = e2e.Budget(self.locks, gb=10, players=1)
        first, _ = budget.try_acquire(6, 'first')
        with self.assertRaises(e2e.WaitTimeout):
            with budget.hold(6, 'second', lambda s: None, max_wait=0.05):
                pass
        self.assertEqual(budget.waiters(), [])
        budget.release(first)

    def test_the_oldest_waiter_waits_for_qa_like_any_player(self):
        self.fast_sleep()
        processes = e2e.qa_processes
        running = [[(4242, 60, 'bash scripts/qa.sh')]]
        e2e.qa_processes = lambda: running[0]
        self.addCleanup(setattr, e2e, 'qa_processes', processes)
        os.environ['E2E_QA_GB'] = '15'
        budget = e2e.Budget(self.locks, gb=20, players=4)
        held, done = threading.Event(), threading.Event()

        def wait():
            with budget.hold(6, 'waiter', lambda s: None):
                held.set()
                done.wait(5)
        thread = threading.Thread(target=wait)
        thread.start()
        time.sleep(0.2)
        self.assertFalse(held.is_set())              # 15 + 6 > 20
        self.assertIsNone(budget.try_acquire(1, 'small')[0])   # it fits, but the waiter is first
        running[0] = []                              # qa.sh ends
        self.assertTrue(held.wait(5))
        self.assertEqual([e['label'] for e in budget.entries()], ['waiter'])
        done.set()
        thread.join(5)
        self.assertEqual(budget.entries(), [])

    def test_player_memory_by_fixture(self):
        with tempfile.NamedTemporaryFile() as small:
            self.assertEqual(e2e.player_gb({'from': 'early-base'}, small.name), e2e.SMALL_GB)
        self.assertEqual(e2e.player_gb({'from': 'U57'}), 15)
        self.assertEqual(e2e.player_gb({'from': '2minnow'}), 11)
        self.assertEqual(e2e.player_gb({'from': 'x', 'memory_gb': 9}), 9)


class PerfWaitVisible(unittest.TestCase):
    def test_waiter_says_what_it_waits_for_and_gives_up(self):
        now, messages = [1000.0], []
        waiter = e2e.Waiter('a perf job to finish', messages.append, max_wait=3600, clock=lambda: now[0])
        waiter.tick('pid 7, running 3 min: scripts/qa.sh', key=7)
        now[0] += 120
        waiter.tick('pid 7, running 5 min: scripts/qa.sh', key=7)      # the same job: quiet
        now[0] += 600
        waiter.tick('pid 7, running 15 min: scripts/qa.sh', key=7)     # ten minutes on: again
        waiter.tick('pid 9, running 1 min: perf record', key=9)        # another job: at once
        self.assertEqual(messages, ['waiting for a perf job to finish: pid 7, running 3 min: scripts/qa.sh',
                                    'waiting for a perf job to finish (12 min so far): pid 7, running 15 min: scripts/qa.sh',
                                    'waiting for a perf job to finish (12 min so far): pid 9, running 1 min: perf record'])
        now[0] += 3000
        with self.assertRaises(e2e.WaitTimeout) as caught:
            waiter.tick('pid 9, running 51 min: perf record', key=9)
        self.assertEqual(caught.exception.args[0], 'gave up after 62.0 min (--max-wait) waiting for a perf job to finish: '
                                                   'pid 9, running 51 min: perf record')

    def test_machine_lock_names_the_job_and_fails_after_max_wait(self):
        processes, sleep = e2e.perf_processes, e2e.time.sleep
        e2e.perf_processes = lambda: [(4242, 3900, 'bash scripts/qa.sh u57')]
        e2e.time.sleep = lambda s: sleep(0.01)
        messages = []
        try:
            with tempfile.TemporaryDirectory() as locks:
                with self.assertRaises(e2e.WaitTimeout) as caught:
                    with e2e.machine_lock(locks, log=messages.append, max_wait=0.05):
                        self.fail('ran beside a perf job')
        finally:
            e2e.perf_processes, e2e.time.sleep = processes, sleep
        self.assertEqual(messages[0], 'waiting for a perf job to finish: pid 4242, running 65 min: bash scripts/qa.sh u57')
        self.assertIn('--max-wait', caught.exception.args[0])

    def test_perf_processes_parse_ps(self):
        lines = ['  311   4000 bash out/local-tools/measure.sh', f'{os.getpid()} 5 bash out/local-tools/measure.sh',
                 '  12 9 vim notes', '  40 70 bash scripts/qa.sh']
        self.assertEqual(e2e.perf_processes(lines), [(311, 4000, 'bash out/local-tools/measure.sh')])
        self.assertEqual(e2e.qa_processes(lines), [(40, 70, 'bash scripts/qa.sh')])

    def test_perf_wanted_holds_specs_back(self):
        sleep = e2e.time.sleep
        e2e.time.sleep = lambda s: sleep(0.01)
        messages = []
        try:
            with tempfile.TemporaryDirectory() as locks:
                with open(os.path.join(locks, 'perf.wanted'), 'w') as f:
                    f.write(f'{os.getpid()}\n')
                with self.assertRaises(e2e.WaitTimeout):
                    with e2e.machine_lock(locks, log=messages.append, max_wait=0.05):
                        self.fail('started while a perf job waited')
                with open(os.path.join(locks, 'perf.wanted'), 'w') as f:
                    f.write('999999999\n')  # a pid that has gone
                with e2e.machine_lock(locks, log=messages.append, max_wait=1):
                    pass
        finally:
            e2e.time.sleep = sleep
        self.assertIn('waits to run a perf job', messages[0])

    def test_qa_counts_in_the_budget(self):
        processes = e2e.qa_processes
        e2e.qa_processes = lambda: [(4242, 60, 'bash scripts/qa.sh')]
        try:
            with tempfile.TemporaryDirectory() as d:
                budget = e2e.Budget(d, gb=40, players=6)
                tokens = [budget.try_acquire(6, f'p{i}')[0] for i in range(5)]
                self.assertEqual(sum(t is not None for t in tokens), 4)  # 15 + 4 x 6 = 39 of 40 GB
                self.assertIn('qa.sh (15 GB, pid 4242)', budget.try_acquire(6, 'p5')[1])
        finally:
            e2e.qa_processes = processes

    def test_max_wait_option(self):
        self.assertEqual(e2e.parse_args(['--max-wait', '90', 'x'])['max_wait'], 5400)
        self.assertIsNone(e2e.parse_args(['x'])['max_wait'])
        with self.assertRaises(e2e.SpecError):
            e2e.parse_args(['--max-wait', 'soon', 'x'])


class RunIds(unittest.TestCase):
    def test_ids_are_unique_and_folders_apart(self):
        ids = {e2e.new_run_id() for _ in range(200)}
        self.assertEqual(len(ids), 200)
        self.assertTrue(all(len(i.split('-')) == 3 for i in ids))
        spec = {'name': 'unit', 'steps': [], 'setup': [], 'mods': []}
        a = e2e.Run(spec, 'ours', 1, {}, e2e.Lane(7, None), lambda s: None, run_id='r1')
        b = e2e.Run(spec, 'ours', 1, {}, e2e.Lane(8, None), lambda s: None, run_id='r2')
        self.assertEqual(a.out, os.path.join(e2e.OUT, 'unit', 'r1', 'ours-1'))
        self.assertNotEqual(a.out, b.out)
        self.assertNotEqual(e2e.Run(spec, 'ours', 1, {}, e2e.Lane(7, None), lambda s: None).out, a.out)

    def test_latest_points_at_the_newest_run(self):
        with tempfile.TemporaryDirectory() as out:
            folder = os.path.join(out, 'spec')
            e2e.point_latest(folder, 'r1')
            e2e.point_latest(folder, 'r2')
            self.assertEqual(os.readlink(os.path.join(folder, 'latest')), 'r2')
            self.assertEqual(os.listdir(folder), ['latest'])


# ui-diff lines as the pilot's specs reported them.
SCALE = ('/GameScreenManager#0/ScreenSpaceOverlayCanvas#2/BottomPanelRight#11/ToolMenu#1/TypeSelect#0 [Canvas]\n'
         '  - renderMode=ScreenSpaceOverlay scaleFactor=0.86 sortingOrder=0 overrideSorting=False pixelPerfect=False\n'
         '  + renderMode=ScreenSpaceOverlay scaleFactor=1 sortingOrder=0 overrideSorting=False pixelPerfect=False')
SCALE_AND_SORT = SCALE.replace('+ renderMode=ScreenSpaceOverlay scaleFactor=1 sortingOrder=0',
                               '+ renderMode=ScreenSpaceOverlay scaleFactor=1 sortingOrder=3')
CARET = ('/A#0/TypeSelect#0/Border#0/TextFilter#2/Text Area#0/Caret#0 [rect]\n'
         '  - rect -50,-50,100,100 world 1099.4,343.81,1185.4,429.81 screen 1099.4,290.19,86,86\n'
         '  + rect -137,-9.52,274,19.04 world 1024.58,378.62,1260.22,395 screen 1024.58,325,235.64,16.37')
TEXT = ('/A#0/SearchBar#2/Input#0/Text Area#0/Text#1 [LocText]\n'
        '  - text="" fontSize=18 isTextOverflowing=False firstOverflowCharacterIndex=-1 textInfo=0,0,0,0,0,0,1,0\n'
        '  + text="​" fontSize=18 isTextOverflowing=True firstOverflowCharacterIndex=0 textInfo=1,0,1,0,1,1,1,0')


class KnownFindings(unittest.TestCase):
    def test_the_committed_list_is_valid(self):
        findings = e2e.load_findings()
        self.assertTrue(findings)
        self.assertEqual(e2e.check_findings(findings), [])

    def test_problems(self):
        problems = e2e.check_findings([{'id': 'Bad Id', 'description': 'x', 'link': 'y'},
                                       {'id': 'ok', 'description': 'x', 'link': 'y', 'component': 'C', 'fields': ['f'], 'colour': 1},
                                       {'id': 'ok', 'description': 'x', 'link': 'y', 'presence': True}])
        self.assertEqual(problems, ['finding 1 (Bad Id): `id` is lowercase words joined by dashes',
                                    'finding 1 (Bad Id): needs `component` and `fields` (or `presence: true` and `path`)',
                                    "finding 2 (ok): unknown key 'colour'",
                                    'finding 3 (ok): `id` is used twice',
                                    'finding 3 (ok): a presence finding needs `path`'])

    def test_pilot_differences(self):
        findings = e2e.load_findings()
        self.assertEqual(e2e.finding_for(SCALE, findings), 'nested-canvas-scale-factor')
        self.assertIsNone(e2e.finding_for(SCALE_AND_SORT, findings))     # sortingOrder is not explained
        self.assertEqual(e2e.finding_for(CARET, findings), 'unused-input-caret-layout')
        self.assertIsNone(e2e.finding_for(CARET.replace('Caret#0', 'Panel#0'), findings))   # path
        self.assertEqual(e2e.finding_for(TEXT, findings), 'empty-input-zero-width-space')
        self.assertIsNone(e2e.finding_for(TEXT.replace('​', 'Quick'), findings))      # ours: regex
        self.assertIsNone(e2e.finding_for('only in second: /A#0/B#1', findings))

    def test_specs_and_presence(self):
        findings = [{'id': 'extra-item', 'description': 'd', 'link': 'l', 'presence': 'only-ours', 'path': '**/List/Item'},
                    {'id': 'scale', 'description': 'd', 'link': 'l', 'component': 'Can*', 'fields': ['scaleFactor'],
                     'specs': ['sweep']}]
        self.assertEqual(e2e.finding_for('only in second: /Root#0/List#1/Item#7', findings), 'extra-item')
        self.assertIsNone(e2e.finding_for('only in first: /Root#0/List#1/Item#7', findings))
        self.assertEqual(e2e.finding_for(SCALE, findings, 'sweep'), 'scale')
        self.assertIsNone(e2e.finding_for(SCALE, findings, 'other'))

    def test_differential_warns_on_known_and_fails_on_the_rest(self):
        with tempfile.TemporaryDirectory() as temp:
            orig, ours = os.path.join(temp, 'orig'), os.path.join(temp, 'ours')
            for folder, scale, sort in ((orig, '0.86', 0), (ours, '1', 0)):
                os.makedirs(folder)
                with open(os.path.join(folder, 'types.ui.txt'), 'w') as f:
                    f.write(f'/Root#0/Types#0\trect 0,0,10,10 | Canvas renderMode=ScreenSpaceOverlay scaleFactor={scale} '
                            f'sortingOrder={sort}\n')
            result = {'signatures': set(), 'log': None, 'out': ours, 'values': [],
                      'evidence': {'ui_dump': [{'name': 'types'}], 'shot': [], 'world_shot': []}}
            reference = e2e.Reference(orig, set(), {}, {}, 'test')
            checks, reasons = e2e.differential({'name': 'sweep'}, result, reference, e2e.load_findings())
            self.assertEqual(reasons, [])
            self.assertEqual(checks['findings'], ['nested-canvas-scale-factor'])
            self.assertEqual(checks['ui_dump']['types']['known'], {'nested-canvas-scale-factor': 1})
            checks, reasons = e2e.differential({'name': 'sweep'}, result, reference, [])
            self.assertEqual((reasons, checks['findings']), (['ui_dump types: 1 difference(s)'], []))


def run_record(player, index, verdict, run='r1', reference_run=None, findings=()):
    record = {'spec': 'unit-mod', 'player': player, 'index': index, 'verdict': verdict, 'run': run,
              'started': '2026-10-22T12:00:00', 'lane': 7, 'reasons': [] if verdict in ('pass', 'warn') else ['step 3: no']}
    if player == 'ours':
        record.update(reference_run=reference_run or run, checks={
            'signatures': {'new': []}, 'findings': list(findings),
            'ui_dump': {'types': {'differences': 0, 'known': {f: 1 for f in findings}}},
            'shot': {'types': {'fraction': 0.001}}, 'world_shot': {}})
    else:
        record['signature_count'] = 4
    return record


class ResultsRecord(unittest.TestCase):
    spec = {'name': 'unit-mod', 'path': 'mods/123-unit-mod.yaml', 'mods': ['123']}

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = self.temp.name

    def tearDown(self):
        self.temp.cleanup()

    def update(self, records, note=None):
        return e2e.update_record(self.spec, records, note, self.folder, commit='abc1234', date='2026-10-22',
                                 hashes={'123': {'Unit.dll': '00'}})

    def test_original_then_ours_twice(self):
        record = self.update([run_record('original', 1, 'pass', 'r1')], note='the dig drops all its mass')
        self.assertEqual((record['verdict'], record['why']), ('pending', '0 run(s) of ours against original run r1; 2 needed'))
        record = self.update([run_record('ours', 1, 'pass', 'r2', 'r1'), run_record('ours', 2, 'pass', 'r2', 'r1')])
        self.assertEqual(record['verdict'], 'played')
        self.assertEqual(record['note'], 'the dig drops all its mass')     # kept
        self.assertEqual(record['spec'], 'mods/123-unit-mod.yaml')
        self.assertEqual(record['mods'], ['123'])
        self.assertEqual([r['index'] for r in record['runs']['original']], [1])
        self.assertEqual(record['runs']['ours'][0]['shots'], {'types': 0.001})
        with open(os.path.join(self.folder, 'unit-mod.json')) as f:
            self.assertEqual(json.load(f), record)

    def test_warning_cites_findings(self):
        record = self.update([run_record('original', 1, 'pass'), run_record('ours', 1, 'pass'),
                              run_record('ours', 2, 'warn', findings=['scroll-normalized-position'])])
        self.assertEqual((record['verdict'], record['findings']), ('warning', ['scroll-normalized-position']))
        self.assertEqual(record['runs']['ours'][1]['ui_dump'], {'types': {'differences': 0, 'known': {'scroll-normalized-position': 1}}})

    def test_failed_and_pending(self):
        record = self.update([run_record('original', 1, 'pass'), run_record('ours', 1, 'pass'), run_record('ours', 2, 'fail')])
        self.assertEqual(record['verdict'], 'failed')
        self.assertEqual(record['runs']['ours'][1]['reasons'], ['step 3: no'])
        # A new original run (re-blessed) makes the old runs of ours stale.
        record = self.update([run_record('original', 1, 'pass', 'r9')])
        self.assertEqual(record['verdict'], 'pending')
        record = self.update([run_record('original', 1, 'fail', 'r10')])
        self.assertEqual(record['why'], 'the spec fails in the original: a broken spec, or a mod broken upstream')
        # Skipped runs of ours (the original failed) leave the record's runs of ours alone.
        record = self.update([run_record('ours', 1, 'skipped')])
        self.assertEqual(len(record['runs']['ours']), 2)


MODTABLE = os.path.join(helpers.SCRIPTS, 'modtable.py')


class ModTable(unittest.TestCase):
    """modtable.py's Played column from tests/e2e/results records, beside its hand-written sets."""

    def table(self, records, load_sweep='', u60_records=None):
        with tempfile.TemporaryDirectory() as temp:
            load_sweeps = os.path.join(temp, 'load-sweeps')
            os.makedirs(load_sweeps)
            if load_sweep:
                with open(os.path.join(load_sweeps, 's.tsv'), 'w') as f:
                    f.write('# scripts/mod-sweep.py s\nid\trank\toriginal\tours\tnote\n' + load_sweep)
            catalog = os.path.join(temp, 'catalog.tsv')
            with open(catalog, 'w') as f:
                # 1717463209 is in modtable's hand-written `played`, 2363561445 in `broken`.
                f.write('1\t1717463209\t900\t\tBigger Camera Zoom Out\n2\t9990001\t800\t\tPlayed Mod\n'
                        '3\t9990002\t700\t\tWarned Mod\n4\t9990003\t600\t\tFailing Mod\n5\t9990004\t500\t\tPending Mod\n'
                        '6\t2363561445\t400\t\tUpstream Mod\n')
            results = os.path.join(temp, 'results')
            os.makedirs(results)
            for name, record in records.items():
                with open(os.path.join(results, name + '.json'), 'w') as f:
                    json.dump(record, f)
            results_u60 = os.path.join(temp, 'results-u60')
            os.makedirs(results_u60)
            for name, record in (u60_records or {}).items():
                with open(os.path.join(results_u60, name + '.json'), 'w') as f:
                    json.dump(record, f)
            sweeps = os.path.join(temp, 'sweeps')
            os.makedirs(sweeps)
            with open(os.path.join(sweeps, 'sweep-b0.txt'), 'w') as f:
                f.write('9990003\n9990004\n')
            out = subprocess.run([sys.executable, MODTABLE, catalog], capture_output=True, text=True, check=True,
                                 env=dict(os.environ, ONI_RESULTS_DIR=results, ONI_RESULTS_U60_DIR=results_u60, ONI_SWEEP_DIR=sweeps,
                                          ONI_SWEEP_RESULTS=load_sweeps)).stdout
        return {line.split(' | ')[1].split(']')[0].lstrip('['): line for line in out.splitlines()
                if line.startswith('| ') and '](https://steam' in line}

    def record(self, mid, verdict, note, findings=()):
        return {'spec': f'mods/{mid}-x.yaml', 'mods': [mid], 'verdict': verdict, 'why': 'a run of ours failed',
                'findings': list(findings), 'note': note, 'runs': {}}

    def test_u60_records_win_and_say_so(self):
        rows = self.table({'a': self.record('9990001', 'failed', 'the button does nothing')},
                          u60_records={'a': self.record('9990001', 'played', 'its tool marks only sandstone'),
                                       'c': self.record('9990003', 'failed', 'the button does nothing')})
        self.assertTrue(rows['Played Mod'].endswith('| ✅ | ✅ | ✅ | on U60: spec tests/e2e/mods/9990001-x.yaml: '
                                                    'its tool marks only sandstone |'))
        self.assertTrue(rows['Failing Mod'].endswith('| ✅ | ✅ | ⬜ | on U60: spec fails in ours: '
                                                     'tests/e2e/mods/9990003-x.yaml: the button does nothing '
                                                     '(a run of ours failed) |'))

    def test_records_decide_the_played_column(self):
        rows = self.table({'a': self.record('9990001', 'played', 'its tool marks only sandstone'),
                           'b': self.record('9990002', 'warning', 'its list looks the same',
                                            ['scroll-normalized-position']),
                           'c': self.record('9990003', 'failed', 'the button does nothing'),
                           'd': self.record('9990004', 'pending', 'not yet')})
        self.assertEqual(rows['Played Mod'], '| 2 | [Played Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=9990001)'
                                             ' | ✅ | ✅ | ✅ | spec tests/e2e/mods/9990001-x.yaml: its tool marks only sandstone |')
        self.assertTrue(rows['Warned Mod'].endswith('| ✅ | ✅ | ⚠️ | spec tests/e2e/mods/9990002-x.yaml: its list looks the '
                                                    'same; known findings: `scroll-normalized-position` |'))
        self.assertTrue(rows['Failing Mod'].endswith('| ✅ | ✅ | ⬜ | spec fails in ours: tests/e2e/mods/9990003-x.yaml: '
                                                     'the button does nothing (a run of ours failed) |'))
        self.assertTrue(rows['Pending Mod'].endswith('| ✅ | ✅ | ⬜ |  |'))      # as swept, no record yet
        # The hand-written entries still work where no record is.
        self.assertIn('| ✅ | ✅ | ✅ | spec tests/e2e/mods/1717463209-bigger-camera-zoom-out.yaml', rows['Bigger Camera Zoom Out'])
        self.assertIn('| ❌ | ❌ | ⬜ | broken upstream', rows['Upstream Mod'])

    def test_load_sweep_results_decide_loads_and_colony(self):
        rows = self.table({'a': self.record('9990001', 'played', 'its tool marks only sandstone')},
                          '9990001\t2\tok\tok\t\n9990002\t3\tmenu\t-\tthe original disables it\n'
                          '9990003\t4\tok\tcolony\tNullReferenceException @ Foo.Bar\n9990004\t5\tok\tok\t\n'
                          '2363561445\t6\tok\tok\t\n')
        self.assertIn('| ✅ | ✅ | ✅ | spec tests/e2e/mods/9990001-x.yaml', rows['Played Mod'])   # a record comes first
        self.assertTrue(rows['Warned Mod'].endswith('| ❌ | ❌ | ⬜ | fails to load in the original as well, at the main '
                                                    'menu, in its sweep batch: the original disables it |'))
        self.assertTrue(rows['Failing Mod'].endswith('| ✅ | ⬜ | ⬜ | its colony fails in ours, in its sweep batch: '
                                                     'NullReferenceException @ Foo.Bar |'))
        self.assertTrue(rows['Pending Mod'].endswith('| ✅ | ✅ | ⬜ |  |'))
        self.assertIn('| ❌ | ❌ | ⬜ | broken upstream', rows['Upstream Mod'])   # the hand-written set still decides

    def test_a_record_replaces_a_hand_written_entry(self):
        rows = self.table({'z': self.record('1717463209', 'warning', 'zooms out', ['nested-canvas-scale-factor'])})
        self.assertTrue(rows['Bigger Camera Zoom Out'].endswith(
            '| ⚠️ | spec tests/e2e/mods/1717463209-x.yaml: zooms out; known findings: `nested-canvas-scale-factor` |'))


if __name__ == '__main__':
    unittest.main()
