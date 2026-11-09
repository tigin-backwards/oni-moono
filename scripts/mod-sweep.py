#!/usr/bin/env python3
"""The load sweep: Workshop mods in batches, each batch in the original Linux player and in ours,
to the main menu and then a colony played for a short while; a failing batch is bisected until
each failure names its mod.
  scripts/mod-sweep.py NAME [--ranks A-B] [--ids ID...] [--batch N] [--seconds S] [--from SAVE]
                       [--first-lane L] [--last-lane L] [--players N] [--download] [--batches I,J-K]
                       [--flagged] [--dry-run]
NAME names the sweep: its results go to tests/e2e/sweeps/NAME.tsv (scripts/modtable.py reads
every file there), its runs and state to out/sweep/NAME/ (E2E_OUT moves out/sweep). A sweep that
is run again goes on where it stopped: a batch whose state file exists is not run again (delete
out/sweep/NAME/batch-NN.json to rerun one). The mods are the catalog's (scripts/mod-catalog.tsv,
then scripts/mod-catalog-u60.tsv) ranks A-B (default all), or the IDS given, less those it flags
notLoading, crashesAtStartup, brokenByUpdate or unanalysed (unless --flagged, a sweep that records
how they fail in the original), and never one onimaxxing's catalog.json (ONI_CATALOG_JSON) flags
as malware.
Translations (a .po and no assembly) are recorded and not batched: the players install them
disabled, as a subscription leaves them. --download fetches with SteamCMD (scripts/ws-mods.py)
the mods out/ws lacks; a mod that is still missing is recorded as not downloadable. A mod the
catalog says requires others is installed with them. A mod is batched only when it and the mods
it requires pass the mod gate (scripts/mod-gate.py); one it skips is recorded as not run, with
the gate's reason.

Each run is scripts/e2e.py's (its Run, lanes, players' budget and perf lock): a spec with the
batch as its mods, `from: SAVE` (default 2minnow, tests/e2e/fixtures/2minnow.sav, the Colony
sweeps' save) and one step, `run: {seconds: S}` (default 90). Lanes FIRST-LAST (default 22-25),
at most --players (default 2) players at once. Per batch:
  1. the original and ours run the batch side by side. Before the sweep's first batch both run
     the save without mods (the baseline): signatures ours logs there and the original does not
     are the engine's, not a mod's;
  2. a mod the original disables ("Failed to load mod TITLE...disabling") fails in the original;
     when the original's run fails (it exits, the save does not load, the run step fails), the
     batch is bisected in the original until each failing mod is named; those fail in the
     original, and the rest of the batch runs again in both players;
  3. ours fails where its run fails, where it disables a mod the original loaded, or where it
     logs a signature (qa-tools.py signatures) neither the original's batch run nor the baseline
     has; it is then bisected in ours, its halves compared with the same original run, until each
     failing mod is named, with the signatures it logs alone. A mod so named runs alone in the
     original too: when it fails there as well (it loaded in its batch only through another mod
     of it, a library it does not ship), it fails in the original, not in ours. Halves that both
     pass are a failure of the mods together (an interaction, recorded for each of them) or a
     flaky one: the batch is run once more and a failure that does not come back is noted, not
     counted.
A bisection first splits off the mods whose own assemblies the failing run's errors name (its
frames), else it halves the set. A failure's stage is the main menu when the player had not
begun loading the save ("Loading save
file:" in its log), else the colony.

tests/e2e/sweeps/NAME.tsv: comment lines (#), then a header and one line per mod: id, rank,
original, ours, note. original and ours are ok, menu (fails by or at the main menu), colony (the
main menu is fine; loading or playing the colony fails) or - (not run: a translation, a mod that
cannot be downloaded, one the mod gate skips, or ours after the original failed). The note
gives the error.
"""

