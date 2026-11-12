#!/usr/bin/env python3
"""Managed allocations, the original against Moono, by run and by update section
(docs/research/allocations-vs-original.md; the counters in tools/PerfProbe's AllocMeter and
AllocSites). Linux, on Xvfb, players through scripts/contain.sh.

    scripts/alloc-compare.py run orig|ours SAVE OUT [--fps N] [--sites] [--trace] [--secs 600] [--warm 90]
        One launch: SAVE loaded at speed 3 with PerfProbe (alloc.txt, novictory.txt, no autosaves),
        1280x720, measured for --secs after --warm s past the first frame-rate window. Moono gets a
        warm-up launch first and runs with its default tweaks. --fps caps the frame rate (both
        players, vsync off); --sites adds the per-section counters; --trace (Moono only) records
        an allocation trace with stacks and thread ids for `split`. Runs as a runq job of kind
        linux-perf (docs/RUNQ.md); with RUNQ=off or no daemon it holds the perf queue
        (perf.wanted and perf.lock, as qa.sh's perf stage) and a player's place in scripts/e2e.py's
        memory budget for the launch, and gives both back after it.
    scripts/alloc-compare.py rates RUN...       KB a frame, MB a game s and a real s, collections
    scripts/alloc-compare.py sites RUN [N]      the N largest sections (KB a game s and a frame)
    scripts/alloc-compare.py compare ORIG OURS  the two runs' sections side by side, buckets grouped
    scripts/alloc-compare.py split RUN [N]      a --trace run by thread, main-thread section and site

Settings (environment, else out/local-tools/qa.env as scripts/e2e.py reads it):
    ALLOC_GAME      the game folder Moono runs (default QA_U57_GAME)
    ALLOC_STEAM     the Steam double Moono loads (default QA_U57_STEAM)
    ALLOC_ORIGINAL  the original player's executable (default E2E_ORIGINAL)
    ALLOC_WORKTREE  the Moono checkout to run, built for release (default this one)
    ALLOC_PROBE     PerfProbe's build (default out/bin/PerfProbe/release: dotnet build tools/PerfProbe
                    -c Release -p:OniManaged=GAME/OxygenNotIncluded_Data/Managed, GAME's version)
    ALLOC_TRACE_TOOL  tools/AllocTrace's build (default out/bin/AllocTrace/release/AllocTrace)
    ALLOC_GB        the player's place in the memory budget (default 15)
"""

import collections, contextlib, fcntl, os, re, shutil, signal, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'scripts'))
STEAM_ID = '76561197960265729'
TRACE_PROVIDERS = 'Microsoft-Windows-DotNETRuntime:0x1:5'


# ---- settings ----


def settings():
    import e2e

    env = e2e.environment()
    get = lambda name, default: os.environ.get(name) or default
    return {
        'game': get('ALLOC_GAME', env.get('QA_U57_GAME')),
        'steam': get('ALLOC_STEAM', env.get('QA_U57_STEAM')),
        'original': get('ALLOC_ORIGINAL', env.get('E2E_ORIGINAL')),
        'worktree': get('ALLOC_WORKTREE', ROOT),
        'probe': get('ALLOC_PROBE', os.path.join(ROOT, 'out', 'bin', 'PerfProbe', 'release')),
        'trace_tool': get('ALLOC_TRACE_TOOL', os.path.join(ROOT, 'out', 'bin', 'AllocTrace', 'release', 'AllocTrace')),
        'gb': float(get('ALLOC_GB', 15)),
        'gallium': env.get('GALLIUM_DRIVER'),
        'adapter': env.get('MESA_D3D12_DEFAULT_ADAPTER_NAME'),
    }


# ---- the perf queue ----


