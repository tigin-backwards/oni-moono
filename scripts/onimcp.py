#!/usr/bin/env python3
"""OniMcp, the in-game MCP server e2e.py and mod-sweep.py play through, built at a pinned commit.
  scripts/onimcp.py [DEST]     fetch the commit below, build it against the game (ONI_GAME_DIR,
                               as scripts/env.sh), and copy the mod into DEST (default
                               out/local-tools/onimcp, where qa.env's E2E_ONIMCP points)
It is fetched from its author's repository (github.com/LIghtJUNction/OniMods) and built here
rather than kept in this one: its licence (AUL-1.0-draft.1) puts duties on copies kept and
changed. The specs depend on its tools' arguments and protocol, which change between commits (its
v0.2.5 release takes coordinates only in coordinate_control; from 2026-10-23 it takes only MCP
2025-11-25), so a new commit is taken by changing COMMIT after the e2e specs pass with it.

It loads only beside Jlib (github.com/LIghtJUNction/Jlib, MIT), its author's PLib fork, which
since 2026-10-26 is a mod of its own (staticID LIghtJUNction.Jlib) rather than merged into
OniMcp: the commit's mods/Jlib submodule is fetched and built with it, and goes into DEST/Jlib,
which e2e.py installs as a mod beside OniMcp. Both are built from the author's source unchanged
(this commit reads U60's report notes itself), so the built mod carries the author's licence
files and no list of changes (AUL-1.0-draft.1, section 3, applies to changed copies)."""

import os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = 'https://github.com/LIghtJUNction/OniMods.git'
COMMIT = 'f9a911e5f6f5804529b4eea88b2b27d9a728b87e'
SOURCE = os.path.join(ROOT, 'out', 'local-tools', 'onimcp-src')
JLIB = os.path.join('mods', 'Jlib')


def run(*args, cwd=None):
    env = dict(os.environ)
    # The SDK where scripts/env.sh finds it.
    home_dotnet = os.path.expanduser('~/.dotnet')
    if os.access(os.path.join(home_dotnet, 'dotnet'), os.X_OK):
        env['PATH'] = home_dotnet + os.pathsep + env.get('PATH', '')
        env['DOTNET_ROOT'] = home_dotnet
    subprocess.run(args, cwd=cwd, env=env, check=True)


def main():
    dest = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'out', 'local-tools', 'onimcp')
    game = os.environ.get('ONI_GAME_DIR') or os.path.expanduser('~/projects/oni-linux-764961')
    if not os.path.isdir(os.path.join(game, 'OxygenNotIncluded_Data', 'Managed')):
        sys.exit(f'onimcp: {game} is not a game install; set ONI_GAME_DIR')
    if not os.path.isdir(os.path.join(SOURCE, '.git')):
        os.makedirs(SOURCE, exist_ok=True)
        run('git', 'init', '-q', cwd=SOURCE)
        run('git', 'remote', 'add', 'origin', REPO, cwd=SOURCE)
    run('git', 'fetch', '-q', '--depth', '1', 'origin', COMMIT, cwd=SOURCE)
    run('git', 'checkout', '-q', '--force', COMMIT, cwd=SOURCE)
    run('git', 'clean', '-q', '-fdx', '-e', 'Directory.Build.props', cwd=SOURCE)
    run('git', 'submodule', 'update', '-q', '--init', '--force', '--depth', '1', JLIB, cwd=SOURCE)
    run('git', 'clean', '-q', '-fdx', cwd=os.path.join(SOURCE, JLIB))
    with open(os.path.join(SOURCE, 'Directory.Build.props'), 'w') as f:
        f.write(f'''<Project>
  <PropertyGroup>
    <OniGamePath>{game}</OniGamePath>
    <OniManagedPath>$(OniGamePath)/OxygenNotIncluded_Data/Managed</OniManagedPath>
  </PropertyGroup>
</Project>
''')
    run(
        os.path.join(ROOT, 'scripts', 'contain.sh'),
        'dotnet',
        'build',
        'mods/OniMcp/OniMcp.csproj',
        '-c',
        'Release',
        '-p:UseSharedCompilation=false',
        '-nodeReuse:false',
        '-v',
        'q',
        '-nologo',
        cwd=SOURCE,
    )
    built, jlib = os.path.join(SOURCE, 'dist', 'OniMcp'), os.path.join(SOURCE, 'dist', 'Jlib')
    for path in (os.path.join(built, 'OniMcp.dll'), os.path.join(built, 'implementation', 'OniMcp.dll'), os.path.join(jlib, 'Jlib.dll')):
        if not os.path.isfile(path):
            sys.exit(f'onimcp: the build left no {path}')
    if os.path.isdir(dest):
        shutil.rmtree(dest)
    shutil.copytree(built, dest)
    # Jlib's own mod (its LICENSE included), installed beside OniMcp; KMod loads only the
    # DLLs at a mod folder's top, so OniMcp's copy never loads it.
    shutil.copytree(jlib, os.path.join(dest, 'Jlib'))
    for name in ('LICENSE', 'LICENSE.MIT', 'LICENSING.md'):
        if os.path.isfile(os.path.join(SOURCE, name)) and not os.path.exists(os.path.join(dest, name)):
            shutil.copy(os.path.join(SOURCE, name), dest)
    print(f'onimcp: OniMcp at {COMMIT[:10]} in {dest}')


if __name__ == '__main__':
    main()
