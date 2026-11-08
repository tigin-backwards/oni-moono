# Releases

How Moono's releases are built, how to check one, and how to add a new game build. The short
version for making a release is in [DEVELOPMENT.md](DEVELOPMENT.md#releases).

## How a release is built

Releases are built by GitHub Actions on GitHub-hosted runners, from the public source at one
commit of `master` (or `u60` for a U60 beta):

| Workflow | What it does |
|---|---|
| `.github/workflows/build.yml` | On every push to `master` and `u60` (and when run by hand): the Linux archive on Ubuntu, the Windows archive natively on Windows (MSVC builds `OniMoono.exe`), and the unit tests that need no game. Both archives come from `scripts/release.sh` (`MOONO_PLATFORMS` picks one), so they are the archives a local `release.sh` makes. |
| `.github/workflows/release.yml` | Run by hand with the release's tag. Calls `build.yml` without caches, takes the archives' SHA-256 digests, has `attest.yml` sign their provenance, and creates a **draft** release with them. It never publishes. |
| `.github/workflows/attest.yml` | Signs SLSA build provenance for the digests it is given, and does nothing else. |

The release is published by hand: write its notes in the draft on GitHub and press Publish.
Publishing creates the tag at the built commit and, as the repository has immutable releases on,
locks the release: its assets and tag cannot change afterwards, and GitHub adds a release
attestation of its own. The draft's notes start with where it was built and how to verify it;
keep that paragraph.

`dry_run` (on by default) runs everything up to the draft, then downloads the draft's assets
again, verifies them, and deletes the draft. Its draft is tagged `vtest-<tag>-<run id>` and
titled `TEST …`; a draft's tag is only made when it is published, and the workflow checks that
none is left. Dry runs do sign provenance, so their attestations stay in the repository's
attestation list and in the public Sigstore log; keep them few.

### SLSA Build Level 3

- The build runs on GitHub-hosted runners, from a pinned commit, with no caches written by other
  runs.
- The build jobs have only `contents: read`. The signing identity (`id-token: write`,
  `attestations: write`) exists only in `attest.yml`'s job, a separate reusable workflow on a
  fresh runner that receives nothing but the archive names and digests (checked against the
  expected names before signing). A compromised build step cannot sign anything, and the
  provenance names `attest.yml` as its signer, which is what verification checks.
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

Each archive has SLSA provenance signed by `attest.yml`:

```sh
gh attestation verify oni-moono-YYYY.MM.DD-windows-x64.zip -R tigin-backwards/oni-moono \
  --signer-workflow tigin-backwards/oni-moono/.github/workflows/attest.yml
```

It shows the commit and workflow run the archive was built by; `--source-ref refs/heads/master`
(or `refs/heads/u60`) also checks the branch. A published release also has GitHub's immutable
release attestation:

```sh
gh release verify vYYYY.MM.DD.NN -R tigin-backwards/oni-moono
gh release verify-asset vYYYY.MM.DD.NN oni-moono-YYYY.MM.DD-windows-x64.zip -R tigin-backwards/oni-moono
```

Releases before the first one built by Actions (2026.11.06.02 and older) were built locally and
have neither.

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

The builds aim at deterministic output but are not reproducible bit for bit yet:

- **Managed assemblies** are deterministic (`Deterministic`, `ContinuousIntegrationBuild` in CI,
  `DeterministicSourcePaths`, so the symbols name sources as `/_/...`). The same commit built
  against the same reference assemblies with the same SDK gives the same files. Built against a
  real game install instead (a local `release.sh`) they differ only in their IDs (MVID, PE
  timestamp, PDB ID), since those hash the referenced assemblies; the IL is the same.
- **The SDK and runtime**: `global.json` allows later SDK patch releases, and the self-contained
  player carries the runtime of whichever SDK built it.
- **OniMoono.exe** is built with `/Brepro` (no timestamps), but with the MSVC toolset of the
  runner image, which changes as GitHub updates it; it embeds the commit.
- **Restarter.exe** built on Windows carries the version resources the SDK adds there; a WSL
  build's has none.
- **The archives** record file times (and the tar.gz its owner and gzip time), so two builds of
  the same files give different archives.
