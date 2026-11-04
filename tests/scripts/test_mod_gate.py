"""scripts/mod-gate.py: a mod passes only when onimaxxing reviewed the exact upload SteamCMD
fetched and the unpacked copy is that upload; and ws-mods.py profile installs nothing it
skips. onimaxxing's CDN is a fake here; nothing is fetched."""
import hashlib, json, os, tempfile, time, unittest
from unittest import mock

import helpers

gate = helpers.script('mod-gate.py')
ws_mods = helpers.script('ws-mods.py')

MID = '3823197258'
HANDLE = 16165179261256059114          # as onimaxxing gives it
SIGNED = HANDLE - (1 << 64)            # as SteamCMD's acf records it
DLL = b'MZ' + b'\0' * 62 + b'mod code'
SHA = hashlib.sha256(DLL).hexdigest()


def acf(handles):
    items = ''.join(f'\t\t"{mid}"\n\t\t{{\n\t\t\t"size"\t\t"1"\n\t\t\t"timeupdated"\t\t"1"\n\t\t\t"manifest"\t\t"-1"\n'
                    f'\t\t\t"ugchandle"\t\t"{h}"\n\t\t}}\n' for mid, h in handles.items())
    return f'"AppWorkshop"\n{{\n\t"WorkshopItemsInstalled"\n\t{{\n{items}\t}}\n}}\n'


class Fake:
    """onimaxxing's CDN: {path under the build: JSON value}; others are 404."""

    def __init__(self, files):
        self.files, self.urls = files, []

    def __call__(self, url):
        self.urls.append(url)
        rel = url.split('/mods-data/761015/', 1)[1]
        return json.dumps(self.files[rel]).encode() if rel in self.files else None


def onimaxxing(analysis=None, versions=None, facts=None):
    analysis = {'malware': False, 'credentials': False, 'chatWebhook': False, 'obfuscated': False, **(analysis or {})}
    files = {'catalog.json': {'format': 2, 'count': 1, 'cols': {'id': [MID], 'analysis': [analysis]}},
             f'mod/{MID}/index.json': {'id': MID, 'current': str(HANDLE),
                                       'versions': versions if versions is not None else
                                       [{'file': str(HANDLE), 'version': '1.1.1', 'reviewed': True}]}}
    files[f'mod/{MID}/{HANDLE}.json'] = facts if facts is not None else \
        {'id': MID, 'hcontentFile': str(HANDLE), 'managed': [{'path': 'DirectOrders.dll', 'sha256': SHA}]}
    return files


