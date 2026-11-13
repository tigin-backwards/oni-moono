#!/usr/bin/env python3
"""How steady a machine's perf numbers are: two Moono builds, A and B, launched in turn on the same
save, each launch measuring a still colony and then a running one with PerfProbe and
`--frame-stats` (.github/workflows/perf-spread.yml, docs/PERFORMANCE.md "Perf spread on CI's
runners"). Linux, under the X display in DISPLAY.

    scripts/perf-spread.py run WORKTREE OUT [--label A] [--save SAVE] [--window 600] [--size 1280x720]
                               [--pause-windows 3] [--run-windows 4] [--timeout 2400]
        One launch of WORKTREE's release build: SAVE loaded with PerfProbe (alloc.txt, sites.txt,
        no autosaves), paused for --pause-windows windows of --window frames, then at speed 3
        for --run-windows, in a --size window; stopped after --timeout seconds whatever it has.
        Writes OUT/result.json: each window's counts and times, no log text.
    scripts/perf-spread.py summary [--a NAME] [--b NAME] [--save NAME] OUT RUN...
        The runs' report (OUT/summary.md, the job summary as is, and OUT/summary.json): a header
        line (A and B by name, the save, the CPU model, the pairs), each gate's verdict, then
        tables of the counts and sections paused and running and of the times' A/B ratios, and
        each launch's numbers and every section folded away last.
    scripts/perf-spread.py jobs SUMMARY.json...
        Several jobs' summaries side by side: the between-job spread of the absolute times.
    scripts/perf-spread.py gate SUMMARY.json
        The verdict line of the nightly's gates (the report shows each); exit 1 when one fails: the
        paused draws and every collection count must not rise, paused allocations (also by
        section) within 5%, running draws within 3%. The interleaved main-thread ratio only warns.

Settings (environment): SPREAD_GAME (the game folder), SPREAD_STEAM (the Steam double),
SPREAD_PROBE (PerfProbe's release build), SPREAD_SAVE (the save, else --save).

The first window of each phase is left out (the load settling, the switch to running). Counts
are per frame (paused) or per game second (running); times are the medians of a launch's windows.
"""

import json, os, re, shutil, signal, statistics, subprocess, sys, time

STEAM_ID = '76561197960265729'
MODS_JSON = (
    '{"version":1,"mods":[{"label":{"distribution_platform":0,"id":"PerfProbe","title":"PerfProbe","version":-2037174645},'
    '"status":1,"enabled":true,"enabledForDlc":["","EXPANSION1_ID"],"crash_count":0,"reinstall_path":null,"staticID":"PerfProbe"}],'
    '"mod_load_in_progress":false}'
)
PREFS = 'strings: {{}}\nints:\n  ResolutionWidth: {}\n  ResolutionHeight: {}\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 0\nfloats: {{}}\n'
GAME_DIR = os.path.join('config', 'unity3d', 'Klei', 'Oxygen Not Included')

# Frame stats phases that are the main thread's own work; `present` waits for the device and
# `gpu` is the device's.
MAIN_PHASES = ('early+fixed', 'update', 'late', 'post-late', 'particles', 'render', 'gui', 'end of frame')


# ---- one launch ----


def prepare_home(out, save, probe, pause_windows, window, size):
    home = os.path.join(out, 'home')
    d = os.path.join(home, GAME_DIR)
    saves = os.path.join(d, 'cloud_save_files', STEAM_ID, 'SPREAD')
    os.makedirs(saves)
    os.makedirs(os.path.join(d, 'mods', 'Local'))
    os.makedirs(os.path.join(d, 'Agreements', 'OxygenNotIncluded'))
    placed = os.path.join(saves, os.path.basename(save))
    shutil.copyfile(save, placed)
    with open(os.path.join(d, 'kplayerprefs.yaml'), 'w') as f:
        f.write(PREFS.format(*size))
    with open(os.path.join(d, 'Agreements', 'OxygenNotIncluded', 'kprivacyprefs.yaml'), 'w') as f:
        f.write('disableDataCollection: true\n')
    with open(os.path.join(d, 'mods', 'mods.json'), 'w') as f:
        f.write(MODS_JSON)
    mod = os.path.join(d, 'mods', 'Local', 'PerfProbe')
    shutil.copytree(probe, mod)
    files = {
        'window.txt': f'{window}\n',
        'load.txt': placed + '\n',
        'noautosave.txt': '',
        'alloc.txt': '',
        'sites.txt': '',
        'novictory.txt': '',
        'speed.txt': '2\n',
    }
    if pause_windows:
        files['pausefirst.txt'] = f'{pause_windows * window}\n'
    for name, text in files.items():
        with open(os.path.join(mod, name), 'w') as f:
            f.write(text)
    return home


