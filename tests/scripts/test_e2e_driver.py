"""e2e.py's E2eDriver client against a small fake server speaking its protocol (JSON lines over TCP,
replies and element descriptions as tools/E2eDriver/README.md gives them), and the UI steps built
on it, with a fake XTEST input standing in for the X server."""
import json, os, socket, socketserver, tempfile, threading, unittest

import helpers  # noqa: F401
import e2e


class FakeGame:
    """The game side: UI elements by path, state values, a camera, canvases; what a click at a
    point does is up to the test."""

    def __init__(self):
        self.elements = {}
        self.state = {}
        self.lists = {}
        self.requests = []
        self.camera = {'pos': [0, 0], 'ortho': 20}
        self.canvases_hidden = False
        self.build_menu = {'settled': True, 'open': False, 'reasons': []}  # buildmenu.settled's check
        self.lock = threading.Lock()

    def add(self, path, rect, hit=None, as_name=None, **fields):
        """An element as ui.find describes it; RECT is [x, y, w, h], HIT the path on top at its
        centre (default the element itself)."""
        x, y, w, h = rect
        fields.setdefault('key', '/' + path)
        self.elements[as_name or path] = dict(path=path, name=path.rsplit('/', 1)[-1], active=True,
                                   rect={'x': x, 'y': y, 'w': w, 'h': h}, center={'x': x + w / 2, 'y': y + h / 2},
                                   onscreen=True, hit=hit or path, **fields)

    @staticmethod
    def relation(element, hit):
        if hit is None:
            return 'none'
        if hit == element:
            return 'self'
        if hit.startswith(element + '/'):
            return 'child'
        return 'ancestor' if element.startswith(hit + '/') else 'other'

    def describe(self, e, hits=0):
        e = dict(e)
        hit = e.pop('hit')
        e['hit'] = {'path': hit, 'relation': self.relation(e['path'], hit), 'raycaster': 'GraphicRaycaster',
                    'sortingLayer': 0, 'sortingOrder': 0, 'depth': 3, 'distance': 0.0, 'count': 1}
        if hits:
            e['hit']['all'] = [{'path': hit, 'relation': e['hit']['relation']}]
        return e

    @staticmethod
    def glob(pattern, path):
        """A path pattern: `**/` any number of levels (none too), `*` within one name."""
        import re
        regex = re.escape(pattern).replace(r'\*\*/', '(?:.*/)?').replace(r'\*', '[^/]*')
        return re.fullmatch(regex, path) is not None

    def matches(self, args):
        found = []
        for e in self.elements.values():
            path = e['path']
            if 'path' in args and not self.glob(args['path'], path):
                continue
            if 'text' in args and e.get('text') != args['text']:
                continue
            if 'under' in args and not self.glob(args['under'] + '/**/*', path):
                continue
            found.append(e)
        return found

    def handle(self, cmd, args):
        with self.lock:
            self.requests.append((cmd, args))
        if cmd == 'ping':
            return {'driver': '1', 'player': 'ours', 'scene': 'backend', 'screen': {'w': 1280, 'h': 720},
                    'paused': True, 'world': True}
        if cmd == 'ui.find':
            found = self.matches(args)
            limit = args.get('limit', 50)
            return {'count': len(found), 'elements': [self.describe(e, args.get('hits', 0)) for e in found[:limit]]}
        if cmd == 'ui.at':
            on_top = [e for e in self.elements.values()
                      if e['rect']['x'] <= args['x'] < e['rect']['x'] + e['rect']['w']
                      and e['rect']['y'] <= args['y'] < e['rect']['y'] + e['rect']['h']]
            if not on_top:
                return {'path': None, 'relation': 'none', 'count': 0}
            hit = on_top[-1]['hit']
            return {'path': hit, 'relation': None, 'count': 1, 'all': [{'path': hit, 'relation': None}]}
        if cmd == 'ui.dump' and 'settle' in args:
            check = self.handle('buildmenu.settled', {'frames': args['settle']})
            return dict(self.handle('ui.dump', {k: v for k, v in args.items() if k != 'settle'}), settle=check)
        if cmd == 'ui.dump':
            # Every element's ancestors are GameObjects too.
            objects = {'/'.join(p.split('/')[:i + 1]) for p in self.elements for i in range(p.count('/') + 1)}
            roots = [p for p in sorted(objects) if self.glob(args['root'], p)]
            if not roots:
                raise Failure(f'no element matches {args["root"]}')
            lines = [f'/{p}\trect {e["rect"]["x"]},{e["rect"]["y"]},{e["rect"]["w"]},{e["rect"]["h"]}'
                     for p, e in sorted(self.elements.items()) if any(p == r or p.startswith(r + '/') for r in roots)]
            return {'roots': roots, 'count': len(lines), 'lines': lines}
        if cmd == 'ui.wait':
            found = self.matches({'path': args['path']})
            state = {'frames': 1 if found else args.get('frames', 300), 'count': len(found),
                     'element': self.describe(found[0]) if found else None}
            if not found:
                raise Failure(f'timeout: {args.get("cond", "exists")} not met within {args.get("frames", 300)} frames', state)
            return state
        if cmd == 'world.to_screen':
            x, y = args['cell']
            return {'x': x * 10, 'y': 720 - y * 3, 'onscreen': 0 <= x * 10 < 1280,
                    'world': {'x': x + 0.5, 'y': y + 0.5, 'z': -25.5}}
        if cmd == 'screen.to_cell':
            x, y = args['x'] // 10, (720 - args['y']) // 3
            return {'cell': y * 256 + x, 'valid': True, 'world': {'x': x + 0.5, 'y': y + 0.5}, 'xy': [x, y], 'worldId': 0}
        if cmd == 'state.get':
            if args['expr'] not in self.state:
                raise Failure(f'no field or property {args["expr"]} on Research')
            value = self.state[args['expr']]
            return {'value': value, 'type': 'System.Int32' if isinstance(value, int) else 'System.Object'}
        if cmd == 'state.each':
            # self.lists: an expression's items, each {member: value}.
            if args['expr'] not in self.lists:
                raise Failure(f'no type at the start of {args["expr"]}')
            items = [[item[m] for m in args['members']] for item in self.lists[args['expr']]]
            return {'count': len(items), 'items': items}
        if cmd == 'camera.get':
            return dict(self.camera)
        if cmd == 'camera.set':
            self.camera.update(args)
            return dict(self.camera)
        if cmd == 'canvas.hide':
            self.canvases_hidden = True
            return {'hidden': 2, 'canvases': ['ScreenSpaceCanvas', 'ScreenSpaceOverlayCanvas'], 'total': 2}
        if cmd == 'canvas.show':
            self.canvases_hidden = False
            return {'shown': 2}
        if cmd == 'buildmenu.settled':
            check = dict(self.build_menu, frames=0 if self.build_menu['settled'] else args.get('frames', 0))
            if not check['settled'] and args.get('frames'):
                raise Failure(f'timeout: the build menu did not settle within {args["frames"]} frames', check)
            return check
        if cmd == 'seed':
            return {'unity': args['n'], 'util': True, 'krandom': True, 'consumedBefore': 0}
        if cmd == 'setup.call':
            return {'value': None, 'type': 'System.Void'}
        raise Failure(f'unknown command {cmd}')


