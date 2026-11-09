"""scripts/workshop-item.py: two releases' assets (made up here, laid out as release.sh makes
them) become the Workshop item's folder: the U60 build at the root, U59 in
archived_versions/u59, each with its mod_info.yaml, moono.json, manifests and players, nothing
at a version's top the game would load, and SHA256SUMS over every file. A tampered archive or
one with a path outside its folder is refused."""
import hashlib, io, json, os, subprocess, sys, tarfile, tempfile, unittest, zipfile

import helpers

SCRIPT = os.path.join(helpers.ROOT, 'scripts', 'workshop-item.py')


def read(path, mode='r'):
    with open(path, mode) as f:
        return f.read()

FILES = {'player/OniMoono.exe': b'MZ exe', 'player/OniMoono.dll': b'MZ dll', 'player/OniMoono': b'\x7fELF',
         'player/translations/zh.po': b'po', 'setup.ps1': b'setup', 'README.md': b'readme'}


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

    def run_script(self, u60='u60', master='master'):
        out = os.path.join(self.dir, 'item')
        p = subprocess.run([sys.executable, '-I', SCRIPT, '--u60', os.path.join(self.dir, u60), '--u60-tag', 'v2026.11.08.02',
                            '--master', os.path.join(self.dir, master), '--master-tag', 'v2026.11.08.01', out],
                           capture_output=True, text=True)
        return p, out

    def test_layout(self):
        release(os.path.join(self.dir, 'u60'), '2026.11.08-u60')
        release(os.path.join(self.dir, 'master'), '2026.11.08')
        p, out = self.run_script()
        self.assertEqual(p.returncode, 0, p.stderr)
        root_info = read(os.path.join(out, 'mod_info.yaml'))
        self.assertIn('minimumSupportedBuild: 762230\nAPIVersion: 2\n', root_info)
        self.assertIn('minimumSupportedBuild: 744825\n', read(os.path.join(out, 'archived_versions', 'u59', 'mod_info.yaml')))
        meta = json.loads(read(os.path.join(out, 'moono.json')))
        self.assertEqual((meta['workshopItem'], meta['version']), ('oni-moono', '2026.11.08.02-u60'))
        self.assertEqual(json.loads(read(os.path.join(out, 'archived_versions', 'u59', 'moono.json')))['version'], '2026.11.08.01')
        self.assertEqual(read(os.path.join(out, 'OniMoono.exe'), 'rb'), b'MZ exe')
        self.assertTrue(os.access(os.path.join(out, 'linux-x64', 'player', 'OniMoono'), os.X_OK))
        for top in (out, os.path.join(out, 'archived_versions', 'u59')):
            self.assertFalse([n for n in os.listdir(top) if n.lower().endswith(('.dll', '.po'))])
        # SHA256SUMS lists every other file, with its digest.
        lines = read(os.path.join(out, 'SHA256SUMS')).splitlines()
        listed = {l[66:]: l[:64] for l in lines}
        present = {os.path.relpath(os.path.join(d, f), out).replace(os.sep, '/') for d, _, fs in os.walk(out) for f in fs} - {'SHA256SUMS'}
        self.assertEqual(set(listed), present)
        for p_, h in listed.items():
            self.assertEqual(hashlib.sha256(read(os.path.join(out, p_), 'rb')).hexdigest(), h)
        # The release manifests check the players as verify.sh checks them.
        subprocess.run(['sha256sum', '--quiet', '--strict', '-c', os.path.join(out, 'oni-moono-2026.11.08-u60-linux-x64.SHA256SUMS')],
                       cwd=os.path.join(out, 'linux-x64'), check=True)

    def test_tampered_archive_is_refused(self):
        release(os.path.join(self.dir, 'u60'), '2026.11.08-u60', tamper='player/OniMoono.dll')
        release(os.path.join(self.dir, 'master'), '2026.11.08')
        p, _ = self.run_script()
        self.assertNotEqual(p.returncode, 0)
        self.assertIn('is not as', p.stderr)

    def test_paths_outside_are_refused(self):
        release(os.path.join(self.dir, 'u60'), '2026.11.08-u60', extra='../evil')
        release(os.path.join(self.dir, 'master'), '2026.11.08')
        p, _ = self.run_script()
        self.assertNotEqual(p.returncode, 0)

    def test_branches_must_match(self):
        release(os.path.join(self.dir, 'u60'), '2026.11.08')
        release(os.path.join(self.dir, 'master'), '2026.11.08')
        p, _ = self.run_script()
        self.assertIn('is not a u60 build', p.stderr)


if __name__ == '__main__':
    unittest.main()
