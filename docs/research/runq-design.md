# runq: a run broker for the shared test machine

Status: phase 1 built (2026-11-12; [RUNQ.md](../RUNQ.md) is how to use it). Phases 2-4 are not. Written 2026-11-12.

## 1. Summary

**Problem.** Many agents share one WSL machine with 16 cores, 62 GB, 19 e2e lanes and a single
Windows game slot. Each agent writes its own claim scripts around `perf.wanted`, `perf.lock`,
`windows-game.lock` and `ps` greps, so every new driver can break the others. In the last two
days:

| Failure | Root cause |
|---|---|
| A runner held `perf.wanted` while sleeping for over an hour | Claims are files a script holds; nothing sees that the holder is idle |
| Two pair studies waited about 8 hours for a quiet machine | "Quiet" was polled for; nothing held new work back |
| Run chains re-claimed the queue the instant it freed | No queue, only a lock race the fastest poller wins |
| A run took `windows-game.lock` beside a live game, then deleted a shared stage folder under it | The other script held no flock; `run-windows.sh` stages into one fixed `%TEMP%\oni-brutal\host` |
| A cleanup removed a QA agent's in-use worktree | No owner record for worktrees |
| Watchers grepped `ps` for script names and fired wrongly | No job identity to wait on |
| A session restart wiped `/tmp` scratchpads with scripts and run data | Run data lives where the agent happens to write it |
| An agent planned 43 runs for an 80 ms gain | No cost is shown or limited |
| Agents polled and handed back mid-task | Nothing wakes them when a run ends |
| check.sh tests were flaky under CPU load from perf runs | check.sh is not scheduled against anything |
| Agents killed their own shells with `pkill -f` | Killing by pattern is the only tool they have |

**Proposal.** One small daemon, `runqd`, owns every shared resource. Agents never take locks:
they hand a command to `scripts/runq`, which queues it, starts it when its resources are free,
isolates it, kills it by its cgroup and run home when it ends or hangs, and wakes the caller on
completion. Resources are granted all at once, so no job ever holds one lock while waiting for
another. Python 3 standard library only (sqlite3, socket, selectors, json, subprocess); systemd
user units for supervision.

**Phases.** (1) core: run/submit/wait, resources, priorities, leases, isolation, status, legacy
bridge, migration shims: about 6 agent-days. (2) status page and history: 1 day. (3) worktree
registry: 1.5 days. (4) release freeze and merge queue: 1.5 days. Section 13 has the details.

## 2. What exists now

| Mechanism | Where | What it does | Gap |
|---|---|---|---|
| Lanes | `scripts/e2e.py` (`try_lane`, `allocate_lane`) | `lane-L.lock` flocks in `~/.cache/oni-brutal-e2e`, ports 8800+10L and 8801+10L, Xvfb `:300+L`; leftovers of dead runners cleaned | Only e2e.py uses them |
| Memory budget | `e2e.py` `Budget` | `players.json` ledger and a FIFO `waiters.json`, 40 GB and 4 players by default; a running qa.sh counts as 15 GB, found by `ps` | FIFO only, no priorities; polled every 5 s |
| Perf lock | `e2e.py` `machine_lock`, `qa.sh` `quiet_begin`, `alloc-compare.py` `PerfQueue` | `perf.lock` shared by specs, exclusive for perf jobs; `perf.wanted` holds the waiting perf job's pid; `PERF_JOBS` regexes over `ps` | Three implementations; a sleeping holder blocks all; regexes miss new driver names |
| Quiet machine | `qa.sh` `quiet_begin` | After the exclusive lock, waits up to 30 min for under 2 busy cores and no Windows game | Polls; others keep starting work meanwhile |
| Windows slot | `windows-game.lock` (convention, no script owns it) | Agents flock it around Windows runs | Not every script takes it; order versus perf only in a memory note |
| Containment | `scripts/contain.sh`, `oni-tests.slice` | systemd scope in a 50 GB slice, OOM score 1000, no WSLg display or audio | Fine; runq builds on it |
| Windows process control | `out/local-tools/win-run.ps1`, `win-boost.ps1` | Find, kill, focus or boost a game by its run home in the command line, never by name | Fine; runq builds on it |
| Windows staging | `scripts/run-windows.sh` | Copies `out/win` to a fixed `%TEMP%\oni-brutal\host` with `rm -rf` first | Shared by all runs: one run's staging deletes another's live player |

The design keeps what works (cgroup containment, kill by run home, lane ports and displays) and
replaces the claims, waits and `ps` greps with one scheduler.

## 3. Architecture

### 3.1 Parts

- **`runqd`**, the daemon. One process; single-threaded event loop (`selectors`) plus a worker
  thread for slow probes (PowerShell calls take 0.5-1 s). It owns the SQLite database and makes
  every scheduling decision.
- **`scripts/runq`**, the CLI. Talks to the daemon over a Unix socket with JSON lines. Thin: it
  validates arguments, sends the request and streams replies.
