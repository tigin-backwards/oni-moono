# Moono for Oxygen Not Included

Moono runs Oxygen Not Included on its own engine (unofficial). This folder holds Moono's
players; it is not a mod, and the game itself loads nothing from it (its mod list shows it with
no supported content).

- **Install:** Windows: double-click `OniMoono.exe` here, once. Linux: run `sh OniMoono.sh` here, once.
  Moono copies itself to `%LOCALAPPDATA%\oni-moono\versioned` (Linux:
  `~/.local/share/oni-moono/versioned`) and makes no shortcut. After that, start the
  `OniMoono.exe` in that folder, not this one: from a shortcut you make to it, as a non-Steam
  game in Steam, or from ONI's Steam launch options (see the install guide). Steam updates this
  folder, and Moono installs each update the next time it starts.
- **Versions:** the root is the build for the game's U60 update; `archived_versions/u59` is the
  build for U59. Moono picks the one for your game's build, as the game picks a mod's version.
- **Checked files:** Moono checks each player file against its release's manifest as it copies
  it, so a download Steam hasn't finished installs nothing. That is for integrity, not a
  signature check: like any mod, this folder is trusted as Steam delivers it.
- **Check it:** `verify.ps1` (Windows) or `verify.sh` (Linux) checks every file here against
  Moono's attested GitHub builds, with the GitHub CLI. Use the copies from GitHub, not these:
  whoever controls this folder can change them (docs/RELEASES.md). For an install you can be sure
  of, install from a GitHub release instead.

More: https://github.com/tigin-backwards/oni-moono (docs/INSTALL-MODES.md).
