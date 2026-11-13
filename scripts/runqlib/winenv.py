"""A job's environment, and its Windows side: run home, stage folder, the Windows backend (§5).

Every job gets RUNQ_JOB, RUNQ_DIR, RUNQ_OUT, RUNQ_OWNER, RUNQ_LEVEL, RUNQ_LANES (and RUNQ_PRIORITY,
the design's name for the level), E2E_MEMORY_GB and E2E_MAX_PLAYERS from its grant,
ONI_CONTAINED=1, ONI_HOST_NO_DIALOG=1 listed in WSLENV, no sound (PULSE_SERVER to nothing,
SDL_AUDIODRIVER=dummy), no desktop display (DISPLAY and WAYLAND_DISPLAY dropped). A job that
holds the Windows slot also gets:

- its own folders: %TEMP%\\oni-runq\\<id>\\stage (ONI_WIN_STAGE: run-windows.sh stages the host
  there) and %TEMP%\\oni-runq\\<id>\\home (ONI_RUN_HOME, unless the caller names its own);
- ONI_WIN_MONITOR=2 (win_monitor), whatever the caller set: test windows never go on monitor 1;
- a run home whose game settings have every volume at 0 (mute_prefs).

The Windows backend lists game processes (read-only), and kills or boosts only the games whose
command line contains a job's run home. It never acts by process name, never focuses a window
and never moves the pointer. FakeWindows stands in for it in tests."""

import json, os, re, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
PS1 = os.path.join(HERE, 'win-games.ps1')
VOLUMES = ('Volume_Master', 'Volume_SFX', 'Volume_Music', 'Volume_Ambience', 'Volume_UI', 'Volume_VO')
DROP = (
    'DISPLAY',
    'WAYLAND_DISPLAY',
    'XAUTHORITY',
    'XDG_SESSION_TYPE',
    'PULSE_SERVER',
    'SSH_AUTH_SOCK',
    'SSH_AGENT_PID',
    'RUNQ_JOB',
    'RUNQ_DIR',
    'RUNQ_OUT',
    'RUNQ_LANES',
    'RUNQ_SOCKET',
)
STEAM_APP = 457140


def allowed(name, allow):
    for pattern in allow:
        if pattern.endswith('*') and name.startswith(pattern[:-1]):
            return True
        if name == pattern:
            return True
    return False


def filter_env(env, allow, extra=()):
    """The caller's variables a job keeps: ALLOW (names, or prefixes ending in *) and EXTRA."""
    return {k: v for k, v in env.items() if (allowed(k, allow) or k in extra) and k not in DROP}


def wslenv_add(current, names):
    items = [x for x in (current or '').split(':') if x]
    have = {x.split('/')[0] for x in items}
    for name in names:
        if name.split('/')[0] not in have:
            items.append(name)
    return ':'.join(items)


def job_paths(win_temp, job_id):
    """(job folder, stage, home) as Windows paths."""
    root = f'{win_temp.rstrip(chr(92))}\\oni-runq\\{job_id}'
    return root, root + '\\stage', root + '\\home'


def job_match(job_id):
    """The command-line text that names this job's run home and nothing else's."""
    return f'\\oni-runq\\{job_id}\\'


def job_env(base, job, grant, cfg, win_temp=None):
    """The job's environment: BASE (the caller's, filtered) plus what runq sets."""
    env = dict(base)
    lanes = grant.get('lanes', [])
    level = job['level']
    env.update(
        RUNQ_JOB=job['id'],
        RUNQ_DIR=job['dir'],
        RUNQ_OUT=os.path.join(job['dir'], 'out'),
        RUNQ_OWNER=job['owner'],
        RUNQ_LEVEL=level,
        RUNQ_PRIORITY=level,
        RUNQ_KIND=job['kind'],
        RUNQ_LANES=','.join(str(x) for x in lanes),
        ONI_CONTAINED='1',
        ONI_HOST_NO_DIALOG='1',
        PULSE_SERVER='unix:/nonexistent/oni-tests-no-audio',
        SDL_AUDIODRIVER='dummy',
        PYTHONUNBUFFERED='1',
    )
    needs = grant.get('needs', job['needs'])
    players = int(needs.get('players', 0) or 0)
    if players:
        env['E2E_MEMORY_GB'] = f'{float(needs.get("mem", 0)):g}'
        env['E2E_MAX_PLAYERS'] = str(players)
    names = ['ONI_HOST_NO_DIALOG', 'RUNQ_JOB']
    if needs.get('windows') and win_temp:
        _, stage, home = job_paths(win_temp, job['id'])
        env['ONI_WIN_STAGE'] = stage
        env.setdefault('ONI_RUN_HOME', home)
        env['ONI_WIN_MONITOR'] = str(cfg['win_monitor'])
        names += ['ONI_RUN_HOME', 'ONI_WIN_STAGE', 'ONI_WIN_MONITOR']
    env['WSLENV'] = wslenv_add(env.get('WSLENV'), names)
    for name in ('DISPLAY', 'WAYLAND_DISPLAY', 'XAUTHORITY'):
        env.pop(name, None)
    return env


