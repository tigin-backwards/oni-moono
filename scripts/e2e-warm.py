#!/usr/bin/env python3
"""A prototype of warm players: runs several specs in one player per kind (the original, then
ours), loading each spec's fixture through OniMcp's launch start (forceLoad, as the pause menu's
Load does) instead of relaunching, and times each spec against the launch it saved.

  scripts/e2e-warm.py [--player original|ours|both] [--first-lane L] [--last-lane L]
                      SPEC...

The specs must agree on what is fixed at launch: mods, options, translation, language, keyboard
and the Spaced Out setting. Each spec's run folder is out/e2e/SPEC/RUN_ID/PLAYER-1 as with
scripts/e2e.py, with the slice of the shared player log its steps wrote; ours is compared with
the original's run of the same invocation (scripts/e2e.py's differential). Lanes, the players'
budget, the perf lock and the settings (E2E_ORIGINAL_ENV among them) are scripts/e2e.py's.

Prints one line per spec run and writes out/e2e/runs/RUN_ID.json like scripts/e2e.py.
"""
import contextlib, importlib.util, json, os, shutil, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_spec = importlib.util.spec_from_file_location('e2e', os.path.join(ROOT, 'scripts', 'e2e.py'))
e2e = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(e2e)

# Players draw only on their lane's Xvfb: never on WSLg's Wayland compositor or the desktop's
# display, whatever this shell has (e2e.Player.start drops them too).
for _name in ('WAYLAND_DISPLAY', 'XDG_SESSION_TYPE', 'DISPLAY', 'XAUTHORITY'):
    os.environ.pop(_name, None)

# What a player fixes at launch: specs share a warm player only when these agree.
LAUNCH_KEYS = ('mods', 'options', 'translation', 'language', 'keyboard', 'display_dpi')


def launch_key(spec):
    return json.dumps([spec.get(k) for k in LAUNCH_KEYS] + [e2e.spaced_out(spec)], sort_keys=True, default=str)


def game_id(driver):
    """Game.Instance's instance id: a new one once a load has replaced the colony."""
    with contextlib.suppress(Exception):
        value = driver.state('Game.Instance')
        return value.get('id') if isinstance(value, dict) else None
    return None


def colony_up(mcp):
    with contextlib.suppress(Exception):
        status = mcp.read('oni://colony/status')
        return isinstance(status, dict) and status.get('duplicantCount', 0) > 0
    return False


def warm_load(player, mcp, fixture, timeout=900):
    """Loads FIXTURE over the colony PLAYER shows (OniMcp's launch start with forceLoad, as the
    pause menu's Load does) and pauses it; returns the seconds until the new colony was up: a
    new Game.Instance (E2eDriver survives the scene change) with Duplicants (OniMcp)."""
    target = os.path.join(player.save_dir, os.path.basename(fixture))
    if os.path.abspath(fixture) != os.path.abspath(target):
        shutil.copy(fixture, target)
    driver = e2e.Driver(player.lane.driver_port).connect(60, player.alive)
    try:
        before = game_id(driver)
        started = time.time()
        mcp.connect()
        mcp.call('game_control', domain='launch', action='start', path=target, confirm=True, forceLoad=True,
                 resume=False)
        deadline = started + timeout
        while True:
            time.sleep(0.5)
            if not player.alive():
                raise e2e.SpecFailure('the player exited while loading')
            if time.time() > deadline:
                raise e2e.SpecFailure(f'the fixture did not load within {timeout // 60} minutes')
            if driver.sock is None:
                with contextlib.suppress(e2e.DriverError):
                    driver.connect(5, player.alive)
            now = game_id(driver)
            if now is None:
                driver.close()
                continue
            if now != before and colony_up(mcp):
                break
        seconds = time.time() - started
    finally:
        driver.close()
    mcp.call('game_control', domain='speed', action='pause', confirm=True)
    return seconds


