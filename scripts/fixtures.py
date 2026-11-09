#!/usr/bin/env python3
"""The e2e fixture saves (tests/e2e/fixtures/*.sav): not kept in git, but listed by hash in
tests/e2e/fixtures.json and stored as release assets of the private repository that holds the
game's reference assemblies (tigin-backwards/oni-moono-refs). Each worktree gets them from a
shared cache, ~/.cache/oni-brutal-e2e/fixtures/SHA256 (E2E_FIXTURE_CACHE moves it), filled by
`gh release download` with the user's own GitHub login.

    scripts/fixtures.py fetch [NAME...]     every stored fixture (or NAME...) into tests/e2e/fixtures
    scripts/fixtures.py check [NAME...]     whether they are there with their hashes; fetches nothing
    scripts/fixtures.py path NAME           fetches NAME if needed and prints its path (for qa.sh)
    scripts/fixtures.py bless PATH [--name NAME] [--description TEXT --game TEXT --build TEXT
                              --mods TEXT --used-by A,B]
                                            uploads PATH as a new asset and points the manifest at it

NAME is a fixture's file name (early-base.sav) or its spec name (early-base); an alias names the
same fixture by an older name. A fixture is placed as a writable copy of the cache file (which is
read-only); fixtures/.verified remembers each placed file's hash by size, time and inode.
A file already in the worktree with another hash is moved to fixtures/.replaced/ first, unless
e2e.py --bless wrote it (fixtures/.blessed-local lists those): that one is kept, and bless
uploads it. E2E_FIXTURES_OFFLINE=1 uses the cache only.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FIXTURES = os.path.join(ROOT, 'tests', 'e2e', 'fixtures')
MANIFEST = os.path.join(ROOT, 'tests', 'e2e', 'fixtures.json')
LOCAL_BLESSED = '.blessed-local'
VERIFIED = '.verified'
NAME = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._-]*\.(sav|png)$')
SHA256 = re.compile(r'^[0-9a-f]{64}$')
TAG = re.compile(r'^saves/[0-9a-f]{16}$')
REPOSITORY = re.compile(r'^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')
DESCRIBED = ('description', 'game', 'build', 'mods', 'used_by')


class FixtureError(Exception):
    pass


def cache_dir():
    return os.environ.get('E2E_FIXTURE_CACHE') or os.path.join(os.path.expanduser('~'), '.cache', 'oni-brutal-e2e', 'fixtures')


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def load_manifest(path=MANIFEST):
    with open(path, encoding='utf-8') as f:
        manifest = json.load(f)
    check_manifest(manifest)
    return manifest


def check_manifest(manifest):
    """Raises FixtureError for anything a fetch should not act on: unknown schema, names that
    are not plain file names, hashes, tags or a repository of an unexpected form."""
    if manifest.get('schema') != 1:
        raise FixtureError(f'fixtures.json: unknown schema {manifest.get("schema")!r}')
    source = manifest.get('source', {})
    if source.get('kind') != 'github-release' or source.get('host') != 'github.com' or not REPOSITORY.match(source.get('repository', '')):
        raise FixtureError(f'fixtures.json: unexpected source {source!r}')
    names = set()
    for name, entry in manifest.get('fixtures', {}).items():
        for n in [name] + entry.get('aliases', []):
            if not NAME.match(n):
                raise FixtureError(f'fixtures.json: unexpected name {n!r}')
            if n in names:
                raise FixtureError(f'fixtures.json: {n} is named twice')
            names.add(n)
        missing = [k for k in DESCRIBED if k not in entry]
        if missing:
            raise FixtureError(f'fixtures.json: {name} lacks {", ".join(missing)}')
        if 'not_stored' in entry:
            continue
        if not SHA256.match(entry.get('sha256', '')):
            raise FixtureError(f'fixtures.json: {name}: not a lower-case SHA-256: {entry.get("sha256")!r}')
        if not isinstance(entry.get('size'), int) or entry['size'] <= 0:
            raise FixtureError(f'fixtures.json: {name}: bad size {entry.get("size")!r}')
        if not TAG.match(entry.get('tag', '')) or entry['tag'] != 'saves/' + entry['sha256'][:16]:
            raise FixtureError(f'fixtures.json: {name}: the tag must be saves/ and the hash\'s first 16 digits, not {entry.get("tag")!r}')
        if not NAME.match(entry.get('asset', '')):
            raise FixtureError(f'fixtures.json: {name}: unexpected asset name {entry.get("asset")!r}')


def file_name(name):
    """A fixture's file name from a spec's name for it (early-base → early-base.sav)."""
    return name if NAME.match(name) else name + '.sav'


def lookup(manifest, name):
    """(canonical name, entry) for NAME or one of its aliases, or (None, None)."""
    name = file_name(name)
    for canonical, entry in manifest['fixtures'].items():
        if name == canonical or name in entry.get('aliases', []):
            return canonical, entry
    return None, None


def local_blessed(fixtures):
    """The hashes e2e.py --bless wrote into FIXTURES, by name."""
    try:
        with open(os.path.join(fixtures, LOCAL_BLESSED), encoding='utf-8') as f:
            return dict(reversed(line.split(None, 1)) for line in f.read().split('\n') if line.strip())
    except OSError:
        return {}


def record_local_bless(name, path, fixtures=FIXTURES):
    """Called by e2e.py --bless after it copied a save to PATH: ensure() then keeps it."""
    blessed = local_blessed(fixtures)
    blessed[os.path.basename(path)] = sha256_file(path)
    with open(os.path.join(fixtures, LOCAL_BLESSED), 'w', encoding='utf-8') as f:
        f.writelines(f'{digest}  {n}\n' for n, digest in sorted(blessed.items()))


def how_to_get(manifest, canonical, entry, fixtures):
    if 'not_stored' in entry:
        return f'it is not stored: {entry["not_stored"]}'
    repository = manifest['source']['repository']
    return (
        f'it is release {entry["tag"]} of the private repository {repository} (asset {entry["asset"]}); '
        f'with read access there, `gh auth login` and then scripts/fixtures.py fetch {canonical}, '
        f'or copy a blessed copy (sha256 {entry["sha256"]}) to {os.path.join(fixtures, canonical)}'
    )


def gh_download(repository, tag, asset, folder):
    """Downloads one release asset into FOLDER with the user's gh login; returns its path."""
    try:
        done = subprocess.run(
            ['gh', 'release', 'download', tag, '--repo', repository, '--pattern', asset, '--dir', folder],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=1800,
        )
    except FileNotFoundError:
        raise FixtureError('gh (GitHub CLI) is not installed') from None
    except subprocess.TimeoutExpired:
        raise FixtureError('gh release download took over 30 minutes') from None
    if done.returncode:
        raise FixtureError(f'gh release download failed: {(done.stderr or done.stdout).strip()}')
    return os.path.join(folder, asset)


