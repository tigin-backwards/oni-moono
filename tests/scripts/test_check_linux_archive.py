"""check-linux-archive.py on archives built here, and the repository's launcher oni-moono.desktop
run as a desktop would run it: its Exec read by the specification's rules, from a folder with
spaces in its path, without a terminal (stdin /dev/null, output to a pipe, its own session)."""

import io, os, subprocess, tarfile, tempfile, unittest

import helpers

check = helpers.script('check-linux-archive.py')
LAUNCHER = os.path.join(helpers.ROOT, 'oni-moono.desktop')


def archive(path, skip=(), modes=None):
    """A tar.gz of the release's Linux layout; `skip`: files left out, `modes`: {name: mode}."""
    modes = modes or {}
    with open(LAUNCHER, 'rb') as f:
        launcher = f.read()
    with tarfile.open(path, 'w:gz') as tar:
        for name in check.EXECUTABLE + check.PRESENT:
            if name in skip:
                continue
            data = launcher if name == 'oni-moono.desktop' else b'x'
            info = tarfile.TarInfo(f'oni-moono/{name}')
            info.size, info.mode = len(data), modes.get(name, 0o755 if name in check.EXECUTABLE else 0o644)
            tar.addfile(info, io.BytesIO(data))


class ArchiveTest(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.dir.name, 'a.tar.gz')

    def tearDown(self):
        self.dir.cleanup()

    def test_release_layout_passes(self):
        archive(self.path)
        self.assertEqual(check.check(self.path), [])

    def test_launcher_without_executable_bit_fails(self):
        archive(self.path, modes={'oni-moono.desktop': 0o644})
        self.assertEqual(check.check(self.path), ['oni-moono/oni-moono.desktop is not executable (mode 644)'])

    def test_missing_launcher_and_icon_fail(self):
        archive(self.path, skip=('oni-moono.desktop', 'player/Moo.png'))
        self.assertEqual(check.check(self.path), ['oni-moono/oni-moono.desktop missing', 'oni-moono/player/Moo.png missing'])


class LauncherTest(unittest.TestCase):
    def test_entry_is_a_terminal_free_application_using_its_own_path(self):
        with open(LAUNCHER) as f:
            self.assertEqual(check.launcher_problems(f.read()), [])
        self.assertIn("doesn't use %k", check.launcher_problems('[Desktop Entry]\nType=Application\nName=a\nExec=player/OniMoono\nTerminal=false\n')[0])

    def test_exec_quoting_follows_the_specification(self):
        self.assertEqual(check.exec_argv('sh -c "a \\"b\\" \\$c" x %k %f 100%%', '/p q/l.desktop'), ['sh', '-c', 'a "b" $c', 'x', '/p q/l.desktop', '100%'])
        self.assertEqual(check.desktop_entry('[Desktop Entry]\nExec=a\\\\$b\n')['Exec'], 'a\\$b')
        with self.assertRaises(ValueError):
            check.desktop_entry('[Desktop Entry]\nExec=a\\$b\n')

    def run_launcher(self, player):
        """Runs the launcher's Exec from `folder/oni-moono.desktop` with a made-up OniMoono at
        `folder/player`, which records how it ran."""
        with tempfile.TemporaryDirectory() as tmp:
            folder = os.path.join(tmp, 'Games and things', 'oni-moono')
            os.makedirs(os.path.join(folder, player))
            ran = os.path.join(tmp, 'ran.txt')
            fake = os.path.join(folder, player, 'OniMoono')
            with open(fake, 'w') as f:
                f.write(f'#!/bin/sh\n{{ echo "$0"; [ -t 0 ] && echo tty || echo no-tty; echo "$#"; }} > "{ran}"\n')
            os.chmod(fake, 0o755)
            entry = os.path.join(folder, 'oni-moono.desktop')
            with open(LAUNCHER) as f:
                text = f.read()
            with open(entry, 'w') as f:
                f.write(text)
            argv = check.exec_argv(check.desktop_entry(text)['Exec'], entry)
            subprocess.run(
                argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True, cwd='/', check=True, timeout=30
            )
            with open(ran) as f:
                return f.read().split('\n')[:3], fake

    def test_runs_the_player_beside_it_without_a_terminal(self):
        (path, tty, count), fake = self.run_launcher('player')
        self.assertEqual((path, tty, count), (fake, 'no-tty', '0'))

    def test_runs_a_clones_out_player(self):
        (path, _, _), fake = self.run_launcher(os.path.join('out', 'player'))
        self.assertEqual(path, fake)


if __name__ == '__main__':
    unittest.main()
