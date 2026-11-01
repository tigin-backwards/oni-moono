"""e2e.py's spec parsing and validation, options, keys and expectations."""
import json, os, tempfile, unittest

import helpers  # noqa: F401  (puts scripts/ on the path)
import e2e
import yaml


def spec(text):
    return yaml.safe_load(text)


class SpecValidation(unittest.TestCase):
    def test_existing_spec_is_valid(self):
        loaded = e2e.load_spec('early-base')
        self.assertEqual(loaded['name'], 'early-base')
        self.assertEqual(loaded['setup'], [])
        self.assertFalse(e2e.needs_driver(loaded))

    def test_every_new_step_kind_of_the_plan(self):
        # The step list of §4.4, as written there.
        s = spec('''
from: early-base
mods: [3468585385]
options: {SweepByType: {DisableIcons: true}}
setup:
  - call: {tool: game_control, args: {domain: speed, action: pause}}
  - driver: {setup.call: {type: X, method: Y, args: []}}
  - driver: {seed: 12345}
steps:
  - open: {management: research}
  - open: {key: R}
  - open: {user_menu: "Label"}
  - click: {ui: "*/ResearchScreen/**/Tech_FarmingTech/Button", mods: [Shift]}
  - click: {text: "Quick Learner", under: "UnityDuplicitySelectionScreen"}
  - toggle: {ui: "a/b", to: false}
  - dropdown: {ui: "a/b", option: "Sandstone"}
  - type: {ui: ".../Input", text: "e2e-blueprint", enter: true}
  - key: {code: "Shift+B"}
  - drag: {from: [110, 207], to: [118, 209]}
  - tool: {toolbar: "SweepByType"}
  - expect_ui: {"**/ToReplace/CurrentlyActive/Label": {text: "Quick Learner", active: true}}
  - expect_state: {"Research.Instance.queuedTech.Count": ">=3"}
  - ui_dump: {name: selector, root: "**/BlueprintSelector"}
  - shot: {name: selector, crop: "**/BlueprintSelector", mask: ["**/Portrait", "**/DateLabel"]}
  - world_shot: {name: zoomed, ortho: 45, at: [128, 210], hide_ui: true}
  - ui_wait: {path: "**/BlueprintSelector", cond: stable}
  - click_world: {cell: [150, 212]}
  - scroll: {at: center, notches: -40}
  - shot: end-of-ui
''')
        self.assertEqual(e2e.check_spec(s), [])
        self.assertTrue(e2e.needs_driver(s))

    def test_setup_call_only_in_setup(self):
        s = spec('''
from: landing
steps:
  - driver: {setup.call: {type: X, method: Y}}
''')
        self.assertEqual(e2e.check_spec(s), ['steps 1: driver setup.call is only allowed in setup:'])

    def test_problems_are_reported(self):
        s = spec('''
from: landing
mods: [abc]
colour: blue
steps:
  - frobnicate: 1
  - click: {under: X}
  - toggle: {ui: X}
  - key: {code: "Hyper+Q"}
  - key: {code: "B+Shift"}
  - drag: {from: [1, 2]}
  - world_shot: {name: w, mask: [dupes]}
  - driver: {teleport: {}}
  - {dig: [1, 2, 3, 4], run: {seconds: 1}}
''')
        problems = e2e.check_spec(s)
        joined = '\n'.join(problems)
        for text in ("unknown key 'colour'", '`mods` is a list of Workshop IDs', "steps 1: unknown step 'frobnicate'",
                     'steps 2: needs `ui`', 'steps 3: toggle needs `to', "unknown key 'Hyper'",
                     "'B' in 'B+Shift' is not a modifier", 'steps 6: drag takes', 'no symbolic masks',
                     "unknown E2eDriver command 'teleport'", 'steps 9: a step is one'):
            self.assertIn(text, joined)

    def test_click_at_a_screen_point(self):
        s = spec('''
from: early-base
steps:
  - click: {at: [640, 112.5]}
  - click: {at: [10, 20], double: true}
''')
        self.assertEqual(e2e.check_spec(s), [])
        s = spec('''
from: early-base
steps:
  - click: {at: [640]}
  - click: {at: [1, 2], ui: "**/X"}
''')
        joined = '\n'.join(e2e.check_spec(s))
        self.assertIn('steps 1: click `at` is a screen point', joined)
        self.assertIn('steps 2: click `at` is a screen point', joined)

    def test_key_held_down_across_steps(self):
        s = spec('''
from: early-base
steps:
  - key: {code: Q, down: true, at: [110, 207]}
  - run: {seconds: 2}
  - key: {code: Q, up: true}
''')
        self.assertEqual(e2e.check_spec(s), [])
        s = spec('''
from: early-base
steps:
  - key: {code: Q, down: true, up: true}
  - key: {code: "Shift+Q", down: true}
''')
        joined = '\n'.join(e2e.check_spec(s))
        self.assertIn('steps 1: key takes `down: true` or `up: true`, not both', joined)
        self.assertIn('steps 2: a key held down (`down`/`up`) is one key, without modifiers', joined)

    def test_file_only_expect_state_needs_no_driver(self):
        s = spec('from: x\nsteps:\n  - expect_state: {"file:blueprints/e2e.blueprint": exists}\n')
        self.assertEqual(e2e.check_spec(s), [])
        self.assertFalse(e2e.needs_driver(s))
        s = spec('from: x\nsteps:\n  - shot: {name: a, crop: "**/X"}\n')
        self.assertTrue(e2e.needs_driver(s))

    def test_load_spec_rejects_invalid(self):
        with tempfile.NamedTemporaryFile('w', suffix='.yaml', delete=False) as f:
            f.write('from: landing\nsteps:\n  - nope: 1\n')
        try:
            with self.assertRaises(e2e.SpecError):
                e2e.load_spec(f.name)
        finally:
            os.remove(f.name)


    def test_load_spec_rejects_a_repeated_key(self):
        text = ('from: early-base\nsteps:\n  - expect_state:\n      "A.B": "> 1"\n      "C": 2\n'
                '      "A.B": "== original"\n')
        with tempfile.NamedTemporaryFile('w', suffix='.yaml', delete=False) as f:
            f.write(text)
        try:
            with self.assertRaises(e2e.SpecError) as caught:
                e2e.load_spec(f.name)
            message = str(caught.exception)
            self.assertIn(f.name, message)
            self.assertIn("'A.B'", message)
            self.assertIn('lines 4 and 6', message)
            with open(f.name, 'w') as g:  # at the top level too
                g.write('from: early-base\nsteps: []\nfrom: landing\n')
            with self.assertRaisesRegex(e2e.SpecError, "'from' is repeated, at lines 1 and 3"):
                e2e.load_spec(f.name)
        finally:
            os.remove(f.name)

    def test_every_committed_spec_loads(self):
        # Without a repeated key anywhere, and with every count:/sum: parsed.
        for folder in (e2e.SPECS, os.path.join(e2e.SPECS, 'mods')):
            for name in sorted(os.listdir(folder)):
                if name.endswith('.yaml') and name != 'known-findings.yaml':
                    e2e.load_spec(os.path.join(folder, name))
        e2e.load_findings()

    def test_check_reports_a_bad_aggregate(self):
        s = spec('from: x\nsteps:\n  - expect_state: {"count:all:X::[*] if .name == two words": 1}\n')
        self.assertEqual(len(e2e.check_spec(s)), 1)
        s = spec('from: x\nsteps:\n  - expect_state: {"count:all:X::[*] if .name == \'two words\'": 1}\n')
        self.assertEqual(e2e.check_spec(s), [])
        s = spec('from: x\nsteps:\n  - expect_state: {"a": []}\n')
        self.assertEqual(len(e2e.check_spec(s)), 1)