def run_home_matches(job_id, env):
    """The command-line texts naming this job's game: its job folder, and the caller's own run
    home when it named one."""
    matches = [job_match(job_id)]
    home = env.get('ONI_RUN_HOME', '')
    if home and job_match(job_id) not in home + '\\' and len(home) > 12:
        matches.append(home)
    return matches


def wsl_path(win_path):
    """C:\\Users\\x as /mnt/c/Users/x."""
    match = re.match(r'^([A-Za-z]):\\(.*)$', win_path)
    if not match:
        raise ValueError(f'not a Windows path: {win_path}')
    return f'/mnt/{match.group(1).lower()}/' + match.group(2).replace('\\', '/')


def mute_prefs(home):
    """Every volume at 0 in the run home HOME's game settings (a WSL path), creating them when
    there are none. The other settings stay as they are."""
    folder = os.path.join(home, 'Documents', 'Klei', 'OxygenNotIncluded')
    path = os.path.join(folder, 'kplayerprefs.yaml')
    os.makedirs(folder, exist_ok=True)
    try:
        with open(path, encoding='utf-8-sig') as f:
            text = f.read()
    except FileNotFoundError:
        text = 'strings: {}\nints: {}\nfloats: {}\n'
    lines = text.replace('\r\n', '\n').split('\n')
    out, i, seen = [], 0, False
    while i < len(lines):
        line = lines[i]
        if re.match(r'^floats:\s*\{\s*\}\s*$', line):
            out.append('floats:')
            out += [f'  {v}: 0' for v in VOLUMES]
            seen = True
        elif re.match(r'^floats:\s*$', line):
            out.append(line)
            i += 1
            while i < len(lines) and lines[i].startswith((' ', '\t')):
                if not re.match(r'^\s+(' + '|'.join(VOLUMES) + r')\s*:', lines[i]):
                    out.append(lines[i])
                i += 1
            out += [f'  {v}: 0' for v in VOLUMES]
            seen = True
            continue
        else:
            out.append(line)
        i += 1
    if not seen:
        while out and out[-1] == '':
            out.pop()
        out.append('floats:')
        out += [f'  {v}: 0' for v in VOLUMES]
    with open(path, 'w') as f:
        f.write('\n'.join(out).rstrip('\n') + '\n')


BACKGROUND = '--' + 'background'  # the flag, spelt so that a grep of the sources finds no use of it


def violations(cmdline, monitor):
    """What a job's game command line breaks of the Windows rules (§5.3)."""
    found = []
    lower = cmdline.lower()
    if re.search(r'oni(moono|brutal)\.exe', lower) and '--no-focus' not in lower:
        found.append('our player without --no-focus')
    m = re.search(r'-monitor\s+"?(\d+)', lower)
    if not m or int(m.group(1)) != int(monitor):
        found.append(f'not on monitor {monitor} (-monitor {m.group(1) if m else "missing"})')
    if BACKGROUND in lower:
        found.append(f'{BACKGROUND} in its command line (it costs about 25% of the frame rate)')
    return found


def rule_breaks(games, monitor):
    """['game pid N: what it breaks'] for a job's own GAMES (attribute's). A helper process of
    our player (its crash watcher) has no window and is left out."""
    out = []
    for g in games:
        cmd = g.get('cmdline', '')
        if '--crash-watch' in cmd:
            continue
        out += [f'game pid {g.get("pid")}: {v}' for v in violations(cmd, monitor)]
    return out


