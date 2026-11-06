#!/usr/bin/env python3
"""scripts/e2e-windows/run.py [e2e.py options] SPEC...: scripts/e2e.py with both players on
Windows, from WSL. Not part of the product.

Ours: out/win (scripts/build-windows.sh) staged in %TEMP%\\oni-brutal\\qa-win\\host, on the U60
Windows data (WIN_GAME, default E:\\oni-builds\\oni-win-762230), --renderer from WIN_RENDERER
(default vulkan; empty for none). The original: a renamed copy of that install (WIN_ORIG, default
E:\\oni-builds\\BrutalNotIncluded-762230), set up by scripts/original-windows.sh on its first run
(WIN_BEPINEX_SOURCE: a game folder with BepInEx 6), on Unity's default renderer (Direct3D 11);
WIN_ORIG_PRODUCT is the product name in its globalgamemanagers. Both players run windowed
1280x720 on the second monitor, never focused, and muted (volume prefs 0; ours --audio-wav, the
original's isolation writes a wav); guard.ps1 kills a window that stays on the first monitor
for over half a second. One player at a time, on lane WIN_LANE (default 25).

E2eDriver and OniMcp listen on the Windows loopback, which WSL cannot reach: each connection goes
through a Windows Python bridge (bridge.py) on a relay port here (the lane's + 1000). Input is
E2eDriver's synthetic input (input.*, tools/E2eDriver/README.md), never the desktop's pointer or
keyboard; so these runs check what the game and the UI do with input, not how either player
reads Windows input. The exception is the side buttons (X11's 8 and 9 in specs): xbutton.ps1
posts WM_XBUTTONDOWN/UP for XBUTTON1, XBUTTON2 to the player's own window, which both players
read. `type` sets the field's text whole; steps that need real input (keyboard
layouts, physical keys) fail with "needs real input". Front-end shots come from E2eDriver's
screen.capture.
"""
import contextlib, json, os, re, shutil, socket, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # scripts/e2e-windows -> the checkout
sys.path.insert(0, os.path.join(ROOT, 'scripts'))
import importlib.util
spec_ = importlib.util.spec_from_file_location('e2e', os.path.join(ROOT, 'scripts', 'e2e.py'))
e2e = importlib.util.module_from_spec(spec_)
sys.modules['e2e'] = e2e
spec_.loader.exec_module(e2e)



def windows_env(name):
    return subprocess.run(['cmd.exe', '/c', f'echo %{name}%'], capture_output=True, text=True,
                          cwd='/mnt/c').stdout.strip()


def windows_python():
    found = subprocess.run(['powershell.exe', '-NoProfile', '-Command', '(Get-Command python).Source'],
                           capture_output=True, text=True).stdout.strip()
    return subprocess.run(['wslpath', '-u', found], capture_output=True, text=True).stdout.strip() if found else None


# WIN_PYTHON: the Windows Python that runs bridge.py (default: python on the Windows PATH).
WIN_PY = os.environ.get('WIN_PYTHON') or windows_python()
WSL_TEMP = subprocess.run(['wslpath', '-u', windows_env('TEMP')], capture_output=True, text=True).stdout.strip()
LOCAL_LOW = subprocess.run(['wslpath', '-u', windows_env('USERPROFILE') + '\\AppData\\LocalLow'],
                           capture_output=True, text=True).stdout.strip()
# The original's product name (globalgamemanagers): its LocalLow folder holds OniMcp's options.
ORIG_PRODUCT = os.environ.get('WIN_ORIG_PRODUCT', 'Brutal Not Included')
STAGE = 'oni-brutal\\qa-win'
WIN_GAME = os.environ.get('WIN_GAME', 'E:\\oni-builds\\oni-win-762230')
WIN_ORIG = os.environ.get('WIN_ORIG', 'E:\\oni-builds\\BrutalNotIncluded-762230')
RENDERER = os.environ.get('WIN_RENDERER', 'vulkan')
# A DLC-owning Steam double for Windows (as QA_U57_STEAM is for Linux).
STUB = os.environ.get('WIN_STEAM_STUB') or os.path.join(ROOT, 'out', 'local-tools', 'steamstub-dlc-win', 'steam_api64.dll')
RELAY = 1000
LANE = int(os.environ.get('WIN_LANE', '25'))
log = e2e.say


def wpath(p):
    return subprocess.run(['wslpath', '-w', p], capture_output=True, text=True, check=True).stdout.strip()


def upath(p):
    return subprocess.run(['wslpath', '-u', p], capture_output=True, text=True, check=True).stdout.strip()