def cached(manifest, entry, offline=False, download=gh_download, log=print):
    """The cache file holding ENTRY's hash: downloaded and checked when it is not there yet."""
    cache = cache_dir()
    path = os.path.join(cache, entry['sha256'])
    if os.path.isfile(path) and os.path.getsize(path) == entry['size']:
        return path
    if offline:
        raise FixtureError(f'not in the cache {cache} (E2E_FIXTURES_OFFLINE is set)')
    os.makedirs(cache, exist_ok=True)
    log(f'fixtures: downloading {entry["asset"]} ({entry["size"] / 2**20:.1f} MiB, {entry["tag"]})')
    with tempfile.TemporaryDirectory(dir=cache, prefix='.download-') as work:
        got = download(manifest['source']['repository'], entry['tag'], entry['asset'], work)
        digest = sha256_file(got)
        if digest != entry['sha256']:
            raise FixtureError(f'the download has sha256 {digest}, the manifest {entry["sha256"]}; nothing kept')
        os.chmod(got, 0o444)
        os.replace(got, path)
    return path


def place(source, target):
    """TARGET as a writable copy of SOURCE, swapped in whole. A copy, not a link to the
    read-only cache: the players' profiles get copies of it, which the game may save over."""
    temp = f'{target}.fetch-{os.getpid()}'
    shutil.copyfile(source, temp)
    os.chmod(temp, 0o644)
    os.replace(temp, target)


def ensure(names=None, fixtures=FIXTURES, manifest=None, offline=None, download=gh_download, log=print):
    """Puts each fixture of NAMES (all stored ones when None) into FIXTURES with its manifest
    hash. Returns {name: problem} for the ones it could not, each saying how to get it."""
    manifest = manifest or load_manifest()
    offline = os.environ.get('E2E_FIXTURES_OFFLINE') == '1' if offline is None else offline
    if names is None:
        names = [n for n, e in manifest['fixtures'].items() if 'not_stored' not in e]
    blessed = local_blessed(fixtures)
    problems = {}
    for name in names:
        canonical, entry = lookup(manifest, name)
        if entry is None:
            problems[file_name(name)] = f'{file_name(name)} is not in tests/e2e/fixtures.json'
            continue
        try:
            ensure_one(manifest, canonical, entry, fixtures, blessed, offline, download, log)
        except (FixtureError, OSError) as e:
            problems[file_name(name)] = f'no fixture {file_name(name)}: {e}; {how_to_get(manifest, canonical, entry, fixtures)}'
    return problems