def find_pid(comm, home):
    for p in os.listdir('/proc'):
        if not p.isdigit():
            continue
        try:
            with open(f'/proc/{p}/comm') as f:
                if f.read().strip() != comm:
                    continue
            with open(f'/proc/{p}/environ', 'rb') as f:
                if home.encode() in f.read():
                    return int(p)
        except OSError:
            continue
    return None


def count(path, pattern):
    try:
        with open(path, errors='replace') as f:
            return sum(1 for l in f if pattern in l)
    except FileNotFoundError:
        return 0


def stop(home, proc):
    pid = find_pid('OniMoono', home)
    if pid:
        try:
            os.kill(pid, signal.SIGINT)
        except ProcessLookupError:
            pass
    try:
        proc.wait(timeout=60)
    except subprocess.TimeoutExpired:
        pid = find_pid('OniMoono', home)
        if pid:
            os.kill(pid, signal.SIGKILL)
        proc.kill()
        proc.wait()


def run(argv):
    import argparse

    ap = argparse.ArgumentParser(prog='perf-spread.py run')
    ap.add_argument('worktree')
    ap.add_argument('out')
    ap.add_argument('--label', default='A')
    ap.add_argument('--save', default=os.environ.get('SPREAD_SAVE'))
    ap.add_argument('--pause-windows', type=int, default=3)
    ap.add_argument('--run-windows', type=int, default=4)
    ap.add_argument('--window', type=int, default=600, help='frames a window')
    ap.add_argument('--size', default='1280x720', help='the window, WxH')
    ap.add_argument('--host-args', default='', help="more of the player's arguments, space-separated")
    ap.add_argument('--timeout', type=int, default=2400, help='seconds for the whole launch')
    a = ap.parse_args(argv)
    game, steam, probe = (os.environ.get(n) for n in ('SPREAD_GAME', 'SPREAD_STEAM', 'SPREAD_PROBE'))
    for name, value in (('SPREAD_GAME', game), ('SPREAD_STEAM', steam), ('SPREAD_PROBE', probe), ('--save', a.save)):
        if not value or not os.path.exists(value):
            raise SystemExit(f'perf-spread: {name} is missing ({value})')
    out = os.path.abspath(a.out)
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(out)
    size = a.size.split('x')
    home = prepare_home(out, os.path.abspath(a.save), probe, a.pause_windows, a.window, size)
    log = os.path.join(home, GAME_DIR, 'Player.log')
    env = dict(os.environ, ONI_TWEAK_SETTINGS='1', ONI_GAME_DIR=game, ONI_RUN_HOME=home, ONI_CONFIG='release')
    worktree = os.path.abspath(a.worktree)
    cmd = [
        os.path.join(worktree, 'scripts', 'run.sh'),
        '--splash',
        'none',
        '--log-paths',
        'full',
        '--frame-stats',
        *a.host_args.split(),
        '--native-override',
        f'steam_api={steam}',
        *('-screen-width', size[0], '-screen-height', size[1], '-screen-fullscreen', '0'),
    ]
    started = time.time()
    with open(os.path.join(out, 'stdout.txt'), 'w') as sink:
        proc = subprocess.Popen(['timeout', '-k', '10', str(a.timeout + 60), *cmd], cwd=worktree, env=env, stdout=sink, stderr=subprocess.STDOUT)
    wanted = a.pause_windows + a.run_windows
    colony = None
    status = 'ok'
    while True:
        if proc.poll() is not None:
            status = f'exited {proc.returncode}'
            break
        if colony is None and count(log, 'PERF colony'):
            colony = time.time() - started
        if count(log, 'PERF sites f') >= wanted:
            break
        if time.time() - started > a.timeout:
            status = 'timed out'
            break
        time.sleep(3)
    took = time.time() - started
    if proc.poll() is None:
        stop(home, proc)
    result = parse(log, a.pause_windows)
    result.update(
        label=a.label,
        worktree=os.path.basename(worktree),
        status=status,
        seconds=round(took, 1),
        to_colony_s=round(colony, 1) if colony else None,
        window=a.window,
        size=a.size,
    )
    with open(os.path.join(out, 'result.json'), 'w') as f:
        json.dump(result, f, indent=1)
    got = len(result['windows'])
    print(f'perf-spread: {a.label} {status}, {got} windows of {wanted} in {took:.0f} s (colony after {result["to_colony_s"]} s)', flush=True)
    if got < wanted:
        for line in failure_lines([os.path.join(out, 'stdout.txt'), log], home):
            print('  ' + line, flush=True)
    return 0 if got >= wanted else 1


