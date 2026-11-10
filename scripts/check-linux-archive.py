#!/usr/bin/env python3
"""Checks the Linux release archive (scripts/release.sh) from the tar.gz alone: the files a player
starts are there and executable (setup.sh, the launcher oni-moono.desktop, player/OniMoono and
player/Restarter), the menu entry's icon player/Moo.png is there, and the launcher is a desktop
entry that runs player/OniMoono beside itself (its Exec read as the Desktop Entry Specification
says, with %k its own path) without a terminal (docs/INSTALL-MODES.md, Linux launcher).

    scripts/check-linux-archive.py out/release/oni-moono-VERSION-linux-x64.tar.gz

Exits 1 on a failed check.
"""

import sys
import tarfile

TOP = 'oni-moono'
EXECUTABLE = ('setup.sh', 'oni-moono.desktop', 'player/OniMoono', 'player/Restarter')
PRESENT = ('player/Moo.png', 'README.md', 'NOTICE.txt')


def desktop_entry(text):
    """The [Desktop Entry] group's keys, their string escapes (\\s \\n \\t \\r \\\\) undone."""
    escapes = {'s': ' ', 'n': '\n', 't': '\t', 'r': '\r', '\\': '\\'}
    group, keys = None, {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('['):
            group = line[1:-1]
            continue
        if group != 'Desktop Entry' or '=' not in line:
            continue
        key, value = (part.strip() for part in line.split('=', 1))
        out, i = '', 0
        while i < len(value):
            if value[i] == '\\' and i + 1 < len(value):
                if value[i + 1] not in escapes:
                    raise ValueError(f'{key}: invalid escape \\{value[i + 1]}')
                out += escapes[value[i + 1]]
                i += 2
            else:
                out += value[i]
                i += 1
        keys[key] = out
    return keys


def exec_argv(exec_value, location):
    """Exec's arguments as a launcher runs them: split at spaces, double-quoted arguments with
    \\" \\` \\$ \\\\ escaped, the field codes expanded (%k: `location`, %% a %, the others
    dropped as with no files or URLs)."""
    args, arg, quoted, started, i = [], '', False, False, 0
    while i < len(exec_value):
        c = exec_value[i]
        if quoted:
            if c == '\\' and i + 1 < len(exec_value) and exec_value[i + 1] in '"`$\\':
                arg += exec_value[i + 1]
                i += 2
                continue
            if c == '"':
                quoted = False
            else:
                arg += c
        elif c == '"':
            quoted = started = True
        elif c == ' ':
            if started:
                args.append(arg)
            arg, started = '', False
        elif c == '%' and i + 1 < len(exec_value):
            code = exec_value[i + 1]
            if code in '%k':
                arg += '%' if code == '%' else location
                started = True
            elif code not in 'fFuUic':
                raise ValueError(f'unknown field code %{code}')
            i += 2
            continue
        else:
            arg += c
            started = True
        i += 1
    if quoted:
        raise ValueError('Exec: an unterminated quote')
    if started:
        args.append(arg)
    return args


def launcher_problems(text):
    """What is wrong with the launcher's desktop entry (an empty list: nothing)."""
    try:
        keys = desktop_entry(text)
    except ValueError as e:
        return [str(e)]
    problems = []
    for key, want in (('Type', 'Application'), ('Terminal', 'false')):
        if keys.get(key) != want:
            problems.append(f'{key} is {keys.get(key)!r}, not {want}')
    for key in ('Name', 'Exec'):
        if not keys.get(key):
            problems.append(f'no {key}')
    if '%k' not in keys.get('Exec', ''):
        problems.append("Exec doesn't use %k (its own path): a relative Exec doesn't work")
    else:
        try:
            exec_argv(keys['Exec'], '/x/oni-moono/oni-moono.desktop')
        except ValueError as e:
            problems.append(str(e))
    return problems


def check(path):
    problems = []
    with tarfile.open(path) as archive:
        members = {m.name.removeprefix('./'): m for m in archive.getmembers()}
        for name in EXECUTABLE + PRESENT:
            member = members.get(f'{TOP}/{name}')
            if member is None or not member.isfile():
                problems.append(f'{TOP}/{name} missing')
            elif name in EXECUTABLE and member.mode & 0o111 != 0o111:
                problems.append(f'{TOP}/{name} is not executable (mode {member.mode:o})')
        launcher = members.get(f'{TOP}/oni-moono.desktop')
        if launcher is not None and launcher.isfile():
            problems += [f'{TOP}/oni-moono.desktop: {p}' for p in launcher_problems(archive.extractfile(launcher).read().decode())]
    return problems


def main(argv):
    if len(argv) != 2:
        print('usage: scripts/check-linux-archive.py ARCHIVE.tar.gz', file=sys.stderr)
        return 2
    problems = check(argv[1])
    for p in problems:
        print(f'check-linux-archive: {p}', file=sys.stderr)
    if not problems:
        print(f'check-linux-archive: {argv[1]}: the launcher, setup.sh and the player are there and executable')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
