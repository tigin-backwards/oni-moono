#!/usr/bin/env python3
"""scripts/modtable.py [CATALOG.tsv] > docs/MOD_COMPATIBILITY.md
The compatibility table from onimaxxing's catalog ranks (a TSV of rank, id, subscriptions, flags,
title; default scripts/mod-catalog.tsv) and what our runs recorded.

The catalog TSV comes from onimaxxing's catalog.json (format 2, column arrays): a mod's rank is its
place among all the catalog's mods by subscriptions, most first (a stable sort, so ties keep the
catalog's order); its flags are analysis.breaks (crashesAtStartup, mayCrashInPlay), else
notLoading, else empty ("unanalysed" for a mod the catalog has no analysis of). Ranks 1-750 are
all there, flagged ones included. Beyond 750 the TSV holds only the mods that work in the
original as far as onimaxxing knows: those flagged notLoading, crashesAtStartup or malware are
left out, down to the rank (2274) where the mods kept, ranks 1-750 counted too, make 2000.
Every rank in the TSV is listed in the table (LAST_RANK is its highest). scripts/mod-catalog-u60.tsv
(or ONI_CATALOG_U60; read beside a CATALOG argument only when that is set) adds, in the same format,
the U60 catalog's mods the TSV lacks (less those flagged brokenByUpdate or malware), with their U60
ranks, sorted in among its rows.

What our runs recorded:
- the load sweeps of scripts/mod-sweep.py, tests/e2e/sweeps/*.tsv (or ONI_SWEEP_RESULTS): per mod
  whether it fails in the original, and if not whether it fails in ours, at the main menu or in
  the colony; a later file (by name) overrides an earlier one, and these override the older
  sweeps below; one whose name starts with u60 (u60-2000.tsv) swept on U60;
- the older sweeps' sweep-*.txt ID lists and modtable-*.tsv results in out/ (or ONI_SWEEP_DIR; in
  a worktree whose out/ has none, the main checkout's out/);
- the spec results in tests/e2e/results/*.json (or ONI_RESULTS_DIR), written by
  `scripts/e2e.py --record`: a record's verdict and note decide the Played column, ahead of the
  hand-written status sets below, which stay for mods no spec has played yet;
- then those played on U60, tests/e2e/results-u60/*.json (or ONI_RESULTS_U60_DIR): a mod's U60
  record wins over its main one, and its note starts "on U60:"."""
import glob, json, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main_checkout_out():
    """The main checkout's out/ when this is a worktree (git's common dir is the main .git)."""
    common = subprocess.run(['git', '-C', ROOT, 'rev-parse', '--path-format=absolute', '--git-common-dir'],
                            capture_output=True, text=True).stdout.strip()
    return os.path.join(os.path.dirname(common), 'out') if common else None


def sweep_dir():
    if os.environ.get('ONI_SWEEP_DIR'):
        return os.environ['ONI_SWEEP_DIR']
    own = os.path.join(ROOT, 'out')
    if glob.glob(os.path.join(own, 'sweep-*.txt')):
        return own
    main = main_checkout_out()
    return main if main and glob.glob(os.path.join(main, 'sweep-*.txt')) else own


def read_results(folder):
    """The spec records (tests/e2e/results/SPEC.json) as {mod id: record}; pending ones (not yet
    played to a verdict) are left out."""
    by_mod = {}
    for path in sorted(glob.glob(os.path.join(folder, '*.json'))):
        with open(path, encoding='utf-8') as f:
            record = json.load(f)
        if record.get('verdict') not in ('played', 'warning', 'failed'):
            continue
        # A spec outside mods/ checks an engine fix with the mods it needs
        # (plib-options-copies.yaml), not those mods.
        if not record.get('spec', '').startswith('mods/'):
            continue
        # A spec is the record of the mod it is named for (mods/ID-slug.yaml); the other mods it
        # installs (a base mod an addon needs) are not played by it.
        named = os.path.basename(record.get('spec', '')).split('-')[0]
        mods = [str(m) for m in record.get('mods', [])]
        for mid in ([named] if named in mods else mods):
            by_mod[mid] = record
    return by_mod


def read_sweeps(folder):
    """scripts/mod-sweep.py's results (tests/e2e/sweeps/NAME.tsv: id, rank, original, ours, note)
    as {mod id: (original, ours, note, game)}; a later file overrides an earlier one. game is 'U60 '
    for a file whose name starts with u60 (a sweep on U60), else ''."""
    by_mod = {}
    for path in sorted(glob.glob(os.path.join(folder, '*.tsv'))):
        game = 'U60 ' if os.path.basename(path).startswith('u60') else ''
        for line in open(path, encoding='utf-8'):
            if line.startswith('#') or line.startswith('id\t'):
                continue
            mid, _, original, ours, note = (line.rstrip('\n').split('\t') + [''] * 5)[:5]
            by_mod[mid] = (original, ours, note, game)
    return by_mod


def sweep_status(original, ours, note, game=''):
    """(Loads, Colony, Played, note) from a load sweep's verdicts."""
    batch = f'in its {game}sweep batch'
    if original in ('menu', 'colony'):
        where = 'at the main menu' if original == 'menu' else 'in the colony'
        return X, X, N, f'fails to load in the original as well, {where}, {batch}' + (f': {note}' if note else '')
    if ours == 'menu':
        return N, N, N, f'fails in ours at the main menu, {batch}: {note}'
    if ours == 'colony':
        return Y, N, N, f'its colony fails in ours, {batch}: {note}'
    if ours == 'ok':
        return Y, Y, N, note
    return N, N, N, note


def read_findings(path):
    if not os.path.exists(path):
        return {}
    import yaml
    with open(path, encoding='utf-8') as f:
        return {x['id']: x for x in yaml.safe_load(f) or []}


def finding_text(ident, findings):
    link = findings.get(ident, {}).get('link', '')
    return f'[{ident}]({link})' if link.startswith('http') else f'`{ident}`'


def record_status(record, findings):
    """(Played mark, note) of a spec record; a U60 one's note starts "on U60:"."""
    on = 'on U60: ' if record.get('u60') else ''
    note = f'tests/e2e/{record["spec"]}' + (f': {record["note"]}' if record.get('note') else '')
    if record['verdict'] == 'played':
        return '✅', f'{on}spec {note}'
    if record['verdict'] == 'warning':
        cited = ', '.join(finding_text(i, findings) for i in record.get('findings', []))
        return '⚠️', f'{on}spec {note}' + (f'; known findings: {cited}' if cited else '')
    return '⬜', f'{on}spec fails in ours: {note}' + (f' ({record["why"]})' if record.get('why') else '')


