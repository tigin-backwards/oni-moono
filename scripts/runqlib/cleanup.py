"""runq-cleanup: run by systemd after every job (ExecStopPost), however it ended (§3.1).

- perf.wanted is removed when it names the job's shim, and the shim's players.json entries go
  (the shim does both itself when it ends normally; this covers a shim killed outright);
- players the job started in systemd scopes of their own (scripts/contain.sh with ONI_SCOPE, as
  e2e.py and qa.sh do) are stopped: the scopes whose processes carry RUNQ_JOB=<id>;
- a job that held the Windows slot: its game is killed by its run home (never by name), and so are
  the games the daemon saw it start under other run homes (by pid and start time); then its
  Windows folder (%TEMP%\\oni-runq\\<id>) is removed, unless --keep-home;
- cleanup.json records systemd's view of the end ($SERVICE_RESULT, $EXIT_CODE, $EXIT_STATUS),
  and the daemon frees the job's resources once it is there."""

import os, re, subprocess, time

from . import legacy, shim, winenv


def job_scopes(job_id, procs=None):
    """The systemd scope units whose processes carry RUNQ_JOB=JOB_ID."""
    scopes = set()
    for pid, _ in legacy.processes() if procs is None else procs:
        if legacy.job_env_of(pid) != job_id:
            continue
        try:
            with open(f'/proc/{pid}/cgroup') as f:
                text = f.read()
        except OSError:
            continue
        match = re.search(r'/([^/\s]+\.scope)\s*$', text, re.M)
        if match:
            scopes.add(match.group(1))
    return sorted(scopes)


def stop_scopes(scopes, log):
    for scope in scopes:
        r = subprocess.run(['systemctl', '--user', 'stop', scope], capture_output=True, text=True, timeout=60)
        log(f'stopped {scope}' + (f' ({r.stderr.strip()})' if r.returncode else ''))


def windows_cleanup(job, windows, log):
    """Kills the job's game by its run home(s), then removes its Windows folder."""
    if windows is None:
        log('no Windows backend: nothing to kill or remove')
        return
    env = shim.read_json(os.path.join(job['dir'], 'env.json'), {}) or {}
    for match in winenv.run_home_matches(job['id'], env):
        try:
            out = windows.kill(match)
            if out.strip():
                log(out.strip())
        except Exception as e:
            log(f'kill by {match}: {e}')
    # The games it started under a run home of their own (the daemon saw them while the job held
    # the Windows slot): by pid and start time.
    for g in shim.read_json(os.path.join(job['dir'], 'games.json'), []) or []:
        try:
            out = windows.kill_pid(g['pid'], g['created'])
            if out.strip():
                log(out.strip())
        except Exception as e:
            log(f'kill {g.get("pid")}: {e}')
    if job.get('keep_home'):
        log('Windows folder kept (--keep-home)')
        return
    win_temp = job.get('win_temp')
    if win_temp:
        root, _, _ = winenv.job_paths(win_temp, job['id'])
        try:
            windows.remove(root)
            log(f'removed {root}')
        except Exception as e:
            log(f'remove {root}: {e}')


def main(job_dir, windows=None, environ=None):
    environ = os.environ if environ is None else environ
    job = shim.read_json(os.path.join(job_dir, 'job.json'), {})
    grant = shim.read_json(os.path.join(job_dir, 'grant.json'), {}) or {}
    lines = []
    log = lambda s: lines.append(f'{time.strftime("%H:%M:%S")} {s}')
    lock_dir = grant.get('lock_dir')
    info = shim.read_json(os.path.join(job_dir, 'shim.json'), {}) or {}
    pid = info.get('pid')
    if lock_dir and pid:
        legacy.clear_wanted(lock_dir, pid)
        try:
            legacy.remove_players(lock_dir, pid)
        except OSError as e:
            log(f'players.json: {e}')
    try:
        stop_scopes(job_scopes(job.get('id')), log)
    except Exception as e:
        log(f'scopes: {e}')
    if job.get('windows_used'):
        if windows is None:
            windows = winenv.backend()
        windows_cleanup(job, windows, log)
    result = {
        'done': True,
        'time': time.time(),
        'service_result': environ.get('SERVICE_RESULT'),
        'exit_code': environ.get('EXIT_CODE'),
        'exit_status': environ.get('EXIT_STATUS'),
        'log': lines,
    }
    shim.write_json(os.path.join(job_dir, 'cleanup.json'), result)
    return 0
