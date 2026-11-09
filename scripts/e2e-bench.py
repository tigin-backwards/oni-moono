#!/usr/bin/env python3
"""Measures what an e2e run of a player costs, for variants of its launch (Mesa driver, Unity
arguments, boot.config, Xvfb depth and size): start-up to OniMcp, the fixture's load, frame rate
and CPU per thread while paused, a spec's `run: {seconds: 60}` (wall time, frame rate, CPU),
and a screenshot to compare pictures between variants. Lanes, the players' budget and the perf
lock are scripts/e2e.py's.

  scripts/e2e-bench.py [--player original|ours] [--fixture NAME|PATH] [--mods ID,...]
                       [--first-lane L] [--last-lane L] [--seconds S] [--out DIR]
                       [--no-run] [--perf] VARIANT...
  scripts/e2e-bench.py --warm [--saves NAME,...|--loads N] [--player ...] [--fixture ...] VARIANT...

A VARIANT is a name, then `+`-separated settings: `base`, `llvm+gallium=llvmpipe`,
`vk+arg=-force-vulkan`, `st+boot=gfx-threading-mode=0`, `d16+depth=16`, `small+size=1024x576`,
`env=NAME=VALUE`. `gallium=none` drops GALLIUM_DRIVER. Arguments and boot.config changes apply
to the original only, in a copy of its folder under OUT (never the installed game).

--no-run leaves out the 60 game seconds; --perf records 10 s of `perf` (OUT/VARIANT/perf.txt).
OUT/VARIANT/log-times.txt stamps each line of the player's log with the seconds since launch.
--warm loads each of --saves (else the fixture LOADS times) in the running player, as
scripts/e2e-warm.py does between specs, timing each load.

Results go to OUT/results.jsonl (default out/e2e-bench), one line per variant.
"""

import contextlib, importlib.util, json, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_spec = importlib.util.spec_from_file_location('e2e', os.path.join(ROOT, 'scripts', 'e2e.py'))
e2e = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(e2e)

# Players draw only on their lane's Xvfb: never on WSLg's Wayland compositor or the desktop's
# display, whatever this shell has (e2e.Player.start drops them too).
for _name in ('WAYLAND_DISPLAY', 'XDG_SESSION_TYPE', 'DISPLAY', 'XAUTHORITY'):
    os.environ.pop(_name, None)

CLK = os.sysconf('SC_CLK_TCK')


def parse_variant(text):
    name, *parts = text.split('+')
    v = {'name': name, 'gallium': None, 'args': [], 'boot': {}, 'env': {}, 'depth': 24, 'size': None}
    for part in parts:
        key, _, value = part.partition('=')
        if key == 'gallium':
            v['gallium'] = value
        elif key == 'arg':
            v['args'].append(value)
        elif key == 'boot':
            k, _, val = value.partition('=')
            v['boot'][k] = val
        elif key == 'env':
            k, _, val = value.partition('=')
            v['env'][k] = val
        elif key == 'depth':
            v['depth'] = int(value)
        elif key == 'size':
            v['size'] = tuple(int(x) for x in value.split('x'))
        else:
            raise SystemExit(f'unknown setting {part!r} in {text!r}')
    return v


def original_folder(executable, lane, variant, out):
    """A copy of the original's folder for this lane and variant: links to everything but the
    executable (copied, Unity's single-instance lock sits beside it) and, when the variant
    changes boot.config, a data folder of links with its own boot.config. Returns a wrapper
    that runs it with the variant's arguments and environment."""
    source = os.path.dirname(os.path.abspath(executable))
    exe = os.path.basename(executable)
    folder = os.path.join(out, 'players', f'{variant["name"]}-{lane}')
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    data = exe + '_Data'
    for name in os.listdir(source):
        target = os.path.join(folder, name)
        if name == 'unity.lock':
            continue
        if name == exe:
            shutil.copy2(executable, target)
        elif name == data and variant['boot']:
            os.makedirs(target)
            src_data = os.path.join(source, data)
            for inner in os.listdir(src_data):
                if inner != 'boot.config':
                    os.symlink(os.path.realpath(os.path.join(src_data, inner)), os.path.join(target, inner))
            lines = open(os.path.join(src_data, 'boot.config')).read().splitlines()
            keys = dict(line.partition('=')[::2] for line in lines if line)
            keys.update(variant['boot'])
            with open(os.path.join(target, 'boot.config'), 'w') as f:
                f.write(''.join(f'{k}={v}\n' for k, v in keys.items() if v != '-'))
        else:
            os.symlink(os.path.realpath(os.path.join(source, name)), target)
    wrapper = os.path.join(folder, 'bench-run.sh')
    with open(wrapper, 'w') as f:
        f.write(
            '#!/bin/sh\nunset WAYLAND_DISPLAY XDG_SESSION_TYPE\n'
            + ''.join(f'export {k}={v!r}\n' for k, v in variant['env'].items())
            + f'exec "$(dirname "$0")/{exe}" "$@" '
            + ' '.join(variant['args'])
            + '\n'
        )
    os.chmod(wrapper, 0o755)
    return wrapper


