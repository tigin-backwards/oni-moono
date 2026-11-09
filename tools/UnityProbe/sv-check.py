#!/usr/bin/env python3
"""The statics probe (StaticValues.cs, lines "PROBE f<frame> SV ..."):
  sv-check.py diff ORIG_LOG OURS_LOG   every value that differs, or is in one player only
  sv-check.py cs LOG                   the C# for Color's static properties, from a log
Exit code 0 when diff finds no difference."""

import re, struct, sys

LINE = re.compile(r'PROBE f\d+ SV (.*)$')


def values(path):
    """{key: value text} of a log's SV lines; prop/field lines are keyed by their name."""
    out = {}
    with open(path, errors='replace') as f:
        for line in f:
            m = LINE.search(line.rstrip('\n'))
            if not m:
                continue
            text = m.group(1)
            parts = text.split(' ', 2)
            if parts[0] in ('prop', 'field') and len(parts) == 3:
                out[parts[0] + ' ' + parts[1]] = parts[2]
            elif parts[0] == 'count':
                out[' '.join(parts[:2])] = text
            else:
                key, _, value = text.partition(' ')
                out[key] = value
    return out


def bits_only(table):
    """The "R" forms dropped: Mono's "R" prints 9 digits where .NET prints the shortest that
    round-trips (ARCHITECTURE D2); the bits decide."""
    return {k: re.sub(r'(\b[0-9A-F]{8})/\S+', r'\1', v) for k, v in table.items()}


def diff(orig_path, ours_path):
    orig, ours = bits_only(values(orig_path)), bits_only(values(ours_path))
    if not orig:
        print(f'no SV lines in {orig_path}')
        return 1
    bad = 0
    for key in sorted(set(orig) | set(ours)):
        a, b = orig.get(key), ours.get(key)
        if a == b:
            continue
        bad += 1
        print(f'{key}\n  original: {a}\n  ours:     {b}')
    print(f'{len(orig)} values in the original, {len(ours)} in ours, {bad} differ')
    return 1 if bad else 0


def literal(bits):
    """The shortest float literal C# reads back to these bits."""
    value = struct.unpack('<f', struct.pack('<I', int(bits, 16)))[0]
    for digits in range(1, 10):
        text = f'{value:.{digits}g}'
        if struct.unpack('<I', struct.pack('<f', float(text)))[0] == int(bits, 16):
            break
    return text + 'f'


def cs(path):
    for key, value in values(path).items():
        if not key.startswith('prop Color.'):
            continue
        fields = value.split()
        if fields[0] != 'Color':
            print(f'// {key}: {value}')
            continue
        bits = [c.split('/')[0] for c in fields[1:5]]
        name = key.split('.', 1)[1]
        print(f'        public static Color {name} => new Color({", ".join(literal(b) for b in bits)});')
    return 0


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        sys.exit(diff(sys.argv[2], sys.argv[3]))
    if len(sys.argv) == 3 and sys.argv[1] == 'cs':
        sys.exit(cs(sys.argv[2]))
    print(__doc__)
    sys.exit(2)
