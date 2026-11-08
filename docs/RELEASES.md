# Releases

How Moono's releases are built, how to check one, and how to add a new game build. The short
version for making a release is in [DEVELOPMENT.md](DEVELOPMENT.md#releases).

## How a release is built

Releases are built by GitHub Actions on GitHub-hosted runners, from the public source at one
commit of `master` (or `u60` for a U60 beta):

| Workflow | What it does |
|---|---|
| `.github/workflows/build.yml` | On every push to `master` and `u60` (and when run by hand): the Linux archive on Ubuntu, the Windows archive natively on Windows (MSVC builds `OniMoono.exe`), and the unit tests that need no game. Both archives come from `scripts/release.sh` (`MOONO_PLATFORMS` picks one), so they are the archives a local `release.sh` makes. |
| `.github/workflows/release.yml` | Run by hand with the release's tag. Calls `build.yml` without caches, checks each archive against its manifest, has `attest.yml` sign provenance for the archives, the manifests and every file in the archives, and creates a **draft** release with the archives and manifests. It never publishes. |
| `.github/workflows/attest.yml` | Signs SLSA build provenance for the digests it is given, and does nothing else. |

Each archive comes with a manifest, `oni-moono-<version>-windows-x64.SHA256SUMS` or
`…-linux-x64.SHA256SUMS`: the SHA-256 of every file in the archive, with paths relative to its
`oni-moono` folder, in `sha256sum`'s format. `release.sh` writes it from the staged folder just
before archiving.

The release is published by hand: write its notes in the draft on GitHub and press Publish.
Publishing creates the tag at the built commit and, as the repository has immutable releases on,
locks the release: its assets and tag cannot change afterwards, and GitHub adds a release
attestation of its own. The draft's notes start with where it was built and how to verify it;
keep that paragraph.

`dry_run` (on by default) runs everything up to the draft, then downloads the draft's assets
again, verifies them and a few single files from the extracted Windows archive (and checks that
a changed file fails), and deletes the draft. Its draft is tagged `vtest-<tag>-<run id>` and
titled `TEST …`; a draft's tag is only made when it is published, and the workflow checks that
none is left. Dry runs do sign provenance, so their attestations stay in the repository's
attestation list and in the public Sigstore log; keep them few.

### SLSA Build Level 3

- The build runs on GitHub-hosted runners, from a pinned commit, with no caches written by other
  runs.
- The build jobs have only `contents: read`. The signing identity (`id-token: write`,
  `attestations: write`) exists only in `attest.yml`'s job, a separate reusable workflow on a
  fresh runner that receives nothing but names and digests: the archives' and manifests', and
  the manifests' lines. Before signing it checks the archive and manifest names, rejects any
  path that is not a plain relative one, and checks that the lines hash to the manifests'
  digests, so each file it signs is one a manifest lists. It signs one attestation per archive
  (the archive, its manifest and its files: about 290 subjects, where the action takes up to
  1024). A compromised build step cannot sign anything, and the provenance names `attest.yml`
  as its signer, which is what verification checks.
- Every action is pinned to a full commit SHA. Workflows run only on pushes to `master`/`u60`
  and by hand, never on pull requests, so a fork's code never sees the secret.

### The game's assemblies

The build compiles against the game's assemblies (`OniMoono.GameTweaks` against Assembly-CSharp
and the Unity modules it uses, the host against 0Harmony, Ionic.Zip and FMODUnity), but nothing
of the game goes into the archives. CI gets them as **reference assemblies**: metadata only,
every method body removed, made with JetBrains Refasmer by `scripts/make-refs.sh` and kept in the
private repository `tigin-backwards/oni-moono-refs`, one folder per game build with a
`SHA256SUMS`, and a LICENSE.txt naming each library's licence (the game's own code is used under
Klei's mod guidelines).

`ci/game-refs.json` lists each game build the branch supports, where its files come from and
the SHA-256 of every file; `default_build` is the one the release builds against (U59 744825 on
`master`, U60 762230 on `u60`). `scripts/fetch-game-refs.py OUT` fetches one build into
`OUT/OxygenNotIncluded_Data/Managed`, a folder that works as `ONI_GAME_DIR`. It checks every file
against the manifest before writing any, refuses hosts other than GitHub's, and never runs
anything it fetched (from git it reads blobs out of a bare repository, with hooks off). A source
is `git` (repository, commit and folder), `url` (a base URL on an allowed host) or `local` (a
folder), so the files can move without touching the scripts.

Actions reads the refs repository with a read-only deploy key, held in the repository secret
`GAME_REFS_DEPLOY_KEY` and loaded into an `ssh-agent` only for the fetch (GitHub's host keys are
pinned in `ci/github-known-hosts`).

### Caches

