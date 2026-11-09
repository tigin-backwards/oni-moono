# Install modes

How Moono starts and where it is installed. Three ways exist side by side:

| Mode | Started by | Player folder | Updates |
|---|---|---|---|
| Launch options | Steam: `"…\OniMoono.exe" -- %command%` | where setup installed it (`%LOCALAPPDATA%\oni-moono\player`, or `player/` of a release on Linux) | run a newer release's setup |
| Standalone | `OniMoono.exe` itself (Explorer, a shortcut, a terminal) | the same, or a versioned install | as the folder it runs from |
| Versioned install | its entry point, `…\oni-moono\versioned\OniMoono.exe` (Steam's launch options, its shortcut) | `versioned\player\<version>\` | from its source folder (a Workshop item) at each start |

The launch-options route is unchanged by the other two: with `-- %command%` Moono takes the game
folder from Steam's command, as before.

## Standalone launch

Started without Steam's `-- %command%` and without `--game` (`src/OniMoono.Host/Launch`):

1. **The game** is found through Steam's libraries: Steam's install folder (Windows: the
   registry's `HKCU\Software\Valve\Steam\SteamPath`; Linux: `~/.steam/steam`,
   `~/.local/share/Steam`, the snap's and the Flatpak's, the places setup uses), each one's
   `steamapps/libraryfolders.vdf`, then app 457140's `appmanifest_457140.acf` in a library
   (`installdir`), or the library's `steamapps/common/OxygenNotIncluded`. With several installs
   the player is asked once (a message box per install on Windows, the terminal on Linux), and
   the choice is kept in `oni-moono/launch.json` beside the other Moono data
   (`%LOCALAPPDATA%` or `~/.local/share`).
2. **An install Steam hasn't finished**, with an update pending or not fully installed
   (`StateFlags` other than 4, or no appmanifest), isn't started: Steam opens on the game's page
   (`steam://nav/games/details/457140`) to finish it. Never `steam://rungameid/457140`: Steam
   would start whatever the launch options say, the original game included, and Moono's start-up
   never starts the original game.
3. **Steam** must be running with an account logged in: it is started (`-silent`) and waited for
   up to 90 seconds when it isn't.
4. **`SteamAppId=457140`** is set for the process (and its native environment), so the game's
   `SteamAPI.RestartAppIfNecessary(457140)` in `SteamManager.Awake` returns false, as the shipped
   `steam_appid.txt` makes it when the game's own folder is the working directory.

The log says how a start came about: `launch: from Steam's launch options (-- %command%)`,
`launch: direct (--game)`, or `launch: standalone: the game at …`.

**Steam Cloud.** The game's saves sync through Steam Auto-Cloud: the game writes plain files
(`cloud_save_files/<account>/` under its save folder), and Steam syncs them before and after the
sessions it starts (the account's `userdata/<id>/457140/remotecache.vdf` lists them with
`root 2`, the Documents folder; there is no `remote/` folder of ISteamRemoteStorage files). A
standalone start is not one Steam started, so its saves sync on the next start from Steam. The
log says so in one line:
`started directly, not by Steam: Steam Cloud syncs on the next start from Steam`.

**Tests** never reach the real Steam: `ONI_MOONO_STEAM_ROOTS` names made-up Steam folders in
place of the machine's (their `registry.vdf` is the client's state), `ONI_MOONO_STEAM_START` a
command run instead of starting Steam (none: nothing starts), `ONI_MOONO_STEAM_OPENED` a file
the steam:// links go to instead of Steam, and `ONI_MOONO_CHOOSE_GAME` the install chosen
(`tests/OniMoono.UnityEngine.Tests/LaunchTests.cs`).

## Versioned install

For a distribution that updates Moono in place, as the Steam Workshop would
(`src/OniMoono.Host/Install`, `src/OniMoono.NativeHost/host.c`):

```
%LOCALAPPDATA%\oni-moono\versioned\        ~/.local/share/oni-moono/versioned/ on Linux
  OniMoono.exe                             the entry point (Linux: OniMoono, a shell script)
  player\2026.11.08.01-u60\                one player per version, never written once there
  player\2026.11.08.01\
  state\current  source  pin  switch       what the entry point and the player read
```

On Linux a Flatpak or snap Steam can't see `~/.local/share`, so the folder is the sandbox's
(`~/.var/app/com.valvesoftware.Steam/data/oni-moono/versioned`,
`~/snap/steam/common/oni-moono/versioned`), as setup does for the player.