def legacy_test_game(cmdline):
    """A test game started outside runq: any game that is neither a runq job's nor the user's."""
    return '\\oni-runq\\' not in cmdline.lower() and test_game(cmdline)


# WSL interop and Windows' image-load policy. A Windows process that a systemd unit starts goes
# through the interop server of WSL's own init (/run/WSL/1_interop, the fallback without
# WSL_INTEROP), whose host process, wslhost.exe, starts it with the PreferSystem32Images
# mitigation; its children inherit it and no parent can turn it off. A DLL beside an executable
# then loses to System32's of the same name: Unity Doorstop's winhttp.dll in the original's copy
# never loads (no isolation), and our host would pick System32's copies of DLLs it ships. A
# terminal's session goes through its own init's socket (/run/WSL/PID_interop), which starts
# processes without it. So runq jobs use a session's socket, one checked to start processes
# without the mitigation (choose_interop); Windows-slot jobs wait while there is none.


def interop_sockets(root='/run/WSL'):
    """[(path, pid)] of the interop sockets whose relay process is alive, longest-lived first,
    WSL's own (pid 1 and 2) last."""
    found = []
    try:
        names = os.listdir(root)
    except OSError:
        return []
    for name in names:
        m = re.fullmatch(r'(\d+)_interop', name)
        if not m:
            continue
        pid = int(m.group(1))
        if not os.path.exists(f'/proc/{pid}'):
            continue
        try:
            with open(f'/proc/{pid}/stat') as f:
                start = int(f.read().rsplit(')', 1)[1].split()[19])
        except (OSError, IndexError, ValueError):
            start = 0
        found.append((os.path.join(root, name), pid, start))
    found.sort(key=lambda x: (x[1] in (1, 2), x[2]))
    return [(path, pid) for path, pid, _ in found]


def choose_interop(sockets, prefers_system32):
    """The first socket whose Windows processes start without PreferSystem32Images
    (PREFERS_SYSTEM32(path) -> True, False, or None when it cannot tell), else None."""
    for path, _ in sockets:
        if prefers_system32(path) is False:
            return path
    return None


def windows_exe(name, folder='C:\\Windows\\System32'):
    """A Windows executable by its full path: the daemon's systemd unit has no Windows folders in
    its PATH (interop itself works there)."""
    import shutil

    found = shutil.which(name)
    if found:
        return found
    return wsl_path(folder) + '/' + name


def test_game(cmdline):
    """A game a test started: a runq job's, one with a run home of its own, or any game run from
    outside the places the user's games run from (user_game)."""
    return not user_game({'cmdline': cmdline})


def executable(cmdline):
    """The executable of a Windows command line (its first word, quoted or not)."""
    cmdline = cmdline.strip()
    if cmdline.startswith('"'):
        return cmdline[1:].split('"', 1)[0]
    return cmdline.split(' ', 1)[0]


def user_game(g, attributed=()):
    """The user's own game: a game run from a Steam library (steamapps) or from Moono's install
    (%LOCALAPPDATA%\\oni-moono: setup's player, the versioned install), with no test's run home. Tests never run from a Steam library (their
    copies and stages live elsewhere), and Steam's RunningAppID alone says nothing: a test game
    can set it, and it outlives the game."""
    cmd = g.get('cmdline', '')
    exe = executable(cmd).lower()
    lower = cmd.lower()
    if '\\oni-runq\\' in lower or '--run-home' in lower or '\\oni-brutal\\' in lower:
        return False
    return ('\\steamapps\\' in exe or '\\appdata\\local\\oni-moono\\' in exe) and g.get('pid') not in attributed


def user_playing(games, attributed=()):
    """The user is in ONI: a game of theirs runs (user_game)."""
    return any(user_game(g, attributed) for g in games)


STEAM_LOG = '/mnt/c/Program Files (x86)/Steam/logs/gameprocess_log.txt'


def steam_tracked(text, app=STEAM_APP):
    """The pids Steam tracks as APP's processes now, from its gameprocess_log.txt: "AppID APP
    adding PID N as a tracked process", "no longer tracking PID N", "Remove APP from running list"."""
    pids = set()
    for line in text.splitlines():
        m = re.search(rf'AppID {app} adding PID (\d+) as a tracked process', line)
        if m:
            pids.add(int(m.group(1)))
            continue
        m = re.search(rf'AppID {app} no longer tracking PID (\d+)', line)
        if m:
            pids.discard(int(m.group(1)))
        elif f'Remove {app} from running list' in line:
            pids.clear()
    return pids