def runq_self_submit(argv, a, s):
    """Re-runs this launch as a runq job of kind linux-perf (docs/RUNQ.md): runq holds the perf
    slot and a player's place for it, on a quiet machine. Not inside a job, with RUNQ=off or
    without a daemon: PerfQueue and e2e.py's budget, as before."""
    if os.environ.get('RUNQ_JOB') or os.environ.get('RUNQ', '').lower() in ('off', '0', 'no', 'false'):
        return
    runq = os.path.join(ROOT, 'scripts', 'runq')
    ping = subprocess.run([sys.executable, runq, 'ping'], capture_output=True, text=True)
    if ping.returncode:
        print(f'runq: {ping.stderr.strip().removeprefix("runq: ")}; running directly with the old locks', file=sys.stderr)
        return
    minutes = 10 + (2 if a.player == 'ours' else 1) * 45  # each launch is capped at 45 min
    cmd = [
        sys.executable,
        runq,
        'run',
        '--kind',
        'linux-perf',
        '--needs',
        f'mem={s["gb"]:g}',
        '--timeout',
        f'{minutes}m',
        '--label',
        f'alloc-compare run {a.player} {os.path.basename(a.save)}',
    ]
    os.execv(sys.executable, cmd + ['--', sys.executable, os.path.abspath(__file__), 'run', *argv])


class PerfQueue:
    """qa.sh's perf hold, taken without blocking on either lock while holding the other: waits
    until perf.wanted is absent and perf.lock is free, then holds both until release()."""

    def __init__(self):
        import e2e

        self.dir = e2e.shared_dir()
        self.wanted = os.path.join(self.dir, 'perf.wanted')
        self.fd = None

    def claim(self):
        os.makedirs(self.dir, exist_ok=True)
        while True:
            if not os.path.exists(self.wanted):
                fd = os.open(os.path.join(self.dir, 'perf.lock'), os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o644)
                try:
                    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError:
                    os.close(fd)
                else:
                    try:
                        w = os.open(self.wanted, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o644)
                        os.write(w, f'{os.getpid()}\n'.encode())
                        os.close(w)
                        self.fd = fd
                        print(f'perf queue claimed {time.strftime("%T")}', flush=True)
                        return
                    except FileExistsError:
                        fcntl.flock(fd, fcntl.LOCK_UN)
                        os.close(fd)
            time.sleep(15)

    def release(self):
        if self.fd is None:
            return
        try:
            os.unlink(self.wanted)
        except FileNotFoundError:
            pass
        fcntl.flock(self.fd, fcntl.LOCK_UN)
        os.close(self.fd)
        self.fd = None
        print(f'perf queue released {time.strftime("%T")}', flush=True)


# ---- one run ----

MODS_JSON = (
    '{"version":1,"mods":[{"label":{"distribution_platform":0,"id":"PerfProbe","title":"PerfProbe","version":-2037174645},'
    '"status":1,"enabled":true,"enabledForDlc":["","EXPANSION1_ID"],"crash_count":0,"reinstall_path":null,"staticID":"PerfProbe"}],'
    '"mod_load_in_progress":false}'
)
PREFS = 'strings: {}\nints:\n  ResolutionWidth: 1280\n  ResolutionHeight: 720\n  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 0\nfloats: {}\n'
SIZE = ['-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0']


def free_display():
    import e2e

    reserved = e2e.reservations()[1]
    for n in range(90, 100):
        if not e2e.display_in_use(n) and str(n) not in {str(d).lstrip(':') for d in reserved}:
            return n
    raise SystemExit('alloc-compare: no free X display in :90-:99')


def cpu_sample(pid):
    with open('/proc/stat') as f:
        busy = sum(int(x) for i, x in enumerate(f.readline().split()[1:]) if i in (0, 1, 2, 5, 6))
    with open(f'/proc/{pid}/stat') as f:
        fields = f.read().rsplit(')', 1)[1].split()
    return f'{time.time():.3f} {busy} {int(fields[11]) + int(fields[12])}'


def windows(log):
    try:
        with open(log, errors='replace') as f:
            return sum(1 for l in f if re.search(r'PERF alloc f\d', l))
    except FileNotFoundError:
        return 0


def find_pid(comm, home):
    found = None
    for p in os.listdir('/proc'):
        if not p.isdigit():
            continue
        try:
            with open(f'/proc/{p}/comm') as f:
                if f.read().strip() != comm:
                    continue
            with open(f'/proc/{p}/environ', 'rb') as f:
                if home.encode() in f.read():
                    found = int(p)
        except OSError:
            continue
    return found


