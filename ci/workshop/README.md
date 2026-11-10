# Moono for Oxygen Not Included

Moono runs Oxygen Not Included on its own engine (unofficial). This folder holds Moono's
players and a small loader mod, `MoonoLoader.dll`: on the game's U60 update, the game shows a
dialog at the main menu that installs Moono and copies the line for ONI's launch options in
Steam (the item's row in the Mods screen shows it again). Disable the mod to stop it.

- **Install from the game:** press Copy in that dialog, then in Steam: right-click Oxygen Not
  Included → Properties → Launch Options, paste, then restart the game.
- **Install by hand:** Windows: double-click `OniMoono.exe` here, once. Linux: run `sh OniMoono.sh` here, once.
  Moono copies itself to `%LOCALAPPDATA%\oni-moono\versioned` (Linux:
  `~/.local/share/oni-moono/versioned`) and makes no shortcut. After that, start the
  `OniMoono.exe` in that folder, not this one: from a shortcut you make to it, as a non-Steam
  game in Steam, or from ONI's Steam launch options (see the install guide). Steam updates this
  folder, and Moono installs each update the next time it starts.
- **Versions:** the root is the build for the game's U60 update; `archived_versions/u59` is the
  build for U59. Moono picks the one for your game's build, as the game picks a mod's version.
- **Checked files:** Moono and the loader mod check each player file against its release's
  manifest as they copy it, so a download Steam hasn't finished installs nothing. That is for
  integrity, not a signature check: like any mod, this folder is trusted as Steam delivers it.
- **Uninstall:** clear Oxygen Not Included's launch options in Steam, then delete
  `%LOCALAPPDATA%\oni-moono` (Linux: `~/.local/share/oni-moono`).
- **Check it:** `verify.ps1` (Windows) or `verify.sh` (Linux) checks every file here against
  Moono's attested GitHub builds, with the GitHub CLI. Use the copies from GitHub, not these:
  whoever controls this folder can change them (docs/RELEASES.md). For an install you can be sure
  of, install from a GitHub release instead.

More: https://github.com/tigin-backwards/oni-moono (docs/INSTALL-MODES.md).