- **`runq-shim`**, the first process of every job. It takes the legacy flocks for the job (3.5),
  writes heartbeats, starts the command as its child, and writes `result.json` when the command
  ends. It stays the job's main process, so the exit status survives a daemon restart.
- **`runq-cleanup`**, run by systemd after every job (`ExecStopPost`), however the job ended:
  kills the job's Windows game by run home, removes its Windows stage folder, removes
  `perf.wanted` if it names the job.

### 3.2 Where things live

| What | Path | Why |
|---|---|---|
| Installed code | `~/.local/share/oni-runq/<version>/` | The daemon must not run from a worktree that may be removed; `runq install` copies the scripts there, as contain.sh installs its slice |
| Database | `~/.local/state/oni-runq/runq.db` (SQLite, WAL) | Persistent, survives WSL and session restarts |
| Socket | `~/.local/state/oni-runq/runq.sock`, mode 0600 | Same user only |
| Job folders | `~/oni-runs/<job id>/` | Persistent run data: `job.json`, `cmd` (a copy of the submitted script), `out.log`, `result.json`, `out/` for artifacts |
| Windows job folders | `%TEMP%\oni-runq\<job id>\` with `stage\` and `home\` | One per job; nothing shared |

Job ids are short and sortable: `j0001234`. A job folder is never reused.

### 3.3 Start-up and supervision

- A systemd user unit `oni-runqd.service` (`Restart=always`, `RestartSec=2`), installed and
  enabled by `scripts/runq install`. `systemctl --user` already runs on this machine
  (contain.sh relies on it).
- `loginctl enable-linger` keeps the user manager alive without an open terminal. To check in
  phase 1: whether linger also stops WSL from idling the VM while jobs run, and whether that is
  wanted.
- The CLI starts nothing itself. If the socket is missing it prints how to start the daemon and
  exits 69, so a broken daemon is loud, not silently bypassed.

### 3.4 How jobs run

The daemon starts each job as a transient systemd service:

```
systemd-run --user --unit=runq-j0001234 --slice=oni-tests.slice \
  -p OOMPolicy=continue -p OOMScoreAdjust=1000 -p KillMode=control-group \
  -p TimeoutStopSec=15 -p ExecStopPost='runq-cleanup j0001234' \
  --working-directory=WORKTREE -- runq-shim j0001234
