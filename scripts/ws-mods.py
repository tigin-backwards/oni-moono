#!/usr/bin/env python3
"""Workshop mods, downloaded by SteamCMD, installed into a run home as Local mods.
  scripts/ws-mods.py download ID...       fetch items with SteamCMD (anonymous login)
  scripts/ws-mods.py profile OUT ID...    unpack each item into out/ws/ID, and write OUT/Local/ID and
                                          OUT/mods.json (OUT is a run home's mods folder)
Items are Steam's legacy zips (Windows paths); a mod's archived_versions are kept, as KMod picks
the right one. Mods are installed as Local mods, the nearest to a Workshop subscription: Dev mods
get KMod's developer checks (ModDevLogError turns a deprecated mod_info.yaml key into an error),
which subscribers never see. A Local mod's version is its folder name's Mono string hash, as KMod
computes it (Local.Subscribe), or KMod disables it as changed. Every mod passes the mod gate
(scripts/mod-gate.py) before it is installed: profile installs none of them when it skips one.

Environment:
  ONI_STEAMCMD       SteamCMD's folder, holding steamcmd.sh (default out/local-tools/steamcmd)
  ONI_STEAMCMD_SNAP  the snap whose shell runs SteamCMD (default steam, where its 32-bit
                     libraries are); empty runs steamcmd.sh directly
  ONI_WS             where items are unpacked (default out/ws)"""

import json, os, re, subprocess, sys, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STEAMCMD = os.environ.get('ONI_STEAMCMD') or os.path.join(ROOT, 'out', 'local-tools', 'steamcmd')
SNAP = os.environ.get('ONI_STEAMCMD_SNAP', 'steam')
CONTENT = os.path.join(STEAMCMD, 'dl', 'steamapps', 'workshop', 'content', '457140')
WS = os.environ.get('ONI_WS') or os.path.join(ROOT, 'out', 'ws')


def download(ids):
    args = f'+force_install_dir {STEAMCMD}/dl +login anonymous ' + ' '.join(f'+workshop_download_item 457140 {i}' for i in ids)
    shell = f'cd {STEAMCMD} && ./steamcmd.sh {args} +quit'
    cmd = ['snap', 'run', '--shell', SNAP, '-c', shell] if SNAP else ['bash', '-c', shell]
    out = subprocess.run(cmd, capture_output=True, text=True).stdout
    for line in out.splitlines():
        line = re.sub(r'\x1b\[[0-9;]*m', '', line)
        if 'Success' in line or 'ERROR' in line or 'ailed' in line:
            print(line[:140])


def unpack(i):
    dest = os.path.join(WS, i)
    if os.path.isdir(dest):
        return dest
    src = os.path.join(CONTENT, i)
    files = os.listdir(src) if os.path.isdir(src) else []
    bins = [f for f in files if f.endswith('.bin')]
    os.makedirs(dest)
    if bins:
        z = zipfile.ZipFile(os.path.join(src, bins[0]))
        for info in z.infolist():
            parts = [p for p in info.filename.replace('\\', '/').split('/') if p not in ('', '.')]
            if not parts or '..' in parts or ':' in parts[0]:
                continue  # an entry that would land outside the mod's folder
            path = os.path.join(dest, *parts)
            if info.filename.endswith(('/', '\\')):
                os.makedirs(path, exist_ok=True)
                continue
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'wb') as f:
                f.write(z.read(info))
    else:  # a plain folder (newer uploads)
        subprocess.run(['cp', '-r', src + '/.', dest], check=True)
    return dest


def mono_hash(s):
    """string.GetHashCode as Unity's 64-bit Mono computes it: the .NET Framework 64-bit algorithm,
    as OniMoono.Engine.MonoStringHash (checked against the original player)."""

    def i32(x):
        x &= 0xFFFFFFFF
        return x - (1 << 32) if x & 0x80000000 else x

    h1 = h2 = 5381
    for i in range(0, len(s), 2):
        h1 = i32(((h1 << 5) + h1) ^ ord(s[i]))
        if i + 1 < len(s):
            h2 = i32(((h2 << 5) + h2) ^ ord(s[i + 1]))
    return i32(h1 + h2 * 1566083941)


def yaml_value(text, key):
    m = re.search(rf'^{key}:\s*["\']?(.*?)["\']?\s*$', text, re.M)
    return m.group(1) if m else None


class GateSkipped(Exception):
    """Mods the mod gate skipped: {id: its verdict}."""

    def __init__(self, skipped):
        self.skipped = skipped
        super().__init__('skipped by the mod gate: ' + '; '.join(f'{i}: {v["reason"]}' for i, v in skipped.items()))


def gate(ids):
    """{id: verdict} from the mod gate (scripts/mod-gate.py, run isolated, as it reads downloaded
    files) for the unpacked mods IDS."""
    if not ids:
        return {}
    done = subprocess.run(
        [sys.executable, '-I', os.path.join(ROOT, 'scripts', 'mod-gate.py'), '--json', *ids],
        capture_output=True,
        text=True,
        env=dict(os.environ, ONI_WS=WS, ONI_STEAMCMD=STEAMCMD),
    )
    try:
        verdicts = json.loads(done.stdout.strip().splitlines()[-1])
    except (IndexError, ValueError):
        verdicts = {}
    failed = {'verdict': 'skip', 'reason': f'the mod gate gave no verdict: {done.stderr.strip()[-300:]}'}
    return {i: verdicts.get(i) or dict(failed, id=i) for i in ids}


def profile(out, ids):
    for i in ids:
        unpack(i)
    skipped = {i: v for i, v in gate(list(ids)).items() if v['verdict'] != 'pass'}
    if skipped:
        raise GateSkipped(skipped)
    os.makedirs(os.path.join(out, 'Local'), exist_ok=True)
    os.makedirs(os.path.join(out, 'config'), exist_ok=True)
    mods = []
    for i in ids:
        src = unpack(i)
        dest = os.path.join(out, 'Local', i)
        if not os.path.exists(dest):
            subprocess.run(['cp', '-r', src, dest], check=True)
        yaml = os.path.join(dest, 'mod.yaml')
        text = open(yaml, encoding='utf-8-sig', errors='replace').read() if os.path.exists(yaml) else ''
        static = yaml_value(text, 'staticID') or i
        title = yaml_value(text, 'title') or i
        mods.append(
            {
                'label': {'distribution_platform': 0, 'id': i, 'title': title, 'version': mono_hash(i)},
                'status': 1,
                'enabled': True,
                'enabledForDlc': ['', 'EXPANSION1_ID'],
                'crash_count': 0,
                'reinstall_path': None,
                'staticID': static,
            }
        )
        print(i, static, '|', title)
    json.dump({'version': 1, 'mods': mods, 'mod_load_in_progress': False}, open(os.path.join(out, 'mods.json'), 'w'))


if __name__ == '__main__':
    if len(sys.argv) < 3 or sys.argv[1] not in ('download', 'profile'):
        sys.exit(__doc__)
    if sys.argv[1] == 'download':
        download(sys.argv[2:])
    else:
        try:
            profile(sys.argv[2], sys.argv[3:])
        except GateSkipped as e:
            sys.exit(str(e))
