# runq: running jobs on the shared test machine

runq is the run broker of the shared WSL test machine: one daemon, `runqd`, owns the machine's
shared resources (the Windows game slot, the perf slot, e2e lanes, the players' memory and CPU),
and agents hand it their commands with `scripts/runq`. It queues each one, starts it when all of
its resources are free, isolates it, stops it by its systemd unit when it ends, times out or
idles, and wakes the caller when it ends. The design, with the reasons behind each rule, is
[research/runq-design.md](research/runq-design.md); this page is how to use what is built
(phase 1 of that design).

## Running a job

```sh
scripts/runq run --kind KIND [--level LEVEL] [--timeout DUR] -- CMD [ARG...]
```

`runq run` prints the job id, its output folder and its log on stderr, streams the job's output
(stdout and stderr together), and exits with the command's exit code. A caller whose shell dies
can `runq wait ID` later: stopping `runq run` does not cancel the job unless it was given
`--cancel-on-detach`. `runq submit` queues and prints the id at once.

| Command | What it does |
|---|---|
| `runq run [OPTIONS] -- CMD` | queue, wait, stream the output, exit with the command's code |
| `runq submit [OPTIONS] -- CMD` | queue, print the job id |
| `runq wait ID...` | wait for each; exit with the first failure's code |
| `runq cancel ID` | dequeue, or stop a running job (SIGTERM, SIGKILL after 30 s) |
| `runq status [--json] [--owner X]` | what runs, what waits and why, what runs outside runq, warnings |
| `runq show ID`, `runq log ID [-f]`, `runq history` | one job's record, its output, past jobs |
| `runq phase --kind K` | inside a job: give back what it holds, then wait for K's resources |
| `runq beat` | inside a job: a heartbeat, for `--heartbeat DUR` |
| `runq hold windows [--for DUR]`, `runq release windows` | keep the Windows slot for the user (default 3 h) |
| `runq ping`, `runq install`, `runq doctor` | start the daemon when it is down; install it; check it |

Exit codes: the command's own, or 124 (timeout), 137 (cancelled, idle-held, no heartbeat, or a
self-deadlock), 75 (refused at submit: a cap, an unknown level or kind), 76 (lost to a WSL
restart), 69 (no daemon).