SWEEPS = sweep_dir()
RESULTS = os.environ.get('ONI_RESULTS_DIR') or os.path.join(ROOT, 'tests', 'e2e', 'results')
RESULTS_U60 = os.environ.get('ONI_RESULTS_U60_DIR') or os.path.join(ROOT, 'tests', 'e2e', 'results-u60')
SWEPT = read_sweeps(os.environ.get('ONI_SWEEP_RESULTS') or os.path.join(ROOT, 'tests', 'e2e', 'sweeps'))
FINDINGS = read_findings(os.path.join(ROOT, 'tests', 'e2e', 'known-findings.yaml'))
if len(sys.argv) > 2: sys.exit(__doc__)
catalog = sys.argv[1] if len(sys.argv) == 2 else os.path.join(ROOT, 'scripts', 'mod-catalog.tsv')
cat = [l.rstrip('\n').split('\t') for l in open(catalog, encoding='utf-8')]
LAST_RANK = max(int(r[0]) for r in cat)
U60_IDS = set()
CATALOG_U60 = os.environ.get('ONI_CATALOG_U60') or (
    None if len(sys.argv) == 2 else os.path.join(ROOT, 'scripts', 'mod-catalog-u60.tsv'))
if CATALOG_U60 and os.path.exists(CATALOG_U60):
    known = {r[1] for r in cat}
    u60 = [r for r in (l.rstrip('\n').split('\t') for l in open(CATALOG_U60, encoding='utf-8')) if r[1] not in known]
    U60_IDS = {r[1] for r in u60}
    cat += u60
    cat.sort(key=lambda r: int(r[0]))  # stable: the TSV's row first at a rank both have
by_id = {r[1]: r for r in cat}
FULL_RANKS = 750  # every mod up to this rank; beyond it, those that work in the original
results = read_results(RESULTS)
for mid, record in read_results(RESULTS_U60).items():
    results[mid] = dict(record, u60=True)

def ids(pattern):
    s = set()
    for f in glob.glob(os.path.join(SWEEPS, pattern)):
        s.update(open(f).read().split())
    return s

swept = ids('sweep-b*.txt') | ids('sweep-d*.txt') | ids('sweep-e*.txt') | ids('sweep-f*.txt')
third = ids('sweep-g*.txt')
# Ranks 501-750: their colony runs used 2minnow (sweep-o*.txt, rerun without the mods below).
fifth = ids('sweep-o[0-9].txt')
fifth_failing = set(ids('sweep-o-excluded.txt'))

