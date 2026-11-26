#!/usr/bin/env python3
"""Lays out Moono's Workshop item from a release's assets (docs/INSTALL-MODES.md).

    python3 -I scripts/workshop-item.py --assets DIR --tag vYYYY.MM.DD.NN --loader MoonoLoader.dll [--notes RELEASES.json] OUT

DIR holds the release's assets as downloaded (and verified: .github/workflows/workshop.yml
checks their attestations first): its Windows zip, Linux tarball and their SHA256SUMS manifests.
Each archive is extracted with every file checked against its manifest, and laid out as a mod's
one version:

    OUT/                         the release, for the current game, U60 (minimumSupportedBuild 763796)
      mod_info.yaml  moono.json  oni-moono-*-windows-x64.SHA256SUMS  oni-moono-*-linux-x64.SHA256SUMS
      windows-x64/   linux-x64/  the archives' oni-moono folders
      OniMoono.exe  OniMoono.sh  verify.ps1  verify.sh  README.md   (root only)
      MoonoLoader.dll            the loader mod (src/MoonoLoader, built by workshop.yml; root only)
      release-notes.json         the notes of the releases up to this one (root only; --notes)
      SHA256SUMS                 every other file of OUT (root only)
      attestation/               added by workshop.yml once SHA256SUMS is attested: its Sigstore bundle

RELEASES.json is GitHub's releases list (workshop.yml fetches it once the release is published):
release-notes.json keeps the NOTES_KEPT newest releases up to the item's, drafts left out, with
the fields Moono's "Updated" badge reads (tag, title, pre-release, page, notes in Markdown).

The item has one version, master's release: no archived_versions (the original U59 game lists a
folder item as failed to download, so U59 players install Moono from a GitHub release, the u59
line's last). A U60 beta build (an archive named -u60, from before the U60 cut-over) is refused.

Of what the original game loads, a version's top has only the loader at the root: no other DLL,
no .po or content folder. Nothing is run from the archives.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tarfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
STATIC = os.path.join(HERE, "..", "ci", "workshop")
MINIMUM_BUILD = 763796  # U60's release build; its hotfixes 764025 and 764961 (764961 is ci/game-refs.json's default_build) run the same Moono
PLATFORMS = {"windows-x64": ".zip", "linux-x64": ".tar.gz"}
TAG = re.compile(r"v([0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{2})")
COMPONENT = re.compile(r"[A-Za-z0-9._+-]+")
LOADER = "MoonoLoader.dll"  # the loader mod (src/MoonoLoader), the root's one DLL
NOTES = "release-notes.json"
NOTES_KEPT = 30


def fail(message):
    sys.exit(f"workshop-item: {message}")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def plain(path):
    return all(COMPONENT.fullmatch(c) and c.strip(".") for c in path.split("/"))


def read_manifest(path):
    entries = {}
    with open(path, "rb") as f:
        for raw in f.read().decode("utf-8").splitlines():
            m = re.fullmatch(r"([0-9a-f]{64})  (.+)", raw)
            if not m or not plain(m.group(2)) or m.group(2) in entries:
                fail(f"{path}: unexpected line {raw!r}")
            entries[m.group(2)] = m.group(1)
    if not entries:
        fail(f"{path} is empty")
    return entries


def members(archive):
    """(path inside oni-moono/, bytes) for every file; links, devices and other paths refused."""
    if archive.endswith(".zip"):
        with zipfile.ZipFile(archive) as z:
            for info in z.infolist():
                if info.is_dir():
                    continue
                if (info.external_attr >> 16) & 0o170000 not in (0, 0o100000):
                    fail(f"{archive}: {info.filename} is not a plain file")
                yield info.filename, z.read(info)
    else:
        with tarfile.open(archive, "r:gz") as t:
            for info in t:
                if info.isdir():
                    continue
                if not info.isfile():
                    fail(f"{archive}: {info.name} is not a plain file")
                yield info.name, t.extractfile(info).read(), info.mode


def extract(archive, manifest, dest):
    """The archive's oni-moono folder into dest, every file as the manifest lists it."""
    expected = read_manifest(manifest)
    seen = set()
    for item in members(archive):
        name, data = item[0], item[1]
        if not name.startswith("oni-moono/") or not plain(name):
            fail(f"{archive}: {name} is outside its oni-moono folder")
        path = name[len("oni-moono/") :]
        if expected.get(path) != sha256(data):
            fail(f"{archive}: {path} is not as {os.path.basename(manifest)} lists it")
        target = os.path.join(dest, *path.split("/"))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "wb") as f:
            f.write(data)
        if len(item) > 2 and item[2] & 0o111:
            os.chmod(target, 0o755)
        seen.add(path)
    if seen != set(expected):
        fail(f"{archive}: missing {sorted(set(expected) - seen)[:5]}")
    return len(seen)