def stage_dir():
    return os.path.join(WSL_TEMP, *STAGE.split('\\'))


def ps(script, *args, **kw):
    return subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                           wpath(os.path.join(HERE, script)), *args], capture_output=True, text=True, **kw)


# ------------------------------------------------------------ the loopback relay

def win_port_open(port):
    return subprocess.run([WIN_PY, wpath(os.path.join(HERE, 'bridge.py')), str(port), '--probe']).returncode == 0


class Relay(threading.Thread):
    """Listens on 127.0.0.1:PORT+RELAY here; each connection is joined to Windows' PORT."""

    def __init__(self, port):
        super().__init__(daemon=True)
        self.port = port
        self.server = socket.socket()
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind(('127.0.0.1', port + RELAY))
        self.server.listen(16)

    def run(self):
        while True:
            conn, _ = self.server.accept()
            threading.Thread(target=self.serve, args=(conn,), daemon=True).start()

    def serve(self, conn):
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        bridge = subprocess.Popen([WIN_PY, '-u', wpath(os.path.join(HERE, 'bridge.py')), str(self.port)],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, bufsize=0)

        def up():
            try:
                while True:
                    data = conn.recv(65536)
                    if not data:
                        break
                    bridge.stdin.write(data)
                    bridge.stdin.flush()
            except OSError:
                pass
            with contextlib.suppress(OSError):
                bridge.stdin.close()
        threading.Thread(target=up, daemon=True).start()
        try:
            while True:
                data = bridge.stdout.read1(65536) if hasattr(bridge.stdout, 'read1') else os.read(bridge.stdout.fileno(), 65536)
                if not data:
                    break
                conn.sendall(data)
        except OSError:
            pass
        with contextlib.suppress(OSError):
            conn.close()
        with contextlib.suppress(Exception):
            bridge.kill()


_relays = {}


def relay(port):
    if port not in _relays:
        _relays[port] = Relay(port)
        _relays[port].start()
    return port + RELAY


_mcp_init = e2e.Mcp.__init__


def mcp_init(self, port):
    _mcp_init(self, port)
    self.url = f'http://127.0.0.1:{relay(port)}/mcp/'


e2e.Mcp.__init__ = mcp_init
_driver_init = e2e.Driver.__init__


def driver_init(self, port, host='127.0.0.1', timeout=60):
    _driver_init(self, relay(port), host, timeout)


e2e.Driver.__init__ = driver_init
_driver_connect = e2e.Driver.connect


def driver_connect(self, deadline=60, alive=lambda: True):
    # The relay always accepts: wait until Windows' port answers first.
    end = time.time() + deadline
    while not win_port_open(self.address[1] - RELAY):
        if time.time() > end or not alive():
            raise e2e.SpecFailure(f'E2eDriver did not answer on Windows port {self.address[1] - RELAY}')
        time.sleep(1)
    return _driver_connect(self, deadline, alive)


e2e.Driver.connect = driver_connect


def windows_path(value):
    if isinstance(value, str) and re.match(r'^[A-Za-z]:\\', value):
        return upath(value)
    return value


_screenshot = e2e.screenshot


def screenshot(mcp, out, name):
    """OniMcp's screenshot, its Windows path read from here."""
    asked = time.time() - 1
    result = mcp.call('navigation_control', action='screenshot', filename=f'e2e-{mcp.port}-{name}', waitFrames=5)
    path = (result.get('path') or result.get('file')) if isinstance(result, dict) else None
    if not path:
        return None
    path = windows_path(path)
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


e2e.screenshot = screenshot


# ------------------------------------------------------------ synthetic input

KEY_NAMES = {'enter': 'Return', 'esc': 'Escape', 'del': 'Delete', 'space': 'Space', 'backspace': 'Backspace',
             'tab': 'Tab'}
MOD_NAMES = {'shift': 'Shift', 'ctrl': 'Ctrl', 'control': 'Ctrl', 'alt': 'Alt'}


def win_key(code):
    parts = [p for p in re.split(r'\s*\+\s*', str(code).strip()) if p]
    mods = [MOD_NAMES[p.lower()] for p in parts[:-1]]
    key = parts[-1]
    if re.fullmatch(r'\d', key):
        key = 'Alpha' + key
    key = KEY_NAMES.get(key.lower(), key)
    return mods, key


class NeedsRealInput(e2e.SpecFailure):
    pass


# X11's side buttons 8 and 9 (the Linux specs' numbering) are Windows' XBUTTON1 and XBUTTON2.
X_BUTTONS = {8: 1, 9: 2}


