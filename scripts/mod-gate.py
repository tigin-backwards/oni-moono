#!/usr/bin/env -S python3 -I
"""The mod gate: a Workshop mod is installed into a run home only once onimaxxing has reviewed
the exact upload we have. Every install goes through it (ws-mods.py profile, which e2e.py's
players and mod-sweep.py use); this command checks mods without playing them.
  scripts/mod-gate.py [--build N] [--json] ID...
It prints one line per mod (pass or skip, and why) and exits 1 when any is skipped; --json
prints {id: {id, verdict, reason}} instead. A skipped mod is not played: it waits until
onimaxxing reviews its upload.

A mod passes when
  1. SteamCMD's upload handle for it (the ugchandle in appworkshop_457140.acf, a signed 64-bit
     number, taken mod 2^64) is the `file` of a version in onimaxxing's mod/ID/index.json that
     has `reviewed: true`. The same upload is the same files: every one of them was analysed,
     a standalone PLib.dll and archived_versions included;
  2. the unpacked folder (out/ws/ID) holds every DLL that version's facts (mod/ID/FILE.json)
     list in managed[], by SHA-256 (not by path: non-ASCII names come out of the zip in
     another encoding), so the unpacked copy is that upload;
  3. onimaxxing's catalog row has no malware, credentials, chatWebhook or obfuscated flag.
A mod with no binaries at all (a translation, a world) passes on 3 alone. One exception is
not on the Workshop: Fast Track's GitHub builds, as the local id 9000000001 (GITHUB_BUILDS),
pass when the folder's binaries are exactly one known build's, by SHA-256 (the user allowed
those builds in tests). The gate reads
onimaxxing's data as JSON and the mod's files as bytes; it never loads a mod's code.

onimaxxing's data comes from https://cdn.onimaxxing.com/mods-data/BUILD/, cached in
out/modgate/cache/BUILD/ with each file's fetch time and fetched again after an hour.

Environment:
  ONI_WS          the unpacked mods (default out/ws)
  ONI_STEAMCMD    SteamCMD's folder (default out/local-tools/steamcmd)
  ONI_MODGATE     the cache (default out/modgate)
  ONI_GAME_BUILD  the game build whose onimaxxing data is read (default: the build in the game
                  folder's name, ONI_GAME_DIR or scripts/env.sh's, else U59's 744825)"""
import argparse, hashlib, json, os, re, sys, time, urllib.error, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WS = os.environ.get('ONI_WS') or os.path.join(ROOT, 'out', 'ws')
STEAMCMD = os.environ.get('ONI_STEAMCMD') or os.path.join(ROOT, 'out', 'local-tools', 'steamcmd')
ACF = os.path.join(STEAMCMD, 'dl', 'steamapps', 'workshop', 'appworkshop_457140.acf')
OUT = os.environ.get('ONI_MODGATE') or os.path.join(ROOT, 'out', 'modgate')
CDN = 'https://cdn.onimaxxing.com/mods-data'
U59_BUILD = 744825
TTL = 3600
FLAGS = ('malware', 'credentials', 'chatWebhook', 'obfuscated')
# Mods from GitHub, by the local id specs install them as: {id: (name, {version: {path: sha256}})}.
# Only these exact builds pass (the folder's binaries, all of them, are one build's).
GITHUB_BUILDS = {
    '9000000001': ('Fast Track', {
        '0.18.5.0': {'FastTrack.dll': 'cdf0150546952fda3a31a612d61fbef3808e05db89b9b6e8cceea1f3c752aa3b'},
        '0.18.6.0': {'FastTrack.dll': '58dbbb3e347b3e9edbaae7fb9bf706c6ab304ce6d9216a8bffbc4624b4138e1a'},
    }),
}
BINARY_EXT = ('.dll', '.exe', '.so', '.dylib')
BINARY_MAGIC = (b'\x7fELF', b'\xfe\xed\xfa\xce', b'\xfe\xed\xfa\xcf', b'\xce\xfa\xed\xfe', b'\xcf\xfa\xed\xfe')