# ---- reading a log ----

FAILURE = re.compile(r'exception|error|abort|fatal|assert|unhandled|signal|crash', re.IGNORECASE)


def failure_lines(paths, home, most=12):
    """A failed launch's last error lines, short, with the run home's path left out: verdicts and
    short excerpts only, as the CI game tests print (ci/game-tests/README.md)."""
    found = []
    for path in paths:
        try:
            with open(path, errors='replace') as f:
                found += [l.strip().replace(home, 'HOME')[:240] for l in f if FAILURE.search(l)]
        except FileNotFoundError:
            pass
    found = found[-most:]
    # And the player's last words, which a native abort leaves without a keyword.
    try:
        with open(paths[0], errors='replace') as f:
            found += ['(end) ' + l.strip().replace(home, 'HOME')[:240] for l in f.readlines()[-6:]]
    except FileNotFoundError:
        pass
    return found


NUM = r'(-?[\d.]+)'


def parse(log, pause_windows):
    """Each 600-frame window after the colony is up: PerfProbe's counters (cumulative, so taken
    as differences) and the frame stats lines logged within it. A window's `PERF f` line comes
    first, then its `PERF alloc` and `PERF sites` lines; frame stats lines before it are its own."""
    windows, pending = [], None
    prev_alloc, prev_sites = None, None
    try:
        with open(log, errors='replace') as f:
            lines = f.read().splitlines()
    except FileNotFoundError:
        lines = []

    def fresh():
        return {'fs': [], 'fc': [], 'sim': [], 'speed': []}

    for line in lines:
        if 'PERF colony' in line:
            pending = fresh()
            continue
        if pending is None:
            continue
        if m := re.search(r'PERF f(\d+) fps ' + NUM, line):
            pending.update(frame=int(m.group(1)), fps=float(m.group(2)))
            windows.append(pending)
            pending = fresh()
        elif windows and (m := re.search(r'PERF alloc f\d+ n (\d+) rt ' + NUM + ' gt ' + NUM + r' mono (-?\d+) .* dc (\d+)/(\d+)/(\d+)', line)):
            alloc = [int(m.group(1)), float(m.group(2)), float(m.group(3)), int(m.group(4)), int(m.group(5)), int(m.group(6)), int(m.group(7))]
            # The first window has no start to take from (mono is the process's total).
            windows[-1]['alloc'] = None if prev_alloc is None else [x - y for x, y in zip(alloc, prev_alloc)]
            prev_alloc = alloc
        elif windows and 'PERF sites f' in line:
            sites = {}
            for part in line.split('PERF sites f', 1)[1].split(' ', 1)[1].split(' | '):
                if '=' in part:
                    name, value = part.rsplit('=', 1)
                    b, c = value.split('/')
                    sites[name.strip()] = (int(b), int(c))
            windows[-1]['sites'] = None if prev_sites is None else {k: v[0] - prev_sites.get(k, (0, 0))[0] for k, v in sites.items()}
            prev_sites = sites
        elif '] frame stats (ms/frame' in line:
            body = line.split('):', 1)[1]
            parts = {k.strip(): float(v) for k, v in re.findall(r'([a-z+\- ]+?) ' + NUM + r'(?:,|$)', body)}
            pending['fs'].append(sum(parts.get(p, 0.0) for p in MAIN_PHASES))
        elif '] frame counts (per frame' in line:
            seg = line.split(';')
            if len(seg) > 1:
                draws = re.search(r'draws (\d+)', seg[1])
                up = re.search(r'texture uploads ' + NUM + r' \((\d+) Ki?B\)', seg[1])
                if draws and up:
                    pending['fc'].append((int(draws.group(1)), float(up.group(1)), int(up.group(2))))
        elif '] sim (ms/frame' in line:
            m = re.search(r'SimEveryTick ' + NUM + r'.*UnsafeSim200ms ' + NUM + r'.*StepTheSim ' + NUM, line)
            if m:
                pending['sim'].append((float(m.group(1)), float(m.group(2)), float(m.group(3))))
        elif '] game speed (over' in line:
            m = re.search(NUM + r' game s a real s', line)
            if m:
                pending['speed'].append(float(m.group(1)))
    out = []
    for i, w in enumerate(windows):
        if not w.get('alloc'):
            continue
        n, rt, gt, mono, g0, g1, g2 = w['alloc']
        phase = 'paused' if i < pause_windows else 'running'
        rec = {
            'index': i,
            'phase': phase,
            'frames': n,
            'real_s': round(rt, 3),
            'game_s': round(gt, 3),
            'alloc_bytes': mono,
            'gc': [g0, g1, g2],
            'frame_ms': round(1000.0 / w['fps'], 3) if w.get('fps') else None,
            'main_ms': round(statistics.mean(w['fs']), 3) if w['fs'] else None,
        }
        if w['fc']:
            rec['draws'] = round(statistics.mean(x[0] for x in w['fc']), 2)
            rec['uploads'] = round(statistics.mean(x[1] for x in w['fc']), 2)
            rec['upload_kb'] = round(statistics.mean(x[2] for x in w['fc']) * 1.024, 1)
        if w['sim']:
            rec['step_ms'] = round(statistics.mean(x[2] for x in w['sim']), 3)
            rec['sim_ms'] = round(statistics.mean(sum(x) for x in w['sim']), 3)
        if w['speed']:
            rec['game_s_per_s'] = w['speed'][-1]
        rec['sites'] = w.get('sites') or {}
        out.append(rec)
    return {'windows': out}


