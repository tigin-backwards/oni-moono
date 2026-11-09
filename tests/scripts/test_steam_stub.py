"""The Steam test double (tools/SteamStub): gen.py's DLC options, and the Workshop part (ugc.s,
--ugc) loaded with ctypes and called as Steamworks.NET calls it, in a subprocess so that its
environment is the one under test."""

import ctypes, os, subprocess, sys, tempfile, textwrap, unittest

import helpers

STUB = os.path.join(helpers.ROOT, 'tools', 'SteamStub')
NATIVE = os.path.join(helpers.ROOT, 'src', 'ThirdParty', 'Steamworks.NET', 'Runtime', 'autogen', 'NativeMethods.cs')


def body(asm, name):
    """The instructions of NAME in gen.py's assembly."""
    lines = asm.splitlines()
    i = lines.index(name + ':')
    out = []
    for line in lines[i + 1 :]:
        if line.startswith('.globl') or line.endswith(':'):
            break
        out.append(line.strip())
    return out


@unittest.skipUnless(sys.platform.startswith('linux'), 'the ELF double')
class GenOptionsTests(unittest.TestCase):
    def gen(self, *options):
        with tempfile.TemporaryDirectory() as d:
            out = os.path.join(d, 'stub.s')
            subprocess.run([sys.executable, '-I', os.path.join(STUB, 'gen.py'), NATIVE, out, *options], check=True)
            with open(out) as f:
                return f.read()

    def test_no_dlc_by_default(self):
        asm = self.gen()
        self.assertEqual(body(asm, 'SteamAPI_ISteamApps_BIsDlcInstalled')[0], 'xor eax, eax')
        self.assertIn('cmp esi, 457140', body(asm, 'SteamAPI_ISteamApps_BIsSubscribedApp'))

    def test_every_dlc(self):
        asm = self.gen('--dlc')
        for name in ('SteamAPI_ISteamApps_BIsDlcInstalled', 'SteamAPI_ISteamApps_BIsSubscribedApp'):
            self.assertEqual(body(asm, name)[0], 'mov rax, 0x1')
        self.assertEqual(body(asm, 'SteamAPI_ISteamUser_UserHasLicenseForApp')[0], 'mov rax, 0x0')

    def test_every_dlc_but_one(self):
        asm = self.gen('--dlc-except', '5315880')
        self.assertEqual(body(asm, 'SteamAPI_ISteamApps_BIsDlcInstalled')[:3], ['xor eax, eax', 'cmp esi, 5315880', 'setne al'])
        self.assertEqual(body(asm, 'SteamAPI_ISteamUser_UserHasLicenseForApp')[:3], ['xor eax, eax', 'cmp edx, 5315880', 'sete al'])

    def test_ugc_leaves_its_functions_to_ugc_s(self):
        asm = self.gen('--dlc', '--ugc')
        self.assertNotIn('SteamAPI_ISteamUGC_GetItemInstallInfo:', asm)
        self.assertNotIn('SteamAPI_ManualDispatch_GetNextCallback:', asm)
        self.assertIn('SteamAPI_ISteamUGC_GetUserItemVote:', asm)