class WinInput:
    """E2eDriver's synthetic input (input.*) in the place of XTEST. The side buttons (8, 9) go
    as real WM_XBUTTON messages posted to the player's own window (xbutton.ps1): the platform
    reads them, and the desktop's pointer and focus are not touched."""

    def __init__(self, driver, home=None):
        self.driver = driver
        self.home = home
        self.scale = (1.0, 1.0)
        self.held = []
        self.mods = []
        self.focused = False

    def frames(self, n):
        self.driver.frames(n)

    def focus(self):
        pass

    def close(self):
        for code in list(self.held):
            with contextlib.suppress(Exception):
                self.key_event(code, False)

    def grab(self, path):
        raise NeedsRealInput('grab')

    def move(self, x, y):
        self.driver.request('input.move', x=float(x), y=float(y))

    def button(self, button, down):
        if button in X_BUTTONS:
            return self.post_x(button, 'down' if down else 'up')
        self.driver.request('input.button', button=button, down=bool(down))

    def post_x(self, button, action, x=0, y=0):
        if not self.home:
            raise NeedsRealInput(f'button {button} needs the player\'s run home')
        if not self.focused:
            # The game drops input while unfocused (KInputManager.isFocused): E2eDriver tells
            # it it has the focus, as for its synthetic input.
            self.driver.request('input.focus')
            self.focused = True
        r = ps('xbutton.ps1', '-Match', self.home, '-Button', str(X_BUTTONS[button]), '-X', str(int(x)),
               '-Y', str(int(y)), '-Action', action)
        for line in (r.stdout + r.stderr).strip().splitlines():
            log(f'  (xbutton) {line}')
        if r.returncode:
            raise e2e.SpecFailure(f'xbutton.ps1 failed ({r.returncode})')

    def key_event(self, name, down):
        mods, key = win_key(name) if not isinstance(name, tuple) else name
        r = self.driver.request('input.key', code=key, mods=mods or self.mods, down=bool(down))
        if down:
            self.held.append(name)
        elif name in self.held:
            self.held.remove(name)
        return r

    @contextlib.contextmanager
    def modifiers(self, names):
        old = self.mods
        self.mods = [MOD_NAMES.get(str(n).lower(), n) for n in names or []]
        try:
            yield
        finally:
            self.mods = old

    def click(self, x, y, button='left', mods=(), double=False):
        if button in X_BUTTONS and not mods and not double:
            self.move(x, y)
            self.frames(2)
            self.post_x(button, 'press', x, y)
            self.frames(4)
            return
        if mods:
            log(f'  (synthetic input: {"+".join(mods)} held for KInput only; the UI reads no modifier)')
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
        mods, key = win_key(code)
        if key == 'Return' and getattr(self, 'typed', False):
            # Enter after text: the field's submit, as the field hears Enter; KInput hears it too.
            self.typed = False
            self.driver.request('input.submit')
        r = self.driver.request('input.key', code=key, mods=mods, down=True)
        if not (r or {}).get('queued'):
            log(f'  (synthetic input: no binding for {code}; nothing queued)')
        self.frames(2)
        self.driver.request('input.key', code=key, mods=mods, down=False)
        self.frames(2)

    def physical_key(self, keycode, mods=()):
        raise NeedsRealInput(f'a physical key ({keycode}) needs real input')

    def type_text(self, text):
        # Typing needs real keys (TMP reads IMGUI events): the field's text is set whole.
        log(f'  (synthetic input: the field text set to {text!r}, not typed)')
        self.driver.request('input.text', text=text)
        self.typed = True
        self.frames(2)

    def drag(self, start, end, button='left', mods=(), steps=8, hold=0, via=()):
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
        self.move(x, y)
        self.frames(2)
        step = 1 if notches > 0 else -1
        for _ in range(abs(notches)):
            self.driver.request('input.scroll', notches=step)
            self.frames(1)


def need_input(self):
    if not self.input:
        self.input = WinInput(self.need_driver(), getattr(self.player, 'whome', None))
    return self.input


e2e.Run.need_input = need_input


def pointer_away(self):
    if self.driver:
        self.need_input().move(e2e.WIDTH - 1, e2e.HEIGHT - 1)
        self.frames(2)


e2e.Run.pointer_away = pointer_away


def menu_grab(self, name):
    self.frames(5)
    result = self.need_driver().request('screen.capture', name=name)
    source = windows_path(result['path'])
    dest = os.path.join(self.out, name + '.png')
    shutil.copy(source, dest)
    return dest


e2e.Run.menu_grab = menu_grab

