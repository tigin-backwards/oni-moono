# When the game updates

The procedure for a new build of Oxygen Not Included, drawn from the U60 updates
(`docs/research/u60-762230.md`, `u60-763023.md`, `u60-763325.md`, `u60-763612.md`). It applies
to the branch that plays the updated game: `u60` for the U60 beta, `master` for U59. BUILD is
the new build, OLD the one the branch targets now. Run builds, players and checks through runq
(`RUNQ_OWNER=gameupdate RUNQ_LEVEL=release`, docs/RUNQ.md).

Never write to the live Steam install, Steam's settings, Documents or `%LOCALAPPDATA%\oni-moono`:
read the live install only, and freeze copies.

## 1. Identify the build

- [ ] **Changelist:** `KleiVersion.ChangeList` in the new `Assembly-CSharp.dll`, read from its
  metadata without running it (copy the file to a scratch folder first):
  `ilspycmd -t KleiVersion Assembly-CSharp.dll`.
- [ ] **Unity version:** the header of `OxygenNotIncluded_Data/globalgamemanagers`
  (`head -c 80 globalgamemanagers | strings`). If it changed, stop: that needs a new type tree
  database and version check, a much larger job.
- [ ] **Steam build:** `buildid` and the depots' manifests in the live
  `steamapps/appmanifest_457140.acf`.