# ---- one job's summary ----


def cpu_model():
    names, n = [], 0
    try:
        for line in open('/proc/cpuinfo'):
            if line.startswith('model name'):
                n += 1
                names.append(line.split(':', 1)[1].strip())
    except FileNotFoundError:
        pass
    return f'{names[0]} x{n}' if names else 'unknown'


def measured(result, phase):
    """A phase's windows; the first running one, which the switch from paused falls in, left out
    (the first paused one, the load settling, has no counters)."""
    ws = [w for w in result['windows'] if w['phase'] == phase]
    return ws[1:] if phase == 'running' and len(ws) > 1 else ws


def launch_metrics(result):
    """One launch's numbers: each count by window (its spread within the launch) and over all its
    measured windows together (the launch's value), times as the windows' medians. Running counts
    go per game second and per frame: on a slow renderer each frame gets about the same game time."""
    m = {}
    for phase in ('paused', 'running'):
        ws = measured(result, phase)
        if not ws:
            continue
        dens = {'frame': lambda w: w['frames'], 'game s': lambda w: w['game_s'] or 1e-9}
        units = ['frame'] if phase == 'paused' else ['game s', 'frame']
        counts, totals = {}, {}
        for unit in units:
            den = dens[unit]
            for key, num in (
                (f'alloc B a {unit}', lambda w: w['alloc_bytes']),
                (f'gen0 GCs a {unit}', lambda w: w['gc'][0]),
                (f'gen1+2 GCs a {unit}', lambda w: w['gc'][1] + w['gc'][2]),
            ):
                counts[key] = [num(w) / den(w) for w in ws]
                totals[key] = sum(num(w) for w in ws) / sum(den(w) for w in ws)
        for key, name in (('draws a frame', 'draws'), ('texture uploads a frame', 'uploads'), ('upload KB a frame', 'upload_kb')):
            vals = [w[name] for w in ws if w.get(name) is not None]
            counts[key] = vals
            if vals:
                totals[key] = sum(v * w['frames'] for v, w in zip(vals, ws)) / sum(w['frames'] for w in ws)
        den = dens[units[0]]
        sites = {}
        for w in ws:
            for k, v in w['sites'].items():
                sites[k] = sites.get(k, 0) + v
        sites = {k: v / sum(den(w) for w in ws) for k, v in sites.items()}
        times = {}
        for key in ('main_ms', 'frame_ms', 'step_ms', 'sim_ms', 'game_s_per_s'):
            vals = [w[key] for w in ws if w.get(key) is not None]
            if vals:
                times[key] = statistics.median(vals)
        m[phase] = {'counts': counts, 'totals': totals, 'sites': sites, 'times': times}
    return m


def rel_spread(values):
    values = [v for v in values if v is not None]
    if len(values) < 2:
        return None
    mid = statistics.median(values)
    return (max(values) - min(values)) / abs(mid) * 100 if mid else (0.0 if max(values) == min(values) else float('inf'))