def game_build():
    if os.environ.get('ONI_GAME_BUILD'):
        return int(os.environ['ONI_GAME_BUILD'])
    game = os.environ.get('ONI_GAME_DIR') or ''
    if not game:
        with open(os.path.join(ROOT, 'scripts', 'env.sh'), encoding='utf-8') as f:
            m = re.search(r'^game="\$\{ONI_GAME_DIR:-([^}]*)\}"', f.read(), re.M)
        game = m.group(1) if m else ''
    m = re.search(r'-(\d{6,})/?$', game)
    return int(m.group(1)) if m else U59_BUILD


def http_get(url):
    """URL's bytes, or None on a 404. The CDN refuses Python's default user agent."""
    last = None
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={'User-Agent': 'moono-mod-gate/1'})
            with urllib.request.urlopen(request, timeout=30) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            last = e
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            last = e
        time.sleep(attempt + 1)
    raise ConnectionError(f'{url}: {last}')


class Onimaxxing:
    """onimaxxing's published data for one build, cached in CACHE (out/modgate/cache/BUILD) with
    the time each file was fetched. What comes back is data, parsed as JSON and nothing else."""

    def __init__(self, build, cache=None, fetch=http_get):
        self.build, self.fetch = build, fetch
        self.cache = cache or os.path.join(OUT, 'cache', str(build))
        self.rows = None

    def get(self, rel):
        """The JSON at REL under the build's data, or None when onimaxxing has none. A copy younger
        than TTL is used; one that cannot be fetched again is used however old."""
        if not re.fullmatch(r'catalog\.json|mod/\d+/(index|\d+)\.json', rel):
            raise ValueError(f'not an onimaxxing data path: {rel}')
        path = os.path.join(self.cache, rel)
        meta = None
        if os.path.exists(path + '.meta'):
            with open(path + '.meta', encoding='utf-8') as f:
                meta = json.load(f)
        if not meta or time.time() - meta['fetchedAt'] >= TTL:
            try:
                data = self.fetch(f'{CDN}/{self.build}/{rel}')
                if data is not None:
                    json.loads(data)
            except (ConnectionError, ValueError, OSError):
                if not meta:
                    raise
            else:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                if data is not None:
                    with open(path + '.tmp', 'wb') as f:
                        f.write(data)
                    os.replace(path + '.tmp', path)
                elif os.path.exists(path):
                    os.remove(path)
                meta = {'url': f'{CDN}/{self.build}/{rel}', 'fetchedAt': int(time.time()), 'found': data is not None}
                with open(path + '.meta', 'w', encoding='utf-8') as f:
                    json.dump(meta, f)
        if not meta.get('found') or not os.path.exists(path):
            return None
        with open(path, encoding='utf-8') as f:
            return json.load(f)

    def row(self, mid):
        """The catalog's row for MID ({column: value}), or None."""
        if self.rows is None:
            data = self.get('catalog.json') or {}
            cols = data.get('cols') if isinstance(data, dict) else None
            self.rows = {}
            if isinstance(cols, dict) and isinstance(cols.get('id'), list):
                names = [k for k, v in cols.items() if isinstance(v, list) and len(v) == len(cols['id'])]
                for i, row_id in enumerate(cols['id']):
                    self.rows[str(row_id)] = {k: cols[k][i] for k in names}
        return self.rows.get(mid)


def upload_handles(acf=ACF):
    """{id: SteamCMD's upload handle (unsigned, as a string)} from appworkshop_457140.acf."""
    if not os.path.exists(acf):
        return {}
    with open(acf, encoding='utf-8', errors='replace') as f:
        text = f.read()
    handles = {}
    for mid, value in re.findall(r'"(\d+)"\s*\{[^{}]*?"ugchandle"\s*"(-?\d+)"', text):
        handles.setdefault(mid, str(int(value) % (1 << 64)))
    return handles


