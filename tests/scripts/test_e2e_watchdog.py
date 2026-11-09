"""The e2e player's hang watchdog (e2e.Watchdog), with a fake player and clock."""

import os, tempfile, unittest

import helpers  # noqa: F401
import e2e


class FakePlayer:
    def __init__(self, out):
        self.out, self.log, self.killed = out, os.path.join(out, 'stdout.txt'), False
        open(self.log, 'w').close()

    def alive(self):
        return not self.killed

    def game_pid(self):
        return 4242

    def kill(self):
        self.killed = True


class WatchdogTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.player = FakePlayer(self.dir.name)
        self.now = 1000.0
        self.dumps = []
        self.dog = e2e.Watchdog(self.player, minutes=5, clock=lambda: self.now, dump=lambda pid, folder: self.dumps.append((pid, folder)) or 'main thread busy')

    def tearDown(self):
        self.dir.cleanup()

    def wait(self, seconds):
        self.now += seconds
        return self.dog.check()

    def grow_log(self):
        with open(self.player.log, 'a') as f:
            f.write('a line\n')

    def test_log_growth_and_replies_are_progress(self):
        self.assertFalse(self.wait(240))
        self.grow_log()
        self.assertFalse(self.wait(240))
        self.dog.progress()  # an OniMcp reply
        self.assertFalse(self.wait(240))
        self.assertFalse(self.player.killed)

    def test_silence_is_a_hang_dumped_before_the_kill(self):
        self.assertFalse(self.wait(200))
        self.assertTrue(self.wait(200))
        self.assertEqual(self.dumps, [(4242, os.path.join(self.dir.name, 'hang'))])
        self.assertTrue(self.player.killed)
        self.assertIn('the player hung (no log output or OniMcp/E2eDriver reply for 7 min)', self.dog.hung)
        self.assertIn('main thread busy', self.dog.hung)
        self.assertTrue(self.wait(600))
        self.assertEqual(len(self.dumps), 1)  # once

    def test_a_held_player_is_not_hung(self):
        self.dog.paused = True
        self.assertFalse(self.wait(3600))
        self.assertFalse(self.player.killed)


if __name__ == '__main__':
    unittest.main()