Pushes restore and save the NuGet packages (keyed by `global.json`, `Directory.Packages.props`
and the project files) and the reference assemblies (keyed by build and `ci/game-refs.json`; a
restored copy is checked file by file like a fetch). Release builds use no cache at all, so
nothing another run wrote reaches a release.

### Unit tests in CI

`scripts/ci-unit-tests.sh` runs the unit test suites against the reference assemblies, without
the classes `ci/tests-need-game.txt` lists: those need the game's data, its runnable Harmony,
Ionic.Zip or Newtonsoft.Json, or a display. `scripts/check.sh` still runs every test locally. A
new test class that needs the game goes into that list.

## Verifying a release

Each archive, each manifest and every file in the archives has SLSA provenance signed by
`attest.yml`:

```sh
gh attestation verify oni-moono-YYYY.MM.DD-windows-x64.zip -R tigin-backwards/oni-moono \
  --signer-workflow tigin-backwards/oni-moono/.github/workflows/attest.yml
```

A single file works the same way, as extracted or as installed (setup copies the player's files
unchanged), so an installed player can be checked without its archive:

```sh
gh attestation verify oni-moono/player/OniMoono.exe -R tigin-backwards/oni-moono \
  --signer-workflow tigin-backwards/oni-moono/.github/workflows/attest.yml
```

To check a whole folder at once, verify its manifest and run `sha256sum -c` against it in the
`oni-moono` folder:

```sh
gh attestation verify oni-moono-YYYY.MM.DD-linux-x64.SHA256SUMS -R tigin-backwards/oni-moono \
  --signer-workflow tigin-backwards/oni-moono/.github/workflows/attest.yml
(cd oni-moono && sha256sum -c ../oni-moono-YYYY.MM.DD-linux-x64.SHA256SUMS)
```

Verification shows the commit and workflow run a file was built by; `--source-ref
refs/heads/master` (or `refs/heads/u60`) also checks the branch. A published release also has GitHub's immutable
release attestation:

```sh
gh release verify vYYYY.MM.DD.NN -R tigin-backwards/oni-moono
gh release verify-asset vYYYY.MM.DD.NN oni-moono-YYYY.MM.DD-windows-x64.zip -R tigin-backwards/oni-moono
```

Releases before the first one built by Actions (2026.11.06.02 and older) were built locally and
have neither. Releases before the manifests were added have provenance for their archives only.

## Adding a new game build

When Klei ships a new build the release should compile against:

1. Make its reference assemblies from the install (the Linux build; DepotDownloader can fetch an
   old one):

   ```sh
   scripts/make-refs.sh ~/projects/oni-linux-NNNNNN NNNNNN out/refs   # out/refs/NNNNNN, and the manifest entry
   ```

2. Add `out/refs/NNNNNN/` to a checkout of `tigin-backwards/oni-moono-refs` as `NNNNNN/`, mention
   it in its README, commit and push.
3. Add the printed entry to `ci/game-refs.json` under `builds`, with `commit` set to that push's
   commit, and set `default_build` if the release should now build against it. Commit and push;
   the next build fetches it.

If the build needs another file from the game's `Managed` folder, add it to the list in
`make-refs.sh` and make every build's refs again.

## Reproducibility

How close the builds are to reproducible, as checked on the first dry run (2026-11-08):

- **The Linux archive's files are reproducible.** Built locally from the same commit, with the
  same tag, against the same reference assemblies and in CI mode, every one of its 291 files is
  byte for byte the one GitHub built (the fetch needs read access to the refs repository):

  ```sh
  python3 -I scripts/fetch-game-refs.py out/game-refs
  ContinuousIntegrationBuild=true MOONO_RELEASE=vYYYY.MM.DD.NN MOONO_PLATFORMS=linux \
    ONI_GAME_DIR=out/game-refs scripts/release.sh "$(scripts/release-version.sh)"
  ```

  This needs `global.json`'s exact SDK and nothing newer: CI installs that SDK alone, since
  the player carries the SDK's runtime and the symbols record the runtime the compiler ran on.
  Managed builds are deterministic (`Deterministic`, `ContinuousIntegrationBuild`,
  `DeterministicSourcePaths`: the symbols name sources as `/_/...`). Built against a real game
  install instead of the reference assemblies, they differ only in their IDs (MVID, PE
  timestamp, PDB ID), which hash the referenced assemblies; the IL is the same.
- **The Windows archive** has the same files, but a WSL build (`release.sh` as it was run before)
  is not the same as GitHub's Windows one: on Windows the SDK writes its generated files with
  CRLF and records the native files' versions in `OniMoono.deps.json`, and `OniMoono.exe` comes
  from the runner image's MSVC toolset, which changes as GitHub updates the image. Reproducing it
  takes a Windows machine with the same toolset. `OniMoono.exe` is built with `/Brepro`, so it
  holds no timestamps; it embeds the commit.
- **The archives themselves** record file times (the tar.gz also its owner and gzip time), so
  two builds of the same files give different archives. Compare their contents.