def binaries(folder):
    """{relative path: sha256} of the folder's binaries: DLLs, executables and libraries, by
    their name or their first bytes (MZ, ELF, Mach-O)."""
    found = {}
    for base, _, names in os.walk(folder):
        for name in names:
            path = os.path.join(base, name)
            if os.path.islink(path) or not os.path.isfile(path):
                continue
            with open(path, 'rb') as f:
                data = f.read()
            if name.lower().endswith(BINARY_EXT) or data[:2] == b'MZ' or data[:4] in BINARY_MAGIC:
                found[os.path.relpath(path, folder)] = hashlib.sha256(data).hexdigest()
    return found


def check(mid, oni, handles, ws=WS):
    """{id, verdict ('pass' or 'skip'), reason} for the unpacked mod MID."""
    def verdict(ok, reason):
        return {'id': mid, 'verdict': 'pass' if ok else 'skip', 'reason': reason}
    folder = os.path.join(ws, mid)
    if not os.path.isdir(folder):
        return verdict(False, 'not downloaded')
    if mid in GITHUB_BUILDS:
        name, builds = GITHUB_BUILDS[mid]
        have = binaries(folder)
        version = next((v for v, files in builds.items() if files == have), None)
        if version:
            return verdict(True, f'a known GitHub build of {name} ({version})')
        return verdict(False, f'not a known GitHub build of {name}')
    analysis = (oni.row(mid) or {}).get('analysis') or {}
    flags = [k for k in FLAGS if analysis.get(k)]
    if flags:
        return verdict(False, 'onimaxxing flags it: ' + ', '.join(flags))
    have = binaries(folder)
    if not have:
        return verdict(True, 'no binaries')
    handle = handles.get(mid)
    if not handle:
        return verdict(False, 'SteamCMD records no upload handle for it')
    index = oni.get(f'mod/{mid}/index.json')
    versions = [v for v in (index or {}).get('versions') or [] if isinstance(v, dict)]
    version = next((v for v in versions if str(v.get('file')) == handle), None)
    if not version:
        return verdict(False, f'not reviewed by onimaxxing yet: upload {handle} is not analysed'
                       + (f' (its current upload is {index.get("current")})' if index else ''))
    if version.get('reviewed') is not True:
        return verdict(False, f'not reviewed by onimaxxing yet: upload {handle} (version {version.get("version")}) '
                       'is analysed, not reviewed')
    facts = oni.get(f'mod/{mid}/{handle}.json') or {}
    if str(facts.get('hcontentFile')) != handle:
        return verdict(False, f'onimaxxing has no facts for upload {handle}')
    hashes = set(have.values())
    differ = [str(m.get('path')) for m in facts.get('managed') or []
              if isinstance(m, dict) and m.get('sha256') and m['sha256'] not in hashes]
    if differ:
        return verdict(False, f'the unpacked copy is not upload {handle}: ' + ', '.join(differ) + ' differ')
    return verdict(True, f'onimaxxing reviewed upload {handle} (version {version.get("version")})')


def gate(ids, build=None, ws=WS, acf=ACF, oni=None):
    """{id: verdict} for the unpacked mods IDS (see check)."""
    oni = oni or Onimaxxing(build or game_build())
    handles = upload_handles(acf)
    results = {}
    for mid in ids:
        if not re.fullmatch(r'\d+', mid):
            results[mid] = {'id': mid, 'verdict': 'skip', 'reason': 'not a Workshop id'}
            continue
        try:
            results[mid] = check(mid, oni, handles, ws)
        except (ConnectionError, ValueError, OSError) as e:
            results[mid] = {'id': mid, 'verdict': 'skip', 'reason': f'onimaxxing\'s data could not be read: {e}'}
    return results


def main(argv):
    p = argparse.ArgumentParser(description='Check Workshop mods with the mod gate, without playing them.')
    p.add_argument('ids', nargs='+')
    p.add_argument('--build', type=int)
    p.add_argument('--json', action='store_true')
    opts = p.parse_args(argv)
    results = gate(list(dict.fromkeys(opts.ids)), opts.build)
    if opts.json:
        print(json.dumps(results))
    else:
        for r in results.values():
            print(f'{r["verdict"]}\t{r["id"]}\t{r["reason"]}')
    return 1 if any(r['verdict'] != 'pass' for r in results.values()) else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