```

- The job's processes are the unit's cgroup. Killing a job is `systemctl --user stop
  runq-j0001234`: SIGTERM to every process in it, then SIGKILL after 15 s. No pattern matching.
  The SIGTERM-first order matters: a player killed in a GPU wait on WSL was followed by a
  kernel soft lockup (qa.sh's note).
- Jobs do not depend on the daemon. If it crashes, they keep running, and the restarted daemon
  finds them by unit name.
- The unit lives in `oni-tests.slice` with contain.sh's settings, and `ONI_CONTAINED=1` is set,
  so contain.sh inside a job execs directly or makes a player scope (`ONI_SCOPE`), as it does
  today.
- cgroup accounting comes free: `cpu.stat` and `memory.peak` per job feed idle detection (6.3)
  and the history (8.3).

**Permission model.** The agent's tool call is `runq run ... -- CMD`, so the command is still
visible where the agent's permissions are checked. The daemon runs exactly that command, in the
caller's worktree, with the caller's environment filtered (3.6). Nothing else can submit
commands: the socket is the user's only.

### 3.5 Bridge to the legacy locks

During migration, runq and old scripts must see each other.

- **runq jobs appear to old scripts.** A job granted `perf` gets `perf.wanted` written with the
  shim's pid and an exclusive `perf.lock` taken non-blocking by the shim. A job granted
  `windows` gets `windows-game.lock`. A job's players get entries in `players.json`. The shim
  holds the flock fds without close-on-exec, so the locks last exactly as long as the job's
  processes. If the shim loses a race for a legacy lock (an old script took it in between), it
  exits with a "lost race" code and the daemon requeues the job ahead of its level.
- **Old scripts appear to runq.** Each scheduling pass reads `/proc/locks` (e2e.py's
  `flock_holders` already does this) and `players.json`. A flock or ledger entry held by a
  process outside any runq unit counts as an external occupant: shared `perf.lock` holders as
  players, an exclusive holder as a perf job, a `windows-game.lock` holder as the Windows slot.
- Lock order stays perf then windows, but no longer matters for runq jobs: the daemon checks
  all legacy locks are free before granting, and the shim takes them non-blocking.

The bridge goes away in phase 1c's last step, once no script takes the files directly.

### 3.6 Environment

The client sends its cwd, argv and environment. The daemon keeps an allowlist (PATH, HOME,
DOTNET_*, `E2E_*`, `QA_*`, `ONI_*`, `ALLOC_*`, `GALLIUM_DRIVER`, `MESA_*`, LANG and friends,
plus `--env NAME`) and drops the rest (DISPLAY, WAYLAND_DISPLAY, PULSE_SERVER, SSH agents). It
then adds the job's variables (5.2).

## 4. Job model

### 4.1 Commands

```
runq run   [OPTIONS] -- CMD [ARG...]   queue, wait, stream out.log, exit with CMD's code
runq submit [OPTIONS] -- CMD [ARG...]  queue, print the job id, return at once
runq wait  ID...                       block until each ends; exit with the first failure's code
runq cancel ID                         dequeue, or stop a running job (SIGTERM, then SIGKILL)
runq show  ID                          the job's record, resources, waits and result
runq log   ID [-f]                     out.log
runq status [--json] [--owner X]       running, queued, why each waits, holds by owner
runq history [--owner X] [--since D]   past jobs
runq stats [--owner X]                 machine-hours by resource, waits p50/p95, success rate
runq phase RESOURCES                   inside a job: release this job's resources, wait for others (4.4)
runq beat                              inside a job: a heartbeat (6.3)
```

`runq run` prints the job id and the output folder first, on stderr, so a caller whose shell
dies can `runq wait ID` later. A client that disconnects does not cancel the job unless it
passed `--cancel-on-detach`.

Exit codes: the command's own code, or 124 timeout, 137 killed (cancel or idle), 75 refused at
submit (a cap, 4.3), 76 lost (a WSL restart, 10), 69 no daemon. The output folder is
`~/oni-runs/<id>/out`, exported as `RUNQ_OUT`.

### 4.2 Options

| Option | Meaning | Default |
|---|---|---|
| `--owner NAME` | Who asks; the unit of fairness and caps | `$RUNQ_OWNER`, else the worktree's registry owner, else refused |
| `--priority LEVEL` | `issue`, `release`, `user`, `study`, `burndown` | `$RUNQ_PRIORITY`, else `user` |
| `--label TEXT` | What the run is, for status and for duration estimates (`win-perf ours baseline`) | the command's first words |
| `--kind KIND` | A preset of resources (4.3) | `generic` |
| `--needs R[=N],...` | Resources beyond the kind's | from the kind |
| `--timeout DUR` | Hard limit; required for `perf` and `windows` | the kind's; at most 6 h |
| `--expect DUR` | Expected duration, for forecasts | the label's median from history |
| `--after ID` | Start only after ID ended (successfully, with `--after-ok`) | |
| `--script FILE` | Copy FILE into the job folder and run the copy | |
| `--retry-on-lost` | Requeue if a WSL restart lost the job | off |
| `--heartbeat DUR` | Kill the job when no `runq beat` comes for DUR | off |

### 4.3 Resources

| Resource | Capacity | Semantics |
|---|---|---|
| `windows` | 1 | The Windows game slot. Granted only when no runq or legacy job holds it and no game of an earlier run is still alive. A functional run may share Windows with the user's own game; a `perf` + `windows` run may not (5.3) |
| `perf` | 1 | Exclusive timing. While a perf job runs, nothing else that loads the machine runs: no players, no `cpu` jobs. Jobs of kind `light` (git, docs, small scripts) still run |
| `quiet` | flag on `perf` | `perf` plus a machine that is quiet outside runq: external CPU under 2 cores (sampled for 5 s by the daemon at grant) and, for Windows perf runs, the user not in ONI. Bounded wait, then the run starts flagged `noisy` (5.3) |
| `lane` | lanes 1-19, minus `reserved` and busy ports or displays | `--needs lane=2` grants two lane numbers, passed as `RUNQ_LANES=3,4` |
| `mem` | 40 GB, at most 4 players | GB counted for the job's players; `--needs mem=15`. Same budget and defaults as e2e.py's today |
| `cpu` | 14 cores (16 less 2 for the desktop and interactive shells) | Cores a job is counted for. A Linux player counts 3, a build 6, check.sh 8 |

**Kinds** bundle these so agents rarely spell them out:

| Kind | Resources | Timeout |
|---|---|---|
| `light` | none | 30 min |
| `build` | cpu=6, mem=6 | 30 min |
| `check` | cpu=8, mem=8, excludes `perf` | 45 min |
| `linux-player` | lane=1, mem=6, cpu=3 | 60 min |
| `e2e` | lane=N, mem and cpu from the spec list (e2e.py computes them, 9.2) | from the list |
| `windows` | windows | 60 min |
| `win-perf` | windows, perf, quiet, and High priority set on the game (5.3) | 45 min |
| `linux-perf` | perf, quiet, lane=1, mem=15, cpu=4 | 60 min |
| `qa` | phases (4.4) | 3 h |

`check` "excludes perf" means a check job and a perf job never run at the same time, in either
direction, which fixes the flaky CPU-sensitive tests (section 9.1).

### 4.4 Phases

Some runs need different resources in different stages: qa.sh needs a player for dogfood and
the whole machine for its perf stage; alloc-compare.py needs the perf queue only for its launch.
A job may call `runq phase NEEDS` from inside. The daemon releases everything the job holds,
then queues the new request with the job's priority and its original submit time, so it keeps
its place in line. The job's process waits holding nothing. This is the only way to change
resources mid-run, so "hold one, wait for the other" cannot happen.

## 5. Isolation applied to every job

### 5.1 Folders

- `~/oni-runs/<id>/`: `job.json` (the request), `cmd` (the script copy, so a wiped scratchpad
  loses nothing), `out.log` (stdout and stderr, with time stamps), `result.json`, `out/`.
- Windows jobs: `%TEMP%\oni-runq\<id>\stage\` (the host copied from `out/win`) and
  `%TEMP%\oni-runq\<id>\home\` (the run home). The run home's path contains the job id, so
  `win-run.ps1 -Match '\oni-runq\<id>\'` matches exactly one run's game.
