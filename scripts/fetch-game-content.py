#!/usr/bin/env python3
"""Fetches the stripped game (tools/GameStrip) that CI's game tests run on, and the e2e fixture
saves they load, from the private refs repository by the hashes in ci/game-content.json.

    scripts/fetch-game-content.py game OUT [--build N] [--transport https|ssh]
        the build's stripped install into OUT (an empty or missing folder): its archive parts
        are read as git blobs at the manifest's commit, each checked against its SHA-256, the
        whole archive against its own, then unpacked (zstd and tar)
    scripts/fetch-game-content.py saves NAME... [--transport https|ssh]
        the fixtures NAME... into tests/e2e/fixtures, through scripts/fixtures.py: each save is
        read from the refs repository's game/saves/SHA256 (SHA256 from tests/e2e/fixtures.json),
        checked, and put in fixtures.py's cache, from which `fixtures.py fetch` places it
    scripts/fetch-game-content.py --print-default-build

The CI job reads them with the refs repository's read-only deploy key (--transport ssh, as
scripts/fetch-game-refs.py does); --transport https uses the user's own git credentials. The
stripped game and the saves are private: nothing here prints their contents, and CI keeps them
in the runner's temporary folder only (docs/DEVELOPMENT.md, "Game tests in CI").
"""

import argparse
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, 'ci', 'game-content.json')


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), os.path.join(ROOT, 'scripts', name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


refs = load('fetch-game-refs')
fixtures = load('fixtures')


class ContentError(Exception):
    pass


def manifest():
    with open(MANIFEST, encoding='utf-8') as f:
        m = json.load(f)
    if m.get('schema') != 1:
        raise ContentError(f'game-content.json: unknown schema {m.get("schema")!r}')
    source = m.get('source', {})
    if source.get('kind') != 'git':
        raise ContentError(f'game-content.json: unexpected source {source!r}')
    for build, entry in m.get('builds', {}).items():
        for name, digest in entry.get('parts', {}).items():
            if not refs.SEGMENT.match(name) or not refs.SHA256.match(digest):
                raise ContentError(f'game-content.json: build {build}: unexpected part {name!r} {digest!r}')
        if not refs.SHA256.match(entry.get('sha256', '')):
            raise ContentError(f'game-content.json: build {build}: no archive SHA-256')
    return m


def blobs(source, path, names, transport):
    """NAME -> bytes of PATH/NAME in the refs repository at the manifest's commit."""
    with tempfile.TemporaryDirectory() as work:
        return refs.fetch_git(dict(source, path=path), names, transport, work)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def fetch_game(out, build, transport):
    m = manifest()
    build = build or m['default_build']
    entry = m['builds'].get(build)
    if entry is None:
        raise ContentError(f'build {build} is not in game-content.json (it has {", ".join(sorted(m["builds"]))})')
    if os.path.isdir(out) and os.listdir(out):
        raise ContentError(f'{out} is not empty')
    names = sorted(entry['parts'])
    got = blobs(m['source'], entry['path'], names, transport)
    for name in names:
        if sha256(got[name]) != entry['parts'][name]:
            raise ContentError(f'{entry["path"]}/{name}: SHA-256 differs from the manifest; nothing unpacked')
    archive = b''.join(got[name] for name in names)
    if sha256(archive) != entry['sha256']:
        raise ContentError(f'build {build}: the joined archive\'s SHA-256 differs from the manifest; nothing unpacked')
    os.makedirs(out, exist_ok=True)
    # The archive holds one folder (the build's), unpacked into OUT itself.
    unzstd = subprocess.Popen(['zstd', '-d', '-q', '-c'], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    tar = subprocess.Popen(['tar', '-x', '-C', out, '--strip-components=1', '--no-same-owner'], stdin=unzstd.stdout)
    unzstd.stdout.close()
    unzstd.stdin.write(archive)
    unzstd.stdin.close()
    if unzstd.wait() or tar.wait():
        raise ContentError(f'build {build}: unpacking failed')
    print(f'game-content: build {build} ({entry["game"]}) in {out}, {len(archive) / 2**20:.0f} MiB fetched')


def fetch_saves(names, transport):
    m = manifest()
    saves = fixtures.load_manifest()
    wanted = {}
    for name in names:
        canonical, entry = fixtures.lookup(saves, name)
        if entry is None or 'sha256' not in entry:
            raise ContentError(f'{name}: not a stored fixture in tests/e2e/fixtures.json')
        if entry['sha256'] not in m.get('saves', []):
            raise ContentError(f'{canonical}: its hash is not among game-content.json\'s saves (copy it to the refs repository\'s game/saves first)')
        wanted[canonical] = entry
    cache = fixtures.cache_dir()
    os.makedirs(cache, exist_ok=True)
    missing = {e['sha256'] for e in wanted.values() if not os.path.isfile(os.path.join(cache, e['sha256']))}
    if missing:
        got = blobs(m['source'], 'game/saves', sorted(missing), transport)
        for digest, data in got.items():
            if sha256(data) != digest:
                raise ContentError(f'game/saves/{digest}: the blob does not have its hash; nothing kept')
            path = os.path.join(cache, digest)
            with open(path + '.part', 'wb') as f:
                f.write(data)
            os.chmod(path + '.part', 0o444)
            os.replace(path + '.part', path)
    problems = fixtures.ensure(list(wanted), offline=True, log=lambda line: None)
    if problems:
        raise ContentError('; '.join(f'{n}: {p}' for n, p in problems.items()))
    print(f'game-content: {len(wanted)} fixture(s) in tests/e2e/fixtures')


def main():
    if sys.argv[1:] == ['--print-default-build']:
        print(manifest()['default_build'])
        return 0
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('what', choices=['game', 'saves'])
    parser.add_argument('args', nargs='+')
    parser.add_argument('--build')
    parser.add_argument('--transport', choices=['https', 'ssh'], default='https')
    a = parser.parse_args()
    try:
        if a.what == 'game':
            if len(a.args) != 1:
                parser.error('game takes one OUT folder')
            fetch_game(a.args[0], a.build, a.transport)
        else:
            fetch_saves(a.args, a.transport)
    except (ContentError, refs.RefsError, fixtures.FixtureError, subprocess.CalledProcessError) as e:
        print(f'fetch-game-content: {e}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