class BenchPlayer(e2e.Player):
    depth = 24

    def start_xvfb(self, display):
        number = display.lstrip(':')
        if e2e.display_in_use(number):
            raise e2e.SpecFailure(f'display {display} is in use')
        self.xvfb = subprocess.Popen(
            ['Xvfb', display, '-screen', '0', f'{e2e.WIDTH}x{e2e.HEIGHT}x{self.depth}', '-nolisten', 'tcp'],
            stdout=subprocess.DEVNULL,
            stderr=open(os.path.join(self.out, 'xvfb.txt'), 'w'),
            start_new_session=True,
        )
        deadline = time.time() + 20
        while not e2e.display_ready(number):
            if self.xvfb.poll() is not None or time.time() > deadline:
                raise e2e.SpecFailure(f'Xvfb {display} did not start')
            time.sleep(0.2)


class LogStamps:
    """Timestamps the player's log as it grows (Unity's log has none): OUT/log-times.txt holds
    each new line with the seconds since the launch."""

    def __init__(self, log, out):
        import threading

        self.log, self.path, self.t0 = log, os.path.join(out, 'log-times.txt'), time.time()
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        pos = 0
        with open(self.path, 'w') as out:
            while not self.stop_event.wait(0.5):
                with contextlib.suppress(OSError):
                    with open(self.log, errors='replace') as f:
                        f.seek(pos)
                        chunk = f.read()
                        pos = f.tell()
                    stamp = time.time() - self.t0
                    for line in chunk.splitlines():
                        if line.strip():
                            out.write(f'{stamp:7.1f} {line[:240]}\n')
                    out.flush()

    def stop(self):
        self.stop_event.set()


def perf_record(pid, seconds, out):
    """perf's view of the player's threads for SECONDS (perf_event_paranoid 2 allows a user's own
    processes): OUT/perf.txt, by thread and symbol."""
    data = os.path.join(out, 'perf.data')
    subprocess.run(['perf', 'record', '-q', '-g', '-o', data, '-p', str(pid), '--', 'sleep', str(seconds)], capture_output=True)
    with open(os.path.join(out, 'perf.txt'), 'w') as f:
        subprocess.run(
            ['perf', 'report', '-i', data, '--no-children', '--sort', 'comm,dso,sym', '--stdio', '-g', 'none', '--percent-limit', '0.5'],
            stdout=f,
            stderr=subprocess.DEVNULL,
        )


def cpu_ticks(pid):
    """{thread name: ticks} for each thread of PID, and the process total."""
    threads = {}
    with contextlib.suppress(OSError):
        for tid in os.listdir(f'/proc/{pid}/task'):
            with contextlib.suppress(OSError):
                stat = open(f'/proc/{pid}/task/{tid}/stat').read()
                name = stat[stat.index('(') + 1 : stat.rindex(')')]
                fields = stat[stat.rindex(')') + 2 :].split()
                threads[f'{name}:{tid}'] = int(fields[11]) + int(fields[12])
    return threads


def cpu_window(pid, seconds, during=None):
    """Runs DURING (or sleeps) and returns (cores used, the busiest threads' cores)."""
    before, t0 = cpu_ticks(pid), time.time()
    if during:
        during()
    else:
        time.sleep(seconds)
    after, dt = cpu_ticks(pid), time.time() - t0
    use = {k: (after[k] - before.get(k, 0)) / CLK / dt for k in after}
    top = sorted(use.items(), key=lambda kv: -kv[1])[:6]
    return round(sum(use.values()), 2), [(k, round(v, 2)) for k, v in top]


def frame_rate(driver, seconds):
    f0, t0 = driver.ping() and driver.frame, time.time()
    time.sleep(seconds)
    driver.ping()
    return (driver.frame - f0) / (time.time() - t0)