def summary(argv):
    # --a NAME, --b NAME, --save NAME: the report's header.
    meta = {}
    while len(argv) > 1 and argv[0].startswith('--'):
        meta[argv[0][2:]] = argv[1]
        argv = argv[2:]
    out, runs = argv[0], argv[1:]
    results = []
    for r in runs:
        try:
            with open(os.path.join(r, 'result.json')) as f:
                results.append(json.load(f))
        except (OSError, ValueError):
            pass
    os.makedirs(out, exist_ok=True)
    launches = [
        {
            'label': r['label'],
            'status': r['status'],
            'to_colony_s': r['to_colony_s'],
            'seconds': r['seconds'],
            'windows': len(r['windows']),
            'metrics': launch_metrics(r),
        }
        for r in results
    ]
    s = {'cpu': cpu_model(), 'nproc': os.cpu_count(), 'runner': os.environ.get('RUNNER_NAME'), 'meta': meta, 'launches': launches}
    # Exact counts: each label's launches, every measured window's value.
    counts = {}
    for phase in ('paused', 'running'):
        for label in sorted({l['label'] for l in launches}):
            ls = [l['metrics'].get(phase) for l in launches if l['label'] == label and l['metrics'].get(phase)]
            if not ls:
                continue
            for key in ls[0]['counts']:
                within = [rel_spread(l['counts'][key]) for l in ls]
                values = [l['totals'][key] for l in ls if key in l['totals']]
                between = rel_spread(values)
                counts.setdefault(phase, {}).setdefault(key, {})[label] = {
                    'median': statistics.median(values or [0]),
                    'within_launch_pct': max([w for w in within if w is not None], default=None),
                    'between_launches_pct': between,
                }
            # Sections: the largest by bytes, their spread between launches.
            names = {}
            for l in ls:
                for k, v in l['sites'].items():
                    names.setdefault(k, []).append(v)
            ranked = sorted(names.items(), key=lambda kv: -abs(statistics.median(kv[1])))
            counts[phase].setdefault('sections', {})[label] = {k: {'median': statistics.median(v), 'between_launches_pct': rel_spread(v)} for k, v in ranked}
    s['counts'] = counts
    # Timings: A/B pairs in launch order, the ratio B/A of each pair's medians.
    timing = {}
    for phase in ('paused', 'running'):
        a = [l['metrics'].get(phase, {}).get('times', {}) for l in launches if l['label'] == 'A']
        b = [l['metrics'].get(phase, {}).get('times', {}) for l in launches if l['label'] == 'B']
        for key in ('main_ms', 'frame_ms', 'step_ms', 'sim_ms', 'game_s_per_s'):
            av = [t[key] for t in a if key in t]
            bv = [t[key] for t in b if key in t]
            pairs = [y / x for x, y in zip(av, bv) if x]
            if not av:
                continue
            entry = {'A_median': statistics.median(av), 'A_spread_pct': rel_spread(av)}
            if bv:
                entry.update(
                    B_median=statistics.median(bv),
                    B_spread_pct=rel_spread(bv),
                    ratio_of_medians=statistics.median(bv) / statistics.median(av) if statistics.median(av) else None,
                )
            if len(pairs) >= 2:
                entry.update(
                    pair_ratios=[round(p, 4) for p in pairs],
                    pair_ratio_sd_pct=statistics.stdev(pairs) * 100,
                    detectable_pct=2 * statistics.stdev(pairs) / len(pairs) ** 0.5 * 100,
                )
            timing.setdefault(phase, {})[key] = entry
    s['timing'] = timing
    s['checks'] = checks(s)
    with open(os.path.join(out, 'summary.json'), 'w') as f:
        json.dump(s, f, indent=1)
    md = markdown(s)
    with open(os.path.join(out, 'summary.md'), 'w') as f:
        f.write(md)
    print(md)
    return 0


def pct(v, signed=False):
    if v is None:
        return '–'
    return f'{v:+.1f}%' if signed else f'{v:.1f}%'


def num(v):
    """Thousands separators; small values to three figures."""
    if v is None:
        return '–'
    if v == 0:
        return '0'
    return f'{v:,.0f}' if abs(v) >= 100 else f'{v:.3g}'


def change(a, b):
    if a is None or b is None:
        return '–'
    if a == b:
        return '0'
    return '–' if not a else f'{(b / a - 1) * 100:+.1f}%'


ICONS = {'pass': '✅', 'fail': '❌', 'warn': '⚠️', 'note': 'ℹ️'}
BUCKET_RATES = {'EVERY_TICK': 'every tick', '33ms': '33 ms', '200ms': '200 ms', '1000ms': '1,000 ms', '4000ms': '4,000 ms'}