played = {
    '2837919908': 'clicks select what is under the cursor (its flat-scaled world selector); played on U57 and the Twinkleverse save it was reported with',
    '1960947963': 'hover cards draw as in the original (needed ARCHITECTURE D17, D18)',
    '2815406414': 'tiles connect as in the original (needed D21)',
    '3630759126': 'menu and U57 colony with 0 errors (needed its IMGUI and asset bundle API, D20, the TMP caret fix); a multiplayer session needs two Steam accounts, not tested',
    '3775625682': "a player's images packed and shown on a canvas as in the original (needed `Texture2D.PackTextures`)",
    '3781314647': 'a colony with its new elements, 0 errors in both players (needed `Shader.GetPropertyCount/Name/Type`)',
    '2958243747': "issue #2's set",
    '2977728791': "issue #2's set",
    '3381262702': "issue #2's set",
    # Played by their specs (tests/e2e/mods, scripts/e2e.py): passed once in the original and
    # twice in ours, with no new log signature and screens matching the original's.
    '1717463209': 'spec tests/e2e/mods/1717463209-bigger-camera-zoom-out.yaml: the mouse wheel zooms out past the game\'s 20, to 200; world shots at three zoom levels within 3.1 % of the original',
    '2094698134': 'spec tests/e2e/mods/2094698134-airlock-door.yaml: built from copper through a build order, its side screen and status as in the original, shut and uncharged without power',
    '1893887916': 'spec tests/e2e/mods/1893887916-research-queue.yaml: Shift+click queues a tech with its prerequisites and numbers the queue; a click takes one out or replaces the queue; the research screen as in the original',
}
# Played by their specs, with a small difference in what the UI reports (not in what it shows):
# ⚠️ until it is fixed. The differences are the engine's, found by the specs' layout dumps.
differences = {
    '1863428350': 'spec tests/e2e/mods/1863428350-sweep-by-type.yaml: the filtered sweep marks only the checked material, and its list looks as in the original (no pixel differs); its layout dump does not match: a nested canvas reports scaleFactor 1 (0.86 in the original), its scroll view normalizedPosition x 1 (0), and an unused input field has laid out its caret',
    '3468585385': 'spec tests/e2e/mods/3468585385-blueprints-expanded.yaml: a blueprint created, chosen, rotated and placed through its windows ([#6](https://github.com/tigin-backwards/oni-moono/issues/6) fixed), screens within 0.6 % of the original\'s pixels; the preview\'s scroll view reports normalizedPosition x 1 (0 in the original) and its content sits up to 30 px aside (no more than 0.4 % of the window\'s pixels differ)',
    '2856555858': 'spec tests/e2e/mods/2856555858-duplicant-stat-selector.yaml: stat editing ([#5](https://github.com/tigin-backwards/oni-moono/issues/5) fixed), removing an interest and a trait, adding one through its searchable list, and printing the Duplicant with it; screens within 0.3 % of the original\'s pixels; the list\'s empty search field holds a zero-width space (empty in the original), which does not show',
}
# Loads and Colony as swept, Played withdrawn until a spec drives the mod's UI (mod-play-testing.md).
unplayed = {
    # Batch 17.
    # Batch 21.
}
# Loads and Colony as swept; a session cannot be played here, in either player (no Played verdict).
not_playable = {
    '3807965885': 'not playable here, the same in the original: a session is a host and a client game over TCP (or Steam P2P), and an e2e run has one game process; its hub and JOIN A GAME dialog open, and a connect to an address nobody listens on is refused and retried (spec tests/e2e/mods/3807965885-oni-multiplayer.yaml, recorded in the original only)',
    '3635632392': 'not playable here, the same in the original: a session is a Steam lobby (its LiteNetLib backend is never used), and the offline Steam double creates and joins no lobby; its lobby screen opens, HOST GAME and JOIN GAME stay at their first status, on two players at once (spec tests/e2e/mods/3635632392-antigravity-multiplayer.yaml); a session needs two real Steam accounts',
}
broken = {
    '1715248368': 'broken upstream: patches `Deconstructable.TriggerDestroy`, which now has two overloads',
    '2363561445': 'broken upstream: `STRINGS.UI.TOOLS.FILTERLAYERS.ALL` is no longer a field',
    '1885337332': 'broken upstream: calls `TreeFilterable.GetTags()`, which the game no longer has',
    '2705951522': 'broken upstream: Harmony patch targets now have overloads',
    '1971998416': 'broken upstream: Harmony patch targets now have overloads',
    '1871382174': 'broken upstream: Harmony patch targets now have overloads',
    '1758223146': 'broken upstream: a Harmony patch whose target is gone',
    '2271373459': 'broken upstream: `OverlayModes.Mode.CreateDefaultFilters` returns another type now',
    '2968184715': 'broken upstream: its animations fail to load',
    '2542160819': 'broken upstream: calls `SimMessages.SetCellProperties(int, byte)`, which is gone',
    '3231839363': 'broken upstream: calls `SimMessages.SetCellProperties(int, byte)`, which is gone',
    '2119648603': "broken upstream: uses TextMeshPro's `FaceInfo`, which the game's TextMeshPro lacks",
    '3046440992': "broken upstream: uses TextMeshPro's `FaceInfo`, which the game's TextMeshPro lacks",
    '1708810461': 'broken upstream: CaiLib calls `EntityTemplates.ExtendEntityToBasicPlant` with arguments the game no longer takes, and the save fails to load',
    '2808978019': 'broken upstream: a Harmony patch whose target is gone (`Deconstructable.SpawnItemsFromConstruction`), and the error dialog quits the game',
    '2661907148': 'broken upstream: adds a space POI the game already has ("Granite"), and the save fails to load',
    '2111768571': 'broken upstream (its title says so): its plants call a removed `EntityTemplates.ExtendEntityToBasicPlant` overload',
    '1992978572': 'broken upstream: its filter patch reads the game\'s changed filter type and crashes the game',
    '1823182964': 'broken upstream: CaiLib calls `EntityTemplates.ExtendEntityToBasicPlant` with arguments the game no longer takes, and the save fails to load',
}
# Ranks 501-750 that fail as they do in the original (out of the sweep's colony runs).
broken.update({
    '2778263368': 'broken upstream: a Harmony transpiler whose target method is gone, and the error dialog quits the game',
    '2646067420': 'broken upstream: calls `ConsumedElement(Tag, float)`, which the game no longer has, and the error dialog quits the game',
    '2043689746': 'broken upstream: calls a `SettingLevel` constructor the game no longer has, and the error dialog quits the game',
})
# Found by the e2e specs of batch 05: the spec fails the same way in the original.
broken.update({
    '2584828134': 'broken upstream (its title says so): its sleeping spots still name the game\'s former `Worker` class, so a Duplicant who lies down in one gets a TypeLoadException and none of the bed\'s effects',
    '1820638089': 'broken upstream: its LIQUIDBOTTLER building\'s codex entry collides with the game\'s own Bottle Filler (LiquidBottler), and loading a colony stops at the crash screen',
})
# Found by the e2e specs of batch 07: the spec fails the same way in the original.
broken.update({
    '2869608898': 'broken upstream: its printing pod side screen calls `Dropdown.OptionData(string, Sprite)`, which the game\'s UI library no longer has, so the selected pod (with the mod\'s BioPrinter) shows no Bio-Ink screen and the crash report screen opens (spec tests/e2e/mods/2869608898-bio-inks.yaml)',
})
# Found by the e2e specs of batch 06: the spec fails the same way in the original.
broken.update({
    '2629866529': 'broken upstream: its PliersWorkable overrides `OnCompleteWork(Worker)`, and the game no longer has a `Worker` type: the type fails to load while the game creates its prefabs, and the player aborts loading a colony; spec tests/e2e/mods/2629866529-pliers-2021.yaml',
    '2539984678': 'broken upstream: its Constructable.OnSpawn transpiler multiplies the AETN\'s 2 cores by a core\'s 25 kg, but the game counts cores by the unit, so a blueprint asks for 50 Neutronium Cores and is never built; spec tests/e2e/mods/2539984678-rebuildable-aetn.yaml',
})
# Found by the e2e specs of batch 08: the spec fails the same way in the original.
broken.update({
    '1718226085': 'broken upstream: Mono cannot set up its emitter and receiver types (`ISliderControl` changed: "VTable setup of type WirelessAutomation.WirelessSignalEmitter failed"), which stops the game\'s prefab creation in `Assets.OnPrefabInit`, so loading a colony leaves prefabs missing and the game crashes (SIGSEGV) twice in two runs (spec tests/e2e/mods/1718226085-wireless-automation.yaml)',
    '1972768699': 'broken upstream: its 0 button and tool work, and a dig errand set to 0 carries the mod\'s precondition and priority class, yet a Duplicant digs it within 300 s, so priority 0 no longer holds errands back (spec tests/e2e/mods/1972768699-priority-zero.yaml)',
    '2356326908': 'broken upstream: its PLib side screens look for `DetailsScreen.sideScreenContentBody`, which the game no longer has, so loading a colony opens the crash screen (spec tests/e2e/mods/2356326908-wireless-power.yaml)',
    '2037089892': 'broken upstream: Mono cannot set up its `Trashcan` type (`ISidescreenButtonControl` gained `SetButtonTextOverride` and `HorizontalGroupID`: "VTable setup of type Trashcans.Trashcan failed"), which stops the game\'s prefab creation in `Assets.OnPrefabInit`, so loading a colony crashes (SIGSEGV) (spec tests/e2e/mods/2037089892-trashcans.yaml)',
    '2053191586': 'broken upstream: Mono cannot set up its `InfiniteSource` type (`ISliderControl.GetSliderTooltip` now takes an index: "VTable setup of type InfiniteSourceSink.InfiniteSource failed"), its buildings fail to register, and loading a colony quits the game (spec tests/e2e/mods/2053191586-new-infinite-sources.yaml)',
    '2524767809': 'broken upstream: its emptier\'s state machine calls `StateMachine.Instance.Subscribe(int, Action<object>)`, which the game no longer has, so the finished building throws a MissingMethodException in `BottleUserEmptier.OnSpawn` and the crash screen opens (spec tests/e2e/mods/2524767809-bottle-emptier-variable-size.yaml)',
})
# Found by the e2e specs of batch 12: the spec fails the same way in the original.
broken.update({
    '2897886291': 'broken upstream: its overlay overrides `OverlayModes.Mode.CreateDefaultFilters`, which returns another type now, so its `OverlayScreen.RegisterModes` postfix throws a MissingMethodException while the colony loads, the overlay screen has no modes and the crash screen opens (spec tests/e2e/mods/2897886291-sweep-zones.yaml)',
})
# Found by the e2e specs of batch 13: the spec fails the same way in the original.
broken.update({
    '1703400426': 'broken upstream: its DebugHandler.OnKeyDown prefix consumes Action 173, which the game\'s Action enum now gives to SreenShot32x (the debug toggle is 174), so with debug mode on Backspace still runs the game\'s debug toggle (its paint screen opens) and reveals the map (spec tests/e2e/mods/1703400426-debug-does-not-discover-map.yaml)',
})
# Found by the e2e specs of batch 17: the spec fails the same way in the original.
broken.update({
    '2872099618': 'broken upstream: its SpaceBomb\'s BuildingComplete.OnSpawn postfix calls `SimMessages.Dig(int, int, bool)`, which the game no longer has, so a finished bomb deletes itself and marks its background as space but digs nothing (spec tests/e2e/mods/2872099618-bomb-space.yaml)',
    '2367992483': 'broken upstream: its prefix names the 19-parameter `EntityTemplates.ExtendEntityToBasicPlant` of 2021 (the game\'s has 20); Harmony lists the method as patched and the game builds its plants through it, yet the prefix never runs, so no plant data or drop times are written or read (spec tests/e2e/mods/2367992483-automatic-harvesting.yaml)',
})
# Found by the e2e specs of batch 20: the spec fails the same way in the original.
broken.update({
    '3036286512': 'broken upstream: its DLL references PLib 4.8.1 but ships without it, so its `OnLoad` (`PUtil.InitLibrary`) throws a FileNotFoundException, its building fails to register (NullReferenceException, its anim missing), and the error dialog quits the game before the colony loads (spec tests/e2e/mods/3036286512-gas-liquefaction-machine.yaml)',
    '3425252325': 'broken upstream: its DLL references PLib 4.16 but ships without it, so its types fail to load (FileNotFoundException), `Assets.OnPrefabInit` stops on a ReflectionTypeLoadException and the game quits before OniMcp starts; it loads only beside another mod that ships PLib (spec tests/e2e/mods/3425252325-auto-drop-gas-bottler.yaml)',
    '1869090131': 'broken upstream: Mono cannot set up its `DestructibleWorkable` type ("VTable setup of type DestructibleFeatures.DestructibleWorkable failed": `ISidescreenButtonControl` gained members, and it overrides `OnCompleteWork(Worker)`), so a spawned geyser gets no workable and no DESTROY button (spec tests/e2e/mods/1869090131-destructible-features.yaml)',
})
# Found by the e2e specs of batch 21: the spec fails the same way in the original.
broken.update({
    '1873884303': 'broken upstream: on the base-game fixture its `OnLoad` stops on a HarmonyException (its `DetailsScreen.OnPrefabInit` patch takes `___sideScreenContentBody`, a field the game no longer has), its scanner then fails to register ("First anim file needs to be non-null", a NullReferenceException in RegisterBuilding), and the game disables it ("Failed to load mod ...disabling") and closes before the save loads',
    '2215696830': 'broken upstream: its tile\'s `ItemPermeableTile.OnSpawn` calls `SimMessages.ClearCellProperties(int, byte)`, which the game no longer has, so every finished Item Permeable Tile throws a MissingMethodException and opens the crash report screen (spec tests/e2e/mods/2215696830-item-permeable-tiles.yaml)',
})
# Found by the e2e specs of batch 24: the spec fails the same way in the original.
broken.update({
    '2029450933': 'broken upstream: its plants call the 19-parameter `EntityTemplates.ExtendEntityToBasicPlant` the game no longer has, so their prefabs fail in `Assets.OnPrefabInit` (MissingMethodException), its care package postfix throws an InvalidCastException, and loading a colony stops at "Error deserializing prefabs" and quits (spec tests/e2e/mods/2029450933-omni-flora.yaml)',
})
# Found by the e2e specs of ranks 1046 to 1130: the spec fails the same way in the original.
broken.update({
    '3456696039': 'broken upstream: its DLL references PLib 4.8.1 but ships without it, so loading it throws a FileNotFoundException while Harmony reads its patch attributes, the game disables it and restarts before the colony loads; it loads only beside another mod that ships PLib (spec tests/e2e/mods/3456696039-dupes-speed-custom.yaml)',
})
# Found by the e2e specs of ranks 1131-1215: the spec fails the same way in the original.
broken.update({
    '1863180631': 'broken upstream: its `ToolParameterMenu.PopulateMenu` postfix calls `ChangeToSetting` with the layer\'s name through Traverse, but the game\'s method now takes a widget, so the call finds nothing and the Deconstruct tool\'s filter stays on All (spec tests/e2e/mods/1863180631-set-default-deconstruct-layer.yaml)',
    '2627892585': 'broken upstream: its builder\'s state machine subscribes to `TreeFilterable.OnFilterChanged` as an `Action<Tag[]>`, a field the game now types differently, so the finished building throws a MissingFieldException in `LiquidLockBuilder.OnSpawn` and the crash screen opens (spec tests/e2e/mods/2627892585-liquid-lock-builder.yaml)',
    '3635077282': 'broken upstream: a Multi-Selection Gas Sensor standing in oxygen, with oxygen in its filter, never switches on (spec tests/e2e/mods/3635077282-multi-selection-sensors.yaml)',
    '2848571032': 'broken upstream: a middle click on a reachable cell, with a Duplicant selected, does not send it there; the Duplicant stays where it stood (spec tests/e2e/mods/2848571032-move-duplicant-middle-click.yaml)',
})
# Found by the e2e specs of ranks 1301-1385: the spec fails the same way in the original.
broken.update({
    '2021224960': 'broken upstream: its Empty Storage button names action 273, which is now `BuildMenuLeft` and bound to no key, so building the button\'s tooltip throws an ArgumentException and selecting any storage building opens the crash screen (spec tests/e2e/mods/2021224960-empty-the-storage.yaml)',
})
# Found by the e2e specs of ranks 1386-1470: the spec fails the same way in the original.
broken.update({
    '2883456864': 'broken upstream: its side screen is never added (it looks for DetailsScreen.sideScreenContentBody, which the game no longer has), so the sign\'s picture cannot be chosen (spec tests/e2e/mods/2883456864-flying-fantasy.yaml)',
    '3479021027': 'broken upstream: its merged PLib asks for TMPro.FaceInfo, which the game\'s TextMeshPro no longer has, so selecting a storage bin or any other building it covers throws a TypeLoadException and opens the crash screen (spec tests/e2e/mods/3479021027-delivery-temperature-limit-fixed.yaml)',
    '2877166424': 'broken upstream: a completed Nuclear Waste Reactor throws a NullReferenceException in Polymerizer.UpdateOilMeter (its kanim has no oil meter) and the crash screen opens (spec tests/e2e/mods/2877166424-nuclear-waste-disposal.yaml)',
})
# Found by the e2e specs of ranks 1471-1555: the spec fails the same way in the original.
broken.update({
    '2368685352': 'broken upstream: its compressors\' and ports\' operational-changed handler casts the event\'s data to a type the game no longer sends, so every finished Quantum Compressor or port throws an InvalidCastException in EnergyConsumer.OnPrefabInit and the crash screen opens (spec tests/e2e/mods/2368685352-quantum-compressors.yaml)',
})
# Found by the e2e specs of ranks 1641-1725: the spec fails the same way in the original.
broken.update({
    '3466461752': 'broken upstream: its builder\'s state machine calls `StateMachine.Instance.Subscribe(int, Action<object>)`, which the game no longer has, so the finished building throws a MissingMethodException in `LiquidLockBuilder.OnSpawn` and cannot be selected (spec tests/e2e/mods/3466461752-uf-liquid-lock-builder.yaml)',
    '3332098282': 'broken upstream: its postfix on `SimpleInfoScreen.RefreshStoragePanel` calls `CollapsibleDetailContentPanel.SetLabelWithButton`, which the game no longer has, so selecting a building with storage throws a MissingMethodException and the crash screen opens (spec tests/e2e/mods/3332098282-advanced-storage-v4.yaml)',
})
# Flagged by onimaxxing as not loading, played by spec tests/e2e/mods/flagged-no-mod-info.yaml on
# the base-game colony (game build U59-744825): KMod skips them in both players.
NO_MOD_INFO = ('broken upstream: ships no mod_info.yaml, which KMod requires since 2021: it logs "Is '
               'missing a mod_info.yaml file and will not be loaded" and leaves the mod disabled '
               '(spec tests/e2e/mods/flagged-no-mod-info.yaml)')
