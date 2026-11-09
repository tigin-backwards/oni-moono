#!/usr/bin/env python3
"""Renames Moono's internals from OniBrutal to OniMoono: the projects and their folders, the
assemblies, namespaces, Harmony ids, the solution, the Moono Tweaks screen's classes
(BrutalTweaksMenu -> MoonoTweaksMenu) and the scripts and docs that name them.
  scripts/rename-internals.py            rename the working tree (git mv + edits), then audit
  scripts/rename-internals.py --dry-run  print what it would move and change; change nothing
  scripts/rename-internals.py --audit    only list what still names OniBrutal; exit 1 if any
                                         of it is not on the keep list
Idempotent: a second run changes nothing, and a branch from before the rename is brought in
line by running it there too (before merging or rebasing onto the renamed master). It never
commits. What keeps the old name, and why, is the KEEP list; hand-written changes that a
plain replacement can't make (migrations, compatibility with older players) are FIXUPS.
BRUTAL (Ahwoo's framework: its packages, namespaces, licence and credit) is a different name
and is left alone, as are the ONI_* environment variables (except the host's ONI_BRUTAL_RESTART_*
for the Restarter, which ship together) and the machine-wide lock folder ~/.cache/oni-brutal-e2e
that every checkout's e2e and QA runs share.
"""

import os, re, subprocess, sys

OLD, NEW = 'OniBrutal', 'OniMoono'

# Every OniBrutal not on the keep list becomes OniMoono (OniBrutalCheckApi too).
GENERIC = re.compile(r'(?<![A-Za-z0-9_])OniBrutal')
# The rest are named one by one: oni-brutal is also the checkout's folder and the lock folder.
EXPLICIT = [
    (re.compile(r'(?<![A-Za-z0-9_])BrutalTweaks(Menu|Screen|Button)'), r'MoonoTweaks\1'),
    (re.compile(r'\bBrutal Tweaks\b'), 'Moono Tweaks'),
    (re.compile(r'\boni-brutal-(check|layout|prefs|dirorder|settings)-'), r'oni-moono-\1-'),  # temp folders
    (re.compile(r'\boni-brutal\.(tests\.|original-isolation)'), r'oni-moono.\1'),  # Harmony/BepInEx ids
    (re.compile(r'"oni-brutal (play-test driver|frame-rate probe)'), r'"Moono \1'),  # mod.yaml
    (re.compile(r'\boni_brutal_missing_export\b'), 'oni_moono_missing_export'),
    # The host's arguments for the Restarter, which ships with it: no older player reads them.
    (re.compile(r'\bONI_BRUTAL_RESTART_(ARGS|ARGV|PID)\b'), r'ONI_MOONO_RESTART_\1'),
]