def read_steam_tracked(path=STEAM_LOG, tail=262144):
    """steam_tracked over the log's last TAIL bytes (None without the log)."""
    try:
        with open(path, 'rb') as f:
            f.seek(0, 2)
            f.seek(max(0, f.tell() - tail))
            return steam_tracked(f.read().decode('utf-8', 'replace'))
    except OSError:
        return None


def background_game(cmdline, paths):
    """A game of another tool of the user's (config background_game_paths): its executable's
    path starts with one of PATHS."""
    exe = executable(cmdline).lower()
    return any(exe.startswith(p.lower()) for p in paths or ())


def attribute(games, matches, background=()):
    """The games that are a job's own: run from, or naming, its stage or run home (MATCHES, from
    run_home_matches: the job's %TEMP%\\oni-runq\\ID folder, and the caller's run home). Never a
    game that only started while the job ran: other tools run games meanwhile (onimaxxing's
    testers), and a job must not be blamed for them, nor kill them. Background games
    (background_game_paths) never count."""
    found = []
    for g in games:
        cmd = g.get('cmdline', '')
        if background_game(cmd, background) or not g.get('created'):
            continue
        if any(m and m in cmd for m in matches):
            found.append({'pid': g['pid'], 'created': g['created'], 'name': g.get('name'), 'cmdline': cmd})
    return found


def boost_targets(jobs, cfg):
    """[(job id, target, priority)] for the running JOBS' games (each job with its env and the
    games tied to it): power throttling off for every Windows job's game (a throttled test game
    runs many times slower), with the High priority class for perf jobs and Normal otherwise.
    A target is ('match', run home text) or ('pid', pid, created)."""
    out = []
    for j in jobs:
        needs = j.get('needs') or {}
        if not needs.get('windows'):
            continue
        perf = cfg['kinds'].get(j.get('kind'), {}).get('boost') or needs.get('perf')
        priority = 'High' if perf else 'Normal'
        for m in run_home_matches(j['id'], j.get('env') or {}):
            out.append((j['id'], ('match', m), priority))
        for g in j.get('games') or []:
            out.append((j['id'], ('pid', g['pid'], g['created']), priority))
    return out