OLD_API = ('broken upstream: its mod_info.yaml does not declare `APIVersion: 2` (a Harmony 1 mod): KMod '
           'logs "DLLs found but not using the correct API version" and loads none of it (spec '
           'tests/e2e/mods/flagged-no-mod-info.yaml)')
broken.update({i: NO_MOD_INFO for i in (
    '1733560461', '1875446986', '1840237194', '1735543242', '1737690640', '1797345263', '1715802131',
    '1810563583', '2191194318', '1810801031', '1852077143', '1796171773', '1728703506', '1842684413',
    '1808602247', '1823287206', '1763024961', '1713687582', '2100888351', '1732806087', '1824440419',
    '2016657763', '1855385913', '1934095066', '1984954968', '1413765966', '1864152914', '1940755077',
    '1883889432', '1860778068', '1869114764', '2019457228', '1737026266', '1836181316', '2017868782',
    '1841466183', '1995546475', '1963899596')})
broken.update({i: OLD_API for i in ('1874426480', '2276953041')})
broken.update({
    '1946209956': 'broken upstream: each of its outfits throws a NullReferenceException in `EquipmentConfigManager.RegisterEquipment` (its `LoadGeneratedEquipment` postfix), so `Assets.OnPrefabInit` fails, and its care package postfix throws an InvalidCastException in `Immigration.OnPrefabInit` (CaiLib reads the game\'s care packages as a `CarePackageInfo[]`, now a list), and the game quits while loading the colony (spec tests/e2e/mods/1946209956-fashionistic.yaml)',
    '2360425208': 'broken upstream: its 600 s printing interval is set, but opening the printing pod\'s screen runs its `MinionStartingStats.GenerateTraits` prefix, which looks up each Duplicant\'s congenital trait, empty today ("Could not find Klei.AI.Trait: "), throws a NullReferenceException and opens the crash screen (spec tests/e2e/mods/2360425208-printing-pod.yaml)',
    '2042663291': 'broken upstream: its PLib side screen looks for `DetailsScreen.sideScreenContentBody`, which the game no longer has (a DetourException in `DetailsScreen.OnPrefabInit`), so loading a colony opens the crash screen (spec tests/e2e/mods/2042663291-configurable-sweepy.yaml)',
})

