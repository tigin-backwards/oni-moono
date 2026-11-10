# Install modes

How Moono starts and where it is installed. Three ways exist side by side:

| Mode | Started by | Player folder | Updates |
|---|---|---|---|
| Launch options | Steam: `"…\OniMoono.exe" -- %command%` | where setup installed it (`%LOCALAPPDATA%\oni-moono\player`, or `player/` of a release on Linux) | run a newer release's setup |
| Standalone | `OniMoono.exe` itself (Explorer, a shortcut, a terminal; Linux: the launcher `oni-moono.desktop`) | the same, or a versioned install | as the folder it runs from |
| Versioned install | its entry point, `…\oni-moono\versioned\OniMoono.exe` (a shortcut you make, a non-Steam game in Steam, Steam's launch options) | `versioned\player\<version>\` | from its source folder (a Workshop item) at each start |

The launch-options route is unchanged by the other two: with `-- %command%` Moono takes the game
folder from Steam's command, as before.

## Standalone launch

Started without Steam's `-- %command%` and without `--game` (`src/OniMoono.Host/Launch`):

1. **The game** is found through Steam's libraries: Steam's install folder (Windows: the
   registry's `HKCU\Software\Valve\Steam\SteamPath`; Linux: `~/.steam/steam`,
   `~/.local/share/Steam`, the snap's and the Flatpak's, the places setup uses), each one's
   `steamapps/libraryfolders.vdf`, then app 457140's `appmanifest_457140.acf` in a library
   (`installdir`), or the library's `steamapps/common/OxygenNotIncluded`. With several installs
   the player is asked once (a message box per install on Windows; on Linux the terminal, or
   without one a desktop dialog, see [Linux launcher](#linux-launcher)), and
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
   `archived_versions`, the root. When that version isn't installed yet, the item's signature is
   verified first ([What the installer verifies](#what-the-installer-verifies)), and a version
   older than one installed of its line is refused unless pinned. Then its player is copied
   into `player\.incoming-…`, every file hashed as it is read and checked against the version's
   `SHA256SUMS` manifest, then moved to `player\<version>\`. A refused item, or a file missing
   or not matching, ends the install, deletes the copy, and the current version plays on.
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

**Tests** of the native host set `ONI_HOST_NO_DIALOG=1`: a failure to start then shows no
message box and touches no Player.log; its line goes to stderr, and to `ONI_HOST_MESSAGES`' file
when set. Without a redirected stderr, a failure is a message box on the desktop.

## Linux launcher

The Linux archive has `oni-moono.desktop` beside `setup.sh`: double-clicked in a file manager,
it starts `player/OniMoono` as a standalone launch, as double-clicking `OniMoono.exe` does on
Windows. A desktop entry's `Exec` can't be relative, so it runs
`sh -c '… exec "$(dirname "$1")/player/OniMoono"' oni-moono %k`: `%k` is the entry's own path,
which GNOME's (GLib's) and KDE's launchers, the Steam Deck's desktop mode included, fill in. From
a clone of the repository it runs `out/player/OniMoono`, which setup builds. The archive keeps it
executable (`scripts/check-linux-archive.py` checks the archive in `scripts/release.sh`).

- **Untrusted launchers.** A file manager may ask once before it runs a launcher it didn't
  install: Dolphin (KDE, Steam Deck) asks to execute it; on GNOME, right-click it and choose
  *Allow Launching* where offered. GNOME Files may open it as text instead; the menu entry below
  works there.
- **The applications menu.** `./setup.sh` asks on a terminal whether to add Moono to it:
  `~/.local/share/applications/oni-moono-player.desktop` (`XDG_DATA_HOME`), with the absolute path
  of the player setup installed and the icon in `~/.local/share/icons/hicolor/96x96/apps/`. A
  later setup keeps an entry it made up to date. `--menu` adds it without asking, `--no-menu`
  doesn't ask, `--remove-menu` removes both files. Its name is apart from the versioned
  install's `oni-moono.desktop`.
- **Messages.** Started without a terminal, Moono would say why it can't start only on stderr,
  where nobody sees it. So `StandaloneLaunch.Tell` also shows it in a dialog when stderr isn't a
  terminal: the first of `zenity`, `kdialog` and `notify-send` that runs (an exit code of 0, or
  1 for a dialog closed; anything else tries the next), else stderr alone
  (`src/OniMoono.Host/Launch/DesktopDialog.cs`). With several installs of the game and no
  terminal to ask on, the choice is a zenity list or a kdialog menu (cancelled: none chosen,
  Moono says so); with neither, the first install.
- **No dialogs** in test runs (`ONI_MOONO_STEAM_ROOTS` set), with `ONI_HOST_NO_DIALOG=1` (as the
  Windows host reads it; on Windows it now also keeps the player's own message boxes away, and
  e2e runs set it), or without `DISPLAY` and `WAYLAND_DISPLAY`.

## Workshop item

The Workshop item carries both branches in the game's `archived_versions` convention, so the
same rule gives each game build its Moono:

```
<item>/                         the u60 build: minimumSupportedBuild 762230
  mod_info.yaml  moono.json  oni-moono-…-u60-windows-x64.SHA256SUMS  oni-moono-…-u60-linux-x64.SHA256SUMS
  windows-x64/  linux-x64/      the release archives' oni-moono folders
  OniMoono.exe  OniMoono.sh     start here once (Windows: double-click; Linux: sh OniMoono.sh)
  verify.ps1  verify.sh  README.md  SHA256SUMS
  attestation/SHA256SUMS.sigstore.json   the signature of SHA256SUMS (not listed in it)
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
  the version for the game as above, and the entry point, then hands over to the
  entry point. The item's folder itself never runs, so Steam can always update it; later starts
  install each update from it. The bootstrap copy verifies the item as every start does, so an
  item whose files aren't as signed installs nothing even on the first start.
- **Built in CI** (`.github/workflows/workshop.yml`, run by hand with a master and a u60 release
  tag): the releases' assets are downloaded and verified against their provenance and branch,
  `scripts/workshop-item.py` checks each archive against its manifest while laying them out,
  and `attest.yml` signs the item's `SHA256SUMS` and every file in it. The Sigstore bundle of
  the attestation naming `SHA256SUMS` goes into the item as `attestation/SHA256SUMS.sigstore.json`,
  and `tools/ItemVerify` checks the item with the installer's own code and trust root before it
  becomes the `workshop-item` artifact. Nothing goes to Steam. `verify.sh` or `verify.ps1` in the
  item checks it against the attested release manifests and its own attested `SHA256SUMS`, the
  bundle included (docs/RELEASES.md).

## What the installer verifies

Steam updates the item's folder whenever its owner uploads, so the folder alone proves nothing:
whoever controls the Steam account could upload an item with matching `SHA256SUMS` files. (A
hijacked Steam account pushing an info-stealer as a game's update has happened before.) So
before it installs any version from a source folder, Moono checks that the item is what this
repository's Actions signed (`src/OniMoono.Host/Install/ItemTrust.cs`, `Sigstore.cs`). Nothing is
fetched over the network. These are the checks:

1. **The bundle** `attestation/SHA256SUMS.sigstore.json` is a Sigstore bundle v0.3 with one DSSE
   envelope, whose ECDSA P-256 signature over the envelope's PAE verifies with the key of the
   bundle's certificate.
2. **The transparency log.** The bundle's Rekor entry is from a log in the trusted root, whose key
   was valid at the entry's time. Its body is this envelope (payload hash, signature,
   certificate). Its signed entry timestamp verifies with the log's key, and so does its Merkle
   inclusion proof (RFC 9162) up to a checkpoint that the log's key signed.
3. **The certificate** was valid at the entry's integrated time. It is a code-signing leaf, issued
   by a Fulcio authority of the trusted root (valid at that time), whose chain to its root checks
   too. It carries an SCT signed over its precertificate by a CT log of the trusted root.
4. **The identity**, from Fulcio's certificate extensions:
   - the SAN is `https://github.com/tigin-backwards/oni-moono/.github/workflows/attest.yml@refs/heads/master`
     (or `…@refs/heads/u60`), and the build signer is the same;
   - the OIDC issuer is `https://token.actions.githubusercontent.com`;
   - the source repository is `https://github.com/tigin-backwards/oni-moono`, with its repository
     and owner IDs (1421479132, 275280754), so a repository made under that name later doesn't
     pass;
   - the source ref is `refs/heads/master` or `refs/heads/u60`;
   - the workflow is `.github/workflows/workshop.yml`, so a release's attestation doesn't pass
     as an item's;
   - the runner is GitHub-hosted.
5. **The statement** is an in-toto v1 statement, and one of its subjects is
   `workshop-item/SHA256SUMS` with the SHA-256 of the item's `SHA256SUMS`.
6. **The version's files.** The chosen version's `moono.json`, `mod_info.yaml` and its release
   manifest (`oni-moono-…-<platform>.SHA256SUMS`) must be listed in that `SHA256SUMS` with those
   hashes. Each is read once, checked, and the checked bytes are used. Then every player file is
   checked against the release manifest as it is copied (above).
7. **No rollback.** A version older than the newest installed one of its line is refused, unless
   `--use-version <version>` pinned it. A version is `YYYY.MM.DD.NN` (master) or
   `YYYY.MM.DD.NN-u60` (u60), and the two lines are compared apart: the item's U59 build is
   often a release behind its U60 build.

**Not checked:** revocation (Fulcio publishes none, and its certificates live ten minutes), RFC 3161 timestamps (Rekor's integrated time is the time used), Rekor v2
entries (Ed25519 logs, which have no signed entry timestamp), and bundles older than v0.3. A
bundle that needs any of these fails the check: Moono refuses rather than passing it.

**The trust root** is Sigstore's public-good trusted root (Fulcio `fulcio.sigstore.dev`, Rekor
`rekor.sigstore.dev`, the CT log `ctfe.sigstore.dev`), built into the player as
`src/OniMoono.Host/Install/SigstoreTrustedRoot.cs`. To refresh it, run
`scripts/sigstore-trusted-root.py`. It takes `gh attestation trusted-root` (which gh verifies
against Sigstore's TUF repository) and keeps the public-good instance. Refresh it when Sigstore
adds or rotates a key, before an item signed with the new key is uploaded. `workshop.yml`
refuses an item that the installer would refuse, because it runs the same code. A player whose
root is too old refuses newer items, so it plays on. Then a first start from the item, or a
release's setup, brings the new root.

**A refusal** installs nothing. The current version plays on, and the log says why, as in
`install: version 2026.11.09.01-u60 from … refused: the Workshop item is not as Moono's release
workflow signed it: its SHA256SUMS (SHA-256 …) is not the one its signature covers`. The player is
told once per refused version, in a message box (Windows) or desktop dialog (Linux): "Moono did
not install version 2026.11.09.01-u60 from the Workshop item: the Workshop item's files are not
the ones Moono's releases signed. Version 2026.11.08.01-u60 plays on; Player.log says why."
On a first start with nothing installed yet, it says "Moono could not be installed: …" and
exits.

**The first install** comes from the item's own `OniMoono.exe` (or `OniMoono.sh`), and a program
can't vouch for itself. A tampered item whose bootstrap player is genuine fails visibly, as
above. But a swapped bootstrap player can do whatever it likes. The GitHub releases are the root
of trust: for a first install you can be sure of, use a release's setup
([RELEASES.md](RELEASES.md#verifying-a-release)), or run the item's `verify.ps1` or `verify.sh`
before its first start. After that, every update is checked.
