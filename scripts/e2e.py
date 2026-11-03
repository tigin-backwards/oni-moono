#!/usr/bin/env python3
"""End-to-end specs: a colony checkpoint, played through OniMcp and E2eDriver, checked by its outcome.
  scripts/e2e.py [--player ours|original|both] [--repeat N] [--bless] [--lanes N] [--first-lane L]
                 [--last-lane L] [--displays :88,:89 | --xvfb-run] [--no-perf-wait] [--max-wait MIN]
                 [--results PATH] [--run-id ID] [--record [--note TEXT]] [--hold | --hold-on-fail]
                 SPEC...
  scripts/e2e.py --check SPEC...             parse and validate specs (and known-findings.yaml)
  scripts/e2e.py --status                    the machine's lanes, players and perf jobs, who waits on
                                             what, and what dead runners left (look here first when a
                                             run waits)
  scripts/e2e.py --clean                     clean what dead runners left on lanes no runner holds
A spec (tests/e2e/NAME.yaml; tests/e2e/README.md describes every step) starts from a fixture save
(tests/e2e/fixtures), installs Workshop mods, gives orders through OniMcp's tools (an in-game MCP
server), drives the UI with real X11 input (XTEST) located through E2eDriver (our test mod, a
JSON-lines server in the game), runs the game and checks what came of it. The simulation is not
deterministic, so a spec checks outcomes and invariants, never a trajectory, and --repeat runs it
several times. --player both runs it in the original Unity player first: a spec passes there
first, and then our player is compared with it (docs/research/mod-play-testing.md §5): new log
signatures, required log lines, UI dumps (ui-diff.py), cropped and masked screenshots
(qa-tools.py image) and values a spec compares with the original's. --bless copies an original
run's saves into tests/e2e/fixtures and its evidence into out/e2e-refs/SPEC, which a run of ours
alone compares against.

Runners in every worktree meet in one lock directory (E2E_LOCK_DIR, default
~/.cache/oni-brutal-e2e). Lanes are machine-wide: --lanes N runs specs from a queue on N lanes at
once, taken from --first-lane (1) to --last-lane (19). Lane L's OniMcp listens on 8800 + 10 L,
its E2eDriver on 8801 + 10 L (E2E_DRIVER_PORT), and its display is an Xvfb on :(300 + L)
(--xvfb-run: xvfb-run -a; --displays: those). A lane holds lane-L.lock there, and is skipped
while its ports or display are in use or reserved (the directory's `reserved` file and
E2E_RESERVED). Each player is counted for its memory in the machine's players' budget
(E2E_MEMORY_GB, default 40; E2E_MAX_PLAYERS, default 4). A run holds perf.lock shared, so a perf
job taking it exclusively (`flock -x -o ~/.cache/oni-brutal-e2e/perf.lock CMD`) keeps specs from
starting; specs also wait while ps shows a perf job. Each wait says what it waits for, and
--max-wait (or E2E_MAX_WAIT) fails the run after that many minutes. A run's players and Xvfbs end
with it however it ends (a watcher, e2e.py --reap-after, sees to a kill -9), and a runner taking a
lane whose last holder died first cleans what that holder left (see "leftovers" below).

Needs, in the environment or out/local-tools/qa.env: QA_U57_GAME and QA_U57_STEAM (the DLC
content and a Steam double owning it, as for qa.sh's U57 stages), E2E_ONIMCP (a build of
OniMcp's mod folder at the commit scripts/onimcp.py pins and builds; its licence does not
allow vendoring it), E2E_DRIVER (E2eDriver's mod
folder; default out/bin/E2eDriver/release) for specs that use it and, for --player original or
both, E2E_ORIGINAL (the original player's executable, with a DLC-owning Steam double in its
Plugins); E2E_ORIGINAL_ENV holds NAME=VALUE settings for the original player only (on WSL,
`GALLIUM_DRIVER=llvmpipe` with a shared MESA_SHADER_CACHE_DIR runs it 3-4 times faster; see
tests/e2e/README.md, "Run time"). Our player runs with `--game-tweak exact` (the users' defaults but path-probe-repair), or with
E2E_GAME_TWEAKS, space-separated `--game-tweak` values (`none`, `background-save`, `exact
-no-save-gc`); E2E_PLAYER_ARGS adds host arguments (`--renderer vulkan`), E2E_ORIGINAL_ARGS the
original's (`-force-glcore`). E2E_SCREEN=WxH and E2E_UI_SCALE=PERCENT run both players at another
screen size and UI scale, for a look at a layout there (comparisons with references want the
default 1280x720 at 100). E2E_CLOUD_SAVES=DIR gives each player's profile DIR's tree as its
cloud saves, hard-linked (load-screen-many-saves.yaml; never a real saves folder). Each invocation
has a run id; each run's log, results and evidence are in
out/e2e/SPEC/RUN_ID/PLAYER-N, and out/e2e/runs/RUN_ID.json holds one record per spec run.
--record writes tests/e2e/results/SPEC.json, the committed record of the spec's verdict (played,
warning, failed or pending), which scripts/modtable.py reads. ONI_RESULTS_DIR (absolute, or
relative to the checkout) writes the records elsewhere: tests/e2e/results-u60 holds those played
on another game update, beside the main ones, and modtable.py reads both. ui_dump differences
that tests/e2e/known-findings.yaml explains make a run of ours a warning, not a failure.

--hold keeps each player running after its steps (--hold-on-fail: after a failing step) until
the hold file in its run folder is removed, for a look through OniMcp and E2eDriver while
writing a spec; a QA perf stage waiting to start ends it 10 minutes later.
"""
import contextlib, ctypes, fcntl, glob, importlib.util, itertools, json, os, re, shutil, signal, socket, subprocess, sys
import threading, time, urllib.error, urllib.request, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPECS = os.path.join(ROOT, 'tests', 'e2e')
FIXTURES = os.path.join(SPECS, 'fixtures')
# A worktree's runs and references (E2E_OUT moves both: references go beside it, in E2E_OUT-refs).
OUT = os.environ.get('E2E_OUT') or os.path.join(ROOT, 'out', 'e2e')
REFS = OUT.rstrip('/') + '-refs'
DRIVER_SOURCE = os.path.join(ROOT, 'tools', 'E2eDriver')  # its mod.yaml and mod_info.yaml
# E2E_SCREEN=WxH: another screen size, for a look at a layout there (references are 1280x720).
WIDTH, HEIGHT = map(int, os.environ.get('E2E_SCREEN', '1280x720').split('x'))
# E2E_UI_SCALE=PERCENT: the game's UI scale option (Options > Graphics), 100 by default.
UI_SCALE = os.environ.get('E2E_UI_SCALE')
PROFILE = ('config', 'unity3d', 'Klei', 'Oxygen Not Included')
# Unity's Mono has ValueTuple in mscorlib but no System.ValueTuple facade, which the
# System.Text.Json merged into OniMcp references: the original gets the package's forwarder.
VALUETUPLE = os.path.expanduser('~/.nuget/packages/system.valuetuple/4.6.1/lib/net47/System.ValueTuple.dll')
# OniMcp's Workshop item: a spec naming it plays it as the runner's OniMcp (its unpacked, gated
# Workshop build in place of E2E_ONIMCP), since two copies would share a staticID and a port.
ONIMCP_WORKSHOP = '3807166922'
MAX_LANES = 6          # the memory budget's hard maximum (§6.2)
SHOT_THRESHOLD = 0.02  # fraction of pixels differing (§5.3), set per step after calibration
WORLD_THRESHOLD = 0.05
UI_TOLERANCE = 2       # pixels, for ui-diff.py --tolerance
# ui_dump members every diff leaves out: a Graphic's depth (the order of graphics on its canvas)
# is numbered differently in our player, whatever is drawn (tests/e2e/README.md, Verdicts).
IGNORE_FIELDS = ('depth',)
FRAME_FALLBACK = 0.1   # seconds a "frame" lasts without E2eDriver to count real ones
BUILD_MENU_FRAMES = 600  # the most frames a shot or ui_dump waits for the build menu to settle
# Perf jobs a spec never starts beside (matched against `ps -eo args`): the sweep and measurement
# scripts, profilers. QA (scripts/qa.sh) is not one: only its perf stage needs a quiet machine,
# and it says so through perf.wanted and an exclusive hold of perf.lock (machine_lock); its other
# stages run beside specs, counted in the players' budget as one QA_GB player (Budget).
QA_JOBS = [r'(^|/)scripts/qa\.sh\b']
QA_GB = 15          # E2E_QA_GB: QA's verify stage runs U57 (0: not counted)
PERF_JOBS = [r'(^|/)scripts/flamegraph\.py\b',
             r'out/local-tools/(sweep|measure|ft-bench|compare-queued|minnow-prof|glut-prof|queue|orig-queue|tw-run)',
             r'^perf (record|stat)\b']


class SpecFailure(Exception):
    pass


class SpecError(Exception):
    """A spec that does not parse or validate."""


printing = threading.Lock()


def say(*parts):
    with printing:
        print(*parts, flush=True)


def environment():
    """The settings from the environment, else from out/local-tools/qa.env (a bash file)."""
    names = ['QA_U57_GAME', 'QA_U57_STEAM', 'E2E_ONIMCP', 'E2E_ORIGINAL', 'E2E_DRIVER', 'GALLIUM_DRIVER',
             'MESA_D3D12_DEFAULT_ADAPTER_NAME', 'E2E_ORIGINAL_ENV']
    env = {n: os.environ[n] for n in names if n in os.environ}
    qa_env = os.path.join(ROOT, 'out', 'local-tools', 'qa.env')
    if os.path.exists(qa_env):
        script = f'root={ROOT!r}; set -a; source {qa_env!r}; ' + '; '.join(f'echo "{n}=${{{n}:-}}"' for n in names)
        for line in subprocess.run(['bash', '-c', script], capture_output=True, text=True).stdout.splitlines():
            name, _, value = line.partition('=')
            if value and name not in env:
                env[name] = value
    if not env.get('E2E_DRIVER'):
        default = os.path.join(ROOT, 'out', 'bin', 'E2eDriver', 'release')
        if os.path.isdir(default):
            env['E2E_DRIVER'] = default
    return env


def load_script(name):
    """A script of ours with a dash in its name, as a module."""
    path = os.path.join(ROOT, 'scripts', name)
    spec = importlib.util.spec_from_file_location(name.replace('-', '_').removesuffix('.py'), path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def ws_mods():
    """ws-mods.py (Workshop items as Local mods; it needs profile(MODS_DIR, IDS) and
    mono_hash(TEXT)): from scripts/ once it has moved there (decision 5), else out/local-tools."""
    for path in (os.path.join(ROOT, 'scripts', 'ws-mods.py'), os.path.join(ROOT, 'out', 'local-tools', 'ws-mods.py')):
        if os.path.exists(path):
            spec = importlib.util.spec_from_file_location('ws_mods', path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            return module
    raise SpecFailure('ws-mods.py is in neither scripts/ nor out/local-tools/')


# ---------------------------------------------------------------- specs

TOP_KEYS = {'name', 'from', 'mods', 'options', 'setup', 'steps', 'require_log', 'mask_text', 'tolerance',
            'thresholds', 'ignore_fields', 'memory_gb', 'spaced_out', 'translation', 'language',
            'keyboard', 'tweaks_menu', 'scaled_screen', 'blocked_plugins', 'stray_plugins', 'data_collection',
            'extra_saves', 'display_dpi', 'workshop', 'update_check', 'game_tweaks', 'env'}
# `blocked_plugins`: how a plugin of the run's copy of the install is blocked (blocked_install).
# `stray_plugins`: paths under Plugins added to that copy, `DIR/` an empty folder, else an empty
# file (what Steam can leave of another branch's build: #49).
BLOCKED_HOW = ('unreadable', 'missing')
# The game's own translations (`language`), as Localization.PreinstalledLanguages names them.
PREINSTALLED_LANGUAGES = ('zh_klei', 'ko_klei', 'ru_klei')
# `from: main-menu` loads no fixture: the steps start at the main menu (a new game's spec).
MAIN_MENU = 'main-menu'
# An XKB layout as setxkbmap takes it, a variant in parentheses: fr, de, us(dvorak).
KEYBOARD_LAYOUT = r'[a-z]{2,8}(\([\w-]+\))?'
# `open: {management: NAME}`: the game Action E2eDriver's ui.action delivers for each screen.
MANAGEMENT_ACTIONS = {'vitals': 'ManageVitals', 'consumables': 'ManageConsumables', 'priorities': 'ManagePriorities',
                      'schedule': 'ManageSchedule', 'skills': 'ManageSkills', 'research': 'ManageResearch',
                      'starmap': 'ManageStarmap', 'report': 'ManageReport', 'database': 'ManageDatabase',
                      'codex': 'ManageDatabase'}
# Steps that talk to E2eDriver (a spec with any of them needs it installed).
DRIVER_STEPS = {'click', 'toggle', 'dropdown', 'type', 'drag', 'tool', 'expect_ui', 'expect_state', 'ui_dump',
                'ui_wait', 'world_shot', 'driver', 'click_world', 'scroll', 'expect_hit', 'press'}
STEP_KINDS = {'dig', 'build', 'run', 'expect', 'call', 'save', 'shot', 'file_shot', 'open', 'key', 'keyboard',
              'expect_exit'} | DRIVER_STEPS
DRIVER_COMMANDS = {'ping', 'ui.action', 'ui.find', 'ui.at', 'ui.dump', 'ui.wait', 'world.to_screen', 'screen.to_cell', 'state.get', 'state.each', 'camera.get',
                   'camera.set', 'canvas.hide', 'canvas.show', 'setup.call', 'seed', 'count', 'stopwatch', 'keys.log', 'imgui.scrollview', 'templates.load',
                   'buildmenu.settled', 'input.move', 'input.button', 'input.key', 'input.scroll', 'input.text', 'input.submit',
                   'input.focus', 'screen.capture'}


def _cell(value):
    return isinstance(value, (list, tuple)) and len(value) == 2 and all(isinstance(v, (int, float)) for v in value)


def _rect(value):
    return isinstance(value, (list, tuple)) and len(value) == 4 and all(isinstance(v, (int, float)) for v in value)


def _ui_query(where, value, problems):
    if not isinstance(value, dict) or not (value.get('ui') or value.get('text')):
        problems.append(f'{where}: needs `ui` (a path) or `text`')


def check_step(where, kind, value, in_setup):
    """The problems of one step, as readable lines."""
    problems = []
    need = lambda ok, text: ok or problems.append(f'{where}: {text}')
    if kind not in STEP_KINDS:
        return [f'{where}: unknown step {kind!r}']
    if kind in ('shot', 'world_shot', 'file_shot', 'ui_dump') and isinstance(value, dict) and 'name' in value:
        need(isinstance(value['name'], str), f"{kind} name {value['name']!r} is not text (YAML reads on, off, yes and no as booleans: quote it)")
    if kind == 'dig':
        need(_rect(value), 'dig takes [x1, y1, x2, y2]')
    elif kind == 'build':
        need(isinstance(value, dict) and value.get('prefab') and (_cell(value.get('at')) or value.get('cells')),
             'build takes {prefab, at: [x, y]} or {prefab, cells: [...]}')
    elif kind == 'run':
        need(isinstance(value, dict) and isinstance(value.get('seconds'), (int, float)), 'run takes {seconds: N}')
        need(not isinstance(value, dict) or value.get('speed', 3) in (1, 2, 3), 'run speed is 1, 2 or 3')
    elif kind in ('expect', 'expect_state'):
        need(isinstance(value, dict) and value, f'{kind} takes a mapping of expectations')
        if kind == 'expect_state' and isinstance(value, dict):
            for expr, wanted in value.items():
                need(not isinstance(wanted, list) or wanted, f'{expr}: a list of checks is not empty')
                try:
                    parse_aggregate(str(expr))
                except SpecFailure as e:
                    problems.append(f'{where}: {e}')
    elif kind == 'call':
        need(isinstance(value, dict) and value.get('tool'), 'call takes {tool, args}')
    elif kind == 'save':
        need(isinstance(value, str) and value, 'save takes a name')
    elif kind == 'shot':
        need(isinstance(value, str) or (isinstance(value, dict) and value.get('name')), 'shot takes NAME or {name, crop, mask, pointer}')
        if isinstance(value, dict):
            need(value.get('pointer', 'away') in ('away', 'stay'), 'shot `pointer` is away (the default) or stay')
            for m in value.get('mask', []):
                need(isinstance(m, str) or _rect(m), f'mask {m!r} is neither a UI path nor [x, y, w, h]')
    elif kind == 'file_shot':
        need(isinstance(value, dict) and value.get('name') and isinstance(value.get('path'), str) and value['path']
             and not os.path.isabs(value['path']) and '..' not in value['path'].split('/'),
             'file_shot takes {name, path} (an image in the run\'s profile, relative, globs allowed), and threshold')
    elif kind == 'expect_exit':
        need(isinstance(value, dict) and isinstance(value.get('seconds', 120), (int, float))
             and (value.get('code', 0) is None or isinstance(value.get('code', 0), int)),
             'expect_exit takes {seconds, code} (the exit code, default 0; null for any)')
    elif kind == 'world_shot':
        need(isinstance(value, dict) and value.get('name'), 'world_shot takes {name, ortho, at, hide_ui, mask}')
        if isinstance(value, dict):
            if 'at' in value:
                need(_cell(value['at']), '`at` is a cell [x, y]')
            for m in value.get('mask', []):
                need(_rect(m) or (isinstance(m, str) and re.search(r'[/*]', m)),
                     f'mask {m!r}: world shots mask [x, y, w, h] or UI paths (no symbolic masks yet)')
    elif kind == 'open':
        need(isinstance(value, dict) and len([k for k in value if k in ('management', 'key', 'user_menu')]) == 1
             and set(value) <= {'management', 'key', 'user_menu', 'at', 'id'},
             'open takes {management: NAME}, {key: CODE} or {user_menu: ACTION_KEY, at: [x, y] | id: ID}')
        if isinstance(value, dict) and 'key' in value:
            problems += [f'{where}: {p}' for p in key_problems(value['key'])]
        if isinstance(value, dict) and 'management' in value:
            need(value['management'] in MANAGEMENT_ACTIONS, 'open management is one of ' + ', '.join(MANAGEMENT_ACTIONS))
    elif kind == 'keyboard':
        need(isinstance(value, str) and re.fullmatch(KEYBOARD_LAYOUT, value),
             'keyboard takes an XKB layout, as setxkbmap does: fr, de, us(dvorak)')
    elif kind == 'key' and isinstance(value, dict) and 'keycode' in value:
        # A physical key by its X keycode, whatever the display's layout puts on it.
        need(isinstance(value['keycode'], int) and 8 <= value['keycode'] <= 255, 'key `keycode` is an X keycode, 8 to 255')
        need(set(value) <= {'keycode', 'mods'}, 'key `keycode` takes only `mods`')
        for m in value.get('mods', []):
            problems += [f'{where}: {p}' for p in key_problems(m)]
    elif kind == 'key':
        need(isinstance(value, dict) and value.get('code'), 'key takes {code: "Shift+B"}')
        need(not isinstance(value, dict) or 'at' not in value or _cell(value['at']),
             'key takes `at: [x, y]`, the world cell to hold the pointer over')
        need(not isinstance(value, dict) or not (value.get('down') and value.get('up')),
             'key takes `down: true` or `up: true`, not both')
        need(not isinstance(value, dict) or not (value.get('down') or value.get('up'))
             or '+' not in str(value.get('code', '')).strip('+'),
             'a key held down (`down`/`up`) is one key, without modifiers')
        need(not isinstance(value, dict) or 'until' not in value
             or (isinstance(value['until'], str) and not (value.get('down') or value.get('up'))),
             'key `until` is a UI path to press the key until it is active (not with down/up)')
        need(not isinstance(value, dict) or value.get('cond', 'active') in ('active', 'gone', 'inactive'),
             'key `cond` (with `until`) is active, gone or inactive')
        if isinstance(value, dict) and value.get('code'):
            problems += [f'{where}: {p}' for p in key_problems(value['code'])]
    elif kind == 'click':
        if isinstance(value, dict) and 'at' in value:
            # A screen point (top-left pixels), for IMGUI controls: they have no element to find.
            need(_cell(value['at']) and not (value.get('ui') or value.get('text')),
                 'click `at` is a screen point [x, y], instead of `ui` or `text`')
        else:
            _ui_query(where, value, problems)
        if isinstance(value, dict):
            for m in value.get('mods', []):
                problems += [f'{where}: {p}' for p in key_problems(m)]
            need(isinstance(value.get('double', False), bool), 'click `double` is true or false')
            need(button_problem(value) is None, button_problem(value) or '')
    elif kind == 'toggle':
        _ui_query(where, value, problems)
        need(isinstance(value, dict) and isinstance(value.get('to'), bool), 'toggle needs `to: true|false`')
    elif kind == 'dropdown':
        _ui_query(where, value, problems)
        need(isinstance(value, dict) and value.get('option') is not None, 'dropdown needs `option`')
    elif kind == 'type':
        need(isinstance(value, dict) and value.get('ui'), 'type needs `ui` (the input field\'s path)')
        need(isinstance(value, dict) and isinstance(value.get('text'), str), 'type needs `text`')
    elif kind == 'drag':
        need(isinstance(value, dict) and _cell(value.get('from')) and _cell(value.get('to')),
             'drag takes {from: [x, y], to: [x, y]} (world cells)')
        need(not isinstance(value, dict) or isinstance(value.get('hold', 0), int), 'drag `hold` is a number of frames')
        need(not isinstance(value, dict) or (isinstance(value.get('via', []), list) and all(_cell(c) for c in value.get('via', []))),
             'drag `via` is a list of world cells [x, y] passed through in order')
    elif kind == 'click_world':
        need(isinstance(value, dict) and (_cell(value.get('cell')) != _cell(value.get('pos'))),
             'click_world takes {cell: [x, y]} or {pos: [x, y]} (a world position)')
        need(not isinstance(value, dict) or isinstance(value.get('select', ''), str),
             'click_world `select` is the prefab ID to click until it is selected')
        need(not isinstance(value, dict) or button_problem(value) is None,
             (isinstance(value, dict) and button_problem(value)) or '')
    elif kind == 'scroll':
        need(isinstance(value, dict) and isinstance(value.get('notches'), int), 'scroll takes {at, notches: N}')
    elif kind == 'tool':
        need(isinstance(value, dict) and (value.get('toolbar') or value.get('ui') or value.get('key')),
             'tool takes {toolbar: NAME}, {ui: PATH} or {key: CODE}')
    elif kind == 'expect_hit':
        need(isinstance(value, dict) and ('top' in value or 'not' in value),
             'expect_hit takes {at, top: PATTERN} or {at, not: PATTERN}')
    elif kind == 'expect_ui':
        need(isinstance(value, dict) and value and all(isinstance(v, dict) for v in value.values()),
             'expect_ui takes {PATH: {text, active, interactable, isOn, exists}}')
    elif kind == 'ui_dump':
        need(isinstance(value, dict) and value.get('name') and value.get('root'), 'ui_dump takes {name, root}')
    elif kind == 'ui_wait':
        need(isinstance(value, dict) and value.get('path'), 'ui_wait takes {path, cond, frames}')
    elif kind == 'driver':
        need(isinstance(value, dict) and len(value) == 1, 'driver takes one {command: args}')
        if isinstance(value, dict) and len(value) == 1:
            (command, _), = value.items()
            need(command in DRIVER_COMMANDS, f'unknown E2eDriver command {command!r}')
            # Reflection may prepare a test, never perform the thing under test (§1, issue #6).
            need(in_setup or command != 'setup.call', 'driver setup.call is only allowed in setup:')
    return problems


def check_spec(spec):
    """The problems of a parsed spec, as readable lines (empty when it is valid)."""
    if not isinstance(spec, dict):
        return ['a spec is a mapping']
    problems = [f'unknown key {k!r}' for k in spec if k not in TOP_KEYS]
    if not spec.get('from'):
        problems.append('`from` names the fixture')
    if not isinstance(spec.get('spaced_out', True), bool):
        problems.append('`spaced_out` is true or false')
    if not isinstance(spec.get('tweaks_menu', False), bool):
        problems.append('`tweaks_menu` is true or false')
    tweaks = spec.get('game_tweaks', [])
    if not isinstance(tweaks, list) or not all(isinstance(t, str) and t for t in tweaks):
        problems.append('`game_tweaks` is a list of `--game-tweak` values')
    extra_env = spec.get('env', {})
    if not isinstance(extra_env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in extra_env.items()):
        problems.append('`env` maps names to strings')
    if not isinstance(spec.get('scaled_screen', False), bool):
        problems.append('`scaled_screen` is true or false')
    if 'display_dpi' in spec and not (type(spec['display_dpi']) is int and 1 <= spec['display_dpi'] <= 1000):
        problems.append('`display_dpi` is a whole number of dots per inch (1-1000)')
    blocked = spec.get('blocked_plugins', {})
    if not isinstance(blocked, dict) or not all(isinstance(n, str) and n and '/' not in n and how in BLOCKED_HOW
                                                for n, how in blocked.items()):
        problems.append('`blocked_plugins` maps plugin file names to ' + ' or '.join(BLOCKED_HOW))
    stray = spec.get('stray_plugins', [])
    if not isinstance(stray, list) or not all(
            isinstance(p, str) and re.fullmatch(r'[\w.-]+(/[\w.-]+)*/?', p) and '..' not in p.split('/') for p in stray):
        problems.append('`stray_plugins` lists paths under Plugins (`DIR/` for an empty folder)')
    extra = spec.get('extra_saves', [])
    if not isinstance(extra, list) or not all(isinstance(n, str) and re.fullmatch(r'[\w.-]+', n) for n in extra):
        problems.append('`extra_saves` is a list of fixture names')
    if not isinstance(spec.get('data_collection', True), bool):
        problems.append('`data_collection` is true or false')
    update = spec.get('update_check')
    if update is not None and not (isinstance(update, dict) and set(update) == {'releases', 'tag'}
                                   and os.path.exists(os.path.join(FIXTURES, str(update['releases'])))):
        problems.append('`update_check` is {releases: FILE in tests/e2e/fixtures, tag: vYYYY.MM.DD.NN}')
    if not isinstance(spec.get('steps'), list):
        problems.append('`steps` is a list')
    mods = spec.get('mods', [])
    if not isinstance(mods, list) or not all(re.fullmatch(r'\d+', str(m)) for m in mods):
        problems.append('`mods` is a list of Workshop IDs')
    elif 'translation' in spec and str(spec['translation']) not in [str(m) for m in mods]:
        problems.append('`translation` names one of `mods`')
    if 'language' in spec and spec['language'] not in PREINSTALLED_LANGUAGES:
        problems.append(f"`language` is one of the game's own: {', '.join(PREINSTALLED_LANGUAGES)}")
    elif 'language' in spec and 'translation' in spec:
        problems.append('`language` and `translation` both pick the language')
    if 'keyboard' in spec and not (isinstance(spec['keyboard'], str) and re.fullmatch(KEYBOARD_LAYOUT, spec['keyboard'])):
        problems.append('`keyboard` is an XKB layout, as setxkbmap takes it: fr, de, us(dvorak)')
    if 'keyboard' not in spec and any(isinstance(s, dict) and 'keyboard' in s for s in spec.get('steps') or []):
        problems.append('a `keyboard` step needs the spec\'s `keyboard`, which keeps the display\'s layout (-noreset)')
    options = spec.get('options', {})
    if not isinstance(options, dict) or not all(isinstance(v, dict) for v in options.values()):
        problems.append('`options` maps a static ID to {Field: value}')
    elif not all(re.fullmatch(r'[^/\\]+\.json', str(v.get('$file', 'x.json'))) for v in options.values()):
        problems.append('an `options` `$file` is a file name ending in .json')
    workshop = spec.get('workshop')
    if workshop is not None and (not isinstance(workshop, dict) or not str(workshop.get('id', '')).isdigit()
                                 or workshop.get('layout', 'folder') not in ('folder', 'zip')
                                 or set(workshop) - {'id', 'layout', 'corrupt'}
                                 or (workshop.get('corrupt') and workshop.get('layout') != 'zip')):
        problems.append('`workshop` is {id: WORKSHOP_ID, layout: folder|zip, corrupt: true (a zip only)}')
    for key in ('require_log', 'mask_text'):
        if not isinstance(spec.get(key, []), list):
            problems.append(f'`{key}` is a list of regular expressions')
        else:
            for pattern in spec.get(key, []):
                try:
                    re.compile(pattern)
                except (re.error, TypeError) as e:
                    problems.append(f'`{key}` pattern {pattern!r}: {e}')
    for block in ('setup', 'steps'):
        for number, step in enumerate(spec.get(block) or [], 1):
            where = f'{block} {number}'
            if not isinstance(step, dict) or len(step) != 1:
                problems.append(f'{where}: a step is one {{kind: value}}')
                continue
            (kind, value), = step.items()
            problems += check_step(where, kind, value, block == 'setup')
    return problems


def load_yaml(path):
    """PATH's YAML, as yaml.safe_load reads it, but a mapping that repeats a key is a SpecError
    (PyYAML keeps the last one, so the checks under the others would never run)."""
    import yaml

    class Loader(yaml.SafeLoader):
        pass

    def mapping(loader, node, deep=False):
        lines = {}
        for key_node, _ in node.value:
            if key_node.tag == 'tag:yaml.org,2002:merge':
                continue
            key = loader.construct_object(key_node, deep=True)
            try:
                hash(key)
            except TypeError:
                continue  # safe_load's own error says so
            line = key_node.start_mark.line + 1
            if key in lines:
                raise SpecError(f'{path}: key {key!r} is repeated, at lines {lines[key]} and {line} '
                                '(YAML keeps only the last; give a list of checks instead)')
            lines[key] = line
        return yaml.SafeLoader.construct_mapping(loader, node, deep)

    Loader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)
    with open(path) as f:
        return yaml.load(f, Loader=Loader)


def load_spec(name):
    path = name if name.endswith('.yaml') else os.path.join(SPECS, name + '.yaml')
    spec = load_yaml(path)
    problems = check_spec(spec)
    if problems:
        raise SpecError(f'{path}:\n  ' + '\n  '.join(problems))
    spec.setdefault('name', os.path.splitext(os.path.basename(path))[0])
    spec['path'] = os.path.relpath(os.path.abspath(path), SPECS)
    spec.setdefault('setup', [])
    spec['mods'] = [str(m) for m in spec.get('mods', [])]
    return spec


def needs_driver(spec):
    for step in (spec.get('setup') or []) + spec['steps']:
        (kind, value), = step.items()
        if kind in DRIVER_STEPS and not (kind == 'expect_state' and all(k.startswith('file:') for k in value)):
            return True
        if kind == 'shot' and isinstance(value, dict) and (value.get('crop') or value.get('mask')):
            return True
        if kind == 'open' and isinstance(value, dict) and 'management' in value:
            return True
    return False


# ---------------------------------------------------------------- lanes, the players' budget and the perf lock
#
# Every runner on the machine, in whichever worktree, meets in one directory (shared_dir):
#   lane-L.lock       held (flock) by the runner using lane L; it holds that runner's pid and root
#   players.json      the players running now and the memory each one is counted for
#   waiters.json      the runners waiting for a player, oldest first (Budget.hold's tickets)
#   players.lock      guards players.json and waiters.json
#   perf.lock         held shared by every spec run; a perf job takes it exclusively
#   xvfb.lock         serialises xvfb-run -a starts (--xvfb-run)
#   reserved          ports and displays that others use, which no lane takes

def shared_dir():
    """The machine-wide lock directory: E2E_LOCK_DIR, default ~/.cache/oni-brutal-e2e."""
    return os.environ.get('E2E_LOCK_DIR') or os.path.join(os.path.expanduser('~'), '.cache', 'oni-brutal-e2e')


class WaitTimeout(SpecFailure):
    """A wait (for a perf job, a lane or memory for a player) longer than --max-wait."""


class Waiter:
    """A wait that says what it waits for: when it starts, when what it waits for changes, and
    again every REPEAT seconds; it gives up after MAX_WAIT seconds (None: never)."""
    REPEAT = 600

    def __init__(self, what, log, max_wait=None, clock=time.time):
        self.what, self.log, self.max_wait, self.clock = what, log, max_wait, clock
        self.start = self.said = self.key = None

    @property
    def waited(self):
        return 0 if self.start is None else self.clock() - self.start

    def tick(self, reason, key=None):
        now = self.clock()
        if self.start is None:
            self.start = now
        waited = now - self.start
        key = reason if key is None else key
        if key != self.key or now - self.said >= self.REPEAT:
            so_far = f' ({waited / 60:.0f} min so far)' if waited >= 60 else ''
            self.log(f'waiting for {self.what}{so_far}: {reason}')
            self.said, self.key = now, key
        if self.max_wait is not None and waited >= self.max_wait:
            raise WaitTimeout(f'gave up after {waited / 60:.1f} min (--max-wait) waiting for {self.what}: {reason}')


def owner_record():
    """Who holds a lock: written into lane locks and the players' ledger."""
    return {'pid': os.getpid(), 'start': process_start(os.getpid()), 'root': ROOT, 'out': OUT,
            'since': time.strftime('%Y-%m-%dT%H:%M:%S')}


def process_start(pid):
    """A process's start time in clock ticks (/proc/PID/stat field 22), which tells it apart
    from a later process given the same pid; None when it has gone."""
    try:
        with open(f'/proc/{pid}/stat') as f:
            return int(f.read().rsplit(')', 1)[1].split()[19])
    except (OSError, IndexError, ValueError):
        return None


def alive_owner(entry):
    return entry.get('pid') is not None and process_start(entry['pid']) == entry.get('start')


# Lanes. Lane L's OniMcp listens on 8800 + 10 L, its E2eDriver on 8801 + 10 L, and its display is
# :(DISPLAY_BASE + L), an Xvfb the runner starts (§4.4). Lane numbers are machine-wide.
FIRST_LANE, LAST_LANE = 1, 19
DISPLAY_BASE = 300  # E2E_DISPLAY_BASE; far above xvfb-run -a's :99 and up


def lane_ports(lane):
    """OniMcp's and E2eDriver's ports of a lane (§4.4)."""
    return 8800 + 10 * lane, 8801 + 10 * lane


def lane_display(lane):
    return f':{int(os.environ.get("E2E_DISPLAY_BASE") or DISPLAY_BASE) + lane}'


def port_open(port):
    with socket.socket() as s:
        s.settimeout(1)
        return s.connect_ex(('127.0.0.1', port)) == 0


def display_in_use(number):
    """Whether X display NUMBER is taken: a server accepts connections there, or a live process
    holds its lock file (Xvfb itself replaces a stale one)."""
    number = str(number).lstrip(':')
    if display_ready(number):
        return True
    try:
        with open(f'/tmp/.X{number}-lock') as f:
            pid = int(f.read().strip())
    except FileNotFoundError:
        return False
    except (OSError, ValueError):
        return True
    return os.path.exists(f'/proc/{pid}')


def parse_displays(text):
    """--displays ":88,:89" (or "88,89") as display names."""
    displays = []
    for part in text.split(','):
        part = part.strip()
        if not re.fullmatch(r':?\d+', part):
            raise ValueError(f'bad display {part!r}')
        displays.append(part if part.startswith(':') else ':' + part)
    if len(set(displays)) != len(displays):
        raise ValueError('a display is named twice')
    return displays


def parse_ranges(text):
    """'8788, 8820-8831 :80-:85' as a set of numbers."""
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


def reservations(directory=None, text=None):
    """Ports and displays others use (another tool, a session on the old per-worktree locks),
    which no lane takes: lines 'ports 8788 8820-8831' and 'displays :80-:85 :91' in DIRECTORY's
    reserved file and in E2E_RESERVED (';' between lines). Returns (ports, displays)."""
    lines = []
    path = os.path.join(directory or shared_dir(), 'reserved')
    if os.path.exists(path):
        with open(path) as f:
            lines += f.read().splitlines()
    lines += (os.environ.get('E2E_RESERVED', '') if text is None else text).split(';')
    ports, displays = set(), set()
    for line in lines:
        line = line.split('#', 1)[0].strip()
        if not line:
            continue
        kind, _, rest = line.partition(' ')
        if kind not in ('ports', 'displays'):
            raise ValueError(f'reserved: {line!r} is neither "ports ..." nor "displays ..."')
        (ports if kind == 'ports' else displays).update(parse_ranges(rest))
    return ports, displays


class Lane:
    """A lane held by this process: its number, ports, display (None for xvfb-run -a) and lock."""

    def __init__(self, number, lock_file, display=None, record=None):
        self.number, self.lock_file, self.display = number, lock_file, display
        self.mcp_port, self.driver_port = lane_ports(number)
        # The lock file's record: its holder (owner_record), what it does now (state) and the
        # processes it runs there (children), for --status and for whoever takes the lane next.
        self.record = record if record is not None else {}
        self.children = []
        self.guard = threading.Lock()
        if lock_file:
            with _held_guard:
                _held_lanes.add(self)

    def update(self, **fields):
        """Rewrites the lock's record with FIELDS and the children."""
        with self.guard:
            self.record.update(fields)
            self.record['children'] = list(self.children)
            if self.lock_file:
                write_record(self.lock_file, self.record)

    def track(self, role, pid, scope=None):
        """A process this lane runs now (ROLE: player, xvfb), in the lock's record."""
        self.children.append({'role': role, 'pid': pid, 'start': process_start(pid), 'scope': scope})
        self.update()

    def untrack(self, pid):
        self.children[:] = [c for c in self.children if c['pid'] != pid]
        self.update()

    def release(self):
        with _held_guard:
            _held_lanes.discard(self)
        if self.lock_file:
            self.children.clear()
            with contextlib.suppress(OSError, ValueError):
                self.update(state='released')
            fcntl.flock(self.lock_file, fcntl.LOCK_UN)
            self.lock_file.close()
            self.lock_file = None


def lane_problem(number, busy=port_open, display_busy=display_in_use, reserved=(set(), set()), display=True):
    """Why lane NUMBER cannot be used now (a reserved or open port, a reserved or running
    display), or None."""
    ports = lane_ports(number)
    taken = [p for p in ports if p in reserved[0]]
    if taken:
        return f'port {taken[0]} is reserved'
    if display and int(lane_display(number)[1:]) in reserved[1]:
        return f'display {lane_display(number)} is reserved'
    taken = [p for p in ports if busy(p)]
    if taken:
        return f'port {taken[0]} is open'
    if display and display_busy(lane_display(number)[1:]):
        return f'display {lane_display(number)} is in use'
    return None


def try_lane(number, lock_dir=None, busy=port_open, display_busy=display_in_use, reserved=(set(), set()), display=True,
             leftovers=None, log=say):
    """Lane NUMBER if no runner on the machine holds it and its ports and display are free and
    not reserved, else None. What a dead runner left on it is cleaned first (lane_leftovers;
    LEFTOVERS(number, record, lock_dir) stands in for it in tests)."""
    if lane_problem(number, lambda p: False, lambda d: False, reserved, display):
        return None
    lock_dir = lock_dir or shared_dir()
    os.makedirs(lock_dir, exist_ok=True)
    f = open(os.path.join(lock_dir, f'lane-{number}.lock'), 'a+')  # 'w' would wipe the holder's record
    try:
        fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        f.close()
        return None
    previous = read_record(f)
    # Looked for only when the lane seems busy or its last runner recorded children (a scan of
    # every process, made for each lane while a runner waits for one).
    if previous.get('children') or lane_problem(number, busy, display_busy, reserved, display):
        found = (leftovers or (lambda n, r, d: lane_leftovers(n, r, d, display=None if display else -1)))(
            number, previous, lock_dir)
        if found:
            log(f'lane {number}: its last runner (pid {previous.get("pid")}) has gone; cleaning what it left')
            clean_leftovers(found, log=lambda s: log(f'lane {number}: {s}'))
    if lane_problem(number, busy, display_busy, reserved, display):
        fcntl.flock(f, fcntl.LOCK_UN)
        f.close()
        return None
    record = dict(owner_record(), lane=number, state='idle', children=[])
    write_record(f, record)
    return Lane(number, f, lane_display(number) if display else None, record)


def allocate_lane(first, lock_dir=None, busy=port_open, wait=True, log=say, last=None, display=True,
                  display_busy=display_in_use, reserved=None, max_wait=None, give_up=None):
    """The first free lane from FIRST to LAST (default LAST_LANE), waiting for one if WAIT; None
    once GIVE_UP() holds while it waits."""
    lock_dir = lock_dir or shared_dir()
    last = LAST_LANE if last is None else last
    reserved = reservations(lock_dir) if reserved is None else reserved
    waiter = Waiter(f'a free lane ({first}..{last})', log, max_wait)
    while True:
        for number in range(first, last + 1):
            lane = try_lane(number, lock_dir, busy, display_busy, reserved, display)
            if lane:
                return lane
        if not wait or (give_up and give_up()):
            return None
        holders = [f'{s["lane"]}: pid {s["pid"]} in {s["root"]}' for s in lane_status(lock_dir) if first <= s['lane'] <= last]
        waiter.tick('held: ' + (', '.join(holders) or 'none; the others are busy or reserved')
                    + ' (scripts/e2e.py --status says by what)', key=tuple(holders))
        time.sleep(5)


def lane_status(lock_dir=None):
    """The lanes runners hold now: [{lane, pid, root, since}]."""
    lock_dir = lock_dir or shared_dir()
    held = []
    for name in sorted(os.listdir(lock_dir)) if os.path.isdir(lock_dir) else []:
        match = re.fullmatch(r'lane-(\d+)\.lock', name)
        if not match:
            continue
        with open(os.path.join(lock_dir, name), 'a+') as f:
            try:
                fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
                fcntl.flock(f, fcntl.LOCK_UN)
                continue  # nobody holds it
            except OSError:
                pass
            f.seek(0)
            try:
                record = json.loads(f.read() or '{}')
            except ValueError:
                record = {}
        held.append({'pid': None, 'root': '?', 'since': '?', **record, 'lane': int(match.group(1))})
    return sorted(held, key=lambda s: s['lane'])


# ---------------------------------------------------------------- leftovers: what a runner that died left running
#
# A runner's players and Xvfb run in sessions (and systemd scopes) of their own, so they outlive a
# runner killed by `timeout` or SIGKILL, holding the lane's ports and display. So that nothing is
# left behind:
# - every process a runner starts carries E2E_OWNER=PID:START (the runner's pid and start time),
#   E2E_LANE and E2E_LOCK_DIR in its environment, and each player runs in a systemd scope named
#   oni-test-PID-START-LABEL.scope (scripts/contain.sh, ONI_SCOPE);
# - the lane's lock records those children (pid, start time, scope) while they run;
# - the runner tears them down on any exit it sees (normal, exception, SIGTERM, SIGINT, SIGHUP), and
#   a watcher process (--reap-after) does it when the runner dies without seeing it (SIGKILL);
# - a runner taking a lane whose last holder is dead first ends what that holder left: processes
#   whose E2E_OWNER names a dead runner, the lane's recorded children (matched by start time),
#   scopes named for a dead runner, and the stale X lock and socket of the lane's display.
# Leftovers of runners from before E2E_OWNER (a player with the lane's E2E_LANE, or an Xvfb on the
# lane's display, with no e2e.py among their ancestors) count in the machine's lock directory only.

OWNER_ENV = 'E2E_OWNER'
SCOPE_PREFIX = 'oni-test'


def owner_tag(pid=None):
    """PID:START of a process (default this one): what E2E_OWNER and scope names hold."""
    pid = pid or os.getpid()
    return f'{pid}:{process_start(pid)}'


def tag_alive(tag):
    """Whether the process a PID:START tag names still runs (that process, not a later one given
    its pid)."""
    match = re.fullmatch(r'(\d+):(\d+)', tag or '')
    return bool(match) and process_start(int(match.group(1))) == int(match.group(2))


def scope_name(label, tag=None):
    """The systemd scope a runner (TAG, default this process) runs a player in."""
    pid, start = (tag or owner_tag()).split(':')
    return f'{SCOPE_PREFIX}-{pid}-{start}-' + re.sub(r'[^A-Za-z0-9_.-]', '_', label) + '.scope'


def scope_owner(unit):
    """(owner tag, label) of a scope named by scope_name, else None."""
    match = re.fullmatch(rf'{SCOPE_PREFIX}-(\d+)-(\d+)-(.+)\.scope', unit)
    return (f'{match.group(1)}:{match.group(2)}', match.group(3)) if match else None


def read_proc(pid):
    """A process as {pid, ppid, pgid, start, comm, args, env}, or None once it has gone."""
    try:
        with open(f'/proc/{pid}/stat') as f:
            stat = f.read()
        with open(f'/proc/{pid}/cmdline', 'rb') as f:
            args = [a.decode(errors='replace') for a in f.read().split(b'\0')[:-1]]
    except OSError:
        return None
    comm, rest = stat[stat.index('(') + 1:stat.rindex(')')], stat[stat.rindex(')') + 2:].split()
    return {'pid': int(pid), 'ppid': int(rest[1]), 'pgid': int(rest[2]), 'start': int(rest[19]), 'comm': comm,
            'args': args, 'env': process_environ(pid)}


def snapshot():
    """Every process this user can see, as read_proc gives them."""
    procs = (read_proc(e) for e in os.listdir('/proc') if e.isdigit())
    return [p for p in procs if p]


def has_e2e_ancestor(proc, by_pid):
    """Whether a runner (an e2e.py process) is among PROC's ancestors: its runner still runs."""
    seen, p = set(), by_pid.get(proc['ppid'])
    while p and p['pid'] not in seen and p['pid'] > 1:
        seen.add(p['pid'])
        if any(a.endswith('e2e.py') for a in p['args'][:3]):
            return True
        p = by_pid.get(p['ppid'])
    return False


_systemd = None


def has_systemd():
    """Whether a systemd user manager runs the players' scopes (scripts/contain.sh)."""
    global _systemd
    if _systemd is None:
        _systemd = bool(shutil.which('systemctl')) and subprocess.run(
            ['systemctl', '--user', 'show-environment'], capture_output=True).returncode == 0
    return _systemd


def test_scopes():
    """The oni-test-* scopes loaded now."""
    if not has_systemd():
        return []
    out = subprocess.run(['systemctl', '--user', 'list-units', '--type=scope', '--all', '--plain', '--no-legend',
                          f'{SCOPE_PREFIX}-*'], capture_output=True, text=True).stdout
    return [line.split()[0] for line in out.splitlines() if line.strip()]


def stop_scope(unit, grace=None):
    """Ends every process in a scope: SIGTERM, then SIGKILL after GRACE seconds (default
    quit_grace(): a scope holds a player)."""
    def active():
        return subprocess.run(['systemctl', '--user', 'is-active', '--quiet', unit]).returncode == 0
    if not has_systemd() or not active():
        return
    subprocess.run(['systemctl', '--user', 'kill', '--signal=SIGTERM', unit], capture_output=True)
    deadline = time.time() + (quit_grace() if grace is None else grace)
    while active() and time.time() < deadline:
        time.sleep(0.2)
    if active():
        subprocess.run(['systemctl', '--user', 'kill', '--signal=SIGKILL', unit], capture_output=True)
        subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True)


