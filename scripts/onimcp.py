#!/usr/bin/env python3
"""OniMcp, the in-game MCP server e2e.py and mod-sweep.py play through, built at a pinned commit.
  scripts/onimcp.py [DEST]     fetch the commit below, build it against the game (ONI_GAME_DIR,
                               as scripts/env.sh), and copy the mod into DEST (default
                               out/local-tools/onimcp, where qa.env's E2E_ONIMCP points)
It is fetched from its author's repository (github.com/LIghtJUNction/OniMods) and built here
rather than kept in this one: its licence (AUL-1.0-draft.1) puts duties on copies kept and
changed. The specs depend on its tools' arguments and protocol, which change between commits (its
v0.2.5 release takes coordinates only in coordinate_control; from 2026-10-23 it takes only MCP
2025-11-25), so a new commit is taken by changing COMMIT after the e2e specs pass with it."""
import os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = 'https://github.com/LIghtJUNction/OniMods.git'
COMMIT = '997e926f8d87809e402553fa0ea79924b7245ccd'
SOURCE = os.path.join(ROOT, 'out', 'local-tools', 'onimcp-src')


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
    game = os.environ.get('ONI_GAME_DIR') or os.path.expanduser('~/projects/oni-linux-761211')
    if not os.path.isdir(os.path.join(game, 'OxygenNotIncluded_Data', 'Managed')):
        sys.exit(f'onimcp: {game} is not a game install; set ONI_GAME_DIR')
    if not os.path.isdir(os.path.join(SOURCE, '.git')):
        os.makedirs(SOURCE, exist_ok=True)
        run('git', 'init', '-q', cwd=SOURCE)
        run('git', 'remote', 'add', 'origin', REPO, cwd=SOURCE)
    run('git', 'fetch', '-q', '--depth', '1', 'origin', COMMIT, cwd=SOURCE)
    run('git', 'checkout', '-q', '--force', COMMIT, cwd=SOURCE)
    run('git', 'clean', '-q', '-fdx', '-e', 'Directory.Build.props', cwd=SOURCE)
    with open(os.path.join(SOURCE, 'Directory.Build.props'), 'w') as f:
        f.write(f'''<Project>
  <PropertyGroup>
    <OniGamePath>{game}</OniGamePath>
    <OniManagedPath>$(OniGamePath)/OxygenNotIncluded_Data/Managed</OniManagedPath>
  </PropertyGroup>
</Project>
''')
    run(os.path.join(ROOT, 'scripts', 'contain.sh'), 'dotnet', 'build', 'mods/OniMcp/OniMcp.csproj', '-c', 'Release',
        '-p:UseSharedCompilation=false', '-nodeReuse:false', '-v', 'q', '-nologo', cwd=SOURCE)
    built = os.path.join(SOURCE, 'dist', 'OniMcp')
    if not os.path.isfile(os.path.join(built, 'OniMcp.dll')):
        sys.exit(f'onimcp: the build left no {built}/OniMcp.dll')
    if os.path.isdir(dest):
        shutil.rmtree(dest)
    shutil.copytree(built, dest)
    print(f'onimcp: OniMcp at {COMMIT[:10]} in {dest}')


if __name__ == '__main__':
    main()
