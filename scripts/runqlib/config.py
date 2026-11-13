"""runq's settings: the defaults below, overridden by ~/.config/oni-runq/config.toml (RUNQ_CONFIG
names another file). Every policy the user may revisit is a value here, not code: the levels and
their wait bounds, the caps, the machine's capacity, the kinds, and the kill switch.

The kill switch: `enabled = false` in the config file, or RUNQ=off in a caller's environment,
sends the migrated scripts (check.sh, e2e.py, qa.sh, alloc-compare.py, run-windows.sh,
original-windows.sh, run.sh) back to their old direct path (the legacy locks)."""

import copy, os

try:
    import tomllib
except ImportError:  # Python before 3.11: the defaults only
    tomllib = None

HOME = os.path.expanduser('~')

DEFAULTS = {
    # The kill switch (see above).
    'enabled': True,
    # Levels in order; the default for a submit without --level; the ones logged when used.
    'levels': ['issue', 'release', 'user', 'study', 'burndown'],
    'default_level': 'study',
    'logged_levels': ['issue', 'release'],
    # A queued job is due after this many minutes; due jobs go ahead of every level (§6.4).
    'due_minutes': {'issue': 0, 'release': 0, 'user': 30, 'study': 180, 'burndown': 480},
    'caps': {
        'running_per_owner': 2,
        'running_per_owner_by_level': {'burndown': 1},
        'queued_per_owner': 20,
        # Queued (and running) hours of the windows or perf slot per owner; more needs --budget-ok.
        'slot_hours_per_owner': 4.0,
    },
    'capacity': {
        'lane_first': 1,
        'lane_last': 19,
        'mem_gb': 40.0,
        'players': 4,
        'cpu': 14.0,
        'windows': 1,
        'perf': 1,
    },
    # Resource bundles. needs: lane (count), mem (GB), cpu (cores), players (count), windows, perf,
    # quiet (flag on perf). timeout in minutes. boost: High priority and no power throttling for
    # the job's Windows game. lane_locks: who takes the legacy lane-L.lock files ('shim', or
    # 'command' for e2e.py, which takes them itself).
    'kinds': {
        'light': {'needs': {}, 'timeout': 30},
        'generic': {'needs': {'cpu': 2, 'mem': 2}, 'timeout': 60},
        'build': {'needs': {'cpu': 6, 'mem': 6}, 'timeout': 30},
        'check': {'needs': {'cpu': 8, 'mem': 8}, 'timeout': 45},
        'linux-player': {'needs': {'lane': 1, 'mem': 6, 'cpu': 3, 'players': 1}, 'timeout': 60},
        'e2e': {'needs': {'lane': 1, 'mem': 6, 'cpu': 3, 'players': 1}, 'timeout': 180, 'lane_locks': 'command'},
        'windows': {'needs': {'windows': 1}, 'timeout': 60},
        'win-perf': {'needs': {'windows': 1, 'perf': 1, 'quiet': True}, 'timeout': 45, 'boost': True},
        'linux-perf': {'needs': {'perf': 1, 'quiet': True, 'mem': 15, 'cpu': 4, 'players': 1}, 'timeout': 60},
        # qa.sh: it starts as qa-unit and moves through its stages with `runq phase --kind qa-STAGE`.
        'qa': {'needs': {'cpu': 8, 'mem': 8}, 'timeout': 180},
        'qa-unit': {'needs': {'cpu': 8, 'mem': 8}, 'timeout': 180},
        'qa-dogfood': {'needs': {'mem': 6, 'cpu': 3, 'players': 1}, 'timeout': 180},
        'qa-verify': {'needs': {'mem': 15, 'cpu': 4, 'players': 1}, 'timeout': 180},
        'qa-perf': {'needs': {'perf': 1, 'quiet': True, 'mem': 15, 'cpu': 4, 'players': 1}, 'timeout': 180},
        'qa-memory': {'needs': {'mem': 15, 'cpu': 3, 'players': 1}, 'timeout': 180},
    },
    'max_timeout_minutes': 360,
    # A job holding windows or perf with no game and its cgroup under idle_cpu_percent of a core:
    # flagged after idle_flag_minutes, stopped after idle_stop_minutes (reason idle-held).
    'idle_cpu_percent': 2.0,
    'idle_flag_minutes': 10,
    'idle_stop_minutes': 20,
    # quiet: external CPU (outside runq) under quiet_cores, waited for at most quiet_wait_minutes;
    # then the job starts flagged noisy. win-perf never starts while the user is in ONI.
    'quiet_cores': 2.0,
    'quiet_wait_minutes': 15,
    # Seconds between two looks at the legacy locks when nothing else happens.
    'scan_seconds': 5,
    # Seconds between the checks of running jobs' units (and idle holders).
    'unit_check_seconds': 15,
    # Seconds systemd waits after SIGTERM before SIGKILL when a job stops.
    'stop_grace_seconds': 30,
    'win_monitor': 2,
    # Games run from these folders (prefixes of the executable's path, any case) belong to other
    # tools of the user's: a background load. They block only jobs that measure (win-perf, quiet),
    # are never the user's game, and runq never kills them (onimaxxing's testers and copies).
    'background_game_paths': [
        'E:\\Development\\TestingNotIncluded',
        'E:\\Development\\SimNotIncluded',
        'E:\\Development\\DataNotIncluded',
        'E:\\Development\\u59bake\\',
        'E:\\Development\\help-i-want-to-customize-my-oxygen-not-included-worldgen\\',
        'E:\\tmp\\oni-fps\\',
    ],
    'min_free_gb': 5,
    'retention_days': 14,
    'retention_failed_days': 30,
    'retention_total_gb': 50,
    # Phase 2 (not served yet): the status page, localhost only.
    'status_port': 8777,
    # The old locks: runq jobs take them, and their holders outside runq count as occupants.
    'legacy_bridge': True,
    # Processes outside runq that count as a perf job (e2e.py's PERF_JOBS, matched on ps args).
    'legacy_perf_patterns': [
        r'(^|/)scripts/flamegraph\.py\b',
        r'out/local-tools/(sweep|measure|ft-bench|compare-queued|minnow-prof|glut-prof|queue|orig-queue|tw-run)',
        r'^perf (record|stat)\b',
    ],
    # A qa.sh outside runq counts as a player of this many GB (e2e.py's QA_GB).
    'legacy_qa_gb': 15,
    'legacy_player_cpu': 3,
    # The environment a job keeps from its caller (names, or prefixes ending in *).
    'env_allow': [
        'PATH',
        'HOME',
        'USER',
        'LOGNAME',
        'SHELL',
        'TERM',
        'TZ',
        'LANG',
        'LANGUAGE',
        'LC_*',
        'TMPDIR',
        'DOTNET_*',
        'NUGET_*',
        'E2E_*',
        'QA_*',
        'ONI_*',
        'ALLOC_*',
        'ORIG_*',
        'GALLIUM_DRIVER',
        'MESA_*',
        'LIBGL_*',
        'VK_*',
        'XDG_RUNTIME_DIR',
        'DBUS_SESSION_BUS_ADDRESS',
        'WSLENV',
        'WSL_DISTRO_NAME',
        'GH_*',
        'GITHUB_*',
        'RUNQ_LEVEL',
        'RUNQ_OWNER',
        'PYTHONUNBUFFERED',
    ],
}