class Aggregates(unittest.TestCase):
    parse = staticmethod(e2e.parse_aggregate)

    def test_plain_forms(self):
        self.assertIsNone(self.parse('Research.Instance.queuedTech.Count'))
        self.assertEqual(self.parse('count:all:Clearable::[*] if .isMarkedForClear == true'),
                         ('count', 'all:Clearable::', '', [('.isMarkedForClear', '==', 'true')]))
        self.assertEqual(self.parse('count:all:X::[*]'), ('count', 'all:X::', '', []))
        self.assertEqual(
            self.parse('sum:all:Clearable::[*].GetComponent<PrimaryElement>.Mass if '
                       '.GetComponent<PrimaryElement>.ElementID == SandStone and .a != 1'),
            ('sum', 'all:Clearable::', '.GetComponent<PrimaryElement>.Mass',
             [('.GetComponent<PrimaryElement>.ElementID', '==', 'SandStone'), ('.a', '!=', '1')]))

    def test_quoted_text_is_kept_whole(self):
        tests = lambda expr: self.parse(expr)[3]
        self.assertEqual(tests("count:X[*] if .name == 'Maximum size: 200 tiles'"),
                         [('.name', '==', 'Maximum size: 200 tiles')])
        self.assertEqual(tests("count:X[*] if .name == 'Salt and Pepper' and .b == 1"),
                         [('.name', '==', 'Salt and Pepper'), ('.b', '==', '1')])
        self.assertEqual(tests("count:X[*] if .name != 'a == b'"), [('.name', '!=', 'a == b')])
        self.assertEqual(tests('count:X[*] if .name == "it\'s here"'), [('.name', '==', "it's here")])
        self.assertEqual(tests('count:X[*] if .id.StartsWith("Geyser and Vent") == true'),
                         [('.id.StartsWith("Geyser and Vent")', '==', 'true')])
        self.assertEqual(tests('count:X[*] if .Get(a, b) == 2'), [('.Get(a, b)', '==', '2')])
        self.assertEqual(self.parse('sum:X[*].Get("a b").Mass if .c == 1')[2], '.Get("a b").Mass')
        self.assertEqual(self.parse('count:Db.Get("x [*] y").List[*]')[1], 'Db.Get("x [*] y").List')

    def test_bad_forms(self):
        for expr in ('count:X[*] if .name == two words', "count:X[*] if .name == 'open", 'count:X[*] if name == 1',
                     'count:X[*] if .a == 1 == 2', 'count:X[*] if ', 'count:X', 'count:[*]', 'sum:X[*].a b',
                     'count:X[*] if .a == 1 and', "count:X[*] if .a == 'x'y'"):
            with self.assertRaises(e2e.SpecFailure, msg=expr):
                self.parse(expr)