- Retention: job folders 14 days or 50 GB in all, oldest first; failed jobs kept 30 days;
  `runq keep ID` pins one. Windows folders are removed by `runq-cleanup` when the job ends,
  unless `--keep-home`.

### 5.2 Environment the daemon sets

| Variable | Value | For |
|---|---|---|
| `RUNQ_JOB`, `RUNQ_OUT`, `RUNQ_OWNER`, `RUNQ_PRIORITY` | the job's | scripts and nested calls; `RUNQ_JOB` set means "already inside runq" |
| `RUNQ_LANES` | granted lane numbers | e2e.py's `--first-lane` and `--last-lane` |
| `E2E_MEMORY_GB`, `E2E_MAX_PLAYERS` | the job's grant | e2e.py inside a job stays within it |
| `ONI_RUN_HOME` | `%TEMP%\oni-runq\<id>\home` (Windows path) | run-windows.sh, original-windows.sh |
| `ONI_WIN_STAGE` | `%TEMP%\oni-runq\<id>\stage` | run-windows.sh (a new override; default unchanged) |
| `ONI_HOST_NO_DIALOG` | `1` | no MessageBox on the user's desktop |
| `WSLENV` | the caller's plus `ONI_HOST_NO_DIALOG:ONI_RUN_HOME/p` and the like | without it the variables never reach a Windows executable |
| `ONI_WIN_MONITOR` | `2` | monitor 2 only |
| `ONI_CONTAINED` | `1` | contain.sh inside the job execs or makes a player scope |
| `PULSE_SERVER`, `SDL_AUDIODRIVER` | a missing server, `dummy` | no sound on the user's speakers (contain.sh's values) |
| `DISPLAY` | unset; e2e.py sets each lane's Xvfb | Linux players never on WSLg's desktop |

### 5.3 Windows rules the daemon enforces

- **Never by name.** Every action on a Windows process goes through `win-run.ps1 -Match <job's
  run home>`. The user's own game and any process runq did not start are never killed, boosted
  or focused. Detecting that the user plays ONI is read-only: Steam's
  `HKCU\Software\Valve\Steam\RunningAppID` equal to 457140.
- **User in ONI.** Only `win-perf` waits while the user plays ONI. Functional `windows` jobs
  run anyway, unfocused on monitor 2. A `win-perf` job that waits for the user longer than its
  bound (6.4) is not started noisy; it stays queued and status says why, since a perf number
  beside the user's game is worthless. Other jobs keep running.
- **After start.** A watcher (the daemon's worker thread, every 30 s for running Windows jobs)
  reads the game's command line through `win-run.ps1 -Action pid` and a command-line read. It
  records a violation in status when the game lacks `--no-focus` (ours) or `-monitor 2`, or has
  `--background`. For `win-perf` it runs `win-boost.ps1` with the run home (High priority,
  power throttling off).
- **No pointer moves, no focus taking.** runq cannot see a pointer move, so this stays a rule in
  agent text (11); `runq` never calls `-Action focus`.
- **After end.** `runq-cleanup` kills the job's game by run home, then removes its folder.

## 6. Scheduling

### 6.1 Order

The scheduler is a pure function, `plan(state, now) -> [grant]`, run on every event (submit,
job end, cancel, phase request, an external lock change, a timer). The daemon applies its
grants. Each pass:

1. **Due jobs first.** A queued job is *due* when it has waited longer than its level's bound
   (6.4). Due jobs are ordered by submit time and take precedence over every non-due job.
2. **Then by level:** issue, release, user, study, burndown.
3. **Within a level, by owner, round-robin:** the owner with the least service in this level over
   the last 24 h (machine-hours of the resources its jobs held) goes first; within an owner, by
   submit time. An owner that just finished a run therefore does not win the next one against
   an owner that has been waiting: back-to-back chains interleave with others.
4. **All or nothing.** A job starts only when every resource it needs is free. It never holds
   part of its set.
5. **Reservation and backfill.** When the first job in order cannot start, the scheduler
   computes the earliest time its resources free up, from the running jobs' timeouts (not their
   estimates, so the bound holds), and reserves them from then. A later job may still start if
   it does not need the reserved resources, or if its own timeout ends before the reservation.
   This is how quiet windows work: a perf job at the head of the line reserves the whole
   machine, new players stop starting, running ones end within their timeouts, and the perf job
   starts. Nothing polls.

