#!/usr/bin/env python3
"""The Direct3D 11 scene set against OpenGL (docs/research/d3d11-plan.md, M3; done criterion 3).

    scenes.py --opengl RUN_A --opengl RUN_C --d3d11 RUN_B [--vulkan RUN_V] [--original RUN_O] [--json OUT.json] [--diffs DIR]

Each RUN is an e2e run id of scripts/e2e-windows/run.py (out/e2e/SPEC/RUN/ours-1/) that ran the
d3d11-scenes specs with `--renderer opengl` (two runs) or `--renderer d3d11` (one run). The shots
are the ones each spec names (its `shot`, `world_shot` and `file_shot` steps; not end.png, taken
after the last step at no set frame): a spec folder or a shot missing from any run counts as a
failure, so a run that lost a scene cannot pass with fewer scenes. For each shot it prints:
- spread: the two OpenGL runs against each other (what differs between runs whatever the
  renderer);
- d3d11: OpenGL run A against D3D11, every pixel;
- stable: the same, leaving out the pixels where the two OpenGL runs differ and those within
  GROW pixels of them (pixel_diff.py --stable --grow);
and the verdict against TOLERANCE (below), which the stable comparison must meet.

--original: an `--player original` run (the original's Direct3D 11) of the specs that have
scenes judged against it (REFERENCE).

--vulkan: a `--renderer vulkan` run of the same specs (Unity's Vulkan programs, drawn
unmodified). A scene over the tolerance against OpenGL is compared with it too, the pixels the
two OpenGL runs make unstable left out, and the result printed: whether D3D11 is within the >40
tolerance of Vulkan. That does not make the difference OpenGL's: the original measured on Windows
(j0008657) agreed with OpenGL, not with Vulkan and D3D11, in the mid-game colony's materials,
conveyor and power overlays (d3d11-plan.md, M3 part B). Every scene over the tolerance counts as
over.

Masks: the rects a shot's spec masks (NAME.shot.json beside the D3D11 run's shot: content that
moves in real time while the game is paused, such as a Duplicant's portrait) are left out of
every comparison of that shot. The share compared is of the whole image: what the spec masks
counts against it as what moves between runs does.

TOLERANCE, per scene (chosen from the OpenGL spread and the D3D11 results of M3's runs; the
plan's M3 section has the numbers): at most 0.5 % of the stable pixels may differ by more than
16 in a channel, and at most 0.1 % by more than 40 (at 1280x720, with 92 % compared, about 850
pixels). A scene whose stable pixels are under half the image (too much moves between runs to
judge) fails as unjudged, but for the scenes in SMALLER_SHARE, whose claim is only the part
named there."""

import argparse
import glob
import json
import os
import sys

import yaml

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixel_diff import compare  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# Scenes judged against the Vulkan run instead of OpenGL: the original's own D3D11 and GLCore
# renderers differ there, and ours on Vulkan equals the original's D3D11 (measured). Klei/
# NorthernLights draws a quad already in clip space, with no projection, so a renderer whose
# render textures run top row first draws it upside down against GL, the original too (the
# original's D3D11 against ours on Vulkan 0.000% over 40, its GLCore against ours on OpenGL
# 0.001%, j0008881; d3d11-plan.md M3 part C).
# The user's decision (2026-11-23, "Yeah can check Vulkan", on the northern lights' mirroring).
# Scenes judged against the original itself (`--original`, its Direct3D 11): the navigation grid's
# GL lines are two rows thick in the original's D3D11 and in ours, one row in ours on OpenGL and
# on Vulkan (d3d11-plan.md, M3 review fixes, finding 5).
REFERENCE = {'d3d11-coverage-sky/sky': 'vulkan', 'd3d11-coverage-debug/navgrid': 'original'}
# Scenes judged on less than half the image, and what they claim: the research tree moves in real
# time while the screen is open (no entry held still for 30 frames within 1,800, j0008715-
# j0008717), so its spec masks it and the scene judges the side bar alone (29 % of the image).
SMALLER_SHARE = {'d3d11-scenes-base/research': (0.2, 'the side bar')}