class Arguments(unittest.TestCase):
    def test_defaults(self):
        o = e2e.parse_args(['early-base'])
        self.assertEqual((o['players'], o['lanes'], o['first_lane'], o['displays'], o['perf_wait']),
                         ('ours', 1, 1, None, True))

    def test_lanes_and_displays(self):
        o = e2e.parse_args(['--lanes', '2', '--first-lane', '2', '--displays', ':88,89', '--no-perf-wait', 'a', 'b'])
        self.assertEqual((o['lanes'], o['first_lane'], o['displays'], o['perf_wait'], o['names']),
                         (2, 2, [':88', ':89'], False, ['a', 'b']))

    def test_bad(self):
        for argv in (['--lanes', '7', 'a'], ['--lanes', '2', '--displays', ':88', 'a'], ['--player', 'x', 'a'],
                     ['--displays', ':88,:88', 'a'], ['--frob', 'a'], [], ['--lanes']):
            with self.assertRaises(e2e.SpecError, msg=argv):
                e2e.parse_args(argv)


class Keys(unittest.TestCase):
    def test_combos(self):
        self.assertEqual(e2e.parse_key('Shift+B'), (['Shift_L'], 'b'))
        self.assertEqual(e2e.parse_key('Ctrl + Alt + F5'), (['Control_L', 'Alt_L'], 'F5'))
        self.assertEqual(e2e.parse_key('Escape'), ([], 'Escape'))
        self.assertEqual(e2e.parse_key('Alpha3'), ([], '3'))
        self.assertEqual(e2e.parse_key('7'), ([], '7'))
        self.assertEqual(e2e.parse_key('LeftShift+UpArrow'), (['Shift_L'], 'Up'))
        self.assertEqual(e2e.parse_key('Shift'), ([], 'Shift_L'))
        self.assertEqual(e2e.parse_key('KeypadPlus'), ([], 'KP_Add'))
        self.assertEqual(e2e.parse_key('return'), ([], 'Return'))

    def test_table_covers_the_plan(self):
        for name in ['A', 'Z', 'Alpha0', 'Alpha9', 'F1', 'F12', 'LeftShift', 'LeftControl', 'LeftAlt', 'Escape',
                     'Return', 'UpArrow', 'DownArrow', 'LeftArrow', 'RightArrow']:
            self.assertIsNotNone(e2e.keysym_name(name), name)

    def test_unknown(self):
        with self.assertRaises(e2e.SpecError):
            e2e.parse_key('Meta+Q')