class WarmRun(e2e.Run):
    """A spec run in a player already up (PLAYER), which loads the spec's fixture over the
    colony it shows."""

    def __init__(self, spec, kind, index, env, lane, log, player, first, run_id, original_values=None):
        super().__init__(spec, kind, index, env, lane, log, original_values=original_values, run_id=run_id)
        self.player, self.first = player, first
        os.makedirs(self.out, exist_ok=True)

    def execute(self):
        spec = self.spec
        outcome, label, failed_kind = None, 'start', None
        log_start = os.path.getsize(self.player.log) if os.path.exists(self.player.log) else 0
        timings = {}
        records = open(os.path.join(self.out, 'results.jsonl'), 'w')
        try:
            started = time.time()
            if self.first:
                self.mcp.connect()  # the launch's own load used another session
            else:
                warm_load(self.player, self.mcp, e2e.fixture_of(spec))
            timings['load'] = round(time.time() - started, 1)
            self.log(f'  {"warm " if not self.first else ""}loaded {spec["from"]} in {timings["load"]} s')
            self.driver = e2e.Driver(self.lane.driver_port).connect(120, self.player.alive)
            self.driver.on_reply = self.player.watchdog.progress
            self.mcp.on_reply = self.player.watchdog.progress
            self.dismiss_load_dialogs()
            steps = [('setup', i, s) for i, s in enumerate(spec['setup'], 1)] + \
                    [('steps', i, s) for i, s in enumerate(spec['steps'], 1)]
            began_steps = time.time()
            for block, number, step in steps:
                (kind_of_step, value), = step.items()
                failed_kind = kind_of_step
                label = number if block == 'steps' else f'setup {number}'
                began = time.time()
                self.step(kind_of_step, value, label)
                records.write(json.dumps({'step': label, kind_of_step: value, 'wall': round(time.time() - began, 1)}) + '\n')
                records.flush()
            timings['steps'] = round(time.time() - began_steps, 1)
            failed_kind, label = None, 'end'
            with contextlib.suppress(e2e.SpecFailure):
                e2e.screenshot(self.mcp, self.out, 'end')
            if not self.player.alive():
                raise e2e.SpecFailure('the player is not alive at the end')
        except (e2e.SpecFailure, ConnectionError, TimeoutError, OSError) as e:
            outcome = f'step {label}: {e}'
            with contextlib.suppress(Exception):
                e2e.screenshot(self.mcp, self.out, 'failure')
        finally:
            if self.input:
                with contextlib.suppress(Exception):
                    self.input.close()  # releases held keys
                self.input = None
            if self.driver:
                self.driver.close()
            records.close()
        # The spec's slice of the shared log, for its signatures and require_log.
        log = os.path.join(self.out, os.path.basename(self.player.log))
        with open(self.player.log, 'rb') as src, open(log, 'wb') as dst:
            src.seek(log_start)
            dst.write(src.read())
        if not outcome:
            errors = e2e.driver_errors(log)
            if errors:
                outcome = f'step end: E2eDriver logged {len(errors)} error(s): {errors[0][:300]}'
        with open(os.path.join(self.out, 'values.json'), 'w') as f:
            json.dump(self.values, f, indent=1, default=str)
        signatures = e2e.Player.signatures(type('P', (), {'log': log})())
        return {'outcome': outcome, 'failed_step_kind': failed_kind, 'run': self.run_id, 'signatures': signatures,
                'saves': self.saves, 'out': self.out, 'values': self.values, 'evidence': self.evidence,
                'lane': self.player.lane_record(), 'options_written': self.player.option_files, 'log': log,
                'timings': timings}