# Flagged by onimaxxing and found by their e2e specs: the game (U59-744825) does not load them,
# in the original as in ours.
broken.update({
    '1713359495': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1713359495-speed-control.yaml)',
    '1724518038': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1724518038-refresh-selection.yaml)',
    '1735550356': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1735550356-self-sealing-airlocks.yaml)',
    '1728728517': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1728728517-super-miner.yaml)',
    '1830443217': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1830443217-travel-tube-crossing.yaml)',
    '1742045344': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1742045344-player-controlled-switch.yaml)',
    '1285243247': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1285243247-extra-large-map.yaml)',
    '1748601256': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1748601256-longer-bridges.yaml)',
    '1852314427': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1852314427-compact-info-cards.yaml)',
    '1711115962': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1711115962-infinite-sources.yaml)',
    '1714094338': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1714094338-buildable-aetn.yaml)',
    '1888643088': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1888643088-telescope-auto-research.yaml)',
    '1981435171': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1981435171-fan-tiles.yaml)',
    '1885140949': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/1885140949-carbon-revolution.yaml)',
    '2193798307': 'broken upstream: its Workshop build has no mod_info.yaml, so the game skips it ("Is missing a mod_info.yaml file and will not be loaded") and the colony loads without it (spec tests/e2e/mods/2193798307-autoharvest.yaml)',
    '1814341183': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it (spec tests/e2e/mods/1814341183-blueprints.yaml)',
    '1848884654': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it (spec tests/e2e/mods/1848884654-pliers.yaml)',
    '1835851890': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it, on the base-game fixture as it supports the base game only (spec tests/e2e/mods/1835851890-drag-tool-area.yaml)',
    '1766168866': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it (spec tests/e2e/mods/1766168866-half-door.yaml)',
    '1820866530': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it (spec tests/e2e/mods/1820866530-piped-output.yaml)',
    '1836997281': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it, on the base-game fixture as it supports the base game only (spec tests/e2e/mods/1836997281-claustrophobia.yaml)',
    '1730126196': 'broken upstream: built for Harmony 1 (its mod_info.yaml names no APIVersion), so the game disables it ("DLLs found but not using the correct API version") and the colony loads without it (spec tests/e2e/mods/1730126196-pipe-pressure-valve.yaml)',
})
# Flagged by onimaxxing; they load, and their e2e specs fail the same way in the original.
broken.update({
    '1918000273': 'broken upstream: its Resculpt button shows on a finished sculpture, but a click throws a MissingFieldException (it reads `Db.ArtableStages`, which the game now reaches through `Db.GetArtableStages()`), so the design never changes (spec tests/e2e/mods/1918000273-resculpt.yaml)',
    '3105273056': 'broken upstream: in Simplified Chinese its room constraint copies call a `RoomConstraints.Constraint` constructor the game no longer has, so the Pressure Door fails to register (TypeInitializationException) and the game quits before the colony loads; in English it does nothing (spec tests/e2e/mods/3105273056-translate-fix.yaml)',
    '2070840646': 'broken upstream: one of its patches names `MotdServerClient`, a type the game no longer has, so it fails to load (TypeLoadException), the game disables it and the game quits before the colony loads (spec tests/e2e/mods/2070840646-taiwanese-language-patch.yaml)',
    '2997749628': 'broken upstream: its ChoreConsumerPatch names a ChoreConsumer method the game no longer has, so Harmony throws ("Undefined target method") while the game creates its prefabs and the game quits before the colony loads; a multiplayer session (two Steam accounts) was not tried (spec tests/e2e/mods/2997749628-multiplayer-mod.yaml)',
    '1932765859': 'broken upstream: its ScheduleScreenEntry.Setup prefix takes `___optionsPanel` and `___optionsButton`, fields the game no longer has, so Harmony throws ("No such field defined in class ScheduleScreenEntry"), the game disables it ("Failed to load mod ...disabling") and the game quits before the colony loads (spec tests/e2e/mods/1932765859-schedule-shift.yaml)',
    '1732501619': 'broken upstream: its lamps are built of `TUNING.MATERIALS.TRANSPARENTS`, which the game no longer has, so each fails in RegisterBuilding (MissingFieldException) and the game quits before the colony loads (spec tests/e2e/mods/1732501619-decor-lights.yaml)',
    '1704376369': 'broken upstream: its Window is built of `TUNING.MATERIALS.TRANSPARENTS`, which the game no longer has, so it fails in RegisterBuilding (MissingFieldException) and the game quits before the colony loads (spec tests/e2e/mods/1704376369-buildable-poi-props.yaml)',
    '2059584551': 'broken upstream: its bridges name `DUPLICANTSTATS.MOVEMENT` (the game\'s `MOVEMENT_MODIFIERS` now), so they fail in RegisterBuilding (TypeLoadException) and the game quits before the colony loads (spec tests/e2e/mods/2059584551-insulated-joint-plate.yaml)',
    '2263043737': 'broken upstream: it patches `Deconstructable.QueueDeconstruction`, which now has overloads, so Harmony throws ("Ambiguous match"), the game disables it and the game quits before the colony loads (spec tests/e2e/mods/2263043737-instant-sandbox.yaml)',
    '1706142008': 'broken upstream: its tile names `DUPLICANTSTATS.MOVEMENT` (the game\'s `MOVEMENT_MODIFIERS` now), so it fails in RegisterBuilding (TypeLoadException) and the game quits before the colony loads (spec tests/e2e/mods/1706142008-mosaic-tile.yaml)',
    '1706061612': 'broken upstream: its tile names `DUPLICANTSTATS.MOVEMENT` (the game\'s `MOVEMENT_MODIFIERS` now), so it fails in RegisterBuilding (TypeLoadException) and the game quits before the colony loads (spec tests/e2e/mods/1706061612-marble-tile.yaml)',
    '2554708805': 'broken upstream: it names the game\'s former `Worker` class (now `WorkerBase`), so its patch fails to load, the game disables it and the game quits before the colony loads (spec tests/e2e/mods/2554708805-choose-neural-vacillator.yaml)',
    '1729316811': 'broken upstream: it names the game\'s former `Worker` class (now `WorkerBase`), so its types fail to load, the game disables it and the game quits before the colony loads (spec tests/e2e/mods/1729316811-happy-digging.yaml)',
    '1706080287': 'broken upstream: it patches `SimpleInfoScreen.SetPanels`, which the game no longer has, so Harmony throws ("Undefined target method"), the game disables it and the game quits before the colony loads (spec tests/e2e/mods/1706080287-show-industrial-machinery-tag.yaml)',
    '3636360749': 'broken upstream: its Localization.Initialize postfix reads `RoomConstraints.REFRIGERATOR`, a field the game no longer has (MissingFieldException at start-up), and the game quits before the colony loads (spec tests/e2e/mods/3636360749-auto-connect-power.yaml)',
    '2047364442': 'broken upstream: it names the game\'s former `Worker` class (now `WorkerBase`), so its patches fail to load, the game disables it and the game quits before the colony loads (spec tests/e2e/mods/2047364442-clean-drop.yaml)',
    '2692663069': 'broken upstream: it patches `EntityTemplates.CreatePlacedEntity`, which now has overloads, so Harmony throws ("Ambiguous match"), the game disables it and the game quits before the colony loads (spec tests/e2e/mods/2692663069-mod-translation.yaml)',
    '2073611052': 'broken upstream: its `WorldGen.RenderOffline` patch fails (HarmonyException), the game disables it and the game quits before the colony loads, on the base-game fixture as it supports the base game only (spec tests/e2e/mods/2073611052-minibase.yaml)',
    '2797484988': 'broken upstream: it names the game\'s former `Worker` class (now `WorkerBase`), so its types fail to load, the game disables it and the game quits before the colony loads (spec tests/e2e/mods/2797484988-portal-cube.yaml)',
    '1751123054': 'broken upstream: its GravityComponents.FixedUpdate prefix reads `GameTags.Minion`, a field the game no longer has, so it throws a MissingFieldException on every tick and the game does not advance (spec tests/e2e/mods/1751123054-floatation.yaml)',
    '3119833849': 'broken upstream: its two Harmony patches are attributes on methods of classes without a class-level `[HarmonyPatch]`, which `PatchAll` skips (one also names `CrealtBuildingDef`, a method the config lacks), so the Oxygen Diffuser keeps the game\'s 0.5 kg/s (spec tests/e2e/mods/3119833849-super-oxygen-diffuser.yaml)',
    '1870621007': 'broken upstream: the Trapdoor Ladder\'s OnSpawn calls `SimMessages.SetCellProperties(int, byte)`, which the game no longer has (MissingMethodException), so its cell is a ladder but no door or fake floor (spec tests/e2e/mods/1870621007-trapdoors-floor-hatches.yaml)',
})
# Translations whose file is named Strings.po: on Linux both players look for strings.po and miss
# it (their specs fail the same way in the original).
broken.update({
    '928441385': 'broken on Linux (a case-sensitive file system): its translation is Strings.po, the game opens strings.po, misses it (GetLanguageFile: ... missing, then a NullReferenceException in `LanguageOptionsScreen.GetLanguageFilename` opens the error dialog), keeps English, and loading a colony quits the game; Windows finds the file (spec tests/e2e/mods/928441385-korean-language.yaml)',
    '3669382915': 'broken on Linux (a case-sensitive file system): its translation is Strings.po, the game opens strings.po, misses it (GetLanguageFile: ... missing, then a NullReferenceException in `LanguageOptionsScreen.GetLanguageFilename` opens the error dialog), keeps English, and loading a colony quits the game; Windows finds the file (spec tests/e2e/mods/3669382915-thai-mary.yaml)',
})
# Found by the e2e specs of ranks 1216 to 1300: the spec fails the same way in the original.
broken.update({
    '3608466042': 'broken upstream: its door\'s OnSpawn and Sim200ms call `SimMessages.SetCellProperties(int, byte)`, which the game no longer has (MissingMethodException), so a built Insulated Pressure Door opens the crash screen (spec tests/e2e/mods/3608466042-insulated-pressure-door.yaml)',
})
# Found by the e2e specs of ranks 1556 to 1640: the spec fails the same way in the original.
broken.update({
    '3038061222': 'broken upstream: its Mars start world guarantees eight oil pocket templates that worldgen cannot place ("Guaranteed placement failure on expansion1::worlds/Mars"), so a new game of the Mars cluster fails to generate (seeds 12345 and 1); spec tests/e2e/mods/3038061222-mars.yaml',
})
# Found by the e2e specs of ranks 1966 to 2063: the spec fails the same way in the original.
broken.update({
    '2996735249': 'broken upstream: its side screen is never added (it looks for DetailsScreen.sideScreenContentBody, which the game no longer has), so the sign\'s picture cannot be chosen (spec tests/e2e/mods/2996735249-meow-meow-egg.yaml)',
    '2936219269': 'broken upstream: its side screen is never added (it looks for DetailsScreen.sideScreenContentBody, which the game no longer has), so the wallpaper\'s picture cannot be chosen (spec tests/e2e/mods/2936219269-customize-your-wallpaper.yaml)',
    '2981889053': 'broken upstream: its side screen is never added (it looks for DetailsScreen.sideScreenContentBody, which the game no longer has), so the weapon\'s picture cannot be chosen (spec tests/e2e/mods/2981889053-xia-weapons.yaml)',
})
# Found by the e2e specs of ranks 1884 to 1965: the spec fails the same way in the original.
broken.update({
    '3590596912': 'broken upstream: it calls `PUtil.InitLibrary` but ships no PLib, so alone it fails to load (FileNotFoundException for PLib 4.8.1.0) and the game exits while loading; the load sweep passed only because another mod of its batch shipped PLib (spec tests/e2e/mods/3590596912-vine-branch-accelerate-growth.yaml)',
    '2865091697': 'broken upstream: its sign\'s design side screen (a clone of the Monument\'s, added to the details screen\'s side screen list) never shows: a built sign selected says no configuration is available, so its six designs cannot be picked (spec tests/e2e/mods/2865091697-warning-signs.yaml)',
    '2844428595': 'broken upstream: its prefix on `CameraController.OnPrefabInit` sets the keyboard panning speed to 0.25, which the method then overwrites from the CameraSpeed pref (`SetSpeedFromPrefs`), so the speed stays 1 (spec tests/e2e/mods/2844428595-camera-scroll-pan-speed.yaml)',
})
# Broken in the original only: ours happens to load it, so its note says how the original fails
# and why ours does not (no "same in the original").
broken.update({
    '1729794261': 'broken upstream: its light class `PixelPack2` lacks three methods the game\'s interfaces now have (`ISliderControl.GetSliderTooltip(int)`, `ISidescreenButtonControl.SetButtonTextOverride` and `HorizontalGroupID`), so the original fails its Color Ceiling Light in RegisterBuilding and the game\'s prefab setup in `Assets.OnPrefabInit` ("VTable setup of type OniLight.PixelPack2 failed"), and the error dialog opens as the game starts; ours happens to load it: Mono checks a class\'s interface methods when it sets up the class\'s vtable at first use, where ours gives the class the methods it lacks, which throw only when called (ARCHITECTURE D22)',
})
loads_in_ours = {'1729794261'}
# Found on U60 (build 761015), in the original only: its colony does not load there, so ours did
# not run it.
broken.update({
    '3823893994': 'broken on U60 (build 761015), version 1.0.1: its Temperature overlay patch reads the field `TemperatureOverlayInfraredVisualizerBase.updateCBHandle`, which U60 no longer has; the TypeInitializationException in Game.OnPrefabInit leaves Immigration and GlobalChoreProvider null, and the original exits (1) while loading a colony; not run in ours',
    '3771895170': 'broken on U60 (build 761015): mastering every skill on load (MinionResume.MasterSkill in its OnSpawn postfix) throws a NullReferenceException in a SkillAttributePerk, and loading a colony stops at the crash screen (spec tests/e2e/mods/3771895170-stronger-duplicants.yaml); not run in ours',
})
not_run_in_ours = {'3823893994', '3771895170'}
# Found on U60 in ranks 2501-2775, in the original only.
broken.update({
    '1353979712': 'broken upstream: its translation is sv_SE.po (with two .po files for the Korean and Chinese fonts) and no strings.po, which the game reads for a translation mod; picked as the language, the original logs "strings.po missing for mod 1353979712", throws a NullReferenceException in LanguageOptionsScreen.GetLanguageFilename and exits (1) while loading a colony; not run in ours',
    '2590563970': 'broken upstream: its `VariablePowerTransformer` implements `GetSliderTooltip()`, not the `ISliderControl.GetSliderTooltip(int)` the game\'s interface now has, so the original fails its prefab setup in `Assets.OnPrefabInit` ("VTable setup of type VariablePowerTransformer failed"), the colony\'s load then throws NullReferenceExceptions, and the player aborts (-6); not run in ours',
})
not_run_in_ours |= {'1353979712', '2590563970'}
# Not installed: the user's rule (2026-10-31) plays only uploads onimaxxing has reviewed, as the
# interim hashcheck.py finds them (id -> its reason); revisited once onimaxxing reviews them.
skipped = {
    '3827209375': 'upload analysed by onimaxxing (version 1.2.0) but not reviewed',
    '3822615860': 'upload not analysed by onimaxxing (it has analysed a newer one)',
    '3713252151': 'upload analysed by onimaxxing but not reviewed',
    '2965290662': 'it needs 2679326313, which onimaxxing has not analysed',
}
# Titles of played mods the catalog TSV does not rank (none since it reaches rank 2274).
unranked = {}
# Worldgen mods no sweep ran, as their specs' runs of ours found them (id -> (loads, colony)): a
# spec that gets to its new game loaded a colony first; Single Asteroid Clusters' worlds log
# errors while the game loads them, which open the error dialog at the main menu in ours, and
# loading a colony then quits.
worldgen = {'3743010929': ('✅', '✅'), '3382061617': ('✅', '✅'), '2919833683': ('❌', '❌')}
# Third hundred: filled in from that sweep's results (id -> (loads, colony, note)).
third_results = {}
for extra in glob.glob(os.path.join(SWEEPS, 'modtable-*.tsv')):
    for line in open(extra, encoding='utf-8'):
        i, loads, colony, note = (line.rstrip('\n').split('\t') + [''] * 4)[:4]
        third_results[i] = (loads, colony, note)