**The entry point** picks the version before the .NET runtime loads, because a process can host
only one runtime and each version carries its own (self-contained): on Windows it is the native
host itself (no `OniMoono.dll` beside it, a `player\` folder instead), which runs
`player\<state\current>\OniMoono.dll` in its own process with `ONI_MOONO_INSTALL_ROOT` set, so
the GPU exports and the crash records work as from any player folder; on Linux the shell script
does the same. The choice itself needs the game's build and the versions' `mod_info.yaml`, which
the player reads:

1. The game's build: `KleiVersion.ChangeList` in its `Assembly-CSharp.dll`, read from the
   metadata without loading it (744825 for U59, 762230 for U60).
2. From the source folder (`state\source`), the version the game itself would load
   (`ArchiveChoice`, the game's `KMod.Mod.GetMostSuitableArchive`): of the root and
   `archived_versions\*` with a `mod_info.yaml`, those whose DLC restrictions allow (the DLC
   isn't known before the game starts, so as for none; Moono's name none) and whose
   `APIVersion` is 2 or 0, the highest `minimumSupportedBuild` at or below the build; with no
   `archived_versions`, the root. When that version isn't installed yet, its player is copied
   into `player\.incoming-…`, every file hashed as it is read and checked against the version's
   `SHA256SUMS` manifest, then moved to `player\<version>\`. A file missing or not matching ends
   the copy, deletes it, and the current version plays on.
3. Of the installed versions the same rule picks the one to play (newest of equals), unless
   `--use-version VERSION` pinned one (`--use-version latest` unpins): a rollback by hand to a
   kept version.
4. When that is not the running version, `state\current` names it and the player exits with code
   77 (`state\switch` written): the entry point starts again on it. A game update from U59 to
   U60 switches this way, once.
5. Otherwise it plays, holding `player\<version>\.in-use` open, and prunes: the current version,
   the two newest others and a pinned one stay, and nothing held open is removed.

The entry point updates itself from the chosen version: on Windows the running `OniMoono.exe` is
renamed to `OniMoono.exe.old` (a running exe can be renamed, not replaced) and the new one put in
place; the next start deletes the `.old`. On Linux the script is replaced by a rename.

The first install from a source folder makes a shortcut to the entry point: `Moono.lnk` in the
Start menu's Programs and on the desktop, or `oni-moono.desktop` in `~/.local/share/applications`.

**Tests** of the native host set `ONI_HOST_NO_DIALOG=1`: a failure to start then shows no
message box and touches no Player.log; its line goes to stderr, and to `ONI_HOST_MESSAGES`' file
when set. Without a redirected stderr, a failure is a message box on the desktop.

## Workshop item

The Workshop item carries both branches in the game's `archived_versions` convention, so the
same rule gives each game build its Moono:

```
<item>/                         the u60 build: minimumSupportedBuild 762230
  mod_info.yaml  moono.json  oni-moono-…-u60-windows-x64.SHA256SUMS  oni-moono-…-u60-linux-x64.SHA256SUMS
  windows-x64/  linux-x64/      the release archives' oni-moono folders
  OniMoono.exe  OniMoono.sh     start here once (Windows: double-click; Linux: sh OniMoono.sh)
  verify.ps1  verify.sh  README.md  SHA256SUMS
  archived_versions/u59/        the master build: minimumSupportedBuild 744825, laid out the same
```

- **The original game loads nothing from it.** Each version's `mod_info.yaml` has `APIVersion: 2`,
  `minimumSupportedBuild` and empty DLC lists, and nothing at a version's top is content the
  game scans for (no `.dll`, `.po`, `strings`, `anim` and the like): the game logs "No supported
  content" and the mod list shows it so. No code of Moono's runs inside the original game.
- **Under Moono, the mod manager skips it** (`OniMoono.GameTweaks.OwnWorkshopItem`, a guarded
  patch of `KMod.Steam.MakeModAndReportOutcome`, declared and checked as every game tweak is,
  always on): no mod, no broken-mod entry, no download-failed event. The item is told by
  `moono.json`'s `"workshopItem": "oni-moono"`, not by an ID.
- **The first start** from the item (`OniMoono.exe` there, which has `moono.json` and
  `windows-x64\player` beside it and no player of its own) copies the root's Windows player to
  `versioned\player\.bootstrap-<pid>` and starts the copy with the item as its source; it installs
  the version for the game as above, the entry point and the shortcut, then hands over to the
  entry point. The item's folder itself never runs, so Steam can always update it; later starts
  install each update from it.
- **Built in CI** (`.github/workflows/workshop.yml`, run by hand with a master and a u60 release
  tag): the releases' assets are downloaded and verified against their provenance and branch,
  `scripts/workshop-item.py` checks each archive against its manifest while laying them out,
  and `attest.yml` signs the item's `SHA256SUMS` and every file in it. The result is the
  `workshop-item` artifact. Nothing goes to Steam. `verify.sh` or `verify.ps1` in the item checks
  it against the attested release manifests and its own attested `SHA256SUMS` (docs/RELEASES.md).
