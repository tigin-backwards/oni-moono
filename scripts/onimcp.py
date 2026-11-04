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

One change is made to the source before building, and only against a game whose
ReportManager.ReportEntry has CollectNotes (U60; U59 still has IterateNotes, which the pinned
commit calls): NotesToList collects the notes into a list instead. The built mod gets a
MOONO-CHANGES.txt saying so, beside the author's licence (AUL-1.0-draft.1, section 3)."""
import os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = 'https://github.com/LIghtJUNction/OniMods.git'
COMMIT = '997e926f8d87809e402553fa0ea79924b7245ccd'
SOURCE = os.path.join(ROOT, 'out', 'local-tools', 'onimcp-src')
REPORT_TOOLS = os.path.join('mods', 'OniMcp', 'Tools', 'Impl', 'Colony', 'ColonyReportTools.cs')
ITERATE = """            entry.IterateNotes(note =>
            {
                notes.Add(new Dictionary<string, object>
                {
                    ["value"] = Round(note.value),
                    ["note"] = note.note
                });
            });"""
COLLECT = """            var collected = new List<ReportManager.ReportEntry.Note>();
            entry.CollectNotes(collected);
            foreach (var note in collected)
            {
                notes.Add(new Dictionary<string, object>
                {
                    ["value"] = Round(note.value),
                    ["note"] = note.note
                });
            }"""
CHANGES = """OniMcp built by Moono's scripts/onimcp.py from LIghtJUNction/OniMods at {commit},
with one change for Oxygen Not Included U60 (ReportEntry.IterateNotes became CollectNotes):
{path}, NotesToList, collects the report notes into a list with CollectNotes and walks it,
instead of IterateNotes. Nothing else differs from the author's source.
"""


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
    with open(os.path.join(game, 'OxygenNotIncluded_Data', 'Managed', 'Assembly-CSharp.dll'), 'rb') as f:
        u60 = b'CollectNotes' in f.read()
    if u60:
        path = os.path.join(SOURCE, REPORT_TOOLS)
        with open(path, encoding='utf-8') as f:
            text = f.read().replace('\r\n', '\n')
        if text.count(ITERATE) != 1:
            sys.exit(f'onimcp: {REPORT_TOOLS} no longer has the IterateNotes call to change; check the commit')
        with open(path, 'w', encoding='utf-8') as f:
            f.write(text.replace(ITERATE, COLLECT))
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
    for name in ('LICENSE', 'LICENSE.MIT', 'LICENSING.md'):
        if os.path.isfile(os.path.join(SOURCE, name)) and not os.path.exists(os.path.join(dest, name)):
            shutil.copy(os.path.join(SOURCE, name), dest)
    if u60:
        with open(os.path.join(dest, 'MOONO-CHANGES.txt'), 'w', encoding='utf-8') as f:
            f.write(CHANGES.format(commit=COMMIT, path=REPORT_TOOLS.replace(os.sep, '/')))
    print(f'onimcp: OniMcp at {COMMIT[:10]} in {dest}')


if __name__ == '__main__':
    main()
