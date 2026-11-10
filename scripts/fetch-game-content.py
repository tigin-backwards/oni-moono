#!/usr/bin/env python3
"""Fetches the stripped game (tools/GameStrip) that CI's game tests run on, and the e2e fixture
saves they load, from the private refs repository's release assets by the hashes in
ci/game-content.json and tests/e2e/fixtures.json.

    scripts/fetch-game-content.py game OUT [--build N]
        the build's stripped install into OUT (an empty or missing folder): its archive, release
        TAG's asset, is downloaded, checked against its size and SHA-256, then unpacked (zstd
        and tar)
    scripts/fetch-game-content.py saves NAME...
        the fixtures NAME... into tests/e2e/fixtures, through scripts/fixtures.py and its cache
    scripts/fetch-game-content.py --print-default-build

Both download with `gh release download`: with the token in GH_TOKEN when it is set (CI's
GitHub App token), else the user's own gh login. The stripped game and the saves are private:
nothing here prints their contents, and CI keeps them in the runner's temporary folder only
(docs/DEVELOPMENT.md, "Game tests in CI").
"""

import argparse
import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, 'ci', 'game-content.json')
BUILD = re.compile(r'^[0-9]+$')
ASSET = re.compile(r'^oni-[0-9]+\.tar\.zst$')


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), os.path.join(ROOT, 'scripts', name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


fixtures = load('fixtures')


class ContentError(Exception):
    pass


def manifest():
    with open(MANIFEST, encoding='utf-8') as f:
        m = json.load(f)
    if m.get('schema') != 2:
        raise ContentError(f'game-content.json: unknown schema {m.get("schema")!r}')
    source = m.get('source', {})
    if source.get('kind') != 'github-release' or source.get('host') != 'github.com' or not fixtures.REPOSITORY.match(source.get('repository', '')):
        raise ContentError(f'game-content.json: unexpected source {source!r}')
    for build, entry in m.get('builds', {}).items():
        digest = entry.get('sha256', '')
        if not BUILD.match(build) or not fixtures.SHA256.match(digest):
            raise ContentError(f'game-content.json: build {build!r}: no archive SHA-256')
        if entry.get('tag') != f'game/{build}-{digest[:16]}':
            raise ContentError(f'game-content.json: build {build}: the tag must be game/{build}- and the hash\'s first 16 digits, not {entry.get("tag")!r}')
        if not ASSET.match(entry.get('asset', '')) or not isinstance(entry.get('size'), int) or entry['size'] <= 0:
            raise ContentError(f'game-content.json: build {build}: unexpected asset {entry.get("asset")!r} or size {entry.get("size")!r}')
    return m


def fetch_game(out, build):
    m = manifest()
    build = build or m['default_build']
    entry = m['builds'].get(build)
    if entry is None:
        raise ContentError(f'build {build} is not in game-content.json (it has {", ".join(sorted(m["builds"]))})')
    if os.path.isdir(out) and os.listdir(out):
        raise ContentError(f'{out} is not empty')
    with tempfile.TemporaryDirectory(prefix='game-content-') as work:
        archive = fixtures.gh_download(m['source']['repository'], entry['tag'], entry['asset'], work)
        size = os.path.getsize(archive)
        if size != entry['size'] or fixtures.sha256_file(archive) != entry['sha256']:
            raise ContentError(f'build {build}: the archive\'s size or SHA-256 differs from the manifest; nothing unpacked')
        os.makedirs(out, exist_ok=True)
        # The archive holds one folder (the build's), unpacked into OUT itself.
        with open(archive, 'rb') as f:
            unzstd = subprocess.Popen(['zstd', '-d', '-q', '-c'], stdin=f, stdout=subprocess.PIPE)
            tar = subprocess.Popen(['tar', '-x', '-C', out, '--strip-components=1', '--no-same-owner'], stdin=unzstd.stdout)
            unzstd.stdout.close()
            if unzstd.wait() or tar.wait():
                raise ContentError(f'build {build}: unpacking failed')
    print(f'game-content: build {build} ({entry["game"]}) in {out}, {size / 2**20:.0f} MiB fetched')


def fetch_saves(names):
    saves = fixtures.load_manifest()
    for name in names:
        _, entry = fixtures.lookup(saves, name)
        if entry is None or 'sha256' not in entry:
            raise ContentError(f'{name}: not a stored fixture in tests/e2e/fixtures.json')
    problems = fixtures.ensure(names, offline=False, log=print)
    if problems:
        raise ContentError('; '.join(f'{n}: {p}' for n, p in problems.items()))
    print(f'game-content: {len(names)} fixture(s) in tests/e2e/fixtures')


def main():
    if sys.argv[1:] == ['--print-default-build']:
        print(manifest()['default_build'])
        return 0
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('what', choices=['game', 'saves'])
    parser.add_argument('args', nargs='+')
    parser.add_argument('--build')
    a = parser.parse_args()
    try:
        if a.what == 'game':
            if len(a.args) != 1:
                parser.error('game takes one OUT folder')
            fetch_game(a.args[0], a.build)
        else:
            fetch_saves(a.args)
    except (ContentError, fixtures.FixtureError, OSError) as e:
        print(f'fetch-game-content: {e}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