def running(pid, start=None):
    """Whether process PID runs (a zombie, ended but not yet reaped, does not), and is the one
    that started at START when given."""
    try:
        with open(f'/proc/{pid}/stat') as f:
            fields = f.read().rsplit(')', 1)[1].split()
    except OSError:
        return False
    return fields[0] not in 'ZX' and (start is None or int(fields[19]) == start)


# A player stops gracefully on SIGTERM. Ours (src/OniMoono.Host/QuitSignals.cs) quits at the next
# frame boundary as Application.Quit does, its graphics device and window going first, and exits
# within 8 s whatever happens. A player killed in the middle of a GPU wait (on WSL, a wait on
# /dev/dxg through Mesa's d3d12 driver) was followed by a kernel soft lockup that hung WSL
# (2026-11-02), so a teardown signals the players first, alone (their X servers still up), waits
# up to E2E_QUIT_GRACE seconds (default 10) for them, and only then kills what is left.
QUIT_GRACE = 10


def quit_grace():
    """E2E_QUIT_GRACE's seconds, else QUIT_GRACE."""
    with contextlib.suppress(ValueError):
        value = float(os.environ.get('E2E_QUIT_GRACE', ''))
        if value >= 0:
            return value
    return QUIT_GRACE


def is_player(proc):
    return proc.get('comm') in PLAYER_NAMES


def quit_players(procs, grace=None):
    """SIGTERM to each of PROCS ({pid, start}) still the same process, then a wait of up to GRACE
    seconds (default quit_grace()) for them to end. Returns the ones still running."""
    procs = [p for p in procs if process_start(p['pid']) == p['start']]
    for p in procs:
        with contextlib.suppress(OSError):
            os.kill(p['pid'], signal.SIGTERM)
    deadline = time.time() + (quit_grace() if grace is None else grace)
    while time.time() < deadline and any(running(p['pid'], p['start']) for p in procs):
        time.sleep(0.05)
    return [p for p in procs if running(p['pid'], p['start'])]


def kill_verified(procs, grace=5):
    """SIGTERM, then SIGKILL after GRACE seconds, to each of PROCS while it is still the same
    process (its start time unchanged). Players among them go first (quit_players), and one
    still running after its quit grace gets SIGKILL with the rest."""
    procs = [p for p in procs if process_start(p['pid']) == p['start']]
    players = [p for p in procs if is_player(p)]
    stuck = quit_players(players) if players else []
    procs = [p for p in procs if not is_player(p)]
    for p in stuck:
        with contextlib.suppress(OSError):
            os.kill(p['pid'], signal.SIGKILL)
    for p in procs:
        with contextlib.suppress(OSError):
            os.kill(p['pid'], signal.SIGTERM)
    deadline = time.time() + grace
    while time.time() < deadline and any(running(p['pid'], p['start']) for p in procs):
        time.sleep(0.1)
    for p in procs:
        if running(p['pid'], p['start']):
            with contextlib.suppress(OSError):
                os.kill(p['pid'], signal.SIGKILL)


def x_tmp():
    """Where X servers keep their lock files and sockets (E2E_X_TMP: the tests')."""
    return os.environ.get('E2E_X_TMP') or '/tmp'


def x_lock_pid(number, tmp=None):
    try:
        with open(os.path.join(tmp or x_tmp(), f'.X{number}-lock')) as f:
            return int(f.read().strip())
    except (OSError, ValueError):
        return None


def socket_answers(path):
    with socket.socket(socket.AF_UNIX) as s:
        try:
            s.connect(path)
            return True
        except OSError:
            return False


def machine_dir(lock_dir):
    """Whether LOCK_DIR is the machine's (not a test's): leftovers of runners from before
    E2E_OWNER count only there."""
    default = os.path.join(os.path.expanduser('~'), '.cache', 'oni-brutal-e2e')
    return os.path.realpath(lock_dir) == os.path.realpath(default)


def lane_leftovers(number, record=None, lock_dir=None, procs=None, scopes=None, alive=tag_alive, display=None,
                   machine=None, tmp=None):
    """What runners that died left on lane NUMBER: [(kind, target, why)], kind 'scope' (a unit
    name), 'process' (a read_proc dict) or 'file' (an X lock file or socket). RECORD is the lane
    lock's last record (nothing is a leftover while its runner lives); DISPLAY the lane's X display
    number (default its own)."""
    lock_dir = lock_dir or shared_dir()
    machine = machine_dir(lock_dir) if machine is None else machine
    record = record or {}
    procs = snapshot() if procs is None else procs
    scopes = test_scopes() if scopes is None else scopes
    tmp = tmp or x_tmp()
    by_pid = {p['pid']: p for p in procs}
    holder = f'{record["pid"]}:{record["start"]}' if record.get('pid') and record.get('start') else None
    if holder and alive(holder):
        return []
    found, chosen = [], set()

    def add_process(p, why):
        if p['pid'] not in chosen and p['pid'] != os.getpid():
            chosen.add(p['pid'])
            found.append(('process', p, why))

    for unit in scopes:
        owner = scope_owner(unit)
        if owner and owner[1].startswith(f'lane{number}-') and not alive(owner[0]):
            found.append(('scope', unit, f'scope of runner {owner[0].split(":")[0]}, which has gone'))
    for p in procs:
        env = p['env']
        if env.get('E2E_LANE') != str(number):
            continue
        if env.get('E2E_LOCK_DIR', lock_dir if machine else None) != lock_dir:
            continue  # another lock directory's lane
        tag = env.get(OWNER_ENV)
        if tag:
            if not alive(tag):
                add_process(p, f'{p["comm"]} of runner {tag.split(":")[0]}, which has gone')
        elif machine and p['comm'] in PLAYER_NAMES and not has_e2e_ancestor(p, by_pid):
            add_process(p, f'{p["comm"]} on lane {number} with no runner')
    for child in record.get('children', []):
        p = by_pid.get(child.get('pid'))
        if p and p['start'] == child.get('start'):
            add_process(p, f'{child.get("role", "child")} recorded in the lane lock')
    # The lane's display: its X server when a dead runner's, then a stale lock file and socket.
    # Outside the machine's lock directory, only in a test's X folder (E2E_X_TMP).
    if display is None:
        display = int(lane_display(number)[1:]) if machine or tmp != '/tmp' else -1
    lock_pid = x_lock_pid(display, tmp)
    server = by_pid.get(lock_pid) if lock_pid else None
    if server and server['comm'] == 'Xvfb' and f':{display}' in server['args']:
        tag = server['env'].get(OWNER_ENV)
        if (tag and not alive(tag)) or (not tag and machine and not has_e2e_ancestor(server, by_pid)):
            add_process(server, f'Xvfb :{display} with no runner')
    lock_file = os.path.join(tmp, f'.X{display}-lock')
    if os.path.exists(lock_file) and (lock_pid in chosen or not server):
        found.append(('file', lock_file, 'stale X lock'))
        socket_file = os.path.join(tmp, '.X11-unix', f'X{display}')
        if os.path.exists(socket_file):
            found.append(('file', socket_file, 'stale X socket'))
    return found


def describe_leftover(item):
    kind, target, why = item
    if kind == 'process':
        return f'pid {target["pid"]} ({why}): {" ".join(target["args"])[:120]}'
    return f'{kind} {target} ({why})'


def clean_leftovers(items, log=say, grace=5):
    """Ends the scopes and processes lane_leftovers found, then removes the stale X files (each
    checked again: the lock's process gone, no server answering on the socket). Players go
    first, with their quit grace, while their X servers still run."""
    for item in items:
        if item[0] == 'process':
            log(f'cleaning {describe_leftover(item)}')
    procs = [target for kind, target, _ in items if kind == 'process']
    kill_verified([p for p in procs if is_player(p)], grace)
    for item in items:
        if item[0] == 'scope':
            log(f'cleaning {describe_leftover(item)}')
            stop_scope(item[1])
    kill_verified([p for p in procs if not is_player(p)], grace)
    for item in items:
        if item[0] != 'file':
            continue
        path = item[1]
        name = os.path.basename(path)
        if name.endswith('-lock'):
            pid = x_lock_pid(name[2:-5], os.path.dirname(path))
            if pid is not None and running(pid):
                continue
        elif socket_answers(path):
            continue
        log(f'cleaning {describe_leftover(item)}')
        with contextlib.suppress(OSError):
            os.unlink(path)


def owned_processes(tag, procs=None):
    """The processes whose E2E_OWNER is TAG: what a runner started, wherever they run now."""
    return [p for p in (snapshot() if procs is None else procs) if p['env'].get(OWNER_ENV) == tag]


def teardown_owner(tag, grace=2, log=None):
    """Ends everything runner TAG started: its scopes, then every process carrying its E2E_OWNER,
    and removes the X lock files its killed Xvfbs leave. The players go first, all at once, with
    their quit grace (quit_players), while their X servers still run."""
    log = log or (lambda s: None)
    procs = owned_processes(tag)
    players = [p for p in procs if is_player(p)]
    for p in players:
        log(f'quitting pid {p["pid"]}: {" ".join(p["args"])[:120]}')
    kill_verified(players, grace)
    for unit in test_scopes():
        owner = scope_owner(unit)
        if owner and owner[0] == tag:
            log(f'stopping {unit}')
            stop_scope(unit)
    procs = [p for p in procs if not is_player(p)]
    for p in procs:
        log(f'killing pid {p["pid"]}: {" ".join(p["args"])[:120]}')
    kill_verified(procs, grace)
    for p in procs:
        number = next((a[1:] for a in p['args'][1:] if re.fullmatch(r':\d+', a)), None)
        if p['comm'] == 'Xvfb' and number and x_lock_pid(number) == p['pid']:
            for path in (os.path.join(x_tmp(), f'.X{number}-lock'), os.path.join(x_tmp(), '.X11-unix', f'X{number}')):
                with contextlib.suppress(OSError):
                    os.unlink(path)


def read_record(f):
    f.seek(0)
    try:
        return json.loads(f.read() or '{}')
    except ValueError:
        return {}


def write_record(f, record):
    f.seek(0)
    f.truncate()
    json.dump(record, f)
    f.flush()


def reap_after(pid, start, lock_dir=None, poll=2.0):
    """--reap-after PID START: waits for runner PID to end, however it ends, then ends what it
    left (teardown_owner) and cleans its lanes. Every runner (and qa.sh) starts one in a session of
    its own, so the `timeout` or kill that ends the runner leaves it running."""
    tag = f'{pid}:{start}'
    while tag_alive(tag):
        time.sleep(poll)
    teardown_owner(tag)
    lock_dir = lock_dir or shared_dir()
    for name in os.listdir(lock_dir) if os.path.isdir(lock_dir) else []:
        match = re.fullmatch(r'lane-(\d+)\.lock', name)
        if not match:
            continue
        with open(os.path.join(lock_dir, name), 'a+') as f:
            try:
                fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                continue  # a live runner holds it now, and cleans it before using it
            try:
                record = read_record(f)
                if f'{record.get("pid")}:{record.get("start")}' == tag:
                    clean_leftovers(lane_leftovers(int(match.group(1)), record, lock_dir), log=lambda s: None)
                    record.pop('children', None)
                    record['state'] = 'ended'
                    write_record(f, record)
            finally:
                fcntl.flock(f, fcntl.LOCK_UN)