def section_parts(name):
    """(section, kind) for a PERF sites name: `bucket OrbitalObject RENDER_1000ms` is the state
    machine bucket OrbitalObject at render 1,000 ms; `(other)` the main thread outside them all."""
    if name == '(other)':
        return 'Main thread, outside the sections', '–'
    if name.startswith('bucket '):
        parts = name.split(' ')
        if len(parts) >= 3 and '_' in parts[-1]:
            kind, _, rate = parts[-1].partition('_')
            return ' '.join(parts[1:-1]), f'bucket, {kind.lower()} {BUCKET_RATES.get(rate, rate)}'
        return name[len('bucket ') :], 'bucket'
    return name, 'method'


def section_label(name):
    section, kind = section_parts(name)
    return section if kind in ('–', 'method') else f'{section}, {kind}'


def table(head, rows):
    return ['| ' + ' | '.join(head) + ' |', '|' + '|'.join('---' for _ in head) + '|'] + ['| ' + ' | '.join(str(c) for c in r) + ' |' for r in rows]


def verdict_line(cs):
    fails = sum(c['status'] == 'fail' for c in cs)
    warns = sum(c['status'] == 'warn' for c in cs)
    head = '❌ **Failed**' if fails else '⚠️ **Passed with a warning**' if warns else '✅ **Passed**'
    return f'{head}: {fails} failed, {warns} warned, of {len(cs)} checks.'


COUNT_ROWS = (
    ('draws a frame', 'Draws a frame'),
    ('texture uploads a frame', 'Texture uploads a frame'),
    ('upload KB a frame', 'Texture upload KB a frame'),
    ('alloc B a frame', 'Allocated bytes a frame'),
    ('alloc B a game s', 'Allocated bytes a game second'),
    ('gen0 GCs a frame', 'Gen0 collections a frame'),
    ('gen1+2 GCs a frame', 'Gen1 and gen2 collections a frame'),
    ('gen0 GCs a game s', 'Gen0 collections a game second'),
    ('gen1+2 GCs a game s', 'Gen1 and gen2 collections a game second'),
)
TIME_ROWS = (
    ('main_ms', 'Main thread, ms a frame'),
    ('step_ms', 'Sim step, ms a frame'),
    ('sim_ms', 'Sim in all, ms a frame'),
    ('frame_ms', 'Frame, ms (the software renderer)'),
)