def run_kind(kind, specs, env, opts, run_id, originals, findings, log):
    spec0 = specs[0]
    fixture = e2e.fixture_of(spec0)
    gb = max(e2e.player_gb(s, e2e.fixture_of(s)) for s in specs)
    records = []
    budget = e2e.Budget()
    with e2e.machine_lock(log=log), budget.hold(gb, f'e2e-warm {kind}', log):
        lane = e2e.allocate_lane(opts['first_lane'], last=opts['last_lane'], log=log)
        player = None
        try:
            folder = os.path.join(e2e.OUT, 'warm', run_id, kind)
            player = e2e.Player(kind, folder, env, fixture, lane, spec0['mods'], spec0.get('options'), driver=True,
                                expansion1=e2e.spaced_out(spec0), translation=spec0.get('translation'),
                                language=spec0.get('language'), keyboard=spec0.get('keyboard'),
                                display_dpi=spec0.get('display_dpi'))
            t0 = time.time()
            player.start()
            started = round(time.time() - t0, 1)
            mcp = e2e.Mcp(lane.mcp_port)
            mcp.on_reply = player.watchdog.progress
            player.load(mcp)
            first_load = round(time.time() - t0 - started, 1)
            log(f'== {kind}: lane {lane.number}, OniMcp up in {started} s, first load {first_load} s')
            for i, spec in enumerate(specs):
                index = sum(1 for s in specs[:i] if s['name'] == spec['name']) + 1
                reference = e2e.Reference.from_runs(spec, [originals[spec['name']]]) \
                    if kind == 'ours' and spec['name'] in originals else None
                t = time.time()
                run = WarmRun(spec, kind, index, env, lane, log, player, i == 0, run_id,
                              reference.values if reference else None)
                result = run.execute()
                wall = round(time.time() - t, 1)
                if i == 0:
                    result['timings']['start'], result['timings']['load'] = started, first_load
                record = {'spec': spec['name'], 'player': kind, 'index': index, 'run': run_id, 'warm': i > 0,
                          'wall': wall, 'timings': result['timings'], 'outcome': result['outcome'],
                          'out': result['out'], 'rss_gb': rss_gb(player.game_pid())}
                reasons = [result['outcome']] if result['outcome'] else []
                if kind == 'original' and not reasons:
                    originals.setdefault(spec['name'], result)
                if reference:
                    checks, more = e2e.differential(spec, result, reference, findings)
                    record['checks'] = checks
                    reasons += more
                record.update(verdict='fail' if reasons else 'pass', reasons=reasons)
                with open(os.path.join(result['out'], 'result.json'), 'w') as f:
                    json.dump(record, f, indent=1, default=sorted)
                log(f'{record["verdict"].upper():4} {spec["name"]} in {kind}: {wall} s {result["timings"]}'
                    + (f' ({"; ".join(reasons)[:300]})' if reasons else ''))
                records.append(record)
                if not player.alive():
                    break
        finally:
            if player:
                player.stop()
            lane.release()
    return records


def rss_gb(pid):
    with contextlib.suppress(Exception):
        for line in open(f'/proc/{pid}/status'):
            if line.startswith('VmRSS'):
                return round(int(line.split()[1]) / 1048576, 2)
    return None


def main(argv):
    opts = {'players': 'both', 'first_lane': 12, 'last_lane': 19}
    names = []
    args = list(argv)
    while args:
        a = args.pop(0)
        if a == '--player':
            opts['players'] = args.pop(0)
        elif a == '--first-lane':
            opts['first_lane'] = int(args.pop(0))
        elif a == '--last-lane':
            opts['last_lane'] = int(args.pop(0))
        elif a.startswith('-'):
            raise SystemExit(__doc__)
        else:
            names.append(a)
    specs = [e2e.load_spec(n) for n in names]
    keys = {launch_key(s) for s in specs}
    if len(keys) > 1:
        raise SystemExit('e2e-warm: the specs differ in what is fixed at launch (mods, options, language, ...)')
    if any(e2e.fixture_of(s) is None for s in specs):
        raise SystemExit('e2e-warm: a spec from the main menu needs a fresh player')
    env = e2e.environment()
    findings = e2e.load_findings()
    run_id = e2e.new_run_id()
    kinds = ['original', 'ours'] if opts['players'] == 'both' else [opts['players']]
    originals, records = {}, []
    for kind in kinds:
        records += run_kind(kind, specs, env, opts, run_id, originals, findings, e2e.say)
    path = os.path.join(e2e.OUT, 'runs', run_id + '.json')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w') as f:
        json.dump(records, f, indent=1, default=sorted)
    e2e.say(f'== {sum(r["verdict"] == "fail" for r in records)} failure(s); results in {path}')


if __name__ == '__main__':
    main(sys.argv[1:])
