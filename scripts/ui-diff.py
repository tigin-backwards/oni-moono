#!/usr/bin/env python3
# Compares two --dump-ui files (PLAN M9) or E2eDriver ui.dump files: elements are matched by path,
# and each differing field is reported. Exit status 0 when the dumps agree.
#   scripts/ui-diff.py SHIPPED.txt OURS.txt [--tolerance PX] [--mask-text REGEX ...] [--ignore FIELD ...]
# --tolerance compares the geometry (an element's rect and world corners) within PX pixels, and its
# size within PX or 2 %, whichever is larger; an element that is 0-sized in the second dump only
# differs whatever the tolerance. --mask-text replaces each match of REGEX inside quoted strings
# (text fields) with '#' before comparing, for numbers or dates a spec names; a field whose text
# a mask changed has its textInfo counts masked too.
import os, re, sys

SIZE_FRACTION = 0.02
QUOTED = re.compile(r'"(?:[^"\\]|\\.)*"')
NUMBER = re.compile(r'-?\d+(?:\.\d+)?')


def split_components(line):
    """LINE split at its ' | ' separators, not those inside quoted text."""
    parts, start, at = [], 0, 0
    while True:
        sep = line.find(' | ', at)
        quote = QUOTED.search(line, at)
        if quote and (sep < 0 or quote.start() < sep):
            at = quote.end()
            continue
        if sep < 0:
            parts.append(line[start:])
            return parts
        parts.append(line[start:sep])
        start = at = sep + 3


def parse(path):
    elements = {}
    for line in open(path, encoding='utf-8'):
        line = line.rstrip('\n')
        if not line or line.startswith('  '):  # vertex listings (--dump-ui-vertices)
            continue
        head, *components = split_components(line)
        key, _, rest = head.partition('\t')
        fields = {'': rest}
        for c in components:
            name, _, values = c.partition(' ')
            fields[name] = values
        elements[key] = fields
    return elements


def mask(value, patterns):
    if value is None or not patterns:
        return value

    def masked(m):
        text = m.group(0)
        for p in patterns:
            text = re.sub(p, '#', text)
        return text

    out = QUOTED.sub(masked, value)
    if out != value:  # textInfo counts the text's characters, lines and words
        out = re.sub(r'\btextInfo=\S+', 'textInfo=#', out)
    return out


def geometry(text):
    """The rect and world fields of an element's geometry text, as {name: [numbers]}, and the
    text with the numbers taken out (what must match exactly)."""
    parts, shape = {}, NUMBER.sub('N', text or '')
    for name, values in re.findall(r'(\w+)[ =]((?:-?\d+(?:\.\d+)?,?)+)', text or ''):
        parts[name] = [float(v) for v in values.strip(',').split(',')]
    return parts, shape


def geometry_differs(a, b, tolerance):
    """Whether geometry texts A and B differ beyond TOLERANCE pixels (sizes: or 2 %)."""
    (pa, sa), (pb, sb) = geometry(a), geometry(b)
    if sa != sb or pa.keys() != pb.keys():
        return True
    for name in pa:
        va, vb = pa[name], pb[name]
        if len(va) != len(vb):
            return True
        for i, (x, y) in enumerate(zip(va, vb)):
            if name == 'rect' and len(va) == 4 and i >= 2:
                if (y == 0) != (x == 0):  # 0-sized in one only
                    return True
                if abs(x - y) > max(tolerance, SIZE_FRACTION * max(abs(x), abs(y))):
                    return True
            elif abs(x - y) > tolerance:
                return True
    return False


def diff(a, b, tolerance=0, mask_text=(), ignore=(), names=('first', 'second')):
    """The differences of parsed dumps A and B, as readable lines."""
    lines = []
    for key in sorted(a.keys() - b.keys()):
        lines.append(f'only in {names[0]}: {key}')
    for key in sorted(b.keys() - a.keys()):
        lines.append(f'only in {names[1]}: {key}')
    for key in sorted(a.keys() & b.keys()):
        fa, fb = a[key], b[key]
        for name in sorted(fa.keys() | fb.keys()):
            if name in ignore:
                continue
            va, vb = fa.get(name), fb.get(name)
            if name == '' and va is not None and vb is not None and tolerance:
                differs = geometry_differs(va, vb, tolerance)
            else:
                differs = mask(va, mask_text) != mask(vb, mask_text)
            if differs:
                lines.append(f'{key} [{name or "rect"}]\n  - {va}\n  + {vb}')
    return lines


def main(argv):
    args, ignore, masks, tolerance = [], set(), [], 0.0
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == '--ignore':
            ignore |= set(argv[i + 1 :])
            break
        elif a == '--tolerance':
            tolerance = float(argv[i + 1])
            i += 1
        elif a == '--mask-text':
            masks.append(argv[i + 1])
            i += 1
        else:
            args.append(a)
        i += 1
    if len(args) != 2:
        sys.exit('usage: ui-diff.py A B [--tolerance PX] [--mask-text REGEX ...] [--ignore FIELD ...]')
    for path in args:
        if not os.path.exists(path):
            print(f'missing dump: {path}')
            return 2
    a, b = parse(args[0]), parse(args[1])
    lines = diff(a, b, tolerance, masks, ignore, names=args)
    for line in lines:
        print(line)
    print(f'{len(a)} vs {len(b)} elements, {len(lines)} differences')
    return 1 if lines else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