class Expectations(unittest.TestCase):
    def check(self, have, wanted, saved=None, original=None):
        return e2e.state_check(have, wanted, {} if saved is None else saved, original)

    def test_values_and_relations(self):
        self.assertEqual(self.check(3, '>=3'), (True, False))
        self.assertEqual(self.check(2, '>=3'), (False, False))
        self.assertEqual(self.check(1, 1), (True, False))
        self.assertEqual(self.check('x', 'x'), (True, False))
        self.assertEqual(self.check(True, True), (True, False))
        self.assertEqual(self.check(1, True), (False, False))
        self.assertEqual(self.check(None, 'exists'), (False, False))
        self.assertEqual(self.check('text', 'exists'), (True, False))
        self.assertEqual(self.check(None, 'missing'), (True, False))
        self.assertEqual(self.check(['QuickLearner', 'Uncultured'], {'contains': 'QuickLearner'}), (True, False))
        self.assertEqual(self.check(None, {'contains': 'QuickLearner'}), (False, False))

    def test_saved_values(self):
        saved = {}
        self.assertEqual(self.check(100.0, {'save_as': 'before'}, saved), (True, False))
        self.assertEqual(saved, {'before': 100.0})
        self.assertEqual(self.check(96.0, '>= 0.95 * before', saved), (True, False))
        self.assertEqual(self.check(90.0, '>= 0.95 * before', saved), (False, False))
        with self.assertRaises(e2e.SpecFailure):
            self.check(1, '== later', saved)
        # Text in quotes, for names such as an enum's.
        self.assertEqual(self.check('Agriculture', "!= 'AnimalControl'", saved), (True, False))
        self.assertEqual(self.check('R90', '== "R90"', saved), (True, False))

    def test_original_is_deferred(self):
        self.assertEqual(self.check(4, '== original'), (True, True))
        self.assertEqual(self.check(4, '== original', original=4), (True, False))
        self.assertEqual(self.check(3, '== original', original=4), (False, False))
        # A boolean is compared as itself, not as a number.
        self.assertEqual(self.check(False, '== original', original=False), (True, False))
        self.assertEqual(self.check(True, '== original', original=False), (False, False))

    def test_a_list_of_checks(self):
        self.assertEqual(self.check(997.0, ['>= 990', '<= 1010']), (True, False))
        self.assertEqual(self.check(980.0, ['>= 990', '<= 1010']), (False, False))
        self.assertEqual(self.check(['A', 'B'], [{'contains': 'A'}, {'contains': 'B'}]), (True, False))
        self.assertEqual(self.check(['A'], [{'contains': 'A'}, {'contains': 'B'}]), (False, False))
        # One part waits for the original; the others are checked now.
        self.assertEqual(self.check(3, ['> 1', '== original']), (True, True))
        self.assertEqual(self.check(1, ['> 1', '== original']), (False, True))
        self.assertEqual(self.check(3, ['> 1', '== original'], original=3), (True, False))
        self.assertEqual(self.check(3, ['> 1', '== original'], original=4), (False, False))

    def test_ui(self):
        element = e2e.Element({'path': 'a/b', 'rect': {'x': 0, 'y': 0, 'w': 10, 'h': 10}, 'active': True, 'isOn': False,
                               'interactable': True, 'text': 'Quick Learner (+2)'})
        self.assertEqual(e2e.ui_mismatches(element, {'active': True, 'text': '~Quick Learner'}), [])
        self.assertEqual(e2e.ui_mismatches(element, {'text': '/^Quick .*\\(\\+\\d\\)$/'}), [])
        self.assertEqual(e2e.ui_mismatches(element, {'isOn': True}), ['isOn is False, wanted True'])
        self.assertEqual(e2e.ui_mismatches(None, {'exists': False}), [])
        self.assertEqual(e2e.ui_mismatches(None, {'active': True}), ['no such element'])
        self.assertEqual(e2e.ui_mismatches(element, {'exists': False}), ['exists is True, wanted False'])

    def test_compare_keeps_the_old_forms(self):
        self.assertTrue(e2e.compare(3, 3))
        self.assertTrue(e2e.compare(3, '>=2'))
        self.assertFalse(e2e.compare(3, '<3'))
        with self.assertRaises(e2e.SpecFailure):
            e2e.compare(3, 'three')


class RunStep(unittest.TestCase):
    def test_a_paused_game_is_resumed_and_paused_again(self):
        calls = []
        steps = iter([{'elapsedGameSeconds': 0.0, 'status': 'completed', 'isPaused': True},
                      {'elapsedGameSeconds': 60.0, 'status': 'completed'},
                      {'elapsedGameSeconds': 60.0, 'status': 'completed'}])

        class Mcp:
            def call(self, tool, **args):
                calls.append(args['action'])
                return next(steps) if args['action'] == 'step_until' else {}
        sleep, e2e.time.sleep = e2e.time.sleep, lambda s: None
        try:
            self.assertEqual(e2e.run(Mcp(), 120), 120.0)
        finally:
            e2e.time.sleep = sleep
        self.assertEqual(calls, ['step_until', 'resume', 'step_until', 'step_until', 'pause'])

    def test_speed_reaches_each_step(self):
        speeds = []

        class Mcp:
            def call(self, tool, **args):
                speeds.append(args.get('speed'))
                return {'elapsedGameSeconds': 8.0, 'status': 'completed'}
        self.assertEqual(e2e.run(Mcp(), 8, speed=1), 8.0)
        self.assertEqual(speeds, [1])

    def test_speed_is_checked(self):
        s = spec('''
from: early-base
steps:
  - run: {seconds: 8, speed: 1}
  - run: {seconds: 8, speed: 10}
''')
        self.assertEqual(e2e.check_spec(s), ['steps 2: run speed is 1, 2 or 3'])


