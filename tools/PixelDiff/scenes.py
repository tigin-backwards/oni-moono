#!/usr/bin/env python3
"""The Direct3D 11 scene set against OpenGL (docs/research/d3d11-plan.md, M3; done criterion 3).

    scenes.py --opengl RUN_A --opengl RUN_C --d3d11 RUN_B [--vulkan RUN_V] [--json OUT.json] [--diffs DIR]

Each RUN is an e2e run id of scripts/e2e-windows/run.py (out/e2e/SPEC/RUN/ours-1/) that ran the
d3d11-scenes specs with `--renderer opengl` (two runs) or `--renderer d3d11` (one run). For each
shot of each spec (every PNG the D3D11 run has, but end.png, which is taken after the spec's
last step at no set frame) it prints:
- spread: the two OpenGL runs against each other (what differs between runs whatever the
  renderer);
- d3d11: OpenGL run A against D3D11, every pixel;
- stable: the same, leaving out the pixels where the two OpenGL runs differ and those within
  GROW pixels of them (pixel_diff.py --stable --grow);
and the verdict against TOLERANCE (below), which the stable comparison must meet.

--vulkan: a `--renderer vulkan` run of the same specs (Unity's Vulkan programs, drawn
unmodified). A scene over the tolerance against OpenGL is compared with it too, the pixels the
two OpenGL runs make unstable left out, and the result printed: whether D3D11 is within the >40
tolerance of Vulkan. That does not make the difference OpenGL's: the original measured on Windows
(j0008657) agreed with OpenGL, not with Vulkan and D3D11, in the mid-game colony's materials,
conveyor and power overlays (d3d11-plan.md, M3 part B). Every scene over the tolerance counts as
over.

Masks: the rects a shot's spec masks (NAME.shot.json beside the D3D11 run's shot: content that
moves in real time while the game is paused, such as a Duplicant's portrait) are left out of
every comparison of that shot, and the share compared is of the pixels they leave.

TOLERANCE, per scene (chosen from the OpenGL spread and the D3D11 results of M3's runs; the
plan's M3 section has the numbers): at most 0.5 % of the stable pixels may differ by more than
16 in a channel, and at most 0.1 % by more than 40. A scene whose stable pixels are under half
the image (too much moves between runs to judge) fails as unjudged."""

import argparse
import glob
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixel_diff import compare  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# Scenes judged against the Vulkan run instead of OpenGL: the original's own D3D11 and GLCore
# renderers differ there, and ours on Vulkan equals the original's D3D11 (measured). Klei/
# NorthernLights draws a quad already in clip space, with no projection, so a renderer whose
# render textures run top row first draws it upside down against GL, the original too (the
# original's D3D11 against ours on Vulkan 0.000% over 40, its GLCore against ours on OpenGL
# 0.001%, j0008881; d3d11-plan.md M3 part C).
VULKAN_REFERENCE = {'d3d11-coverage-sky/sky'}

SPECS = ('d3d11-scenes', 'd3d11-scenes-base', 'd3d11-scenes-mid', 'd3d11-coverage', 'd3d11-coverage-particles', 'd3d11-coverage-debug',
         'd3d11-coverage-rockets', 'd3d11-coverage-impactor', 'd3d11-coverage-sky')
TOLERANCE = {'16': 0.005, '40': 0.001}
MIN_STABLE = 0.5
GROW = 3


def shots(spec, run):
    folder = os.path.join(ROOT, 'out', 'e2e', spec, run, 'ours-1')
    return folder, {os.path.basename(p)[:-4] for p in glob.glob(os.path.join(folder, '*.png'))} - {'end'}


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
        for name in sorted(b):
            scene = f'{spec}/{name}'
            if name not in a or name not in c:
                rows.append({'scene': scene, 'verdict': 'missing in an OpenGL run'})
                print(f'{scene:40} missing in an OpenGL run')
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
            # The share compared, of the pixels the spec does not mask.
            spec_masked = compare(ra, ra, masks=masks)[0]['masked'] if masks else 0
            share = stable['pixels'] / (stable['pixels'] + stable['masked'] - spec_masked)
            f = stable['fraction_over']
            if scene in VULKAN_REFERENCE:
                if name not in v:
                    verdict = 'needs --vulkan (judged against Vulkan)'
                else:
                    rv = os.path.join(v_dir, name + '.png')
                    vulkan, _ = compare(rv, rb, masks=masks, stable_pairs=[(ra, rc)], grow_by=GROW)
                    row['vulkan'] = vulkan
                    fv = vulkan['fraction_over']
                    over = [k for k, limit in TOLERANCE.items() if fv[k] > limit]
                    verdict = ('ok' if not over else 'over at ' + ', '.join('>' + k for k in over)) + f' against Vulkan (>40 {fv["40"]:.3%})'
                    if not over:
                        verdict = 'ok'
                        f = fv
                        print(f'{scene:40} judged against Vulkan: >16 {fv["16"]:.3%}, >40 {fv["40"]:.3%}')
            elif share < MIN_STABLE:
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