# (path regex, span regex, reason): spans the replacements leave alone, and that the audit
# accepts. A path regex of None applies everywhere.
KEEP = [
    (None, r'"OniBrutal", "(?:conversions|startup-checkpoints\.json)"', 'the cache folder of players from before the rename, read once or removed (FIXUPS)'),
    (
        r'\.Host\.csproj$',
        r'\$\(OutDir\)OniBrutal(?:\.(?:exe|dll|pdb|deps\.json|runtimeconfig\.json))?(?=[;"])|\(OniBrutal, OniBrutal\.exe, OniBrutal\.dll …\)',
        'removes the pre-Moono host from old output folders',
    ),
    (r'^scripts/build-windows\.sh$', r'\(OniBrutal\.exe\)|"\$out/host/OniBrutal\."\{[^}]*\}', 'removes the pre-Moono host from old output folders'),
    (r'^scripts/release\.sh$', r'OniBrutal\.exe and its OniBrutal\.dll', 'history: the pre-Moono host'),
    (r'^setup\.sh$', r"oni-brutal's OniBrutal\)|^old_player\(\).*$", 'finds launch options naming the pre-Moono player'),
    (r'^setup\.ps1$', r"-Name OniMoono, OniBrutal\b|oni-brutal's OniBrutal\.exe|^function Is-Old.*$", 'finds the pre-Moono player (process, launch options)'),
    (
        r'^setup\.sh$',
        r'\(OniBrutal and its OniBrutal\.\* assemblies\)|^\s*rm -f "\$player/OniBrutal" "\$player"/OniBrutal\.\*$',
        "deletes a pre-Moono player's files from a release's player/",
    ),
    (
        r'^setup\.ps1$',
        r"\(OniBrutal\.exe and its OniBrutal\.\* assemblies\)|-Filter 'OniBrutal\.\*' \| Where-Object Name -like 'OniBrutal\.\*'",
        "deletes a pre-Moono player's files from a release's player/",
    ),
    (
        r'^docs/DEVELOPMENT\.md$',
        r'deleting the `OniBrutal`$|^and `OniBrutal\.\*` files|renamed from OniBrutal to OniMoono',
        'documents the cleanup and the rename',
    ),
    (r'^README\.md$', r'player \(`OniBrutal`\)', 'tells users of pre-Moono releases what changed'),
    (r'PerfProbe/Probe\.cs$', r'"OniBrutal\.Host\.GameTweaks\.FastSerialization, Oni(?:Brutal|Moono)"', 'the probe also measures older players'),
    (r'E2eDriver/Commands\.cs$', r'\|\| x\.GetName\(\)\.Name\.StartsWith\("OniBrutal", StringComparison\.Ordinal\)', 'the driver also runs in older players'),
    (r'E2eDriver/README\.md$', r'older `OniBrutal\*`', 'the driver also runs in older players'),
    (r'^scripts/qa-tools\.py$', r"\(\?:OniMoono\|OniBrutal\)|replace\('OniBrutal\.', 'OniMoono\.'\)", 'signatures stored by runs from before the rename'),
    (r'^tests/scripts/test_log_signatures\.py$', r"OniBrutal\.Engine\.|were OniBrutal\.\*", 'tests reading signatures stored before the rename'),
]
# What the audit accepts besides KEEP: names that are not ours, or that stay on purpose.
ALLOWED = [
    r'oni-brutal-e2e',  # the machine-wide lock folder, shared with every checkout and branch
    r'projects[/-]oni-brutal\b',  # the checkout's own folder (and Claude's project folder)
    r'%TEMP%\\oni-brutal\b|\$win_temp\\\\oni-brutal\b|\)/oni-brutal"',  # run-windows.sh's staging folder
]
ALLOWED_FILES = {  # whole files whose oni-brutal mentions record the pre-Moono player or research
    'README.md': r'oni-brutal',
    'setup.sh': r'oni-brutal',
    'setup.ps1': r'oni-brutal',
    'src/OniMoono.Host/PlayerSettings.cs': r'oni-brutal',
    'tests/OniMoono.UnityEngine.Tests/PlayerSettingsTests.cs': r'"oni-brutal", "settings\.json"',
    'scripts/qa.sh': r'\bonibrutal\b',  # the pre-Moono player's Windows process
    # Installed by contain.sh whenever it differs: an edit makes checkouts on either side of
    # the rename reinstall it in turn.
    'scripts/oni-tests.slice': r"oni-brutal's",
    'docs/research/brutal-api.md': r'oni-brutal',
}