def ensure_one(manifest, canonical, entry, fixtures, blessed, offline, download, log):
    target = os.path.join(fixtures, canonical)
    have = os.path.isfile(target) and sha256_of_placed(target)
    if have == entry.get('sha256'):
        pass
    elif have and blessed.get(canonical) == have:
        log(f'fixtures: {canonical} is a local bless not in the manifest (scripts/fixtures.py bless {target})')
    elif 'not_stored' in entry:
        raise FixtureError('not in the worktree')
    else:
        # An alias with the manifest's hash (an older checkout's copy) serves, and fills the cache.
        source = next(
            (a for a in (os.path.join(fixtures, n) for n in entry.get('aliases', [])) if os.path.isfile(a) and sha256_of_placed(a) == entry['sha256']), None
        )
        if source:
            fill_cache(source, entry['sha256'])
        else:
            source = cached(manifest, entry, offline, download, log)
        if have:
            set_aside(target, have, log)
        os.makedirs(fixtures, exist_ok=True)
        place(source, target)
        stamp(target, entry['sha256'])
        log(f'fixtures: {canonical} ({entry["sha256"][:12]}) placed')
    # An alias is the same file under its older name.
    for alias in entry.get('aliases', []):
        path = os.path.join(fixtures, alias)
        if os.path.isfile(path) and os.path.samefile(path, target):
            continue
        if os.path.isfile(path):
            digest = sha256_of_placed(path)
            if digest == sha256_of_placed(target):
                continue
            if blessed.get(alias) == digest:
                log(f'fixtures: {alias} is a local bless not in the manifest (scripts/fixtures.py bless {path} --name {canonical})')
                continue
            set_aside(path, digest, log)
        temp = f'{path}.fetch-{os.getpid()}'
        try:
            os.link(target, temp)  # both writable; a bless swaps a new file in, never writes into one
        except OSError:
            shutil.copyfile(target, temp)
        os.replace(temp, path)


def set_aside(path, digest, log):
    aside = os.path.join(os.path.dirname(path), '.replaced', f'{os.path.basename(path)}.{digest[:12]}')
    os.makedirs(os.path.dirname(aside), exist_ok=True)
    os.replace(path, aside)
    log(f'fixtures: {os.path.basename(path)} had another hash ({digest[:12]}); moved to {aside}')


def sha256_of_placed(path):
    """PATH's hash, read again only when its size, modification time or inode changed since
    the last time (fixtures/.verified keeps them), so a run does not read a 150 MB save twice."""
    stamps = read_stamps(path)
    st = os.stat(path)
    name = os.path.basename(path)
    if stamps.get(name, [None])[:3] == [st.st_size, st.st_mtime_ns, st.st_ino]:
        return stamps[name][3]
    digest = sha256_file(path)
    stamp(path, digest)
    return digest


def read_stamps(path):
    try:
        with open(os.path.join(os.path.dirname(path), VERIFIED), encoding='utf-8') as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def stamp(path, digest):
    """Notes PATH's hash in fixtures/.verified with its size, modification time and inode."""
    stamps = read_stamps(path)
    st = os.stat(path)
    stamps[os.path.basename(path)] = [st.st_size, st.st_mtime_ns, st.st_ino, digest]
    stamp_path = os.path.join(os.path.dirname(path), VERIFIED)
    temp = f'{stamp_path}.{os.getpid()}'
    with open(temp, 'w', encoding='utf-8') as f:
        json.dump(stamps, f, indent=0, sort_keys=True)
    os.replace(temp, stamp_path)


def check(names=None, fixtures=FIXTURES, manifest=None):
    """{name: problem} for the fixtures of NAMES not in FIXTURES with their hashes."""
    manifest = manifest or load_manifest()
    if names is None:
        names = [n for n, e in manifest['fixtures'].items() if 'not_stored' not in e]
    problems = {}
    for name in names:
        canonical, entry = lookup(manifest, name)
        path = os.path.join(fixtures, file_name(name))
        if entry is None:
            problems[file_name(name)] = 'not in tests/e2e/fixtures.json'
        elif not os.path.isfile(path):
            problems[file_name(name)] = 'missing'
        elif 'sha256' in entry and sha256_of_placed(path) != entry['sha256']:
            problems[file_name(name)] = f'sha256 {sha256_of_placed(path)}, the manifest {entry["sha256"]}'
    return problems