def start_reaper(lock_dir=None):
    """This runner's watcher (reap_after), in a session of its own."""
    env = {k: v for k, v in os.environ.items() if k != OWNER_ENV}
    env['E2E_LOCK_DIR'] = lock_dir or shared_dir()
    return subprocess.Popen([sys.executable, os.path.abspath(__file__), '--reap-after', str(os.getpid()),
                             str(process_start(os.getpid()))], env=env, start_new_session=True,
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


# The lanes this process holds, for the teardown on a signal or at exit.
_held_lanes = set()
_held_guard = threading.Lock()


def teardown_all(log=None):
    """Ends every process this runner started and clears its lanes' records."""
    teardown_owner(owner_tag(), log=log)
    with _held_guard:
        lanes = list(_held_lanes)
    for lane in lanes:
        with contextlib.suppress(Exception):
            lane.children.clear()
            lane.update(state='ended')


def install_teardown():
    """The teardown on SIGTERM, SIGINT and SIGHUP (`timeout`, ^C, a closed terminal) and at exit."""
    import atexit

    def signal_say(*parts):
        # The signal can arrive while the interrupted code is inside print(): a print from the
        # handler then raises RuntimeError (a reentrant call into the buffered stdout), which
        # skipped the teardown. os.write goes past the buffer; a failed write is dropped.
        with contextlib.suppress(OSError):
            os.write(sys.stdout.fileno(), (' '.join(map(str, parts)) + '\n').encode())

    def on_signal(signum, frame):
        try:
            for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
                signal.signal(s, signal.SIG_IGN)
            signal_say(f'e2e: {signal.Signals(signum).name}: stopping this run\'s players')
            teardown_all(log=signal_say)
        finally:
            os._exit(128 + signum)
    for s in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(s, on_signal)
    atexit.register(teardown_all)


# The players' memory budget (§6.2): the machine has 62 GB. A player on a small save takes 4-6 GB,
# on 2minnow about 11 GB and on U57 about 15 GB.
MEMORY_GB = 40      # E2E_MEMORY_GB: what all players on the machine may use at once
MAX_PLAYERS = 4     # E2E_MAX_PLAYERS: how many may run at once (more exhaust the WSL GPU)
FIXTURE_GB = {'U57': 15, '2minnow': 11}
SMALL_GB, MID_GB, LATE_GB = 6, 11, 15


def fixture_of(spec):
    """The fixture save SPEC loads, or None for `from: main-menu`."""
    return None if spec['from'] == MAIN_MENU else os.path.join(FIXTURES, spec['from'] + '.sav')


def save_is_spaced_out(path):
    """Whether a save is a Spaced Out one, from its header's dlcIds (scripts/windows-run-home.sh);
    older saves list none, and their cluster names the expansion."""
    with open(path, 'rb') as f:
        head = f.read(65536).decode('latin1')
    start = head.index('{')
    info, _ = json.JSONDecoder().raw_decode(head, start)
    return 'EXPANSION1_ID' in (info.get('dlcIds') or []) or str(info.get('clusterId', '')).startswith('expansion1::')


def spaced_out(spec):
    """Whether SPEC's players run with Spaced Out: its `spaced_out`, else its fixture's mode
    (a base-game save loads in a base-game player), else on (the game's default)."""
    if 'spaced_out' in spec:
        return spec['spaced_out']
    fixture = fixture_of(spec)
    return save_is_spaced_out(fixture) if fixture and os.path.exists(fixture) else True


def player_gb(spec, fixture=None):
    """The memory a player of SPEC is counted for: the spec's memory_gb, else by its fixture."""
    if spec.get('memory_gb'):
        return float(spec['memory_gb'])
    if spec.get('from') in FIXTURE_GB:
        return float(FIXTURE_GB[spec['from']])
    size = os.path.getsize(fixture) if fixture and os.path.exists(fixture) else 0
    return float(SMALL_GB if size < 8 << 20 else MID_GB if size < 40 << 20 else LATE_GB)


class Budget:
    """The players running on the machine, from every runner: a ledger (players.json, guarded
    by players.lock) of who runs one and for how many GB. A player starts only while the total
    stays within E2E_MEMORY_GB and the count within E2E_MAX_PLAYERS (a player bigger than the
    whole budget starts alone). An entry whose process has gone is dropped.

    Waiting is first come, first served across every runner: hold() takes a ticket at the end
    of a queue (waiters.json), and only the oldest live waiter may take a player. A runner that
    has just freed a player waits behind those already waiting, and try_acquire without a ticket
    gets nothing while anyone waits. The oldest waiter's turn still depends on what runs, qa.sh
    included: it starts once its player fits. A ticket whose process has gone is dropped, so a
    dead waiter never holds up the queue."""

    def __init__(self, directory=None, gb=None, players=None):
        self.dir = directory or shared_dir()
        self.gb = float(gb or os.environ.get('E2E_MEMORY_GB') or MEMORY_GB)
        self.players = int(players or os.environ.get('E2E_MAX_PLAYERS') or MAX_PLAYERS)
        self.ledger = os.path.join(self.dir, 'players.json')
        self.queue = os.path.join(self.dir, 'waiters.json')
        self.guard = threading.Lock()

    @contextlib.contextmanager
    def _locked(self):
        os.makedirs(self.dir, exist_ok=True)
        with self.guard, open(os.path.join(self.dir, 'players.lock'), 'a+') as f:
            fcntl.flock(f, fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(f, fcntl.LOCK_UN)

    def _read(self, path=None):
        try:
            with open(path or self.ledger) as f:
                entries = json.load(f)
        except (OSError, ValueError):
            entries = []
        return [e for e in entries if alive_owner(e)]

    def _write(self, entries, path=None):
        path = path or self.ledger
        temp = f'{path}.{os.getpid()}.{threading.get_ident()}'
        with open(temp, 'w') as f:
            json.dump(entries, f, indent=1)
        os.replace(temp, path)

    def entries(self):
        with self._locked():
            return self._read()

    def waiters(self):
        """The live waiters' tickets, oldest first."""
        with self._locked():
            return self._read(self.queue)

    def enqueue(self, gb, label):
        """A ticket at the end of the queue of waiters."""
        with self._locked():
            waiters = self._read(self.queue)
            ticket = f'{os.getpid()}-{threading.get_ident()}-{time.time_ns()}'
            waiters.append(dict(owner_record(), ticket=ticket, gb=gb, label=label))
            self._write(waiters, self.queue)
            return ticket

    def dequeue(self, ticket):
        with self._locked():
            self._write([w for w in self._read(self.queue) if w['ticket'] != ticket], self.queue)

    @staticmethod
    def describe(entries):
        return ', '.join(f'{e["label"]} ({e["gb"]:g} GB, pid {e["pid"]})' for e in entries) or 'none'

    def try_acquire(self, gb, label, ticket=None):
        """A token for one player of GB, or (None, why not). A running qa.sh counts as one player
        of QA_GB. Only the oldest live waiter's TICKET may take a player while anyone waits (no
        ticket: none); the ticket leaves the queue with the token."""
        with self._locked():
            waiters = self._read(self.queue)
            if waiters and waiters[0]['ticket'] != ticket:
                ahead = next((i for i, w in enumerate(waiters) if w['ticket'] == ticket), len(waiters))
                first = waiters[0]
                return None, (f'{ahead} waiter(s) ahead in the queue, first {first["label"]} '
                              f'({first["gb"]:g} GB, pid {first["pid"]})')
            entries = self._read()
            qa_gb = float(os.environ.get('E2E_QA_GB', QA_GB))
            counted = entries + ([dict(label='qa.sh', gb=qa_gb, pid=pid) for pid, _, _ in qa_processes()]
                                 if qa_gb > 0 else [])
            used = sum(e['gb'] for e in counted)
            if counted and (len(counted) >= self.players or used + gb > self.gb):
                return None, (f'{used:g} of {self.gb:g} GB and {len(counted)} of {self.players} players in use: '
                              + self.describe(counted))
            token = f'{os.getpid()}-{threading.get_ident()}-{time.time_ns()}'
            entries.append(dict(owner_record(), token=token, gb=gb, label=label))
            self._write(entries)
            if waiters:
                self._write(waiters[1:], self.queue)
            return token, None

    def release(self, token):
        with self._locked():
            self._write([e for e in self._read() if e['token'] != token])

    @contextlib.contextmanager
    def hold(self, gb, label, log=say, max_wait=None):
        waiter = Waiter(f'memory for a player ({gb:g} GB)', log, max_wait)
        ticket = self.enqueue(gb, label)
        try:
            while True:
                token, why = self.try_acquire(gb, label, ticket)
                if token:
                    break
                waiter.tick(why, key=why.split(':')[0].split(',')[0])
                time.sleep(5)
        except BaseException:
            self.dequeue(ticket)
            raise
        try:
            yield token
        finally:
            self.release(token)


# Perf jobs: a spec never starts beside one (§10 decision 7). It waits while one holds
# perf.lock exclusively (`flock -x -o ~/.cache/oni-brutal-e2e/perf.lock CMD`) or while ps shows one.

def perf_jobs(lines=None):
    """The ps lines (args) of running perf jobs."""
    if lines is None:
        return [args for _, _, args in perf_processes()]
    found = []
    for line in lines:
        args = re.sub(r'^(/usr/bin/env )?(ba)?sh ', '', line.strip())
        if any(re.search(p, args) for p in PERF_JOBS):
            found.append(line.strip())
    return found


def perf_processes(lines=None, patterns=None):
    """Running perf jobs as (pid, seconds running, args), from `ps -eo pid=,etimes=,args=`."""
    patterns = PERF_JOBS if patterns is None else patterns
    if lines is None:
        lines = subprocess.run(['ps', '-eo', 'pid=,etimes=,args='], capture_output=True, text=True).stdout.splitlines()
    found = []
    for line in lines:
        parts = line.strip().split(None, 2)
        args = re.sub(r'^(/usr/bin/env )?(ba)?sh ', '', parts[2].strip()) if len(parts) == 3 else ''
        if len(parts) == 3 and parts[0].isdigit() and parts[1].isdigit() and int(parts[0]) != os.getpid() \
                and any(re.search(p, args) for p in patterns):
            found.append((int(parts[0]), int(parts[1]), parts[2]))
    return found


def qa_processes(lines=None):
    """Running qa.sh processes as (pid, seconds running, args)."""
    return perf_processes(lines, QA_JOBS)


def perf_wanted(lock_dir):
    """The pid in perf.wanted while that process runs: a perf job (qa.sh's perf stage) waiting
    for the specs already running to end, so that no new one starts (flock alone would let new
    shared holders in ahead of it)."""
    try:
        pid = int(open(os.path.join(lock_dir, 'perf.wanted')).read().split()[0])
    except (OSError, ValueError, IndexError):
        return None
    return pid if os.path.exists(f'/proc/{pid}') else None


@contextlib.contextmanager
def machine_lock(lock_dir=None, perf_wait=True, log=say, max_wait=None):
    """Shared hold of perf.lock for a spec run, after any perf job has finished: a perf job that
    takes the lock exclusively, or one ps shows, keeps a spec from starting. The wait says what
    it waits for, and gives up after MAX_WAIT seconds."""
    lock_dir = lock_dir or shared_dir()
    os.makedirs(lock_dir, exist_ok=True)
    path = os.path.join(lock_dir, 'perf.lock')
    f = open(path, 'a+')
    waiter = Waiter('a perf job to finish', log, max_wait)
    try:
        while True:
            jobs = perf_processes() if perf_wait else []
            wanted = perf_wanted(lock_dir) if perf_wait else None
            if wanted:
                reason, key = f'pid {wanted} waits to run a perf job ({path.replace("perf.lock", "perf.wanted")})', 'wanted'
            elif not jobs:
                try:
                    fcntl.flock(f, fcntl.LOCK_SH | fcntl.LOCK_NB)
                    break
                except OSError:
                    reason, key = f'a perf job holds {path} exclusively', 'flock'
            else:
                pid, seconds, args = jobs[0]
                more = f' (and {len(jobs) - 1} more)' if len(jobs) > 1 else ''
                reason, key = f'pid {pid}, running {seconds // 60} min: {args[:160]}{more}', pid
            waiter.tick(reason, key)
            time.sleep(20)
        if waiter.start is not None:
            log(f'perf wait over after {waiter.waited / 60:.1f} min')
        yield
    finally:
        f.close()


def port_listeners(procs=None):
    """{port: [pid]} of the TCP ports listened on now, from /proc/net/tcp and tcp6 (the
    processes this user can see)."""
    inodes = {}
    for table in ('/proc/net/tcp', '/proc/net/tcp6'):
        with contextlib.suppress(OSError), open(table) as f:
            for line in f.readlines()[1:]:
                fields = line.split()
                if len(fields) > 9 and fields[3] == '0A':  # LISTEN
                    inodes[fields[9]] = int(fields[1].rsplit(':', 1)[1], 16)
    found = {}
    for p in (snapshot() if procs is None else procs):
        with contextlib.suppress(OSError):
            for fd in os.listdir(f'/proc/{p["pid"]}/fd'):
                with contextlib.suppress(OSError):
                    link = os.readlink(f'/proc/{p["pid"]}/fd/{fd}')
                    if link.startswith('socket:[') and link[8:-1] in inodes:
                        found.setdefault(inodes[link[8:-1]], set()).add(p['pid'])
    return {port: sorted(pids) for port, pids in found.items()}


def flock_holders(path):
    """[(pid, 'shared' or 'exclusive')] holding flocks on PATH, from /proc/locks."""
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


def ago(seconds):
    return f'{seconds / 3600:.1f} h' if seconds >= 5400 else f'{seconds // 60:.0f} min'


def boot_time():
    with contextlib.suppress(OSError, StopIteration, ValueError):
        with open('/proc/stat') as f:
            return int(next(line for line in f if line.startswith('btime')).split()[1])
    return 0


BOOT_TIME = boot_time()


def proc_age(p):
    """Seconds since process P (read_proc) started."""
    return time.time() - (BOOT_TIME + p['start'] / os.sysconf('SC_CLK_TCK'))


def compress_numbers(numbers):
    """[1, 2, 3, 5] as '1-3, 5'."""
    parts = []
    for n in sorted(numbers):
        if parts and parts[-1][1] == n - 1:
            parts[-1][1] = n
        else:
            parts.append([n, n])
    return ', '.join(f'{a}' if a == b else f'{a}-{b}' for a, b in parts)


def known_lanes(lock_dir):
    """FIRST_LANE..LAST_LANE and every lane with a lock file."""
    numbers = set(range(FIRST_LANE, LAST_LANE + 1))
    for name in os.listdir(lock_dir) if os.path.isdir(lock_dir) else []:
        match = re.fullmatch(r'lane-(\d+)\.lock', name)
        if match:
            numbers.add(int(match.group(1)))
    return sorted(numbers)


def status(lock_dir=None, procs=None):
    """What the machine's runners hold and wait for, and what dead ones left (--status), as lines:
    the first thing to look at when a run waits."""
    lock_dir = lock_dir or shared_dir()
    procs = snapshot() if procs is None else procs
    by_pid = {p['pid']: p for p in procs}
    listeners = port_listeners(procs)
    scopes = test_scopes()

    def who(pid):
        p = by_pid.get(pid)
        if not p:
            return f'pid {pid} (gone)'
        owner = p['env'].get(OWNER_ENV)
        mark = f', runner {owner.split(":")[0]} {"alive" if tag_alive(owner) else "GONE"}' if owner else ''
        return f'pid {pid} {p["comm"]} ({ago(proc_age(p))} old{mark})'

    def holder_alive(record):
        return tag_alive(f'{record.get("pid")}:{record.get("start")}')

    lines = [f'lock directory {lock_dir}']
    ports, displays = reservations(lock_dir)
    lines.append(f'reserved: {len(ports)} port(s), {len(displays)} display(s)')
    held = {s['lane']: s for s in lane_status(lock_dir)}
    free, cleanable = [], 0
    for number in known_lanes(lock_dir):
        mcp, driver = lane_ports(number)
        display = lane_display(number)
        head = f'lane {number} (:{mcp}, :{driver}, {display})'
        busy = [f'port {port}: {", ".join(who(pid) for pid in listeners.get(port, [])) or "open"}'
                for port in (mcp, driver) if port in listeners]
        lock_pid = x_lock_pid(display[1:])
        if lock_pid or display_ready(display[1:]):
            busy.append(f'display {display}: ' + (who(lock_pid) if lock_pid else 'a server answers'))
        if number in held:
            s = held[number]
            lines.append(f'{head}: HELD by pid {s["pid"]} ({"alive" if holder_alive(s) else "gone; a child keeps its lock"}) '
                         f'in {s["root"]} since {s["since"]}: {s.get("state") or "?"}')
            for c in s.get('children') or []:
                p = by_pid.get(c['pid'])
                lines.append(f'    {c["role"]}: pid {c["pid"]} ({"alive" if p and p["start"] == c["start"] else "gone"})'
                             + (f', scope {c["scope"]}' if c.get('scope') else ''))
            lines += [f'    {b}' for b in busy]
            continue
        record = {}
        path = os.path.join(lock_dir, f'lane-{number}.lock')
        if os.path.exists(path):
            with open(path) as f:
                record = read_record(f)
        found = lane_leftovers(number, record, lock_dir, procs=procs, scopes=scopes)
        if not busy and not found:
            free.append(number)
            continue
        last = (f'last held by pid {record["pid"]} ({"alive" if holder_alive(record) else "gone"}) since '
                f'{record.get("since")}: {record.get("state") or "?"}') if record.get('pid') else 'never held'
        lines.append(f'{head}: FREE but busy; {last}')
        lines += [f'    {b}' for b in busy]
        lines += [f'    would clean: {describe_leftover(item)}' for item in found]
        cleanable += len(found)
    lines.append('free lanes: ' + (compress_numbers(free) or 'none'))
    if cleanable:
        lines.append(f'    {cleanable} leftover(s) of dead runners: the next runner to take those lanes cleans them; '
                     'scripts/e2e.py --clean does now')
    # perf.lock, perf.wanted and who waits on them.
    holders = flock_holders(os.path.join(lock_dir, 'perf.lock'))
    exclusive = [pid for pid, mode in holders if mode == 'exclusive']
    shared = [pid for pid, mode in holders if mode == 'shared']
    lines.append('perf.lock: ' + (f'held EXCLUSIVELY by {who(exclusive[0])}' if exclusive else
                                  f'held shared by {len(shared)} spec run(s) (pids {", ".join(map(str, shared))})'
                                  if shared else 'free'))
    try:
        with open(os.path.join(lock_dir, 'perf.wanted')) as f:
            wanted = int(f.read().split()[0])
    except (OSError, ValueError, IndexError):
        wanted = None
    if wanted:
        p = by_pid.get(wanted)
        lines.append(f'perf.wanted: {who(wanted)}' + (f': {" ".join(p["args"])[:140]}' if p else ' (stale: ignored)'))
        if p:
            waiting = [n for n, s in held.items() if 'perf job' in (s.get('state') or '')]
            lines.append(f'    new spec runs wait for it; lanes waiting now: {compress_numbers(waiting) or "none"}')
    # Runners, their watchers and qa.sh.
    for p in procs:
        script = p['args'][1] if len(p['args']) > 1 else ''
        if p['pid'] == os.getpid() or not (re.search(r'(^|/)e2e\.py$', script) or re.search(r'(^|/)qa\.sh$', script)):
            continue
        if '--reap-after' in p['args']:
            lines.append(f'watcher: pid {p["pid"]} for runner {p["args"][p["args"].index("--reap-after") + 1]}')
        elif script.endswith('qa.sh'):
            lines.append(f'qa.sh: pid {p["pid"]}, {ago(proc_age(p))}: {" ".join(p["args"])[:140]}')
        elif '--status' not in p['args']:
            lanes = [n for n, s in held.items() if s.get('pid') == p['pid']]
            what = f'lanes {compress_numbers(lanes)}' if lanes else 'no lane (waiting for one, or between jobs)'
            lines.append(f'runner: pid {p["pid"]}, {ago(proc_age(p))}, {what}: {" ".join(p["args"])[:140]}')
    budget = Budget(lock_dir)
    entries = budget.entries()
    lines.append(f'players: {sum(e["gb"] for e in entries):g} of {budget.gb:g} GB, {len(entries)} of {budget.players}')
    lines += [f'    {e["label"]}: {e["gb"]:g} GB, pid {e["pid"]} in {e["root"]} since {e["since"]}' for e in entries]
    for pid, seconds, args in perf_processes():
        lines.append(f'perf job: pid {pid}, running {seconds // 60} min: {args[:160]}')
    for unit in scopes:
        owner = scope_owner(unit)
        if owner:
            lines.append(f'scope {unit}: runner {owner[0].split(":")[0]} {"alive" if tag_alive(owner[0]) else "GONE"}')
    # X servers whose launcher has gone (an xvfb-run's, a runner's): nothing cleans those that are
    # not on a lane's display, so they are only shown.
    for p in procs:
        parent = by_pid.get(p['ppid'])
        # Orphans go to init, the user's systemd or, under WSL, the session's /init relay.
        if p['comm'] == 'Xvfb' and (p['ppid'] <= 1 or not parent or parent['comm'] == 'systemd'
                                    or parent['comm'].startswith('Relay(') or parent['args'][:1] == ['/init']):
            lines.append(f'X server without its launcher: pid {p["pid"]}, {ago(proc_age(p))} old: {" ".join(p["args"])[:100]}')
    return lines


def clean(lock_dir=None, log=say):
    """--clean: what dead runners left on every lane no runner holds (lane_leftovers), cleaned."""
    lock_dir = lock_dir or shared_dir()
    cleaned = 0
    for number in known_lanes(lock_dir):
        path = os.path.join(lock_dir, f'lane-{number}.lock')
        with open(path, 'a+') as f:
            try:
                fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                continue  # a live runner holds it
            try:
                record = read_record(f)
                found = lane_leftovers(number, record, lock_dir)
                if found:
                    clean_leftovers(found, log=lambda s: log(f'lane {number}: {s}'))
                    cleaned += len(found)
                    if record:
                        record.pop('children', None)
                        record['state'] = 'cleaned'
                        write_record(f, record)
            finally:
                fcntl.flock(f, fcntl.LOCK_UN)
    log(f'cleaned {cleaned} leftover(s)')
    return cleaned


class JobQueue:
    """Spec runs in order; a job starts once the jobs it depends on are done (ours after the
    original's runs of the same spec, whose evidence it is compared with)."""

    def __init__(self, jobs):
        self.jobs = list(jobs)
        self.taken, self.finished = set(), set()
        self.condition = threading.Condition()

    def next(self):
        with self.condition:
            while True:
                pending = [j for j in self.jobs if j['id'] not in self.taken]
                if not pending:
                    return None
                for job in pending:
                    if all(d in self.finished for d in job['after']):
                        self.taken.add(job['id'])
                        return job
                self.condition.wait()

    def untaken(self):
        """Whether a job is left that no slot has taken."""
        with self.condition:
            return any(j['id'] not in self.taken for j in self.jobs)

    def done(self, job):
        with self.condition:
            self.finished.add(job['id'])
            self.condition.notify_all()


def plan_jobs(specs, kinds, repeat):
    jobs = []
    for spec in specs:
        originals = []
        for kind in kinds:
            for index in range(1, repeat + 1):
                job = {'id': f'{spec["name"]}/{kind}-{index}', 'spec': spec, 'kind': kind, 'index': index,
                       'after': list(originals) if kind == 'ours' else []}
                if kind == 'original':
                    originals.append(job['id'])
                jobs.append(job)
    return jobs


# ---------------------------------------------------------------- OniMcp

def why_failed(value):
    """The error, reason and message texts nested anywhere in an OniMcp result, JSON in strings
    included: the failure's summary is cut short, and a build's own reason sits several levels
    down, in a JSON text."""
    if isinstance(value, str) and value.startswith('{'):
        try:
            value = json.loads(value)
        except ValueError:
            return
    if isinstance(value, dict):
        for key, item in value.items():
            if key in ('error', 'reason', 'message', 'detail', 'why') and isinstance(item, str) \
                    and not item.startswith('{'):
                yield item
            else:
                yield from why_failed(item)
    elif isinstance(value, list):
        for item in value:
            yield from why_failed(item)


class Mcp:
    """A minimal client for OniMcp's streamable HTTP endpoint (JSON text results)."""

    # OniMcp from 2026-10-23 accepts only this version; earlier builds also take 2025-06-18.
    PROTOCOL = '2025-11-25'

    def __init__(self, port):
        self.port, self.url = port, f'http://127.0.0.1:{port}/mcp/'
        self.session = None
        self.version = None  # the protocol version initialize agreed on, sent with later requests
        self.on_reply = None  # a player's Watchdog.progress

    def post(self, body):
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
        if self.session:
            headers['Mcp-Session-Id'] = self.session
        if self.version:
            headers['MCP-Protocol-Version'] = self.version
        request = urllib.request.Request(self.url, data=json.dumps(body).encode(), headers=headers)
        with urllib.request.urlopen(request, timeout=120) as response:
            self.session = response.headers.get('Mcp-Session-Id') or self.session
            raw = response.read().decode()
        if self.on_reply:
            self.on_reply()
        if raw.startswith('event:') or raw.startswith('data:') or '\ndata:' in raw:
            data = [line[5:].strip() for line in raw.splitlines() if line.startswith('data:')]
            raw = data[-1] if data else '{}'
        return json.loads(raw) if raw.strip() else {}

    def connect(self):
        self.session = self.version = None
        reply = self.post({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
            'protocolVersion': self.PROTOCOL, 'capabilities': {}, 'clientInfo': {'name': 'e2e', 'version': '1'}}})
        self.version = reply.get('result', {}).get('protocolVersion') or self.PROTOCOL
        self.post({'jsonrpc': '2.0', 'method': 'notifications/initialized'})

    @staticmethod
    def parse(result):
        if 'error' in result:
            raise SpecFailure('MCP error ' + json.dumps(result['error']))
        body = result.get('result', {})
        parts = body.get('content') or body.get('contents') or []
        text = '\n'.join(p.get('text', '') for p in parts)
        try:
            value = json.loads(text)
        except ValueError:
            value = text
        return value, bool(body.get('isError'))

    def call(self, tool, **args):
        args.setdefault('task', 'e2e spec')
        value, error = self.parse(self.post({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call',
                                             'params': {'name': tool, 'arguments': args}}))
        if error:
            why = '; '.join(dict.fromkeys(why_failed(value)))[:600]
            raise SpecFailure(f'{tool} failed: {str(value)[:600]}' + (f' ... why: {why}' if why else ''))
        return value

    def read(self, uri):
        return self.parse(self.post({'jsonrpc': '2.0', 'id': 3, 'method': 'resources/read', 'params': {'uri': uri}}))[0]


# ---------------------------------------------------------------- E2eDriver

class DriverError(SpecFailure):
    """A request E2eDriver answered with ok: false (or could not answer); RESULT is what it
    still sent, such as a ui.wait timeout's last state."""

    def __init__(self, message, result=None):
        super().__init__(message)
        self.result = result


class Element:
    """One element of a ui.find or ui.wait reply (tools/E2eDriver/README.md, "Element
    description"): its path, screen rect (x, y, w, h; pixels, origin top-left), centre, state, and
    the top raycast hit at its centre, with that hit's relation to the element."""

    def __init__(self, raw):
        self.raw = raw
        self.path = raw.get('path', '')
        rect = raw.get('rect')
        self.rect = (float(rect['x']), float(rect['y']), float(rect['w']), float(rect['h'])) if rect else None
        self.active = raw.get('active', True)
        self.onscreen = raw.get('onscreen')
        # A button inside a non-interactable CanvasGroup takes no clicks either.
        interactable = raw.get('interactable')
        self.interactable = None if interactable is None else bool(interactable and raw.get('groupInteractable', True))
        self.is_on = raw.get('isOn')
        self.state = raw.get('state')
        self.text = raw.get('text')
        hit = raw.get('hit') if isinstance(raw.get('hit'), dict) else {}
        self.hit, self.relation, self.hits = hit.get('path'), hit.get('relation'), hit.get('all') or []

    @property
    def center(self):
        center = self.raw.get('center')
        if center:
            return int(round(center['x'])), int(round(center['y']))
        x, y, w, h = self.rect
        return int(round(x + w / 2)), int(round(y + h / 2))

    def hit_is_self(self):
        """The top raycast hit is this element or one of its children: a click lands on it."""
        return self.relation in ('self', 'child')


class Driver:
    """E2eDriver's client (tools/E2eDriver/README.md): JSON lines over TCP on
    127.0.0.1:E2E_DRIVER_PORT, handled on the game's main thread one per frame. A request is
    {"id": N, "cmd": "ui.find", "args": {...}}; a reply {"id": N, "ok": true, "frame": F,
    "result": ...} or {"id": N, "ok": false, "frame": F, "error": "...", "result"?: ...}."""

    def __init__(self, port, host='127.0.0.1', timeout=60):
        self.address, self.timeout = (host, port), timeout
        self.sock = self.reader = None
        self.next_id = 0
        self.frame = None  # the game frame of the last reply
        self.lock = threading.Lock()
        self.on_reply = None  # a player's Watchdog.progress

    def connect(self, deadline=60, alive=lambda: True):
        end = time.time() + deadline
        while True:
            try:
                self.sock = socket.create_connection(self.address, timeout=self.timeout)
                self.reader = self.sock.makefile('r', encoding='utf-8', newline='\n')
                return self
            except OSError:
                if time.time() > end or not alive():
                    raise DriverError(f'E2eDriver is not listening on port {self.address[1]}')
                time.sleep(1)

    def close(self):
        if self.sock:
            with contextlib.suppress(OSError):
                self.reader.close()
                self.sock.close()
        self.sock = self.reader = None

    def request(self, cmd, timeout=None, **args):
        if not self.sock:
            raise DriverError('E2eDriver is not connected')
        with self.lock:
            self.next_id += 1
            ident = self.next_id
            line = json.dumps({'id': ident, 'cmd': cmd, 'args': args}) + '\n'
            try:
                self.sock.settimeout(timeout or self.timeout)
                self.sock.sendall(line.encode())
                while True:
                    reply = self.reader.readline()
                    if not reply:
                        raise DriverError(f'E2eDriver closed the connection during {cmd}')
                    reply = json.loads(reply)
                    # A malformed line is answered with id null; any other id is not ours.
                    if reply.get('id') in (ident, None):
                        break
            except (OSError, ValueError) as e:
                raise DriverError(f'E2eDriver {cmd}: {e}')
        self.frame = reply.get('frame', self.frame)
        if self.on_reply:
            self.on_reply()
        if not reply.get('ok'):
            raise DriverError(f'E2eDriver {cmd} {json.dumps(args)[:300]}: {reply.get("error")}', reply.get('result'))
        return reply.get('result')

    # The commands of the README.
    def ping(self):
        return self.request('ping')

    def find(self, path=None, text=None, type=None, under=None, inactive=False, limit=None, hits=None):
        query = {k: v for k, v in (('path', path), ('text', text), ('type', type), ('under', under),
                                   ('limit', limit), ('hits', hits)) if v is not None}
        if inactive:
            query['inactive'] = True
        return [Element(e) for e in self.request('ui.find', **query)['elements']]

    def find_one(self, query, index=None):
        index = query.get('index', 0) if index is None else index
        found = self.find(path=query.get('ui'), text=query.get('text'), type=query.get('type'),
                          under=query.get('under'), limit=max(50, index + 1))
        what = query.get('ui') or f'text {query.get("text")!r}' + (f' under {query["under"]}' if query.get('under') else '')
        if not found:
            raise SpecFailure(f'no active element {what}')
        if index >= len(found):
            raise SpecFailure(f'{what}: {len(found)} match(es), wanted index {index}')
        if found[index].rect is None:
            raise SpecFailure(f'{what}: E2eDriver gave no rect')
        return found[index]

    def count(self, path):
        """How many active elements match PATH."""
        return self.request('ui.find', path=path, limit=0, hit=False)['count']

    def at(self, x, y, hits=8):
        """The raycast at a screen point: {path, relation, count, all: [...]}."""
        return self.request('ui.at', x=x, y=y, hits=hits)

    def dump(self, root):
        return ''.join(line + '\n' for line in self.request('ui.dump', root=root)['lines'])

    def dump_settled(self, root, frames=BUILD_MENU_FRAMES):
        """ROOT's subtree as dump() gives it, after waiting up to FRAMES frames for the build
        menu to settle, in the frame it is found settled: (the text, the settle check)."""
        result = self.request('ui.dump', timeout=max(self.timeout, frames), root=root, settle=frames)
        return ''.join(line + '\n' for line in result['lines']), result.get('settle')

    def wait(self, path, cond='exists', frames=300, **more):
        # The driver holds its queue while it waits: a slow player may take a while.
        return self.request('ui.wait', timeout=max(self.timeout, frames), path=path, cond=cond, frames=frames, **more)

    def build_menu_settled(self, frames=BUILD_MENU_FRAMES):
        """Waits up to FRAMES frames for the build menu to show what the game would compute for
        it now (buildmenu.settled); the check's result."""
        return self.request('buildmenu.settled', timeout=max(self.timeout, frames), frames=frames)

    def to_screen(self, cell, pos=False):
        """Where a cell's centre (or, with POS, the world position [x, y]) is on screen."""
        result = self.request('world.to_screen', **{'pos' if pos else 'cell': list(cell)})
        if result.get('onscreen') is False:
            raise SpecFailure(f'{"position" if pos else "cell"} {list(cell)} is off screen (at {result["x"]}, {result["y"]})')
        return result['x'], result['y']

    def to_cell(self, x, y):
        result = self.request('screen.to_cell', x=x, y=y)
        return tuple(result['xy']) if result.get('valid') else None

    def state(self, expr):
        return self.request('state.get', expr=expr)['value']

    def each(self, expr, members):
        """EXPR's items, each a list of its MEMBERS' values (state.each), in one request."""
        return self.request('state.each', timeout=max(self.timeout, 600), expr=expr, members=members)['items']

    def camera_get(self):
        return self.request('camera.get')

    def camera_set(self, **args):
        return self.request('camera.set', **args)

    def canvas(self, hide):
        return self.request('canvas.hide' if hide else 'canvas.show')

    def setup_call(self, **args):
        return self.request('setup.call', **args)

    def seed(self, n):
        return self.request('seed', n=n)

    def frames(self, n):
        """Lets N frames pass: each request is answered in its own frame's Update."""
        for _ in range(n):
            self.request('ping')


# ---------------------------------------------------------------- XTEST input

# KKeyCode names (the game's key enum) as X keysym names, US layout (Xvfb's default keymap).
KKEYCODES = {
    'Escape': 'Escape', 'Return': 'Return', 'Space': 'space', 'Tab': 'Tab', 'Backspace': 'BackSpace',
    'Delete': 'Delete', 'Insert': 'Insert', 'Home': 'Home', 'End': 'End', 'PageUp': 'Prior', 'PageDown': 'Next',
    'UpArrow': 'Up', 'DownArrow': 'Down', 'LeftArrow': 'Left', 'RightArrow': 'Right',
    'LeftShift': 'Shift_L', 'RightShift': 'Shift_R', 'LeftControl': 'Control_L', 'RightControl': 'Control_R',
    'LeftAlt': 'Alt_L', 'RightAlt': 'Alt_R', 'CapsLock': 'Caps_Lock', 'Numlock': 'Num_Lock',
    'ScrollLock': 'Scroll_Lock', 'Pause': 'Pause', 'Print': 'Print',
    'Comma': 'comma', 'Period': 'period', 'Slash': 'slash', 'Backslash': 'backslash', 'Semicolon': 'semicolon',
    'Quote': 'apostrophe', 'BackQuote': 'grave', 'LeftBracket': 'bracketleft', 'RightBracket': 'bracketright',
    'Minus': 'minus', 'Equals': 'equal',
    'KeypadPeriod': 'KP_Decimal', 'KeypadDivide': 'KP_Divide', 'KeypadMultiply': 'KP_Multiply',
    'KeypadMinus': 'KP_Subtract', 'KeypadPlus': 'KP_Add', 'KeypadEnter': 'KP_Enter', 'KeypadEquals': 'KP_Equal',
    **{chr(c): chr(c).lower() for c in range(ord('A'), ord('Z') + 1)},
    **{f'Alpha{d}': str(d) for d in range(10)},
    **{f'Keypad{d}': f'KP_{d}' for d in range(10)},
    **{f'F{n}': f'F{n}' for n in range(1, 16)},
}
MODIFIERS = {'shift': 'Shift_L', 'ctrl': 'Control_L', 'control': 'Control_L', 'alt': 'Alt_L'}
_KEYS_LOWER = {k.lower(): v for k, v in KKEYCODES.items()}
# XTEST's pointer in Xvfb has ten buttons; a higher one goes as a sent event (X11Input.send_button).
XTEST_BUTTONS = 10


class XButtonEvent(ctypes.Structure):
    """Xlib's XButtonEvent, padded to an XEvent (24 longs)."""
    _fields_ = [('type', ctypes.c_int), ('serial', ctypes.c_ulong), ('send_event', ctypes.c_int),
                ('display', ctypes.c_void_p), ('window', ctypes.c_ulong), ('root', ctypes.c_ulong),
                ('subwindow', ctypes.c_ulong), ('time', ctypes.c_ulong), ('x', ctypes.c_int), ('y', ctypes.c_int),
                ('x_root', ctypes.c_int), ('y_root', ctypes.c_int), ('state', ctypes.c_uint),
                ('button', ctypes.c_uint), ('same_screen', ctypes.c_int), ('pad', ctypes.c_long * 12)]


# X11 button numbers; 4 to 7 are the wheel, 8 and up the side buttons (back 8, forward 9).
BUTTONS = {'left': 1, 'middle': 2, 'right': 3, 'back': 8, 'forward': 9}


def button_problem(value):
    """Why a step's `button` is not one, or None: a name in BUTTONS, or an X button number 1 to
    3 or 8 to 11. Xvfb's XTEST pointer has ten buttons (an 11th is a BadValue, which ends the
    runner): button 11 is sent to the window under the pointer instead (send_button)."""
    if 'button' not in value:
        return None
    b = value['button']
    if b in BUTTONS or (isinstance(b, int) and not isinstance(b, bool) and (1 <= b <= 3 or 8 <= b <= 11)):
        return None
    return '`button` is left, middle, right, back, forward or an X button 1-3 or 8-11'


def keysym_name(name):
    """A KKeyCode name (or a letter, digit or modifier) as an X keysym name, or None."""
    name = str(name).strip()
    if re.fullmatch(r'\d', name):
        return name
    return MODIFIERS.get(name.lower()) or _KEYS_LOWER.get(name.lower())


def parse_key(code):
    """"Shift+B" as ([modifier keysyms], key keysym); a lone modifier is the key itself."""
    parts = [p for p in re.split(r'\s*\+\s*', str(code).strip()) if p]
    if not parts:
        raise SpecError(f'empty key {code!r}')
    names = []
    for part in parts:
        name = keysym_name(part)
        if name is None:
            raise SpecError(f'unknown key {part!r} in {code!r}')
        names.append(name)
    for part in parts[:-1]:
        if part.lower() not in MODIFIERS and keysym_name(part) not in MODIFIERS.values():
            raise SpecError(f'{part!r} in {code!r} is not a modifier (Shift, Ctrl, Alt)')
    return names[:-1], names[-1]


def key_problems(code):
    try:
        parse_key(code)
        return []
    except SpecError as e:
        return [str(e)]


_xauth = threading.Lock()
# map_state's index in XWindowAttributes read as 32-bit ints on LP64: x, y, width, height,
# border_width, depth (6 ints), visual (pointer: 2), root (long: 2), class, bit_gravity,
# win_gravity, backing_store (4), backing_planes, backing_pixel (longs: 4), save_under (1, padded
# to 2), colormap (long: 2), map_installed (1), map_state.
XWA_MAP_STATE = 6 + 2 + 2 + 4 + 4 + 2 + 2 + 1


class Input:
    """Real input on a display through XTEST (ctypes on libXtst and libX11; nothing to install).
    FRAMES(n) lets n game frames pass (E2eDriver round trips, or a sleep)."""

    def __init__(self, display, xauthority=None, frames=None):
        self.x11 = x11 = ctypes.CDLL('libX11.so.6')
        self.xtst = xtst = ctypes.CDLL('libXtst.so.6')
        x11.XOpenDisplay.argtypes, x11.XOpenDisplay.restype = [ctypes.c_char_p], ctypes.c_void_p
        x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
        x11.XFlush.argtypes = [ctypes.c_void_p]
        x11.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
        x11.XStringToKeysym.argtypes, x11.XStringToKeysym.restype = [ctypes.c_char_p], ctypes.c_ulong
        x11.XKeysymToKeycode.argtypes, x11.XKeysymToKeycode.restype = [ctypes.c_void_p, ctypes.c_ulong], ctypes.c_ubyte
        x11.XkbKeycodeToKeysym.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, ctypes.c_int, ctypes.c_int]
        x11.XkbKeycodeToKeysym.restype = ctypes.c_ulong
        xtst.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        xtst.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        xtst.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        x11.XDefaultRootWindow.argtypes, x11.XDefaultRootWindow.restype = [ctypes.c_void_p], ctypes.c_ulong
        x11.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong),
                                   ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)),
                                   ctypes.POINTER(ctypes.c_uint)]
        x11.XGetWindowAttributes.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_void_p]
        x11.XSetInputFocus.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        x11.XFree.argtypes = [ctypes.c_void_p]
        # libX11 reads XAUTHORITY when it connects; lanes have their own (xvfb-run's), so set it
        # only around the connect.
        with _xauth:
            saved = os.environ.get('XAUTHORITY')
            if xauthority:
                os.environ['XAUTHORITY'] = xauthority
            try:
                self.dpy = x11.XOpenDisplay(display.encode())
            finally:
                if xauthority:
                    if saved is None:
                        os.environ.pop('XAUTHORITY', None)
                    else:
                        os.environ['XAUTHORITY'] = saved
        if not self.dpy:
            raise SpecFailure(f'cannot open display {display}')
        self.display = display
        self.frames = frames or (lambda n: time.sleep(n * FRAME_FALLBACK))
        self.held = []
        self.focused = False

    def focus(self):
        """Gives the keyboard focus to the player's window. Xvfb runs no window manager, so no
        window ever gets it: the original Unity player then counts itself unfocused
        (Application.isFocused false) and drops every key, text included. Mouse input works
        without. The window is the largest viewable top-level one."""
        if self.focused:
            return
        x11 = self.x11
        root, parent = ctypes.c_ulong(), ctypes.c_ulong()
        children, count = ctypes.POINTER(ctypes.c_ulong)(), ctypes.c_uint()
        if not x11.XQueryTree(self.dpy, x11.XDefaultRootWindow(self.dpy), ctypes.byref(root), ctypes.byref(parent),
                              ctypes.byref(children), ctypes.byref(count)):
            return
        best, area = None, 0
        attributes = (ctypes.c_int * 64)()  # XWindowAttributes, read by offset below
        for i in range(count.value):
            if x11.XGetWindowAttributes(self.dpy, children[i], attributes):
                # x, y, width, height, border_width, depth, visual*, root, class, bit_gravity,
                # win_gravity, backing_store, backing_planes, backing_pixel, save_under,
                # colormap, map_installed, map_state: map_state is IsViewable (2) for a shown window.
                width, height = attributes[2], attributes[3]
                map_state = attributes[XWA_MAP_STATE]
                if map_state == 2 and width * height > area:
                    best, area = children[i], width * height
        if count.value:
            x11.XFree(children)
        if best:
            x11.XSetInputFocus(self.dpy, best, 1, 0)  # RevertToPointerRoot, CurrentTime
            x11.XSync(self.dpy, 0)
            self.frames(2)
            self.focused = True

    def close(self):
        if self.dpy:
            for keycode in reversed(self.held):
                self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 0, 0)
            self.x11.XSync(self.dpy, 0)
            self.x11.XCloseDisplay(self.dpy)
            self.dpy = None

    def _flush(self):
        self.x11.XFlush(self.dpy)

    def grab(self, path):
        """The display's whole screen as a PNG at PATH (XGetImage of the root window): what the
        player shows where OniMcp takes no screenshot (the main menu, before a colony exists)."""
        x11 = self.x11
        x11.XGetImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int, ctypes.c_uint,
                                  ctypes.c_uint, ctypes.c_ulong, ctypes.c_int]
        x11.XGetImage.restype = ctypes.c_void_p
        x11.XDefaultScreen.argtypes = [ctypes.c_void_p]
        x11.XDisplayWidth.argtypes = x11.XDisplayHeight.argtypes = [ctypes.c_void_p, ctypes.c_int]
        x11.XSync(self.dpy, 0)
        screen = x11.XDefaultScreen(self.dpy)
        image = x11.XGetImage(self.dpy, x11.XDefaultRootWindow(self.dpy), 0, 0, x11.XDisplayWidth(self.dpy, screen),
                              x11.XDisplayHeight(self.dpy, screen), ctypes.c_ulong(-1).value, 2)  # AllPlanes, ZPixmap
        if not image:
            raise SpecFailure(f'cannot read the screen of {self.display}')
        try:
            # XImage: int width, height, xoffset, format; char *data; int byte_order, bitmap_unit,
            # bitmap_bit_order, bitmap_pad, depth, bytes_per_line, bits_per_pixel; ...
            ints = (ctypes.c_int * 4).from_address(image)
            data = ctypes.c_void_p.from_address(image + 16).value
            depth, stride, bpp = (ctypes.c_int * 7).from_address(image + 24)[4:7]
            width, height = ints[0], ints[1]
            if bpp != 32 or depth < 24:
                raise SpecFailure(f'{self.display}: a {depth}-bit screen at {bpp} bits a pixel')
            from PIL import Image
            pixels = ctypes.string_at(data, stride * height)
            Image.frombuffer('RGB', (width, height), pixels, 'raw', 'BGRX', stride, 1).save(path)
        finally:
            x11.XFree.argtypes = [ctypes.c_void_p]
            x11.XFree(ctypes.c_void_p.from_address(image + 16).value)
            x11.XFree(image)
        return path

    def keycode(self, name):
        keysym = self.x11.XStringToKeysym(name.encode())
        if not keysym and len(name) == 1:
            keysym = ord(name)  # Latin-1 keysyms are their code points
        keycode = self.x11.XKeysymToKeycode(self.dpy, keysym) if keysym else 0
        if not keycode:
            raise SpecFailure(f'no key for {name!r} on {self.display}')
        return keycode, keysym

    # The display's pixels per pixel of the game's screen: Run.update_scale sets them for a
    # spec whose game renders smaller than the display (`scaled_screen`).
    scale = (1.0, 1.0)

    def move(self, x, y):
        """The pointer to X, Y: the game's screen pixels from the top left, which are the
        display's unless the game renders at a smaller size stretched over it (`scale`)."""
        self.xtst.XTestFakeMotionEvent(self.dpy, -1, int(x * self.scale[0]), int(y * self.scale[1]), 0)
        self._flush()

    def button(self, button, down):
        number = BUTTONS.get(button, button)
        if number > XTEST_BUTTONS:
            self.send_button(number, down)
            return
        self.xtst.XTestFakeButtonEvent(self.dpy, number, 1 if down else 0, 0)
        self._flush()

    def send_button(self, number, down):
        """A button past XTEST's ten as an event sent to the window under the pointer
        (XSendEvent: send_event set, as no real device's event has it)."""
        x11 = self.x11
        x11.XQueryPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong, *[ctypes.POINTER(ctypes.c_ulong)] * 2,
                                      *[ctypes.POINTER(ctypes.c_int)] * 4, ctypes.POINTER(ctypes.c_uint)]
        x11.XSendEvent.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_long, ctypes.c_void_p]
        root = x11.XDefaultRootWindow(self.dpy)
        window, x, y, rx, ry = root, 0, 0, 0, 0
        while True:
            r, child = ctypes.c_ulong(), ctypes.c_ulong()
            ints = [ctypes.c_int() for _ in range(4)]
            mask = ctypes.c_uint()
            x11.XQueryPointer(self.dpy, window, ctypes.byref(r), ctypes.byref(child), *[ctypes.byref(i) for i in ints],
                              ctypes.byref(mask))
            rx, ry, x, y = (i.value for i in ints)
            if not child.value:
                break
            window = child.value
        event = XButtonEvent(type=4 if down else 5, send_event=1, display=self.dpy, window=window, root=root,
                             x=x, y=y, x_root=rx, y_root=ry, button=number, same_screen=1)
        mask = (1 << 2) if down else (1 << 3)  # ButtonPressMask, ButtonReleaseMask
        x11.XSendEvent(self.dpy, window, 1, mask, ctypes.byref(event))
        self._flush()

    def key_event(self, name, down):
        self.focus()
        keycode, _ = self.keycode(name)
        self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 1 if down else 0, 0)
        if down:
            self.held.append(keycode)
        elif keycode in self.held:
            self.held.remove(keycode)
        self._flush()

    @contextlib.contextmanager
    def modifiers(self, names):
        names = [keysym_name(n) or n for n in names or []]
        for n in names:
            self.key_event(n, True)
        if names:
            self.frames(2)
        try:
            yield
        finally:
            for n in reversed(names):
                self.key_event(n, False)
            if names:
                self.frames(2)

    def click(self, x, y, button='left', mods=(), double=False):
        """Move, hover two frames, press, two frames, release (§4.3). A double click presses
        twice, a frame apart, so the event system counts them as one double click."""
        with self.modifiers(mods):
            self.move(x, y)
            self.frames(2)
            for press in range(2 if double else 1):
                if press:
                    self.frames(1)
                self.button(button, True)
                self.frames(1 if double else 2)
                self.button(button, False)
            self.frames(2)

    def key(self, code):
        mods, key = parse_key(code)
        with self.modifiers(mods):
            self.key_event(key, True)
            self.frames(2)
            self.key_event(key, False)
            self.frames(2)

    def physical_key(self, keycode, mods=()):
        """Presses and releases the key at X keycode KEYCODE, with MODS held: the key at a
        position, whatever the layout puts on it."""
        self.focus()
        with self.modifiers(mods):
            self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 1, 0)
            self._flush()
            self.frames(2)
            self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 0, 0)
            self._flush()
            self.frames(2)

    def type_text(self, text):
        self.focus()
        for ch in text:
            name = {'\n': 'Return', '\t': 'Tab', ' ': 'space'}.get(ch, ch)
            keycode, keysym = self.keycode(name)
            # A character on a key's shifted level needs Shift held.
            shifted = self.x11.XkbKeycodeToKeysym(self.dpy, keycode, 0, 0) != keysym
            with self.modifiers(['Shift_L'] if shifted else []):
                self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 1, 0)
                self._flush()
                self.frames(1)
                self.xtst.XTestFakeKeyEvent(self.dpy, keycode, 0, 0)
                self._flush()
                self.frames(1)

    def drag(self, start, end, button='left', mods=(), steps=8, hold=0, via=()):
        """Press at START, move through each point of VIA to END, STEPS motion events a frame
        apart on each leg, wait HOLD frames there (the button still down), release."""
        with self.modifiers(mods):
            self.move(*start)
            self.frames(2)
            self.button(button, True)
            self.frames(2)
            prev = start
            for point in [*via, end]:
                for i in range(1, steps + 1):
                    self.move(prev[0] + (point[0] - prev[0]) * i / steps, prev[1] + (point[1] - prev[1]) * i / steps)
                    self.frames(1)
                prev = point
            self.frames(1 + hold)
            self.button(button, False)
            self.frames(2)

    def scroll(self, x, y, notches):
        """Wheel notches at x, y: positive up (zoom in), negative down."""
        self.move(x, y)
        self.frames(2)
        for _ in range(abs(notches)):
            b = 4 if notches > 0 else 5
            self.button(b, True)
            self.button(b, False)
            self.frames(1)