class Windows:
    """The real backend: PowerShell through WSL interop."""

    def __init__(self, ps1=PS1):
        self.ps1 = ps1
        self.powershell = windows_exe('powershell.exe', 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0')
        self.cmd = windows_exe('cmd.exe')
        self.reg = windows_exe('reg.exe')

    def _ps(self, *args, timeout=60):
        win = subprocess.run(['wslpath', '-w', self.ps1], capture_output=True, text=True).stdout.strip()
        r = subprocess.run(
            [self.powershell, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', win, *args],
            capture_output=True,
            text=True,
            timeout=timeout,
            stdin=subprocess.DEVNULL,
            cwd='/mnt/c',
        )
        return r.stdout.replace('\r', '')

    def available(self):
        return os.path.exists(self.powershell) and (os.path.exists('/proc/sys/fs/binfmt_misc/WSLInterop') or os.path.exists('/run/WSL'))

    def temp(self):
        r = subprocess.run([self.cmd, '/c', 'echo %TEMP%'], capture_output=True, text=True, timeout=30, stdin=subprocess.DEVNULL, cwd='/mnt/c')
        return r.stdout.strip().replace('\r', '')

    def games(self):
        out = []
        for line in self._ps('-Action', 'list').splitlines():
            line = line.strip()
            if line.startswith('{'):
                try:
                    out.append(json.loads(line))
                except ValueError:
                    pass
        return out

    def kill(self, match):
        if len(match) < 12:
            raise ValueError(f'refusing a short match {match!r}')
        return self._ps('-Action', 'kill', '-Match', match)

    def boost(self, match, priority='High'):
        """Power throttling off and PRIORITY (Normal or High) for the games MATCH names."""
        if len(match) < 12:
            raise ValueError(f'refusing a short match {match!r}')
        return self._ps('-Action', 'boost', '-Match', match, '-Priority', priority)

    def boost_pid(self, pid, created, priority='High'):
        return self._ps('-Action', 'boost', '-Id', str(int(pid)), '-Created', str(int(created)), '-Priority', priority)

    def kill_pid(self, pid, created):
        """Kills game PID if it still is the process created at CREATED (epoch seconds)."""
        return self._ps('-Action', 'killpid', '-Id', str(int(pid)), '-Created', str(int(created)))

    def prefers_system32(self, interop):
        """Whether a Windows process started through the interop socket INTEROP runs with the
        PreferSystem32Images mitigation (None: no answer)."""
        env = dict(os.environ, WSL_INTEROP=interop)
        try:
            r = subprocess.run(
                [self.powershell, '-NoProfile', '-NonInteractive', '-Command', '(Get-ProcessMitigation -Id $PID).ImageLoad.PreferSystem32'],
                capture_output=True,
                text=True,
                timeout=60,
                stdin=subprocess.DEVNULL,
                cwd='/mnt/c',
                env=env,
            )
        except (OSError, subprocess.TimeoutExpired):
            return None
        answer = r.stdout.strip().upper()
        return {'ON': True, 'OFF': False}.get(answer)

    def user_in_oni(self):
        """Steam's RunningAppID is ONI's (read-only)."""
        r = subprocess.run(
            [self.reg, 'query', r'HKCU\Software\Valve\Steam', '/v', 'RunningAppID'],
            capture_output=True,
            text=True,
            timeout=30,
            stdin=subprocess.DEVNULL,
            cwd='/mnt/c',
        )
        match = re.search(r'RunningAppID\s+REG_DWORD\s+0x([0-9a-fA-F]+)', r.stdout)
        return bool(match) and int(match.group(1), 16) == STEAM_APP

    def remove(self, win_path):
        """Removes a job's Windows folder (rm -rf of its WSL path: links are removed, not followed)."""
        import shutil

        path = wsl_path(win_path)
        if '/oni-runq/j' not in path:
            raise ValueError(f'not a runq job folder: {win_path}')
        shutil.rmtree(path, ignore_errors=True)


class FakeWindows:
    """Records every call; games are what the test puts in .procs ({pid, name, cmdline})."""

    def __init__(self, temp='C:\\Users\\test\\AppData\\Local\\Temp', root=None):
        self.calls, self.procs, self.in_oni, self._temp, self.root = [], [], False, temp, root
        self.policy = {}  # interop socket -> prefers System32 (True/False)

    def available(self):
        return True

    def temp(self):
        return self._temp

    def games(self):
        self.calls.append(('games',))
        return list(self.procs)

    def kill(self, match):
        self.calls.append(('kill', match))
        self.procs = [p for p in self.procs if match not in p['cmdline']]
        return ''

    def boost(self, match, priority='High'):
        self.calls.append(('boost', match, priority))
        return ''

    def boost_pid(self, pid, created, priority='High'):
        self.calls.append(('boostpid', pid, created, priority))
        return ''

    def kill_pid(self, pid, created):
        self.calls.append(('killpid', pid, created))
        self.procs = [p for p in self.procs if not (p['pid'] == pid and p.get('created') == created)]
        return ''

    def user_in_oni(self):
        return self.in_oni

    def prefers_system32(self, interop):
        self.calls.append(('probe', interop))
        return self.policy.get(interop)

    def remove(self, win_path):
        self.calls.append(('remove', win_path))
        if self.root:
            import shutil

            shutil.rmtree(os.path.join(self.root, win_path.rsplit('\\', 1)[-1]), ignore_errors=True)


def backend(name=None):
    name = name or os.environ.get('RUNQ_WINDOWS') or 'real'
    if name == 'fake':
        return FakeWindows()
    if name == 'off':
        return None
    w = Windows()
    return w if w.available() else None


if __name__ == '__main__':
    import sys

    # winenv.py mute HOME: every volume at 0 in a run home's game settings (run-windows.sh and
    # original-windows.sh inside a runq job).
    if sys.argv[1:2] == ['mute'] and len(sys.argv) == 3:
        mute_prefs(sys.argv[2])
    else:
        sys.exit('usage: winenv.py mute RUN_HOME (a WSL path)')