class Failure(Exception):
    """A command's ok: false reply: its error, and the result it still carries (ui.wait's)."""

    def __init__(self, error, result=None):
        super().__init__(error)
        self.error, self.result = error, result


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        frame = 8861
        for line in self.rfile:
            frame += 1
            request = json.loads(line)
            if request.get('cmd') == 'noise':  # an unrelated line first, then the reply
                self.wfile.write(b'{"id": -1, "ok": true, "frame": 1, "result": "ignored"}\n')
            try:
                reply = {'id': request['id'], 'ok': True, 'frame': frame,
                         'result': self.server.game.handle(request['cmd'], request.get('args', {}))}
            except Failure as e:
                reply = {'id': request['id'], 'ok': False, 'frame': frame, 'error': e.error}
                if e.result is not None:
                    reply['result'] = e.result
            self.wfile.write((json.dumps(reply) + '\n').encode())


class FakeServer(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True


class FakeInput:
    """Records XTEST input; a click at a point runs the game's handler for it."""

    def __init__(self, on_click=None):
        self.events, self.on_click = [], on_click or (lambda x, y, mods: None)

    def click(self, x, y, button='left', mods=(), double=False):
        self.events.append(('double-click' if double else 'click', x, y, button, tuple(mods)))
        self.on_click(x, y, mods)

    def key(self, code):
        self.events.append(('key', code))

    def type_text(self, text):
        self.events.append(('type', text))

    def drag(self, start, end, button='left', mods=(), steps=8, hold=0, via=()):
        self.events.append(('drag', tuple(start), *(tuple(v) for v in via), tuple(end)))

    def move(self, x, y):
        self.events.append(('move', x, y))

    def scroll(self, x, y, notches):
        self.events.append(('scroll', x, y, notches))


class FakeMcp:
    def __init__(self, folder):
        self.folder, self.calls, self.port = folder, [], 8820

    def call(self, tool, **args):
        self.calls.append((tool, args))
        if tool == 'navigation_control' and args.get('action') == 'screenshot':
            path = os.path.join(self.folder, args['filename'] + '.png')
            from PIL import Image
            Image.new('RGB', (1280, 720)).save(path)
            return {'path': path}
        return {}


class DriverTests(unittest.TestCase):
    def setUp(self):
        self.game = FakeGame()
        self.server = FakeServer(('127.0.0.1', 0), Handler)
        self.server.game = self.game
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.port = self.server.server_address[1]
        self.driver = e2e.Driver(self.port).connect(5)
        self.temp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.driver.close()
        self.server.shutdown()
        self.server.server_close()
        self.temp.cleanup()

    def run_for(self, steps_input=None):
        run = e2e.Run({'name': 'unit', 'steps': [], 'setup': [], 'mods': []}, 'ours', 1, {}, e2e.Lane(2, None), lambda s: None)
        run.out = self.temp.name
        run.driver = self.driver
        run.input = steps_input or FakeInput()
        run.mcp = FakeMcp(self.temp.name)

        class StubPlayer:
            display, xauthority, profile = ':88', None, self.temp.name
        run.player = StubPlayer()
        return run

    # -- the client
    def test_wire_format(self):
        received = []
        server = socket.socket()
        server.bind(('127.0.0.1', 0))
        server.listen(1)

        def serve():
            conn, _ = server.accept()
            with conn, conn.makefile('rw') as f:
                received.append(json.loads(f.readline()))
                f.write('{"id": 1, "ok": true, "frame": 9, "result": {"x": 3, "y": 4, "onscreen": true, '
                        '"world": {"x": 10.5, "y": 20.5, "z": -25.5}}}\n')
                f.flush()
        t = threading.Thread(target=serve)
        t.start()
        driver = e2e.Driver(server.getsockname()[1]).connect(5)
        self.assertEqual(driver.to_screen([10, 20]), (3, 4))
        self.assertEqual(driver.frame, 9)
        t.join(5)
        driver.close()
        server.close()
        self.assertEqual(received, [{'id': 1, 'cmd': 'world.to_screen', 'args': {'cell': [10, 20]}}])

    def test_errors_and_unrelated_lines(self):
        with self.assertRaises(e2e.DriverError) as caught:
            self.driver.state('Research.Instance.nothing')
        self.assertIn('no field or property Research.Instance.nothing on Research', str(caught.exception))
        with self.assertRaises(e2e.DriverError):
            self.driver.request('noise')  # the fake answers an unknown command with an error, after an unrelated line
        self.game.state['x'] = 5
        self.assertEqual(self.driver.state('x'), 5)  # the connection is still in step

    def test_connect_fails_when_nothing_listens(self):
        s = socket.socket()
        s.bind(('127.0.0.1', 0))
        port = s.getsockname()[1]
        s.close()
        with self.assertRaises(e2e.DriverError):
            e2e.Driver(port).connect(1)

    def test_find_reads_the_element_description(self):
        self.game.add('Canvas/A', [10, 20, 100, 40], text='Hello', isOn=True, interactable=True)
        self.game.add('Canvas/B', [1, 2, 3, 4], hit='Canvas/B/Label', interactable=True, groupInteractable=False)
        self.game.add('Canvas/C', [0, 0, 20, 10], hit='Popup/Blocker')
        self.game.add('Canvas/C/Inner', [0, 0, 5, 5], hit='Canvas/C')
        a, = self.driver.find(path='Canvas/A')
        self.assertEqual((a.rect, a.center, a.text, a.is_on, a.interactable, a.relation, a.hit_is_self()),
                         ((10, 20, 100, 40), (60, 40), 'Hello', True, True, 'self', True))
        b, = self.driver.find(path='Canvas/B')
        # A non-interactable CanvasGroup above it: no clicks.
        self.assertEqual((b.rect, b.hit, b.relation, b.hit_is_self(), b.interactable),
                         ((1, 2, 3, 4), 'Canvas/B/Label', 'child', True, False))
        c, = self.driver.find(path='Canvas/C')
        self.assertEqual((c.relation, c.hit_is_self()), ('other', False))
        inner, = self.driver.find(path='Canvas/C/Inner')
        self.assertEqual((inner.relation, inner.hit_is_self()), ('ancestor', False))
        self.assertEqual(len(self.driver.find(path='Canvas/*', limit=1)), 1)
        self.assertEqual(self.game.requests[-1], ('ui.find', {'path': 'Canvas/*', 'limit': 1}))

    def test_at_and_to_cell(self):
        self.game.add('Screen/Panel', [0, 0, 100, 100], hit='Screen/Panel/Image')
        self.assertEqual(self.driver.at(50, 50)['path'], 'Screen/Panel/Image')
        self.assertEqual(self.driver.at(500, 500)['relation'], 'none')
        self.assertEqual(self.driver.to_cell(1100, 99), (110, 207))

    def test_wait_timeout_keeps_the_last_state(self):
        with self.assertRaises(e2e.DriverError) as caught:
            self.driver.wait('Screen/Never', frames=10)
        self.assertIn('timeout: exists not met within 10 frames', str(caught.exception))
        self.assertEqual(caught.exception.result, {'frames': 10, 'count': 0, 'element': None})
        self.game.add('Screen/Ready', [0, 0, 1, 1])
        self.assertEqual(self.driver.wait('Screen/Ready', 'stable', stable=5)['element']['path'], 'Screen/Ready')
        self.assertEqual(self.game.requests[-1], ('ui.wait', {'path': 'Screen/Ready', 'cond': 'stable', 'frames': 300,
                                                              'stable': 5}))

    def test_frames_are_round_trips(self):
        before = len(self.game.requests)
        self.driver.frames(3)
        self.assertEqual([c for c, _ in self.game.requests[before:]], ['ping'] * 3)

    def test_dump(self):
        self.game.add('Screen/List/Item', [0, 0, 5, 5])
        self.assertEqual(self.driver.dump('**/Screen'), '/Screen/List/Item\trect 0,0,5,5\n')
        with self.assertRaises(e2e.DriverError) as caught:
            self.driver.dump('**/Nope')
        self.assertIn('no element matches **/Nope', str(caught.exception))

    # -- the steps
    def test_click_checks_occlusion(self):
        self.game.add('Screen/Button', [100, 100, 50, 20], hit='Screen/Button/Label')
        self.game.add('Screen/Covered', [300, 100, 50, 20], hit='Popup/Blocker')
        run = self.run_for()
        run.step('click', {'ui': 'Screen/Button', 'mods': ['Shift']}, 1)
        self.assertEqual(run.input.events, [('click', 125, 110, 'left', ('Shift',))])
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.step('click', {'ui': 'Screen/Covered'}, 2)
        self.assertEqual(str(caught.exception), 'Screen/Covered is occluded by Popup/Blocker (hits there: Popup/Blocker)')
        self.assertEqual(len(run.input.events), 1)  # no blind click
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.step('click', {'text': 'Nope', 'under': 'Screen'}, 3)
        self.assertIn('no active element', str(caught.exception))

    def test_double_click(self):
        self.game.add('Screen/Row', [0, 0, 10, 10])
        run = self.run_for()
        run.step('click', {'ui': 'Screen/Row', 'double': True}, 1)
        self.assertEqual(run.input.events, [('double-click', 5, 5, 'left', ())])
        self.assertTrue(e2e.check_step('s', 'click', {'ui': 'x', 'double': 1}, False))

    def test_click_by_text_under(self):
        self.game.add('Screen/List/Item0', [0, 0, 10, 10], text='Sandstone')
        self.game.add('Other/Item0', [50, 50, 10, 10], text='Sandstone')
        run = self.run_for()
        run.step('click', {'text': 'Sandstone', 'under': 'Screen'}, 1)
        self.assertEqual(run.input.events[0][1:3], (5, 5))

    def test_toggle_asserts_the_new_state(self):
        self.game.add('Screen/Toggle', [0, 0, 20, 20], isOn=False)
        flips = FakeInput(lambda x, y, mods: self.game.elements['Screen/Toggle'].update(isOn=True))
        run = self.run_for(flips)
        run.step('toggle', {'ui': 'Screen/Toggle', 'to': True}, 1)
        self.assertEqual(len(flips.events), 1)
        run.step('toggle', {'ui': 'Screen/Toggle', 'to': True}, 2)  # already on: no click
        self.assertEqual(len(flips.events), 1)
        dead = FakeInput()  # #5's case: the click lands and nothing happens
        run = self.run_for(dead)
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.step('toggle', {'ui': 'Screen/Toggle', 'to': False}, 3)
        self.assertIn('isOn stayed True', str(caught.exception))

    def test_dropdown(self):
        self.game.add('Screen/Dropdown', [0, 0, 100, 20], text='Copper')

        def clicked(x, y, mods):
            if (x, y) == (50, 10):
                self.game.add('Screen/Dropdown/Dropdown List/Item 1', [0, 20, 100, 20], text='Sandstone')
            elif (x, y) == (50, 30):
                del self.game.elements['Screen/Dropdown/Dropdown List/Item 1']
                self.game.elements['Screen/Dropdown']['text'] = 'Sandstone'
        run = self.run_for(FakeInput(clicked))
        run.step('dropdown', {'ui': 'Screen/Dropdown', 'option': 'Sandstone'}, 1)
        self.assertEqual([e[1:3] for e in run.input.events], [(50, 10), (50, 30)])

    def test_type_key_drag_scroll(self):
        self.game.add('Dialog/Input', [10, 10, 100, 20])
        run = self.run_for()
        run.step('type', {'ui': 'Dialog/Input', 'text': 'e2e', 'enter': True}, 1)
        run.step('key', {'code': 'Shift+B'}, 2)
        run.step('drag', {'from': [110, 207], 'to': [118, 209]}, 3)
        run.step('drag', {'from': [110, 207], 'via': [[110, 208]], 'to': [118, 209]}, 4)
        run.step('click_world', {'cell': [100, 212]}, 4)
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.step('click_world', {'cell': [150, 212]}, 5)  # x 1500: off the 1280-pixel screen
        self.assertIn('cell [150, 212] is off screen', str(caught.exception))
        run.step('scroll', {'at': [100, 200], 'notches': -3}, 5)
        self.assertEqual(run.input.events, [('click', 60, 20, 'left', ()), ('type', 'e2e'), ('key', 'Return'),
                                            ('key', 'Shift+B'), ('drag', (1100, 99), (1180, 93)),
                                            ('drag', (1100, 99), (1100, 96), (1180, 93)),
                                            ('click', 1000, 84, 'left', ()), ('scroll', 1000, 120, -3)])

    def test_tool_by_toolbar_name(self):
        # Every toolbar button is an ActionIconToggle: the collection's sibling index tells them apart.
        row = 'ToolMenu/Row/ActionIconToggle'
        self.game.add(row, [0, 600, 40, 40], key='/ToolMenu#0/Row#0/ActionIconToggle#0')
        self.game.add(row, [50, 600, 40, 40], key='/ToolMenu#0/Row#0/ActionIconToggle#1', as_name='second')
        self.game.state['ToolMenu.Instance.basicTools'] = [
            {'text': 'Dig', 'icon': 'icon_action_dig', 'toggle': {'path': row}},
            {'text': 'Sweep', 'icon': 'filtered_clear', 'toggle': {'path': row}}]
        self.game.state['ToolMenu.Instance.basicTools[1].toggle.transform.GetSiblingIndex()'] = 1
        run = self.run_for()
        run.step('tool', {'toolbar': 'filtered_clear'}, 1)
        self.assertEqual(run.input.events[0][1:3], (70, 620))
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.step('tool', {'toolbar': 'Nothing'}, 2)
        self.assertIn('there are Dig (icon_action_dig), Sweep (filtered_clear)', str(caught.exception))

    def test_expect_ui_and_state(self):
        self.game.add('Card/Traits', [0, 0, 10, 10], text='Quick Learner, Uncultured')
        self.game.state['Research.Instance.queuedTech.Count'] = 4
        run = self.run_for()
        run.step('expect_ui', {'Card/Traits': {'text': '~Quick Learner', 'active': True}, 'Card/Gone': {'exists': False}}, 1)
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.expect_ui({'Card/Traits': {'text': 'Other'}}, tries=2)
        self.assertIn("text is 'Quick Learner, Uncultured', wanted 'Other'", str(caught.exception))
        run.step('expect_state', {'Research.Instance.queuedTech.Count': '>=4'}, 2)
        run.step('expect_state', {'Research.Instance.queuedTech.Count': '== original'}, 3)
        self.assertEqual(run.values[-1], {'step': 3, 'expr': 'Research.Instance.queuedTech.Count', 'value': 4,
                                          'wanted': '== original', 'deferred': True})
        with self.assertRaises(e2e.SpecFailure):
            run.step('expect_state', {'Research.Instance.queuedTech.Count': '>4'}, 4)
        os.makedirs(os.path.join(self.temp.name, 'blueprints'))
        open(os.path.join(self.temp.name, 'blueprints', 'e2e.blueprint'), 'w').close()
        run.step('expect_state', {'file:blueprints/e2e.blueprint': 'exists', 'file:blueprints/x': 'missing'}, 5)

    def test_cells_sum_and_arithmetic(self):
        self.game.state['Grid.WidthInCells'] = 10
        for cell, mass in ((21, 100.0), (22, 50.5), (31, 1.0), (32, 2.0)):
            self.game.state[f'Grid.Mass.get_Item({cell})'] = mass
        self.game.state['inventory'] = 10.0
        run = self.run_for()
        run.step('expect_state', {'cells:1,2..2,3::Grid.Mass.get_Item({cell})': {'save_as': 'dug'},
                                  'inventory': {'save_as': 'before'}}, 1)
        self.assertEqual(run.saved, {'dug': 153.5, 'before': 10.0})
        self.game.state['inventory'] = 160.0
        run.step('expect_state', {'inventory': '>= before + 0.95 * dug'}, 2)
        with self.assertRaises(e2e.SpecFailure):
            run.step('expect_state', {'inventory': '> (before + dug) * 1.1'}, 3)
        with self.assertRaises(e2e.SpecFailure) as caught:
            run.step('expect_state', {'inventory': '> nothing'}, 4)
        self.assertIn("no value saved as 'nothing'", str(caught.exception))
        run.original_values = {'5:inventory': 150.0}
        run.step('expect_state', {'inventory': '>= 0.9 * original'}, 5)

    def test_count_and_sum_read_every_item_in_one_request(self):
        spawnables = [{'.spawnInfo.id': 'GeyserGeneric_steam', '.isSpawned': False, '.spawnInfo.units': 2.0},
                      {'.spawnInfo.id': 'Tile', '.isSpawned': True, '.spawnInfo.units': 0.0},
                      {'.spawnInfo.id': 'GeyserGeneric_steam', '.isSpawned': True, '.spawnInfo.units': 180.0},
                      {'.spawnInfo.id': 'Hatch Baby', '.isSpawned': False, '.spawnInfo.units': True}]
        self.game.lists['SaveGame.Instance.worldGenSpawner.spawnables'] = spawnables
        self.game.lists['all:Clearable::'] = []
        run = self.run_for()
        run.step('expect_state', {
            'count:SaveGame.Instance.worldGenSpawner.spawnables[*] if .spawnInfo.id == GeyserGeneric_steam': 2,
            "count:SaveGame.Instance.worldGenSpawner.spawnables[*] if .spawnInfo.id == 'Hatch Baby'": 1,
            'count:SaveGame.Instance.worldGenSpawner.spawnables[*] if .isSpawned == true and .spawnInfo.id != Tile': 1,
            'count:SaveGame.Instance.worldGenSpawner.spawnables[*]': 4,
            'count:all:Clearable::[*] if .isMarkedForClear == true': 0,
            # Floats compare as Python prints them; a sum adds numbers only (not booleans).
            'count:SaveGame.Instance.worldGenSpawner.spawnables[*] if .spawnInfo.units == 180.0': 1,
            'sum:SaveGame.Instance.worldGenSpawner.spawnables[*].spawnInfo.units': 182.0,
            'sum:SaveGame.Instance.worldGenSpawner.spawnables[*].spawnInfo.units if .isSpawned == false': 2.0}, 1)
        each = [args for cmd, args in self.game.requests if cmd == 'state.each']
        self.assertEqual(len(each), 8)
        self.assertEqual(each[-1], {'expr': 'SaveGame.Instance.worldGenSpawner.spawnables',
                                    'members': ['.isSpawned', '.spawnInfo.units']})
        self.assertFalse([cmd for cmd, _ in self.game.requests if cmd == 'state.get'])

    def test_original_value_is_compared_when_known(self):
        self.game.state['n'] = 4
        run = self.run_for()
        run.original_values = {'7:n': 5}
        with self.assertRaises(e2e.SpecFailure):
            run.step('expect_state', {'n': '== original'}, 7)

    def test_evidence_steps(self):
        self.game.add('Screen/Selector', [100, 50, 400, 300])
        self.game.add('Screen/Selector/Date', [110, 60, 50, 10])
        run = self.run_for()
        run.step('ui_dump', {'name': 'selector', 'root': 'Screen/Selector'}, 1)
        self.assertTrue(open(os.path.join(self.temp.name, 'selector.ui.txt')).read().startswith('/Screen/Selector\t'))
        run.step('shot', {'name': 'selector', 'crop': 'Screen/Selector', 'mask': ['Screen/Selector/Date', [0, 0, 5, 5]]}, 2)
        meta = json.load(open(os.path.join(self.temp.name, 'selector.shot.json')))
        self.assertEqual((meta['crop'], meta['masks'], meta['file']),
                         ([100, 50, 400, 300], [[110, 60, 50, 10], [0, 0, 5, 5]], 'selector.png'))
        self.assertEqual(run.input.events[0], ('move', 1279, 719))  # the pointer off the UI first
        run.step('world_shot', {'name': 'zoomed', 'ortho': 45, 'at': [128, 210]}, 3)
        commands = [c for c, _ in self.game.requests]
        self.assertEqual(self.game.camera, {'pos': [0, 0], 'cell': [128, 210], 'ortho': 45})
        self.assertLess(commands.index('canvas.hide'), commands.index('canvas.show'))
        self.assertFalse(self.game.canvases_hidden)
        self.assertEqual([e['name'] for e in run.evidence['world_shot']], ['zoomed'])
        run.step('shot', 'plain', 4)  # the old form
        self.assertTrue(os.path.exists(os.path.join(self.temp.name, 'plain.png')))

    def test_evidence_waits_for_the_build_menu(self):
        self.game.add('Screen/Menu', [0, 0, 100, 100])
        run = self.run_for()
        self.game.build_menu = {'settled': True, 'open': True, 'reasons': [], 'frames': 3}
        run.step('ui_dump', {'name': 'menu', 'root': 'Screen/Menu'}, 1)
        self.assertIn(('ui.dump', {'root': 'Screen/Menu', 'settle': e2e.BUILD_MENU_FRAMES}), self.game.requests)
        run.step('shot', {'name': 'menu'}, 2)
        self.assertIn(('buildmenu.settled', {'frames': e2e.BUILD_MENU_FRAMES}), self.game.requests)
        self.game.build_menu = {'settled': False, 'open': True, 'reasons': ['Tile: shows state 2, wants 0 (Complete)']}
        with self.assertRaisesRegex(e2e.SpecFailure, 'shot menu: the build menu did not settle within 600 frames: Tile: shows'):
            run.step('shot', {'name': 'menu'}, 3)
        with self.assertRaisesRegex(e2e.SpecFailure, 'ui_dump menu: the build menu did not settle'):
            run.step('ui_dump', {'name': 'menu', 'root': 'Screen/Menu'}, 4)

    def test_a_driver_step_waits_for_the_build_menu(self):
        run = self.run_for()
        self.game.build_menu = {'settled': True, 'open': True, 'reasons': [], 'frames': 40}
        run.step('driver', {'buildmenu.settled': {}}, 1)
        self.assertIn(('buildmenu.settled', {'frames': e2e.BUILD_MENU_FRAMES}), self.game.requests)
        self.game.build_menu = {'settled': False, 'open': True, 'reasons': ['WireBridge: shows state 1, wants 0 (Complete)']}
        with self.assertRaisesRegex(e2e.SpecFailure, 'driver buildmenu.settled: the build menu did not settle within 600 frames: WireBridge'):
            run.step('driver', {'buildmenu.settled': {}}, 2)
        self.assertEqual(e2e.check_spec({'from': 'early-base', 'steps': [{'driver': {'buildmenu.settled': {}}}]}), [])

    def test_driver_and_wait_steps(self):
        run = self.run_for()
        run.step('driver', {'seed': 12345}, 'setup 1')
        run.step('driver', {'setup.call': {'type': 'T', 'method': 'M', 'args': []}}, 'setup 2')
        self.assertIn(('seed', {'n': 12345}), self.game.requests)
        self.assertIn(('setup.call', {'type': 'T', 'method': 'M', 'args': []}), self.game.requests)
        self.game.add('Screen/Ready', [0, 0, 1, 1])
        run.step('ui_wait', {'path': 'Screen/Ready', 'cond': 'stable'}, 1)
        with self.assertRaises(e2e.DriverError):
            run.step('ui_wait', {'path': 'Screen/Never'}, 2)

    def test_open(self):
        run = self.run_for()
        run.step('open', {'management': 'research'}, 1)
        run.step('open', {'key': 'R'}, 2)
        self.assertEqual(run.mcp.calls[0][0], 'game_control')
        self.assertEqual(run.input.events, [('key', 'R')])


if __name__ == '__main__':
    unittest.main()
