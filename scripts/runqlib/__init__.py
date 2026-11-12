"""runq: the run broker of the shared test machine (docs/research/runq-design.md).

Modules: config (defaults and config.toml), sched (the pure scheduler), legacy (the old locks:
perf.lock, perf.wanted, windows-game.lock, lane locks, players.json), winenv (Windows run homes,
stage folders, environment and the Windows backend), store (SQLite), shim (a job's first
process), cleanup (ExecStopPost), daemon (runqd), client (the CLI's socket side)."""
