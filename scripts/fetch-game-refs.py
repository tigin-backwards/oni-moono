#!/usr/bin/env python3
"""Fetches one game build's reference assemblies (ci/game-refs.json) into a folder laid out like
a game install, OUT/OxygenNotIncluded_Data/Managed/, so a build takes it as its game directory
(-p:OniGameDir=OUT, ONI_GAME_DIR=OUT). Every file is checked against the manifest's SHA-256
before anything is written there; the files are build inputs only and are never run.

    scripts/fetch-game-refs.py OUT [--build N] [--manifest ci/game-refs.json] [--reuse]
    scripts/fetch-game-refs.py --print-default-build

A build's source is one of
  {"kind": "git", "host": "github.com", "repository": "owner/name", "commit": SHA, "path": DIR}
      the files DIR/NAME at that commit, read as blobs (no checkout, hooks or filters run) from a
      partial clone that fetches only the commit's trees and those blobs; over https, with the
      token in GH_TOKEN when it is set (CI's GitHub App token), else the user's git credentials;
  {"kind": "url", "base": "https://HOST/.../"}   BASE + NAME, from an allowed host only;
  {"kind": "local", "path": DIR}                 DIR/NAME (relative to the repository root).
"""

import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# Where a source may point. A manifest naming another host is refused, whatever its hashes say.
GIT_HOSTS = ("github.com",)
URL_HOSTS = ("github.com", "raw.githubusercontent.com", "objects.githubusercontent.com")
NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*\.dll$")
SHA1 = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
SEGMENT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")


class RefsError(Exception):
    pass


