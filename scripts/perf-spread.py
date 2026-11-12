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
    scripts/perf-spread.py summary OUT RUN...
        The runs' spreads (OUT/summary.md, OUT/summary.json): exact counts across a label's
        launches, the A/B timing ratio by pair, and the machine's CPU.
    scripts/perf-spread.py jobs SUMMARY.json...
        Several jobs' summaries side by side: the between-job spread of the absolute times.

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
        lines = open(log, errors='replace').read().splitlines()
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
    out, runs = argv[0], argv[1:]
    results = []
    for r in runs:
        try:
            results.append(json.load(open(os.path.join(r, 'result.json'))))
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
    s = {'cpu': cpu_model(), 'nproc': os.cpu_count(), 'runner': os.environ.get('RUNNER_NAME'), 'launches': launches}
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
            top = sorted(names.items(), key=lambda kv: -abs(statistics.median(kv[1])))[:12]
            counts[phase].setdefault('sections', {})[label] = {k: {'median': statistics.median(v), 'between_launches_pct': rel_spread(v)} for k, v in top}
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
    with open(os.path.join(out, 'summary.json'), 'w') as f:
        json.dump(s, f, indent=1)
    md = markdown(s)
    with open(os.path.join(out, 'summary.md'), 'w') as f:
        f.write(md)
    print(md)
    return 0


def pct(v):
    return '–' if v is None else f'{v:.2f}%'


def num(v):
    return '–' if v is None else (f'{v:,.0f}' if abs(v) >= 100 else f'{v:.3g}')


def markdown(s):
    out = [f'## Perf spread: {s["cpu"]} ({s["nproc"]} CPUs)', '']
    out.append('| launch | status | to colony | windows |')
    out.append('|---|---|---|---|')
    for l in s['launches']:
        out.append(f'| {l["label"]} | {l["status"]} | {l["to_colony_s"]} s | {l["windows"]} |')
    for phase, keys in s['counts'].items():
        out += [
            '',
            f'### Counts, {phase} (spread: max−min over the median)',
            '',
            '| count | label | median | within a launch | between launches |',
            '|---|---|---|---|---|',
        ]
        for key, by in keys.items():
            if key == 'sections':
                continue
            for label, e in by.items():
                out.append(f'| {key} | {label} | {num(e["median"])} | {pct(e["within_launch_pct"])} | {pct(e["between_launches_pct"])} |')
        for label, secs in keys.get('sections', {}).items():
            out += ['', f'Sections, {phase}, {label} (B a {"frame" if phase == "paused" else "game s"}, between launches):', '']
            out.append(', '.join(f'{k} {num(v["median"])} ({pct(v["between_launches_pct"])})' for k, v in secs.items()))
    for phase, keys in s['timing'].items():
        out += [
            '',
            f'### Times, {phase} (launch medians)',
            '',
            '| time | A | A spread | B | B spread | B/A | pair ratio sd | 2 sd/√n |',
            '|---|---|---|---|---|---|---|---|',
        ]
        for key, e in keys.items():
            out.append(
                f'| {key} | {num(e["A_median"])} | {pct(e["A_spread_pct"])} | {num(e.get("B_median"))} | {pct(e.get("B_spread_pct"))} | '
                f'{"–" if e.get("ratio_of_medians") is None else f"{e["ratio_of_medians"]:.4f}"} | {pct(e.get("pair_ratio_sd_pct"))} | {pct(e.get("detectable_pct"))} |'
            )
    return '\n'.join(out) + '\n'


# ---- several jobs ----


def jobs(argv):
    ss = [json.load(open(p)) for p in argv]
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


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ('run', 'summary', 'jobs'):
        print(__doc__)
        return 2
    return {'run': run, 'summary': summary, 'jobs': jobs}[sys.argv[1]](sys.argv[2:])


if __name__ == '__main__':
    sys.exit(main())