# ---------------------------------------------------------------- the player

def descendants(pid):
    children = {}
    for entry in os.listdir('/proc'):
        if entry.isdigit():
            try:
                with open(f'/proc/{entry}/stat') as f:
                    ppid = int(f.read().rsplit(')', 1)[1].split()[1])
            except (OSError, IndexError, ValueError):
                continue
            children.setdefault(ppid, []).append(int(entry))
    found, todo = [], [pid]
    while todo:
        p = todo.pop()
        for c in children.get(p, []):
            found.append(c)
            todo.append(c)
    return found


def process_environ(pid):
    try:
        with open(f'/proc/{pid}/environ', 'rb') as f:
            items = f.read().split(b'\0')
    except OSError:
        return {}
    return dict(i.decode(errors='replace').partition('=')[::2] for i in items if b'=' in i)


def display_ready(number):
    """Whether X server :NUMBER accepts connections: on its abstract socket (where Xvfb listens
    when /tmp/.X11-unix is not writable, as under WSLg) or its socket file."""
    for address in (f'\0/tmp/.X11-unix/X{number}', f'/tmp/.X11-unix/X{number}'):
        with socket.socket(socket.AF_UNIX) as s:
            try:
                s.connect(address)
                return True
            except OSError:
                pass
    return False


# xvfb-run -a (--xvfb-run) picks the first free display, and two lanes starting at once may pick
# the same: starts are serialised, here and (through xvfb.lock in shared_dir) across runners.
_xvfb_start = threading.Lock()


def translation_only(folder):
    """Whether a mod folder is a translation: a .po file at its root (KMod's Translation
    content, whatever the case of its name) and no assembly anywhere."""
    if not os.path.isdir(folder) or not any(f.lower().endswith('.po') and os.path.isfile(os.path.join(folder, f))
                                            for f in os.listdir(folder)):
        return False
    return not any(f.lower().endswith('.dll') for _, _, files in os.walk(folder) for f in files)


def translation_prefs(mod, language=None):
    """kplayerprefs.yaml's strings: none, the language a translation mod installs (`translation`),
    or one of the game's own (`language`). The Translations screen saves these two when a player
    picks a mod's language, and Localization.Initialize loads that mod's strings.po at the next
    start, whatever the mod's distribution platform (its label id, the Workshop ID of a Local
    mod); for one of the game's own it saves the type Preinstalled and the language's code, and
    the game loads StreamingAssets/strings/strings_preinstalled_CODE.po."""
    if language is not None:
        return f"strings:\n  SelectedLanguageType: Preinstalled\n  SelectedLanguageCode: {language}\n"
    if mod is None:
        return 'strings: {}\n'
    return f"strings:\n  SelectedLanguageType: UGC\n  InstalledLanguage: '{mod}'\n"


PLAYER_NAMES = ('OniMoono', 'OxygenNotInclud')  # /proc comm: ours, the original (15 characters)
_scopes = itertools.count(1)  # numbers this runner's player scopes


def child_marks(lane):
    """What every process a runner starts carries in its environment, so that a teardown, the
    watcher and the next runner on the lane find it (lane_leftovers)."""
    marks = {OWNER_ENV: owner_tag(), 'E2E_LOCK_DIR': shared_dir()}
    if lane is not None:
        marks['E2E_LANE'] = str(lane.number)
    return marks


class Watchdog(threading.Thread):
    """A player's hang watchdog: progress is its log growing or an OniMcp or E2eDriver reply.
    None for E2E_HANG_MIN minutes (default 5; a held player is never hung) is a hang:
    scripts/hang-dump.sh takes its threads' stacks and states into the run's hang folder, and the
    player is killed, so the waits the spec is in end. A step failing while the player has not
    progressed for HANG_ON_FAIL seconds (an OniMcp request timing out at 120 s) takes the same
    diagnostics before the player is stopped."""

    HANG_ON_FAIL = 60

    def __init__(self, player, minutes=None, clock=time.time, dump=None):
        super().__init__(daemon=True)
        self.player, self.clock = player, clock
        self.limit = 60 * float(minutes if minutes is not None else os.environ.get('E2E_HANG_MIN', 5))
        self.last, self.size, self.paused = clock(), None, False
        self.hung = None  # what was found, once diagnostics were taken
        self.lock = threading.Lock()
        self.dump = dump or self.hang_dump
        self.stopping = threading.Event()

    def progress(self, *_):
        self.last = self.clock()

    def stalled(self):
        """Seconds since the last progress (the log is looked at first)."""
        try:
            size = os.path.getsize(self.player.log)
        except OSError:
            size = -1
        if (self.size is not None and size != self.size) or self.paused:
            self.progress()
        self.size = size
        return self.clock() - self.last

    def check(self):
        """One look: True when the player is hung (diagnostics taken, the player killed)."""
        if self.hung or not self.player.alive():
            return bool(self.hung)
        stalled = self.stalled()
        if stalled < self.limit:
            return False
        self.capture(f'no log output or OniMcp/E2eDriver reply for {stalled / 60:.0f} min')
        self.player.kill()
        return True

    def run(self):
        while not self.stopping.wait(10) and not self.check():
            pass

    def stop(self):
        self.stopping.set()

    def capture(self, reason):
        with self.lock:
            if self.hung:
                return self.hung
            folder = os.path.join(self.player.out, 'hang')
            pid = self.player.game_pid()
            summary = self.dump(pid, folder) if pid else 'no player process found'
            self.hung = f'the player hung ({reason}); diagnostics in {folder}: {summary}'
            return self.hung

    def hang_dump(self, pid, folder):
        result = subprocess.run([os.path.join(ROOT, 'scripts', 'hang-dump.sh'), str(pid), folder, self.player.log],
                                capture_output=True, text=True)
        return (result.stdout.splitlines() or ['hang-dump.sh printed nothing'])[0]


def keyboard_server_options(keyboard):
    """The X server's options for a spec with a `keyboard`. A server resets its keymap when its
    last client leaves, as setxkbmap does: -noreset keeps the layout until the player connects.
    Keys pressed by keycode are held for frames, which at a colony's frame rate outlast the
    default autorepeat delay (660 ms): -ardelay puts it out of reach, so each press types one
    character in both players."""
    return ['-noreset', '-ardelay', '30000'] if keyboard else []


def dpi_server_options(dpi):
    """The X server's options for a spec with a `display_dpi`: -dpi gives the X screen the
    millimetre size of that many dots per inch (DisplayWidthMM). Xvfb's RandR output still
    reports no size (0x0 mm), whatever the -dpi."""
    return ['-dpi', str(dpi)] if dpi else []