### 6.2 Caps

| Cap | Default | Why |
|---|---|---|
| Running jobs per owner | 2 (burndown: 1) | One agent cannot fill the lanes |
| Queued jobs per owner | 20 | |
| Queued machine-hours per owner of `windows` or `perf` | 4 h | The 43-run plan would be refused at submit, with its forecast: "43 runs, about 7.2 h of the Windows slot; cap 4 h. Ask the user, then pass --budget-ok 'REASON'" |
| `release` level | only while a release freeze is open (12), or with `--release-ok` | Keeps the level honest |

Caps are in a config file (`~/.config/oni-runq/config.toml`, parsed with `tomllib`) so the user
can change them without a code change. `runq submit --dry-run` prints the forecast (start and end
time of each job) without queueing.

### 6.3 Leases, heartbeats and idle holders

| Situation | Detected by | Action |
|---|---|---|
| Job ran past `--timeout` | the daemon's deadline | stop the unit; exit 124 |
| Job's processes died | unit inactive; `result.json` | release at once |
| Job hung (opted in) | no `runq beat` for `--heartbeat` | stop; exit 137, reason `heartbeat` |
| Job holds `windows` or `perf` but does nothing | no game for its run home and cgroup CPU under 2% for 10 min | flag in status at 10 min; stop at 20 min, reason `idle-held` |
| Shim killed outright | systemd stops the unit; `ExecStopPost` still runs | cleanup, release |
| Legacy holder is dead | its pid gone (`/proc/locks` entry vanishes) | resource free |

The idle rule fixes the hour-long sleep inside a held perf claim: sleeping now costs the job its
resources. A job that needs a pause between launches uses `runq phase` (releasing everything) or
splits into chained jobs.

### 6.4 Bounded wait

Levels' due bounds: issue 0, release 0, user 30 min, study 3 h, burndown 8 h. Once due, a job
waits at most for the jobs due before it plus the longest running timeout among the resources it
needs. Timeouts are capped (6 h), and due jobs are finite, so every job starts within a bound
the status shows ("starts by 14:20 at the latest"). The two pair studies of 8 hours would have
become due after 3 h and then started within the running jobs' timeouts.

