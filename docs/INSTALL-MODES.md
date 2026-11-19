# Install modes

How Moono starts and where it is installed. Three ways exist side by side:

| Mode | Started by | Player folder | Updates |
|---|---|---|---|
| Launch options | Steam: `"…\OniMoono.exe" -- %command%` | where setup installed it (`%LOCALAPPDATA%\oni-moono\player`, or `player/` of a release on Linux) | run a newer release's setup |
| Standalone | `OniMoono.exe` itself (Explorer, a shortcut, a terminal; Linux: the launcher `oni-moono.desktop`) | the same, or a versioned install | as the folder it runs from |
| Versioned install | its entry point, `…\oni-moono\versioned\OniMoono.exe` (a shortcut you make, a non-Steam game in Steam, Steam's launch options) | `versioned\player\<version>\` | from Steam's copy of the Workshop item at each start |

The other two don't change the launch-options route. With `-- %command%`, Moono still takes the
game folder from Steam's command.

## Standalone launch

Started without Steam's `-- %command%` and without `--game` (`src/OniMoono.Host/Launch`):

1. **The game.** Moono finds it through Steam's libraries:
   - Steam's install folder. Windows: the registry's `HKCU\Software\Valve\Steam\SteamPath`.
     Linux: `~/.steam/steam`, `~/.local/share/Steam`, the snap's and the Flatpak's (the places
     setup uses).
   - Each one's `steamapps/libraryfolders.vdf`.
   - In a library, app 457140's `appmanifest_457140.acf` (`installdir`), or the library's
     `steamapps/common/OxygenNotIncluded`.

   With several installs, Moono asks the player once and keeps the choice in
   `oni-moono/launch.json` beside the other Moono data (`%LOCALAPPDATA%` or `~/.local/share`).
   On Windows it asks with a message box per install. On Linux it asks on the terminal, or
   without one in a desktop dialog (see [Linux launcher](#linux-launcher)).
2. **An install Steam hasn't finished** isn't started. That is an install with an update pending
   or not fully installed (`StateFlags` other than 4, or no appmanifest). Steam opens on the
   game's page (`steam://nav/games/details/457140`) to finish it. Moono never uses
   `steam://rungameid/457140`: Steam would start whatever the launch options say, the original
   game included, and Moono's start-up never starts the original game.
3. **Steam** must be running with an account logged in. When it isn't, Moono starts it
   (`-silent`) and waits up to 90 seconds.
4. **`SteamAppId=457140`** is set for the process (and its native environment). The game's
   `SteamAPI.RestartAppIfNecessary(457140)` in `SteamManager.Awake` then returns false, as the
   shipped `steam_appid.txt` makes it when the game's folder is the working directory.

The log says how a start came about: `launch: from Steam's launch options (-- %command%)`,
`launch: direct (--game)`, or `launch: standalone: the game at …`.

**Steam Cloud.** The game's saves sync through Steam Auto-Cloud:

- The game writes plain files (`cloud_save_files/<account>/` under its save folder).
- Steam syncs them before and after the sessions it starts.
- The account's `userdata/<id>/457140/remotecache.vdf` lists them with `root 2`, the Documents
  folder. There is no `remote/` folder of ISteamRemoteStorage files.

A standalone start is not one Steam started, so its saves sync on the next start from Steam. The
log says so in one line:
`started directly, not by Steam: Steam Cloud syncs on the next start from Steam`.

**Tests** never reach the real Steam (`tests/OniMoono.UnityEngine.Tests/LaunchTests.cs`).
Four variables stand in for it:

- `ONI_MOONO_STEAM_ROOTS`: made-up Steam folders to use in place of the machine's. Their `registry.vdf`
  is the client's state.
- `ONI_MOONO_STEAM_START`: the command to run instead of starting Steam. With none, nothing
  starts.
- `ONI_MOONO_STEAM_OPENED`: the file the steam:// links go to instead of Steam.
- `ONI_MOONO_CHOOSE_GAME`: the install to choose.

## Versioned install

For a distribution that updates Moono in place, as the Steam Workshop would
(`src/OniMoono.Host/Install`, `src/OniMoono.NativeHost/host.c`):

```
%LOCALAPPDATA%\oni-moono\versioned\        ~/.local/share/oni-moono/versioned/ on Linux
  OniMoono.exe                             the entry point (Linux: OniMoono, a shell script)
  Restarter.exe                            Windows: the entry point again, for the game's App.Restart
  player\2026.11.20.01\                    one player per version, never written once there
  player\2026.11.08.01\
  state\current  source  pin  switch       what the entry point and the player read
```

On Linux a Flatpak or snap Steam can't see `~/.local/share`, so the folder is the sandbox's
(`~/.var/app/com.valvesoftware.Steam/data/oni-moono/versioned`,
`~/snap/steam/common/oni-moono/versioned`), as setup does for the player.

**The entry point** picks the version before the .NET runtime loads. A process can host only one
runtime, and each version carries its own (self-contained).

- **Windows:** the entry point is the native host itself (no `OniMoono.dll` beside it, a
  `player\` folder instead). It runs `player\<state\current>\OniMoono.dll` in its own process
  with `ONI_MOONO_INSTALL_ROOT` set, so the GPU exports and the crash records work as from any
  player folder.
- **Linux:** the shell script does the same.
- **Restarts.** The game's `App.Restart` (a language change, the mods screen) starts the entry
  point again with the same arguments, so the version is chosen again. On Windows it looks for
  `Restarter.exe` beside the entry point, a copy of it that runs the installed version's
  Restarter; on Linux the host tells the version's Restarter to start the entry point
  ([ARCHITECTURE.md](ARCHITECTURE.md), "Which executable").

The choice needs the game's build and the versions' `mod_info.yaml`, which the player reads:

1. The game's build: `KleiVersion.ChangeList` in its `Assembly-CSharp.dll`, read from the
   metadata without loading it (744825 for U59, 763796 for U60).
2. From the source folders (`state\source` and Steam's copy of the item, see
   [Where updates come from](#where-updates-come-from)), the version the game itself would load
   (`ArchiveChoice`, the game's `KMod.Mod.GetMostSuitableArchive`):
   - The candidates are the root and `archived_versions\*` with a `mod_info.yaml`.
   - Their DLC restrictions must allow it. The DLC isn't known before the game starts, so they
     are checked as for none; Moono's name none.
   - Their `APIVersion` must be 2 or 0.
   - Of those, the highest `minimumSupportedBuild` at or below the build wins. With no
     `archived_versions`, the root.
   - A version for a newer game than the installed one is never installed
     ([A game older than the item](#a-game-older-than-the-item)).

   When the version isn't installed yet and is not older than the installed one for this
   game, Moono copies its player into `player\.incoming-…`,
   hashes every file as it reads it, checks it against the version's `SHA256SUMS` manifest, then
   moves the copy to `player\<version>\`
   ([What the installer verifies](#what-the-installer-verifies)). A file missing or not
   matching, or any error while reading the item, ends the install and deletes the copy, and
   the current version plays on.
3. The same rule picks which installed version plays (newest of equals), unless
   `--use-version VERSION` pinned one. That is a rollback by hand to a kept version.
   `--use-version latest` unpins.
4. When that is not the running version, `state\current` names it and the player exits with code
   77 (`state\switch` written): the entry point starts again on it. A game update from U59 to
   U60 switches this way, once.
5. Otherwise it plays, holding `player\<version>\.in-use` open, and prunes: the current version,
   one previous (the newest other version for the game's build) and a pinned one stay, and
   nothing held open is removed. Each version is about 100 MB.

The entry point updates itself from the chosen version:

- **Windows:** the running `OniMoono.exe` is renamed to `OniMoono.exe.old` (a running exe can be
  renamed, not replaced) and the new one put in place. The next start deletes the `.old`.
  `Restarter.exe` is updated the same way.
- **Linux:** a rename replaces the script.

Moono makes no shortcut. After the first start, start Moono through the entry point,
`%LOCALAPPDATA%\oni-moono\versioned\OniMoono.exe` (Linux: `versioned/OniMoono`, as above), in
one of these ways:

1. A shortcut you make to it (right-click, Send to, Desktop).
2. Steam's "Add a Non-Steam Game to My Library", picking that `OniMoono.exe`. Steam starts it,
   so the overlay works; ONI's Steam Cloud is not synced this way.
3. ONI's launch options in Steam, with the full path (Steam may not expand `%LOCALAPPDATA%`):
   `"C:\Users\<you>\AppData\Local\oni-moono\versioned\OniMoono.exe" -- %command%`, the form
   setup.cmd uses. This gives the overlay and ONI's Steam Cloud.

The Workshop item's `OniMoono.exe` is for the first start only.

**Tests** of the native host set `ONI_HOST_NO_DIALOG=1`. A failure to start then shows no
message box and touches no Player.log. Its line goes to stderr, and to `ONI_HOST_MESSAGES`' file
when set. Without a redirected stderr, a failure is a message box on the desktop.

## Linux launcher

The Linux archive has `oni-moono.desktop` beside `setup.sh`. Double-clicked in a file manager,
it starts `player/OniMoono` as a standalone launch, as double-clicking `OniMoono.exe` does on
Windows.

- **`Exec`.** A desktop entry's `Exec` can't be relative, so it runs
  `sh -c '… exec "$(dirname "$1")/player/OniMoono"' oni-moono %k`. `%k` is the entry's path.
  GNOME's (GLib's) and KDE's launchers fill it in, the Steam Deck's desktop mode included.
- **From a clone** of the repository it runs `out/player/OniMoono`, which setup builds.
- **Executable bit.** The archive keeps it executable (`scripts/check-linux-archive.py` checks the archive in
  `scripts/release.sh`).
- **Untrusted launchers.** A file manager may ask once before it runs a launcher it didn't
  install. Dolphin (KDE, Steam Deck) asks to execute it. On GNOME, right-click it and choose
  *Allow Launching* where offered. GNOME Files may open it as text instead; the menu entry below
  works there.
- **The applications menu.** `./setup.sh` asks on a terminal whether to add Moono to it. The
  entry is `~/.local/share/applications/oni-moono-player.desktop` (`XDG_DATA_HOME`), with the
  absolute path of the player setup installed and the icon in
  `~/.local/share/icons/hicolor/96x96/apps/`. A later setup keeps an entry it made up to date.
  `--menu` adds it without asking, `--no-menu` doesn't ask, `--remove-menu` removes both files.
  Its name differs from the versioned install's `oni-moono.desktop`.
- **Messages.** Started without a terminal, Moono would say why it can't start only on stderr,
  where nobody sees it. So `StandaloneLaunch.Tell` also shows it in a dialog when stderr isn't a
  terminal (`src/OniMoono.Host/Launch/DesktopDialog.cs`):
  - It uses the first of `zenity`, `kdialog` and `notify-send` that runs. An exit code of 0, or
    1 for a dialog closed, counts; anything else tries the next. With none, stderr alone.
  - With several installs of the game and no terminal to ask on, the choice is a zenity list or
    a kdialog menu. Cancelled means none chosen, and Moono says so. With neither, Moono takes
    the first install.
- **No dialogs** in three cases:
  - In test runs (`ONI_MOONO_STEAM_ROOTS` set).
  - With `ONI_HOST_NO_DIALOG=1`, as the Windows host reads it. On Windows it now also keeps the
    player's own message boxes away, and e2e runs set it.
  - Without `DISPLAY` and `WAYLAND_DISPLAY`.

## Workshop item

The Workshop item carries one version: a master release, for the current game (U60). U59
players install Moono's last U59 release,
[v2026.11.19.03](https://github.com/tigin-backwards/oni-moono/releases/tag/v2026.11.19.03): the
original U59 game takes a Workshop item only as a zip, so it lists this folder item as failed to
download.

```
<item>/                         the release: minimumSupportedBuild 763796
  mod_info.yaml  moono.json  oni-moono-…-windows-x64.SHA256SUMS  oni-moono-…-linux-x64.SHA256SUMS
  windows-x64/  linux-x64/      the release archives' oni-moono folders
  OniMoono.exe  OniMoono.sh     start here once (Windows: double-click; Linux: sh OniMoono.sh)
  MoonoLoader.dll               the loader mod, which the original game loads (below)
  verify.ps1  verify.sh  README.md  SHA256SUMS
  attestation/SHA256SUMS.sigstore.json   SHA256SUMS's provenance, for checks by hand (not listed in it)
```

- **The original game loads only the loader mod from it** (below). Its `mod_info.yaml` has
  `APIVersion: 2`, `minimumSupportedBuild` and empty DLC lists, and nothing else at the top is
  content the game scans for (no other `.dll`, no `.po`, `strings`, `anim` and the like).
- **No archived versions.** Until November 2026 the item also had the U59 build in
  `archived_versions/u59`. Since the U60 cut-over `scripts/workshop-item.py` lays out one version
  and the verify scripts refuse an `archived_versions` folder; the installer still handles one.
- **Under Moono, the mod manager skips it:** no mod, no broken-mod entry, no download-failed
  event. The patch is `OniMoono.GameTweaks.OwnWorkshopItem`, a guarded patch of
  `KMod.Steam.MakeModAndReportOutcome`, declared and checked as every game tweak is, and always
  on. A mod the profile's `mods.json` has for it (the original game keeps one) is dropped from
  the list without an event. Moono recognises the item by `moono.json`'s
  `"workshopItem": "oni-moono"`, not by an ID.
- **The first start** from the item runs `OniMoono.exe` there, which has `moono.json` and
  `windows-x64\player` beside it and no player of its own:
  1. It copies the root's Windows player to `versioned\player\.bootstrap-<pid>` and starts the
     copy with the item as its source.
  2. The copy installs the version for the game as above, and the entry point.
  3. The copy hands over to the entry point.

  The item's folder itself never runs, so Steam can always update it. Later starts install each
  update from Steam's copy of it ([Where updates come from](#where-updates-come-from)). The
  bootstrap copy checks the files as every start does, so a download Steam hasn't finished
  installs nothing even on the first start.
- **Built in CI** (`.github/workflows/workshop.yml`, run by hand with a release tag):
  - The release's assets are downloaded and verified against their provenance from master.
  - `scripts/workshop-item.py` checks each archive against its manifest while laying them out.
  - `attest.yml` attests the item's `SHA256SUMS` and every file in it.
  - The Sigstore bundle of the attestation naming `SHA256SUMS` goes into the item as
    `attestation/SHA256SUMS.sigstore.json`, and gh checks it before the item becomes the
    `workshop-item` artifact.
  - Nothing goes to Steam.
  - `ci/workshop/verify.sh` or `verify.ps1`, taken from GitHub, checks the item against the
    attested release manifests and its own attested `SHA256SUMS`, the bundle included
    ([RELEASES.md](RELEASES.md#verifying-the-workshop-item)).
- **Size:** about 230 MB in about 610 files (it was about 404 MB with the U59 build).
- **Uninstall:** clear Oxygen Not Included's launch options in Steam, then delete
  `%LOCALAPPDATA%\oni-moono` (Linux: `~/.local/share/oni-moono`, or the sandboxed Steam's folder
  above).

### Where updates come from

The item exists in two places:

- **Steam's copy:** `steamapps\workshop\content\457140\<item id>` in a Steam library. Steam
  downloads the item there and updates it.
- **The game's copy:** the original U60 game copies every subscribed mod into its mods folder
  and loads it from there: `Documents\Klei\OxygenNotIncluded\mods\Steam\<item id>` on Windows,
  `~/.config/unity3d/Klei/Oxygen Not Included/mods/Steam/<item id>` on Linux. The loader mod
  runs from this copy. Only the original game refreshes it, at its next start after it saw the
  item change. Under Moono the mod manager skips the item, so a player who always starts
  through Moono keeps an old copy there.

An install updates from Steam's copy, wherever it was first started from (`SteamItem`,
`VersionedStart`). At each start Moono:

1. Finds Steam's copy. It looks in the library that holds the game (`<library>\steamapps\common\…`)
   and in every library of every Steam install (as [Standalone launch](#standalone-launch) finds
   them), for a folder of `workshop\content\457140` whose `moono.json` says
   `"workshopItem": "oni-moono"`. The ID is not fixed.
2. Leaves that copy alone while Steam still has an update of it to download: the library's
   `steamapps\workshop\appworkshop_457140.acf` then names a `latest_manifest` for the item that
   differs from the installed `manifest`. Player.log says so. Without the file or those
   entries, the copy is read, and the file hashes decide.
3. Looks at Steam's copy and at the recorded folder (`state\source`), and installs from the one
   whose version for the game is newer. When that one fails its checks, the other is tried.
4. Records Steam's copy in `state\source`. An install whose source was the game's copy heals
   this way at its next start: Player.log says
   `install: updates come from Steam's copy of the Workshop item, … (was …)`.

Without a Steam copy (a source folder of another kind, or a Steam install Moono can't find),
the recorded folder alone is used, as before.

A version older than the installed one for the game is not installed, so nothing downgrades:
neither a start from the game's old copy (its `OniMoono.exe`, or the loader's install button) nor
an item rolled back at Steam. `--use-version` still pins any installed version.

**Versions before this (up to 2026.11.15.02)** read only `state\source`. An install whose
source is the game's copy stays on its version until one of these is done once:

- Run `OniMoono.exe` (Linux: `sh OniMoono.sh`) in Steam's copy of the item, in
  `steamapps\workshop\content\457140\<item id>`. It records that folder as the source.
- Or start the original game twice with the item enabled (clear the launch options for that):
  the first start sees the update and asks for a restart, the second refreshes the game's copy.

**Tests:** `InstallTests.StaleSourceHealsFromSteamsCopy`, `SteamsCopyIsFoundInItsLibrary`,
`SteamsCopyForANewerGameIsNotInstalled` and `LoaderTests.CopyFromAStaleCopyKeepsTheNewerVersion`,
on made-up Steam folders (`ONI_MOONO_STEAM_ROOTS`).

### A game older than the item

The item has no version for a U59 game. The installer installs nothing from it then, deletes
nothing, and says so (`VersionedStart`, `VersionedInstall.SourceNeedsBuild`;
`InstallTests.ItemWithoutAVersionForTheGame`):

- **An install that already has a U59 version** keeps playing it. Player.log says at each start
  that the item has no version for the game's build and where the releases are. The player is
  told once per game build (`state\no-updates`), in a message box (Windows) or desktop dialog
  (Linux): "Moono's Workshop item is for the U60 update (build …) of Oxygen Not Included and
  later. Your game is the U59 update (build 744825). Version … plays on, without updates from
  the Workshop. Moono for your game is on GitHub: …/releases/tag/v2026.11.19.03", the u59
  line's last release. `ONI_HOST_NO_DIALOG=1`
  keeps the dialog away, as for every message.
- **When that game updates to U60,** the next start installs the item's version and switches to
  it, as before.
- **A first start from the item on a U59 game** (`OniMoono.exe` or `OniMoono.sh` there) installs
  nothing and says: "Moono's Workshop item is for the U60 update … Your game is the U59 update
  (build 744825). Install Moono for your game from a GitHub release instead:
  …/releases/tag/v2026.11.19.03".
- **U59 versions installed from the item** predate this and don't know it. With the
  U60-only item they install the U60 version beside their own (about 105 MB on Windows, unused
  until the game updates), keep playing the U59 version, and say nothing. They update with a
  U59 release's setup.

## What the installer verifies

**File hashes, for integrity.** Before a version from the item counts as installed:

- Moono hashes every file of its player as it copies it, and checks it against the version's
  release manifest (`oni-moono-…-<platform>.SHA256SUMS`).
- It reads `moono.json`, `mod_info.yaml` and the manifest once, capped (64 KB for the metadata,
  16 MB for the manifest), and uses the bytes it read.

A file missing or not matching, a manifest path outside the folder, or any error while reading
the item installs nothing. Moono deletes the copy, the current version plays on, and the log
says why.

When the item can't be read at all, Moono tells the player once, in a message box (Windows) or
desktop dialog (Linux): "Moono did not install version … from the Workshop item: the
Workshop item could not be read. Version … plays on; Player.log says why." On a first start with
nothing installed yet, it says "Moono could not be installed: …" and exits.

**Missing files at start.** The installed version keeps its release manifest (`SHA256SUMS` in
`player\<version>\`). Before it starts, every player file the manifest lists must be there
(existence only, nothing hashed or repaired). If any is missing, it doesn't start:

- A message box (Windows) or desktop dialog (Linux) names up to five of them, and says to delete
  the version's folder (`%LOCALAPPDATA%\oni-moono\versioned\player\<version>`) and start the
  game again.
- Player.log lists them all.

Versions installed before Moono kept the manifest aren't checked.

**No signature check.** The hashes guard against a download Steam hasn't finished or a corrupt
file. They don't guard against a forged item, because the manifests come from the same item.
Moono trusts the Workshop item as Steam delivers it, as the game trusts any mod. Whoever
controls the Steam account that uploads it controls what Moono installs from it. Versions are
compared only to leave out one older than the installed version, which would never play.

**Checking by hand.** The item and every file in it have SLSA provenance from this repository's
`workshop.yml` (`attestation/SHA256SUMS.sigstore.json` in the item, and GitHub's attestations),
and each version's release manifest has its release's provenance. `ci/workshop/verify.sh` or
`verify.ps1`, fetched from GitHub rather than taken from the item, checks all of it
([RELEASES.md](RELEASES.md#verifying-the-workshop-item)).

**The first install you can be sure of** is a GitHub release's setup
([RELEASES.md](RELEASES.md#verifying-a-release)): the GitHub releases are the root of trust. The
first install from the item runs its own `OniMoono.exe` (or `OniMoono.sh`), and whoever controls
the item also writes its Workshop description, so check the item by hand first if you start
there.

## The loader mod

`MoonoLoader.dll` at the item's root (`src/MoonoLoader`) is a plain `KMod.UserMod2` with the
game's Harmony and nothing else. The original U60 game loads it like any mod while the item is
enabled. Under Moono it does nothing. Moono's player runs the game on .NET with its own
`OniMoono*` assemblies, so the loader returns at once, and Moono leaves the item out of its mod
list anyway.

- **At the main menu,** once each start, it shows the game's info dialog "Play with Moono?"
  (`MoonoDialog`). The dialog has:
  - The text "Moono makes Oxygen Not Included run faster. To play with it:".
  - Three numbered steps: click Install & copy launch option; in Steam, paste it into the
    game's launch options; quit the game and press Play.
  - The launch-option line under step 1, in the box of the game's text field. It is one line,
    shortened in the middle when long: `"…\oni-moono\versioned\OniMoono.exe" -- %command%`.
  - A muted line: "To stop seeing this, disable the Moono mod."

  It stays open until **Not now** (or Escape), and the game carries on. Nothing is added to the
  main menu itself.
- **Install & copy launch option** installs Moono for this game build, in the game's own process:
  - It copies the version the game loaded the loader from into `versioned\player\<version>\`
    with the installer's code (`src/OniMoono.Host/Install/PlayerFiles.cs`, compiled into both),
    and checks every file against the version's manifest.
  - It puts the entry point in place, and `state\source` names the item's folder. That is the
    game's copy; Moono's next start records Steam's copy in its place
    ([Where updates come from](#where-updates-come-from)).
  - It leaves an installed version as it is. When a newer version than the game's copy is
    already the current one, it installs nothing, and that version and its source stay.
  - A first install copies on a thread of its own (`InstallRun`), so the game does not freeze
    for the copy and its checks: the button says "Installing…" and takes no second click until
    the copy ends. A later click finds the version installed and is done at once.
  - Closing the dialog during the copy lets it finish, without touching the clipboard.
    Quitting the game cuts it off. That leaves only the `player\.incoming-<version>-<pid>`
    folder, which the copy renames into place as its last step and which Moono's next start
    removes; the next click copies again.

  Like the installer, the loader checks no signature: it is code from that same item, which the
  game runs as it runs any mod. The GitHub releases stay the root of trust, as for the first
  install above.

  Then the whole line goes on the clipboard, the button says "Copied ✓" and step 1 says "Moono
  installed. Launch option copied." (or why it could not be installed), so the player can switch
  to Steam, paste and come back. The line is the entry point with its full path, as setup uses:
  `"C:\Users\<you>\AppData\Local\oni-moono\versioned\OniMoono.exe" -- %command%` (Linux:
  `"/home/<you>/.local/share/oni-moono/versioned/OniMoono" -- %command%`, the sandboxed Steam's
  folder for a Flatpak or snap Steam, as `OniMoono.sh` picks it; `ONI_MOONO_INSTALL_ROOT` moves it).

  Steam's properties can be changed while the game runs. No helper process is started and
  Steam's configuration is not touched: the player pastes the line.
- **The Mods screen:** the item's row has a **Play with Moono** button beside its Manage
  button, which shows the same dialog.
- **Translations:** its text is in the game's language where Moono has a translation: the
  `STRINGS.MOONO.LOADER.*` strings of `translations/CODE.po` (`LoaderStrings`, listed in
  `moono.pot` with the Moono Tweaks screen's, docs/TWEAKS.md), read from the item's player
  folder.
- **Built** by `workshop.yml` from its run's commit, against U60's reference assemblies
  (`ci/game-refs.json`, build 763796), and attested with the rest of the item.
- **Tests:** `LoaderTests` (the install and the line, without the game);
  `tests/e2e/workshop-loader.yaml` in the original game with a made-up item as a local mod, and
  `workshop-loader-moono.yaml` in Moono with it also as a Workshop subscription.