# Runs in a child: the double reads its configuration from the environment it starts with.
CHILD = textwrap.dedent(r'''
    import ctypes, json, os, sys
    lib = ctypes.CDLL(sys.argv[1])
    u64, u32, ptr = ctypes.c_uint64, ctypes.c_uint32, ctypes.c_void_p
    out = {}
    out['count'] = lib.SteamAPI_ISteamUGC_GetNumSubscribedItems(ptr(1))
    items = (u64 * 2)()
    out['items'] = [lib.SteamAPI_ISteamUGC_GetSubscribedItems(ptr(1), items, u32(2)), items[0]]
    lib.SteamAPI_ISteamUGC_GetItemState.restype = u32
    out['state'] = [lib.SteamAPI_ISteamUGC_GetItemState(ptr(1), u64(123456789012)), lib.SteamAPI_ISteamUGC_GetItemState(ptr(1), u64(5))]
    size, stamp = u64(9), u32(9)
    folder = ctypes.create_string_buffer(1024)
    lib.SteamAPI_ISteamUGC_GetItemInstallInfo.restype = ctypes.c_bool
    ok = lib.SteamAPI_ISteamUGC_GetItemInstallInfo(ptr(1), u64(123456789012), ctypes.byref(size), folder, u32(1024), ctypes.byref(stamp))
    short = ctypes.create_string_buffer(8)
    lib.SteamAPI_ISteamUGC_GetItemInstallInfo(ptr(1), u64(123456789012), ctypes.byref(size), short, u32(8), ctypes.byref(stamp))
    out['install'] = [ok, folder.value.decode(), size.value, stamp.value, short.value.decode()]
    lib.SteamAPI_ISteamUGC_SendQueryUGCRequest.restype = u64
    msg = ctypes.create_string_buffer(20)
    lib.SteamAPI_ManualDispatch_GetNextCallback.restype = ctypes.c_bool
    out['idle'] = lib.SteamAPI_ManualDispatch_GetNextCallback(ptr(1), msg)
    call = lib.SteamAPI_ISteamUGC_SendQueryUGCRequest(ptr(1), u64(1))
    got = lib.SteamAPI_ManualDispatch_GetNextCallback(ptr(1), msg)
    raw = msg.raw
    completed = ctypes.cast(int.from_bytes(raw[8:16], 'little'), ctypes.POINTER(ctypes.c_ubyte * 16)).contents
    done = bytes(completed)
    out['query_msg'] = [call, got, int.from_bytes(raw[4:8], 'little'), int.from_bytes(done[0:8], 'little'),
                        int.from_bytes(done[8:12], 'little'), int.from_bytes(done[12:16], 'little'),
                        lib.SteamAPI_ManualDispatch_GetNextCallback(ptr(1), msg)]
    result = ctypes.create_string_buffer(b'\xff' * 280, 280)
    failed = ctypes.c_bool(True)
    lib.SteamAPI_ManualDispatch_GetAPICallResult.restype = ctypes.c_bool
    ok = lib.SteamAPI_ManualDispatch_GetAPICallResult(ptr(1), u64(call), result, ctypes.c_int(280), ctypes.c_int(3401), ctypes.byref(failed))
    r = result.raw
    out['query_result'] = [ok, failed.value, int.from_bytes(r[0:8], 'little'), int.from_bytes(r[8:12], 'little'),
                           int.from_bytes(r[12:16], 'little'), r[20:].count(0)]
    details = ctypes.create_string_buffer(9764)
    lib.SteamAPI_ISteamUGC_GetQueryUGCResult.restype = ctypes.c_bool
    ok = lib.SteamAPI_ISteamUGC_GetQueryUGCResult(ptr(1), u64(1), u32(0), details)
    d = details.raw
    out['details'] = [ok, int.from_bytes(d[0:8], 'little'), int.from_bytes(d[8:12], 'little'), int.from_bytes(d[16:20], 'little'),
                      d[24:24 + 129].split(b'\0')[0].decode(), int.from_bytes(d[9208:9216], 'little'), d[9216:9224].hex()]
    lib.SteamAPI_ISteamRemoteStorage_UGCDownloadToLocation.restype = u64
    target = sys.argv[2]
    call = lib.SteamAPI_ISteamRemoteStorage_UGCDownloadToLocation(ptr(1), u64(7), target.encode(), u32(0))
    got = lib.SteamAPI_ManualDispatch_GetNextCallback(ptr(1), msg)
    done = bytes(ctypes.cast(int.from_bytes(msg.raw[8:16], 'little'), ctypes.POINTER(ctypes.c_ubyte * 16)).contents)
    result = ctypes.create_string_buffer(288)
    ok = lib.SteamAPI_ManualDispatch_GetAPICallResult(ptr(1), u64(call), result, ctypes.c_int(288), ctypes.c_int(1317), ctypes.byref(failed))
    r = result.raw
    out['download'] = [call, got, int.from_bytes(done[8:12], 'little'), int.from_bytes(done[12:16], 'little'), ok,
                       int.from_bytes(r[0:4], 'little'), int.from_bytes(r[4:12], 'little'), int.from_bytes(r[16:20], 'little'),
                       open(target, 'rb').read().decode()]
    print(json.dumps(out))
''')