SPECS = (
    'd3d11-scenes',
    'd3d11-scenes-base',
    'd3d11-scenes-mid',
    'd3d11-coverage',
    'd3d11-coverage-particles',
    'd3d11-coverage-debug',
    'd3d11-coverage-rockets',
    'd3d11-coverage-impactor',
    'd3d11-coverage-sky',
    'd3d11-timelapse-freeze',
    'd3d11-coverage-gamepad',
)
TOLERANCE = {'16': 0.005, '40': 0.001}
MIN_STABLE = 0.5
GROW = 3


def shots(spec, run, player='ours'):
    folder = os.path.join(ROOT, 'out', 'e2e', spec, run, f'{player}-1')
    return folder, {os.path.basename(p)[:-4] for p in glob.glob(os.path.join(folder, '*.png'))} - {'end'}


def spec_shots(spec):
    """The shots the spec names, in its order (its shot, world_shot and file_shot steps)."""
    with open(os.path.join(ROOT, 'tests', 'e2e', spec + '.yaml')) as f:
        steps = (yaml.safe_load(f) or {}).get('steps') or []
    names = []
    for step in steps:
        if isinstance(step, dict):
            for kind, value in step.items():
                if kind in ('shot', 'world_shot', 'file_shot') and isinstance(value, dict) and value.get('name'):
                    names.append(value['name'])
    return names


