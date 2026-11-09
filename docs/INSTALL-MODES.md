# Install modes

How Moono starts and where it is installed. Two ways exist side by side:

| Mode | Started by | Player folder | Updates |
|---|---|---|---|
| Launch options | Steam: `"…\OniMoono.exe" -- %command%` | where setup installed it (`%LOCALAPPDATA%\oni-moono\player`, or `player/` of a release on Linux) | run a newer release's setup |
| Standalone | `OniMoono.exe` itself (Explorer, a shortcut, a terminal) | the same | as the folder it runs from |

The launch-options route is unchanged by the standalone one: with `-- %command%` Moono takes the game
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
