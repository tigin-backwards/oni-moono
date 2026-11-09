#!/usr/bin/env python3
"""Compares two players' member-order files (MemberOrder.cs, PROBE_SCENARIO=memberorder).
  mo-check.py ORIG OURS               per call, the types listing the same members in the same
                                      order, the same members in another order, or other members
  mo-check.py ORIG OURS CALL [N]      and N examples of each for that call (default 5)
Members are Declaring.Name, so a reorder within a class and one across classes tell apart: "within"
counts the types where one class's own members come in another order."""

import collections, sys


def load(path):
    lists = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            parts = line.rstrip('\n').split('\t')
            if len(parts) == 3:
                lists[(parts[0], parts[1])] = parts[2]
    return lists


def by_class(text):
    groups = collections.defaultdict(list)
    for member in text.split('|'):
        groups[member.split('.')[0]].append(member)
    return groups


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    orig, ours = load(sys.argv[1]), load(sys.argv[2])
    only = sys.argv[3] if len(sys.argv) > 3 else None
    shown = int(sys.argv[4]) if len(sys.argv) > 4 else 5
    stats = collections.defaultdict(collections.Counter)
    examples = collections.defaultdict(list)
    for (type_name, call), a in orig.items():
        if only and call != only:
            continue
        b = ours.get((type_name, call))
        if b is None:
            kind = 'missing'
        elif a == b:
            kind = 'same'
        elif sorted(a.split('|')) != sorted(b.split('|')):
            kind = 'members'
        else:
            kind = 'order'
            stats[call]['within' if by_class(a) != by_class(b) else 'across'] += 1
        stats[call][kind] += 1
        if kind in ('order', 'members'):
            examples[kind].append((type_name, a, b))
    for call, counts in sorted(stats.items()):
        print(call, ' '.join(f'{k} {v}' for k, v in sorted(counts.items())))
    if only:
        for kind, found in examples.items():
            print(f'== {kind} ({len(found)})')
            for type_name, a, b in found[:shown]:
                print(' ', type_name)
                print('    orig', a[:600])
                print('    ours', b[:600])


if __name__ == '__main__':
    main()
