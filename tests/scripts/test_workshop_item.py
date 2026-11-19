"""scripts/workshop-item.py: a master release's assets (made up here, laid out as release.sh
makes them) become the Workshop item's folder: the release at the root with its mod_info.yaml,
moono.json, manifests and players, no archived_versions, nothing at the top the game would load
but the loader mod, and SHA256SUMS over every file. A tampered archive, one with a path outside
its folder, and a U60 beta build (-u60) are refused."""

import hashlib, io, json, os, subprocess, sys, tarfile, tempfile, unittest, zipfile

import helpers

SCRIPT = os.path.join(helpers.ROOT, 'scripts', 'workshop-item.py')


def read(path, mode='r'):
    with open(path, mode) as f:
        return f.read()


FILES = {
    'player/OniMoono.exe': b'MZ exe',
    'player/OniMoono.dll': b'MZ dll',
    'player/OniMoono': b'\x7fELF',
    'player/translations/zh.po': b'po',
    'setup.ps1': b'setup',
    'README.md': b'readme',
}


def release(folder, version, tamper=None, extra=None):
    os.makedirs(folder)
    manifest = ''.join(f'{hashlib.sha256(d).hexdigest()}  {p}\n' for p, d in sorted(FILES.items()))
    for plat in ('windows-x64', 'linux-x64'):
        base = os.path.join(folder, f'oni-moono-{version}-{plat}')
        with open(base + '.SHA256SUMS', 'w') as f:
            f.write(manifest)
        files = dict(FILES)
        if tamper and plat == 'linux-x64':
            files[tamper] = b'changed'
        if extra:
            files[extra] = b'x'
        if plat == 'windows-x64':
            with zipfile.ZipFile(base + '.zip', 'w') as z:
                for p, d in files.items():
                    z.writestr('oni-moono/' + p if not p.startswith('../') else p, d)
        else:
            with tarfile.open(base + '.tar.gz', 'w:gz') as t:
                for p, d in files.items():
                    info = tarfile.TarInfo('oni-moono/' + p)
                    info.size = len(d)
                    info.mode = 0o755 if p == 'player/OniMoono' else 0o644
                    t.addfile(info, io.BytesIO(d))


class WorkshopItemTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def run_script(self, assets='assets', loader='MoonoLoader.dll'):
        out = os.path.join(self.dir, 'item')
        with open(os.path.join(self.dir, loader), 'wb') as f:
            f.write(b'MZ loader')
        p = subprocess.run(
            [sys.executable, '-I', SCRIPT, '--assets', os.path.join(self.dir, assets), '--tag', 'v2026.11.08.02']
            + ['--loader', os.path.join(self.dir, loader), out],
            capture_output=True,
            text=True,
        )
        return p, out

    def check_sums(self, out):
        """SHA256SUMS lists every other file, with its digest."""
        lines = read(os.path.join(out, 'SHA256SUMS')).splitlines()
        listed = {l[66:]: l[:64] for l in lines}
        present = {os.path.relpath(os.path.join(d, f), out).replace(os.sep, '/') for d, _, fs in os.walk(out) for f in fs} - {'SHA256SUMS'}
        self.assertEqual(set(listed), present)
        for p_, h in listed.items():
            self.assertEqual(hashlib.sha256(read(os.path.join(out, p_), 'rb')).hexdigest(), h)
        return listed

    def test_layout(self):
        release(os.path.join(self.dir, 'assets'), '2026.11.08')
        p, out = self.run_script()
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertIn('minimumSupportedBuild: 763796\nAPIVersion: 2\n', read(os.path.join(out, 'mod_info.yaml')))
        meta = json.loads(read(os.path.join(out, 'moono.json')))
        self.assertEqual((meta['workshopItem'], meta['version'], meta['branch'], meta['tag']), ('oni-moono', '2026.11.08.02', 'master', 'v2026.11.08.02'))
        # One version: no archived version, and nothing of one in SHA256SUMS.
        self.assertEqual(
            sorted(os.listdir(out)),
            sorted(
                ['MoonoLoader.dll', 'OniMoono.exe', 'OniMoono.sh', 'README.md', 'SHA256SUMS', 'linux-x64', 'mod_info.yaml', 'moono.json', 'verify.ps1']
                + ['verify.sh', 'windows-x64', 'oni-moono-2026.11.08-linux-x64.SHA256SUMS', 'oni-moono-2026.11.08-windows-x64.SHA256SUMS']
            ),
        )
        self.assertFalse([p_ for p_ in self.check_sums(out) if p_.startswith('archived_versions/')])
        self.assertEqual(read(os.path.join(out, 'OniMoono.exe'), 'rb'), b'MZ exe')
        self.assertEqual(read(os.path.join(out, 'MoonoLoader.dll'), 'rb'), b'MZ loader')

        # The release manifests check the players as verify.sh checks them.
        subprocess.run(
            ['sha256sum', '--quiet', '--strict', '-c', os.path.join(out, 'oni-moono-2026.11.08-linux-x64.SHA256SUMS')],
            cwd=os.path.join(out, 'linux-x64'),
            check=True,
        )

    def test_tampered_archive_is_refused(self):
        release(os.path.join(self.dir, 'assets'), '2026.11.08', tamper='player/OniMoono.dll')
        p, _ = self.run_script()
        self.assertNotEqual(p.returncode, 0)
        self.assertIn('is not as', p.stderr)

    def test_paths_outside_are_refused(self):
        release(os.path.join(self.dir, 'assets'), '2026.11.08', extra='../evil')
        p, _ = self.run_script()
        self.assertNotEqual(p.returncode, 0)

    def test_loader_is_moonoloader(self):
        release(os.path.join(self.dir, 'assets'), '2026.11.08')
        p, _ = self.run_script(loader='Other.dll')
        self.assertIn('the loader is MoonoLoader.dll', p.stderr)

    def test_a_u60_beta_build_is_refused(self):
        release(os.path.join(self.dir, 'assets'), '2026.11.08-u60')
        p, _ = self.run_script()
        self.assertNotEqual(p.returncode, 0)
        self.assertIn('is a U60 beta build', p.stderr)


if __name__ == '__main__':
    unittest.main()