class Options(unittest.TestCase):
    def test_plib_config_beside_each_dll_and_shared(self):
        with tempfile.TemporaryDirectory() as profile:
            mod = os.path.join(profile, 'mods', 'Local', '1863428350')
            os.makedirs(os.path.join(mod, 'archived_versions', 'old'))
            open(os.path.join(mod, 'SweepByType.dll'), 'w').close()
            open(os.path.join(mod, 'archived_versions', 'old', 'SweepByType.dll'), 'w').close()
            with open(os.path.join(mod, 'config.json'), 'w') as f:
                json.dump({'Kept': 1, 'DisableIcons': False}, f)
            written = e2e.write_options(profile, {'1863428350': 'SweepByType'}, {'SweepByType': {'DisableIcons': True}})
            self.assertEqual(sorted(os.path.relpath(w, profile) for w in written), [
                'mods/Local/1863428350/archived_versions/old/config.json',
                'mods/Local/1863428350/config.json',
                'mods/config/SweepByType/config.json'])
            self.assertEqual(json.load(open(os.path.join(mod, 'config.json'))), {'Kept': 1, 'DisableIcons': True})
            # By Workshop ID too.
            e2e.write_options(profile, {'1863428350': 'SweepByType'}, {'1863428350': {'X': 2}})
            self.assertEqual(json.load(open(os.path.join(mod, 'config.json')))['X'], 2)
            with self.assertRaises(e2e.SpecFailure):
                e2e.write_options(profile, {'1863428350': 'SweepByType'}, {'Other': {'X': 1}})

    @staticmethod
    def tiny_assembly(module):
        """The least of a PE file e2e.module_name reads: one section holding a CLI header and
        metadata with a #~ stream (its Module table only) and a #Strings heap."""
        import struct
        strings = b'\0' + module.encode('utf-8') + b'\0'
        strings += b'\0' * (-len(strings) % 4)
        tables = struct.pack('<IBBBBQQI', 0, 2, 0, 0, 1, 1, 0, 1) + struct.pack('<HHHHH', 0, 1, 1, 0, 0)
        tables += b'\0' * (-len(tables) % 4)
        version = b'v4.0.30319\0\0'
        headers = struct.pack('<IIHH', 0x424A5342, 0x10001, 0, 0) + struct.pack('<I', len(version)) + version
        headers += struct.pack('<HH', 0, 2)
        streams_at = len(headers) + 2 * 8 + 4 + 12
        headers += struct.pack('<II', streams_at, len(tables)) + b'#~\0\0'
        headers += struct.pack('<II', streams_at + len(tables), len(strings)) + b'#Strings\0\0\0\0'
        metadata = headers + tables + strings
        section_rva, raw = 0x2000, 0x200
        cli = struct.pack('<IHHII', 72, 2, 5, section_rva + 72, len(metadata)) + b'\0' * 56
        body = cli + metadata
        pe = 0x80
        coff = struct.pack('<HHIIIHH', 0x14c, 1, 0, 0, 0, 224, 0x2102)
        optional = struct.pack('<H', 0x10b) + b'\0' * 94 + b'\0' * (14 * 8) + struct.pack('<II', section_rva, 72) + b'\0' * 8
        section = b'.text\0\0\0' + struct.pack('<IIII', len(body), section_rva, len(body), raw) + b'\0' * 16
        head = b'MZ' + b'\0' * 58 + struct.pack('<I', pe)
        head += b'\0' * (pe - len(head)) + b'PE\0\0' + coff + optional + section
        return head + b'\0' * (raw - len(head)) + body

    def test_module_name_names_the_shared_config_folder(self):
        with tempfile.TemporaryDirectory() as profile:
            mod = os.path.join(profile, 'mods', 'Local', '2959893715')
            os.makedirs(mod)
            # A zip's file name decoded in the wrong code page; the metadata keeps the right one.
            with open(os.path.join(mod, '┤≤╥╗═│.dll'), 'wb') as f:
                f.write(self.tiny_assembly('大一统.dll'))
            self.assertEqual(e2e.module_name(os.path.join(mod, '┤≤╥╗═│.dll')), '大一统.dll')
            written = e2e.write_options(profile, {'2959893715': '2959893715'}, {'2959893715': {'X': 1}})
            self.assertIn('mods/config/大一统/config.json', [os.path.relpath(w, profile) for w in written])
        self.assertIsNone(e2e.module_name(__file__))

    def test_config_file_named_by_the_spec(self):
        with tempfile.TemporaryDirectory() as profile:
            mod = os.path.join(profile, 'mods', 'Local', '2890083659')
            os.makedirs(mod)
            open(os.path.join(mod, 'MiniBox.dll'), 'w').close()
            written = e2e.write_options(profile, {'2890083659': '2890083659'},
                                        {'2890083659': {'$file': 'MiniBox.json', 'WireLoadKw': 3.0}})
            self.assertEqual(sorted(os.path.relpath(w, profile) for w in written), [
                'mods/Local/2890083659/MiniBox.json',
                'mods/config/MiniBox/MiniBox.json'])
            self.assertEqual(json.load(open(os.path.join(mod, 'MiniBox.json'))), {'WireLoadKw': 3.0})
        problems = e2e.check_spec({'steps': [], 'options': {'X': {'$file': '../x.json'}}})
        self.assertTrue(any('$file' in p for p in problems), problems)


