# Releases

How Moono's releases are built, how to check one, and how to add a new game build. The short
version for making a release is in [DEVELOPMENT.md](DEVELOPMENT.md#releases).

## How a release is built

Releases are built by GitHub Actions on GitHub-hosted runners, from the public source at one
commit of `master`, for the game's current update (U60 since the
[U60 cut-over](#u60-cut-over-2026-11-19)):

| Workflow | What it does |
|---|---|
| `.github/workflows/build.yml` | On every push to `master` (and when run by hand): the Linux archive on Ubuntu, the Windows archive natively on Windows (MSVC builds `OniMoono.exe`), and the unit tests that need no game. Both archives come from `scripts/release.sh` (`MOONO_PLATFORMS` picks one), so they are the archives a local `release.sh` makes. |
| `.github/workflows/release.yml` | Run by hand with the release's tag. Calls `build.yml` without caches, checks each archive against its manifest, has `attest.yml` sign provenance for the archives, the manifests and every file in the archives, and creates a **draft** release with the archives and manifests. It never publishes. |
| `.github/workflows/attest.yml` | Signs SLSA build provenance for the digests it is given, and does nothing else. |
| `.github/workflows/workshop.yml` | Run by hand with a release tag (or, for a dry run, the release.yml run that built it). Downloads the release's assets and verifies their provenance from master, lays out the Workshop item (that release alone) (`scripts/workshop-item.py`, [INSTALL-MODES.md](INSTALL-MODES.md#workshop-item)), has `attest.yml` attest its `SHA256SUMS` and every file in it, puts the bundle for `SHA256SUMS` in the item (`attestation/`), checks it with gh, and keeps it as the `workshop-item` artifact. Nothing is uploaded to Steam. |
| `.github/workflows/workshop-upload.yml` | Run by hand with a `workshop.yml` run's number. Checks that run's item as `verify.sh` does and that its attestation is that run's, writes the VDF, and (mode `upload`, in the `steam-workshop` environment, which alone holds the Steam secrets) signs in with a cached steamcmd session and uploads it to the Steam Workshop. [WORKSHOP-UPLOAD.md](WORKSHOP-UPLOAD.md). |

`<version>` in the archives' names is the release tag without its `v`:
`oni-moono-2026.11.20.05-…` for `v2026.11.20.05`, so each release of a day has names of its own.
Releases up to `v2026.11.20.04` were named by the tag's date alone (`oni-moono-2026.11.10-…` for
`v2026.11.10.01`, with `-u60` after it for the U60 beta's pre-releases); they verify the same
way, since attestations go by digest, not name. A build of a plain push is named by its commit's
date (`scripts/release-version.sh`).

Each archive comes with a manifest, `oni-moono-<version>-windows-x64.SHA256SUMS` or
`…-linux-x64.SHA256SUMS`. It lists the SHA-256 of every file in the archive, with paths relative
to its `oni-moono` folder, in `sha256sum`'s format. `release.sh` writes it from the staged
folder just before archiving.

The release is published by hand: write its notes in the draft on GitHub and press Publish.
Publishing:

- Creates the tag at the built commit.
- Locks the release, as the repository has immutable releases on. Its assets and tag cannot
  change afterwards.
- Makes GitHub add a release attestation of its own.

The draft's notes start with where it was built and how to verify it. Keep that paragraph.

`dry_run` (on by default) runs everything up to the draft, then:

1. Downloads the draft's assets again.
2. Verifies them and a few single files from the extracted Windows archive, and checks that a
   changed file fails.
3. Deletes the draft.

Its draft is tagged `vtest-<tag>-<run id>` and titled `TEST …`. A draft's tag is only made when
it is published, and the workflow checks that none is left. Dry runs do sign provenance, so
their attestations stay in the repository's attestation list and in the public Sigstore log.
Keep them few.

### SLSA Build Level 3

- The build runs on GitHub-hosted runners, from a pinned commit, with no caches written by other
  runs.
- The build jobs have only `contents: read`. The signing identity (`id-token: write`,
  `attestations: write`) exists only in `attest.yml`'s job, a separate reusable workflow on a
  fresh runner. That job:
  - Receives nothing but names and digests: the archives' and manifests', and the manifests'
    lines.
  - Before signing, checks the archive and manifest names, rejects any path that is not a plain
    relative one, and checks that the lines hash to the manifests' digests. So each file it
    signs is one a manifest lists.
  - Signs one attestation per archive (the archive, its manifest and its files: about 290
    subjects, where the action takes up to 1024).

  A compromised build step cannot sign anything, and the provenance names `attest.yml` as its
  signer, which is what verification checks.
- Every action is pinned to a full commit SHA. Workflows run only on pushes to `master`
  and by hand, never on pull requests, so a fork's code never sees the secret.

### The game's assemblies

The build compiles against the game's assemblies, but nothing of the game goes into the
archives:

- `OniMoono.GameTweaks` compiles against Assembly-CSharp and the Unity modules it uses.
- The host compiles against 0Harmony, Ionic.Zip and FMODUnity.

CI gets them as **reference assemblies**: metadata only, every method body removed. Private
members are kept, because `OniMoono.GameTweaks` compiles against publicized copies and uses them
directly. `scripts/make-refs.sh` makes them with JetBrains Refasmer. They are kept in the
private repository `tigin-backwards/oni-moono-refs`: one folder per game build with a
`SHA256SUMS`, and a LICENSE.txt naming each library's licence (the game's own code is used
under Klei's mod guidelines).

`ci/game-refs.json` lists each game build the branch supports, where its files come from and
the SHA-256 of every file. `default_build` is the one the release builds against (U60 764025;
the frozen `u59` branch has U59 744825).

`scripts/fetch-game-refs.py OUT` fetches one build into `OUT/OxygenNotIncluded_Data/Managed`, a
folder that works as `ONI_GAME_DIR`. It:

- Checks every file against the manifest before writing any.
- Refuses hosts other than GitHub's.
- Never runs anything it fetched (from git it reads blobs out of a bare repository, with hooks
  off).

A source is `git` (repository, commit and folder), `url` (a base URL on an allowed host) or
`local` (a folder), so the files can move without touching the scripts.

Actions reads the refs repository with a GitHub App, oni-moono-refs-reader, installed on that
repository alone with read-only Contents. Its ID is the repository variable `REFS_APP_ID` and its
private key the secret `REFS_APP_KEY`. For each fetch, `.github/actions/game-refs` mints a
short-lived token with `actions/create-github-app-token`, and the fetch passes it to git in its
environment only. The fetch is a partial clone: it takes the commit's trees and only the blobs
it reads.

### Caches

Pushes restore and save two caches:

- The NuGet packages, keyed by `global.json`, `Directory.Packages.props` and the project files.
- The reference assemblies, keyed by build and `ci/game-refs.json`. A restored copy is checked
  file by file like a fetch.

Release builds use no cache at all, so nothing another run wrote reaches a release.

### Unit tests in CI

`scripts/ci-unit-tests.sh` runs every unit test suite against the reference assemblies, with
`ONI_TESTS_NO_GAME=1`. A test that needs the game's data, or its runnable Harmony, Ionic.Zip or
Newtonsoft.Json, skips itself without them. The skip's reason starts `needs game data:` or
`needs game assembly:`, and the run ends with the skips listed by reason
(`scripts/test-skips.py`). `scripts/check.sh` runs the same suites with the game and fails on any
such skip, naming the tests.

A new test gets the skip by reaching the game through `tests/Shared/GameInstall.cs`:

- For the data: `GameInstall.DataDir`, or the helpers built on it, such as
  `TmpTestSupport.LoadGameData`.
- For one of the game's assemblies: `[NeedsGameAssembly("0Harmony")]` on the test.

In a run without the game, a test that misses them still skips. A failure on a missing file
under the game folder, or on loading a reference assembly, is reported as one of those skips
(`NoGameSkips`, `ONI_TESTS_NO_GAME=1` only). Any other failure fails the build.

## Writing the release notes

The workflow's draft holds only the build line and the install paragraph. The notes themselves
are written into the draft before it is published.

**Checklist**
1. List the issues the release fixes and their reporters:
   `gh issue list --label "fixed in next release" --json number,title,author --jq '.[]|"#\(.number) @\(.author.login) \(.title)"'`.
   Check each against `git log PREVIOUS_TAG..` (fix commits name their issue in the body).
2. List the changes since the previous release: `git log --oneline PREVIOUS_TAG..`. Skip what
   players don't see (CI, tests, docs, lint, internal tooling).
3. Write the notes into the draft.
4. Publish it as Latest, and verify one asset with `gh release verify-asset`.

**Structure**
- No opener: releases are for the normal game. (The first release after the U60 cut-over says it
  in a section, below.)
- Up to three sections, in this order, each left out when empty: **New**, **Faster**,
  **Fixes**.
- Then the workflow's build line and install paragraph, unchanged.

**Lines**
- One bullet per change, in plain, concise prose: what the player notices, not how it was done.
  A Moono Tweaks option is named in bold with its default, e.g. "(Moono Tweaks, on by default)".
- A line that fixes or answers an issue ends with "Reported in #N by @reporter." The @mention is
  intended: it tells the reporter.
- Speed and memory: give memory and load-time figures where there are some (e.g. "about 0.6 to
  1 GB less memory"), never fps numbers or tables.
- Describe test saves by kind ("a late-game colony", "a 200-Duplicant colony"), never by our
  names for them.
- Say "as in the original" where a fix matches the original game's behaviour.

Releases are immutable, but their notes can still be edited after publishing. The Workshop item's
`release-notes.json` (the main menu's "Updated" badge, [TWEAKS.md](TWEAKS.md#the-updated-badge))
takes the notes as they are when `workshop.yml` runs, so finish them before that; GitHub
installs read them live.

## Verifying a release

Each archive, each manifest and every file in the archives has SLSA provenance signed by
`attest.yml`:

```sh
gh attestation verify oni-moono-YYYY.MM.DD.NN-windows-x64.zip -R tigin-backwards/oni-moono \
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
gh attestation verify oni-moono-YYYY.MM.DD.NN-linux-x64.SHA256SUMS -R tigin-backwards/oni-moono \
  --signer-workflow tigin-backwards/oni-moono/.github/workflows/attest.yml
(cd oni-moono && sha256sum -c ../oni-moono-YYYY.MM.DD.NN-linux-x64.SHA256SUMS)
```

Verification shows the commit and workflow run that built a file. `--source-ref
refs/heads/master` also checks the branch (`refs/heads/u60` for the U60 beta's pre-releases;
U59's releases were built from `master` before the cut-over). A published release also has
GitHub's immutable release attestation:

```sh
gh release verify vYYYY.MM.DD.NN -R tigin-backwards/oni-moono
gh release verify-asset vYYYY.MM.DD.NN oni-moono-YYYY.MM.DD.NN-windows-x64.zip -R tigin-backwards/oni-moono
```

Releases before the first one built by Actions (2026.11.06.02 and older) were built locally and
have neither. Releases before the manifests were added have provenance for their archives only.

### Verifying the Workshop item

**Moono checks no signature on the item.** It checks each player file against its release's
manifest as it installs it. That catches a download Steam hasn't finished, not a forged item:
like any mod, the item is trusted as Steam delivers it ([INSTALL-MODES.md](INSTALL-MODES.md#what-the-installer-verifies)).

For an install you can be sure of, install from a GitHub release (setup,
[Verifying a release](#verifying-a-release)). The releases are the root of trust. Don't follow
advice in the item or its Workshop description to run anything from it: whoever controls the
item writes both.

**By hand,** check the item with `verify.sh` or `verify.ps1` from GitHub, not the copies inside the
item, which its uploader can change. They check:

1. Each release manifest, by its provenance from `release.yml` on master (u60 for an item laid
   out before the U60 cut-over), and every player file against it (the item has one version).
2. The item's `SHA256SUMS`, by its provenance from `workshop.yml` (looked up, and the bundle in
   `attestation/`), and every file against it.
3. That nothing else is in the folder.

They need the GitHub CLI:

```sh
gh api -H 'Accept: application/vnd.github.raw' repos/tigin-backwards/oni-moono/contents/ci/workshop/verify.sh > verify.sh
sh verify.sh path/to/the/item
```

```powershell
gh api -H 'Accept: application/vnd.github.raw' repos/tigin-backwards/oni-moono/contents/ci/workshop/verify.ps1 > verify.ps1
powershell -ExecutionPolicy Bypass -File verify.ps1 path\to\the\item
```

With gh alone: `--source-ref` pins the branch, and the workflow that called `attest.yml` must be
`workshop.yml` (the command prints it):

```sh
gh attestation verify SHA256SUMS --bundle attestation/SHA256SUMS.sigstore.json \
  -R tigin-backwards/oni-moono --signer-workflow tigin-backwards/oni-moono/.github/workflows/attest.yml \
  --source-ref refs/heads/master --format json --jq '.[].verificationResult.signature.certificate.buildConfigURI'
```

It must print `https://github.com/tigin-backwards/oni-moono/.github/workflows/workshop.yml@refs/heads/master`.
Single files verify the same way, for example `workshop-item/mod_info.yaml` in place of
`SHA256SUMS` (without `--bundle`).

### The Workshop item's description

The Workshop page's description is `ci/workshop/description.bbcode` (Steam's BBCode; `ITEM_ID`,
if present, stands for the item's ID).

- **Edit the file, never the Steam page.** Steam keeps no history of descriptions.
  `workshop-upload.yml` uploads master's copy with every item
  ([WORKSHOP-UPLOAD.md](WORKSHOP-UPLOAD.md)).
- **Escaping.** steamcmd reads the VDF raw. It does not unescape backslashes, so `\\` would show
  doubled, and a double quote can't be written at all (`scripts/workshop-upload.py` refuses
  one).
- **Images.** The images it shows are in `ci/workshop/images/`, linked by their raw GitHub URLs.
- **Preview.** The item's preview is `ci/workshop/preview.gif` (under 1 MB), uploaded only when
  a run asks for it.

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

## U60 cut-over (2026-11-19)

Klei made U60 the public game on 2026-11-19. Until then `master` was the U59 line (normal
releases, Latest) and `u60` the U60 beta (pre-releases titled "… (U60 Beta)", archives named
`-u60`, notes opening "Only for Oxygen Not Included's U60 public test"). At the cut-over:

- `master` was force-pushed to `u60`'s tip (fda7d70c1, U60 763796). `u60` was left at the same
  commit, unpushed since; whether it stays for the next beta is open.
- The U59 line is frozen on `u59` at 808ac53e9; nothing is pushed to it.
- The last U59 release is v2026.11.19.03 (from the old `master`); the last U60 pre-release is
  v2026.11.19.04. Builds on a U59 game point there.
- From then on every release is a normal one from `master`: tag `vYYYY.MM.DD.NN`, no suffix,
  Latest, no opener.

**What older builds see.** Their update check matches by the pre-release flag: U59 builds look
at normal releases, U60 beta builds at pre-releases only (`UpdateCheck.Newer`;
`UpdateCheckTests.TheCutOverReachesOldU59AndU60BuildsAndNewOnes`).

- Old U59 builds see the new normal releases, which are for U60. That is right for most of them,
  since their game updated too. A player who keeps U59 gets the start message that names
  2026.11.19.03.
- Old U60 builds see no normal release. One **bridging pre-release** reaches them. Publish it by
  hand once, right after the first normal U60 release (so `releases/latest` is a U60 build).
  It has no assets, and it must not be marked Superseded (they would skip it):

  ```sh
  cat > bridge.md <<'EOF'
  This build was only for Oxygen Not Included's U60 public test. U60 is now the normal game, and Moono's updates for it are normal releases: get [the latest release](https://github.com/tigin-backwards/oni-moono/releases/latest).

  **U60 is the normal game now**
  - Moono's updates for U60 are normal releases from now on: get the latest release on the releases page.
  EOF
  gh release create vYYYY.MM.DD.NN --prerelease --target master --title "YYYY.MM.DD.NN (U60 is the normal game now)" --notes-file bridge.md
  ```

  Its tag is the next free counter after the first normal release. Old U60 builds show it in
  their update badge from then on, and no new build sees it. Its first sentence contains the
  phrase that old U59 builds' start message links a search for when they start on a U60 game
  ("Only for Oxygen Not Included's U60 public test", which the U60 pre-releases open with), so
  that search should list it beside the last U60 pre-release, which also runs there.
- The first normal U60 release carries a section that says so, which old U59 builds' badge
  shows, e.g. **U60** with "Moono now plays U60, the game's current update. For U59, stay on
  2026.11.19.03."

## A future beta

Nothing is built for one now. When Klei opens the next public test (say U61) and Moono should
follow it before it ships:

1. Branch it from `master` (`u61`, or reuse `u60`), and let `build.yml` run on it (`branches`).
2. `release.yml`: allow that ref and make its releases pre-releases titled "… (U61 Beta)" with
   the opener "Only for Oxygen Not Included's U61 public test …". The U60 beta's version of both
   is `release.yml` at fda7d70c1.
3. `StartText.Updates`: add the beta's Unity version with `Beta` true. Its builds then look for
   updates among pre-releases only, and master's among normal releases. Add the two mismatch
   messages back (the beta build on the normal game, the normal build on the beta), as
   `GAME_IS_NORMAL` and `GAME_IS_BETA` were at fda7d70c1, and translate them.
4. The Workshop item stays master's.
5. When the beta becomes the public game, repeat the cut-over above, including the bridging
   pre-release.

## Reproducibility

How close the builds are to reproducible, as checked on the first dry run (2026-11-08):

- **The Linux archive's files are reproducible.** Built locally from the same commit, with the
  same tag, against the same reference assemblies and in CI mode, every one of its 291 files is
  byte for byte the one GitHub built (the fetch needs read access to the refs repository):

  ```sh
  python3 -I scripts/fetch-game-refs.py out/game-refs
  ContinuousIntegrationBuild=true MOONO_RELEASE=vYYYY.MM.DD.NN \
    MOONO_PLATFORMS=linux ONI_GAME_DIR=out/game-refs scripts/release.sh "$(scripts/release-version.sh)"
  ```

  This needs `global.json`'s exact SDK and nothing newer: CI installs that SDK alone, since
  the player carries the SDK's runtime and the symbols record the runtime the compiler ran on.
  Managed builds are deterministic (`Deterministic`, `ContinuousIntegrationBuild`,
  `DeterministicSourcePaths`: the symbols name sources as `/_/...`). Built against a real game
  install instead of the reference assemblies, they differ only in their IDs (MVID, PE
  timestamp, PDB ID), which hash the referenced assemblies; the IL is the same.
- **The Windows archive** has the same files, but a WSL build (`release.sh` as it was run before)
  is not the same as GitHub's Windows one:
  - On Windows the SDK writes its generated files with CRLF and records the native files'
    versions in `OniMoono.deps.json`.
  - `OniMoono.exe` comes from the runner image's MSVC toolset, which changes as GitHub updates
    the image.

  Reproducing it takes a Windows machine with the same toolset. `OniMoono.exe` is built with
  `/Brepro`, so it holds no timestamps; it embeds the commit.
- **The archives themselves** record file times (the tar.gz also its owner and gzip time), so
  two builds of the same files give different archives. Compare their contents.