def set_keyboard(display, xauthority, keyboard):
    """KEYBOARD, an XKB layout, on DISPLAY (a lane's, never the desktop's)."""
    if not keyboard:
        return
    env = dict(os.environ, DISPLAY=display)
    if xauthority:
        env['XAUTHORITY'] = xauthority
    layout, _, variant = keyboard.partition('(')
    command = ['setxkbmap', '-display', display, layout, *(['-variant', variant.rstrip(')')] if variant else [])]
    done = subprocess.run(command, env=env, capture_output=True, text=True)
    if done.returncode:
        raise SpecFailure(f'setxkbmap {keyboard} on {display} failed: {done.stderr.strip()}')


class Player:
    """One player process on its own display, with a scratch profile holding OniMcp, E2eDriver,
    the spec's mods and options, and a fixture."""

    def __init__(self, kind, out, env, fixture, lane, mods=(), options=None, driver=False, expansion1=True,
                 translation=None, language=None, keyboard=None, tweaks_menu=False, blocked_plugins=None,
                 data_collection=False, extra_saves=(), display_dpi=None, workshop=None, update_check=None,
                 stray_plugins=(), game_tweaks=(), extra_env=None):
        self.kind, self.out, self.env, self.lane = kind, out, env, lane
        self.game_tweaks = list(game_tweaks)
        self.extra_env = dict(extra_env or {})
        self.blocked_plugins = dict(blocked_plugins or {})
        self.stray_plugins = list(stray_plugins or ())
        self.keyboard = keyboard
        self.display_dpi = display_dpi
        self.tweaks_menu = tweaks_menu
        self.update_check = update_check
        self.home = os.path.join(out, 'home')
        self.profile = os.path.join(self.home, *PROFILE)
        self.log = os.path.join(out, 'player.log' if kind == 'original' else 'stdout.txt')
        self.save_dir = os.path.join(self.profile, 'save_files', 'e2e')
        self.fixture = fixture and os.path.join(self.save_dir, os.path.basename(fixture))
        self.display = self.xauthority = None
        self.process = self.xvfb = self.scope = None
        self.watchdog = Watchdog(self)
        shutil.rmtree(out, ignore_errors=True)
        os.makedirs(os.path.join(self.profile, 'mods', 'Local'))
        os.makedirs(self.save_dir)
        if fixture:
            shutil.copy(fixture, self.fixture)
        # `extra_saves`: more colonies in the profile, each in a folder of its own as the game
        # keeps them (save_files/NAME/NAME.sav), with its preview (NAME.png) when the fixture has one.
        for name in extra_saves:
            folder = os.path.join(self.profile, 'save_files', name)
            os.makedirs(folder, exist_ok=True)
            for ext in ('.sav', '.png'):
                source = os.path.join(FIXTURES, name + ext)
                if os.path.exists(source):
                    shutil.copy(source, os.path.join(folder, name + ext))
                elif ext == '.sav':
                    raise SpecFailure(f'no fixture {source} (extra_saves)')
        # E2E_CLOUD_SAVES=DIR: the profile's cloud saves (the Steam double's user) are DIR's tree,
        # copied as hard links (DIR on the same disk), for a look at the Load screen with many
        # saves. Never a real saves folder: the game may write and delete there.
        cloud = os.environ.get('E2E_CLOUD_SAVES')
        if cloud:
            target = os.path.join(self.profile, 'cloud_save_files', '76561197960265729')
            os.makedirs(os.path.dirname(target), exist_ok=True)
            subprocess.run(['cp', '-al', '--', cloud, target], check=True)
        self.workshop, self.workshop_env = workshop, {}
        self.mods = self.install_mods(list(mods), driver)
        self.option_files = write_options(self.profile, self.mods, options or {})
        # OniMcp's options: the lane's port, JSON tool text, and none of its own gameplay policies
        # (it turns the global auto-disinfect off and dismisses tutorial notifications by default).
        with open(os.path.join(self.profile, 'OniMcpConfig.json'), 'w') as f:
            json.dump({'SecurityMigrationVersion': 1, 'Host': 'localhost', 'Port': lane.mcp_port, 'AuthEnabled': False,
                       'GlobalAutoDisinfectDisabled': False, 'AutoDismissTutorialNotifications': False,
                       'ToolTextFormat': 'json'}, f)
        with open(os.path.join(self.profile, 'kplayerprefs.yaml'), 'w') as f:
            f.write(translation_prefs(translation, language) + f'ints:\n  ResolutionWidth: {WIDTH}\n  ResolutionHeight: {HEIGHT}\n'
                    '  RefreshRateNumerator: 60\n  RefreshRateDenominator: 1\n  FullScreen: 0\n'
                    # DlcManager reads Spaced Out as on unless this says 0.
                    + ('' if expansion1 else '  EXPANSION1_ID.ENABLED: 0\n')
                    + (f'floats:\n  UIScalePref: {float(UI_SCALE)}\n' if UI_SCALE else 'floats: {}\n'))
        if not data_collection:
            # The Data Collection option off (KPrivacyPrefs) unless the spec says
            # `data_collection: true`: no test run submits a crash report to Klei's server (a
            # worldgen failure did). The Supply Closet and locker screens read the option, so their
            # specs turn it on, and must not provoke errors.
            agreements = os.path.join(self.profile, 'Agreements', 'OxygenNotIncluded')
            os.makedirs(agreements)
            with open(os.path.join(agreements, 'kprivacyprefs.yaml'), 'w') as f:
                f.write('disableDataCollection: true\n')

    def install_mods(self, ids, driver):
        """The spec's Workshop mods as Local mods (ws-mods.py profile, which installs them only
        once the mod gate passes them; one it skips fails the run), then OniMcp and E2eDriver;
        returns {folder: staticID}. A translation (translation_only) is installed and left
        disabled, as a subscription leaves a new mod: KMod loads no content of an enabled
        translation (its Mod.LoadTranslations returns false), so it would mark an enabled one as
        failed and disable it, with a load error dialog at the main menu. Its language is picked
        on the Translations screen, or by the spec's `translation` (translation_prefs)."""
        mods_dir = os.path.join(self.profile, 'mods')
        entries = []
        if ids:
            ws = ws_mods()
            try:
                ws.profile(mods_dir, ids)  # the mod gate first (scripts/mod-gate.py)
            except ws.GateSkipped as e:
                raise SpecFailure(str(e)) from None
            with open(os.path.join(mods_dir, 'mods.json')) as f:
                entries = json.load(f)['mods']
            for entry in entries:
                if translation_only(os.path.join(mods_dir, 'Local', entry['label']['id'])):
                    entry['enabled'], entry['enabledForDlc'] = False, []
            hash_of = ws.mono_hash
        else:
            hash_of = None
        own = [('OniMcp', self.env['E2E_ONIMCP'])]
        if ONIMCP_WORKSHOP in ids:
            # Gated and unpacked above; it moves to the OniMcp folder the runner's copy would take.
            workshop = os.path.join(mods_dir, 'Local', ONIMCP_WORKSHOP)
            entries = [e for e in entries if e['label']['id'] != ONIMCP_WORKSHOP]
            own = [('OniMcp', workshop)]
        if driver:
            own.append(('E2eDriver', self.env['E2E_DRIVER']))
        for folder, source in own:
            dest = os.path.join(mods_dir, 'Local', folder)
            if folder == 'E2eDriver':
                # The build's assembly, with the mod's yaml files from its source folder.
                os.makedirs(dest)
                dll = os.path.join(source, 'E2eDriver.dll')
                if not os.path.exists(dll):
                    raise SpecFailure(f'no E2eDriver.dll in {source} (dotnet build tools/E2eDriver -c Release)')
                shutil.copy(dll, dest)
                for name in ('mod.yaml', 'mod_info.yaml'):
                    shutil.copy(next(p for p in (os.path.join(DRIVER_SOURCE, name), os.path.join(source, name))
                                     if os.path.exists(p)), dest)
            elif source == os.path.join(mods_dir, 'Local', ONIMCP_WORKSHOP):
                shutil.move(source, dest)
            else:
                shutil.copytree(source, dest)
            if folder == 'OniMcp' and self.kind == 'original':
                shutil.copy(VALUETUPLE, dest)
            static = mod_yaml_value(dest, 'staticID') or folder
            if hash_of is None:
                hash_of = ws_mods().mono_hash
            # A local mod's version is its folder name's Mono hash, which KMod recomputes.
            entries.append({'label': {'distribution_platform': 0, 'id': folder, 'title': folder,
                                      'version': hash_of(folder)},
                            'status': 1, 'enabled': True, 'enabledForDlc': ['', 'EXPANSION1_ID'], 'crash_count': 0,
                            'reinstall_path': None, 'staticID': static})
        if self.workshop:
            entries.append(self.install_workshop_item(self.workshop))
        with open(os.path.join(mods_dir, 'mods.json'), 'w') as f:
            json.dump({'version': 1, 'mod_load_in_progress': False, 'mods': entries}, f)
        return {e['label']['id']: e['staticID'] for e in entries}

    def install_workshop_item(self, workshop):
        """The spec's `workshop` item as a Steam subscription, for the Steam double's Workshop part
        (tools/SteamStub/ugc.s, `build.sh --dlc --ugc`; both players must load that library): the
        mod (gated and unpacked as `mods` are) laid out under the run home's
        steamapps/workshop/content/457140/ID, as a folder (U60's loose files) or as a legacy zip
        there, truncated to half its size when `corrupt`. The double's environment names the item
        (ONI_STEAMSTUB_UGC_*), and the good zip is what its UGCDownloadToLocation serves. Returns
        the item's mods.json entry: a Steam label, enabled."""
        wid = str(workshop['id'])
        staging = os.path.join(self.home, 'workshop-staging')
        ws = ws_mods()
        try:
            ws.profile(staging, [wid])  # the mod gate first, as for `mods`
        except ws.GateSkipped as e:
            raise SpecFailure(str(e)) from None
        source = os.path.join(staging, 'Local', wid)
        item = os.path.join(self.home, 'steamapps', 'workshop', 'content', '457140', wid)
        good = os.path.join(self.home, 'workshop-download.zip')
        with zipfile.ZipFile(good, 'w', zipfile.ZIP_DEFLATED) as z:
            for folder, _, files in os.walk(source):
                for name in sorted(files):
                    full = os.path.join(folder, name)
                    z.write(full, os.path.relpath(full, source))
        if workshop.get('layout', 'folder') == 'folder':
            shutil.copytree(source, item)
            path = item
        else:
            os.makedirs(item)
            path = os.path.join(item, wid + '.zip')
            shutil.copy(good, path)
            if workshop.get('corrupt'):
                with open(path, 'r+b') as f:
                    f.truncate(os.path.getsize(path) // 2)
        title = mod_yaml_value(source, 'title') or wid
        self.workshop_env = {'ONI_STEAMSTUB_UGC_ID': wid, 'ONI_STEAMSTUB_UGC_PATH': path,
                             'ONI_STEAMSTUB_UGC_TITLE': title, 'ONI_STEAMSTUB_UGC_DOWNLOAD': good}
        shutil.rmtree(staging)
        # A Steam label's version is the item's lastUpdateTime, which the double's details leave 0.
        return {'label': {'distribution_platform': 1, 'id': wid, 'title': title, 'version': 0},
                'status': 1, 'enabled': True, 'enabledForDlc': ['', 'EXPANSION1_ID'], 'crash_count': 0,
                'reinstall_path': None, 'staticID': mod_yaml_value(source, 'staticID') or wid}

    def start(self):
        for port in (self.lane.mcp_port, self.lane.driver_port):
            if port_open(port):
                raise SpecFailure(f'port {port} is already in use (another session?)')
        size = ['-screen-width', str(WIDTH), '-screen-height', str(HEIGHT), '-screen-fullscreen', '0']
        env = dict(os.environ, XDG_CONFIG_HOME=os.path.join(self.home, 'config'),
                   XDG_CACHE_HOME=os.path.join(self.home, 'cache'),
                   E2E_DRIVER_PORT=str(self.lane.driver_port), **child_marks(self.lane))
        env.update(self.workshop_env)  # the Steam double's Workshop item (install_workshop_item)
        # The player's own systemd scope (scripts/contain.sh), which a teardown stops whole.
        self.scope = scope_name(f'lane{self.lane.number}-{self.kind}-{next(_scopes)}') if has_systemd() else None
        if self.scope:
            env['ONI_SCOPE'] = self.scope
        for name in ('GALLIUM_DRIVER', 'MESA_D3D12_DEFAULT_ADAPTER_NAME'):
            if name in self.env:
                env[name] = self.env[name]
        if self.kind == 'original':
            env.update(original_environment(self.env.get('E2E_ORIGINAL_ENV')))
        # `blocked_plugins`, `stray_plugins`: each player runs a copy of its install with those
        # plugins unreadable and those files added. The copy is the lane's (both players of a run
        # use it in turn), at the same path for both, so the plugin's path in a load error is the
        # same in their logs.
        install = os.path.join(OUT, f'.blocked-{self.lane.number}')
        altered = bool(self.blocked_plugins or self.stray_plugins)
        if self.kind == 'original':
            executable = self.env['E2E_ORIGINAL']
            if altered:
                blocked_install(os.path.dirname(executable), self.blocked_plugins, install, self.stray_plugins)
                # The executable itself copied (Unity's unity.lock goes beside it; the copy is
                # the lane's own, as original_player's folder is).
                copy = os.path.join(install, os.path.basename(executable))
                os.remove(copy)
                shutil.copy2(os.path.realpath(executable), copy)
                executable = copy
            else:
                executable = original_player(executable, self.lane.number)
            # E2E_ORIGINAL_ARGS: more Unity player arguments for the original (e.g. -force-vulkan).
            command = [executable, *size, '-logFile', self.log, *os.environ.get('E2E_ORIGINAL_ARGS', '').split()]
            stdout = os.path.join(self.out, 'stdout.txt')
        else:
            game = self.env['QA_U57_GAME']
            if altered:
                game = blocked_install(game, self.blocked_plugins, install, self.stray_plugins)
            env.update(ONI_GAME_DIR=game, ONI_RUN_HOME=self.home, ONI_CONFIG='release')
            # The main menu's update check reads the spec's releases file, as this tag (else
            # scripts/run.sh turns it off).
            env.pop('ONI_UPDATE_RELEASES_URL', None)
            env.pop('ONI_UPDATE_TAG', None)
            if self.update_check:
                env.update(ONI_UPDATE_RELEASES_URL='file://' + os.path.join(FIXTURES, self.update_check['releases']),
                           ONI_UPDATE_TAG=str(self.update_check['tag']))
            env.update(self.extra_env)
            command = [os.path.join(ROOT, 'scripts', 'run.sh'), '--native-override',
                       'steam_api=' + self.env['QA_U57_STEAM'], *size,
                       *(a for t in os.environ.get('E2E_GAME_TWEAKS', 'exact').split() for a in ('--game-tweak', t)),
                       # A spec's own tweaks on top (`game_tweaks`), with its environment (`env`).
                       *(a for t in self.game_tweaks for a in ('--game-tweak', t)),
                       # The original's main menu has no Moono Tweaks button: the menus compare
                       # (a spec of the Moono Tweaks screen keeps it: `tweaks_menu: true`).
                       *([] if self.tweaks_menu else ['--no-tweaks-menu']),
                       # E2E_PLAYER_ARGS: more host arguments for our player (e.g. --renderer vulkan).
                       *os.environ.get('E2E_PLAYER_ARGS', '').split()]
            stdout = self.log
        # The runner's own display (a desktop's) must reach neither the player nor xvfb-run's
        # server, whose environment find_display reads: input would land on the desktop.
        env.pop('DISPLAY', None)
        env.pop('XAUTHORITY', None)
        # Nor may WSLg's Wayland compositor: a player preferring Wayland would draw on the
        # user's Windows desktop, outside the lane's Xvfb.
        for name in ('WAYLAND_DISPLAY', 'XDG_SESSION_TYPE'):
            env.pop(name, None)
        # A preferred OOM victim in the memory-capped test slice: an over-committed machine loses a
        # player (a machine failure, rerun), never the user's sessions (scripts/contain.sh).
        command = [os.path.join(ROOT, 'scripts', 'contain.sh'), *command]
        if self.lane.display:
            self.start_xvfb(self.lane.display)
            env['DISPLAY'] = self.display = self.lane.display
            self.process = subprocess.Popen(command, env=env, stdout=open(stdout, 'w'), stderr=subprocess.STDOUT,
                                            start_new_session=True)
            self.lane.track('player', self.process.pid, self.scope)
        else:
            command = ['xvfb-run', '-a', '-s', ' '.join([f'-screen 0 {WIDTH}x{HEIGHT}x24', *keyboard_server_options(self.keyboard),
                                                         *dpi_server_options(self.display_dpi)]),
                       *command]
            os.makedirs(shared_dir(), exist_ok=True)
            with _xvfb_start, open(os.path.join(shared_dir(), 'xvfb.lock'), 'a+') as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                self.process = subprocess.Popen(command, env=env, stdout=open(stdout, 'w'), stderr=subprocess.STDOUT,
                                                start_new_session=True)
                self.lane.track('player', self.process.pid, self.scope)
                self.find_display()
        with open(os.path.join(self.out, 'lane.json'), 'w') as f:
            json.dump(self.lane_record(), f)
        self.watchdog.start()
        deadline = time.time() + 900
        while not port_open(self.lane.mcp_port):
            if self.process.poll() is not None:
                raise SpecFailure(f'the player exited ({self.process.returncode}) before OniMcp started')
            if time.time() > deadline:
                raise SpecFailure('OniMcp did not start within 15 minutes')
            time.sleep(3)

    def lane_record(self):
        return {'lane': self.lane.number, 'onimcp_port': self.lane.mcp_port, 'driver_port': self.lane.driver_port,
                'display': self.display}

    def start_xvfb(self, display):
        number = display.lstrip(':')
        if display_in_use(number):
            raise SpecFailure(f'display {display} is in use')
        self.xvfb = subprocess.Popen(['Xvfb', display, '-screen', '0', f'{WIDTH}x{HEIGHT}x24', '-nolisten', 'tcp',
                                      *keyboard_server_options(self.keyboard), *dpi_server_options(self.display_dpi)],
                                     stdout=subprocess.DEVNULL, stderr=open(os.path.join(self.out, 'xvfb.txt'), 'w'),
                                     start_new_session=True, env=dict(os.environ, **child_marks(getattr(self, 'lane', None))))
        if getattr(self, 'lane', None):
            self.lane.track('xvfb', self.xvfb.pid)
        deadline = time.time() + 20
        while not display_ready(number):
            if self.xvfb.poll() is not None or time.time() > deadline:
                raise SpecFailure(f'Xvfb {display} did not start')
            time.sleep(0.2)
        # The spec's layout, before the player connects (-noreset keeps it).
        set_keyboard(display, None, self.keyboard)

    def find_display(self):
        """The display and X authority file xvfb-run gave the player (from its environment)."""
        deadline = time.time() + 60
        while time.time() < deadline:
            for pid in descendants(self.process.pid):
                environ = process_environ(pid)
                if environ.get('DISPLAY') and environ.get('XAUTHORITY'):  # what xvfb-run gives its command
                    self.display, self.xauthority = environ['DISPLAY'], environ.get('XAUTHORITY')
                    number = self.display.lstrip(':').split('.')[0]
                    while not display_ready(number) and time.time() < deadline:
                        time.sleep(0.2)
                    set_keyboard(self.display, self.xauthority, self.keyboard)
                    return
            if self.process.poll() is not None:
                raise SpecFailure(f'xvfb-run exited ({self.process.returncode})')
            time.sleep(0.2)
        raise SpecFailure('xvfb-run started no player with a display')

    def alive(self):
        return self.process is not None and self.process.poll() is None

    def game_pid(self):
        """The game process under the launch's wrappers (contain.sh, xvfb-run)."""
        if not self.process:
            return None
        for p in [self.process.pid, *descendants(self.process.pid)]:
            with contextlib.suppress(OSError):
                with open(f'/proc/{p}/comm') as f:
                    if f.read().strip() in PLAYER_NAMES:
                        return p
        return None

    def quit(self):
        """The game asked to quit (quit_players), its wrappers and X server left running."""
        pid = self.game_pid()
        start = process_start(pid) if pid else None
        if start is not None:
            quit_players([{'pid': pid, 'start': start}])

    def kill(self):
        # Even a hung player: its threads may be waiting on the GPU (and the quit's deadline
        # ends it if its main thread is stuck).
        self.quit()
        with contextlib.suppress(OSError):
            os.killpg(self.process.pid, signal.SIGKILL)
        if self.scope:
            stop_scope(self.scope, grace=0)

    def load(self, mcp):
        mcp.connect()
        if not self.fixture:
            return  # `from: main-menu`: the steps start at the main menu
        mcp.call('game_control', domain='launch', action='start', path=self.fixture, confirm=True)
        deadline = time.time() + 900
        while True:
            time.sleep(3)
            if self.process.poll() is not None:
                raise SpecFailure(f'the player exited ({self.process.returncode}) while loading')
            try:
                status = mcp.read('oni://colony/status')
            except (urllib.error.URLError, ConnectionError, SpecFailure):
                status = {}
            # The player starts at the main menu, so a colony with Duplicants is the fixture's.
            if isinstance(status, dict) and status.get('duplicantCount', 0) > 0:
                break
            if time.time() > deadline:
                raise SpecFailure('the fixture did not load within 15 minutes')
        mcp.call('game_control', domain='speed', action='pause', confirm=True)

    def stop(self):
        self.watchdog.stop()
        # The player outlives xvfb-run when its X server goes first: every descendant is known
        # before anything is signalled, and whatever survives SIGTERM gets SIGKILL. The game
        # quits first, alone: xvfb-run's group holds its X server too.
        family = [p for process in (self.process, self.xvfb) if process for p in descendants(process.pid)]
        if self.alive():
            self.quit()
        for process in (self.process, self.xvfb):
            if process and process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(30)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
        deadline = time.time() + 20
        while time.time() < deadline and any(os.path.exists(f'/proc/{p}') for p in family):
            time.sleep(0.5)
        for p in family:
            with contextlib.suppress(OSError):
                os.kill(p, signal.SIGKILL)
        # Whatever the player started in its scope, out of its process group.
        if self.scope:
            stop_scope(self.scope)
        for process in (self.process, self.xvfb):
            if process:
                self.lane.untrack(process.pid)
        deadline = time.time() + 30
        while (port_open(self.lane.mcp_port) or port_open(self.lane.driver_port)) and time.time() < deadline:
            time.sleep(1)

    def signatures(self):
        if not os.path.exists(self.log):
            return set()
        result = subprocess.run([sys.executable, os.path.join(ROOT, 'scripts', 'qa-tools.py'), 'signatures', self.log],
                                capture_output=True, text=True)
        return set(result.stdout.splitlines())


def original_environment(text):
    """E2E_ORIGINAL_ENV's NAME=VALUE words: settings for the original player only, such as
    `GALLIUM_DRIVER=llvmpipe LP_NUM_THREADS=4`. On WSL the original renders through Mesa's d3d12
    driver at about 2 frames a second and takes two minutes to reach its main menu, CPU-bound in
    one thread of copies inside the driver; llvmpipe gives it 3-4 times the frame rate and a main
    menu in 20 s, with shots within 0.05 % of the d3d12 ones (docs in tests/e2e/README.md)."""
    pairs = {}
    for word in (text or '').split():
        name, sep, value = word.partition('=')
        if not sep or not re.fullmatch(r'[A-Za-z_]\w*', name):
            raise SpecFailure(f'E2E_ORIGINAL_ENV: {word!r} is not NAME=VALUE')
        pairs[name] = value
    return pairs


def original_player(executable, lane, root=OUT):
    """The original player's executable in a folder of the lane's own: Unity's Linux player runs
    single-instance, locking unity.lock beside its executable ("Player is already running"), so
    each lane gets a copy of the executable beside links to everything else but that lock. The
    links follow E2E_ORIGINAL: one left from another install (a copy whose Steam double differs)
    is pointed at this one's."""
    source = os.path.dirname(os.path.abspath(executable))
    folder = os.path.join(root, f'.original-{lane}')
    os.makedirs(folder, exist_ok=True)
    for name in os.listdir(source):
        if name == 'unity.lock':
            continue
        target = os.path.join(folder, name)
        if name == os.path.basename(executable):
            if (not os.path.exists(target) or os.path.getmtime(target) < os.path.getmtime(executable)
                    or os.path.getsize(target) != os.path.getsize(executable)):
                shutil.copy2(executable, target)
        elif not os.path.lexists(target):
            os.symlink(os.path.join(source, name), target)
        elif os.path.islink(target) and os.readlink(target) != os.path.join(source, name):
            # A link into another install (E2E_ORIGINAL changed since the folder was made).
            os.remove(target)
            os.symlink(os.path.join(source, name), target)
    return os.path.join(folder, os.path.basename(executable))


def blocked_install(source, plugins, dest, stray=()):
    """A copy of the game install at `source` whose native plugins cannot be loaded, as when the
    operating system or an antivirus blocks them. `plugins` maps file names to how
    (`blocked_plugins`): `unreadable` copies the plugin with mode 000, `missing` leaves it out.
    `stray` lists paths under Plugins added to the copy (`stray_plugins`): `DIR/` an empty
    folder, anything else an empty file. Everything else is linked, with real folders only on the
    way to a blocked plugin or a stray path. Built in the runner's output folder, never in the
    install."""
    names = list(plugins)
    source = os.path.realpath(source)
    found = {}
    datas = sorted(d for d in os.listdir(source) if d.endswith('_Data'))
    if stray and not datas:
        raise SpecFailure(f'stray_plugins: no {source}/*_Data')
    strays = [os.path.normpath(os.path.join(datas[0], 'Plugins', p)) + ('/' if p.endswith('/') else '') for p in stray]
    for data in datas:
        for folder, dirs, files in os.walk(os.path.join(source, data, 'Plugins'), followlinks=True):
            for name in files:
                if name in names:
                    found.setdefault(name, os.path.relpath(os.path.join(folder, name), source))
    missing = [n for n in names if n not in found]
    if missing:
        raise SpecFailure(f'blocked_plugins: no {", ".join(missing)} under {source}/*_Data/Plugins')
    shutil.rmtree(dest, ignore_errors=True)
    real = {os.curdir}  # the folders on the way to a blocked plugin, relative to the install
    for path in [*found.values(), *strays]:
        parts = path.rstrip('/').split(os.sep)[:None if path.endswith('/') else -1]
        real.update(os.path.join(*parts[:i]) for i in range(1, len(parts) + 1))

    def mirror(rel):
        os.makedirs(os.path.join(dest, rel), exist_ok=True)
        for name in os.listdir(os.path.join(source, rel)):
            child = os.path.normpath(os.path.join(rel, name))
            target = os.path.realpath(os.path.join(source, child))
            if child in real:
                mirror(child)
            elif child in found.values():
                if plugins[os.path.basename(child)] == 'unreadable':
                    shutil.copyfile(target, os.path.join(dest, child))
                    os.chmod(os.path.join(dest, child), 0)
            else:
                os.symlink(target, os.path.join(dest, child))
    mirror(os.curdir)
    for path in strays:
        added = os.path.join(dest, path.rstrip('/'))
        if path.endswith('/'):
            os.makedirs(added, exist_ok=True)
            continue
        os.makedirs(os.path.dirname(added), exist_ok=True)
        if os.path.lexists(added):
            os.remove(added)  # a link into the install: replaced, never written through
        open(added, 'w').close()
    return dest


def mod_yaml_value(folder, key):
    path = os.path.join(folder, 'mod.yaml')
    if not os.path.exists(path):
        return None
    with open(path, encoding='utf-8-sig', errors='replace') as f:
        match = re.search(rf'^{key}:\s*["\']?(.*?)["\']?\s*$', f.read(), re.M)
    return match.group(1) if match else None


def module_name(path):
    """The name a .NET assembly's metadata gives its module (its Module table's one row, such as
    "大一统.dll"), or None if PATH is not one. PLib's shared config folder is named after the
    assembly, which a Workshop download's file name may misspell (a zip's names decoded in the
    wrong code page)."""
    import struct
    try:
        with open(path, 'rb') as f:
            data = f.read()
        pe = struct.unpack_from('<I', data, 0x3c)[0]
        if data[pe:pe + 4] != b'PE\0\0':
            return None
        sections, opt_size = struct.unpack_from('<H', data, pe + 6)[0], struct.unpack_from('<H', data, pe + 20)[0]
        opt = pe + 24
        dirs = opt + (96 if struct.unpack_from('<H', data, opt)[0] == 0x10b else 112)
        cli_rva = struct.unpack_from('<I', data, dirs + 14 * 8)[0]
        table = [struct.unpack_from('<IIII', data, opt + opt_size + 40 * i + 8) for i in range(sections)]

        def offset(rva):
            for vsize, vaddr, raw_size, raw_ptr in table:
                if vaddr <= rva < vaddr + max(vsize, raw_size):
                    return rva - vaddr + raw_ptr
            raise ValueError(rva)
        root = offset(struct.unpack_from('<I', data, offset(cli_rva) + 8)[0])
        version_len = struct.unpack_from('<I', data, root + 12)[0]
        pos = root + 16 + version_len
        streams = {}
        header = pos + 4
        for _ in range(struct.unpack_from('<H', data, pos + 2)[0]):
            start = struct.unpack_from('<I', data, header)[0]
            end = data.index(b'\0', header + 8)
            streams[data[header + 8:end].decode('ascii')] = root + start
            header = (end + 4) & ~3
        tables = streams.get('#~', streams.get('#-'))
        heap_sizes = data[tables + 6]
        valid = struct.unpack_from('<Q', data, tables + 8)[0]
        rows = tables + 24 + 4 * bin(valid).count('1')   # the Module table's first row
        index = struct.unpack_from('<I' if heap_sizes & 1 else '<H', data, rows + 2)[0]
        strings = streams['#Strings'] + index
        return data[strings:data.index(b'\0', strings)].decode('utf-8')
    except (OSError, ValueError, KeyError, IndexError, struct.error, UnicodeDecodeError):
        return None


def write_options(profile, mods, options):
    """A mod's PLib options before launch: OPTIONS maps a static ID (or Workshop ID) to fields,
    merged into config.json where POptions reads it, beside the mod's assembly (every folder of
    the mod holding a DLL, as KMod may load an archived version) and in the shared location
    mods/config/ASSEMBLY/ (named by the DLL's file and by its metadata's module name, which a
    download may spell otherwise). A `$file` field names the file instead, for a mod whose
    [ConfigFile] gives it another name ("MiniBox.json"). Returns the files written."""
    written = []
    mods_dir = os.path.join(profile, 'mods')
    for key, fields in options.items():
        folder = next((f for f, static in mods.items() if key in (f, static)), None)
        if folder is None:
            raise SpecFailure(f'options for {key!r}, which is not among the spec\'s mods')
        fields = dict(fields)
        name = fields.pop('$file', 'config.json')
        root = os.path.join(mods_dir, 'Local', folder)
        targets = set()
        for directory, _, files in os.walk(root):
            dlls = [f for f in files if f.lower().endswith('.dll')]
            if dlls:
                targets.add(os.path.join(directory, name))
                # PLib's [ConfigFile] may spell it otherwise ("Config.json"): a mod that ships
                # its file reads that one, on a case-sensitive file system too.
                targets.update(os.path.join(directory, f) for f in files
                               if f.lower() == name.lower() and f != name)
                for dll in dlls:
                    targets.add(os.path.join(mods_dir, 'config', os.path.splitext(dll)[0], name))
                    module = module_name(os.path.join(directory, dll))
                    if module:
                        module = module[:-4] if module.lower().endswith('.dll') else module
                        targets.add(os.path.join(mods_dir, 'config', module, name))
        for target in sorted(targets):
            data = {}
            if os.path.exists(target):
                with contextlib.suppress(ValueError), open(target, encoding='utf-8-sig') as f:
                    data = json.load(f)
            data.update(fields)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, 'w') as f:
                json.dump(data, f, indent=2)
            written.append(target)
    return written