class Gate(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.ws = os.path.join(self.temp.name, 'ws')
        self.acf = os.path.join(self.temp.name, 'appworkshop_457140.acf')
        self.mod(MID, {'DirectOrders.dll': DLL, 'mod.yaml': b'title: Direct Orders\n'})
        with open(self.acf, 'w') as f:
            f.write(acf({MID: SIGNED}))

    def tearDown(self):
        self.temp.cleanup()

    def mod(self, mid, files):
        for name, data in files.items():
            path = os.path.join(self.ws, mid, name)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'wb') as f:
                f.write(data)

    def run_gate(self, files, ids=(MID,)):
        # A fresh cache for each run: a cached copy would stand in for FILES.
        self.fake, self.cache = Fake(files), tempfile.mkdtemp(dir=self.temp.name)
        oni = gate.Onimaxxing(761015, cache=self.cache, fetch=self.fake)
        return gate.gate(list(ids), ws=self.ws, acf=self.acf, oni=oni)

    def test_reviewed_upload_with_its_dlls_passes(self):
        result = self.run_gate(onimaxxing())[MID]
        self.assertEqual(result['verdict'], 'pass', result)
        self.assertIn(str(HANDLE), result['reason'])

    def test_signed_acf_handle_is_taken_mod_2_64(self):
        self.assertEqual(gate.upload_handles(self.acf), {MID: str(HANDLE)})

    def test_another_upload_is_skipped(self):
        files = onimaxxing(versions=[{'file': '9422738633831198253', 'version': '1.0.0', 'reviewed': True}])
        result = self.run_gate(files)[MID]
        self.assertEqual(result['verdict'], 'skip')
        self.assertIn('not reviewed by onimaxxing yet', result['reason'])

    def test_analysed_but_unreviewed_upload_is_skipped(self):
        files = onimaxxing(versions=[{'file': str(HANDLE), 'version': '1.1.1', 'reviewed': False}])
        result = self.run_gate(files)[MID]
        self.assertEqual(result['verdict'], 'skip')
        self.assertIn('analysed, not reviewed', result['reason'])

    def test_unpacked_copy_of_another_upload_is_skipped(self):
        self.mod(MID, {'DirectOrders.dll': DLL + b'changed'})
        result = self.run_gate(onimaxxing())[MID]
        self.assertEqual(result['verdict'], 'skip')
        self.assertIn('DirectOrders.dll differ', result['reason'])

    def test_dlls_match_by_hash_not_by_path(self):
        # A non-ASCII name the zip gave in another encoding, and a library onimaxxing lists without a hash.
        os.remove(os.path.join(self.ws, MID, 'DirectOrders.dll'))
        self.mod(MID, {'╦╖╧.dll': DLL, 'PLib.dll': b'MZ library'})
        facts = {'id': MID, 'hcontentFile': str(HANDLE),
                 'managed': [{'path': '大一统.dll', 'sha256': SHA}, {'path': 'PLib.dll'}]}
        self.assertEqual(self.run_gate(onimaxxing(facts=facts))[MID]['verdict'], 'pass')

    def test_catalog_flags_skip(self):
        for flag in gate.FLAGS:
            with self.subTest(flag=flag):
                result = self.run_gate(onimaxxing(analysis={flag: True}))[MID]
                self.assertEqual(result['verdict'], 'skip')
                self.assertIn(flag, result['reason'])

    def test_no_handle_is_skipped(self):
        with open(self.acf, 'w') as f:
            f.write(acf({}))
        self.assertIn('no upload handle', self.run_gate(onimaxxing())[MID]['reason'])

    def test_mod_without_binaries_passes_unless_flagged(self):
        self.mod('1135041189', {'strings.po': b'msgid ""\n', 'mod_info.yaml': b'x: 1\n'})
        self.assertEqual(self.run_gate(onimaxxing(), ['1135041189'])['1135041189']['verdict'], 'pass')
        # A binary is one by its bytes too, whatever its name.
        self.mod('1135041189', {'data.bin': b'\x7fELF payload'})
        self.assertEqual(self.run_gate(onimaxxing(), ['1135041189'])['1135041189']['verdict'], 'skip')

    def test_github_builds_pass_only_as_a_known_build(self):
        mid, files = '9000000001', {'mod.yaml': b'title: Fast Track\n', 'FastTrack.dll': b'MZ fast track'}
        self.mod(mid, files)
        known = {'FastTrack.dll': hashlib.sha256(files['FastTrack.dll']).hexdigest()}
        with mock.patch.dict(gate.GITHUB_BUILDS, {mid: ('Fast Track', {'0.18.6.0': known})}):
            self.assertEqual(self.run_gate(onimaxxing(), [mid])[mid]['verdict'], 'pass')
            self.mod(mid, {'Extra.dll': b'MZ another'})                # a binary the build lacks
            self.assertIn('not a known GitHub build', self.run_gate(onimaxxing(), [mid])[mid]['reason'])
            os.remove(os.path.join(self.ws, mid, 'Extra.dll'))
            self.mod(mid, {'FastTrack.dll': b'MZ changed'})
            self.assertEqual(self.run_gate(onimaxxing(), [mid])[mid]['verdict'], 'skip')
        # Another local id gets no exception.
        self.mod('9000000002', files)
        self.assertEqual(self.run_gate(onimaxxing(), ['9000000002'])['9000000002']['verdict'], 'skip')

    def test_not_downloaded_and_bad_ids_are_skipped(self):
        results = self.run_gate(onimaxxing(), ['1', '../x'])
        self.assertEqual(results['1']['reason'], 'not downloaded')
        self.assertEqual(results['../x']['reason'], 'not a Workshop id')

    def test_data_is_cached_with_its_fetch_time(self):
        self.run_gate(onimaxxing())
        fetched = len(self.fake.urls)
        oni = gate.Onimaxxing(761015, cache=self.cache, fetch=self.fake)
        gate.gate([MID], ws=self.ws, acf=self.acf, oni=oni)
        self.assertEqual(len(self.fake.urls), fetched)  # all from the cache
        with open(os.path.join(self.cache, 'catalog.json.meta')) as f:
            self.assertLessEqual(time.time() - json.load(f)['fetchedAt'], 60)
        with self.assertRaises(ValueError):
            oni.get('../../etc/passwd')

    def test_unreachable_onimaxxing_skips(self):
        def down(url):
            raise ConnectionError('down')
        oni = gate.Onimaxxing(761015, cache=os.path.join(self.temp.name, 'cache2'), fetch=down)
        result = gate.gate([MID], ws=self.ws, acf=self.acf, oni=oni)[MID]
        self.assertEqual(result['verdict'], 'skip')

    def test_build_from_game_folder(self):
        with mock.patch.dict(os.environ, {'ONI_GAME_BUILD': '', 'ONI_GAME_DIR': '/x/oni-linux-761015'}):
            self.assertEqual(gate.game_build(), 761015)
        with mock.patch.dict(os.environ, {'ONI_GAME_BUILD': '', 'ONI_GAME_DIR': '/x/oni-linux'}):
            self.assertEqual(gate.game_build(), gate.U59_BUILD)


class Profile(unittest.TestCase):
    def test_profile_installs_nothing_the_gate_skips(self):
        with tempfile.TemporaryDirectory() as temp:
            with mock.patch.object(ws_mods, 'WS', os.path.join(temp, 'ws')), \
                    mock.patch.object(ws_mods, 'unpack', lambda i: None), \
                    mock.patch.object(ws_mods, 'gate', lambda ids: {i: {'id': i, 'verdict': 'skip', 'reason': 'not reviewed'}
                                                                    for i in ids}):
                with self.assertRaises(ws_mods.GateSkipped) as caught:
                    ws_mods.profile(os.path.join(temp, 'mods'), ['3823197258'])
            self.assertIn('3823197258: not reviewed', str(caught.exception))
            self.assertFalse(os.path.exists(os.path.join(temp, 'mods')))


if __name__ == '__main__':
    unittest.main()