Y, N, X, W = '✅', '⬜', '❌', '⚠️'
# A mod onimaxxing's analysis finds broken in the game itself is not run here (no sweep, no
# spec): its row says so, from the catalog TSV's flags.
ONIMAXXING_BROKEN = {
    'notLoading': 'does not load in vanilla (onimaxxing analysis, not run here)',
    'crashesAtStartup': 'crashes vanilla at startup (onimaxxing analysis, not run here)',
}

def row(rank, mid, title, loads, colony, play, note):
    link = f'[{title.replace("|", "/")}](https://steamcommunity.com/sharedfiles/filedetails/?id={mid})' if mid else title
    return f'| {rank} | {link} | {loads} | {colony} | {play} | {note} |'

# Mods not played because onimaxxing has not reviewed the upload we have (or flags it): id, reason,
# from the interim hash check (scripts/mod-skipped-u60.tsv). Revisit them when it does.
SKIPPED = {}
_skipped = os.path.join(ROOT, 'scripts', 'mod-skipped-u60.tsv')
if os.path.exists(_skipped):
    for _line in open(_skipped, encoding='utf-8'):
        if _line.strip() and not _line.startswith('#'):
            _mid, _, _why = _line.rstrip('\n').partition('\t')
            SKIPPED[_mid] = _why