# ---------------------------------------------------------------- orders and expectations (OniMcp)

def compare(actual, wanted):
    if isinstance(wanted, bool) or isinstance(actual, bool):
        return actual == wanted
    if isinstance(wanted, (int, float)):
        return actual == wanted
    match = re.fullmatch(r'\s*(>=|<=|>|<|==|!=)?\s*(-?\d+(?:\.\d+)?)\s*', str(wanted))
    if not match:
        raise SpecFailure(f'bad expectation {wanted!r}')
    op, number = match.group(1) or '==', float(match.group(2))
    return apply_op(op, actual, number)


def apply_op(op, actual, number):
    if not isinstance(actual, (int, float)) or isinstance(actual, bool):
        return {'==': actual == number, '!=': actual != number}.get(op, False)
    return {'>=': actual >= number, '<=': actual <= number, '>': actual > number,
            '<': actual < number, '==': actual == number, '!=': actual != number}[op]


def facts(mcp, keys):
    """What `expect` and `until` check, read only as far as the keys need."""
    found = {}
    if 'buildings' in keys:
        summary = mcp.read('oni://colony/summary')
        found['buildings'] = {b['id']: b['count'] for b in summary.get('buildings', [])}
    if 'duplicants' in keys or 'cycle' in keys:
        status = mcp.read('oni://colony/status')
        found['duplicants'], found['cycle'] = status.get('duplicantCount', 0), status.get('cycle', 0)
    if 'pending_construction' in keys or 'blueprints' in keys:
        # Unfinished blueprints, wires and pipes included: OniMcp connects a powered building it
        # places to a power source with wire blueprints of its own.
        counts, offset = {}, 0
        while True:
            # OniMcp lists at most 100 rows a read.
            index = mcp.call('world_editor', command='read', path='/active/index.md', format='progress',
                             limit=100 if 'blueprints' in keys else 1, offset=offset)
            text = index.get('content', '') if isinstance(index, dict) else str(index)
            match = re.search(r'pending=(\d+)', text)
            if not match:
                raise SpecFailure('no construction progress in /active/index.md: ' + text[-300:])
            found['pending_construction'] = int(match.group(1))
            rows = re.findall(r'^\| \d+ \| (\w+) \| \d+,\d+ \|', text, re.M)
            for prefab in rows:
                counts[prefab] = counts.get(prefab, 0) + 1
            offset += len(rows)
            if 'blueprints' not in keys or not rows or offset >= found['pending_construction']:
                break
        found['blueprints'] = counts
    if 'pending_digs' in keys:
        pending = mcp.call('game_control', domain='speed', action='step_facts').get('pendingOrders', {})
        found['pending_digs'] = pending.get('remainingDigOrders', 0)
    if 'rooms' in keys:
        rooms = mcp.read('oni://rooms/list')
        counts = {}
        for room in rooms.get('rooms', []) if isinstance(rooms, dict) else []:
            kind = room.get('roomType') or room.get('type') or room.get('id')
            counts[kind] = counts.get(kind, 0) + 1
        found['rooms'] = counts
    return found


def unmet(mcp, expect):
    """The expectations that do not hold, as readable lines."""
    found = facts(mcp, expect.keys())
    misses = []
    for key, wanted in expect.items():
        if isinstance(wanted, dict):
            for name, want in wanted.items():
                have = found[key].get(name, 0)
                if not compare(have, want):
                    misses.append(f'{key}.{name} is {have}, wanted {want}')
        elif key in found:
            if not compare(found[key], wanted):
                misses.append(f'{key} is {found[key]}, wanted {wanted}')
        else:
            raise SpecFailure(f'unknown expectation {key!r}')
    return misses


def edit(mcp, path, search, replace, **payload):
    extra = {'payload': payload} if payload else {}
    result = mcp.call('world_editor', command='edit', path=path, confirm=True,
                      content=f'<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE', **extra)
    if not (isinstance(result, dict) and result.get('ok') and not result.get('failed')):
        raise SpecFailure(f'edit of {path} failed: {json.dumps(result)[:1500]}')
    return result


def dig(mcp, rect, priority=5):
    x1, y1, x2, y2 = rect
    edit(mcp, '/active/ops/orders.md', '', f'挖 x1={x1} y1={y1} x2={x2} y2={y2} priority={priority}')


def build(mcp, prefab, cells, priority=5, material=None, wire=False):
    """Blueprints through the viewport map (OniMcp places exact construction only that way): each
    cell's token on its map row becomes PREFAB:PRIORITY[#MATERIAL], one row per edit. OniMcp would
    connect a powered building to a power source with wire blueprints and dig orders of its own
    choosing (54 wires towards a sulfur cell, in early-base): only with WIRE."""
    token = f'{prefab}:{priority}' + (f'#{material}' if material else '')
    rows = {}
    for x, y in cells:
        rows.setdefault(y, []).append(x)
    for y, xs in sorted(rows.items()):
        mcp.call('navigation_control', action='focus_cell', x=min(xs), y=y, snap=True)
        page = mcp.call('world_editor', command='read', path='/active/map/viewport.md', format='edit',
                        compact=False, view='default')
        text = page.get('content', '') if isinstance(page, dict) else str(page)
        bounds = re.search(r'X=(\d+)~(\d+)', text)
        # The map pads rows to three digits (Y=014: in a rocket's cabin).
        line = next((l for l in text.splitlines() if re.match(rf'Y=0*{y}:', l)), None)
        if not bounds or line is None:
            raise SpecFailure(f'row {y} is not on the viewport map')
        x0, x_end = int(bounds.group(1)), int(bounds.group(2))
        head, cells_text = line.split(':', 1)
        parts = cells_text.split()
        tokens = [i for i, part in enumerate(parts) if part != '┆']
        for x in xs:
            if not x0 <= x <= x_end:
                raise SpecFailure(f'cell {x},{y} is outside the viewport ({x0}..{x_end})')
            parts[tokens[x - x0]] = token
        report = edit(mcp, '/active/map/viewport.md', line, head + ': ' + ' '.join(parts), autoConnectPower=wire)
        for placed in build_results(report):
            if not placed.get('blueprintsPlaced') and not placed.get('blueprintsAlreadyPresent'):
                if placed.get('pendingBuildAfterDig'):
                    # OniMcp digs the cells in the way first, and places the blueprint after.
                    raise SpecFailure(f'{prefab} at row {y}: cells in the way must be dug first '
                                      f'({placed.get("autoDigMarked", 0) + placed.get("autoDigAlreadyMarked", 0)} '
                                      'marked); dig them in an earlier step')
                raise SpecFailure(f'{prefab} at row {y}: no blueprint placed: {json.dumps(placed)[:600]}')


def build_results(report):
    """The per-token results of a viewport build edit (OniMcp's build_area answers)."""
    found = []
    for block in report.get('results', []):
        for kind in (block.get('result') or {}).get('results', []):
            if kind.get('kind') == 'build':
                found += [r.get('result') or {} for r in (kind.get('result') or {}).get('results', [])]
    return found


def run(mcp, seconds, until=None, log=None, speed=3):
    """Game time at `speed` (1-3) in steps of up to a minute, until `until` holds or the time is
    spent; the game is paused again afterwards (UI steps happen in paused time)."""
    resumed = []
    try:
        return run_steps(mcp, seconds, until, log, resumed, speed)
    finally:
        if resumed:
            with contextlib.suppress(SpecFailure, OSError):  # never hide the step's own failure
                mcp.call('game_control', domain='speed', action='pause', confirm=True)


def run_steps(mcp, seconds, until, log, resumed, speed=3):
    spent, stalls = 0.0, 0
    while spent < seconds:
        if until and not unmet(mcp, until):
            return spent
        step = mcp.call('game_control', domain='speed', action='step_until', until={'fact': 'cycle_end'},
                        speed=speed, maxGameSeconds=min(60, seconds - spent), maxWallSeconds=30, confirm=True)
        elapsed = step.get('elapsedGameSeconds', 0) if isinstance(step, dict) else 0
        if log:
            log(f'  ran {elapsed:.0f} s ({step.get("reason") or step.get("status")})')
        if elapsed <= 0:
            # Just after a load the game's speed controls are not up yet, and a step returns at
            # once; it stalls for good only if it keeps doing so. step_until does not unpause
            # the game the load paused: a paused game is resumed (and paused again below).
            if isinstance(step, dict) and (step.get('isPaused') or step.get('currentIsPaused')):
                mcp.call('game_control', domain='speed', action='resume', confirm=True)
                resumed.append(True)
            stalls += 1
            if stalls >= 60:
                raise SpecFailure(f'the game did not advance: {json.dumps(step)[:600]}')
            time.sleep(0.5)
            continue
        stalls = 0
        spent += elapsed
    if until:
        misses = unmet(mcp, until)
        if misses:
            raise SpecFailure(f'after {spent:.0f} game seconds: ' + '; '.join(misses))
    return spent


def screenshot(mcp, out, name):
    """A full-frame screenshot through OniMcp, as OUT/NAME.png; returns its path or None."""
    # OniMcp's screenshot folder (/tmp/oni-mcp/screenshots) is shared by every lane: the name
    # carries the lane's port. It answers with the path before the frame is written, so the
    # file counts once it is newer than the request and its size has settled.
    asked = time.time() - 1
    result = mcp.call('navigation_control', action='screenshot', filename=f'e2e-{mcp.port}-{name}', waitFrames=5)
    path = (result.get('path') or result.get('file')) if isinstance(result, dict) else None
    if not path:
        return None
    deadline, size = time.time() + 30, -1
    while time.time() < deadline:
        try:
            stat = os.stat(path)
        except OSError:
            stat = None
        if stat and stat.st_mtime >= asked and stat.st_size > 0:
            if stat.st_size == size:
                dest = os.path.join(out, name + os.path.splitext(path)[1])
                shutil.copy(path, dest)
                return dest
            size = stat.st_size
        time.sleep(0.3)
    return None


# ---------------------------------------------------------------- UI and state expectations

def ui_mismatches(element, wanted):
    """How a found element (or None) differs from {text, active, interactable, isOn, exists}."""
    misses = []
    if 'exists' in wanted and bool(wanted['exists']) != (element is not None):
        return [f'exists is {element is not None}, wanted {wanted["exists"]}']
    if element is None:
        return ['no such element'] if wanted.get('exists', True) else []
    for field, have in (('active', element.active), ('interactable', element.interactable), ('isOn', element.is_on)):
        if field in wanted and have != wanted[field]:
            misses.append(f'{field} is {have}, wanted {wanted[field]}')
    if 'text' in wanted and not text_matches(element.text, wanted['text']):
        misses.append(f'text is {element.text!r}, wanted {wanted["text"]!r}')
    return misses


def text_matches(have, want):
    """WANT is exact text, "~part" (contained) or "/regex/"."""
    have = '' if have is None else str(have)
    want = str(want)
    if want.startswith('~'):
        return want[1:] in have
    if len(want) > 1 and want.startswith('/') and want.endswith('/'):
        return re.search(want[1:-1], have) is not None
    return have == want


RELATION = re.compile(r'\s*(>=|<=|>|<|==|!=)\s*([-+*/().\w\s]+?)\s*')


def arithmetic(text, names):
    """TEXT, an expression of numbers, NAMES, + - * / and parentheses, evaluated."""
    import ast
    def value(node):
        if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)):
            return node.value
        if isinstance(node, ast.Name):
            if node.id not in names:
                raise SpecFailure(f'{text!r}: no value saved as {node.id!r}')
            v = names[node.id]
            if not isinstance(v, (int, float)) or isinstance(v, bool):
                raise SpecFailure(f'{text!r}: {node.id} is {v!r}, not a number')
            return v
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, (ast.USub, ast.UAdd)):
            return -value(node.operand) if isinstance(node.op, ast.USub) else value(node.operand)
        if isinstance(node, ast.BinOp) and isinstance(node.op, (ast.Add, ast.Sub, ast.Mult, ast.Div)):
            a, b = value(node.left), value(node.right)
            return {ast.Add: a + b, ast.Sub: a - b, ast.Mult: a * b}.get(type(node.op)) if not isinstance(node.op, ast.Div) else a / b
        raise SpecFailure(f'{text!r}: only numbers, saved names, + - * / and parentheses')
    try:
        tree = ast.parse(text.strip(), mode='eval')
    except SyntaxError:
        raise SpecFailure(f'bad expression {text!r}')
    return value(tree.body)


def mask_quoted(text, nested=True):
    """TEXT with each character inside quotes ('...' or "...", the quotes too) and, when NESTED,
    inside () and [], as NUL: what is left is the text's top level, the same length."""
    out, quote, depth = list(text), None, 0
    for i, c in enumerate(text):
        if quote:
            quote = None if c == quote else quote
        elif c in '\'"':
            quote = c
        elif nested and c in '([':
            depth += 1
        elif nested and c in ')]' and depth:
            depth -= 1
        elif not depth:
            continue
        out[i] = '\0'
    if quote:
        raise SpecFailure(f'{text!r}: a {quote} quote is not closed')
    return ''.join(out)


def parse_condition(text):
    """.MEMBER == VALUE or .MEMBER != VALUE as (MEMBER, OP, VALUE). VALUE is a word, or 'TEXT' or
    "TEXT" in quotes, which may hold spaces, `and` and `==`; so may a member's quoted arguments."""
    text = text.strip()
    top = mask_quoted(text)
    ops = list(re.finditer(r'==|!=', top))
    member = text[:ops[0].start()].strip() if len(ops) == 1 else ''
    want = text[ops[0].end():].strip() if len(ops) == 1 else ''
    quoted = len(want) >= 2 and want[0] in '\'"' and want[-1] == want[0] and want[0] not in want[1:-1]
    if not (member.startswith('.') and not re.search(r'\s', mask_quoted(member)) and want
            and (quoted or not re.search(r'[\s\'"]', want))):
        raise SpecFailure(f'{text!r}: a condition is .MEMBER == VALUE or .MEMBER != VALUE '
                          "(VALUE in quotes if it holds spaces)")
    return member, ops[0].group(), want[1:-1] if quoted else want


def parse_aggregate(expr):
    """count:HEAD[*] [if CONDITION and ...] or sum:HEAD[*].SUFFIX [if ...] as (HOW, HEAD, SUFFIX,
    [(MEMBER, OP, VALUE)...]); None for any other expression. Quoted text (in HEAD, SUFFIX or the
    conditions) is never split."""
    how = re.match(r'(count|sum):', expr)
    if not how:
        return None
    star = mask_quoted(expr, False).find('[*]')
    if star < 0:
        raise SpecFailure(f'{expr!r}: {how.group(1)}: reads HEAD[*]')
    head, rest = expr[how.end():star], expr[star + 3:]
    top = mask_quoted(rest)
    split = re.search(r'\s+if\s+', top)
    suffix, conditions = (rest[:split.start()], rest[split.end():]) if split else (rest, None)
    if not head.strip() or re.search(r'\s', mask_quoted(suffix)) or (conditions is not None and not conditions.strip()):
        raise SpecFailure(f'{expr!r}: {how.group(1)}:HEAD[*]{".SUFFIX" if how.group(1) == "sum" else ""} [if .MEMBER == VALUE and ...]')
    tests = []
    if conditions is not None:
        top, start = mask_quoted(conditions), 0
        for match in list(re.finditer(r'\s+and\s+', top)) + [None]:
            tests.append(parse_condition(conditions[start:match.start() if match else None]))
            start = match and match.end()
    return how.group(1), head, suffix, tests


def aggregate_rows(how, tests, rows):
    """count: or sum: over state.each ROWS, each the values of the TESTS' members (MEMBER, OP,
    WANT) and, for sum, the summed member last. A member is compared as text, booleans as
    true/false; a sum adds numbers only."""
    as_text = lambda v: str(v).lower() if isinstance(v, bool) else str(v)
    total = 0
    for row in rows:
        if all((as_text(row[i]) == want) == (op == '==') for i, (_, op, want) in enumerate(tests)):
            if how == 'count':
                total += 1
            else:
                v = row[len(tests)]
                total += v if isinstance(v, (int, float)) and not isinstance(v, bool) else 0
    return total


def state_check(have, wanted, saved, original=None):
    """Whether a state.get value meets WANTED: a value, "OP EXPR" (EXPR of numbers and names,
    + - * / and parentheses; a name is a value saved with save_as, or `original`: the original
    player's value at this step), exists / missing,
    or {contains: X}, {equals: X}, {save_as: NAME}. Returns (ok, deferred): deferred when it
    refers to the original and no original value is known yet. A list of these holds when each
    of them does (several checks on one expression: a mapping cannot repeat a key)."""
    if isinstance(wanted, list):
        ok, deferred = True, False
        for one in wanted:
            one_ok, one_deferred = state_check(have, one, saved, original)
            ok &= one_ok
            deferred |= one_deferred
        return ok, deferred
    if isinstance(wanted, dict):
        ok = True
        if 'save_as' in wanted:
            saved[wanted['save_as']] = have
        if 'contains' in wanted:
            ok &= have is not None and wanted['contains'] in (have if isinstance(have, (list, str, dict)) else str(have))
        if 'equals' in wanted:
            ok &= have == wanted['equals']
        return ok, False
    if wanted == 'exists':
        return have is not None, False
    if wanted == 'missing':
        return have is None, False
    if isinstance(wanted, str):
        quoted = re.fullmatch(r'\s*(==|!=)\s*(["\'])(.*)\2\s*', wanted)
        if quoted:  # "!= 'AnimalControl'": text, such as an enum's name
            return (str(have) == quoted.group(3)) == (quoted.group(1) == '=='), False
        match = RELATION.fullmatch(wanted)
        if match:
            op, expression = match.group(1), match.group(2)
            if re.search(r'\boriginal\b', expression):
                if original is None:
                    return True, True
                if not re.fullmatch(r'\s*original\s*', expression) or (isinstance(original, (int, float)) and not isinstance(original, bool)):
                    return apply_op(op, have, arithmetic(expression, dict(saved, original=original))), False
                return apply_op(op, have, original), False
            return apply_op(op, have, arithmetic(expression, saved)), False
    if isinstance(wanted, bool) or isinstance(have, bool):
        return type(have) is type(wanted) and have == wanted, False  # 1 is not True
    return have == wanted, False


# ---------------------------------------------------------------- one spec run

def new_run_id():
    """An invocation's id, unique among a worktree's runs: its start time and a random part.
    Its runs are in out/e2e/SPEC/RUN_ID/PLAYER-N, and out/e2e/SPEC/latest points at the newest."""
    return time.strftime('%Y%m%d-%H%M%S') + '-' + os.urandom(4).hex()


def run_folder(name, run_id, kind, index, out=None):
    return os.path.join(out or OUT, name, run_id, f'{kind}-{index}')


def point_latest(folder, target):
    """FOLDER/latest as a symlink to TARGET (a name in FOLDER), replaced atomically."""
    os.makedirs(folder, exist_ok=True)
    link = os.path.join(folder, 'latest')
    temp = f'{link}.{os.getpid()}.{threading.get_ident()}'
    with contextlib.suppress(OSError):
        os.remove(temp)
    os.symlink(target, temp)
    os.replace(temp, link)