- [ ] **Did the other branch change?** For a beta update, U59's main depot manifest:
  `DepotDownloader.exe -app 457140 -depot 457141 -os windows -username NAME -remember-password
  -manifest-only -dir E:\tmp\...` (744825's is 4837644823067158760). If it changed, `master`
  needs this procedure too.

## 2. Freeze the build

- [ ] **Windows and Linux:** DepotDownloader 3.x (`E:\oni-builds\depotdownloader`), the saved
  login, into new folders named for the build: `-app 457140 -os windows -all-platforms=false
  -beta public_testing -dir E:\oni-builds\oni-win-BUILD` and `-app 457140 -os linux -beta
  public_testing -dir E:\oni-builds\oni-linux-BUILD` (no `-beta` for U59). One download at a
  time: two with the same login drop each other's connection. 3.x warns that it did not use
  `-all-platforms=false`; one platform is its default. Retry a dropped connection; success is its
  `Total downloaded` line, not its exit code. Check that the main depot's manifest and
  `Assembly-CSharp.dll` match the live install's.
- [ ] **Linux-side copies:** `~/projects/oni-linux-BUILD` (the Linux download without the DLC
  `StreamingAssets` entries that `~/projects/oni-dlc-OLD` holds) and `~/projects/oni-dlc-BUILD`
  (those entries from `oni-win-BUILD`).
- [ ] **Test install:** `out/local-tools/u60orig-BUILD` in the main checkout: `u60orig-OLD`'s
  links pointed at the BUILD copies, its DLC-owning Steam double copied as is. No link may point
  into the Steam install.
- [ ] **OniMcp:** `ONI_GAME_DIR=~/projects/oni-linux-BUILD scripts/onimcp.py
  out/local-tools/onimcp-u60-BUILD`.
- [ ] **The isolated Windows original** `E:\oni-builds\BrutalNotIncluded-BUILD`:
  `scripts/e2e-windows/run.py` makes it from `oni-win-BUILD` on the original's first run
  (`ONI_DOORSTOP_SOURCE`: another copy, for Unity Doorstop's `winhttp.dll`).
- [ ] **qa.env:** `out/local-tools/qa.env` of the checkout (and of a QA worktree) names the BUILD
  test install and OniMcp.

## 3. Decompile and compare

- [ ] **Decompile:** copy the Windows `Managed\` without PDBs to
  `E:\Development\U60-BUILD\Managed`, then `ilspycmd -p -o .../Assembly-CSharp -r .../Managed
  Managed/Assembly-CSharp.dll` (firstpass too when it changed).
- [ ] **oni-versioning tag** on a commit of its own, without touching its working tree or
  `master`: with a scratch `GIT_INDEX_FILE`, `read-tree U60-OLD`, replace `OxygenNotIncluded/`
  with the decompile (its `.csproj` left out) from a staging folder (`git --work-tree=STAGE add
  -f -A OxygenNotIncluded`, which also drops removed files; `git rm --cached` refuses while the
  repository's working tree has changes), then `write-tree`, `commit-tree -p U60-OLD`, `tag
  U60-BUILD`. Check the tag's tree against the decompile.
- [ ] **MD5 of every file**, OLD against BUILD, Linux and Windows (`.DepotDownloader` left out;
  mind file names with spaces): what changed and what did not (firstpass, Unity, BCL, FMOD,
  SimDLL).
- [ ] **Serialized data:** a UnityPy listing of `globalgamemanagers`, `level0`,
  `resources.assets` and `sharedassets*.assets` (type, name, path ID, size and MD5 of each
  object), compared by path ID and by type, name and size (path IDs are dense, so an insertion
  renumbers the rest). A texture's pixels are in the `.resS` file, outside its object's bytes:
  hash each `Texture2D`'s stream data (`m_StreamData` offset and size) too.
- [ ] **AssemblyDiff:** OLD against BUILD for Assembly-CSharp (and firstpass when changed), the
  members gone or added; and Linux against Windows of BUILD (one Windows-only field,
  `Global.saveFolderTestResult`, is expected).
- [ ] **Patch notes**, item by item: the code (`uBUILD:File.cs:N`) or data behind each, and what
  Moono does differently, if anything.

## 4. Impact on Moono

- [ ] **check-tweaks review:** `scripts/check-tweaks.sh --review` on the four frozen folders
  (`oni-linux-BUILD`, `oni-win-BUILD`, `oni-linux-OLD`, `oni-win-OLD`), then `diff -r` of the
  listings in `out/tweak-review/`. A changed fingerprinted body is reviewed before its hash is
  accepted.
- [ ] **Our hooks into changed code:** grep `src/` for every type the decompile diff changes,
  and check each against the tweaks of its area (fetch, conduits, overlays, rooms, rockets,
  saves).
- [ ] **Engine surface:** what the new code relies on in the engine (coroutines, destruction
  timing, schedulers), checked against ours.
- [ ] **Natives and FMOD:** `fmod-steal-fix` patches only libraries whose SHA-256 it knows;
  `check-natives` (in check.sh) checks the plugins' entry points.
- [ ] **Mods:** the members AssemblyDiff finds gone; whether U60's removed members
  (`LocText.ModifyLinkStrings`, `PlantAttributes.YieldAmount`) are still gone; onimaxxing's data
  for BUILD (`https://cdn.onimaxxing.com/mods-data/BUILD/catalog.json`). Without it the mod gate
  skips every mod with binaries until `ONI_GAME_BUILD` names a build that has data.

## 5. Retarget the branch

As in `13b5e5b9` and the commits after it (`u60-763023.md`, "Moono's commits for 763023"):

- [ ] OLD → BUILD in `Directory.Build.props`, `scripts/env.sh`, `regen-surface.sh`,
  `unityapi.sh`, `onimcp.py`, `workshop-item.py` (`minimumSupportedBuild`),
  `scripts/e2e-windows/run.py` (`WIN_GAME`, `WIN_ORIG`), `src/OniMoono.Host/Install/GameBuild.cs`,
  `docs/DEVELOPMENT.md`, `docs/INSTALL-MODES.md` and the tests' fallbacks
  (`tests/Shared/GameInstall.cs`, `GameData.cs`, `AnimationTests.cs`,
  `LoadScreenClickBugTests.cs`, `PathGridPagesTests.cs`, `tests/scripts/test_workshop_item.py`).
- [ ] **V1 fixtures:** early-base with bytes 0-3 set to BUILD and BUILD+1, uploaded with
  `scripts/fixtures.py bless PATH --name v1-current.sav` (and `v1-newer.sav`) with `--description`
  and `--build`; the comment in `tests/e2e/u60-v1-build-check.yaml`; the refs repository's
  `saves/README.md`.
- [ ] **Fingerprints:** `linux-BUILD` and `win-BUILD` in every accepted list in `src/` that names
  OLD (once the bodies are reviewed); `fmod-steal-fix`'s range and `docs/TWEAKS.md` when the FMOD
  libraries are the same bytes.
- [ ] **CI refs:** `scripts/make-refs.sh ~/projects/oni-linux-BUILD BUILD <refs clone>`; commit
  and push the refs repository (its README and `saves/README.md` too); `ci/game-refs.json` (BUILD
  the default, OLD kept) with that commit. Check with `scripts/fetch-game-refs.py OUT`.
- [ ] **Stripped game:** the branch's `tools/GameStrip` (Release) `strip
  E:\oni-builds\oni-linux-BUILD OUT/oni-BUILD`; `tar --sort=name --mtime=@0 --owner=0 --group=0
  --numeric-owner --transform s,^oni-BUILD,BUILD,` then zstd -19 (without a `zstd` command, the
  Python `zstandard` module at level 19 makes the same format); a refs release
  `game/BUILD-SHA16` with the archive and its `SHA256SUMS`; `ci/game-content.json`. Check with
  `scripts/fetch-game-content.py game OUT`.
- [ ] `ci/game-tests/budget/BUILD.txt`, `.github/workflows/workshop.yml`'s refs build,
  `docs/RELEASES.md`.
- [ ] **Mod table:** the build ranges in `scripts/modtable.py`, then
  `scripts/modtable.py > docs/MOD_COMPATIBILITY.md`.
- [ ] Fix what breaks; `scripts/check.sh` green (it runs check-tweaks on every frozen build of
  the branch's update, the older ones included).

## 6. QA

In a worktree of the branch with the retarget, after `dotnet build tools/E2eDriver -c Release`
and `scripts/build-windows.sh release`, with the Windows Steam double in its
`out/local-tools/steamstub-dlc-win/` and a `qa.env` naming BUILD. Jobs of 15 minutes or less.

- [ ] **Linux quick QA**, both players (the Vulkan gate in ours): a list file holding

  ```
  game-update-smoke
  u60-v1-build-check
  u60/vk-gate ours player-args='--renderer vulkan'
  ```

  then `scripts/e2e.py --player both --lanes 3 --list FILE`. On `master` (U59), which has
  neither U60 spec, `game-update-smoke` alone.
- [ ] **Windows Vulkan smoke** on a late game, ours:
  `scripts/e2e-windows/run.py --player ours tests/e2e/game-update-smoke-late.yaml` (monitor 2,
  `--no-focus`, muted, `ONI_HOST_NO_DIALOG` through runq; never the background flag).
- [ ] A one-off spec for each fix that touches a path a tweak hooks.

`game-update-smoke` and `game-update-smoke-late` name no build: they play whatever game
`qa.env` points at.

## 7. The study

- [ ] `docs/research/u60-BUILD.md`, in the shape of the earlier ones: what replaced what and
  when, the changed and unchanged lists, whether U59 changed, Sources, Summary, The code delta
  (the patch notes as a table, Note | Code | Moono, and the changes the notes do not mention),
  the impact on Moono's tweaks and engine, Mods, the data delta, the frozen copies, what the
  retarget changed, the commits table, and the QA results as a table.
- [ ] Push the branch; say whether release drafts built for OLD stay valid.

## One-off checks

Checks to run again on a new build, besides the gate (`scripts/check.sh`, the unit tests) and the
parity scenarios in `docs/PARITY.md`. They are too slow or too broad for every change, but the
place a new build can break assumptions this engine makes about its data.

- **Videos, every frame.** The unit tests compare the first 60 frames of each clip with libvpx.
  With a new build, compare every frame of every clip:
  `ONI_FULL_VIDEO_CHECK=1 LD_LIBRARY_PATH=<dir with libvpx.so.9> out/bin/OniMoono.UnityEngine.Tests/debug/OniMoono.UnityEngine.Tests -method OniMoono.Tests.Vp8DecoderTests.ClipsDecodeAsLibvpxDecodesThem`
  (about 17,000 frames, most of an hour in a debug build). Without a system libvpx, Ubuntu's
  package can be unpacked into a scratch directory: `apt-get download libvpx9`, then
  `dpkg-deb -x libvpx9_*.deb <dir>`.
- **Windows and Linux builds alike.** The Windows player assumes the Windows build is the same
  game build as the Linux one (`KleiVersion.ChangeList`) and that its shaders carry OpenGL
  programs; `tools/AssemblyDiff` between the two builds' assemblies and `--check-shaders` on
  Windows show whether that still holds.