def version_folder(assets, tag, out):
    m = TAG.fullmatch(tag)
    if not m:
        fail(f"the tag must be vYYYY.MM.DD.NN, not {tag!r}")
    version = m.group(1)
    os.makedirs(out, exist_ok=True)
    platforms = {}
    for platform, ext in PLATFORMS.items():
        found = [f for f in os.listdir(assets) if f.startswith("oni-moono-") and f.endswith(f"-{platform}{ext}")]
        manifests = [f for f in os.listdir(assets) if f.startswith("oni-moono-") and f.endswith(f"-{platform}.SHA256SUMS")]
        if len(found) != 1 or len(manifests) != 1 or found[0][: -len(ext)] != manifests[0][: -len(".SHA256SUMS")]:
            fail(f"{assets}: expected one {platform} archive and its manifest, found {found + manifests}")
        if "-u60-" in found[0]:
            fail(f"{found[0]} is a U60 beta build, not a master release")
        # Named by the tag's version (scripts/release-version.sh), or by its date alone for
        # releases up to v2026.11.20.04.
        if found[0] not in (f"oni-moono-{v}-{platform}{ext}" for v in (version, version[:10])):
            fail(f"{found[0]} is not named by {tag}'s version")
        n = extract(os.path.join(assets, found[0]), os.path.join(assets, manifests[0]), os.path.join(out, platform))
        shutil.copyfile(os.path.join(assets, manifests[0]), os.path.join(out, manifests[0]))
        platforms[platform] = {"folder": platform, "manifest": manifests[0], "archive": found[0]}
        print(f"{version} {platform}: {n} files from {found[0]}")
    with open(os.path.join(out, "mod_info.yaml"), "w", newline="\n") as f:
        f.write(f"# Moono {version}, for the game's U60 update; the game loads only its loader mod, MoonoLoader.dll, from here.\n")
        f.write(f"minimumSupportedBuild: {MINIMUM_BUILD}\nAPIVersion: 2\nrequiredDlcIds: []\nforbiddenDlcIds: []\nversion: \"{version}\"\n")
    info = {"workshopItem": "oni-moono", "version": version, "tag": tag, "branch": "master", "repository": "tigin-backwards/oni-moono", "platforms": platforms}
    with open(os.path.join(out, "moono.json"), "w", newline="\n") as f:
        json.dump(info, f, indent=2)
        f.write("\n")
    return version


def release_notes(path, version):
    """The releases list's newest NOTES_KEPT releases up to `version`, in GitHub's form."""
    with open(path, encoding="utf-8") as f:
        releases = json.load(f)
    if not isinstance(releases, list):
        fail(f"{path} is not a releases list")
    kept = []
    for r in releases:
        if not isinstance(r, dict) or r.get("draft"):
            continue
        tag, url = r.get("tag_name"), r.get("html_url")
        m = TAG.fullmatch(tag) if isinstance(tag, str) else None
        if not m or not isinstance(url, str) or m.group(1) > version:
            continue
        kept.append(
            {
                "tag_name": tag,
                "name": str(r.get("name") or tag),
                "prerelease": bool(r.get("prerelease")),
                "html_url": url,
                "body": str(r.get("body") or ""),
            }
        )
    kept.sort(key=lambda r: r["tag_name"], reverse=True)
    return kept[:NOTES_KEPT]


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--assets", required=True, help="the release's archives and manifests")
    parser.add_argument("--tag", required=True, help="the release's tag")
    parser.add_argument("--loader", required=True, help="the loader mod's MoonoLoader.dll, put at the root")
    parser.add_argument("--notes", help="GitHub's releases list, for the root's release-notes.json")
    parser.add_argument("out")
    args = parser.parse_args()
    if os.path.exists(args.out):
        fail(f"{args.out} exists already")
    final, args.out = args.out, args.out + ".partial"
    shutil.rmtree(args.out, ignore_errors=True)
    version = version_folder(args.assets, args.tag, args.out)
    if args.notes:
        notes = release_notes(args.notes, version)
        with open(os.path.join(args.out, NOTES), "w", encoding="utf-8", newline="\n") as f:
            json.dump(notes, f, indent=1, ensure_ascii=False)
            f.write("\n")
        print(f"{NOTES}: {len(notes)} releases, newest {notes[0]['tag_name'] if notes else 'none'}")
    shutil.copyfile(os.path.join(args.out, "windows-x64", "player", "OniMoono.exe"), os.path.join(args.out, "OniMoono.exe"))
    for name in ("OniMoono.sh", "verify.sh", "verify.ps1", "README.md"):
        shutil.copyfile(os.path.join(STATIC, name), os.path.join(args.out, name))
    for name in ("OniMoono.sh", "verify.sh"):
        os.chmod(os.path.join(args.out, name), 0o755)
    if os.path.basename(args.loader) != LOADER:
        fail(f"the loader is {LOADER}, not {args.loader}")
    shutil.copyfile(args.loader, os.path.join(args.out, LOADER))
    # The game looks only at a version's top: nothing there may be content but the loader.
    for name in os.listdir(args.out):
        lower = name.lower()
        if name == LOADER:
            continue
        if lower.endswith((".dll", ".po")) or (
            os.path.isdir(os.path.join(args.out, name)) and lower in ("strings", "codex", "elements", "templates", "worldgen", "buildingfacades", "anim")
        ):
            fail(f"{args.out}/{name} would be content the game loads")
    paths = sorted(os.path.relpath(os.path.join(d, f), args.out).replace(os.sep, "/") for d, _, files in os.walk(args.out) for f in files)
    with open(os.path.join(args.out, "SHA256SUMS"), "wb") as out:
        for p in paths:
            with open(os.path.join(args.out, p), "rb") as f:
                out.write(f"{sha256(f.read())}  {p}\n".encode())
    os.rename(args.out, final)
    print(f"{final}: {len(paths)} files and SHA256SUMS")


if __name__ == "__main__":
    main()