class Run:
    """One spec in one player: the step interpreter."""

    def __init__(self, spec, kind, index, env, lane, log, perf_wait=True, original_values=None, hold=None, run_id=None):
        self.spec, self.kind, self.index, self.env, self.lane, self.log = spec, kind, index, env, lane, log
        self.run_id = run_id or new_run_id()
        self.out = run_folder(spec['name'], self.run_id, kind, index)
        self.perf_wait = perf_wait
        self.hold = hold  # 'fail' or 'end': keep the player up for a look (--hold)
        self.exited = False  # the player quit in an expect_exit step
        self.mcp = Mcp(lane.mcp_port)
        self.driver = self.input = None
        self.saves, self.values, self.saved, self.evidence = [], [], {}, {'ui_dump': [], 'shot': [], 'world_shot': []}
        self.original_values = original_values or {}
        self.player = None

    # -- helpers
    def need_driver(self):
        if not self.driver:
            raise SpecFailure('this step needs E2eDriver (E2E_DRIVER)')
        return self.driver

    def need_input(self):
        if not self.input:
            if not self.player.display or self.player.display == os.environ.get('DISPLAY'):
                raise SpecFailure(f'no lane display for input (got {self.player.display}; never the runner\'s own)')
            self.input = Input(self.player.display, self.player.xauthority,
                               frames=self.driver.frames if self.driver else None)
        return self.input

    def update_scale(self):
        """`scaled_screen`: in a fullscreen window the game renders at Display.main's rendering
        size, stretched over the whole display (the original, measured on Xvfb: 960x720 on a
        1280x720 screen, the pointer at 160,0 read as 120,719); pointer positions are scaled
        to match. Windowed, the window sits at the display's origin at its own size."""
        driver, inp = self.need_driver(), self.need_input()
        if driver.state('UnityEngine.Screen.fullScreen'):
            width, height = driver.state('UnityEngine.Display.main.renderingWidth'), driver.state('UnityEngine.Display.main.renderingHeight')
            scale = (WIDTH / width, HEIGHT / height) if width and height else (1.0, 1.0)
        else:
            scale = (1.0, 1.0)
        if scale != inp.scale:
            self.log(f'  the game renders at {WIDTH / scale[0]:.0f}x{HEIGHT / scale[1]:.0f}, stretched over the '
                     f'{WIDTH}x{HEIGHT} display: pointer positions scaled by {scale[0]:.4g}, {scale[1]:.4g}')
            inp.scale = scale

    def frames(self, n):
        if self.driver:
            self.driver.frames(n)
        else:
            time.sleep(n * FRAME_FALLBACK)

    def element(self, query):
        """The element QUERY names, which must not be covered by another graphic."""
        element = self.need_driver().find_one(query)
        if element.onscreen is False:
            raise SpecFailure(f'{element.path} is off screen (rect {element.rect})')
        if not element.hit_is_self():
            # The hits under the centre, top first, say what covers it.
            below = self.driver.at(*element.center, hits=4)
            stack = ', '.join(f'{h.get("path")}' for h in below.get('all', [])) or 'none'
            what = element.hit if element.relation in ('other', 'ancestor') else f'nothing ({element.relation})'
            raise SpecFailure(f'{element.path} is occluded by {what} (hits there: {stack})')
        return element

    def click_element(self, query, mods=(), button='left', double=False):
        element = self.element(query)
        self.need_input().click(*element.center, button=button, mods=mods, double=double)
        return element

    def at_main_menu(self):
        """Whether the player shows the front end (the main menu and its screens, no colony),
        where OniMcp takes no screenshot."""
        return bool(self.driver and self.driver.find(path='SceneInitializerFE/FrontEndManager', limit=1))

    def menu_grab(self, name):
        """A front-end shot: the lane's display grabbed (Input.grab), a frame after the pointer
        moved away."""
        if not (self.player and self.player.display):
            raise SpecFailure(f'shot {name}: the main menu is shot from a lane display')
        self.frames(5)
        return self.need_input().grab(os.path.join(self.out, name + '.png'))

    def settle_build_menu(self, what, wait=None):
        """Before evidence of the UI: while a build menu category is open, waits for the menu to
        catch up (the game recounts, re-checks and repaints it over frames). WAIT makes the
        request and returns (its value, the check); by default buildmenu.settled alone. Fails
        when the menu does not settle within BUILD_MENU_FRAMES; returns WAIT's value."""
        if not self.driver:
            return None
        wait = wait or (lambda: (None, self.driver.build_menu_settled()))
        try:
            value, result = wait()
        except DriverError as e:
            last = e.result if isinstance(e.result, dict) else {}
            if last.get('reasons'):
                raise SpecFailure(f'{what}: the build menu did not settle within {BUILD_MENU_FRAMES} frames: '
                                  + '; '.join(last['reasons'][:6])) from None
            raise SpecFailure(f'{what}: {e}') from None
        if result and (result.get('open') or result.get('frames')):
            self.log(f'  {what}: the build menu settled in {result.get("frames", 0)} frames '
                     f'(checks took up to {result.get("maxMs")} ms)')
        return value

    def pointer_away(self):
        """The pointer off the UI before a shot (§5.4)."""
        if self.player and self.player.display:
            self.need_input().move(WIDTH - 1, HEIGHT - 1)
            self.frames(2)

    def rects(self, paths):
        rects = []
        for path in paths:
            if _rect(path):
                rects.append([float(v) for v in path])
            else:
                rects += [list(e.rect) for e in self.need_driver().find(path=path) if e.rect and e.active is not False]
        return rects

    # Dialogs a load may open, by their header text: a fixture saved with other mods than the
    # spec's opens "MOD DIFFERENCES" in both players (in a spec's `language:`, the game's own
    # translation of it). Its OK button is pressed like a player would.
    LOAD_DIALOGS = ('MOD DIFFERENCES', '模组差异', '모드 차이', 'РАЗЛИЧИЯ МОДОВ')

    def dismiss_load_dialogs(self):
        for _ in range(5):
            headers = [e for title in self.LOAD_DIALOGS for e in self.driver.find(text=title)]
            if not headers:
                return
            segments = headers[0].path.split('/')
            dialog = '/'.join(segments[:next((i + 1 for i, s in enumerate(segments) if 'Dialog' in s), 2)])
            self.log(f'  dismissing {headers[0].text!r} ({dialog})')
            ok = self.driver.find(path=dialog + '/**/OKButton') or self.driver.find(text='OK', under=dialog)
            if not ok:
                raise SpecFailure(f'{dialog}: no OK button to dismiss it')
            # A mod's notice at load may be another dialog on the same path, shown above this one
            # (Project Expedition's): a player closes the topmost first. The dialogs are told
            # apart by their element keys' sibling indices; the last one is on top.
            def sibling(element):
                parts = element.raw.get('key', '').split('/')
                part = parts[len(dialog.split('/'))] if len(parts) > len(dialog.split('/')) else ''
                return int(part.rsplit('#', 1)[1]) if '#' in part else 0
            top = max(range(len(ok)), key=lambda i: sibling(ok[i]))
            if sibling(ok[top]) != sibling(headers[0]):
                self.log(f'  dismissing the dialog above it ({ok[top].path})')
                self.click_element({'ui': ok[top].path, 'index': top})
                self.frames(10)
                continue
            self.click_element({'ui': ok[top].path, 'index': top})
            self.driver.wait(dialog, 'gone', 120)

    # -- steps
    def step(self, kind, value, number):
        mcp = self.mcp
        if kind == 'dig':
            dig(mcp, value)
        elif kind == 'build':
            cells = value.get('cells') or [value['at']]
            build(mcp, value['prefab'], cells, value.get('priority', 5), value.get('material'), value.get('wire', False))
        elif kind == 'run':
            spent = run(mcp, value['seconds'], value.get('until'), self.log, value.get('speed', 3))
            self.log(f'  run: {spent:.0f} game seconds')
        elif kind == 'expect':
            misses = unmet(mcp, value)
            if misses:
                raise SpecFailure('; '.join(misses))
        elif kind == 'call':
            mcp.call(value['tool'], **value.get('args', {}))
        elif kind == 'save':
            saved = mcp.call('game_control', domain='save', action='save', name=value, overwrite=True,
                             updateActiveSave=False, confirm=True)
            path = saved.get('saved') if isinstance(saved, dict) else None
            if not path or not os.path.exists(path):
                raise SpecFailure(f'save {value} failed: {json.dumps(saved)[:600]}')
            shutil.copy(path, os.path.join(self.out, value + '.sav'))
            self.saves.append(value)
        elif kind == 'shot':
            self.shot(value if isinstance(value, dict) else {'name': value})
        elif kind == 'world_shot':
            self.world_shot(value)
        elif kind == 'file_shot':
            self.file_shot(value)
        elif kind == 'expect_exit':
            self.expect_exit(value)
        elif kind == 'open':
            self.open(value)
        elif kind == 'keyboard':
            # The layout switched while the player runs, as a player's alt+shift does.
            set_keyboard(self.player.display, self.player.xauthority, value)
            self.frames(5)
        elif kind == 'key' and 'keycode' in value:
            self.need_input().physical_key(value['keycode'], value.get('mods', ()))
        elif kind == 'key':
            if 'at' in value:
                # A tool hears keys only while the pointer is over the world, not the UI.
                self.need_input().move(*self.need_driver().to_screen(value['at']))
                self.frames(2)
            if value.get('down') or value.get('up'):
                # Held across the steps between (a `run` included), released by `up` or at the end.
                self.need_input().key_event(parse_key(value['code'])[1], bool(value.get('down')))
                self.frames(2)
            elif value.get('until'):
                # Pressed until the element is active, at most four times: Escape closes what
                # is open (a selection, a screen) before it opens the pause menu.
                for _ in range(4):
                    self.need_input().key(value['code'])
                    try:
                        self.need_driver().wait(value['until'], value.get('cond', 'active'), 60)
                        break
                    except DriverError:
                        continue
                else:
                    raise SpecFailure(f'{value["until"]} not {value.get("cond", "active")} after pressing {value["code"]} four times')
            else:
                self.need_input().key(value['code'])
        elif kind == 'click' and 'at' in value:
            self.need_input().click(*value['at'], button=value.get('button', 'left'), mods=value.get('mods', ()),
                                    double=bool(value.get('double')))
        elif kind == 'click' and value.get('quits'):
            # A click that ends the game (Save and Quit to the desktop): E2eDriver goes with it
            # while the click waits its last frames; an expect_exit step checks the exit.
            try:
                self.click_element(value, value.get('mods', ()), value.get('button', 'left'), bool(value.get('double')))
            except (SpecFailure, ConnectionError, OSError) as e:
                if not re.search(r'closed the connection|Connection|refused|reset', str(e)):
                    raise
        elif kind == 'click':
            self.click_element(value, value.get('mods', ()), value.get('button', 'left'), bool(value.get('double')))
        elif kind == 'press':
            # The left button held over an element (`down`, for a shot of its pressed state; a
            # `shot` after it wants `pointer: stay`), or released (`up`) away from it, in the
            # bottom-right corner, so that the press clicks nothing.
            if value.get('up'):
                self.need_input().move(WIDTH - 1, HEIGHT - 1)
                self.frames(2)
                self.need_input().button('left', False)
            else:
                # (`hover: true`: the pointer over it only.)
                element = self.element({k: value[k] for k in ('ui', 'under', 'index', 'text') if k in value})
                self.need_input().move(*element.center)
                self.frames(2)
                if not value.get('hover'):
                    self.need_input().button('left', True)
            self.frames(3)
        elif kind == 'click_world':
            where = value.get('cell', value.get('pos'))
            x, y = self.need_driver().to_screen(where, pos='pos' in value)
            self.need_input().click(x, y, button=value.get('button', 'left'), mods=value.get('mods', ()))
            if value.get('select'):
                # Clicking the same cell again picks the next object under it, as a player does
                # when a Duplicant stands in front of the building wanted.
                for clicks in range(1, 6):
                    self.frames(3)
                    selected = None
                    with contextlib.suppress(DriverError):
                        selected = self.driver.state('selected:KPrefabID.PrefabTag.Name')
                    if selected == value['select']:
                        break
                    if clicks == 5:
                        raise SpecFailure(f'no {value["select"]} selected at {where} after 5 clicks '
                                          f'(last: {selected})')
                    self.input.click(x, y)
        elif kind == 'toggle':
            element = self.element(value)
            if element.is_on is None:
                raise SpecFailure(f'{element.path} is not a toggle (no isOn)')
            if element.is_on != value['to']:
                self.need_input().click(*element.center)
                self.frames(2)
                after = self.driver.find(path=element.path)
                if not after or after[0].is_on != value['to']:
                    raise SpecFailure(f'{element.path} isOn stayed {element.is_on} after a click')
        elif kind == 'dropdown':
            self.dropdown(value)
        elif kind == 'type':
            # `text` is what to type here: the field is found by its path alone.
            self.click_element({k: value[k] for k in ('ui', 'under', 'index') if k in value})
            if value.get('clear'):
                self.input.key('Ctrl+A')
                self.input.key('Backspace')
            self.input.type_text(value['text'])
            if value.get('enter'):
                self.input.key('Return')
        elif kind == 'drag':
            driver = self.need_driver()
            start, end = driver.to_screen(value['from']), driver.to_screen(value['to'])
            more = {'via': [driver.to_screen(c) for c in value['via']]} if value.get('via') else {}
            self.need_input().drag(start, end, button=value.get('button', 'left'), mods=value.get('mods', ()),
                                   steps=value.get('steps', 8), hold=value.get('hold', 0), **more)
        elif kind == 'scroll':
            x, y = self.screen_point(value.get('at', 'center'))
            self.need_input().scroll(x, y, value['notches'])
        elif kind == 'tool':
            self.tool(value)
        elif kind == 'expect_hit':
            self.expect_hit(value)
        elif kind == 'expect_ui':
            self.expect_ui(value)
        elif kind == 'expect_state':
            self.expect_state(value, number)
        elif kind == 'ui_wait':
            more = {k: value[k] for k in ('value', 'stable', 'under') if k in value}
            self.need_driver().wait(value['path'], value.get('cond', 'exists'), value.get('frames', 300), **more)
        elif kind == 'ui_dump':
            # In the frame the menu is found settled: a menu already settled is dumped in the
            # same frame as without the wait.
            self.need_driver()
            text = self.settle_build_menu(f'ui_dump {value["name"]}',
                                          lambda: self.driver.dump_settled(value['root']))
            path = os.path.join(self.out, value['name'] + '.ui.txt')
            with open(path, 'w') as f:
                f.write(text)
            self.evidence['ui_dump'].append({'name': value['name'], 'root': value['root'],
                                             'mask_text': value.get('mask_text', []),
                                             'mask_path': value.get('mask_path', []),
                                             'ignore_fields': value.get('ignore_fields', [])})
        elif kind == 'driver':
            (command, args), = value.items()
            args = args if isinstance(args, dict) else {'n': args}
            if command == 'buildmenu.settled':
                # Waits for the build menu to catch up, as before a shot, and fails when it does not.
                self.need_driver()
                self.settle_build_menu('driver buildmenu.settled')
            else:
                self.need_driver().request(command, **args)
        else:
            raise SpecFailure(f'unknown step {kind!r}')

    def open(self, value):
        if 'key' in value:
            self.need_input().key(value['key'])
        elif 'management' in value:
            # The screen's Action through E2eDriver (ui.action), as its hotkey delivers it. OniMcp's
            # open_management no longer runs on U60 (ManagementMenu.OpenResearch returns bool there).
            self.need_driver().request('ui.action', action=MANAGEMENT_ACTIONS[value['management']])
            self.frames(2)
        else:
            # A user-menu button by its OniMcp action key (… surface=user_menu action=list), on the
            # object at a cell or with an ID.
            target = {'x': value['at'][0], 'y': value['at'][1]} if 'at' in value else \
                ({'id': value['id']} if 'id' in value else {})
            self.mcp.call('building_control', domain='side_surface', surface='user_menu', action='press',
                          actionKey=value['user_menu'], confirm=True, **target)
        self.frames(5)

    def tool(self, value):
        if value.get('key'):
            self.need_input().key(value['key'])
        elif value.get('ui'):
            self.click_element(value)
        else:
            self.click_element(self.toolbar_entry(value['toolbar']))
        self.frames(5)

    def toolbar_entry(self, name):
        """The toolbar button of a ToolMenu collection named by its text ("Sweep") or icon
        ("filtered_clear"), as a ui query. Every button is called ActionIconToggle, so it is told
        apart by its sibling index (the #N of ui.find's key)."""
        driver = self.need_driver()
        wanted = name.lower()
        names = []
        # The basic tools, then the sandbox's (shown in their place in sandbox mode).
        for row in ('basicTools', 'sandboxTools'):
            try:
                collections = driver.state(f'ToolMenu.Instance.{row}') or []
            except SpecFailure:
                if row == 'basicTools':
                    raise
                collections = []
            names += [f'{c.get("text")} ({c.get("icon")})' for c in collections]
            for i, collection in enumerate(collections):
                if wanted in (str(collection.get('text', '')).lower(), str(collection.get('icon', '')).lower()):
                    break
            else:
                continue
            break
        else:
            raise SpecFailure(f'no toolbar entry {name!r}; there are {", ".join(names)}')
        path = collection['toggle']['path']
        sibling = driver.state(f'ToolMenu.Instance.{row}[{i}].toggle.transform.GetSiblingIndex()')
        for index, element in enumerate(driver.find(path=path)):
            if element.raw.get('key', '').endswith(f'#{sibling}'):
                return {'ui': path, 'index': index}
        raise SpecFailure(f'toolbar entry {name!r} ({path} #{sibling}) is not shown')

    def dropdown(self, value):
        element = self.click_element(value)
        # Unity's (TMP_)Dropdown opens its list as a child "Dropdown List" of the dropdown.
        option = {'text': str(value['option']), 'under': element.path}
        for _ in range(30):
            if any(e.active is not False for e in self.driver.find(**option)):
                break
            self.frames(2)
        self.click_element(option)
        self.frames(2)
        after = self.driver.find(path=element.path)
        if after and after[0].text is not None and str(value['option']) not in str(after[0].text):
            raise SpecFailure(f'{element.path} shows {after[0].text!r} after choosing {value["option"]!r}')

    def screen_point(self, at):
        """A step's `at`: `center` (the screen's), a cell [x, y] or a UI path, as screen pixels."""
        if at == 'center':
            scale = getattr(self.input, 'scale', (1.0, 1.0))
            return int(WIDTH / scale[0]) // 2, int(HEIGHT / scale[1]) // 2
        if _cell(at):
            return self.need_driver().to_screen(at)
        return self.element({'ui': at}).center

    def expect_hit(self, value):
        """The top raycast hit at a point (E2eDriver's ui.at): its path matches `top`, or does not
        match `not` (text_matches patterns; no hit is the empty path), within `frames` (30)."""
        x, y = self.screen_point(value.get('at', 'center'))
        for attempt in range(max(1, value.get('frames', 30) // 3)):
            top = self.need_driver().at(x, y).get('path') or ''
            misses = []
            if 'top' in value and not text_matches(top, value['top']):
                misses.append(f'top hit at {x},{y} is {top!r}, wanted {value["top"]!r}')
            if 'not' in value and text_matches(top, value['not']):
                misses.append(f'top hit at {x},{y} is {top!r}, wanted none matching {value["not"]!r}')
            if not misses:
                return
            self.frames(3)
        raise SpecFailure('; '.join(misses))

    def expect_ui(self, value, tries=10):
        for attempt in range(tries):
            misses = []
            for path, wanted in value.items():
                # `active: false` asks about an inactive element, which only inactive: true finds.
                # any_text looks at every match, not only ui.find's default 50.
                found = self.need_driver().find(path=path, inactive='active' in wanted,
                                                limit=1000 if 'any_text' in wanted else None)
                active = [e for e in found if e.active is not False]
                element = (active or found or [None])[0]
                fields = {k: v for k, v in wanted.items() if k not in ('count', 'any_text')}
                if 'any_text' in wanted and not any(text_matches(e.text, wanted['any_text']) for e in active):
                    misses.append(f'{path}: no element with text {wanted["any_text"]!r} among {len(active)}')
                misses += [f'{path}: {m}' for m in ui_mismatches(element, fields)] if fields else []
                if 'count' in wanted:
                    # How many active elements match: a value, "OP EXPR" or {save_as: NAME}.
                    count = self.driver.count(path)
                    if not state_check(count, wanted['count'], self.saved)[0]:
                        misses.append(f'{path}: count is {count}, wanted {wanted["count"]}')
            if not misses:
                return
            self.frames(3)
        raise SpecFailure('; '.join(misses))

    def state_value(self, expr):
        if expr.startswith('file:'):
            path = os.path.join(self.player.profile, expr[5:])
            if not os.path.exists(path):
                return None
            if os.path.isdir(path):
                return len(os.listdir(path))  # a folder's value: how many entries it holds
            with open(path, encoding='utf-8', errors='replace') as f:
                return f.read()
        each = parse_aggregate(expr)
        if each:
            return self.aggregate(*each)
        cells = re.fullmatch(r'cells:(\d+),(\d+)\.\.(\d+),(\d+)::(.+)', expr)
        if cells:
            # The sum of EXPR over a rectangle of cells, {cell} standing for each cell's index.
            x1, y1, x2, y2 = (int(v) for v in cells.groups()[:4])
            width = self.need_driver().state('Grid.WidthInCells')
            total = 0
            for y in range(min(y1, y2), max(y1, y2) + 1):
                for x in range(min(x1, x2), max(x1, x2) + 1):
                    v = self.driver.state(cells.group(5).replace('{cell}', str(y * width + x)))
                    if not isinstance(v, (int, float)) or isinstance(v, bool):
                        raise SpecFailure(f'{expr}: cell {x},{y} gives {v!r}, not a number')
                    total += v
            return total
        return self.need_driver().state(expr)

    def aggregate(self, how, head, suffix, tests):
        """count:HEAD[*] if .A == X and .B != Y, or sum:HEAD[*].SUFFIX [if ...] (parse_aggregate):
        HEAD a state.get expression for an array or list (all:Clearable::), its items' members
        read in one E2eDriver request (state.each)."""
        driver = self.need_driver()
        members = [member for member, _, _ in tests] + ([suffix] if how == 'sum' else [])
        return aggregate_rows(how, tests, driver.each(head, members))

    def expect_state(self, value, number):
        misses = []
        for expr, wanted in value.items():
            have = self.state_value(expr)
            original = self.original_values.get(f'{number}:{expr}')
            ok, deferred = state_check(have, wanted, self.saved, original)
            self.values.append({'step': number, 'expr': expr, 'value': have, 'wanted': wanted, 'deferred': deferred})
            if not ok:
                misses.append(f'{expr} is {json.dumps(have)[:200]}, wanted {wanted}')
        if misses:
            raise SpecFailure('; '.join(misses))

    def shot(self, value):
        name = value['name']
        if value.get('pointer', 'away') == 'away':
            self.pointer_away()
        menu = self.at_main_menu()
        if not menu:
            self.settle_build_menu(f'shot {name}')
        meta = {'name': name, 'crop': None, 'masks': [], 'threshold': value.get('threshold')}
        if value.get('crop'):
            meta['crop'] = list(self.need_driver().find_one({'ui': value['crop']}).rect)
            # An empty crop compares nothing: in ours it fails as "off the image", too late to
            # fix the spec without blessing the original again.
            if meta['crop'][2] < 1 or meta['crop'][3] < 1:
                raise SpecFailure(f'shot {name}: crop {value["crop"]} is empty ({meta["crop"]})')
        if value.get('mask'):
            meta['masks'] = self.rects(value['mask'])
        path = self.menu_grab(name) if menu else screenshot(self.mcp, self.out, name)
        if not path:
            raise SpecFailure(f'shot {name}: OniMcp made no screenshot')
        scale = getattr(self.input, 'scale', (1.0, 1.0))
        if menu and scale != (1.0, 1.0):
            # The display's grab shows the game's screen stretched (`scaled_screen`): the rects,
            # in the game's pixels, are scaled to the grab's.
            stretch = lambda r: [r[0] * scale[0], r[1] * scale[1], r[2] * scale[0], r[3] * scale[1]]
            meta['crop'] = meta['crop'] and stretch(meta['crop'])
            meta['masks'] = [stretch(r) for r in meta['masks']]
        meta['file'] = os.path.basename(path)
        with open(os.path.join(self.out, name + '.shot.json'), 'w') as f:
            json.dump(meta, f)
        self.evidence['shot'].append(meta)

    def world_shot(self, value):
        name = value['name']
        driver = self.need_driver()
        camera = {}
        if 'at' in value:
            camera['cell'] = list(value['at'])
        if 'ortho' in value:
            camera['ortho'] = value['ortho']
        if value.get('max'):  # raise the camera controller's maximum orthographic size first
            camera['max'] = value['max']
        if camera:
            driver.camera_set(**camera)
        self.pointer_away()
        masks = self.rects([m for m in value.get('mask', []) if not _rect(m)]) + \
            [[float(v) for v in m] for m in value.get('mask', []) if _rect(m)]
        hide = value.get('hide_ui', True)
        if hide:
            driver.canvas(True)
            self.frames(2)
        try:
            path = screenshot(self.mcp, self.out, name)
        finally:
            if hide:
                driver.canvas(False)
        if not path:
            raise SpecFailure(f'world_shot {name}: OniMcp made no screenshot')
        meta = {'name': name, 'crop': None, 'masks': masks, 'threshold': value.get('threshold'),
                'file': os.path.basename(path), 'world': True}
        with open(os.path.join(self.out, name + '.shot.json'), 'w') as f:
            json.dump(meta, f)
        self.evidence['world_shot'].append(meta)

    def expect_exit(self, value):
        """The player quits by itself (Quit to Desktop): waits for its process to end, with
        `code` (default 0; null for any). Only steps that need no player may follow (file_shot,
        expect_state on file: paths)."""
        end = time.time() + float(value.get('seconds', 120))
        while self.player.alive():
            if time.time() > end:
                raise SpecFailure(f'expect_exit: the player still runs after {value.get("seconds", 120)} s')
            self.player.watchdog.progress()
            time.sleep(0.5)
        code = self.player.process.returncode
        if value.get('code', 0) is not None and code != value.get('code', 0):
            raise SpecFailure(f'expect_exit: the player exited with {code}, wanted {value.get("code", 0)}')
        self.exited = True

    def file_shot(self, value):
        """An image the game wrote into the run's profile (a save's preview, a timelapse frame),
        copied into the evidence and compared as a world_shot is (5 % by default; a size more than
        2 px off fails). `path` is relative to the profile; of a glob's matches the newest."""
        name = value['name']
        pattern = os.path.join(glob.escape(self.player.profile), value['path'])
        # The game writes it from a worker thread (U60's Timelapser): wait until it is there and
        # its size holds for a second.
        deadline, last = time.time() + float(value.get('wait', 60)), None
        while True:
            matches = glob.glob(pattern)
            source = max(matches, key=os.path.getmtime) if matches else None
            size = os.path.getsize(source) if source else None
            if source and size and (source, size) == last:
                break
            if time.time() > deadline:
                raise SpecFailure(f'file_shot {name}: no complete {value["path"]} in the profile')
            last = (source, size)
            time.sleep(1)
        target = os.path.join(self.out, name + '.png')
        shutil.copyfile(source, target)
        meta = {'name': name, 'crop': None, 'masks': [], 'threshold': value.get('threshold'),
                'file': os.path.basename(target), 'world': True,
                'source': os.path.relpath(source, self.player.profile)}
        with open(os.path.join(self.out, name + '.shot.json'), 'w') as f:
            json.dump(meta, f)
        self.evidence['world_shot'].append(meta)

    # -- the run
    def execute(self):
        spec = self.spec
        fixture = fixture_of(spec)
        # E2eDriver goes into every run it is built for (the same in both players); a spec with
        # driver steps needs it.
        uses_driver = needs_driver(spec) or bool(self.env.get('E2E_DRIVER'))
        outcome, label, failed_kind = None, 'start', None
        self.player = None
        records = None
        try:
            if uses_driver and not self.env.get('E2E_DRIVER'):
                raise SpecFailure('the spec needs E2eDriver, and E2E_DRIVER names no build of it')
            if fixture and not os.path.exists(fixture):
                raise SpecFailure(f'no fixture {fixture}')
            self.player = Player(self.kind, self.out, self.env, fixture, self.lane, spec['mods'], spec.get('options'),
                                 driver=uses_driver, expansion1=spaced_out(spec), translation=spec.get('translation'),
                                 language=spec.get('language'), keyboard=spec.get('keyboard'),
                                 tweaks_menu=spec.get('tweaks_menu', False),
                                 blocked_plugins=spec.get('blocked_plugins'), stray_plugins=spec.get('stray_plugins'),
                                 data_collection=spec.get("data_collection", False),
                                 extra_saves=spec.get('extra_saves', ()), display_dpi=spec.get('display_dpi'),
                                 workshop=spec.get('workshop'), update_check=spec.get('update_check'),
                                 game_tweaks=spec.get('game_tweaks', ()), extra_env=spec.get('env'))
            self.mcp.on_reply = self.player.watchdog.progress
            records = open(os.path.join(self.out, 'results.jsonl'), 'w')
            started = time.time()
            self.player.start()
            self.log(f'  lane {self.lane.number}: OniMcp :{self.lane.mcp_port}, E2eDriver :{self.lane.driver_port}, '
                     f'display {self.player.display}')
            self.player.load(self.mcp)
            self.log(f'  loaded {spec["from"]} in {time.time() - started:.0f} s')
            if uses_driver:
                self.driver = Driver(self.lane.driver_port).connect(120, self.player.alive)
                self.driver.on_reply = self.player.watchdog.progress
                self.dismiss_load_dialogs()
            steps = [('setup', i, s) for i, s in enumerate(spec['setup'], 1)] + \
                    [('steps', i, s) for i, s in enumerate(spec['steps'], 1)]
            for block, number, step in steps:
                (kind_of_step, value), = step.items()
                failed_kind = kind_of_step
                label = number if block == 'steps' else f'setup {number}'
                began = time.time()
                if spec.get('scaled_screen') and self.driver:
                    self.update_scale()
                self.step(kind_of_step, value, label)
                records.write(json.dumps({'step': label, kind_of_step: value, 'wall': round(time.time() - began, 1)}) + '\n')
                records.flush()
            failed_kind = None
            label = 'end'
            # A look for a person, not a check: OniMcp cannot take it at the main menu (a spec
            # of a menu mod ends there).
            if not self.exited:
                with contextlib.suppress(SpecFailure):
                    screenshot(self.mcp, self.out, 'end')
            if not self.player.alive() and not self.exited:
                raise SpecFailure('the player is not alive at the end')
            errors = driver_errors(self.player.log) if uses_driver else []
            if errors:
                raise SpecFailure(f'E2eDriver logged {len(errors)} error(s): {errors[0][:300]}')
        except (SpecFailure, urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
            outcome = f'step {label}: {e}'
            watchdog = self.player and self.player.watchdog
            if watchdog and self.player.alive() and watchdog.stalled() >= watchdog.HANG_ON_FAIL:
                watchdog.capture(f'no log output or OniMcp/E2eDriver reply for {watchdog.stalled():.0f} s '
                                 'when the step failed')
            if not (watchdog and watchdog.hung):  # a hung player takes no screenshot
                with contextlib.suppress(Exception):
                    screenshot(self.mcp, self.out, 'failure')
        finally:
            if self.hold and self.player and self.player.alive() and (self.hold == 'end' or outcome):
                self.held()
            if self.input:
                with contextlib.suppress(Exception):
                    self.input.close()
            if self.driver:
                self.driver.close()
            if self.player:
                self.player.stop()
            if records:
                records.close()
        if self.player and self.player.watchdog.hung:
            outcome = self.player.watchdog.hung + (f'; {outcome}' if outcome else '')
            self.log(f'  {self.player.watchdog.hung}')
        if os.path.isdir(self.out):
            with open(os.path.join(self.out, 'values.json'), 'w') as f:
                json.dump(self.values, f, indent=1, default=str)
        return {'outcome': outcome, 'failed_step_kind': failed_kind, 'run': self.run_id,
                'signatures': self.player.signatures() if self.player else set(),
                'saves': self.saves, 'out': self.out, 'values': self.values, 'evidence': self.evidence,
                'lane': self.player.lane_record() if self.player else {'lane': self.lane.number},
                'options_written': self.player.option_files if self.player else [],
                'log': self.player.log if self.player else None}


    HOLD_SECONDS = 3600
    HOLD_YIELD_SECONDS = 600

    def held(self):
        """Keeps the player running (--hold) until its hold file is removed, the player exits or
        an hour passes, so its OniMcp and E2eDriver can be queried by hand. A perf job waiting to
        start (perf.wanted) ends the hold HOLD_YIELD_SECONDS later: a held spec keeps perf.lock
        shared, and QA's perf stage waits for it."""
        flag = os.path.join(self.out, 'hold')
        open(flag, 'w').close()
        self.log(f'  holding: OniMcp :{self.lane.mcp_port}, E2eDriver :{self.lane.driver_port}, display '
                 f'{self.player.display}; rm {flag} to go on')
        if self.driver:
            self.driver.close()  # others may connect meanwhile; reconnected below
        self.player.watchdog.paused = True  # a held player waits for a person, not hung
        end = time.time() + self.HOLD_SECONDS
        wanted_since = None
        while os.path.exists(flag) and self.player.alive() and time.time() < end:
            if perf_wanted(shared_dir()):
                if wanted_since is None:
                    wanted_since = time.time()
                    self.log(f'  a perf job waits: the hold ends in {self.HOLD_YIELD_SECONDS // 60} min')
                elif time.time() - wanted_since > self.HOLD_YIELD_SECONDS:
                    self.log('  hold ended for the waiting perf job')
                    break
            else:
                wanted_since = None
            time.sleep(2)
        with contextlib.suppress(OSError):
            os.remove(flag)


def driver_errors(log):
    """E2eDriver's own error lines in a player's log (§5.2)."""
    if not log or not os.path.exists(log):
        return []
    with open(log, encoding='utf-8', errors='replace') as f:
        return [l.strip() for l in f if 'E2eDriver' in l and ('[ERROR]' in l or re.search(r'\b\w+Exception\b', l))]


# ---------------------------------------------------------------- known findings

KNOWN_FINDINGS = os.path.join(SPECS, 'known-findings.yaml')
FINDING_KEYS = {'id', 'description', 'link', 'component', 'fields', 'path', 'specs', 'original', 'ours', 'presence'}


def as_list(value):
    return [] if value is None else list(value) if isinstance(value, (list, tuple)) else [value]


def check_findings(findings):
    """The problems of a known-findings list, as readable lines."""
    if not isinstance(findings, list):
        return ['known findings are a list']
    problems, ids = [], set()
    for number, f in enumerate(findings, 1):
        where = f'finding {number}' + (f' ({f.get("id")})' if isinstance(f, dict) and f.get('id') else '')
        if not isinstance(f, dict):
            problems.append(f'{where}: a mapping')
            continue
        problems += [f'{where}: unknown key {k!r}' for k in f if k not in FINDING_KEYS]
        if not re.fullmatch(r'[a-z0-9]+(-[a-z0-9]+)*', str(f.get('id', ''))):
            problems.append(f'{where}: `id` is lowercase words joined by dashes')
        elif f['id'] in ids:
            problems.append(f'{where}: `id` is used twice')
        ids.add(f.get('id'))
        for key in ('description', 'link'):
            if not isinstance(f.get(key), str) or not f[key].strip():
                problems.append(f'{where}: needs `{key}`')
        if f.get('presence'):
            if not f.get('path'):
                problems.append(f'{where}: a presence finding needs `path`')
        elif not f.get('component') or not f.get('fields'):
            problems.append(f'{where}: needs `component` and `fields` (or `presence: true` and `path`)')
        for key in ('original', 'ours'):
            if key in f:
                try:
                    re.compile(f[key])
                except (re.error, TypeError) as e:
                    problems.append(f'{where}: `{key}` {e}')
    return problems


def load_findings(path=None):
    """tests/e2e/known-findings.yaml: reviewed engine differences a ui_dump diff may cite."""
    path = path or KNOWN_FINDINGS
    if not os.path.exists(path):
        return []
    findings = load_yaml(path) or []
    problems = check_findings(findings)
    if problems:
        raise SpecError(f'{path}:\n  ' + '\n  '.join(problems))
    return findings


def glob_regex(pattern):
    """An E2eDriver path glob (* within a name, ** any number of levels) as a regex."""
    out = ''
    for part in re.split(r'(\*\*/?|\*)', pattern.strip('/')):
        if part.startswith('**'):
            out += '(?:.*/)?' if part.endswith('/') else '.*'
        elif part == '*':
            out += '[^/]*'
        else:
            out += re.escape(part)
    return re.compile(out + r'\Z')


def path_matches(key, patterns):
    """Whether a dump key (/A#0/B#2/C#0) matches a glob, with or without its sibling numbers."""
    key = key.strip('/')
    plain = re.sub(r'#\d+(?=/|$)', '', key)
    return any(glob_regex(p).match(key) or glob_regex(p).match(plain) for p in as_list(patterns))


MEMBER = re.compile(r'(\w+)(?:=|\s+)("(?:[^"\\]|\\.)*"|[^\s"]+)')


def members(text):
    """A dump component's NAME=VALUE (or, for the rect, NAME VALUE) members, or None."""
    found, rest = {}, MEMBER.sub('', text or '')
    if rest.strip():
        return None
    for name, value in MEMBER.findall(text or ''):
        found[name] = value
    return found


def parse_difference(line):
    """A ui-diff.py line as (kind, key, component, original, ours): kind 'only-original',
    'only-ours' or 'changed'."""
    match = re.match(r'only in (first|second): (.+)$', line)
    if match:
        return ('only-original' if match.group(1) == 'first' else 'only-ours'), match.group(2), None, None, None
    head, _, rest = line.partition('\n')
    match = re.fullmatch(r'(.+) \[(.+)\]', head)
    values = rest.split('\n')
    if not match or len(values) != 2:
        return 'changed', head, None, None, None
    return 'changed', match.group(1), match.group(2), values[0].removeprefix('  - '), values[1].removeprefix('  + ')


def finding_for(line, findings, spec_name=None):
    """The id of the first known finding that explains one ui-diff difference, or None. A
    finding explains a changed component when the component and the element path match and
    every member that differs is one of its fields (and the values match its `original` and
    `ours` regexes); a presence finding explains an element only one player has."""
    kind, key, component, a, b = parse_difference(line)
    for f in findings:
        if f.get('specs') and spec_name not in as_list(f['specs']):
            continue
        if f.get('path') and not path_matches(key, f['path']):
            continue
        if kind != 'changed':
            if f.get('presence') in (True, kind):
                return f['id']
            continue
        if f.get('presence') or component is None:
            continue
        if not any(fnmatch_name(component, c) for c in as_list(f['component'])):
            continue
        ma, mb = members(a), members(b)
        if ma is None or mb is None:
            continue
        differing = {n for n in ma.keys() | mb.keys() if ma.get(n) != mb.get(n)}
        if not differing or not differing <= set(as_list(f['fields'])):
            continue
        if f.get('original') and not re.search(f['original'], a):
            continue
        if f.get('ours') and not re.search(f['ours'], b):
            continue
        return f['id']
    return None


def fnmatch_name(name, pattern):
    import fnmatch
    return fnmatch.fnmatchcase(name, pattern)


# ---------------------------------------------------------------- differential checks (§5.3)

def log_matches(log, patterns):
    if not patterns:
        return {}
    text = open(log, encoding='utf-8', errors='replace').read() if log and os.path.exists(log) else ''
    return {p: re.search(p, text, re.M) is not None for p in patterns}


class Reference:
    """The original's evidence for a spec: from this invocation's original runs, or blessed into
    out/e2e-refs/SPEC."""

    def __init__(self, folder, signatures, log_found, values, source, run=None):
        self.folder, self.signatures, self.log_found, self.values, self.source = folder, signatures, log_found, values, source
        self.run = run  # the original run's id, which a results record cites

    @staticmethod
    def from_runs(spec, results):
        passed = [r for r in results if not r['outcome']]
        if not passed:
            return None
        first = passed[0]
        signatures = set().union(*(r['signatures'] for r in results))
        return Reference(first['out'], signatures, log_matches(first['log'], spec.get('require_log', [])),
                         values_by_step(first['values']), 'original run', first.get('run'))

    @staticmethod
    def blessed(spec):
        folder = os.path.join(REFS, spec['name'])
        if not os.path.isdir(folder):
            return None
        read = lambda name, default: json.load(open(os.path.join(folder, name))) if os.path.exists(os.path.join(folder, name)) else default
        return Reference(folder, set(read('signatures.json', [])), read('log.json', {}),
                         values_by_step(read('values.json', [])), folder, read('source.json', {}).get('run'))


def values_by_step(values):
    return {f'{v["step"]}:{v["expr"]}': v['value'] for v in values}


def bless(spec, result):
    """An original run's evidence into out/e2e-refs/SPEC (one folder per spec, whichever run
    blessed it; source.json names that run). It is written beside and then swapped in, so a run
    of ours never reads half of it."""
    folder = os.path.join(REFS, spec['name'])
    temp = f'{folder}.new-{os.getpid()}'
    shutil.rmtree(temp, ignore_errors=True)
    os.makedirs(temp)
    for name in os.listdir(result['out']):
        if name.endswith(('.ui.txt', '.shot.json', '.png', '.jpg')):
            shutil.copy(os.path.join(result['out'], name), temp)
    json.dump(sorted(result['signatures']), open(os.path.join(temp, 'signatures.json'), 'w'), indent=1)
    json.dump(log_matches(result['log'], spec.get('require_log', [])), open(os.path.join(temp, 'log.json'), 'w'), indent=1)
    json.dump(result['values'], open(os.path.join(temp, 'values.json'), 'w'), indent=1, default=str)
    json.dump({'run': result.get('run'), 'out': result['out'], 'date': time.strftime('%Y-%m-%dT%H:%M:%S')},
              open(os.path.join(temp, 'source.json'), 'w'), indent=1)
    old = f'{folder}.old-{os.getpid()}'
    if os.path.isdir(folder):
        os.rename(folder, old)
    os.rename(temp, folder)
    shutil.rmtree(old, ignore_errors=True)
    return folder


def differential(spec, result, reference, findings=None):
    """Ours against the original's evidence: {check: details}, and the reasons it fails. A
    ui_dump difference a known finding explains (FINDINGS, default known-findings.yaml) is no
    reason; its id goes into checks['findings'], which makes the run a warning."""
    findings = load_findings() if findings is None else findings
    checks, reasons, cited = {}, [], set()
    new = load_script('qa-tools.py').unknown(result['signatures'], reference.signatures)
    checks['signatures'] = {'new': new, 'reference': len(reference.signatures)}
    if new:
        reasons.append(f'{len(new)} log signature(s) the original lacks')
    ours_log = log_matches(result['log'], spec.get('require_log', []))
    missing = [p for p, found in reference.log_found.items() if found and not ours_log.get(p)]
    checks['require_log'] = {'missing': missing, 'ours': ours_log, 'original': reference.log_found}
    if missing:
        reasons.append('required log lines missing: ' + ', '.join(missing))
    ui_diff = load_script('ui-diff.py')
    tolerance = spec.get('tolerance', UI_TOLERANCE)
    checks['ui_dump'] = {}
    for dump in result['evidence']['ui_dump']:
        name = dump['name']
        a, b = os.path.join(reference.folder, name + '.ui.txt'), os.path.join(result['out'], name + '.ui.txt')
        if not os.path.exists(a):
            checks['ui_dump'][name] = {'ok': None, 'note': 'no reference dump'}
            continue
        masks = list(spec.get('mask_text', [])) + list(dump.get('mask_text', []))
        fields = list(IGNORE_FIELDS) + list(spec.get('ignore_fields', [])) + list(dump.get('ignore_fields', []))
        paths = dump.get('mask_path', [])
        lines = ui_diff.diff(masked_paths(without_fields(ui_diff.parse(a), fields), paths),
                             masked_paths(without_fields(ui_diff.parse(b), fields), paths),
                             tolerance=tolerance, mask_text=masks)
        differences, known = [], {}
        for line in lines:
            ident = finding_for(line, findings, spec.get('name'))
            if ident:
                known.setdefault(ident, []).append(line)
            else:
                differences.append(line)
        cited |= set(known)
        checks['ui_dump'][name] = {'ok': not differences, 'differences': len(differences), 'first': differences[:20],
                                   'known': {i: len(v) for i, v in sorted(known.items())},
                                   'known_first': {i: v[:3] for i, v in sorted(known.items())}}
        if differences:
            reasons.append(f'ui_dump {name}: {len(differences)} difference(s)')
    qa_tools = load_script('qa-tools.py')
    thresholds = spec.get('thresholds', {})
    for kind, default in (('shot', SHOT_THRESHOLD), ('world_shot', WORLD_THRESHOLD)):
        checks[kind] = {}
        for meta in result['evidence'][kind]:
            name = meta['name']
            ref_meta_path = os.path.join(reference.folder, name + '.shot.json')
            if not os.path.exists(ref_meta_path):
                checks[kind][name] = {'ok': None, 'note': 'no reference shot'}
                continue
            ref_meta = json.load(open(ref_meta_path))
            crop_a, crop_b = ref_meta.get('crop'), meta.get('crop')
            masks = relative_masks(ref_meta.get('masks', []), crop_a) + relative_masks(meta.get('masks', []), crop_b)
            diff_png = os.path.join(result['out'], name + '.diff.png')
            compared = qa_tools.compare_images(os.path.join(reference.folder, ref_meta['file']),
                                               os.path.join(result['out'], meta['file']), diff_png,
                                               crop=crop_a, crop_new=crop_b, masks=masks)
            threshold = meta.get('threshold') or thresholds.get(kind) or default
            ok = compared['fraction'] <= threshold
            checks[kind][name] = dict(compared, threshold=threshold, ok=ok, diff=diff_png)
            if not ok:
                reasons.append(f'{kind} {name}: {compared["fraction"]:.2%} of pixels differ (> {threshold:.0%})')
    deferred = []
    for v in result['values']:
        if not v.get('deferred'):
            continue
        original = reference.values.get(f'{v["step"]}:{v["expr"]}')
        if original is None:
            deferred.append({'step': v['step'], 'expr': v['expr'], 'ok': None, 'note': 'no original value'})
            continue
        wanted = v['wanted']
        if isinstance(wanted, list):  # only the checks that wait for the original; ours ran the rest
            wanted = [w for w in wanted if isinstance(w, str) and re.search(r'\boriginal\b', w)]
        ok, _ = state_check(v['value'], wanted, {}, original)
        deferred.append({'step': v['step'], 'expr': v['expr'], 'ours': v['value'], 'original': original, 'ok': ok})
        if not ok:
            reasons.append(f'step {v["step"]}: {v["expr"]} is {v["value"]}, wanted {v["wanted"]} ({original} in the original)')
    checks['expect_original'] = deferred
    checks['findings'] = sorted(cited)
    return checks, reasons


def masked_paths(elements, patterns):
    """A parsed dump with each match of PATTERNS in its element paths replaced by '#', for names
    that carry a number varying from run to run (such as an instance id)."""
    for p in patterns:
        elements = {re.sub(p, '#', key): fields for key, fields in elements.items()}
    return elements


def without_fields(elements, names):
    """A parsed dump without the NAME=VALUE members NAMES (such as a Graphic's depth, which
    counts the graphics drawn before it and so moves with content elsewhere on the canvas), and
    without the geometry's NAME X,Y,... parts among them (world, screen: where an element is on
    screen, for content that scrolls or pans to a place that varies)."""
    if not names:
        return elements
    pattern = re.compile(r'\s*\b(?:' + '|'.join(re.escape(n) for n in names) + r')(?:=\S+|\s+-?\d[-\d.,]*)')
    return {key: {c: pattern.sub('', v) for c, v in fields.items()} for key, fields in elements.items()}


def relative_masks(masks, crop):
    if not crop:
        return [list(m) for m in masks]
    return [[m[0] - crop[0], m[1] - crop[1], m[2], m[3]] for m in masks]


# ---------------------------------------------------------------- the results record (§7)

RESULTS = os.path.join(ROOT, os.environ.get('ONI_RESULTS_DIR') or os.path.join(SPECS, 'results'))
OURS_RUNS_NEEDED = 2  # Played: once in the original, twice in ours (§2)
VERDICTS = ('played', 'warning', 'failed', 'pending')


def run_summary(record):
    """What a results record keeps of one run: its verdict and the numbers behind it."""
    checks = record.get('checks') or {}
    summary = {'run': record.get('run'), 'index': record['index'], 'verdict': record['verdict'],
               'date': record.get('started'), 'lane': record.get('lane')}
    if record.get('reasons'):
        summary['reasons'] = [r[:300] for r in record['reasons'][:5]]
    if record['player'] == 'original':
        summary['signatures'] = record.get('signature_count', 0)
        return summary
    summary['reference_run'] = record.get('reference_run')
    summary['new_signatures'] = len(checks.get('signatures', {}).get('new', []))
    summary['findings'] = checks.get('findings', [])
    summary['ui_dump'] = {n: {'differences': c.get('differences'), **({'known': c['known']} if c.get('known') else {})}
                          for n, c in checks.get('ui_dump', {}).items()}
    summary['shots'] = {n: c.get('fraction') for kind in ('shot', 'world_shot') for n, c in checks.get(kind, {}).items()}
    return summary


def record_verdict(runs):
    """A spec's verdict from its runs: played, warning (passed, with differences known
    findings explain), failed (in ours), or pending (no passing original run, or fewer than
    OURS_RUNS_NEEDED runs of ours against the newest one), and why."""
    originals = runs.get('original', [])
    passed = [r for r in originals if r['verdict'] == 'pass']
    if not originals:
        return 'pending', 'no run in the original'
    if not passed:
        return 'pending', 'the spec fails in the original: a broken spec, or a mod broken upstream'
    current = {r['run'] for r in passed}
    ours = [r for r in runs.get('ours', []) if r.get('reference_run') in current]
    if len(ours) < OURS_RUNS_NEEDED:
        return 'pending', (f'{len(ours)} run(s) of ours against original run {", ".join(sorted(current))}; '
                           f'{OURS_RUNS_NEEDED} needed')
    if any(r['verdict'] == 'fail' for r in ours):
        return 'failed', 'a run of ours failed'
    if any(r['verdict'] == 'warn' for r in ours):
        return 'warning', 'known findings: ' + ', '.join(sorted({f for r in ours for f in r.get('findings', [])}))
    return 'played', 'passed once in the original and twice in ours'


def mod_hashes(ids):
    """The SHA-256 of each mod's DLLs (out/ws/ID, top level), so a record says which build it played."""
    import hashlib
    ws = os.environ.get('ONI_WS') or os.path.join(ROOT, 'out', 'ws')
    found = {}
    for i in ids:
        folder = os.path.join(ws, i)
        dlls = sorted(n for n in os.listdir(folder) if n.lower().endswith('.dll')) if os.path.isdir(folder) else []
        found[i] = {n: hashlib.sha256(open(os.path.join(folder, n), 'rb').read()).hexdigest() for n in dlls}
    return found


def head_commit():
    result = subprocess.run(['git', '-C', ROOT, 'rev-parse', '--short', 'HEAD'], capture_output=True, text=True)
    return result.stdout.strip() or None


def update_record(spec, records, note=None, folder=None, commit=None, date=None, hashes=None):
    """tests/e2e/results/SPEC.json, the committed record of a spec (§7): this invocation's runs
    replace the record's runs of the same player, the others are kept, and the verdict follows
    from them. NOTE replaces the note (else it is kept). Returns the record."""
    folder = folder or RESULTS
    path = os.path.join(folder, spec['name'] + '.json')
    old = {}
    if os.path.exists(path):
        with open(path) as f:
            old = json.load(f)
    runs = {k: list(v) for k, v in old.get('runs', {}).items()}
    for kind in ('original', 'ours'):
        mine = [run_summary(r) for r in records if r['spec'] == spec['name'] and r['player'] == kind
                and r['verdict'] != 'skipped']
        if mine:
            runs[kind] = mine
    runs = {'original': runs.get('original', []), 'ours': runs.get('ours', [])}
    verdict, why = record_verdict(runs)
    record = {
        'spec': spec.get('path') or spec['name'] + '.yaml',
        'mods': spec['mods'],
        'verdict': verdict,
        'why': why,
        'findings': sorted({f for r in runs['ours'] for f in r.get('findings', [])}) if verdict == 'warning' else [],
        'note': old.get('note', '') if note is None else note,
        'updated': date or time.strftime('%Y-%m-%d'),
        'commit': commit if commit is not None else head_commit(),
        'mod_dlls': hashes if hashes is not None else mod_hashes(spec['mods']),
        'runs': runs,
    }
    os.makedirs(folder, exist_ok=True)
    temp = f'{path}.{os.getpid()}'
    with open(temp, 'w') as f:
        json.dump(record, f, indent=1, ensure_ascii=False)
        f.write('\n')
    os.replace(temp, path)
    return record


# ---------------------------------------------------------------- main

def usage():
    sys.exit(__doc__)


def parse_args(argv):
    opts = {'players': 'ours', 'repeat': 1, 'bless': False, 'hold': None, 'lanes': 1, 'first_lane': FIRST_LANE,
            'last_lane': None, 'displays': None, 'xvfb_run': False, 'perf_wait': True, 'results': None, 'check': False,
            'status': False, 'clean': False, 'record': False, 'note': None, 'max_wait': None, 'run_id': None, 'names': []}
    i = 0
    takes = {'--player': 'players', '--repeat': 'repeat', '--lanes': 'lanes', '--first-lane': 'first_lane',
             '--last-lane': 'last_lane', '--displays': 'displays', '--results': 'results', '--note': 'note',
             '--max-wait': 'max_wait', '--run-id': 'run_id'}
    while i < len(argv):
        a = argv[i]
        if a in takes:
            if i + 1 >= len(argv):
                raise SpecError(f'{a} needs a value')
            opts[takes[a]] = argv[i + 1]
            i += 1
        elif a == '--bless':
            opts['bless'] = True
        elif a in ('--hold', '--hold-on-fail'):
            opts['hold'] = 'end' if a == '--hold' else 'fail'
        elif a == '--no-perf-wait':
            opts['perf_wait'] = False
        elif a == '--xvfb-run':
            opts['xvfb_run'] = True
        elif a == '--record':
            opts['record'] = True
        elif a == '--check':
            opts['check'] = True
        elif a == '--status':
            opts['status'] = True
        elif a == '--clean':
            opts['clean'] = True
        elif a.startswith('-'):
            raise SpecError(f'unknown option {a}')
        else:
            opts['names'].append(a)
        i += 1
    if opts['players'] not in ('ours', 'original', 'both'):
        raise SpecError('--player is ours, original or both')
    for key in ('repeat', 'lanes', 'first_lane', 'last_lane'):
        if opts[key] is None:
            continue
        try:
            opts[key] = int(opts[key])
        except ValueError:
            raise SpecError(f'--{key.replace("_", "-")} takes a number')
    if opts['max_wait'] is None and os.environ.get('E2E_MAX_WAIT'):
        opts['max_wait'] = os.environ['E2E_MAX_WAIT']
    if opts['max_wait'] is not None:
        try:
            opts['max_wait'] = float(opts['max_wait']) * 60
        except ValueError:
            raise SpecError('--max-wait takes minutes')
    if opts['run_id'] is not None and not re.fullmatch(r'[\w.-]+', opts['run_id']):
        raise SpecError('--run-id is letters, digits, dots and dashes')
    if not 1 <= opts['lanes'] <= MAX_LANES:
        raise SpecError(f'--lanes is 1..{MAX_LANES}')
    last = LAST_LANE if opts['last_lane'] is None else opts['last_lane']
    if not opts['first_lane'] + opts['lanes'] - 1 <= last:
        raise SpecError('--first-lane .. --last-lane leaves fewer lanes than --lanes')
    if opts['first_lane'] < 0 or opts['repeat'] < 1:
        raise SpecError('--first-lane is 0 or more, --repeat 1 or more')
    if opts['displays'] is not None:
        try:
            opts['displays'] = parse_displays(opts['displays'])
        except ValueError as e:
            raise SpecError(str(e))
        if len(opts['displays']) < opts['lanes']:
            raise SpecError('--displays names fewer displays than --lanes')
    if opts['record'] and (opts['hold'] == 'end'):
        raise SpecError('--record takes no --hold (a held run is exploration, not a verdict)')
    if not opts['names'] and not opts['status'] and not opts['clean']:
        raise SpecError('no spec named')
    return opts


def report(record):
    lines = []
    head = f'{record["spec"]} in {record["player"]} ({record["index"]})'
    if record['verdict'] == 'pass':
        lines.append(f'ok   {head} ({record["out"]})')
    elif record['verdict'] == 'warn':
        lines.append(f'warn {head}: known findings ' + ', '.join(record.get('findings', [])) + f' ({record["out"]})')
    elif record['verdict'] == 'skipped':
        lines.append(f'skip {head}: {record["reasons"][0]}')
    else:
        lines.append(f'FAIL {head}: ' + '; '.join(record['reasons']))
        for s in record.get('checks', {}).get('signatures', {}).get('new', []):
            lines.append('     ' + s)
    if record['player'] == 'original' and record.get('signature_count'):
        lines.append(f'     the original logged {record["signature_count"]} signature(s)')
    return '\n'.join(lines)


def main(argv):
    # Internal: the watcher of a runner or qa.sh, and qa.sh's teardown of its players.
    if argv[:1] == ['--reap-after'] and len(argv) == 3:
        reap_after(int(argv[1]), int(argv[2]))
        return 0
    if argv[:1] == ['--teardown-owner'] and len(argv) == 2 and re.fullmatch(r'\d+:\d+', argv[1]):
        teardown_owner(argv[1])
        return 0
    os.environ.pop(OWNER_ENV, None)  # this runner's children carry its own
    try:
        opts = parse_args(argv)
        specs = [load_spec(n) for n in opts['names']]
        findings = load_findings()
    except (SpecError, ValueError) as e:
        print(f'e2e: {e}', file=sys.stderr)
        usage()
    if opts['clean']:
        clean()
        if not specs and not opts['status']:
            return 0
    if opts['status']:
        for line in status():
            print(line)
        if not specs:
            return 0
    if opts['check']:
        for spec in specs:
            print(f'ok   {spec["name"]}: {len(spec["setup"])} setup and {len(spec["steps"])} steps'
                  + (', needs E2eDriver' if needs_driver(spec) else ''))
        print(f'ok   {len(findings)} known finding(s)')
        return 0
    env = environment()
    kinds = ['original', 'ours'] if opts['players'] == 'both' else [opts['players']]
    needed = ['QA_U57_GAME', 'QA_U57_STEAM', 'E2E_ONIMCP'] + (['E2E_ORIGINAL'] if 'original' in kinds else [])
    missing = [n for n in needed if not env.get(n)]
    if missing:
        print('e2e: skipped, needs ' + ', '.join(missing) + ' (out/local-tools/qa.env)')
        return 0
    # Its players and Xvfbs end with it, however it ends (see "leftovers").
    install_teardown()
    start_reaper()
    run_id = opts['run_id'] or new_run_id()
    results_path = opts['results'] or os.path.join(OUT, 'runs', run_id + '.json')
    say(f'== run {run_id}: lock directory {shared_dir()}')
    queue = JobQueue(plan_jobs(specs, kinds, opts['repeat']))
    results, records, state = {}, [], threading.Lock()
    budget = Budget()

    def process(job, lane, log):
        spec, kind, index = job['spec'], job['kind'], job['index']
        record = {'spec': spec['name'], 'player': kind, 'index': index, 'mods': spec['mods'], 'run': run_id,
                  'started': time.strftime('%Y-%m-%dT%H:%M:%S')}
        with state:
            originals = [results[d] for d in job['after'] if d in results]
        reference = None
        if kind == 'ours':
            reference = Reference.from_runs(spec, originals) if job['after'] else Reference.blessed(spec)
            if job['after'] and reference is None:
                return dict(record, verdict='skipped', reasons=['the spec fails in the original'], out=None)
        if spec['mods']:
            # The mod gate (scripts/mod-gate.py), before a lane is taken; the install checks again.
            ws = ws_mods()
            for m in spec['mods']:
                ws.unpack(m)
            skipped = {i: v for i, v in ws.gate(spec['mods']).items() if v['verdict'] != 'pass'}
            if skipped:
                return dict(record, verdict='skipped', reasons=[str(ws.GateSkipped(skipped))], out=None)
        log(f'== {spec["name"]} in {kind} ({index}/{opts["repeat"]})')
        gb = player_gb(spec, fixture_of(spec))
        job_name = f'{spec["name"]} in {kind} ({index}/{opts["repeat"]}), run {run_id}'
        lane.update(state=f'starting {job_name}', job=job_name)

        def waits(line):  # a wait's lines also say, in the lane's lock, what the lane waits for
            log(line)
            if line.startswith('waiting for'):
                lane.update(state=line)
        try:
            with machine_lock(perf_wait=opts['perf_wait'], log=waits, max_wait=opts['max_wait']), \
                    budget.hold(gb, f'{spec["name"]}/{kind}-{index} lane {lane.number}', waits, opts['max_wait']):
                lane.update(state=f'running {job_name}')
                point_latest(os.path.join(OUT, spec['name']), run_id)
                result = Run(spec, kind, index, env, lane, log, opts['perf_wait'],
                             reference.values if reference else None, opts['hold'], run_id).execute()
        except WaitTimeout as e:
            return dict(record, lane=lane.number, verdict='fail', reasons=[str(e)], out=None)
        finally:
            lane.update(state='idle', job=None)
        with state:
            results[job['id']] = result
        record.update(result['lane'], out=result['out'], outcome=result['outcome'], saves=result['saves'],
                      options_written=result['options_written'], signature_count=len(result['signatures']),
                      memory_gb=gb)
        reasons = [result['outcome']] if result['outcome'] else []
        if reasons and reference and result['failed_step_kind'] in ('expect_ui', 'expect_state'):
            reasons[0] += ' (it held in the original: the interaction has no effect in ours)'
        checks = {}
        if kind == 'ours':
            record['reference'] = reference.source if reference else None
            record['reference_run'] = reference.run if reference else None
            if reference:
                checks, more = differential(spec, result, reference, findings)
                record['checks'] = checks
                reasons += more
        record['findings'] = checks.get('findings', [])
        record.update(verdict='fail' if reasons else 'warn' if record['findings'] else 'pass', reasons=reasons)
        if opts['bless'] and kind == 'original' and not result['outcome'] and index == 1:
            for save in result['saves']:
                shutil.copy(os.path.join(result['out'], save + '.sav'), os.path.join(FIXTURES, save + '.sav'))
                log(f'     blessed fixture {save}')
            log(f'     blessed references into {bless(spec, result)}')
        if os.path.isdir(result['out']):
            with open(os.path.join(result['out'], 'result.json'), 'w') as f:
                json.dump(record, f, indent=1, default=sorted)
        return record

    def worker(slot):
        log = lambda s: say(f'[slot {slot}] {s}')
        try:
            # A slot still waiting for a lane once the other slots have taken every job gives up,
            # or the invocation would wait for that lane before writing its records.
            lane = allocate_lane(opts['first_lane'], last=opts['last_lane'], log=log, max_wait=opts['max_wait'],
                                 display=not opts['xvfb_run'] and not opts['displays'],
                                 give_up=lambda: not queue.untaken())
        except WaitTimeout as e:
            say(f'FAIL slot {slot}: {e}')
            with state:
                records.append({'spec': '-', 'player': '-', 'index': 0, 'verdict': 'fail', 'reasons': [str(e)], 'out': None})
            return
        if lane is None:
            return
        if opts['displays']:
            lane.display = opts['displays'][slot]
        log = lambda s: say(f'[lane {lane.number}] {s}')
        log(f'lane {lane.number}: OniMcp :{lane.mcp_port}, E2eDriver :{lane.driver_port}, display '
            f'{lane.display or "from xvfb-run -a"}')
        try:
            while True:
                job = queue.next()
                if job is None:
                    return
                try:
                    record = process(job, lane, log)
                except Exception as e:  # a runner bug fails this run, not the queue
                    import traceback
                    traceback.print_exc()
                    record = {'spec': job['spec']['name'], 'player': job['kind'], 'index': job['index'], 'run': run_id,
                              'verdict': 'fail', 'reasons': [f'runner error: {e!r}'], 'out': None}
                with state:
                    records.append(record)
                say(report(record))
                queue.done(job)
        finally:
            lane.release()

    threads = [threading.Thread(target=worker, args=(slot,)) for slot in range(min(opts['lanes'], len(queue.jobs)))]
    for t in threads:
        t.start()
        time.sleep(1)  # lanes are allocated in order
    for t in threads:
        t.join()
    os.makedirs(os.path.dirname(os.path.abspath(results_path)), exist_ok=True)
    with open(results_path, 'w') as f:
        json.dump(records, f, indent=1, default=sorted)
    if not opts['results']:
        point_latest(os.path.join(OUT, 'runs'), run_id + '.json')
    if opts['record']:
        for spec in specs:
            saved = update_record(spec, records, opts['note'])
            say(f'== {spec["name"]}: {saved["verdict"]} ({saved["why"]}), in '
                f'{os.path.relpath(os.path.join(RESULTS, spec["name"] + ".json"), ROOT)}')
    failures = sum(1 for r in records if r['verdict'] == 'fail')
    say(f'== {failures} failure(s); results in {results_path}')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
