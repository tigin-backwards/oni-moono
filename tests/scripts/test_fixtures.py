"""scripts/fixtures.py: the e2e fixture saves come by hash from a shared cache or a release
download, and nothing with another hash is kept or overwritten unnoticed; the committed
manifest lists every fixture a spec loads. The download is a fake here; gh is never run."""

import copy, glob, hashlib, json, os, re, stat, tempfile, unittest
from unittest import mock

import helpers

fixtures = helpers.script('fixtures.py')

SAVE = b'KSAV' + b'\1' * 100
SHA = hashlib.sha256(SAVE).hexdigest()
REPO = 'owner/refs'


def manifest(**more):
    entry = dict(
        description='a colony', game='base game', build='1', mods='none', used_by=['s'], sha256=SHA, size=len(SAVE), tag='saves/' + SHA[:16], asset='colony.sav'
    )
    m = {
        'schema': 1,
        'source': {'kind': 'github-release', 'host': 'github.com', 'repository': REPO},
        'fixtures': {'colony.sav': dict(entry, aliases=['old-name.sav'])},
    }
    m['fixtures'].update(more)
    return m


class Fetch(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = os.path.join(self.tmp.name, 'fixtures')
        self.cache = os.path.join(self.tmp.name, 'cache')
        os.makedirs(self.dir)
        env = mock.patch.dict(os.environ, {'E2E_FIXTURE_CACHE': self.cache})
        env.start()
        self.addCleanup(env.stop)
        self.addCleanup(self.tmp.cleanup)
        self.downloads = []
        self.lines = []

    def download(self, repository, tag, asset, folder, data=SAVE):
        self.downloads.append((repository, tag, asset))
        with open(os.path.join(folder, asset), 'wb') as f:
            f.write(data)
        return os.path.join(folder, asset)

    def ensure(self, names, m=None, **kw):
        return fixtures.ensure(
            names,
            fixtures=self.dir,
            manifest=m or manifest(),
            download=kw.pop('download', self.download),
            log=self.lines.append,
            offline=kw.pop('offline', False),
        )

    def read(self, name):
        with open(os.path.join(self.dir, name), 'rb') as f:
            return f.read()

    def test_a_missing_fixture_is_downloaded_once_and_copied_from_the_cache(self):
        self.assertEqual(self.ensure(['colony']), {})
        self.assertEqual(self.downloads, [(REPO, 'saves/' + SHA[:16], 'colony.sav')])
        self.assertEqual(self.read('colony.sav'), SAVE)
        cached = os.path.join(self.cache, SHA)
        self.assertFalse(os.stat(cached).st_mode & (stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH))
        # The worktree's copy is writable (the players' copies of it are saved over).
        self.assertTrue(os.stat(os.path.join(self.dir, 'colony.sav')).st_mode & stat.S_IWUSR)
        # Fetched again: nothing to do, from its stamp.
        with mock.patch.object(fixtures, 'sha256_file', side_effect=AssertionError('read again')):
            self.assertEqual(self.ensure(['colony']), {})
        # Another worktree: the cache serves it.
        other = os.path.join(self.tmp.name, 'other')
        self.assertEqual(fixtures.ensure(['colony.sav'], fixtures=other, manifest=manifest(), download=self.download, log=self.lines.append, offline=True), {})
        self.assertEqual(len(self.downloads), 1)

    def test_an_alias_is_placed_beside_its_fixture(self):
        self.assertEqual(self.ensure(['old-name']), {})
        self.assertEqual(self.read('old-name.sav'), SAVE)
        self.assertEqual(self.read('colony.sav'), SAVE)

    def test_an_old_name_with_the_hash_serves_without_a_download(self):
        with open(os.path.join(self.dir, 'old-name.sav'), 'wb') as f:
            f.write(SAVE)
        self.assertEqual(self.ensure(['colony'], offline=True), {})
        self.assertEqual(self.read('colony.sav'), SAVE)
        self.assertEqual(self.downloads, [])
        self.assertTrue(os.path.exists(os.path.join(self.cache, SHA)))

    def test_a_download_with_another_hash_is_not_kept(self):
        problems = self.ensure(['colony'], download=lambda *a: self.download(*a, data=b'other'))
        self.assertIn('the download has sha256', problems['colony.sav'])
        self.assertIn('release saves/' + SHA[:16], problems['colony.sav'])
        self.assertEqual(os.listdir(self.cache), [])
        self.assertFalse(os.path.exists(os.path.join(self.dir, 'colony.sav')))

    def test_a_fixture_changed_in_place_is_hashed_again(self):
        self.assertEqual(self.ensure(['colony']), {})
        path = os.path.join(self.dir, 'colony.sav')
        with open(path, 'ab') as f:
            f.write(b'appended')
        self.assertIn('colony.sav', fixtures.check(['colony'], fixtures=self.dir, manifest=manifest()))
        self.assertEqual(self.ensure(['colony']), {})
        self.assertEqual(self.read('colony.sav'), SAVE)

    def test_a_fixture_with_another_hash_is_moved_aside(self):
        with open(os.path.join(self.dir, 'colony.sav'), 'wb') as f:
            f.write(b'stale')
        self.assertEqual(self.ensure(['colony']), {})
        self.assertEqual(self.read('colony.sav'), SAVE)
        aside = glob.glob(os.path.join(self.dir, '.replaced', 'colony.sav.*'))
        self.assertEqual(len(aside), 1)
        with open(aside[0], 'rb') as f:
            self.assertEqual(f.read(), b'stale')

    def test_a_local_bless_is_kept(self):
        path = os.path.join(self.dir, 'colony.sav')
        with open(path, 'wb') as f:
            f.write(b'blessed here')
        fixtures.record_local_bless('colony.sav', path, self.dir)
        self.assertEqual(self.ensure(['colony']), {})
        self.assertEqual(self.read('colony.sav'), b'blessed here')
        self.assertEqual(self.downloads, [])
        self.assertTrue(any('local bless' in line for line in self.lines))

    def test_failures_say_what_is_missing_and_how_to_get_it(self):
        def fails(*a):
            raise fixtures.FixtureError('gh release download failed: HTTP 404')

        m = manifest(
            **{'made.sav': dict(description='d', game='g', build='b', mods='m', used_by=[], not_stored='scripts/e2e.py --player original --bless maker')}
        )
        problems = self.ensure(['colony', 'made', 'unknown'], m, download=fails)
        self.assertRegex(
            problems['colony.sav'], r'^no fixture colony\.sav: gh release download failed: HTTP 404; .*private repository owner/refs.*gh auth login'
        )
        self.assertIn('--bless maker', problems['made.sav'])
        self.assertIn('not in tests/e2e/fixtures.json', problems['unknown.sav'])
        offline = self.ensure(['colony'], offline=True)
        self.assertIn('E2E_FIXTURES_OFFLINE', offline['colony.sav'])

    def test_bless_uploads_and_writes_the_entry(self):
        path = os.path.join(self.tmp.name, 'fixtures.json')
        with open(path, 'w') as f:
            json.dump(manifest(), f)
        new = os.path.join(self.tmp.name, 'new.sav')
        with open(new, 'wb') as f:
            f.write(b'new save')
        uploads = []
        upload = lambda repo, tag, src, asset, notes: uploads.append((repo, tag, asset)) or True  # noqa: E731
        with self.assertRaisesRegex(fixtures.FixtureError, 'give --description'):
            fixtures.bless(new, manifest_path=path, upload=upload, log=self.lines.append)
        fields = dict(description='a new colony', game='base game', build='1', mods='none', used_by=['spec'])
        fixtures.bless(new, fields=fields, manifest_path=path, upload=upload, log=self.lines.append)
        digest = hashlib.sha256(b'new save').hexdigest()
        self.assertEqual(uploads, [(REPO, 'saves/' + digest[:16], 'new.sav')])
        with open(path) as f:
            entry = json.load(f)['fixtures']['new.sav']
        self.assertEqual((entry['sha256'], entry['tag'], entry['description']), (digest, 'saves/' + digest[:16], 'a new colony'))
        # Re-blessed under an old name: a new asset, the entry's description and aliases kept.
        with open(new, 'wb') as f:
            f.write(b'reblessed')
        fixtures.bless(new, name='old-name', manifest_path=path, upload=upload, log=self.lines.append)
        with open(path) as f:
            entry = json.load(f)['fixtures']['colony.sav']
        self.assertEqual((entry['sha256'], entry['aliases'], entry['description']), (hashlib.sha256(b'reblessed').hexdigest(), ['old-name.sav'], 'a colony'))


class Manifest(unittest.TestCase):
    def test_bad_entries_are_refused(self):
        for change in (
            lambda e: e.update(sha256='ABC'),
            lambda e: e.update(tag='saves/0000000000000000'),
            lambda e: e.update(asset='../x.sav'),
            lambda e: e.pop('description'),
        ):
            m = copy.deepcopy(manifest())
            change(m['fixtures']['colony.sav'])
            with self.assertRaises(fixtures.FixtureError):
                fixtures.check_manifest(m)
        m = manifest()
        m['source']['host'] = 'example.com'
        with self.assertRaises(fixtures.FixtureError):
            fixtures.check_manifest(m)

    def test_the_committed_manifest_lists_every_fixture_a_spec_loads(self):
        m = fixtures.load_manifest()
        specs = os.path.join(helpers.ROOT, 'tests', 'e2e')
        wanted = set()
        for path in glob.glob(os.path.join(specs, '**', '*.yaml'), recursive=True):
            with open(path, encoding='utf-8') as f:
                text = f.read()
            found = re.search(r'^from:\s*([\w.-]+)', text, re.M)
            if found and found.group(1) != 'main-menu':
                wanted.add(found.group(1))
            extra = re.search(r'^extra_saves:\s*\[(.*)\]', text, re.M)
            if extra:
                wanted |= {n.strip() for n in extra.group(1).split(',') if n.strip()}
        missing = sorted(n for n in wanted if fixtures.lookup(m, n)[1] is None)
        self.assertEqual(missing, [], 'add them to tests/e2e/fixtures.json (scripts/fixtures.py bless)')


if __name__ == '__main__':
    unittest.main()