def wait_for(path, pattern, proc):
    rx = re.compile(pattern)
    while True:
        try:
            with open(path, errors='replace') as f:
                if any(rx.search(l) for l in f):
                    return True
        except FileNotFoundError:
            pass
        if proc.poll() is not None:
            return False
        time.sleep(2)


def stop(pid, proc):
    if pid:
        try:
            os.kill(pid, signal.SIGINT)
        except ProcessLookupError:
            pass
        for _ in range(20):
            if not os.path.exists(f'/proc/{pid}'):
                break
            time.sleep(1)
        else:
            try:
                os.kill(pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()


def prepare_home(out, save, probe, fps, sites):
    home = os.path.join(out, 'home')
    d = os.path.join(home, 'config', 'unity3d', 'Klei', 'Oxygen Not Included')
    saves = os.path.join(d, 'cloud_save_files', STEAM_ID, 'INV')
    os.makedirs(saves)
    os.makedirs(os.path.join(d, 'mods', 'Local'))
    os.makedirs(os.path.join(d, 'Agreements', 'OxygenNotIncluded'))
    placed = os.path.join(saves, os.path.basename(save))
    shutil.copyfile(save, placed)
    with open(os.path.join(d, 'kplayerprefs.yaml'), 'w') as f:
        f.write(PREFS)
    with open(os.path.join(d, 'Agreements', 'OxygenNotIncluded', 'kprivacyprefs.yaml'), 'w') as f:
        f.write('disableDataCollection: true\n')
    mod = os.path.join(d, 'mods', 'Local', 'PerfProbe')
    shutil.copytree(probe, mod)
    files = {'load.txt': placed + '\n', 'noautosave.txt': '', 'alloc.txt': '', 'novictory.txt': '', 'speed.txt': '2\n'}
    if fps:
        files['fps.txt'] = f'{fps}\n'
    if sites:
        files['sites.txt'] = ''
    for name, text in files.items():
        with open(os.path.join(mod, name), 'w') as f:
            f.write(text)
    return home, os.path.join(d, 'mods', 'mods.json')


def original_folder(out, executable):
    """The original in a folder of its own (Unity's single-instance lock sits beside it)."""
    folder = os.path.join(out, 'player')
    os.makedirs(folder)
    source = os.path.dirname(os.path.abspath(executable))
    for name in os.listdir(source):
        if name == 'unity.lock':
            continue
        path = os.path.join(source, name)
        if name == os.path.basename(executable):
            shutil.copy2(os.path.realpath(path), os.path.join(folder, name))
        else:
            os.symlink(os.path.realpath(path), os.path.join(folder, name))
    return os.path.join(folder, os.path.basename(executable))


def dotnet_env():
    """The environment for .NET tools (dotnet-trace, AllocTrace): DOTNET_ROOT at ~/.dotnet when unset."""
    env = dict(os.environ)
    if not env.get('DOTNET_ROOT') and os.path.isdir(os.path.expanduser('~/.dotnet')):
        env['DOTNET_ROOT'] = os.path.expanduser('~/.dotnet')
    return env


def run(argv):
    import argparse
    import e2e

    ap = argparse.ArgumentParser(prog='alloc-compare.py run')
    ap.add_argument('player', choices=['orig', 'ours'])
    ap.add_argument('save')
    ap.add_argument('out')
    ap.add_argument('--fps', type=int)
    ap.add_argument('--sites', action='store_true')
    ap.add_argument('--trace', action='store_true')
    ap.add_argument('--secs', type=int, default=600)
    ap.add_argument('--warm', type=int, default=90)
    a = ap.parse_args(argv)
    if a.trace and a.player != 'ours':
        raise SystemExit('alloc-compare: --trace needs .NET (ours)')
    s = settings()
    need = ['probe'] + (['original'] if a.player == 'orig' else ['game', 'steam'])
    missing = [n for n in need if not s[n] or not os.path.exists(s[n])]
    if missing:
        raise SystemExit(f'alloc-compare: missing {", ".join(missing)} (see --help)')
    runq_self_submit(argv, a, s)
    out = os.path.abspath(a.out)
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(out)
    home, mods_json = prepare_home(out, os.path.abspath(a.save), s['probe'], a.fps, a.sites)
    env = dict(os.environ)
    for k, v in (('GALLIUM_DRIVER', s['gallium']), ('MESA_D3D12_DEFAULT_ADAPTER_NAME', s['adapter'])):
        if v:
            env[k] = v
    contain = os.path.join(ROOT, 'scripts', 'contain.sh')
    if a.player == 'orig':
        executable = original_folder(out, s['original'])
        comm, log = os.path.basename(executable)[:15], os.path.join(out, 'player.log')
        env.update(XDG_CONFIG_HOME=os.path.join(home, 'config'), XDG_CACHE_HOME=os.path.join(home, 'cache'), ORIGBASE_HOME=home)
    else:
        comm, log = 'OniMoono', os.path.join(out, 'stdout.txt')
        env.update(ONI_TWEAK_SETTINGS='1', ONI_GAME_DIR=s['game'], ONI_RUN_HOME=home, ONI_CONFIG='release')

    def launch(path):
        with open(mods_json, 'w') as f:
            f.write(MODS_JSON)
        if a.player == 'orig':
            cmd = [contain, executable, *SIZE, '-logFile', path]
            sink = open(os.path.join(out, 'player-stdout.txt'), 'a')
        else:
            cmd = [contain, 'scripts/run.sh', '--splash', 'none', '--log-paths', 'full', '--native-override', f'steam_api={s["steam"]}', *SIZE]
            sink = open(path, 'w')
        return subprocess.Popen(['timeout', '-k', '10', '2700', *cmd], cwd=s['worktree'], env=env, stdout=sink, stderr=subprocess.STDOUT)

    in_runq = bool(os.environ.get('RUNQ_JOB'))
    queue = PerfQueue()
    if not in_runq:
        queue.claim()
    xvfb = None
    try:
        with contextlib.nullcontext() if in_runq else e2e.Budget().hold(s['gb'], f'alloc-compare {os.path.basename(out)}'):
            display = free_display()
            xvfb = subprocess.Popen(
                ['Xvfb', f':{display}', '-screen', '0', '1280x720x24', '-nolisten', 'tcp'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
            )
            time.sleep(2)
            env['DISPLAY'] = f':{display}'
            if a.player == 'ours':
                proc = launch(os.path.join(out, 'warmup.txt'))
                if not wait_for(os.path.join(out, 'warmup.txt'), r'PERF f\d+ fps', proc):
                    raise SystemExit('alloc-compare: the warm-up launch ended before the colony')
                stop(find_pid(comm, home), proc)
            proc = launch(log)
            if not wait_for(log, r'PERF f\d+ fps', proc):
                raise SystemExit('alloc-compare: the player ended before the colony')
            pid = find_pid(comm, home)
            time.sleep(a.warm)
            cpu0, win0 = cpu_sample(pid), windows(log)
            if a.trace:
                with open(os.path.join(out, 'threads.txt'), 'w') as f:
                    for t in os.listdir(f'/proc/{pid}/task'):
                        with open(f'/proc/{pid}/task/{t}/comm') as c:
                            f.write(f'{t} {c.read().strip()}\n')
                tool = shutil.which('dotnet-trace') or os.path.expanduser('~/.dotnet/tools/dotnet-trace')
                duration = time.strftime('%H:%M:%S', time.gmtime(a.secs))
                with open(os.path.join(out, 'trace.txt'), 'w') as f:
                    subprocess.run(
                        [tool, 'collect', '-p', str(pid), '--providers', TRACE_PROVIDERS, '--duration', duration, '-o', os.path.join(out, 'trace.nettrace')],
                        stdout=f,
                        stderr=subprocess.STDOUT,
                        stdin=subprocess.DEVNULL,
                        env=dotnet_env(),
                    )
            else:
                time.sleep(a.secs)
            cpu1, win1 = cpu_sample(pid), windows(log)
            for name, text in (('cpu0', cpu0), ('cpu1', cpu1), ('win0', win0), ('win1', win1)):
                with open(os.path.join(out, name), 'w') as f:
                    f.write(f'{text}\n')
            stop(pid, proc)
    finally:
        if xvfb:
            xvfb.terminate()
        queue.release()
        shutil.rmtree(os.path.join(home, 'cache'), ignore_errors=True)
        shutil.rmtree(os.path.join(home, 'config', 'unity3d', 'Klei', 'Oxygen Not Included', 'cloud_save_files'), ignore_errors=True)
    if a.trace:
        trace = os.path.join(out, 'trace.nettrace')
        if os.path.exists(trace) and os.path.exists(s['trace_tool']):
            subprocess.run([s['trace_tool'], trace, os.path.join(out, 'allocs.tsv')], check=False, env=dotnet_env())
    print(format_rates(out, rates(out)))


# ---- reading runs ----


def parse_alloc(line):
    d = {}
    m = re.search(r'PERF alloc f(\d+) (.*)', line)
    toks = m.group(2).split()
    d['f'] = int(m.group(1))
    i = 0
    while i < len(toks):
        t = toks[i]
        if t.startswith('['):
            k, v = t.split(']=')
            ns, fr = v.split('/')
            d['M' + k[1:]] = int(ns)
            d['MF' + k[1:]] = int(fr)
            i += 1
            continue
        k, v = t, toks[i + 1]
        d[k] = [int(x) for x in v.split('/')] if k == 'dc' else float(v)
        i += 2
    return d


def parse_sites(line):
    body = line.split('PERF sites f', 1)[1].rstrip('\n').split(' ', 1)
    d = {}
    if len(body) > 1 and body[1].strip():
        for part in body[1].strip().split(' | '):
            k, v = part.rsplit('=', 1)
            b, c = v.split('/')
            d[k] = (int(b), int(c))
    return d


def log_of(path):
    p = os.path.join(path, 'player.log')
    return p if os.path.exists(p) else os.path.join(path, 'stdout.txt')


def span(path):
    """The measured span's first and last PERF alloc lines and the PERF sites lines after them."""
    allocs, sites = [], []
    with open(log_of(path), errors='replace') as f:
        for l in f:
            if re.search(r'PERF alloc f\d', l):
                allocs.append(parse_alloc(l))
                sites.append(None)
            elif 'PERF sites f' in l and sites:
                sites[-1] = parse_sites(l)
    w0 = int(open(os.path.join(path, 'win0')).read())
    w1 = int(open(os.path.join(path, 'win1')).read())
    return allocs[w0 - 1], allocs[w1 - 1], sites[w0 - 1], sites[w1 - 1]


def rates(path):
    a, b, _, _ = span(path)
    n, rt, gt = b['n'] - a['n'], b['rt'] - a['rt'], b['gt'] - a['gt']
    if n <= 0 or rt <= 0:
        raise SystemExit(f'alloc-compare: {path} measured no frame-rate window (win0 = win1)')
    mono = b['mono'] - a['mono']
    r = dict(
        frames=n, real=rt, game=gt, fps=n / rt, gps=gt / rt, kb_frame=mono / n / 1000, mb_gs=mono / gt / 1e6, mb_rs=mono / rt / 1e6, gcs=b['gcno'] - a['gcno']
    )
    if b.get('fullms', -1) >= 0:
        r['full_gc_ms'] = b['fullms'] - a['fullms']
    if 'dnpause' in b:
        r['pause_ms'] = b['dnpause'] - a['dnpause']
        r['gen012'] = '/'.join(str(y - x) for x, y in zip(a['dc'], b['dc']))
    try:
        t0, c0, p0 = map(float, open(os.path.join(path, 'cpu0')).read().split())
        t1, c1, p1 = map(float, open(os.path.join(path, 'cpu1')).read().split())
        r['player_cores'] = (p1 - p0) / 100 / (t1 - t0)
        r['other_cores'] = ((c1 - c0) - (p1 - p0)) / 100 / (t1 - t0)
    except (OSError, ValueError):
        pass
    return r


def format_rates(path, r):
    return os.path.basename(path.rstrip('/')) + ': ' + ', '.join(f'{k} {v:.3f}' if isinstance(v, float) else f'{k} {v}' for k, v in r.items())


def sections(path):
    """{section: (bytes, calls)} over the span, with `(other threads)` for what the PERF alloc total
    has beyond the sections (Moono's counter is the main thread's), and the span's rates."""
    a, b, sa, sb = span(path)
    if sa is None or sb is None:
        raise SystemExit(f'alloc-compare: {path} has no PERF sites lines (run with --sites)')
    out = {k: (v[0] - sa.get(k, (0, 0))[0], v[1] - sa.get(k, (0, 0))[1]) for k, v in sb.items()}
    rest = (b['mono'] - a['mono']) - sum(v[0] for v in out.values())
    if rest > 0.005 * (b['mono'] - a['mono']):
        out['(other threads)'] = (rest, 0)
    return out, rates(path)


def group(name):
    """Buckets by kind, as in the doc: every `X.root.*` state bucket of one type is `X states`."""
    if not name.startswith('bucket '):
        return name
    name = name[len('bucket ') :]
    m = re.match(r'([A-Za-z0-9_]+)(\+States)?\.root(\.|$)', name)
    return f'{m.group(1)} states' if m else name


def show_sites(argv):
    path, n = argv[0], int(argv[1]) if len(argv) > 1 else 20
    secs, r = sections(path)
    frames, gt = r['frames'], r['game']
    total = sum(v[0] for v in secs.values())
    print(f'{os.path.basename(path.rstrip("/"))}: {total / gt / 1000:.0f} KB a game s, {total / frames / 1000:.1f} KB a frame')
    for k, (by, calls) in sorted(secs.items(), key=lambda kv: -kv[1][0])[:n]:
        print(f'  {by / gt / 1000:8.1f} KB/gs {by / frames / 1000:7.2f} KB/frame {calls / frames:7.1f} calls/frame  {k}')


def compare(argv):
    orig, ours = argv[0], argv[1]
    n = int(argv[2]) if len(argv) > 2 else 15
    (so, ro), (sm, rm) = sections(orig), sections(ours)
    go, gm = collections.Counter(), collections.Counter()
    for k, v in so.items():
        go[group(k)] += v[0]
    for k, v in sm.items():
        gm[group(k)] += v[0]
    keys = sorted(set(go) | set(gm), key=lambda k: -max(go[k] / ro['game'], gm[k] / rm['game']))[:n]
    print(f'original {ro["frames"] / ro["game"]:.1f} frames a game s, Moono {rm["frames"] / rm["game"]:.1f}')
    print('| Section | Original KB a game s | Moono KB a game s | Original KB a frame | Moono KB a frame |')
    print('|---|---|---|---|---|')
    for k in keys:
        cells = [
            f'{go[k] / ro["game"] / 1000:,.0f}',
            f'{gm[k] / rm["game"] / 1000:,.0f}',
            f'{go[k] / ro["frames"] / 1000:.1f}',
            f'{gm[k] / rm["frames"] / 1000:.1f}',
        ]
        if k == '(other threads)' and not go[k]:
            cells[0], cells[2] = '(in the rows)', ''
        print(f'| {k} | ' + ' | '.join(cells) + ' |')
    to, tm = sum(go.values()), sum(gm.values())
    print(f'| Total | {to / ro["game"] / 1000:,.0f} | {tm / rm["game"] / 1000:,.0f} | {to / ro["frames"] / 1000:.0f} | {tm / rm["frames"] / 1000:.0f} |')


# ---- a --trace run: by thread, section and site ----

SECTIONS = [
    'Game.Update',
    'Game.LateUpdate',
    'Game.UnsafeSim200ms',
    'Global.Update',
    'Global.LateUpdate',
    'KBatchedAnimUpdater.LateUpdate',
    'KAnimBatchManager.Render',
    'AsyncPathProber.TickFrame',
    'Pathfinding.RenderEveryTick',
    'StatusItemRenderer.RenderEveryTick',
    'PrioritizableRenderer.RenderEveryTick',
    'PropertyTextures.LateUpdate',
    'CameraController.Update',
    'CircuitManager.RenderEveryTick',
    'LogicCircuitManager.RenderEveryTick',
    'World.LateUpdate',
    'KScreenManager.Update',
    'SolidConduitFlow.RenderEveryTick',
    'ToolMenu.Update',
    'OverlayScreen.LateUpdate',
    'CanvasUpdateRegistry.PerformUpdate',
    'Game.LateUpdateComponents',
    'KComponentSpawn.RenderEveryTick',
    'BrainScheduler.RenderEveryTick',
    'World.UpdateCellInfo',
    'Pathfinding.UpdateNavGrids',
]
SECTION_RX = [(s, re.compile(r'(^|[.!])' + re.escape(s) + r'(_Patch[0-9]+)?\(')) for s in SECTIONS]
# The engine's own sites in `(other)` and the canvas rebuilds (allocation batch 4's groups), by the
# first two frames from the innermost OniMoono frame that isn't a tweak.
ENGINE_SITES = [
    ('canvas copies (CanvasGeometry.From)', r'CanvasGeometry\.(From|CopyColors)|CanvasArrays.*<- OniMoono.Engine.UI.CanvasGeometry'),
    ('worker mesh channels (Mesh setters)', r'NewChannelAtLeast <- UnityEngine\.Mesh\.'),
    ('Mesh.Clear submeshes', r'MeshNative\.Clear'),
    ('baked positions/colours (BuildMesh)', r'<- OniMoono.Engine.UI.CanvasSystem.BuildMesh|^OniMoono.Engine.UI.CanvasSystem.BuildMesh'),
    ('nested canvas merge (Concatenate)', r'Concatenate'),
    ('willRenderCanvases removals', r'willRenderCanvases'),
    ('render targets (Bind, SetRenderTarget, Of)', r'RenderTargetBinding\.Of|Blits\.Bind|GlDevice\.SetRenderTarget|CameraTarget'),
    ('GetTemporary descriptions', r'RenderTexture\.Describe|FrameRenderer\.ImageEffects <-|RenderTextureNative\]? \(temp\)'),
    (
        'per-frame snapshots and lists',
        r'RenderScreen|RenderCameraCore|CanvasSystem\.Update <-|SendMovesOnPixelPerfect|BehaviourManager\.RunStarts <- OniMoono.Engine.PlayerLoop|'
        r'PhaseList\.Run <- OniMoono.Engine.PlayerLoop|PlayerLoop\.RunPhase <-|Coroutines\.RunPhase|ParticleSystems\.Run|CanvasSystem\.Batched|CanvasSystem\.Collect',
    ),
    ('immediate draws (DrawImmediate)', r'GlDevice\.DrawImmediate'),
]


def module(f):
    return f.split('!', 1)[0]


def method(f):
    return f.split('!', 1)[-1]


def short(f):
    return re.sub(r'\(.*', '', method(f))[-90:]


def bcl(f):
    m = module(f)
    return m.startswith(('System', 'Microsoft', '0Harmony', 'MonoMod')) or m in ('?', '')


def site(frames, depth=2):
    i = 0
    while i < len(frames) and bcl(frames[i]):
        i += 1
    return ' <- '.join(short(f) for f in frames[i : i + depth]) or (short(frames[0]) if frames else '?')


def section_of(frames):
    for f in frames:
        m = method(f)
        if 'AdvanceOneSubTick' in m or ('UpdateBucketWithUpdater' in m and '.Update(' in m):
            return 'buckets'
        for s, rx in SECTION_RX:
            if rx.search(m):
                return s
    return '(other)'


def split(argv):
    path, n = argv[0], int(argv[1]) if len(argv) > 1 else 25
    r = rates(path)
    names = {}
    for name in ('threads.txt',):
        p = os.path.join(path, name)
        if os.path.exists(p):
            for l in open(p):
                parts = l.split(None, 1)
                if len(parts) == 2:
                    names[parts[0]] = parts[1].strip()
    rows = []
    for l in open(os.path.join(path, 'allocs.tsv'), errors='replace'):
        p = l.rstrip('\n').split('\t')
        if len(p) < 6:
            continue
        rows.append((float(p[0]), int(p[1]), p[3], p[4], p[5].split(' <- ') if p[5] else []))
    if not rows:
        raise SystemExit(f'alloc-compare: {path}/allocs.tsv has no samples')
    secs = (rows[-1][0] - rows[0][0]) / 1000
    frames = r['fps'] * secs
    kbf = lambda b: b / frames / 1000
    by_thread = collections.Counter()
    for _, b, _, tid, _ in rows:
        by_thread[tid] += b
    main = max(by_thread, key=lambda k: sum(b for _, b, _, tid, fr in rows if tid == k and any('PlayerLoop' in f for f in fr[-12:])))
    total = sum(by_thread.values())
    print(
        f'{os.path.basename(path.rstrip("/"))}: {secs:.0f} s, {r["fps"]:.1f} fps; sampled {kbf(total):.1f} KB a frame; counter {r["kb_frame"]:.1f} KB a frame'
    )
    print('\nby thread (KB a frame):')
    for tid, b in by_thread.most_common(15):
        print(f'  {kbf(b):7.1f}  {tid:>8} {names.get(tid, "?")}{"  (main)" if tid == main else ""}')
    by_section, rows_of = collections.Counter(), collections.defaultdict(list)
    for row in rows:
        if row[3] == main:
            s = section_of(row[4])
            by_section[s] += row[1]
            rows_of[s].append(row)
    print('\nmain thread by section (KB a frame):')
    for s, b in by_section.most_common(15):
        print(f'  {kbf(b):7.1f}  {s}')

    def table(rs, title, key, limit):
        c, types = collections.Counter(), collections.defaultdict(collections.Counter)
        for _, b, ty, _, fr in rs:
            k = key(fr)
            c[k] += b
            types[k][ty] += b
        print(f'\n{title} ({kbf(sum(c.values())):.1f} KB a frame):')
        for k, b in c.most_common(limit):
            tops = ', '.join(f'{x.split(".")[-1]} {kbf(y):.1f}' for x, y in types[k].most_common(2))
            print(f'  {kbf(b):6.2f}  {k}   [{tops}]')

    owners = collections.Counter()
    for _, b, _, _, fr in rows_of['(other)']:
        first = next((module(f) for f in fr if not bcl(f)), '?')
        owners['the engine\'s own' if first.startswith('OniMoono') and not first.startswith('OniMoono.GameTweaks') else 'game, uGUI and mod code'] += b
    print('\n(other) by owner of its first frame outside the BCL (KB a frame):')
    for k, b in owners.most_common():
        print(f'  {kbf(b):7.1f}  {k}')
    table(rows_of['(other)'], '(other) by site', lambda fr: site(fr, 2), n)
    table(
        rows_of['(other)'], '(other) by owner (first frame outside the BCL)', lambda fr: next((module(f) + '!' + short(f) for f in fr if not bcl(f)), '?'), 40
    )
    table(rows_of['CanvasUpdateRegistry.PerformUpdate'], 'canvas rebuilds by site', lambda fr: site(fr, 3), n)
    table([row for row in rows if row[3] != main], 'other threads by site', lambda fr: site(fr, 2), 30)
    engine = collections.Counter()
    for _, b, ty, tid, fr in rows:
        if tid != main:
            continue
        i = next((i for i, f in enumerate(fr) if module(f).startswith('OniMoono') and not module(f).startswith('OniMoono.GameTweaks')), None)
        j = next((j for j, f in enumerate(fr) if not bcl(f)), None)
        if i is None or i != j:
            continue
        k = ' <- '.join(short(f) for f in fr[i : i + 2])
        for name, rx in ENGINE_SITES:
            if re.search(rx, k):
                if name.startswith('GetTemporary') and 'RenderTextureNative' not in ty:
                    continue
                if name.startswith('per-frame') and 'Lifecycle.Invoke' in k:
                    continue
                engine[name] += b
                break
    print('\nthe engine\'s own sites, main thread (KB a frame):')
    for name, _ in ENGINE_SITES:
        print(f'  {kbf(engine[name]):6.2f}  {name}')


def main():
    if len(sys.argv) < 2 or sys.argv[1] in ('-h', '--help'):
        print(__doc__)
        return
    cmd, argv = sys.argv[1], sys.argv[2:]
    if cmd == 'run':
        run(argv)
    elif cmd == 'rates':
        for p in argv:
            print(format_rates(p, rates(p)))
    elif cmd == 'sites':
        show_sites(argv)
    elif cmd == 'compare':
        compare(argv)
    elif cmd == 'split':
        split(argv)
    else:
        raise SystemExit(f'alloc-compare: unknown command {cmd} (see --help)')


if __name__ == '__main__':
    main()
