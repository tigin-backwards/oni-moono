"""scripts/alloc-compare.py: a run's span from its PERF alloc and PERF sites lines, the sections
with Moono's other threads as the counter's remainder, and buckets grouped by kind."""

import os, tempfile, unittest

import helpers

ac = helpers.script('alloc-compare.py')


def alloc(f, n, rt, gt, mono):
    return f'PERF alloc f{f} n {n} rt {rt} gt {gt} mono {mono} gcno 1 fullms -1 hg 0 hgf 0 gcf 0 used 0 heap 0 rec 0 recf 0 recn 0 dnpause 10 dc 1/0/0\n'


class AllocCompareTests(unittest.TestCase):
    def run_dir(self, lines, win0, win1):
        d = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__('shutil').rmtree(d))
        with open(os.path.join(d, 'stdout.txt'), 'w') as f:
            f.writelines(lines)
        for name, value in (('win0', win0), ('win1', win1)):
            with open(os.path.join(d, name), 'w') as f:
                f.write(f'{value}\n')
        return d

    def test_rates_and_sections(self):
        d = self.run_dir(
            [
                'PERF alloc calibrate objects 24.00 B each\n',
                alloc(600, 600, 10.0, 20.0, 1_000_000),
                'PERF sites f600 (other)=100000/0 | Game.Update=200000/600 | bucket Work(Pickup)=50000/3000\n',
                alloc(1200, 1200, 20.0, 40.0, 3_000_000),
                'PERF sites f1200 (other)=500000/0 | Game.Update=900000/1200 | bucket Work(Pickup)=150000/6000 | bucket SapTree.root.alive=10000/5\n',
            ],
            1,
            2,
        )
        r = ac.rates(d)
        self.assertEqual(r['frames'], 600)
        self.assertAlmostEqual(r['kb_frame'], 2_000_000 / 600 / 1000)
        self.assertAlmostEqual(r['mb_gs'], 0.1)
        self.assertEqual(r['gen012'], '0/0/0')
        secs, _ = ac.sections(d)
        self.assertEqual(secs['Game.Update'], (700000, 600))
        self.assertEqual(secs['bucket SapTree.root.alive'], (10000, 5))
        # 2,000,000 counted in all; the main thread's sections hold 1,210,000.
        self.assertEqual(secs['(other threads)'][0], 790000)

    def test_groups(self):
        self.assertEqual(ac.group('bucket SapTree.root.alive.normal.oozing'), 'SapTree states')
        self.assertEqual(ac.group('bucket HighEnergyParticle+States.root.ready'), 'HighEnergyParticle states')
        self.assertEqual(ac.group('bucket Work(Pickup)'), 'Work(Pickup)')
        self.assertEqual(ac.group('Game.Update'), 'Game.Update')

    def test_trace_sections(self):
        self.assertEqual(ac.section_of(['System.Private.CoreLib!List.Grow(int)', 'Assembly-CSharp!Game.Update_Patch1(Game)']), 'Game.Update')
        self.assertEqual(ac.section_of(['Assembly-CSharp!StateMachineUpdater+BucketGroup.AdvanceOneSubTick(float)']), 'buckets')
        self.assertEqual(ac.section_of(['OniMoono.UnityEngine!X.Y()']), '(other)')


if __name__ == '__main__':
    unittest.main()