class Install(unittest.TestCase):
    def test_onimcp_and_driver_beside_the_mods(self):
        with tempfile.TemporaryDirectory() as root:
            onimcp, driver = os.path.join(root, 'onimcp'), os.path.join(root, 'driver')
            os.makedirs(onimcp)
            with open(os.path.join(onimcp, 'mod.yaml'), 'w') as f:
                f.write('title: "x"\nstaticID: "LIghtJUNction.OniMcp"\n')
            # E2eDriver: its build's assembly, and its yaml files from tools/E2eDriver.
            os.makedirs(driver)
            for name in ('E2eDriver.dll', 'E2eDriver.pdb', 'E2eDriver.deps.json'):
                open(os.path.join(driver, name), 'w').close()
            fixture = os.path.join(root, 'landing.sav')
            open(fixture, 'w').close()
            lane = e2e.Lane(3, None)
            player = e2e.Player('ours', os.path.join(root, 'run'), {'E2E_ONIMCP': onimcp, 'E2E_DRIVER': driver},
                                fixture, lane, driver=True)
            mods = json.load(open(os.path.join(player.profile, 'mods', 'mods.json')))['mods']
            self.assertEqual([(m['label']['id'], m['staticID'], m['label']['version']) for m in mods],
                             [('OniMcp', 'LIghtJUNction.OniMcp', 312605614),
                              ('E2eDriver', 'E2eDriver', 1399985550)])
            installed = os.path.join(player.profile, 'mods', 'Local', 'E2eDriver')
            self.assertEqual(sorted(os.listdir(installed)), ['E2eDriver.dll', 'mod.yaml', 'mod_info.yaml'])
            config = json.load(open(os.path.join(player.profile, 'OniMcpConfig.json')))
            self.assertEqual(config['Port'], 8830)
            self.assertEqual(lane.driver_port, 8831)