def markdown(s):
    meta = s.get('meta', {})
    pairs = min(sum(l['label'] == 'A' for l in s['launches']), sum(l['label'] == 'B' for l in s['launches']))
    a_name, b_name = meta.get('a', 'the release'), meta.get('b', 'this commit')
    out = [
        f'## Perf: {a_name} (A) against {b_name} (B)',
        '',
        f'{meta.get("save", "the save")} · {s["cpu"]} ({s["nproc"]} CPUs) · {pairs} pairs, launched A B A B …',
        '',
    ]
    cs = s.get('checks') or []
    if cs:
        out += [verdict_line(cs), '']
        names = {'hard': 'Hard gate', 'tolerance': 'Tolerance', 'soft': 'Warning only'}
        rows = []
        for c in cs:
            rows.append([ICONS[c['status']], c['name'], num(c['a']), num(c['b']), c['limit'], names[c['group']]])
        out += table(['', 'Check', 'A', 'B', 'Limit', 'Kind'], rows) + ['']
        out += ['Hard gates and tolerances fail the job; the warning does not. Frame rates and absolute times are never judged.', '']
    counts = s.get('counts', {})
    for phase, title in (('paused', 'Paused colony'), ('running', 'Running at speed 3')):
        keys = counts.get(phase)
        if not keys:
            continue
        rows = []
        for key, label in COUNT_ROWS:
            by = keys.get(key)
            if not by:
                continue
            a, b = by.get('A', {}), by.get('B', {})
            spread = max([x for x in (a.get('between_launches_pct'), b.get('between_launches_pct')) if x is not None], default=None)
            rows.append([label, num(a.get('median')), num(b.get('median')), change(a.get('median'), b.get('median')), pct(spread)])
        out += [f'### {title}', ''] + table(['Count', 'A', 'B', 'Change', 'Spread'], rows) + ['']
        if phase == 'paused':
            out += ['Change is B against A. Spread is the wider of A\'s and B\'s spreads between their launches (largest less smallest, over the median).', '']
        secs_a, secs_b = keys.get('sections', {}).get('A', {}), keys.get('sections', {}).get('B', {})
        if secs_a or secs_b:
            unit = 'B a frame' if phase == 'paused' else 'B a game s'
            out += [f'Allocations by section, the 12 largest ({unit}):', ''] + section_table(secs_a, secs_b, 12) + ['']
    timing = s.get('timing', {}).get('running', {})
    if timing:
        rows = []
        for key, label in TIME_ROWS:
            e = timing.get(key)
            if not e:
                continue
            ratio = e.get('ratio_of_medians')
            rows.append(
                [
                    label,
                    num(e['A_median']),
                    num(e.get('B_median')),
                    '–' if ratio is None else f'{ratio:.3f}',
                    pct(e.get('pair_ratio_sd_pct')),
                    pct(e.get('detectable_pct')),
                ]
            )
        out += ['### Times while running (not judged, but the main thread warns)', '']
        out += table(['Time', 'A', 'B', 'B/A', 'Pairs sd', 'Detectable'], rows) + ['']
        out += ['Times differ by 20–40% between runner machines: only B/A, measured in turn on one machine, compares.', '']
    out += ['<details><summary>Each launch, and every section</summary>', '']
    rows = []
    for i, l in enumerate(s['launches'], 1):
        m = l['metrics']
        p, r = m.get('paused', {}), m.get('running', {})
        rows.append(
            [
                i,
                l['label'],
                l['status'],
                '–' if l['to_colony_s'] is None else f'{l["to_colony_s"]:.0f} s',
                l['windows'],
                num(p.get('totals', {}).get('alloc B a frame')),
                num(p.get('totals', {}).get('draws a frame')),
                num(r.get('totals', {}).get('draws a frame')),
                num(r.get('times', {}).get('main_ms')),
                num(r.get('times', {}).get('step_ms')),
            ]
        )
    out += table(['#', 'Build', 'Status', 'To colony', 'Windows', 'Paused B a frame', 'Paused draws', 'Running draws', 'Main ms', 'Step ms'], rows) + ['']
    for phase in ('paused', 'running'):
        secs = counts.get(phase, {}).get('sections', {})
        if secs:
            unit = 'B a frame' if phase == 'paused' else 'B a game s'
            out += [f'Every section, {phase} ({unit}):', ''] + section_table(secs.get('A', {}), secs.get('B', {}), None) + ['']
    out += ['</details>', '']
    return '\n'.join(out)


def section_table(secs_a, secs_b, most):
    names = sorted(set(secs_a) | set(secs_b), key=lambda k: -max(secs_a.get(k, {}).get('median', 0), secs_b.get(k, {}).get('median', 0)))
    rows = []
    for name in names[:most] if most else names:
        a, b = secs_a.get(name, {}), secs_b.get(name, {})
        section, kind = section_parts(name)
        spread = max([x for x in (a.get('between_launches_pct'), b.get('between_launches_pct')) if x is not None], default=None)
        rows.append([section, kind, num(a.get('median')), num(b.get('median')), change(a.get('median'), b.get('median')), pct(spread)])
    return table(['Section', 'Kind', 'A', 'B', 'Change', 'Spread'], rows)


# ---- several jobs ----


def jobs(argv):
    ss = []
    for path in argv:
        with open(path) as f:
            ss.append(json.load(f))
    print('CPUs: ' + '; '.join(s['cpu'] for s in ss))
    for phase in ('paused', 'running'):
        for key in ('main_ms', 'frame_ms', 'step_ms', 'sim_ms', 'game_s_per_s'):
            vals = [s['timing'].get(phase, {}).get(key, {}) for s in ss]
            a = [v['A_median'] for v in vals if 'A_median' in v]
            r = [v['ratio_of_medians'] for v in vals if v.get('ratio_of_medians')]
            if a:
                print(
                    f'{phase} {key}: A by job {[round(x, 3) for x in a]} (spread {pct(rel_spread(a))}), B/A by job {[round(x, 4) for x in r]} (spread {pct(rel_spread(r))})'
                )
    return 0


# ---- the nightly's gates ----

