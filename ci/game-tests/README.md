# CI game tests

The game job of `.github/workflows/build.yml` (every push) and `.github/workflows/game-nightly.yml`
run `scripts/ci-game-tests.sh` on a stripped copy of the game. docs/DEVELOPMENT.md, "Game tests in
CI", describes them in full.

- `push.txt` and `nightly.txt` are the e2e lists, in `scripts/e2e.py --list` form.
- `budget/BUILD.txt` is each game build's log-signature budget, one `qa-tools.py signatures` line
  each. A run of ours fails on a signature it lacks. Add a line only for an error the game logs
  itself (the full install logs it too) and say why in a `#` comment.
- `../game-content.json` names the stripped build and the saves by hash in the private refs
  repository.

oni-moono is public; the stripped game and the saves are not. The job:

- reads them only with the read-only deploy key `GAME_REFS_DEPLOY_KEY`, and skips itself without it,
  as in a fork's run;
- caches none of them, and fetches them fresh each run;
- uploads no game files, saves, screenshots, UI dumps or logs as artifacts, and prints verdicts and
  short excerpts only.

Keep it that way. OniMcp is built fresh for each run with `scripts/onimcp.py`, and never cached or
uploaded.