@unittest.skipUnless(sys.platform.startswith('linux'), 'the ELF double')
class UgcTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        root = cls.tmp.name
        out = os.path.join(root, 'stub')
        os.makedirs(out)
        subprocess.run([sys.executable, '-I', os.path.join(STUB, 'gen.py'), NATIVE, os.path.join(out, 's.s'), '--dlc', '--ugc'], check=True)
        subprocess.run(['as', '--64', '-o', os.path.join(out, 's.o'), os.path.join(out, 's.s')], check=True)
        subprocess.run(['as', '--64', '-o', os.path.join(out, 'u.o'), os.path.join(STUB, 'ugc.s')], check=True)
        cls.lib = os.path.join(out, 'libsteam_api.so')
        subprocess.run(['ld', '-shared', '-o', cls.lib, os.path.join(out, 's.o'), os.path.join(out, 'u.o'), '/lib/x86_64-linux-gnu/libc.so.6'], check=True)
        cls.source = os.path.join(root, 'fresh.zip')
        with open(cls.source, 'wb') as f:
            f.write(b'fresh content')

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def run_child(self, env):
        target = os.path.join(self.tmp.name, 'item.zip.download')
        script = os.path.join(self.tmp.name, 'child.py')
        with open(script, 'w') as f:
            f.write(CHILD)
        full = {k: v for k, v in os.environ.items() if not k.startswith('ONI_STEAMSTUB_')}
        full.update(env)
        import json

        done = subprocess.run([sys.executable, '-I', script, self.lib, target], env=full, capture_output=True, text=True, check=True)
        return json.loads(done.stdout)

    def test_a_subscription_installed_at_a_folder(self):
        out = self.run_child(
            {
                'ONI_STEAMSTUB_UGC_ID': '123456789012',
                'ONI_STEAMSTUB_UGC_PATH': '/ws/content/457140/123456789012',
                'ONI_STEAMSTUB_UGC_TITLE': 'Loose Mod',
                'ONI_STEAMSTUB_UGC_DOWNLOAD': self.source,
            }
        )
        self.assertEqual(out['count'], 1)
        self.assertEqual(out['items'], [1, 123456789012])
        self.assertEqual(out['state'], [5, 0])  # subscribed | installed; another item: nothing
        self.assertEqual(out['install'], [True, '/ws/content/457140/123456789012', 0, 0, '/ws/con'])
        self.assertFalse(out['idle'])
        # SendQueryUGCRequest's call 0x100 completes once, as SteamUGCQueryCompleted_t (3401, 280 bytes).
        self.assertEqual(out['query_msg'], [0x100, True, 703, 0x100, 3401, 280, False])
        self.assertEqual(out['query_result'], [True, False, 1, 1, 1, 260])
        self.assertEqual(out['details'], [True, 123456789012, 1, 457140, 'Loose Mod', 7, 'ff' * 8])
        # UGCDownloadToLocation copies the source and completes as RemoteStorageDownloadUGCResult_t.
        self.assertEqual(out['download'], [0x200, True, 1317, 288, True, 1, 7, 13, 'fresh content'])

    def test_no_subscription(self):
        out = self.run_child({'ONI_STEAMSTUB_UGC_DOWNLOAD': self.source})
        self.assertEqual(out['count'], 0)
        self.assertEqual(out['items'][0], 0)
        self.assertEqual(out['state'], [0, 0])
        self.assertFalse(out['install'][0])


if __name__ == '__main__':
    unittest.main()