# Counts that were exactly steady on every runner (docs/PERFORMANCE.md, "Perf spread on CI's
# runners"): B over A fails.
HARD = (
    ('paused', 'draws a frame', 'Draws a frame, paused'),
    ('paused', 'gen0 GCs a frame', 'Gen0 collections a frame, paused'),
    ('paused', 'gen1+2 GCs a frame', 'Gen1 and gen2 collections a frame, paused'),
    ('running', 'gen0 GCs a game s', 'Gen0 collections a game second, running'),
    ('running', 'gen1+2 GCs a game s', 'Gen1 and gen2 collections a game second, running'),
)
# Counts steady within a few percent: B past A by more than the tolerance fails.
TOLERANCE = (
    ('paused', 'alloc B a frame', 'Allocations a frame, paused', 5.0),
    ('running', 'draws a frame', 'Draws a frame, running', 3.0),
)
SECTION_TOLERANCE = 5.0
# Sections smaller than this (B a frame, paused) are left out: a few bytes swing by a lot.
SECTION_FLOOR = 200
# The interleaved main-thread ratio warns past the larger of this and its own 2 sd/sqrt(n).
RATIO_WARN = 3.0


def checks(s):
    """The gates on one job's summary: each {group, name, a, b, limit, status}, status 'pass',
    'fail', 'warn' or 'note' (not measured, or nothing to compare)."""
    out = []
    has = {lab: any(l['label'] == lab and l['windows'] > 0 for l in s['launches']) for lab in ('A', 'B')}
    if not has['B']:
        return [dict(group='hard', name='This commit (B) measured', a=None, b=None, limit='every launch failed', status='fail')]
    if not has['A']:
        return [
            dict(group='hard', name='The release (A) measured', a=None, b=None, limit='nothing to compare (it may not run on this game build)', status='note')
        ]
    counts = s['counts']

    def value(phase, key, lab):
        return counts.get(phase, {}).get(key, {}).get(lab, {}).get('median')

    for phase, key, name in HARD:
        a, b = value(phase, key, 'A'), value(phase, key, 'B')
        status = 'note' if a is None or b is None else 'fail' if b > a + 1e-9 else 'pass'
        out.append(dict(group='hard', name=name, a=a, b=b, limit='no more than A', status=status))
    for phase, key, name, tol in TOLERANCE:
        a, b = value(phase, key, 'A'), value(phase, key, 'B')
        status = 'note' if not a or b is None else 'fail' if (b / a - 1) * 100 > tol else 'pass'
        out.append(dict(group='tolerance', name=name, a=a, b=b, limit=f'at most {tol:g}% over A', status=status))
    sa = counts.get('paused', {}).get('sections', {}).get('A', {})
    sb = counts.get('paused', {}).get('sections', {}).get('B', {})
    worst = None
    over = []
    for section, ea in sa.items():
        eb = sb.get(section)
        if eb is None or ea['median'] < SECTION_FLOOR:
            continue
        # A section the release's own launches spread wider than the tolerance gets that spread.
        tol = max(SECTION_TOLERANCE, ea['between_launches_pct'] or 0)
        change = (eb['median'] / ea['median'] - 1) * 100
        if change > tol:
            over.append(f'{section_label(section)} {change:+.1f}% against {tol:.1f}%')
        if worst is None or change - tol > worst[0]:
            worst = (change - tol, section, change, tol)
    if worst:
        _, section, rise, _ = worst
        detail = f'each at most {SECTION_TOLERANCE:g}% over A (or A\'s own spread); '
        detail += 'over: ' + '; '.join(over) if over else f'closest: {section_label(section)}, {rise:+.1f}%'
        out.append(dict(group='tolerance', name='Allocations by section, paused', a=None, b=None, limit=detail, status='fail' if over else 'pass'))
    t = s['timing'].get('running', {}).get('main_ms', {})
    if t.get('ratio_of_medians'):
        change = (t['ratio_of_medians'] - 1) * 100
        limit = max(RATIO_WARN, t.get('detectable_pct') or 0)
        out.append(
            dict(
                group='soft',
                name='Main-thread ms a frame, running (B/A)',
                a=t['A_median'],
                b=t.get('B_median'),
                limit=f'{change:+.1f}% against ±{limit:.1f}% detectable',
                status='warn' if abs(change) > limit else 'pass',
            )
        )
    return out


def gate(argv):
    """Exit 1 when one of SUMMARY.json's gates failed (the summary's report shows them); prints
    the verdict line."""
    with open(argv[0]) as f:
        s = json.load(f)
    cs = s.get('checks') or checks(s)
    print(verdict_line(cs))
    return 1 if any(c['status'] == 'fail' for c in cs) else 0


def main():
    commands = {'run': run, 'summary': summary, 'jobs': jobs, 'gate': gate}
    if len(sys.argv) < 2 or sys.argv[1] not in commands:
        print(__doc__)
        return 2
    return commands[sys.argv[1]](sys.argv[2:])


if __name__ == '__main__':
    sys.exit(main())