class Translation(unittest.TestCase):
    """A translation installed and left disabled; `translation: ID` picks its language in the prefs."""

    class FakeWs:
        @staticmethod
        def profile(mods_dir, ids):
            # 929139073 is a translation (a strings.po alone); 1863428350 has code beside its .po.
            for i, files in (('929139073', ['strings.po', 'preview.png']), ('1863428350', ['strings.po', 'Sweep.dll'])):
                os.makedirs(os.path.join(mods_dir, 'Local', i))
                for name in files:
                    open(os.path.join(mods_dir, 'Local', i, name), 'w').close()
            entries = [{'label': {'distribution_platform': 0, 'id': i, 'title': i, 'version': 1}, 'status': 1,
                        'enabled': True, 'enabledForDlc': ['', 'EXPANSION1_ID'], 'staticID': i} for i in ids]
            with open(os.path.join(mods_dir, 'mods.json'), 'w') as f:
                json.dump({'version': 1, 'mods': entries}, f)

        @staticmethod
        def mono_hash(text):
            return 7

    def player(self, root, translation, language=None):
        onimcp = os.path.join(root, 'onimcp')
        os.makedirs(onimcp, exist_ok=True)
        old, e2e.ws_mods = e2e.ws_mods, lambda: self.FakeWs
        try:
            player = e2e.Player('ours', os.path.join(root, 'run'), {'E2E_ONIMCP': onimcp}, None, e2e.Lane(3, None),
                                ['929139073', '1863428350'], translation=translation, language=language)
        finally:
            e2e.ws_mods = old
        with open(os.path.join(player.profile, 'mods', 'mods.json')) as f:
            mods = json.load(f)['mods']
        with open(os.path.join(player.profile, 'kplayerprefs.yaml')) as f:
            prefs = yaml.safe_load(f)
        return [(m['label']['id'], m['enabled'], m['enabledForDlc']) for m in mods[:2]], prefs

    def test_the_translation_disabled_and_its_language_in_the_prefs(self):
        with tempfile.TemporaryDirectory() as root:
            mods, prefs = self.player(root, '929139073')
            self.assertEqual(mods, [('929139073', False, []), ('1863428350', True, ['', 'EXPANSION1_ID'])])
            self.assertEqual(prefs['strings'], {'SelectedLanguageType': 'UGC', 'InstalledLanguage': '929139073'})
            self.assertEqual(prefs['ints']['ResolutionWidth'], e2e.WIDTH)

    def test_without_translation_the_prefs_pick_no_language(self):
        with tempfile.TemporaryDirectory() as root:
            mods, prefs = self.player(root, None)
            self.assertEqual(prefs['strings'], {})
            # Still disabled: a player picks it on the Translations screen.
            self.assertEqual(mods, [('929139073', False, []), ('1863428350', True, ['', 'EXPANSION1_ID'])])

    def test_a_language_of_the_game_in_the_prefs(self):
        with tempfile.TemporaryDirectory() as root:
            _, prefs = self.player(root, None, 'zh_klei')
            self.assertEqual(prefs['strings'], {'SelectedLanguageType': 'Preinstalled', 'SelectedLanguageCode': 'zh_klei'})

    def test_tweaks_menu_is_a_flag(self):
        self.assertEqual(e2e.check_spec({'from': 'main-menu', 'mods': [], 'tweaks_menu': True, 'steps': []}), [])
        self.assertIn('`tweaks_menu` is true or false',
                      e2e.check_spec({'from': 'main-menu', 'mods': [], 'tweaks_menu': 'yes', 'steps': []}))

    def test_blocked_plugins_are_file_names(self):
        for how in ('unreadable', 'missing'):
            self.assertEqual(e2e.check_spec({'from': 'main-menu', 'blocked_plugins': {'libSimDLL.so': how}, 'steps': []}), [])
        for bad in ('libSimDLL.so', ['libSimDLL.so'], {'x86_64/libSimDLL.so': 'missing'}, {'libSimDLL.so': 'gone'}):
            self.assertIn('`blocked_plugins` maps plugin file names to unreadable or missing',
                          e2e.check_spec({'from': 'main-menu', 'blocked_plugins': bad, 'steps': []}))
        self.assertIn('`data_collection` is true or false',
                      e2e.check_spec({'from': 'main-menu', 'data_collection': 'no', 'steps': []}))

    def test_extra_saves_are_fixture_names(self):
        self.assertEqual(e2e.check_spec({'from': 'main-menu', 'extra_saves': ['terra-wide'], 'steps': []}), [])
        for bad in ('terra-wide', ['../x'], [1]):
            self.assertIn('`extra_saves` is a list of fixture names',
                          e2e.check_spec({'from': 'main-menu', 'extra_saves': bad, 'steps': []}))

    def test_blocked_install_links_all_but_the_blocked_plugin(self):
        with tempfile.TemporaryDirectory() as root:
            game, elsewhere = os.path.join(root, 'game'), os.path.join(root, 'depot')
            os.makedirs(os.path.join(elsewhere, 'x86_64'))
            os.makedirs(os.path.join(game, 'Game_Data', 'Plugins', 'AnyCPU'))
            for path in ('x86_64/libSimDLL.so', 'x86_64/libfmod.so'):
                with open(os.path.join(elsewhere, path), 'w') as f:
                    f.write(path)
            # The plugin folder is a link into another install, as in the frozen test installs.
            os.symlink(os.path.join(elsewhere, 'x86_64'), os.path.join(game, 'Game_Data', 'Plugins', 'x86_64'))
            for path in ('Game', 'Game_Data/level0', 'Game_Data/Plugins/AnyCPU/libsteam_api.so'):
                with open(os.path.join(game, path), 'w') as f:
                    f.write(path)
            dest = e2e.blocked_install(game, {'libSimDLL.so': 'unreadable'}, os.path.join(root, 'copy'))
            plugins = os.path.join(dest, 'Game_Data', 'Plugins')
            blocked = os.path.join(plugins, 'x86_64', 'libSimDLL.so')
            self.assertFalse(os.path.islink(blocked))
            self.assertEqual(os.stat(blocked).st_mode & 0o777, 0)
            for path in ('Game', 'Game_Data/level0', 'Game_Data/Plugins/AnyCPU', 'Game_Data/Plugins/x86_64/libfmod.so'):
                self.assertTrue(os.path.islink(os.path.join(dest, path)), path)
            for path in ('Game_Data', 'Game_Data/Plugins', 'Game_Data/Plugins/x86_64'):
                self.assertFalse(os.path.islink(os.path.join(dest, path)), path)
            # The install itself is untouched.
            self.assertEqual(os.stat(os.path.join(elsewhere, 'x86_64', 'libSimDLL.so')).st_mode & 0o444, 0o444)
            missing = e2e.blocked_install(game, {'libSimDLL.so': 'missing'}, os.path.join(root, 'copy2'))
            self.assertFalse(os.path.lexists(os.path.join(missing, 'Game_Data', 'Plugins', 'x86_64', 'libSimDLL.so')))
            self.assertTrue(os.path.islink(os.path.join(missing, 'Game_Data', 'Plugins', 'x86_64', 'libfmod.so')))
            with self.assertRaises(e2e.SpecFailure):
                e2e.blocked_install(game, {'nope.so': 'missing'}, os.path.join(root, 'copy3'))

    def test_language_is_one_of_the_games_own(self):
        self.assertEqual(e2e.check_spec({'from': 'main-menu', 'mods': [], 'language': 'zh_klei', 'steps': []}), [])
        self.assertTrue(any('`language` is one of' in p for p in
                            e2e.check_spec({'from': 'main-menu', 'mods': [], 'language': 'de', 'steps': []})))
        self.assertIn('`language` and `translation` both pick the language',
                      e2e.check_spec({'from': 'main-menu', 'mods': [929139073], 'translation': 929139073,
                                      'language': 'zh_klei', 'steps': []}))

    def test_translation_names_one_of_the_mods(self):
        self.assertIn('`translation` names one of `mods`',
                      e2e.check_spec({'from': 'main-menu', 'mods': [1], 'translation': 2, 'steps': []}))
        self.assertEqual(e2e.check_spec({'from': 'main-menu', 'mods': [929139073], 'translation': 929139073,
                                         'steps': []}), [])