`quiet`'s external-load wait is bounded too: 15 min, then the job starts with `noisy: true` in
its result (except `win-perf` beside the user's ONI game, 5.3). Results are labelled so a study
can discard noisy runs instead of waiting forever.

## 7. Worktree registry (phase 3)

```
runq wt add REPO PATH [-b BRANCH | --detach SHA] --owner NAME --purpose TEXT
runq wt release PATH
runq wt list [--owner X] [--state S]
runq wt gc [--dry-run]
runq wt import                       record existing worktrees as state legacy
```

- `add` runs `git worktree add` and records owner, purpose, branch and the creating session.
- States: `active`, `released`, `legacy` (found by `import`, owner unknown), `missing` (the
  folder is gone; `git worktree prune` candidate).
- A worktree is **pinned** while any queued or running job has its cwd inside it.
- `gc` removes a worktree only when all hold: state `released`; not pinned; `git status
  --porcelain` empty (ignored `out/` content aside); HEAD reachable from a ref that outlives it
  (a remote-tracking branch, or a local branch that `git worktree remove` keeps). It never
  touches `legacy` worktrees, the main checkouts (`oni-brutal`, `oni-brutal-u60`,
  `oni-brutal-qa`), or `.claude/worktrees/` (the Agent harness manages those). It lists what it
  skipped and why.
- Both repositories: `oni-brutal-u60` and `oni-brutal-qa` are worktrees of the same repository
  as `oni-brutal`, so one `git worktree list` covers them. The registry keys worktrees by
  absolute path.

## 8. Observability

### 8.1 `runq status`

```
runq 1.0, up 3 d. Machine: windows busy, perf free, lanes 5/19, mem 21/40 GB, cpu 9/14
RUNNING
  j0001240 issue    i82        win-perf ours #82 repro    windows,perf   12 min of 45   ~8 min left
  j0001238 user     alloc-b6   e2e release.txt            lane 3,4 mem 12  31 min of 90
QUEUED (in start order)
  j0001241 release  hfqa       e2e release.txt u60        lane=2 mem=12  starts ~13:05 (after j0001238)
  j0001236 study    gc79       win-perf orig pair 3/8     windows,perf   due in 1 h 10 min; by 15:40 at the latest
EXTERNAL
  perf.lock shared by pid 41233 (scripts/e2e.py, not via runq) in out/u762230
WARNINGS
  j0001240: game has no --no-focus in its command line
OWNERS (24 h)  gc79 5.1 h windows | i82 0.4 h | alloc-b6 2.0 h lanes
```

`--json` gives the same for scripts. Each queued job says what it waits for, in words.

### 8.2 Status page

The daemon serves one read-only HTML page and `/status.json` on `127.0.0.1:8777` (outside the
lane ports 8810-8991 and the `reserved` file's). WSL's localhost forwarding lets the user open
it in a Windows browser. Plain HTML, refreshed every 10 s; no dependencies.

### 8.3 History and stats

Every job's record stays in SQLite: owner, label, level, resources, submit, start and end times,
exit code, reason, cgroup CPU seconds and peak memory, `noisy`. `runq stats` shows per owner and
label: count, machine-hours per resource, wait p50/p95, failure rate. Duration estimates for
`--expect` come from the label's median.

## 9. check.sh and the other runners as jobs

### 9.1 check.sh

`check.sh` gets a shim at its top, next to its contain.sh line: when `RUNQ_JOB` is unset and the
daemon answers, it re-execs itself as `runq run --kind check -- scripts/check.sh "$@"`. Inside a
job it runs as today. `RUNQ=off` skips the shim. The `check` kind never overlaps a perf job, and
its 8 cores are counted, so its CPU-sensitive tests run on a machine that is not saturated.

### 9.2 The other runners

Each gets the same self-submit shim, with resources it computes:

| Runner | Kind and resources | Change inside |
|---|---|---|
| `scripts/e2e.py` | `e2e`: lane=`--lanes N`, mem from the specs' `memory_gb` or fixture (largest N), cpu 3 per lane; priority from `RUNQ_PRIORITY` | With `RUNQ_JOB` set: lanes from `RUNQ_LANES`, no `machine_lock` wait, no `Budget` wait (runq's grant and its bridge entry stand in), no `PERF_JOBS` greps |
| `scripts/qa.sh` | `qa`, phases: unit as `check`, dogfood and verify as players, perf as `linux-perf` | `quiet_begin` and `quiet_end` become `runq phase linux-perf` and `runq phase NEEDS`; the `tasklist` and `cores_busy` loop go |
| `scripts/alloc-compare.py run` | `linux-perf` per launch | `PerfQueue.claim` becomes `runq phase linux-perf` inside the job, or the run submits itself |
| `scripts/run-windows.sh`, `scripts/original-windows.sh` | `windows` (`win-perf` with `RUNQ_KIND=win-perf`) | `ONI_WIN_STAGE` replaces the fixed stage folder; `ONI_RUN_HOME` defaults to the job's |
| `out/local-tools/win-*.sh` and the queue scripts | wrapped by the agents that own them: `runq run --kind win-perf -- out/local-tools/win-perf.sh ...` | their own `rm -rf "$home"` stays inside the job's folders |
| `scripts/run.sh` | `linux-player` | |

### 9.3 Compatibility during the transition

- Old scripts keep working: the bridge (3.5) makes runq jobs visible to their flocks and ledger,
  and their flocks visible to runq.
- Each shim falls back to today's behaviour when the daemon does not answer, with one line on
  stderr, until the last step of phase 1c.
- The last step of phase 1c removes direct claims: `machine_lock`, `Budget`'s waits and
  `PerfQueue` become errors outside runq ("run through runq"), and `PERF_JOBS` goes.

## 10. Failure modes

| Failure | What happens | Recovery |
|---|---|---|
| Daemon crash | Jobs keep running (own units); clients get a broken socket and retry for 60 s | systemd restarts it; it reads SQLite, then reconciles: each `running` job's unit state, `result.json` for ones that ended meanwhile; clients reattach by id |
| Job crash | Unit goes inactive, `ExecStopPost` cleans up | Daemon records the exit code; resources free at once |
| Job killed with SIGKILL from outside | Same as a crash: the shim's `result.json` is missing, systemd's exit status stands in | |
| WSL restart (`wsl --shutdown`, Windows update) | Every Linux process and unit is gone; Windows games started by jobs may still run | On start the daemon marks `running` jobs `lost` (exit 76), kills Windows games whose command line matches `\oni-runq\j` for jobs not running, runs each job's cleanup, requeues `--retry-on-lost` jobs with their original submit time |
| A Windows game left running after its job | Found by the 30 s watcher or the start-up reconcile | Killed by its run home; the `windows` slot frees only once it is gone |
| A Windows game of a legacy run (`%TEMP%\oni-brutal\...` run home) | Counted as the slot taken, shown under EXTERNAL | Never killed by runq: it did not start it |
| User's own game | Read from Steam's RunningAppID | Never touched; only `win-perf` waits |
| SQLite corruption | WAL, `synchronous=NORMAL`, one writer | `runq doctor` checks integrity; the daemon rebuilds the running set from units and Windows processes |
| Clock jump (WSL's clock drifts after the host sleeps, then steps) | The daemon compares `time.monotonic()` with wall time each tick | Deadlines are kept in monotonic time while the daemon runs; a step of the wall clock never fires timeouts. After a restart, deadlines are rebuilt from the wall clock, and a job whose deadline passed during a detected step gets the step added back |
| Host sleep | Monotonic time stops in the VM | Timeouts measure time the machine ran, which is what they bound |
| Disk full | Job log writes fail | The daemon refuses new jobs below 5 GB free and says so in status |
| Two daemons | Second one fails to take the socket's flock | Exits with the first one's pid |

## 11. Text for agent prompts

To paste into briefs that run anything on the machine:

> Run every player, build, check.sh, e2e, perf or Windows run through runq. Never take
> perf.lock, perf.wanted or windows-game.lock yourself, and never start players outside it.
> - `scripts/runq run --owner NAME --priority LEVEL --kind KIND --timeout DUR -- CMD` runs CMD
>   when the machine has room and returns its exit code. Its output folder is printed first
>   (`~/oni-runs/ID/out`, also `$RUNQ_OUT`). Put scripts in the worktree, not /tmp, or pass
>   `--script FILE` so runq keeps a copy.
> - Long runs: start `runq run ...` with the Bash tool's run_in_background, and do other work
>   or end your turn. You are woken when it exits. Do not poll `runq status` in a loop, and do
>   not hand back while a run of yours is queued or running.
> - LEVEL: `issue` for GitHub issue work, `release` for release QA, `user` for the user's
>   requests, `study` for A/B or measurement series, `burndown` for burndowns.
> - KIND: `check` (scripts/check.sh), `e2e`, `linux-player`, `windows`, `win-perf`,
>   `linux-perf`, `build`, `light`. Check `runq submit --dry-run` before queueing a series;
>   more than 4 h of the Windows or perf slot needs the user's OK.
> - Stop a run with `runq cancel ID`. Never `pkill -f`, `killall` or kill by name; never touch a
>   Windows process except through runq.
> - Windows runs: monitor 2, `--no-focus`, never `--background`, never move the pointer or take
>   the focus. runq sets ONI_HOST_NO_DIALOG, WSLENV and the run home.
> - Worktrees: `runq wt add` to make one, `runq wt release` when done. Never remove a worktree
>   you did not make.

## 12. Release freeze and merge queue (phase 4)

**Freeze.** The release agent opens it when its QA starts:

```
runq freeze open master --sha 2ade4d48 --release v2026.11.12.03
runq freeze open u60 --sha e81ddb58 --release v2026.11.12.03-u60
```

- While a branch is frozen, the local pre-push hook (already present, shared by all worktrees of
  the repository) refuses pushes to it and names `runq push`. The hook asks the daemon; with no
  daemon it allows the push, as today.
- `runq push BRANCH` from a worktree queues a push intent: owner, local SHA, whether it is
  docs-only. `runq push --wait` blocks until it lands, so agents are woken, not polling.
- The freeze closes on `runq freeze close`, or by itself when the release tag exists on origin
  at the frozen SHA (`git ls-remote --tags`, every 5 min while frozen). If the tag points
  elsewhere, the daemon does not close it and status says so.
- `--no-verify` still bypasses a local hook. A GitHub ruleset could enforce the freeze on the
  server; that changes repository settings, so it is the user's call and not part of this plan.

**Merge queue.** After the freeze closes, intents land one at a time in submit order:

1. If the intent's SHA fast-forwards the remote branch, the daemon pushes it
   (`git push origin SHA:refs/heads/BRANCH`, master and u60 only).
2. Otherwise the owner is woken with "rebase onto origin/BRANCH, then `runq push` again"; the
   daemon does not rebase anyone's work.
3. Code intents (anything outside `docs/` and `*.md`) need a passing `check` job on the exact
   SHA first; docs-only intents do not, as today.

The queue only orders pushes; it does not batch or squash. Pushes outside a freeze go directly,
as now.

## 13. Phases

| Phase | Contents | Effort (agent-days) |
|---|---|---|
| 0 | The user's review of this plan and the open questions (14) | - |
| 1a | Daemon, SQLite schema, socket protocol, CLI `run/submit/wait/cancel/show/log/status`, the scheduler (levels, due bounds, round-robin, caps, all-or-nothing, reservation and backfill), shim and cleanup units, timeouts, idle detection, crash and restart reconcile; unit and integration tests | 3 |
| 1b | Windows: per-job stage and run home, `ONI_WIN_STAGE` in run-windows.sh, environment and WSLENV, win-run.ps1 kills, the 30 s watcher and violations, win-boost for `win-perf`, user-in-ONI check; the legacy bridge (flocks, `players.json`, `/proc/locks`) | 1.5 |
| 1c | Self-submit shims in check.sh, e2e.py, qa.sh (phases), alloc-compare.py, run-windows.sh, original-windows.sh, run.sh; DEVELOPMENT.md and tests/e2e/README.md sections; the prompt text; then, after a week of use, removing direct claims and `PERF_JOBS` | 1.5 |
| 2 | Status page, `history`, `stats`, `--dry-run` forecasts and duration estimates | 1 |
| 3 | Worktree registry: `wt add/release/list/gc/import`, pinning by running jobs | 1.5 |
| 4 | Release freeze, the pre-push hook check, `runq push`, the merge queue, closing on the tag | 1.5 |

Total about 10 agent-days. Phase 1 alone fixes every failure in the table of section 1 except
the worktree removal (phase 3). Phases 3 and 4 do not depend on each other.

### 13.1 Tests

In `tests/scripts/`, so `check.sh` runs them (`python3 -m unittest discover`):

- **Scheduler units** (`test_runq_scheduler.py`): `plan()` on hand-built states with a fake
  clock. Cases: levels order; round-robin between two owners; an owner's back-to-back jobs
  interleave; caps refuse at submit; all-or-nothing never grants a partial set; a perf job at the
  head drains players and starts when they end; backfill starts a short job that ends before the
  reservation and refuses one that would not; due jobs overtake higher levels; `check` and
  `perf` never overlap; `phase` keeps the original submit time.
- **Scheduler properties**: seeded random job streams (stdlib `random`), 10,000 steps each,
  asserting after every step: no capacity exceeded, no job holds a partial set, every job starts
  within its computed bound, no lower-level job starts ahead of a waiting higher one except
  through backfill that does not move the reservation.
- **Daemon integration** (`test_runq_daemon.py`): a daemon on a temporary state folder, units
  under a test prefix in a test slice, jobs that are `true`, `false`, `sleep`, a job that
  ignores SIGTERM. Cases: run returns the exit code; cancel kills a whole process tree; timeout;
  idle-held; kill -9 the daemon mid-job and restart it, the job's result arrives; kill -9 the
  shim; a simulated WSL restart (stop every test unit with the daemon down); a legacy flock
  holder blocks a grant and its exit frees it.
- **Windows backend**: an interface with a fake implementation that records calls, so tests
  never touch Windows. One opt-in test (`RUNQ_TEST_WINDOWS=1`) starts a real Windows run and
  checks it is killed by run home.
- **Rules as tests**: the runq sources contain no `pkill`, `killall`, `taskkill /IM`,
  `Get-Process -Name`, `SetCursorPos` or `--background`.

## 14. Open questions for the user

1. **Who sets the level?** The design trusts `--priority` from the agent, with `release` gated
   by a freeze. Alternatively the orchestrator registers each owner with its level once
   (`runq owner add i82 --level issue`), and agents cannot choose.
2. **Daemon-run commands.** Jobs run as systemd units started by the daemon, not in the agent's
   shell. The agent's tool call still shows the command. Is that acceptable for the permission
   setup?
3. **Caps:** 2 running per owner, 4 h of Windows or perf queued per owner, 14 cores and 40 GB.
4. **Studies' due bound** of 3 h and burndowns' of 8 h.
5. **check.sh self-submitting by default**, or only when asked (`RUNQ=on`).
6. **Status page port** 8777 on localhost.
7. **A GitHub ruleset** for the freeze, or the local hook only.
8. **Linger:** whether keeping the user manager (and so possibly the WSL VM) alive between
   sessions is wanted.

## 15. Phase 1 as built

The user's answers to section 14, each a value in `~/.config/oni-runq/config.toml` (defaults in
`scripts/runqlib/config.py`), and where phase 1 differs from the text above:

- **Levels:** the agent passes its own, `--level` (`--priority` is the same option), default
  `study`; `issue` and `release` are allowed and logged to `levels.log`. No owner registry yet;
  the owner defaults to the worktree's folder name instead of being refused.
- **Commands run as transient systemd units** in `oni-tests.slice`, as in 3.4. `TimeoutStopSec`
  is 30 s (`stop_grace_seconds`), since e2e.py's own teardown takes up to 10 s per player.
- **Caps and bounds** as proposed: 2 running per owner (burndown 1), 4 h of the Windows or perf
  slot per owner, user 30 min, study 3 h, burndown 8 h.
- **check.sh** goes through runq by default as the `check` kind. `RUNQ_JOB` set (inside any job,
  the daemon's own runs included) runs it directly, so it never recurses.
- **No linger:** the CLI installs and starts `oni-runqd.service` when the daemon does not answer
  (3.3 had it exit 69 instead). Inside a job, or with a test daemon (`RUNQ_STATE_DIR`), it starts
  none.
- **Status page** (phase 2): `status_port = 8777`, localhost only, not served yet.
- **Legacy bridge:** a lock or ledger entry counts as runq's only when the daemon or a job's shim
  holds it; an old script run inside a job still counts as an occupant. A job whose own process
  blocks on a legacy lock its shim holds is stopped at once (`self-deadlock`).
- **out.log** has the command's output as it wrote it, without time stamps, so `runq run` shows
  what the script prints.
- Not built in phase 1: `runq stats`, `--dry-run` forecasts, the status page (phase 2), the
  worktree registry (phase 3), the freeze and merge queue (phase 4), and removing the direct
  claims from the old scripts (the end of 1c, after a week of use).