STATE = [
    'UnityEngine.Application.runInBackground',
    'UnityEngine.Application.isFocused',
    'UnityEngine.Application.targetFrameRate',
    'UnityEngine.QualitySettings.vSyncCount',
    'UnityEngine.Time.maximumDeltaTime',
    'UnityEngine.Screen.width',
    'UnityEngine.Screen.height',
    'UnityEngine.SystemInfo.graphicsDeviceName',
    'UnityEngine.SystemInfo.graphicsDeviceType',
    'UnityEngine.SystemInfo.renderingThreadingMode',
    'UnityEngine.QualitySettings.names',
    'UnityEngine.QualitySettings.GetQualityLevel()',
]


def measure(player, mcp, driver, seconds, out, name, log, no_run=False):
    record = {}
    pid = player.game_pid()
    for expr in STATE:
        with contextlib.suppress(Exception):
            record[expr.split('.', 1)[1]] = driver.state(expr)
    time.sleep(3)
    cores, top = cpu_window(pid, seconds, lambda: record.__setitem__('fps_paused', round(frame_rate(driver, seconds), 2)))
    record['cpu_paused'], record['threads_paused'] = cores, top
    log(f'  paused: {record["fps_paused"]} fps, {cores} cores {top[:3]}')
    if not no_run:
        # A spec's `run: {seconds: 60}`, timed as e2e.py runs it (speed 3), with the frame rate
        # and CPU meanwhile.
        f0, t0 = driver.ping() and driver.frame, time.time()
        cores, top = cpu_window(pid, 0, lambda: e2e.run(mcp, 60))
        wall = time.time() - t0
        driver.ping()
        record['run60_wall'] = round(wall, 1)
        record['fps_speed3'] = round((driver.frame - f0) / wall, 2)
        record['cpu_speed3'], record['threads_speed3'] = cores, top
        log(f'  run 60 game s: {record["run60_wall"]} s, {record["fps_speed3"]} fps, {cores} cores {top[:3]}')
    driver.camera_set(pos=[128.5, 210.5], ortho=20)
    driver.frames(5)
    shot = e2e.screenshot(mcp, out, name)
    record['shot'] = shot
    return record


def bench(variant, opts, env, log):
    player_kind = opts['player']
    out = os.path.join(opts['out'], variant['name'] + ('-ours' if player_kind == 'ours' else ''))
    fixture = opts['fixture']
    record = {'variant': variant['name'], 'player': player_kind, 'fixture': os.path.basename(fixture), 'settings': variant}
    env = dict(env)
    if variant['gallium'] == 'none':
        env.pop('GALLIUM_DRIVER', None)
    elif variant['gallium']:
        env['GALLIUM_DRIVER'] = variant['gallium']
    if variant['size']:
        e2e.WIDTH, e2e.HEIGHT = variant['size']
    else:
        e2e.WIDTH, e2e.HEIGHT = 1280, 720
    budget = e2e.Budget()
    with e2e.machine_lock(log=log), budget.hold(e2e.player_gb({'from': 'early-base', 'mods': []}, fixture), f'e2e-bench {variant["name"]}', log=log):
        lane = e2e.allocate_lane(opts['first_lane'], last=opts['last_lane'], log=log)
        saved = e2e.original_player
        try:
            if player_kind == 'original':
                e2e.original_player = lambda exe, number, root=None: original_folder(exe, number, variant, opts['out'])
            else:
                for k, v in variant['env'].items():
                    os.environ[k] = v
            BenchPlayer.depth = variant['depth']
            player = BenchPlayer(player_kind, out, env, fixture, lane, mods=opts['mods'], driver=True)
            mcp = e2e.Mcp(lane.mcp_port)
            mcp.on_reply = player.watchdog.progress
            driver = None
            try:
                t0 = time.time()
                stamps = LogStamps(player.log, out)
                player.start()
                record['start_s'] = round(time.time() - t0, 1)
                t1 = time.time()
                player.load(mcp)
                record['load_s'] = round(time.time() - t1, 1)
                log(f'  {variant["name"]}: OniMcp up in {record["start_s"]} s, {record["fixture"]} loaded in {record["load_s"]} s')
                driver = e2e.Driver(lane.driver_port).connect(120, player.alive)
                driver.on_reply = player.watchdog.progress
                if opts['warm']:
                    record.update(warm_loads(player, mcp, driver, opts, log))
                else:
                    if opts['perf']:
                        perf_record(player.game_pid(), 10, out)
                    record.update(measure(player, mcp, driver, opts['seconds'], out, 'bench', log, opts['no_run']))
                record['rss_gb'] = rss_gb(player.game_pid())
            except Exception as e:  # a variant that fails is a result too
                record['error'] = f'{type(e).__name__}: {e}'
                log(f'  {variant["name"]}: {record["error"]}')
            finally:
                if driver:
                    driver.close()
                player.stop()
                with contextlib.suppress(NameError):
                    stamps.stop()
        finally:
            e2e.original_player = saved
            for k in variant['env']:
                os.environ.pop(k, None)
            lane.release()
    return record