import argparse, concurrent.futures, contextlib, io, json, os, re, shutil, sys, tempfile, threading, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SWEEP_OUT = os.environ.get('E2E_OUT') or os.path.join(ROOT, 'out', 'sweep')
os.environ.setdefault('E2E_OUT', SWEEP_OUT)  # e2e.py's run folders, set before it is loaded
RESULTS = os.path.join(ROOT, 'tests', 'e2e', 'sweeps')
CATALOG = os.path.join(ROOT, 'scripts', 'mod-catalog.tsv')
CATALOG_U60 = os.path.join(ROOT, 'scripts', 'mod-catalog-u60.tsv')
CATALOG_JSON = os.environ.get('ONI_CATALOG_JSON') or os.path.expanduser('~/projects/onimaxxing/apps/onimaxxing/.mods-data/public/catalog.json')
SKIPPED_FLAGS = ('notLoading', 'crashesAtStartup', 'brokenByUpdate', 'unanalysed')
LOAD_MARK = 'Loading save file:'
GL_REFUSED = re.compile(r'failed to create drisw screen|X_GLXCreateContext|VersionUnavailable')
DISABLED = re.compile(r'Failed to load mod (.*?)\.\.\.disabling')


def load_e2e():
    import importlib.util

    spec = importlib.util.spec_from_file_location('e2e', os.path.join(ROOT, 'scripts', 'e2e.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


e2e = load_e2e()
qa_tools = e2e.load_script('qa-tools.py')
ws = e2e.ws_mods()
say_lock = threading.Lock()
OWN = {}  # {mod id: its own assemblies' names}


def say(text):
    with say_lock:
        print(time.strftime('%H:%M:%S ') + text, flush=True)


# ---------------------------------------------------------------- the mods


def read_catalog(paths=(CATALOG, CATALOG_U60)):
    """The catalog's rows, then those of the U60 catalog it lacks (scripts/mod-catalog-u60.tsv:
    their U60 ranks, which may repeat the main TSV's)."""
    rows, seen = [], set()
    for path in paths:
        if not os.path.exists(path):
            continue
        for line in open(path, encoding='utf-8'):
            rank, mid, subs, flags, title = (line.rstrip('\n').split('\t') + [''] * 5)[:5]
            if mid not in seen:
                seen.add(mid)
                rows.append({'rank': int(rank), 'id': mid, 'flags': flags, 'title': title})
    return rows


def read_catalog_json(path=CATALOG_JSON):
    """{id: {requires, malware}} from onimaxxing's catalog.json (format 2), or {} without it."""
    if not os.path.exists(path):
        return {}
    data = json.load(open(path, encoding='utf-8'))
    cols = data['cols']
    return {
        cols['id'][i]: {'requires': list(cols['requires'][i] or []), 'malware': bool((cols['analysis'][i] or {}).get('malware'))} for i in range(data['count'])
    }


def downloaded(mid):
    return os.path.isdir(os.path.join(ws.WS, mid)) or os.path.isdir(os.path.join(ws.CONTENT, mid))


def mod_folder(mid):
    """The mod's unpacked folder in out/ws (unpacked now if SteamCMD has it), or None."""
    return ws.unpack(mid) if downloaded(mid) else None


def titles(folder, mid):
    """The names KMod may give a mod in its log: its mod.yaml title, else its folder (the id)."""
    yaml = os.path.join(folder, 'mod.yaml') if folder else ''
    text = open(yaml, encoding='utf-8-sig', errors='replace').read() if yaml and os.path.exists(yaml) else ''
    return {ws.yaml_value(text, 'title') or mid, mid}


# ---------------------------------------------------------------- runs


class Runner:
    """Runs of e2e.py's Run, at most PLAYERS at once, each on a lane of its own."""

    def __init__(self, name, env, save, seconds, first, last, players):
        self.name, self.env, self.save, self.seconds = name, env, save, seconds
        self.first, self.last = first, last
        self.pool = concurrent.futures.ThreadPoolExecutor(max_workers=players)
        self.budget = e2e.Budget()
        self.run_id = e2e.new_run_id()
        self.count = 0
        self.lock = threading.Lock()
        self.requires = {}

    def install_set(self, mods):
        """MODS and what they require (catalog.json), in order, without duplicates."""
        out = []
        for m in mods:
            out += [r for r in self.requires.get(m, []) if downloaded(r)] + [m]
        return list(dict.fromkeys(out))

    def play(self, kind, mods, tag):
        """One run: {kind, mods, outcome, signatures, menu_signatures, loading, disabled, out}."""
        with self.lock:
            self.count += 1
            label = f'{tag}-{self.count:04d}'
        spec = {
            'name': f'{self.name}-{label}',
            'from': self.save,
            'mods': self.install_set(mods),
            'setup': [],
            'steps': [{'run': {'seconds': self.seconds}}],
            'path': f'sweep/{self.name}',
        }
        log = lambda s: say(f'[{label} {kind}] {s}')
        lane = e2e.allocate_lane(self.first, last=self.last, log=log)
        try:
            log(f'{len(mods)} mod(s) on lane {lane.number}')
            with e2e.machine_lock(log=log), self.budget.hold(e2e.player_gb(spec, e2e.fixture_of(spec)), f'sweep {label} {kind} lane {lane.number}', log):
                result = e2e.Run(spec, kind, 1, self.env, lane, log, run_id=self.run_id).execute()
        finally:
            lane.release()
        if (
            result['outcome']
            and re.search(r'is off screen|is occluded by|no OK button', result['outcome'])
            and not re.search(r'CrashReportScreen|ErrorCanvas', result['outcome'])
        ):
            # The runner could not get past a dialog: no verdict on the mods; the sweep stops. A
            # crash screen over the dialog is the game's failure, which counts.
            raise RuntimeError(f'{label} {kind}: the runner failed, not the mods: {result["outcome"]}')
        if result.get('out'):
            # The run's profile (the mods, the save) is large; its logs and screenshot stay.
            shutil.rmtree(os.path.join(result['out'], 'home'), ignore_errors=True)
        text = open(result['log'], encoding='utf-8', errors='replace').read() if result.get('log') and os.path.exists(result['log']) else ''
        if result['outcome'] and GL_REFUSED.search(text):
            # The machine's GL driver refused the player a context (too many players hold one):
            # no verdict on the mods; the sweep stops, and the batch runs again when resumed.
            raise RuntimeError(f'{label} {kind}: the GL driver refused a context, not the mods: {result["outcome"]}')
        mark = text.find(LOAD_MARK)
        found = {
            'kind': kind,
            'mods': list(mods),
            'outcome': result['outcome'],
            'out': result['out'],
            'signatures': sorted(result['signatures']),
            'loading': mark >= 0,
            'menu_signatures': sorted(signatures_of(text[:mark] if mark >= 0 else text)),
            'disabled': sorted(set(DISABLED.findall(text))),
            'errors': error_text(text),
        }
        log(
            f'{"ok" if not found["outcome"] else found["outcome"][:200]}; {len(found["signatures"])} signature(s)'
            + (f', disabled: {", ".join(found["disabled"])}' if found['disabled'] else '')
        )
        return found

    def many(self, jobs):
        """Several (kind, mods, tag) runs at once (up to the pool's players): their results, in order."""
        futures = [self.pool.submit(self.play, *job) for job in jobs]
        return [f.result() for f in futures]


def signatures_of(text):
    with tempfile.NamedTemporaryFile('w', suffix='.log', delete=False, encoding='utf-8') as f:
        f.write(text)
        path = f.name
    try:
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            qa_tools.signatures(path)
        return set(printed.getvalue().splitlines())
    finally:
        os.remove(path)


def stage(run):
    return 'colony' if run['loading'] else 'menu'


# ---------------------------------------------------------------- judging


class Judge:
    """Whether a run of ours fails against the original's run of a batch and the baseline."""

    def __init__(self, original, baseline_ours, names):
        self.reference = set(original['signatures']) | set(baseline_ours)
        self.original_disabled = set(original['disabled'])
        self.names = names  # {id: names KMod may log for it}

    def new(self, run):
        return qa_tools.unknown(run['signatures'], self.reference)

    def disabled(self, run):
        """The mods of RUN ours disabled and the original did not."""
        extra = set(run['disabled']) - self.original_disabled
        return [m for m in run['mods'] if self.names.get(m, {m}) & extra]

    def fails(self, run):
        return bool(run['outcome'] or self.new(run) or self.disabled(run))

    def why(self, run):
        parts = []
        if run['outcome']:
            parts.append(run['outcome'])
        if self.disabled(run):
            parts.append('ours disables it ("Failed to load mod ...disabling")')
        new = self.new(run)
        if new:
            parts.append('; '.join(new[:3]) + (f' (and {len(new) - 3} more)' if len(new) > 3 else ''))
        return '; '.join(parts)


def original_fails(run):
    return bool(run['outcome'])


LIBRARIES = re.compile(r'^(0Harmony|PLib.*|Newtonsoft\.Json|System\..*|Mono\..*|Microsoft\..*|UnityEngine.*)$', re.I)


def assemblies(folder):
    """The names of a mod's own assemblies (its DLLs, less the libraries mods ship)."""
    names = set()
    for _, _, files in os.walk(folder):
        names |= {f[:-4] for f in files if f.lower().endswith('.dll') and not LIBRARIES.match(f[:-4])}
    return {n for n in names if len(n) >= 4}


def error_text(text):
    """A log's error lines with the frames after each, where a failing mod names itself."""
    lines = text.splitlines()
    keep = []
    for i, line in enumerate(lines):
        if re.search(r'\b\w+(Exception|Error)\b|\[ERROR\]|Failed to patch', line):
            keep += lines[i : i + 9]
    return '\n'.join(keep)


def suspects(run, mods, own):
    """The mods of MODS whose assemblies (OWN: {id: names}) the run's errors name."""
    text = run.get('errors', '')
    return [m for m in mods if any(re.search(rf'(?<![\w.]){re.escape(n)}[.:]', text) for n in own.get(m, ()))]


def bisect(runner, kind, mods, fails, tag, first=None):
    """The mods of a failing set MODS whose failure (FAILS(run)) comes from them alone, as
    [(mods, run)]: one mod each, or the whole set when its halves pass (an interaction). FIRST,
    the mods the failure's errors name, are split from the rest first, a shorter way to them."""
    if len(mods) == 1:
        return None  # the caller's run of it is the evidence
    half = len(mods) // 2
    parts = [mods[:half], mods[half:]]
    if first and 0 < len(first) < len(mods):
        parts = [list(first), [m for m in mods if m not in first]]
    runs = runner.many([(kind, part, tag) for part in parts])
    found = []
    for part, run in zip(parts, runs):
        if not fails(run):
            continue
        deeper = bisect(runner, kind, part, fails, tag, suspects(run, part, OWN))
        found += deeper if deeper is not None else [(part, run)]
    return found or [(mods, None)]


# ---------------------------------------------------------------- a batch


def original_why(run, baseline):
    """A failing original run's outcome and the first signatures the baseline lacks."""
    new = qa_tools.unknown(run['signatures'], baseline)
    return '; '.join(p for p in [run['outcome']] + new[:2] if p)[:500]


def sweep_batch(runner, number, batch, baseline, names):
    """{id: (original, ours, note)} for a batch of mods; BASELINE is the runs without mods."""
    tag = f'b{number:02d}'
    verdicts = {}
    mods = list(batch)
    original, ours = runner.many([('original', mods, tag), ('ours', mods, tag)])
    for _ in range(4):
        # Mods KMod disables in the original fail there, whatever else happens.
        disabled = [m for m in mods if names[m] & set(original['disabled'])]
        for m in disabled:
            verdicts[m] = ('menu', '-', 'the original disables it ("Failed to load mod ...disabling"): ' + original_why(original, baseline['original']))
        if not original_fails(original):
            break
        if disabled:
            # The mods it disabled are the likeliest cause: the rest runs again without them first.
            say(f'{tag}: the original fails and disables {" ".join(disabled)}; the rest again without them')
            mods = [m for m in mods if m not in verdicts]
            if not mods:
                return verdicts
            original, ours = runner.many([('original', mods, tag), ('ours', mods, tag)])
            continue
        say(f'{tag}: the original fails ({original["outcome"][:160]}); bisecting in the original')
        found = bisect(runner, 'original', mods, original_fails, tag, suspects(original, mods, OWN)) or [(mods, original)]
        named = [(part, run) for part, run in found if len(part) == 1]
        for part, run in found:
            run = run or original
            why = original_why(run, baseline['original'])
            if len(part) == 1:
                verdicts[part[0]] = (stage(run), '-', why)
        if not named:
            for m in mods:
                verdicts.setdefault(m, (stage(original), '-', f'fails in the original only with its batch: {why}'))
            return verdicts
        mods = [m for m in mods if m not in verdicts]
        if not mods:
            return verdicts
        original, ours = runner.many([('original', mods, tag), ('ours', mods, tag)])
    else:
        for m in mods:
            verdicts.setdefault(m, (stage(original), '-', f'the original keeps failing: {original["outcome"][:300]}'))
        return verdicts
    judge = Judge(original, baseline['ours'], names)
    for attempt in range(2):
        if not judge.fails(ours):
            break
        say(f'{tag}: ours fails ({judge.why(ours)[:200]}); bisecting in ours')
        left = [m for m in mods if m not in verdicts]
        found = bisect(runner, 'ours', left, judge.fails, tag, suspects(ours, left, OWN)) or [(left, ours)]
        if all(run is None for _, run in found) and attempt == 0:
            say(f'{tag}: no half fails alone; the batch once more')
            ours = runner.many([('ours', [m for m in mods if m not in verdicts], tag)])[0]
            if not judge.fails(ours):
                say(f'{tag}: ours passes the second time; the first failure was flaky')
                break
            continue
        # A mod that fails in ours alone runs alone in the original too: one that loaded in its
        # batch only through another mod of it (a library it does not ship) fails there as well.
        named = [part for part, run in found if run is not None]
        alone = dict(zip([p[0] for p in named], runner.many([('original', part, tag) for part in named]))) if named else {}
        for part, run in found:
            if run is None:
                for m in part:
                    verdicts.setdefault(
                        m, ('ok', stage(ours), 'fails in ours only together with ' + ', '.join(x for x in part if x != m) + ': ' + judge.why(ours)[:400])
                    )
                continue
            original_alone = alone[part[0]]
            if original_fails(original_alone) or names[part[0]] & set(original_alone['disabled']):
                verdicts[part[0]] = (
                    stage(original_alone),
                    '-',
                    'fails alone in the original too (it loads only with another mod of its batch): ' + original_why(original_alone, baseline['original']),
                )
            else:
                verdicts[part[0]] = ('ok', stage(run), judge.why(run)[:600])
        break
    for m in mods:
        verdicts.setdefault(m, ('ok', 'ok', ''))
    return verdicts


# ---------------------------------------------------------------- the sweep


def parse_args(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('name')
    p.add_argument('--ranks', help='A-B: the catalog ranks to sweep')
    p.add_argument('--ids', nargs='+', help='Workshop IDs to sweep instead of ranks')
    p.add_argument('--batch', type=int, default=22)
    p.add_argument('--seconds', type=int, default=90)
    p.add_argument('--from', dest='save', default='2minnow')
    p.add_argument('--first-lane', type=int, default=22)
    p.add_argument('--last-lane', type=int, default=25)
    p.add_argument('--players', type=int, default=2)
    p.add_argument('--screen', default='1280x1024', help="the players' screen: tall enough for the MOD DIFFERENCES dialog of a whole batch")
    p.add_argument('--download', action='store_true')
    p.add_argument('--flagged', action='store_true', help='sweep the mods flagged notLoading, crashesAtStartup or unanalysed too (never malware)')
    p.add_argument('--batches', help='only these batch numbers: 0,3,5-7')
    p.add_argument('--dry-run', action='store_true', help='print the batches and stop')
    return p.parse_args(argv)


def numbers(text):
    out = set()
    for part in text.split(','):
        a, _, b = part.partition('-')
        out.update(range(int(a), int(b or a) + 1))
    return out


def plain(note):
    """A note in a player's terms: the crash screen covering the load's dialog, as e2e.py words
    it for a click, is the crash screen opening."""
    note = re.sub(r'step start: \S+ is occluded by ErrorCanvas/CrashReportScreen[^;]*\)', 'the crash screen opens while the save loads', note)
    note = re.sub(r'(^|; )\S+ ~~~!', r'\1', note)  # KCrashReporter's "OBJECT ~~~!" prefix
    return re.sub(r'^step start: the player exited \((-?\d+)\) while loading', r'the player exits (\1) while loading', note)


def write_results(path, name, opts, ranks, state_dir):
    rows = []
    for f in sorted(os.listdir(state_dir)):
        if re.fullmatch(r'(batch-\d+|skipped)\.json', f):
            rows += json.load(open(os.path.join(state_dir, f)))['mods']
    rows.sort(key=lambda r: (ranks.get(r['id'], 1 << 30), r['id']))
    commit = e2e.head_commit()
    with open(path, 'w', encoding='utf-8') as f:
        f.write(
            f'# scripts/mod-sweep.py {name}: {len(rows)} mods, batches of {opts.batch}, the save {opts.save}, '
            f'{opts.seconds} s of play; commit {commit}, {time.strftime("%Y-%m-%d")}\n'
        )
        f.write('id\trank\toriginal\tours\tnote\n')
        for r in rows:
            note = re.sub(r'\s+', ' ', plain(r['note'])).strip()
            f.write(f"{r['id']}\t{ranks.get(r['id'], '')}\t{r['original']}\t{r['ours']}\t{note}\n")


def main(argv):
    opts = parse_args(argv)
    catalog = read_catalog()
    ranks = {r['id']: r['rank'] for r in catalog}
    info = read_catalog_json()
    if opts.ids:
        chosen = [r for r in catalog if r['id'] in set(opts.ids)] + [{'rank': 0, 'id': i, 'flags': '', 'title': i} for i in opts.ids if i not in ranks]
    else:
        a, _, b = (opts.ranks or '1-999999').partition('-')
        chosen = [r for r in catalog if int(a) <= r['rank'] <= int(b or a)]
    if not opts.flagged:
        chosen = [r for r in chosen if r['flags'] not in SKIPPED_FLAGS]
    malware = [r['id'] for r in chosen if info.get(r['id'], {}).get('malware')]
    chosen = [r for r in chosen if r['id'] not in malware]
    if malware:
        say(f'left out as malware: {" ".join(malware)}')
    if opts.download:
        need = [r['id'] for r in chosen if not downloaded(r['id'])]
        need += [q for r in chosen for q in info.get(r['id'], {}).get('requires', []) if q in info and not info[q]['malware'] and not downloaded(q)]
        need = list(dict.fromkeys(need))
        for i in range(0, len(need), 40):
            say(f'downloading {len(need[i : i + 40])} item(s) ({i + 1}-{i + len(need[i : i + 40])} of {len(need)})')
            ws.download(need[i : i + 40])
    state_dir = os.path.join(SWEEP_OUT, opts.name)
    os.makedirs(state_dir, exist_ok=True)
    skipped, batchable, names = [], [], {}
    for r in chosen:
        folder = mod_folder(r['id'])
        if folder is None:
            skipped.append({'id': r['id'], 'original': '-', 'ours': '-', 'note': 'not downloadable with SteamCMD'})
        elif e2e.translation_only(folder):
            skipped.append({'id': r['id'], 'original': '-', 'ours': '-', 'note': 'a translation: installed disabled, as a subscription leaves it; not swept'})
        else:
            batchable.append(r['id'])
            names[r['id']] = titles(folder, r['id'])
            OWN[r['id']] = assemblies(folder)
    # The mod gate (scripts/mod-gate.py) on every mod a batch installs, those required among
    # them: a mod it skips, or one that requires a mod it skips, is not batched.
    requires = {m: [q for q in info.get(m, {}).get('requires', []) if q in info and downloaded(q)] for m in batchable}
    for q in {q for m in batchable for q in requires[m]}:
        ws.unpack(q)
    verdicts = ws.gate(list(dict.fromkeys(batchable + [q for m in batchable for q in requires[m]])))
    for m in list(batchable):
        why = [f'{q}: {verdicts[q]["reason"]}' for q in [m] + requires[m] if verdicts[q]['verdict'] != 'pass']
        if why:
            batchable.remove(m)
            skipped.append({'id': m, 'original': '-', 'ours': '-', 'note': 'skipped by the mod gate: ' + '; '.join(why)})
            say(f'{m}: skipped by the mod gate: {"; ".join(why)}')
    json.dump({'mods': skipped}, open(os.path.join(state_dir, 'skipped.json'), 'w'), indent=1)
    batches = [batchable[i : i + opts.batch] for i in range(0, len(batchable), opts.batch)]
    say(f'{opts.name}: {len(batchable)} mods in {len(batches)} batches; {len(skipped)} not batched')
    if opts.dry_run:
        for n, b in enumerate(batches):
            print(f'batch {n:02d}: ranks {ranks.get(b[0])}-{ranks.get(b[-1])}: {" ".join(b)}')
        return 0
    env = e2e.environment()
    missing = [n for n in ('QA_U57_GAME', 'QA_U57_STEAM', 'E2E_ONIMCP', 'E2E_ORIGINAL') if not env.get(n)]
    if missing:
        sys.exit('mod-sweep: needs ' + ', '.join(missing) + ' (out/local-tools/qa.env)')
    e2e.ensure_fixtures([{'from': opts.save}])
    if not os.path.exists(os.path.join(e2e.FIXTURES, opts.save + '.sav')):
        sys.exit(f'mod-sweep: no tests/e2e/fixtures/{opts.save}.sav')
    # A save loaded with other mods than it was saved with opens MOD DIFFERENCES, listing them;
    # with a batch's mods its OK button falls below e2e.py's 1280x720 screen.
    e2e.WIDTH, e2e.HEIGHT = (int(v) for v in opts.screen.split('x'))
    # Its players and Xvfbs end with it, however it ends (e2e.py's "leftovers").
    os.environ.pop(e2e.OWNER_ENV, None)
    e2e.install_teardown()
    e2e.start_reaper()
    runner = Runner(opts.name, env, opts.save, opts.seconds, opts.first_lane, opts.last_lane, opts.players)
    runner.requires = {r['id']: [q for q in info.get(r['id'], {}).get('requires', []) if q in info and not info[q]['malware']] for r in chosen}
    baseline_path = os.path.join(state_dir, 'baseline.json')
    if os.path.exists(baseline_path):
        baseline = json.load(open(baseline_path))
    else:
        original, ours = runner.many([('original', [], 'base'), ('ours', [], 'base')])
        if original['outcome'] or ours['outcome']:
            sys.exit(f'mod-sweep: the save does not play without mods: {original["outcome"] or ours["outcome"]}')
        baseline = {'original': original['signatures'], 'ours': ours['signatures'], 'engine': qa_tools.unknown(ours['signatures'], original['signatures'])}
        json.dump(baseline, open(baseline_path, 'w'), indent=1)
        say(f'baseline: {len(baseline["engine"])} signature(s) ours logs without mods and the original does not')
    results = os.path.join(RESULTS, opts.name + '.tsv')
    os.makedirs(RESULTS, exist_ok=True)
    wanted = numbers(opts.batches) if opts.batches else None
    for n, batch in enumerate(batches):
        path = os.path.join(state_dir, f'batch-{n:02d}.json')
        if os.path.exists(path) or (wanted is not None and n not in wanted):
            continue
        say(f'== batch {n:02d} of {len(batches)}: ranks {ranks.get(batch[0])}-{ranks.get(batch[-1])}')
        started = time.time()
        verdicts = sweep_batch(runner, n, batch, baseline, names)
        rows = [{'id': m, 'original': v[0], 'ours': v[1], 'note': v[2]} for m, v in ((m, verdicts[m]) for m in batch)]
        json.dump({'batch': batch, 'mods': rows, 'minutes': round((time.time() - started) / 60, 1)}, open(path, 'w'), indent=1)
        bad = [r for r in rows if r['original'] != 'ok' or r['ours'] != 'ok']
        say(
            f'== batch {n:02d}: {len(rows) - len(bad)} ok, '
            + ', '.join(f'{r["id"]} {r["original"]}/{r["ours"]}' for r in bad)
            + f' ({(time.time() - started) / 60:.0f} min)'
        )
        write_results(results, opts.name, opts, ranks, state_dir)
    write_results(results, opts.name, opts, ranks, state_dir)
    say(f'results in {os.path.relpath(results, ROOT)}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