class SpacedOut(unittest.TestCase):
    """Which mode a spec's players run in: its `spaced_out`, else its fixture's header."""

    def save(self, folder, name, info):
        with open(os.path.join(folder, name + '.sav'), 'wb') as f:
            f.write(b'\x00\x01KSAV' + json.dumps(info).encode() + b'\x00tail{not json')

    def test_mode_follows_the_fixture_or_the_spec(self):
        with tempfile.TemporaryDirectory() as folder:
            self.save(folder, 'so', {'dlcIds': ['EXPANSION1_ID', 'DLC2_ID'], 'clusterId': 'expansion1::clusters/X'})
            self.save(folder, 'base', {'dlcIds': ['', 'DLC2_ID'], 'clusterId': 'clusters/SandstoneDefault'})
            self.save(folder, 'old', {'clusterId': 'expansion1::clusters/Y'})
            old_fixtures, e2e.FIXTURES = e2e.FIXTURES, folder
            try:
                self.assertTrue(e2e.spaced_out({'from': 'so'}))
                self.assertFalse(e2e.spaced_out({'from': 'base'}))
                self.assertTrue(e2e.spaced_out({'from': 'old'}))
                self.assertFalse(e2e.spaced_out({'from': 'so', 'spaced_out': False}))
                self.assertTrue(e2e.spaced_out({'from': e2e.MAIN_MENU}))
                self.assertIsNone(e2e.fixture_of({'from': e2e.MAIN_MENU}))
            finally:
                e2e.FIXTURES = old_fixtures

    def test_spaced_out_is_a_boolean(self):
        self.assertIn('`spaced_out` is true or false',
                      e2e.check_spec({'from': 'main-menu', 'spaced_out': 'no', 'steps': []}))
        self.assertEqual(e2e.check_spec({'from': 'main-menu', 'spaced_out': False, 'steps': []}), [])


if __name__ == '__main__':
    unittest.main()