def shot_masks(folder, name):
    """The shot's mask rects as its spec gave them (whole pixels)."""
    path = os.path.join(folder, name + '.shot.json')
    if not os.path.exists(path):
        return []
    with open(path) as f:
        return [[int(round(v)) for v in rect] for rect in json.load(f).get('masks', [])]


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument('--opengl', action='append', required=True)
    p.add_argument('--d3d11', required=True)
    p.add_argument('--vulkan')
    p.add_argument('--original')
    p.add_argument('--json')
    p.add_argument('--diffs')
    args = p.parse_args()
    if len(args.opengl) != 2:
        p.error('two --opengl runs')
    rows, failed, explained = [], 0, 0
    print(f'{"scene":40} {"spread>8":>9} {"d3d>8":>8} | {"stable":>7} {">2":>7} {">8":>7} {">16":>7} {">40":>7} {"PSNR":>6}  verdict')
    for spec in SPECS:
        a_dir, a = shots(spec, args.opengl[0])
        c_dir, c = shots(spec, args.opengl[1])
        b_dir, b = shots(spec, args.d3d11)
        v_dir, v = shots(spec, args.vulkan) if args.vulkan else (None, set())
        others = {'vulkan': (v_dir, v), 'original': shots(spec, args.original, 'original') if args.original else (None, set())}
        expected = spec_shots(spec)
        for name in sorted((set(expected) | a | b | c) - {'end'}):
            scene = f'{spec}/{name}'
            lacking = [role for role, have in (('OpenGL A', a), ('OpenGL C', c), ('D3D11', b)) if name not in have]
            if name not in expected:
                lacking.append('the spec')
            if lacking:
                rows.append({'scene': scene, 'verdict': 'missing in ' + ', '.join(lacking)})
                print(f'{scene:40} missing in ' + ', '.join(lacking))
                failed += 1
                continue
            ra, rb, rc = (os.path.join(d, name + '.png') for d in (a_dir, b_dir, c_dir))
            masks = shot_masks(b_dir, name)
            spread, _ = compare(ra, rc, masks=masks)
            whole, _ = compare(ra, rb, masks=masks)
            stable, image = compare(ra, rb, masks=masks, stable=[rc], grow_by=GROW)
            row = {'scene': scene, 'spread': spread, 'd3d11': whole, 'stable': stable}
            if 'note' in stable:
                row['verdict'] = stable['note']
                failed += 1
                print(f'{scene:40} {stable["note"]}')
                rows.append(row)
                continue
            if image is not None and args.diffs:
                os.makedirs(args.diffs, exist_ok=True)
                from PIL import Image

                Image.fromarray(image).save(os.path.join(args.diffs, f'{spec}-{name}.png'))
            # The share compared, of the whole image (the spec's masks count against it).
            share = stable['pixels'] / (stable['pixels'] + stable['masked'])
            min_share, part = SMALLER_SHARE.get(scene, (MIN_STABLE, None))
            f = stable['fraction_over']
            if scene in REFERENCE:
                ref = REFERENCE[scene]
                o_dir, o = others[ref]
                other = None
                if name not in o:
                    verdict = f'needs --{ref} (judged against it)'
                else:
                    ro = os.path.join(o_dir, name + '.png')
                    other, _ = compare(ro, rb, masks=masks, stable_pairs=[(ra, rc)], grow_by=GROW)
                    row[ref] = other
                if other is None:
                    pass
                elif 'note' in other:
                    verdict = f'against {ref}: {other["note"]}'
                elif (oshare := other['pixels'] / (other['pixels'] + other['masked'])) < min_share:
                    verdict = f'unjudged ({oshare:.0%} compared against {ref})'
                else:
                    share = oshare
                    fo = other['fraction_over']
                    over = [k for k, limit in TOLERANCE.items() if fo[k] > limit]
                    verdict = 'ok' if not over else 'over at ' + ', '.join('>' + k for k in over) + f' against {ref} (>40 {fo["40"]:.3%})'
                    f = fo
                    print(f'{scene:40} judged against {ref}: >16 {fo["16"]:.3%}, >40 {fo["40"]:.3%}')
            elif share < min_share:
                verdict = f'unjudged ({share:.0%} stable)'
            else:
                over = [k for k, limit in TOLERANCE.items() if f[k] > limit]
                verdict = 'ok' if not over else 'over at ' + ', '.join('>' + k for k in over)
                if over and name in v:
                    rv = os.path.join(v_dir, name + '.png')
                    vulkan, _ = compare(rv, rb, masks=masks, stable_pairs=[(ra, rc)], grow_by=GROW)
                    against, _ = compare(rv, ra, masks=masks, stable_pairs=[(ra, rc)], grow_by=GROW)
                    row['vulkan'], row['opengl_vulkan'] = vulkan, against
                    fv, fo = vulkan.get('fraction_over', {}), against.get('fraction_over', {})
                    if fv and fo:
                        verdict += f'; >40 D3D11-Vulkan {fv["40"]:.3%}, OpenGL-Vulkan {fo["40"]:.3%}'
                        if fv['40'] <= TOLERANCE['40'] < fo['40']:
                            explained += 1
            failed += verdict != 'ok'
            if part and verdict == 'ok':
                print(f'{scene:40} judged on {part} alone ({share:.0%} of the image)')
            row['verdict'] = verdict
            row['stable_share'] = round(share, 4)
            rows.append(row)
            psnr = stable['psnr_rgb']
            print(
                f'{scene:40} {spread["fraction_over"]["8"]:9.3%} {whole["fraction_over"]["8"]:8.3%} | {share:7.1%} '
                f'{f["2"]:7.3%} {f["8"]:7.3%} {f["16"]:7.3%} {f["40"]:7.3%} {psnr if psnr is not None else "inf":>6}  {verdict}'
            )
    if args.json:
        with open(args.json, 'w') as out:
            json.dump({'tolerance': TOLERANCE, 'min_stable': MIN_STABLE, 'grow': GROW, 'scenes': rows}, out, indent=1)
    print(
        f'{len(rows) - failed} of {len(rows)} scenes within the tolerance; {failed} over, {explained} of them where D3D11 and Vulkan agree and OpenGL differs'
    )
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