# (path, text after the replacements, replacement): edits by hand. Each applies once; a run
# that finds the replacement already there skips it, and one that finds neither fails.
FIXUPS = [
    # The cache folder, beside the settings' oni-moono: start-up checkpoints are read from the
    # old folder until the first start-up writes them.
    (
        'src/OniMoono.UnityEngine/Engine/StartupProgress.cs',
        '        static string FilePath => Path.Combine(EngineConfig.TemporaryCachePath, "OniBrutal", "startup-checkpoints.json");\n',
        '        static string FilePath => Path.Combine(EngineConfig.TemporaryCachePath, "oni-moono", "startup-checkpoints.json");\n'
        '        /// Where players from before the Moono name kept them: read until a start-up writes FilePath.\n'
        '        static string OldFilePath => Path.Combine(EngineConfig.TemporaryCachePath, "OniBrutal", "startup-checkpoints.json");\n',
    ),
    (
        'src/OniMoono.UnityEngine/Engine/StartupProgress.cs',
        'JsonDocument.Parse(File.ReadAllText(FilePath))',
        'JsonDocument.Parse(File.ReadAllText(File.Exists(FilePath) ? FilePath : OldFilePath))',
    ),
    # Converted images name the host build that made them, so the old folder's are never read
    # again: it is removed once a new image is kept.
    (
        'src/OniMoono.Host/MonoConversions.cs',
        'cached = Path.Combine(EngineConfig.TemporaryCachePath, "OniBrutal", "conversions"',
        'cached = Path.Combine(EngineConfig.TemporaryCachePath, "oni-moono", "conversions"',
    ),
    (
        'src/OniMoono.Host/MonoConversions.cs',
        '                    File.Move(temporary, cached, overwrite: true);\n',
        '                    File.Move(temporary, cached, overwrite: true);\n                    DeleteOldCache();\n',
    ),
    (
        'src/OniMoono.Host/MonoConversions.cs',
        '        static byte[]? Rewrite(byte[] image, ref byte[]? symbols, string path, string what = "mod conversions")\n',
        '        static bool oldCacheDeleted;\n'
        '\n'
        '        /// The cache folder of players from before the Moono name: its images name an older\n'
        '        /// host build, so none of them is read again.\n'
        '        static void DeleteOldCache()\n'
        '        {\n'
        '            if (oldCacheDeleted) return;\n'
        '            oldCacheDeleted = true;\n'
        '            try { Directory.Delete(Path.Combine(EngineConfig.TemporaryCachePath, "OniBrutal", "conversions"), recursive: true); }\n'
        '            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }\n'
        '        }\n'
        '\n'
        '        static byte[]? Rewrite(byte[] image, ref byte[]? symbols, string path, string what = "mod conversions")\n',
    ),
    ('docs/ARCHITECTURE.md', '(`OniMoono/conversions`', '(`oni-moono/conversions`'),
    # Ours: the host and the engine now share the prefix.
    (
        'src/OniMoono.Host/PatchFrames.cs',
        'name == "OniMoono" || name?.StartsWith("OniMoono", StringComparison.Ordinal) == true',
        'name?.StartsWith("OniMoono", StringComparison.Ordinal) == true',
    ),
    # Tools that also run in, or read the output of, players from before the rename.
    (
        'tools/PerfProbe/Probe.cs',
        '(System.Type.GetType("OniBrutal.Host.GameTweaks.FastSerialization, OniMoono") ?? System.Type.GetType("OniBrutal.Host.GameTweaks.FastSerialization, OniBrutal"))',
        '(System.Type.GetType("OniMoono.Host.GameTweaks.FastSerialization, OniMoono")'
        ' ?? System.Type.GetType("OniBrutal.Host.GameTweaks.FastSerialization, OniMoono")'
        ' ?? System.Type.GetType("OniBrutal.Host.GameTweaks.FastSerialization, OniBrutal"))',
    ),
    (
        'tools/E2eDriver/Commands.cs',
        'x.GetName().Name.StartsWith("OniMoono", StringComparison.Ordinal));',
        'x.GetName().Name.StartsWith("OniMoono", StringComparison.Ordinal) || x.GetName().Name.StartsWith("OniBrutal", StringComparison.Ordinal));',
    ),
    ('tools/E2eDriver/README.md', 'when an `OniMoono*` assembly', 'when an `OniMoono*` (or older `OniBrutal*`) assembly'),
    ('scripts/qa-tools.py', r"r'|^OniMoono\.')", r"r'|^(?:OniMoono|OniBrutal)\.')"),
    (
        'scripts/qa-tools.py',
        "    message, _, frame = signature.partition(' @ ')\n",
        "    signature = signature.replace('OniBrutal.', 'OniMoono.')  # stored before the rename\n    message, _, frame = signature.partition(' @ ')\n",
    ),
    (
        'tests/scripts/test_log_signatures.py',
        '    def test_run_folder_is_masked(self):\n',
        '    def test_references_from_before_the_rename(self):\n'
        '        # A reference stored when the engine\'s namespaces were OniBrutal.* still matches.\n'
        "        self.assertEqual(qa_tools.unknown(['InvalidCastException: to OniMoono.Engine.X'],\n"
        "                                          ['InvalidCastException: to OniBrutal.Engine.X']), [])\n"
        "        self.assertEqual(signatures('NullReferenceException: x\\n  at OniBrutal.Engine.EngineLog.Log ()\\n'),\n"
        "                         signatures('NullReferenceException: x\\n  at OniMoono.Engine.EngineLog.Log ()\\n'))\n"
        '\n'
        '    def test_run_folder_is_masked(self):\n',
    ),
]

AUDIT = re.compile(r'OniBrutal|oni-brutal|onibrutal|oni_brutal|BrutalTweaks|Brutal Tweaks', re.I)


def git(*args, capture=True):
    return subprocess.run(['git', *args], check=True, text=True, capture_output=capture).stdout


SELF = 'scripts/rename-internals.py'


def tracked():
    return [p for p in git('ls-files', '-z').split('\0') if p and p != SELF and os.path.isfile(p)]


def new_path(path):
    parts = path.split('/')
    parts = [p.replace(OLD, NEW) for p in parts]
    if parts[-1] == 'BrutalTweaksMenu.cs':
        parts[-1] = 'MoonoTweaksMenu.cs'
    return '/'.join(parts)


def move(dry):
    """git mv: the folders whole (so untracked files go along), then any tracked file left."""
    moves, moved = 0, []
    for top in ('src', 'tests', 'tools'):
        if not os.path.isdir(top):
            continue
        for name in sorted(os.listdir(top)):
            old = f'{top}/{name}'
            if OLD not in name or not os.path.isdir(old):
                continue
            new = f'{top}/{name.replace(OLD, NEW)}'
            if os.path.exists(new):
                continue  # merged by the per-file pass below
            print(f'move {old} -> {new}')
            moves += 1
            moved.append(old + '/')
            if not dry:
                git('mv', old, new)
    for path in tracked():
        target = new_path(path)
        if target == path or (dry and any(path.startswith(d) for d in moved)):
            continue
        print(f'move {path} -> {target}')
        moves += 1
        if dry:
            continue
        if os.path.exists(target):
            sys.exit(f'rename-internals: {target} exists already; merge {path} into it by hand')
        os.makedirs(os.path.dirname(target) or '.', exist_ok=True)
        git('mv', path, target)
    return moves