# The Windows U60 original is a development build: Unity draws "Development Build" in the
# bottom right corner of every frame, which ours does not. Every shot masks it.
WATERMARK = [e2e.WIDTH - 130, e2e.HEIGHT - 28, 130, 28]
_shot, _world_shot = e2e.Run.shot, e2e.Run.world_shot


def with_watermark(value):
    value = dict(value) if isinstance(value, dict) else {'name': value}
    value['mask'] = list(value.get('mask', [])) + [WATERMARK]
    return value


e2e.Run.shot = lambda self, value: _shot(self, with_watermark(value))
e2e.Run.world_shot = lambda self, value: _world_shot(self, with_watermark(value))
_step = e2e.Run.step


def step(self, kind, value, number):
    if kind == 'keyboard':
        raise NeedsRealInput('a keyboard layout switch needs the desktop')
    if kind == 'save':
        # e2e.py's save step, with the Windows path OniMcp answers read from here.
        saved = self.mcp.call('game_control', domain='save', action='save', name=value, overwrite=True,
                              updateActiveSave=False, confirm=True)
        path = windows_path(saved.get('saved')) if isinstance(saved, dict) else None
        if not path or not os.path.exists(path):
            raise e2e.SpecFailure(f'save {value} failed: {json.dumps(saved)[:600]}')
        shutil.copy(path, os.path.join(self.out, value + '.sav'))
        self.saves.append(value)
        return None
    if kind == 'click' and isinstance(value, dict) and value.get('quits'):
        # A quitting game keeps its socket open while it shuts down: the click's last frames
        # time out here, where on Linux the connection closes.
        try:
            return _step(self, kind, value, number)
        except (e2e.SpecFailure, OSError) as e:
            if 'timed out' not in str(e):
                raise
            return None
    return _step(self, kind, value, number)


e2e.Run.step = step


# ------------------------------------------------------------ the players

e2e.PROFILE = ('Documents', 'Klei', 'OxygenNotIncluded')


