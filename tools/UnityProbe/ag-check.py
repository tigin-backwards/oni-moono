#!/usr/bin/env python3
"""Compares two players' API gaps probe lines (tools/UnityProbe/ApiGaps.cs, "PROBE ... AG ...").
  ag-check.py ORIGINAL.log OURS.log [PART...]
Prints, per part, the lines that differ once what differs between runs is masked: frame numbers,
shader property ids (idOk says whether each matches PropertyToID), the loopback server's port,
temporary file names and the player's install path."""
import difflib, re, struct, sys


def shortest(number):
    f = struct.unpack('f', struct.pack('f', float(number)))[0]
    for digits in range(1, 10):
        text = f'{f:.{digits}g}'
        if struct.unpack('f', struct.pack('f', float(text)))[0] == f:
            return text
    return number


def lines(path, parts):
    out = {}
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.search(r'PROBE f\d+ AG (\S+) (.*)$', line.rstrip('\n'))
        if not m:
            continue
        part, text = m.groups()
        if part == 'log':
            part = last
        else:
            last = part
        if parts and part not in parts:
            continue
        text = re.sub(r' id=\d+', ' id=#', text)
        text = re.sub(r'127\.0\.0\.1:\d+', '127.0.0.1:PORT', text)
        text = re.sub(r'apigaps-(dl|dir)-[0-9a-f]{32}', r'apigaps-\1-X', text)
        text = re.sub(r'/\S*/OxygenNotIncluded_Data/', '<data>/', text)
        # Mono's and CoreCLR's formats of an argument exception's parameter name.
        text = re.sub(r"\\nParameter name: (\w+)", r" (Parameter '\1')", text)
        text = text.replace('instance of an object.', 'instance of an object')
        # Mono's "R" gives 9 digits where CoreCLR gives the shortest round trip: the same float.
        text = re.sub(r'-?\d+\.\d{6,}(?:E[-+]?\d+)?', lambda m: shortest(m.group(0)), text)
        out.setdefault(part, []).append(text)
    return out


last = ''
orig, ours = lines(sys.argv[1], set(sys.argv[3:])), lines(sys.argv[2], set(sys.argv[3:]))


def shader_blocks(texts):
    blocks, name = {}, None
    for t in texts:
        m = re.match(r"shader '(.*)' count=", t)
        if m:
            name = m.group(1)
            blocks.setdefault(name, [])
        if name is not None:
            blocks[name].append(t)
    return blocks


# The players load different sets of shaders by the main menu: compare the ones both have.
if 'shader' in orig and 'shader' in ours:
    a, b = shader_blocks(orig['shader']), shader_blocks(ours['shader'])
    common = [n for n in a if n in b]
    print(f'shaders: {len(a)} in the original, {len(b)} in ours, {len(common)} in both')
    orig['shader'] = [t for n in common for t in a[n]]
    ours['shader'] = [t for n in common for t in b[n]]
same = True
for part in list(dict.fromkeys(list(orig) + list(ours))):
    a, b = orig.get(part, []), ours.get(part, [])
    diff = [d for d in difflib.unified_diff(a, b, 'original', 'ours', n=0, lineterm='') if not d.startswith(('---', '+++'))]
    print(f'== {part}: {len(a)} lines, {len(b)} in ours, {"same" if not diff else str(sum(1 for d in diff if d[:1] in "+-")) + " differ"}')
    for d in diff:
        print('  ' + d[:400])
    same &= not diff
sys.exit(0 if same else 1)