def gh_upload(repository, tag, path, asset, notes):
    """Creates release TAG of REPOSITORY with PATH as its one asset, named ASSET."""
    with tempfile.TemporaryDirectory() as work:
        named = os.path.join(work, asset)
        shutil.copyfile(path, named)
        view = subprocess.run(
            ['gh', 'release', 'view', tag, '--repo', repository, '--json', 'assets'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
        )
        if view.returncode == 0:
            assets = [a['name'] for a in json.loads(view.stdout)['assets']]
            if asset in assets:
                return False
            subprocess.run(['gh', 'release', 'upload', tag, named, '--repo', repository], check=True)
            return True
        subprocess.run(['gh', 'release', 'create', tag, named, '--repo', repository, '--title', tag, '--notes', notes, '--target', 'main'], check=True)
        return True


def bless(path, name=None, fields=None, manifest_path=MANIFEST, upload=gh_upload, log=print):
    """Uploads PATH (a new or re-blessed fixture) and writes its manifest entry; an existing
    entry keeps its description and the rest unless FIELDS gives them."""
    with open(manifest_path, encoding='utf-8') as f:
        manifest = json.load(f)
    check_manifest(manifest)
    name = file_name(name or os.path.basename(path))
    canonical, entry = lookup(manifest, name)
    canonical = canonical or name
    entry = dict(entry or {})
    entry.pop('not_stored', None)
    for k, v in (fields or {}).items():
        if v is not None:
            entry[k] = v
    missing = [k for k in DESCRIBED if k not in entry]
    if missing:
        raise FixtureError(f'{canonical} is new: give --{" --".join(m.replace("_", "-") for m in missing)} (by kind only: no colony or player names)')
    digest = sha256_file(path)
    entry.update(sha256=digest, size=os.path.getsize(path), tag='saves/' + digest[:16], asset=canonical)
    repository = manifest['source']['repository']
    notes = f'{canonical}: an e2e fixture save (sha256 {digest}). Private: see saves/README.md on main.'
    if upload(repository, entry['tag'], path, canonical, notes):
        log(f'fixtures: uploaded {canonical} as {entry["tag"]}')
    else:
        log(f'fixtures: {entry["tag"]} already holds {canonical}')
    ordered = {
        k: entry[k] for k in ('description', 'game', 'build', 'mods', 'used_by', 'blessed_by', 'aliases', 'sha256', 'size', 'tag', 'asset') if k in entry
    }
    manifest['fixtures'][canonical] = ordered
    manifest['fixtures'] = dict(sorted(manifest['fixtures'].items()))
    with open(manifest_path, 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write('\n')
    return canonical, fill_cache(path, digest)


def fill_cache(path, digest):
    """The cache file for DIGEST, copied from PATH (which has that hash) when it is not there."""
    os.makedirs(cache_dir(), exist_ok=True)
    cache = os.path.join(cache_dir(), digest)
    if not os.path.exists(cache):
        temp = f'{cache}.{os.getpid()}.tmp'
        shutil.copyfile(path, temp)
        os.chmod(temp, 0o444)
        os.replace(temp, cache)
    return cache


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    for command in ('fetch', 'check'):
        sub.add_parser(command).add_argument('names', nargs='*')
    sub.add_parser('path').add_argument('name')
    b = sub.add_parser('bless')
    b.add_argument('path')
    b.add_argument('--name')
    for field in ('description', 'game', 'build', 'mods'):
        b.add_argument('--' + field)
    b.add_argument('--used-by', help='comma-separated specs, QA scenarios or scripts')
    args = parser.parse_args(argv)
    say = lambda line: print(line, file=sys.stderr)  # noqa: E731 (stdout is `path`'s answer)
    if args.command == 'check':
        problems = check(args.names or None)
        for name, problem in sorted(problems.items()):
            print(f'{name}: {problem}')
        return 1 if problems else 0
    if args.command in ('fetch', 'path'):
        names = [args.name] if args.command == 'path' else (args.names or None)
        problems = ensure(names, log=say)
        for problem in problems.values():
            say(f'fixtures: {problem}')
        if problems:
            return 1
        if args.command == 'path':
            print(os.path.join(FIXTURES, file_name(args.name)))
        return 0
    fields = {k: getattr(args, k) for k in ('description', 'game', 'build', 'mods')}
    fields['used_by'] = args.used_by.split(',') if args.used_by else None
    before = subprocess.run(['git', '-C', ROOT, 'diff', '--quiet', '--', MANIFEST]).returncode
    canonical, cache = bless(args.path, args.name, fields, log=say)
    place(cache, os.path.join(FIXTURES, canonical))
    subprocess.run(['git', '-C', ROOT, '--no-pager', 'diff', '--', MANIFEST])
    if before:
        say('fixtures: the manifest had other changes before this bless too')
    say('fixtures: add where the save came from to saves/README.md in the refs repository (private), then commit the manifest')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1:]))
    except (FixtureError, subprocess.CalledProcessError, OSError, ValueError) as e:
        print(f'fixtures: {e}', file=sys.stderr)
        sys.exit(1)