def keep_spans(path, line):
    spans = []
    for where, pattern, _ in KEEP:
        if where is None or re.search(where, path):
            spans += [m.span() for m in re.finditer(pattern, line)]
    return spans


def replace_line(path, line):
    spans = keep_spans(path, line)

    def sub(regex, repl, text):
        out, at = [], 0
        for m in regex.finditer(text):
            if any(s <= m.start() < e for s, e in spans):
                continue
            out.append(text[at : m.start()])
            out.append(m.expand(repl))
            at = m.end()
        out.append(text[at:])
        return ''.join(out)

    for regex, repl in EXPLICIT:
        line = sub(regex, repl, line)
        spans = keep_spans(path, line)
    return sub(GENERIC, NEW, line)


def read(path):
    with open(path, 'rb') as f:
        data = f.read()
    if b'\0' in data:
        return None
    return data.decode('utf-8', 'surrogateescape')


def write(path, text):
    with open(path, 'wb') as f:
        f.write(text.encode('utf-8', 'surrogateescape'))


def edit(dry, paths):
    changed = {}
    for path in paths:
        text = read(path)
        if text is None or not AUDIT.search(text):
            continue
        lines = text.splitlines(keepends=True)
        out = ''.join(replace_line(path, l) for l in lines)
        if out != text:
            changed[path] = (text, out)
    for path, (old, new) in sorted(changed.items()):
        n = sum(1 for a, b in zip(old.splitlines(), new.splitlines()) if a != b)
        print(f'edit {path}: {n} line(s)')
        if not dry:
            write(path, new)
    return len(changed)


def fixups(dry):
    applied = 0
    for path, before, after in FIXUPS:
        if not os.path.exists(path):
            if dry:
                print(f'fixup {path}: after the move')
                continue
            sys.exit(f'rename-internals: fixup target {path} is missing; update FIXUPS')
        text = read(path)
        # Applied: the replacement is there and, unless it only adds to the text it replaces,
        # that text is gone (a replacement can be part of what it replaces).
        if after in text and (before in after or before not in text):
            continue
        if dry:  # before the replacements, the text to replace is not there yet
            print(f'fixup {path}: {before.strip()[:70]}')
            applied += 1
            continue
        if text.count(before) != 1:
            sys.exit(f'rename-internals: fixup in {path} found {text.count(before)} matches for:\n{before}\nupdate FIXUPS')
        write(path, text.replace(before, after))
        print(f'fixup {path}: {before.strip()[:70]}')
        applied += 1
    return applied


def audit():
    """Lists what still names OniBrutal; fails on anything not kept on purpose."""
    unexpected, kept = [], 0
    for path in tracked():
        text = read(path)
        if text is None:
            continue
        for number, line in enumerate(text.splitlines(), 1):
            if not AUDIT.search(line):
                continue
            rest = line
            for s, e in sorted(keep_spans(path, line), reverse=True):
                rest = rest[:s] + ' ' * (e - s) + rest[e:]
            for pattern in ALLOWED + ([ALLOWED_FILES[path]] if path in ALLOWED_FILES else []):
                rest = re.sub(pattern, lambda m: ' ' * len(m.group()), rest)
            if AUDIT.search(rest):
                unexpected.append(f'{path}:{number}: {line.strip()[:160]}')
            else:
                kept += 1
    for path in tracked():
        if AUDIT.search(path):
            unexpected.append(f'{path}: (file name)')
    print(f'audit: {kept} line(s) keep the old name on purpose, {len(unexpected)} unexpected')
    for u in unexpected:
        print('  ' + u)
    return not unexpected


def main():
    args = set(sys.argv[1:])
    if args - {'--dry-run', '--audit'}:
        sys.exit(__doc__)
    os.chdir(git('rev-parse', '--show-toplevel').strip())
    if '--audit' in args:
        sys.exit(0 if audit() else 1)
    dry = '--dry-run' in args
    moves = move(dry)
    edits = edit(dry, tracked())
    fixed = fixups(dry)
    print(f'{"would make" if dry else "made"}: {moves} move(s), {edits} edited file(s), {fixed} fixup(s)')
    if not dry:
        sys.exit(0 if audit() else 1)


if __name__ == '__main__':
    main()