def merge(base, extra):
    out = copy.deepcopy(base)
    for key, value in extra.items():
        if isinstance(value, dict) and isinstance(out.get(key), dict):
            out[key] = merge(out[key], value)
        else:
            out[key] = value
    return out


def config_path():
    return os.environ.get('RUNQ_CONFIG') or os.path.join(HOME, '.config', 'oni-runq', 'config.toml')


def load(path=None):
    """The defaults merged with the config file's values (a missing file: the defaults)."""
    path = path or config_path()
    data = {}
    if tomllib and os.path.exists(path):
        with open(path, 'rb') as f:
            data = tomllib.load(f)
    return merge(DEFAULTS, data)


def state_dir():
    return os.environ.get('RUNQ_STATE_DIR') or os.path.join(HOME, '.local', 'state', 'oni-runq')


def runs_dir():
    return os.environ.get('RUNQ_RUNS_DIR') or os.path.join(HOME, 'oni-runs')


def socket_path():
    """RUNQ_SOCKET names another (a Unix socket's path is at most 107 bytes long)."""
    return os.environ.get('RUNQ_SOCKET') or os.path.join(state_dir(), 'runq.sock')


def legacy_dir():
    """The old scripts' lock directory (scripts/e2e.py's shared_dir)."""
    return os.environ.get('E2E_LOCK_DIR') or os.path.join(HOME, '.cache', 'oni-brutal-e2e')


def unit_prefix():
    return os.environ.get('RUNQ_UNIT_PREFIX') or 'runq'


def slice_name():
    return os.environ.get('RUNQ_SLICE') or 'oni-tests.slice'


def disabled(environ=None, cfg=None):
    """Why the kill switch is on (RUNQ=off, enabled = false, or a GitHub Actions runner), else
    None. runq brokers the shared test machine; a CI runner is a machine of one job's own, and a
    job runq ran there would lose the display its caller made (a job never keeps DISPLAY)."""
    environ = os.environ if environ is None else environ
    if environ.get('RUNQ', '').lower() in ('off', '0', 'no', 'false'):
        return 'RUNQ=off'
    if environ.get('GITHUB_ACTIONS') == 'true':
        return 'a GitHub Actions runner'
    try:
        cfg = cfg or load()
    except Exception as e:  # a broken config file turns runq off rather than every script
        return f'config unreadable ({e})'
    if not cfg.get('enabled', True):
        return f'enabled = false in {config_path()}'
    return None


def kind_needs(cfg, kind):
    k = cfg['kinds'].get(kind)
    if k is None:
        raise KeyError(kind)
    return dict(k.get('needs', {}))


def parse_needs(text):
    """'lane=2,mem=12,perf' as {'lane': 2, 'mem': 12.0, 'perf': 1}."""
    needs = {}
    for part in (text or '').split(','):
        part = part.strip()
        if not part:
            continue
        name, _, value = part.partition('=')
        name = name.strip()
        if name not in ('lane', 'mem', 'cpu', 'players', 'windows', 'perf', 'quiet'):
            raise ValueError(f'unknown resource {name!r} (lane, mem, cpu, players, windows, perf, quiet)')
        if name == 'quiet':
            needs[name] = value.strip().lower() not in ('0', 'no', 'false') if value else True
        elif not value:
            needs[name] = 1
        else:
            needs[name] = float(value) if name in ('mem', 'cpu') else int(value)
    return needs


def parse_duration(text):
    """'90', '90s', '45m', '2h', '1h30m' as seconds (a bare number is minutes)."""
    import re

    text = str(text).strip()
    if re.fullmatch(r'\d+(\.\d+)?', text):
        return float(text) * 60
    total, matched = 0.0, False
    for number, unit in re.findall(r'(\d+(?:\.\d+)?)\s*([hms])', text):
        total += float(number) * {'h': 3600, 'm': 60, 's': 1}[unit]
        matched = True
    if not matched or re.sub(r'(\d+(?:\.\d+)?)\s*[hms]', '', text).strip():
        raise ValueError(f'bad duration {text!r} (90s, 45m, 2h, 1h30m)')
    return total