def status(mid, flags):
    """(Loads, Colony, Played, note) of a mod from the sweeps and the hand-written sets."""
    if mid in SKIPPED and mid not in results:
        return N, N, N, f'skipped: {SKIPPED[mid]}'
    if mid in played:
        return Y, Y, Y, played[mid]
    if mid in differences:
        return Y, Y, W, differences[mid]
    if mid in unplayed:
        return Y, Y, N, unplayed[mid]
    if mid in not_playable:
        return Y, Y, N, not_playable[mid]
    if mid in broken:
        return X, X, N, broken[mid] + ('' if mid in loads_in_ours or mid in not_run_in_ours else ' (same in the original)')
    if mid in skipped:
        return N, N, N, f'skipped: not reviewed by onimaxxing in this upload yet ({skipped[mid]})'
    if mid in SWEPT:
        return sweep_status(*SWEPT[mid])
    if flags == 'unanalysed':
        return N, N, N, 'not analysed by onimaxxing (no malware check); not tested'
    if flags in ONIMAXXING_BROKEN:
        return X, X, N, ONIMAXXING_BROKEN[flags]
    if flags:
        return N, N, N, f'flagged by onimaxxing ({flags}); not tested'
    if mid in worldgen:
        return (*worldgen[mid], N, 'worldgen mod: spec pending (functional expectations)')
    if mid in fifth_failing:
        return X, X, N, 'fails to load in the original as well, in its sweep batch'
    if mid in third_results:
        loads, colony, note = third_results[mid]
        return loads, colony, N, note
    if mid in fifth or mid in swept:
        return Y, Y, N, ''
    return N, N, N, ''