Options: `--owner NAME` (default `$RUNQ_OWNER`, else the worktree's folder name), `--level`,
`--kind`, `--needs R=N,...`, `--timeout DUR`, `--expect DUR`, `--label TEXT`, `--after ID`
(`--after-ok`), `--script FILE` (runs a copy kept in the job folder, so a wiped scratchpad loses
nothing), `--env NAME`, `--heartbeat DUR`, `--retry-on-lost`, `--keep-home`, `--budget-ok WHY`.
`RUNQ_LEVEL`, `RUNQ_OWNER`, `RUNQ_TIMEOUT` and `RUNQ_EXPECT` in the environment stand in for the
options (`RUNQ_TIMEOUT` and `RUNQ_EXPECT` win over a script's own estimate).

### Levels

`issue` (GitHub issue work), `release` (release QA), `user` (the user's requests), `study` (A/B
and measurement series), `burndown`. The default is `study`. The agent passes its own level;
`issue` and `release` are allowed and logged in `~/.local/state/oni-runq/levels.log`. A queued
job is due after its level's bound (issue and release at once, user 30 min, study 3 h, burndown
8 h); due jobs go ahead of every job not yet due, by level and then by submit time, so a
higher-level job that becomes due goes ahead of lower-level jobs due earlier. Running jobs are
never preempted. Within a level, the owner with the least service over the last 24 h goes first,
so one agent's back-to-back runs interleave with another's.

### Kinds and resources

| Kind | Resources | Timeout |
|---|---|---|
| `light` | none (runs beside a perf job) | 30 min |
| `generic` (the default) | cpu=2, mem=2 | 60 min |
| `build` | cpu=6, mem=6 | 30 min |
| `check` | cpu=8, mem=8 (never beside a perf job) | 45 min |
| `linux-player` | a lane, mem=6, cpu=3, a player | 60 min |
| `e2e` | lanes, players, memory and cores as `scripts/e2e.py` computes them | 3 h |
| `windows` | the Windows slot | 60 min |
| `win-perf` | the Windows slot, the perf slot, a quiet machine; High priority for the game | 45 min |
| `linux-perf` | the perf slot, a quiet machine, mem=15, cpu=4, a player | 60 min |
| `qa` | `qa.sh`'s stages, one `runq phase --kind qa-STAGE` each | 3 h |

The machine: lanes 1-19 (less reserved and busy ones), 40 GB and 4 players, 14 cores, one
Windows slot, one perf slot. While a perf job runs nothing else that loads the machine runs
(only `light` jobs); a perf job at the head of the queue holds new work back until the running
jobs end. A job starts only when every resource it needs is free; it never holds part of them.
A later job may start ahead of a waiting one only when it fits beside that one's reservation for
its whole timeout. `quiet` waits at most 15 minutes for the CPU outside runq to go under 2
cores, then starts the job flagged noisy (`RUNQ_NOISY=1`, and `noisy` in its record). A
job that needs the Windows slot starts while the user is in ONI (a game of theirs runs: one
started from a Steam library or Moono's own install, `%LOCALAPPDATA%\oni-moono`, with no test run
home; Steam's RunningAppID counts for nothing, as test games set it and it outlives them) or while
the slot is held for them (`runq hold windows [--for 3h]`, `runq release windows`; `runq status`
shows the hold). Such a job waits holding nothing, and others run meanwhile.

### Caps

2 running jobs per owner (burndown: 1), 20 queued, and 4 h of the Windows or perf slot queued or
running per owner (counted by `--expect`, else the label's median duration, else the timeout).
More is refused with exit 75 and the forecast; ask the user, then pass `--budget-ok 'REASON'`.

### Leases

A job past its timeout is stopped (124). A job that holds the Windows or perf slot with no game
of its run home and its cgroup under 2% of a core is flagged after 10 minutes and stopped after
20 (`idle-held`, 137): a pause between launches is `runq phase` or a chain of jobs, never a sleep
inside a held slot. A wrapped script that takes `perf.lock` or `windows-game.lock` itself, which
runq already holds for its job, would wait forever: it is stopped at once (`self-deadlock`).

## What each job gets

- A folder of its own, `~/oni-runs/ID/`: `out.log`, `result.json`, `job.json`, `cmd` (the
  command, or the `--script` copy), and `out/` (`$RUNQ_OUT`) for the job's artifacts. Folders stay
  14 days (failed jobs 30, `touch ~/oni-runs/ID/keep` pins one), 50 GB in all.
- A transient systemd service, `runq-ID-N.service`, in `oni-tests.slice` (OOM score 1000, the
  slice's memory cap). The daemon starts the command, not the caller's shell; the command runs in
  the caller's working folder with the caller's environment filtered: `PATH`, `HOME`, `LANG`,
  `DOTNET_*`, `E2E_*`, `QA_*`, `ONI_*`, `ALLOC_*`, `MESA_*`, `GALLIUM_DRIVER`, `WSLENV` and a few
  more stay; `DISPLAY`, `WAYLAND_DISPLAY`, `PULSE_SERVER` and SSH agents go. `--env NAME` keeps
  another.
- `RUNQ_JOB`, `RUNQ_DIR`, `RUNQ_OUT`, `RUNQ_OWNER`, `RUNQ_LEVEL`, `RUNQ_KIND`, `RUNQ_LANES` (the
  granted lane numbers, `3,4`), `E2E_MEMORY_GB` and `E2E_MAX_PLAYERS` for jobs with players,
  `ONI_CONTAINED=1`, `ONI_HOST_NO_DIALOG=1` listed in `WSLENV`, and no sound (`PULSE_SERVER` to
  nothing, `SDL_AUDIODRIVER=dummy`).
- Windows processes started the way a terminal starts them: through a WSL session's interop
  socket (`WSL_INTEROP`), one the daemon checks. A systemd unit's default, the socket of WSL's
  own init, starts them with Windows' PreferSystem32Images mitigation, which their children
  inherit: a DLL beside an executable then loses to System32's of the same name (Unity
  Doorstop's `winhttp.dll` in the original's copy never loaded, so `original-windows.sh` failed
  its isolation check). Windows-slot jobs wait while no such socket exists (a WSL terminal
  provides one); `runq status` names the one in use.
- With the Windows slot: `%TEMP%\oni-runq\ID\stage` (`ONI_WIN_STAGE`, where `run-windows.sh`
  stages the host) and `%TEMP%\oni-runq\ID\home` (`ONI_RUN_HOME`, unless the caller set its own),
  the run home's volume settings at 0, and `ONI_WIN_MONITOR=2` whatever the caller set. When the
  job ends, its game is killed by its run home (never by process name) and the folder is removed
  (`--keep-home` keeps it). A game started while the job holds the slot under a run home of its
  own (a runner that picks its homes) is tied to the job when the daemon sees it, and killed with
  it by pid and start time. Every 30 s the daemon reads the job's game command line and warns in
  `runq status` when our player lacks `--no-focus`, a game is not on monitor 2, or a game was
  started with the flag that pins the pointer over it. Every Windows job's game gets power
  throttling (EcoQoS) off, as a throttled test game runs many times slower; a `win-perf` job's
  also gets the High priority class, the others Normal. runq never focuses a window or moves the pointer.
- No contact with the user's Steam: a Windows job whose game Steam tracks as ONI running (Steam's
  `logs\gameprocess_log.txt`) is stopped (`steam-contact`), and test games outside runq that Steam
  tracks are named under OUTSIDE RUNQ.
- A heartbeat lease with `--heartbeat DUR`: `runq beat` from inside the job.

## The migrated scripts

`scripts/check.sh`, `scripts/e2e.py`, `scripts/qa.sh`, `scripts/alloc-compare.py run`,
`scripts/run-windows.sh`, `scripts/original-windows.sh` and `scripts/run.sh` queue themselves
through runq: run them as before and they re-run as a job of their kind.

| Script | Kind |
|---|---|
| `check.sh` | `check` |
| `e2e.py` | `e2e`: a lane, a player and 3 cores per `--lanes`, the specs' largest memory; the timeout from the number of runs |
| `qa.sh` | `qa`, then `qa-unit`, `qa-dogfood`, `qa-verify`, `qa-perf` (the perf slot, quiet) and `qa-memory` per stage |
| `alloc-compare.py run` | `linux-perf` (memory `ALLOC_GB`) |
| `run-windows.sh`, `original-windows.sh` | `windows`, or `RUNQ_KIND=win-perf` for frame-rate runs |
| `run.sh` | `linux-player` |

Inside a job (`RUNQ_JOB` set) they run directly, so a script the daemon runs never queues itself
again: `e2e.py` then uses the granted lanes and neither waits for perf jobs nor counts its
players in the budget (runq's grant stands in for both), `qa.sh`'s perf stage takes no lock and
waits for no quiet machine, and `alloc-compare.py` skips its perf queue.

Other scripts that take `perf.lock`, `perf.wanted` or `windows-game.lock` themselves (the local
sweep and queue scripts) must drop those claims before they run as runq jobs: runq takes the
locks for its jobs.

### The kill switch

`RUNQ=off` in the environment, or `enabled = false` in `~/.config/oni-runq/config.toml`, sends
the migrated scripts back to their old direct path, with the old locks. So does a daemon that
does not answer, with one line on stderr, and a GitHub Actions runner (`GITHUB_ACTIONS=true`):
runq would start a daemon there, and a script queued through it would lose the caller's X
display, as every job does.

## Living beside the old locks

Until no script takes them directly, runq and the old locks see each other:

- A runq job's shim takes the old locks of its grant without blocking, perf first, then
  windows: `perf.lock` exclusively and `perf.wanted` for a perf job, `windows-game.lock` for the
  Windows slot, `lane-L.lock` for its lanes (`e2e.py` takes its own), and `players.json` entries
  for its players. If an old script took one in between, the job is requeued ahead of its level.
- Holders of the old locks outside runq count as occupants: a shared `perf.lock` holder or a
  `players.json` entry as a player, an exclusive holder or a live `perf.wanted` as a perf job,
  `windows-game.lock` or a test game started outside runq as the Windows slot taken. So do a
  running `qa.sh` (15 GB) and the sweep and measurement scripts `e2e.py` calls perf jobs.
  `runq status` lists them under OUTSIDE RUNQ.
- While a perf job waits for load to end, the daemon writes its own pid to `perf.wanted`, so old
  spec runners start no new spec. It does the same once a job is due and only a perf job outside
  runq holds it up, so the old perf claimers start no new run and the wait ends with theirs.
- A migrated script whose caller already holds `perf.lock`, `windows-game.lock` or `perf.wanted`
  (an old-style wrapper, `flock -x perf.lock scripts/qa.sh`) runs directly, as before.

## The daemon

`oni-runqd.service`, a systemd user unit, runs `~/.local/share/oni-runq/VERSION/runq _daemon`
(`runq install` copies the scripts there, so a removed worktree takes nothing with it). The CLI
installs and starts it when it does not answer; there is no linger, so it runs while a session
of the user's does. Its state is `~/.local/state/oni-runq/` (`runq.db`, SQLite; `runq.sock`, mode
0600). Jobs do not depend on it: a daemon restart finds them again by unit name, and a WSL
restart marks the jobs that ran lost (76) and requeues those with `--retry-on-lost`.
`journalctl --user -u oni-runqd` has its log.

Settings are in `~/.config/oni-runq/config.toml`; every default is in `scripts/runqlib/config.py`
(levels, wait bounds, caps, capacity, kinds, idle and quiet limits). For example:

```toml
default_level = "study"
[due_minutes]
study = 120
[caps]
slot_hours_per_owner = 6.0
```

`runq install` again after a change to `scripts/runq` or `scripts/runqlib` (on master) installs
the new version and restarts the daemon; running jobs go on.

## For agent briefs

> Run every player, build, check.sh, e2e, perf or Windows run through runq: `scripts/check.sh`,
> `e2e.py`, `qa.sh`, `alloc-compare.py run`, `run-windows.sh`, `original-windows.sh` and `run.sh`
> queue themselves; anything else goes through `scripts/runq run --kind KIND -- CMD`. Never take
> perf.lock, perf.wanted or windows-game.lock yourself, and drop them from scripts you wrap.
> - Set `RUNQ_OWNER=NAME` (your task's short name) and `RUNQ_LEVEL=LEVEL`: `issue` for GitHub issue
>   work, `release` for release QA, `user` for the user's requests, `study` for A/B or
>   measurement series (the default), `burndown` for burndowns.
> - KIND: `check`, `e2e`, `linux-player`, `windows`, `win-perf` (frame rates; also
>   `RUNQ_KIND=win-perf scripts/run-windows.sh`), `linux-perf`, `build`, `light`, `generic`.
>   More than 4 h of the Windows or perf slot needs the user's OK (`--budget-ok`).
> - Long runs: start them with the Bash tool's run_in_background and do other work or end your
>   turn; you are woken when they exit. `runq run` prints the job id and `~/oni-runs/ID/out`
>   (`$RUNQ_OUT`) first; `runq wait ID` picks a job up again. Do not poll `runq status` in a loop,
>   and do not hand back while a run of yours is queued or running.
> - Stop a run with `runq cancel ID`. Never `pkill -f`, `killall` or kill by name; never touch a
>   Windows process except through runq.
> - Windows runs: runq gives each its own stage folder and run home, ONI_HOST_NO_DIALOG in
>   WSLENV, volume 0 and monitor 2; keep `--no-focus`, never pass the background flag, never move
>   the pointer or take the focus.
> - `runq status` says what runs and why a job waits. `RUNQ=off` runs a script the old way.