def sha256_file(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def check_manifest(manifest, build):
    builds = manifest.get("builds", {})
    if build not in builds:
        raise RefsError(f"build {build} is not in the manifest (it has {', '.join(sorted(builds))})")
    entry = builds[build]
    files = entry.get("files", {})
    if not files:
        raise RefsError(f"build {build}: no files")
    for name, digest in files.items():
        if not NAME.match(name):
            raise RefsError(f"build {build}: unexpected file name {name!r}")
        if not SHA256.match(digest):
            raise RefsError(f"build {build}: {name}: not a lower-case SHA-256: {digest!r}")
    return entry["source"], files


def segments(path, what):
    parts = path.split("/")
    if not all(SEGMENT.match(p) for p in parts):
        raise RefsError(f"unexpected {what} {path!r}")
    return parts


def fetch_git(source, names, work):
    host = source.get("host", "github.com")
    if host not in GIT_HOSTS:
        raise RefsError(f"refusing git host {host!r} (allowed: {', '.join(GIT_HOSTS)})")
    repository = "/".join(segments(source["repository"], "repository"))
    if len(repository.split("/")) != 2:
        raise RefsError(f"repository must be owner/name: {repository!r}")
    commit = source["commit"]
    if not SHA1.match(commit):
        raise RefsError(f"commit must be a full SHA-1: {commit!r}")
    path = "/".join(segments(source["path"], "path"))
    url = f"https://{host}/{repository}.git"
    repo = os.path.join(work, "repo.git")
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")
    token = os.environ.get("GH_TOKEN")
    if token:
        # The token as a header from the environment: never in a command line or on disk.
        basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
        env.update(
            GIT_CONFIG_COUNT="2",
            GIT_CONFIG_KEY_0=f"http.https://{host}/.extraheader",
            GIT_CONFIG_VALUE_0=f"Authorization: Basic {basic}",
            GIT_CONFIG_KEY_1="credential.helper",
            GIT_CONFIG_VALUE_1="",
        )
    # A bare repository with no hooks, read with cat-file: nothing of the fetched tree runs. A
    # partial clone: the fetch takes the commit's trees, and cat-file fetches the blobs it reads.
    git = ["git", "-c", "core.hooksPath=/dev/null", "-c", "protocol.file.allow=never", "-C", repo]
    subprocess.run(["git", "init", "-q", "--bare", repo], check=True)
    subprocess.run(git + ["remote", "add", "origin", url], check=True)
    subprocess.run(git + ["config", "remote.origin.promisor", "true"], check=True)
    subprocess.run(git + ["config", "remote.origin.partialclonefilter", "blob:none"], check=True)
    subprocess.run(git + ["fetch", "-q", "--depth=1", "--no-tags", "--filter=blob:none", "origin", commit], check=True, env=env)
    out = {}
    for name in names:
        blob = subprocess.run(git + ["cat-file", "blob", f"{commit}:{path}/{name}"], check=True, stdout=subprocess.PIPE, env=env).stdout
        out[name] = blob
    return out


def fetch_url(source, names):
    base = source["base"]
    parsed = urllib.parse.urlparse(base)
    if parsed.scheme != "https" or parsed.hostname not in URL_HOSTS or not base.endswith("/"):
        raise RefsError(f"refusing url {base!r} (https, ending in /, on {', '.join(URL_HOSTS)})")
    out = {}
    for name in names:
        request = urllib.request.Request(base + name, headers={"User-Agent": "oni-moono-fetch-game-refs/1"})
        with urllib.request.urlopen(request, timeout=120) as response:
            final = urllib.parse.urlparse(response.geturl()).hostname
            if final not in URL_HOSTS:
                raise RefsError(f"{name}: redirected to unexpected host {final!r}")
            out[name] = response.read()
    return out


def fetch_local(source, names):
    path = source["path"]
    folder = path if os.path.isabs(path) else os.path.join(ROOT, path)
    out = {}
    for name in names:
        with open(os.path.join(folder, name), "rb") as f:
            out[name] = f.read()
    return out


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("out", nargs="?")
    parser.add_argument("--build", help="the game build (default: the manifest's default_build)")
    parser.add_argument("--manifest", default=os.path.join(ROOT, "ci", "game-refs.json"))
    parser.add_argument("--reuse", action="store_true", help="fetch nothing when OUT already holds every file with its hash (a restored cache)")
    parser.add_argument("--print-default-build", action="store_true")
    args = parser.parse_args(argv)
    with open(args.manifest, encoding="utf-8") as f:
        manifest = json.load(f)
    if manifest.get("schema") != 1:
        raise RefsError(f"{args.manifest}: unknown schema {manifest.get('schema')!r}")
    build = args.build or manifest["default_build"]
    if args.print_default_build:
        print(build)
        return 0
    if not args.out:
        parser.error("OUT is required")
    source, files = check_manifest(manifest, build)
    names = sorted(files)
    managed = os.path.join(args.out, "OxygenNotIncluded_Data", "Managed")
    if args.reuse and os.path.isdir(managed) and sorted(os.listdir(managed)) == names and all(sha256_file(os.path.join(managed, n)) == files[n] for n in names):
        print(f"game refs {build}: {len(names)} files already in {managed}, verified")
        return 0
    with tempfile.TemporaryDirectory() as work:
        kind = source.get("kind")
        if kind == "git":
            blobs = fetch_git(source, names, work)
        elif kind == "url":
            blobs = fetch_url(source, names)
        elif kind == "local":
            blobs = fetch_local(source, names)
        else:
            raise RefsError(f"build {build}: unknown source kind {kind!r}")
        bad = [f"{n}: sha256 {hashlib.sha256(blobs[n]).hexdigest()}, expected {files[n]}" for n in names if hashlib.sha256(blobs[n]).hexdigest() != files[n]]
        if bad:
            raise RefsError("hash mismatch, nothing written:\n  " + "\n  ".join(bad))
        if os.path.isdir(managed):
            shutil.rmtree(managed)
        os.makedirs(managed)
        for name in names:
            # Data files only: never executable.
            with open(os.path.join(managed, name), "wb") as f:
                f.write(blobs[name])
            os.chmod(os.path.join(managed, name), 0o644)
    print(f"game refs {build}: {len(names)} files verified into {managed}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except (RefsError, subprocess.CalledProcessError, OSError, KeyError, ValueError) as e:
        print(f"fetch-game-refs: {e}", file=sys.stderr)
        sys.exit(1)