def with_record(mid, flags):
    """A mod's status, its Played column and note from its spec record when it has one: a played
    or ⚠️ spec ran it in a colony, so Loads and Colony are ✅."""
    loads, colony, play, note = status(mid, flags)
    if mid not in results:
        return loads, colony, play, note
    play, note = record_status(results[mid], FINDINGS)
    return (Y, Y, play, note) if play != N else (loads, colony, play, note)


rows = [row('–', None, 'Fast Track (GitHub)', Y, Y, Y, 'about 30 fps on U57, where the original runs 5–6')]
listed = set()
for r in cat:
    rank, mid, _, flags, title = int(r[0]), r[1], r[2], r[3], r[4]
    if rank > LAST_RANK and mid not in U60_IDS and mid not in played and mid not in results: continue
    listed.add(mid)
    rows.append(row(rank, mid, title, *with_record(mid, flags)))
for mid in list(played) + [m for m in results if m not in played]:
    if mid not in listed and mid in by_id:
        r = by_id[mid]
        rows.append(row(r[0], mid, r[4], *with_record(mid, r[3])))
    elif mid not in listed:
        rows.append(row('–', mid, unranked.get(mid, mid), *with_record(mid, '')))

print(f'''# Mod compatibility

Which Workshop mods have been tried in this player, by popularity in
[onimaxxing's catalog](https://onimaxxing.com/mods): the {FULL_RANKS} most-subscribed, then down to
rank {LAST_RANK} those the catalog flags neither as broken (not loading, crashing at startup) nor
as malware, the 2000 that work in the original game by the catalog's analysis; and mods players
reported. The rest of the U60 catalog (to rank 3047) is listed with its U60 ranks, tried on U60
(build 761015), less the mods it flags as broken by that update or as malware.
Worldgen mods are played with functional expectations (their worlds generate with their content),
not identical layouts.

**Columns:**
- **Loads:** reaches the main menu with no error of ours.
- **Colony:** loaded with its batch of about 25 mods and played 90 seconds of a Spaced Out colony,
  with no error of ours: U57 (late game) for ranks 1–500, 2minnow (mid game) from 501.
- **Played:** a save played with the mod, its screens and logs compared with the original player.
  ⚠️ means played with a small known difference, its issue linked.

"No error of ours" means no error that the original player's log lacks. ❌ means the mod fails the
same way in the original player (it needs an update from its author). Every run uses the
Workshop build of each mod, installed as a local mod (the closest to a subscription; Dev-folder
installs get developer checks subscribers never see).

| Rank | Mod | Loads | Colony | Played | Notes |
|---|---|---|---|---|---|
''' + '\n'.join(rows) + '''

## Known differences

- **Particle features no game data uses** (collision, lights, noise, clamp velocity, sub
  emitters, some shapes) are not simulated, and are logged; mods' effects (Capsule 2.0) look
  simpler.
- **LineRenderer:** rounded corners and caps are drawn square.
- **A mod class missing a method of a game interface** (built for an older game) loads, as on
  Mono; where Mono fails as soon as the game checks the class against the interface, ours fails
  only when the missing method is called (ARCHITECTURE D22).
- **A game class's nested type a mod names that the game no longer has.** Mono binds it to the
  class's last nested type when something listed that class's nested types before (its log says
  "TypeRef ResolutionScope not yet handled") and fails it otherwise. The game lists those of its
  strings (STRINGS) before it loads mods, and ours binds a missing one there as Mono does
  (ARCHITECTURE D32: U&F-Translate Fix); elsewhere ours fails it, where Mono binds it only after
  something (a mod's reflection, say) listed the class's nested types.
- **Transpilers on Unity's UI code** (uGUI, TextMeshPro) are not supported: they need the exact
  IL of Unity's builds, which ours does not have. Ordinary Harmony patches on those classes
  work. Text Input Hotkeys transpiles TMP_InputField.Backspace.
- **Out-of-range NativeArray access:** a write is dropped and a read gives the default value,
  and copies and sub-arrays keep to the part within the arrays, where Unity's release player
  reads or writes the memory before or past them (ARCHITECTURE D46). Affected: Zoned Auto
  Sweeper.''')
