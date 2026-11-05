"""e2e.py's differential checks (§5.3): ours against the original's evidence, through ui-diff.py
and qa-tools.py image, and blessed references."""
import json, os, tempfile, unittest

import helpers  # noqa: F401
import e2e
from test_image_diff import DUMP_A, DUMP_B, png


class Differential(unittest.TestCase):
    """e2e.differential: ours against an original run's folder."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.orig, self.ours = os.path.join(self.temp.name, 'orig'), os.path.join(self.temp.name, 'ours')
        for folder, dump, clock in ((self.orig, DUMP_A, (0, 0, 255)), (self.ours, DUMP_B, (0, 255, 0))):
            os.makedirs(folder)
            open(os.path.join(folder, 'panel.ui.txt'), 'w').write(dump)
            png(os.path.join(folder, 'panel.png'), size=(1280, 720),
                boxes=[(540, 310, 200, 100, (90, 90, 90)), (550, 320, 120, 20, clock)])
            json.dump({'name': 'panel', 'crop': [540, 310, 200, 100], 'masks': [], 'file': 'panel.png'},
                      open(os.path.join(folder, 'panel.shot.json'), 'w'))
            open(os.path.join(folder, 'player.log'), 'w').write(
                'PLib options saved\n' if folder == self.orig else 'NullReferenceException: Object reference\n')

    def tearDown(self):
        self.temp.cleanup()

    def result(self, masks=()):
        meta = {'name': 'panel', 'crop': [540, 310, 200, 100], 'masks': list(masks), 'file': 'panel.png'}
        return {'signatures': {'NullReferenceException: Object reference', 'shared'}, 'log': os.path.join(self.ours, 'player.log'),
                'out': self.ours, 'evidence': {'ui_dump': [{'name': 'panel', 'mask_text': []}], 'shot': [meta], 'world_shot': []},
                'values': [{'step': 4, 'expr': 'n', 'value': 3, 'wanted': '== original', 'deferred': True}]}

    def test_reports_each_difference(self):
        spec = {'require_log': ['PLib options saved'], 'mask_text': [r'\d+']}
        reference = e2e.Reference(self.orig, {'shared'}, e2e.log_matches(os.path.join(self.orig, 'player.log'),
                                                                         spec['require_log']), {'4:n': 4}, 'test')
        checks, reasons = e2e.differential(spec, self.result(), reference)
        self.assertEqual(checks['signatures']['new'], ['NullReferenceException: Object reference'])
        self.assertEqual(checks['require_log']['missing'], ['PLib options saved'])
        self.assertEqual(checks['ui_dump']['panel']['differences'], 3)
        self.assertAlmostEqual(checks['shot']['panel']['fraction'], 2400 / 20000)
        self.assertFalse(checks['shot']['panel']['ok'])
        self.assertEqual(checks['expect_original'], [{'step': 4, 'expr': 'n', 'ours': 3, 'original': 4, 'ok': False}])
        self.assertEqual(len(reasons), 5)
        # The clock masked (in screen space; relative to the crop for the comparison): the shot passes.
        checks, reasons = e2e.differential(spec, self.result(masks=[[550, 320, 120, 20]]), reference)
        self.assertEqual(checks['shot']['panel']['fraction'], 0.0)
        self.assertTrue(checks['shot']['panel']['ok'])

    def test_ignored_fields_and_screen_positions(self):
        """ignore_fields drops NAME=VALUE members, and the geometry's world and screen parts (a
        panned view's content), keeping the element's own rect."""
        a = {'/C#0/T#0': {'': 'rect 0,0,100,20 world 500,0,600,20 screen 500,700,100,20',
                          'Image': 'color=1,1,1,1 depth=3 raycastTarget=True'}}
        b = {'/C#0/T#0': {'': 'rect 0,0,104,20 world 517,23,621,43 screen 517,677,104,20',
                          'Image': 'color=1,1,1,1 depth=9 raycastTarget=True'}}
        stripped = e2e.without_fields(a, ['depth', 'world', 'screen'])['/C#0/T#0']
        self.assertEqual(stripped, {'': 'rect 0,0,100,20', 'Image': 'color=1,1,1,1 raycastTarget=True'})
        self.assertEqual(e2e.without_fields(b, ['depth', 'world', 'screen'])['/C#0/T#0'][''], 'rect 0,0,104,20')
        self.assertEqual(e2e.without_fields(a, ['depth'])['/C#0/T#0'][''], a['/C#0/T#0'][''])

    def test_blessed_reference_round_trip(self):
        spec = {'name': 'unit-bless', 'require_log': ['PLib options saved']}
        refs = e2e.REFS
        e2e.REFS = os.path.join(self.temp.name, 'refs')
        try:
            folder = e2e.bless(spec, {'out': self.orig, 'signatures': {'shared'}, 'log': os.path.join(self.orig, 'player.log'),
                                      'values': [{'step': 4, 'expr': 'n', 'value': 3}], 'run': '20261022-120000-abcdef'})
            self.assertEqual(sorted(os.listdir(folder)), ['log.json', 'panel.png', 'panel.shot.json', 'panel.ui.txt',
                                                          'signatures.json', 'source.json', 'values.json'])
            reference = e2e.Reference.blessed(spec)
            self.assertEqual((reference.signatures, reference.log_found, reference.values, reference.run),
                             ({'shared'}, {'PLib options saved': True}, {'4:n': 3}, '20261022-120000-abcdef'))
            # A second blessing replaces the first whole, and leaves nothing beside it.
            e2e.bless(spec, {'out': self.orig, 'signatures': set(), 'log': None, 'values': [], 'run': 'second'})
            self.assertEqual(e2e.Reference.blessed(spec).run, 'second')
            self.assertEqual(os.listdir(e2e.REFS), ['unit-bless'])
            checks, reasons = e2e.differential(spec, self.result(), reference)
            self.assertEqual(checks['expect_original'][0]['ok'], True)
        finally:
            e2e.REFS = refs


if __name__ == '__main__':
    unittest.main()


class InstallMismatch(unittest.TestCase):
    """e2e.install_mismatch: both players need one game build with the same DLC content."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.temp.cleanup()

    def install(self, name, assembly=b'build-1', dlc=('expansion1', 'dlc2')):
        folder = os.path.join(self.temp.name, name)
        os.makedirs(os.path.join(folder, 'OxygenNotIncluded_Data', 'Managed'))
        open(os.path.join(folder, 'OxygenNotIncluded_Data', 'Managed', 'Assembly-CSharp.dll'), 'wb').write(assembly)
        for d in dlc:
            os.makedirs(os.path.join(folder, 'OxygenNotIncluded_Data', 'StreamingAssets', 'dlc', d))
        return folder

    def test_same_install_passes(self):
        self.assertIsNone(e2e.install_mismatch(self.install('ours-761654'), self.install('orig-761654')))

    def test_missing_dlc_content_is_refused(self):
        why = e2e.install_mismatch(self.install('bare-761654', dlc=()), self.install('orig-761654'))
        self.assertIn('DLC content differs', why)
        self.assertIn('DLC content: none', why)

    def test_other_build_is_refused(self):
        why = e2e.install_mismatch(self.install('ours', assembly=b'build-2'), self.install('orig-761654'))
        self.assertIn('game builds differ', why)
        self.assertIn('build 761654', why)

    def test_unreadable_install_is_not_judged(self):
        os.makedirs(os.path.join(self.temp.name, 'empty'))
        self.assertIsNone(e2e.install_mismatch(os.path.join(self.temp.name, 'empty'), self.install('orig')))