class WinPlayer(e2e.Player):
    def __init__(self, kind, out, env, fixture, lane, *args, **kw):
        super().__init__(kind, out, env, fixture, lane, *args, **kw)
        # The home moves to the Windows side, under its own run-home name.
        self.whome_u = os.path.join(stage_dir(), f'home-{kind}')
        shutil.rmtree(self.whome_u, ignore_errors=True)
        shutil.copytree(self.home, self.whome_u, symlinks=False)
        shutil.rmtree(self.home)
        os.symlink(self.whome_u, self.home)  # the evidence folder still finds it
        self.whome = wpath(self.whome_u)
        prefs = os.path.join(self.profile, 'kplayerprefs.yaml')
        text = open(prefs).read().replace('floats: {}', 'floats:\n' + ''.join(
            f'  Volume_{n}: 0\n' for n in ('Master', 'SFX', 'Music', 'Ambience', 'UI', 'VO', 'Bus')))
        assert 'Volume_Master: 0' in text
        open(prefs, 'w').write(text)
        # OniMcp reads its options from Application.persistentDataPath first: the original's is
        # its copy's own LocalLow folder (WIN_ORIG_PRODUCT), ours' the run home's.
        config = open(os.path.join(self.profile, 'OniMcpConfig.json')).read()
        local_low = (os.path.join(LOCAL_LOW, 'Klei', ORIG_PRODUCT) if kind == 'original'
                     else os.path.join(self.whome_u, 'AppData', 'LocalLow', 'Klei', 'Oxygen Not Included'))
        os.makedirs(local_low, exist_ok=True)
        with open(os.path.join(local_low, 'OniMcpConfig.json'), 'w') as f:
            f.write(config)
        with open(os.path.join(self.profile, 'mods', 'Local', 'OniMcp', 'OniMcpConfig.json'), 'w') as f:
            f.write(config)
        self.log = os.path.normpath(os.path.join(self.whome_u, 'player.log' if kind == 'ours' else 'Player.log'))
        self.guard = None

    def start(self):
        env = dict(os.environ, E2E_DRIVER_PORT=str(self.lane.driver_port))
        env.update(self.workshop_env)
        env['WSLENV'] = ':'.join(filter(None, [os.environ.get('WSLENV'), 'E2E_DRIVER_PORT',
                                               *self.workshop_env.keys()]))
        size = ['-screen-width', str(e2e.WIDTH), '-screen-height', str(e2e.HEIGHT), '-screen-fullscreen', '0']
        stdout = open(os.path.join(self.out, 'stdout.txt'), 'w')
        if self.kind == 'original':
            env.update(ORIG_DIR=WIN_ORIG, ONI_STEAM_STUB=STUB, ONI_RUN_HOME=self.whome, ORIG_SECONDS='3600',
                       ONI_WIN_GAME_DIR=WIN_GAME, ONI_BEPINEX_SOURCE=os.environ.get('WIN_BEPINEX_SOURCE', ''))
            command = [os.path.join(e2e.ROOT, 'scripts', 'original-windows.sh'), *size,
                       *os.environ.get('E2E_ORIGINAL_ARGS', '').split(), *self.game_args]
        else:
            host = os.path.join(stage_dir(), 'host')
            shutil.rmtree(host, ignore_errors=True)
            shutil.copytree(os.path.join(e2e.ROOT, 'out', 'win', 'host'), host)
            stub = os.path.join(stage_dir(), 'steam_api64.dll')
            shutil.copy(STUB, stub)
            command = [os.path.join(host, 'OniMoono.exe'), '--game', WIN_GAME, '--run-home', self.whome,
                       '--audio-wav', self.whome, '--game-tweak', 'none', '--splash', 'none', '--log', wpath(self.log), '--log-paths', 'full',
                       '--native-override', 'steam_api64=' + wpath(stub), *size, '-monitor', '2', '--no-focus',
                       *(['--renderer', RENDERER] if RENDERER else []),
                       *(a for t in os.environ.get('E2E_GAME_TWEAKS', 'exact').split() for a in ('--game-tweak', t)),
                       *([] if self.tweaks_menu else ['--no-tweaks-menu']),
                       *os.environ.get('E2E_PLAYER_ARGS', '').split(),
                       # `game_args`: in the Steam form, after the game's executable.
                       *(['--', WIN_GAME + '\\OxygenNotIncluded.exe', *self.game_args] if self.game_args else [])]
        with open(os.path.join(self.out, 'command.txt'), 'w') as f:
            f.write(' '.join(command) + '\n')
        self.process = subprocess.Popen(command, env=env, stdout=stdout, stderr=subprocess.STDOUT, start_new_session=True)
        self.guard = subprocess.Popen(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                       wpath(os.path.join(HERE, 'guard.ps1')), '-Match', self.whome,
                                       '-Log', wpath(os.path.join(self.whome_u, 'guard.txt'))],
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        with open(os.path.join(self.out, 'lane.json'), 'w') as f:
            json.dump(self.lane_record(), f)
        self.watchdog.start()
        deadline = time.time() + 900
        while not win_port_open(self.lane.mcp_port):
            if self.process.poll() is not None:
                raise e2e.SpecFailure(f'the player exited ({self.process.returncode}) before OniMcp started')
            if time.time() > deadline:
                raise e2e.SpecFailure('OniMcp did not start within 15 minutes')
            time.sleep(3)

    def game_path(self, path):
        return wpath(path.replace(self.home, self.whome_u))

    def game_pid(self):
        return None

    def kill(self):
        ps('winproc.ps1', '-Match', self.whome, '-Action', 'kill')
        with contextlib.suppress(OSError):
            os.killpg(self.process.pid, 9)

    def stop(self):
        self.watchdog.stop()
        out = ps('winproc.ps1', '-Match', self.whome, '-Action', 'kill').stdout.strip()
        if out:
            log(f'  {out}')
        if self.process:
            try:
                self.process.wait(60)
            except subprocess.TimeoutExpired:
                with contextlib.suppress(OSError):
                    os.killpg(self.process.pid, 9)
        if self.guard:
            with contextlib.suppress(Exception):
                self.guard.wait(15)
        deadline = time.time() + 30
        while (win_port_open(self.lane.mcp_port) or win_port_open(self.lane.driver_port)) and time.time() < deadline:
            time.sleep(1)
        # The evidence keeps the logs; the home's other files stay on the Windows side.
        for name in ('player.log', 'Player.log', 'guard.txt'):
            p = os.path.join(self.whome_u, name)
            if os.path.exists(p):
                shutil.copy(p, os.path.join(self.out, 'win-' + name))


e2e.Player = WinPlayer


# ------------------------------------------------------------ the lane

def allocate_lane(first, **kw):
    lane = e2e.Lane(LANE, None)
    return lane


e2e.allocate_lane = allocate_lane


@contextlib.contextmanager
def no_budget(self, *a, **k):
    yield


e2e.Budget.hold = no_budget

if __name__ == '__main__':
    os.makedirs(stage_dir(), exist_ok=True)
    sys.exit(e2e.main(['--no-perf-wait', *sys.argv[1:]]))