def rss_gb(pid):
    with contextlib.suppress(Exception):
        for line in open(f'/proc/{pid}/status'):
            if line.startswith('VmRSS'):
                return round(int(line.split()[1]) / 1048576, 2)
    return None


def warm():
    """scripts/e2e-warm.py, whose warm_load this shares."""
    spec = importlib.util.spec_from_file_location('e2e_warm', os.path.join(ROOT, 'scripts', 'e2e-warm.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def warm_loads(player, mcp, driver, opts, log):
    """Loads (from the colony, as the pause menu's Load does) each of --saves, else the fixture
    --loads times, in the running player; each load's seconds."""
    loads = []
    saves = opts['saves'] or [opts['fixture']] * opts['loads']
    for i, save in enumerate(saves):
        seconds = warm().warm_load(player, mcp, save)
        driver.close()
        driver.connect(120, player.alive)
        driver.ping()
        loads.append([os.path.basename(save), round(seconds, 1)])
        log(f'  warm load {i + 1} ({os.path.basename(save)}): {seconds:.1f} s, frame {driver.frame}, rss {rss_gb(player.game_pid())} GB')
    return {'warm_loads': loads}


def save_path(name):
    """A fixture's name (tests/e2e/fixtures/NAME.sav), or a path."""
    return os.path.abspath(name if os.path.exists(name) else os.path.join(e2e.FIXTURES, name + '.sav'))


def main(argv):
    opts = {
        'player': 'original',
        'fixture': 'early-base',
        'mods': [],
        'first_lane': 12,
        'last_lane': 19,
        'seconds': 15,
        'out': os.path.join(ROOT, 'out', 'e2e-bench'),
        'warm': False,
        'loads': 3,
        'perf': False,
        'no_run': False,
        'saves': [],
    }
    variants = []
    args = list(argv)
    while args:
        a = args.pop(0)
        if a == '--player':
            opts['player'] = args.pop(0)
        elif a == '--fixture':
            opts['fixture'] = args.pop(0)
        elif a == '--mods':
            opts['mods'] = [m for m in args.pop(0).split(',') if m]
        elif a == '--first-lane':
            opts['first_lane'] = int(args.pop(0))
        elif a == '--last-lane':
            opts['last_lane'] = int(args.pop(0))
        elif a == '--seconds':
            opts['seconds'] = float(args.pop(0))
        elif a == '--out':
            opts['out'] = os.path.abspath(args.pop(0))
        elif a == '--warm':
            opts['warm'] = True
        elif a == '--saves':
            opts['saves'] = [save_path(x) for x in args.pop(0).split(',') if x]
        elif a == '--perf':
            opts['perf'] = True
        elif a == '--no-run':
            opts['no_run'] = True
        elif a == '--loads':
            opts['loads'] = int(args.pop(0))
        elif a.startswith('-'):
            raise SystemExit(__doc__)
        else:
            variants.append(parse_variant(a))
    if not variants:
        variants = [parse_variant('base')]
    opts['fixture'] = save_path(opts['fixture'])
    env = e2e.environment()
    os.makedirs(opts['out'], exist_ok=True)
    for variant in variants:
        e2e.say(f'{variant["name"]} ({opts["player"]}, {os.path.basename(opts["fixture"])})')
        record = bench(variant, opts, env, e2e.say)
        with open(os.path.join(opts['out'], 'results.jsonl'), 'a') as f:
            f.write(json.dumps(record, default=str) + '\n')
        e2e.say(json.dumps({k: v for k, v in record.items() if k not in ('settings',)}, default=str))


if __name__ == '__main__':
    main(sys.argv[1:])
