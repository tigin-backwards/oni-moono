#!/usr/bin/env python3
"""Checks the word-count probe's WC lines (WordCount.cs).

  wc-check.py diff ORIG OURS   the lines whose counts, words or breaks differ between two
                               players' logs
  wc-check.py rule LOG         every line's words and line word counts against the measured
                               rule (textmeshpro.md, "Words and their separators")

The rule is stateful: a text ending in an apostrophe reads the character slot after it, and a
text without words keeps the previous layout's first word record, so the lines are replayed in
order, per component (one per font).
"""

import re
import sys
import unicodedata

LINE = re.compile(r'PROBE f\d+ WC (\S) (NotoSans-Regular|GRAYSTROKE REGULAR SDF) "((?:[^"\\]|\\u[0-9A-F]{4})*)" (.*)$')


def unhex(s):
    return re.sub(r'\\u([0-9A-F]{4})', lambda m: chr(int(m.group(1), 16)), s)


def parse(path):
    """The WC lines in order: (kind, font, text, fields)."""
    out = []
    with open(path, encoding='utf-8', errors='replace') as f:
        for raw in f:
            m = LINE.search(raw.rstrip('\n'))
            if m:
                kind, font, text, rest = m.groups()
                out.append((kind, font, text, dict(re.findall(r'(\w+)=(\S*)', rest))))
    return out


def key(kind, font, text, f):
    return (kind, font, text, ('w=' + f['w']) if 'w' in f else ('preferred' if 'preferred' in f else ''))


# Word characters: letters and decimal digits (categories L* and Nd, per UTF-16 unit) and four
# hyphens. Apostrophes join between letters or digits. "Punctuation" is P*, except that § and ¶
# count as symbols.
HYPHENS = set('-­‐‑')
APOSTROPHES = set("'’")


def letter_or_digit(c):
    cat = unicodedata.category(c)
    return cat[0] == 'L' or cat == 'Nd'


def word_char(c):
    return letter_or_digit(c) or c in HYPHENS


def punctuation(c):
    return unicodedata.category(c)[0] == 'P' and c not in '§¶'


def predict(chars, slots, previous):
    """(words, the index of the character whose line counts each word) for a laid-out text,
    given the character slots left by earlier layouts and the previous first word record."""
    n = len(chars)
    words, at = [], []
    in_word, first = False, 0
    for i, c in enumerate(chars):
        if word_char(c):
            if not in_word:
                in_word, first = True, i
            if i == n - 1:
                words.append((first, i))
                at.append(i)
            continue
        if c in APOSTROPHES and i > 0 and letter_or_digit(chars[i - 1]):
            nxt = chars[i + 1] if i + 1 < n else (slots[i + 1] if i + 1 < len(slots) else '\0')
            if letter_or_digit(nxt):
                continue
        if in_word:
            words.append((first, i - 1))
            at.append(i)
            in_word = False
        elif i == 0 and (not punctuation(c) or n == 1):
            words.append((0, -1))
            at.append(0)
    if not words and n > 0:
        return [previous], []
    return words, at


def rule(path):
    slots, previous = {}, {}
    bad = total = lines_bad = lines_total = 0
    for kind, font, text, f in parse(path):
        if 'chars' not in f:
            continue
        chars = [chr(int(x.rstrip('-'), 16)) for x in f['chars'].split(',')] if f['chars'] else []
        s = slots.setdefault(font, [])
        # A drawn soft hyphen reads as "-" at a break; the rule is the same for both.
        words, at = predict(chars, s, previous.get(font, (0, 0, 0)))
        want = ','.join(f'{w[0]}-{w[1]}:{w[2] if len(w) > 2 else w[1] - w[0] + 1}' for w in words) if chars else ''
        total += 1
        if want != f['words'] or str(len(words) if chars else 0) != f['wc']:
            bad += 1
            print('words', kind, font, repr(unhex(text)), f.get('w', ''), 'measured', f['wc'], f['words'], 'predicted', want)
        # The line ranges are logged for W lines only; other texts are checked when on one line.
        if 'lines' in f or (kind != 'W' and f['lc'] in ('0', '1')):
            ends, end = [], -1
            for seg in f['lines'].split('|') if 'lines' in f else ['x' * len(chars)] if chars else []:
                end += len(re.findall(r'\\u[0-9A-F]{4}|.', seg))
                ends.append(end)
            counts = [0] * len(ends)
            for i in at:
                counts[next(k for k, e in enumerate(ends) if i <= e)] += 1
            lines_total += 1
            if ','.join(map(str, counts)) != f['lw']:
                lines_bad += 1
                print('lines', kind, font, repr(unhex(text)), f.get('w', ''), 'measured', f['lw'], 'predicted', counts)
        for i, c in enumerate(chars):
            if i < len(s):
                s[i] = c
            else:
                s.append(c)
        if chars and f['words']:
            a, rest = f['words'].split(',')[0].split(':')
            first, last = a.split('-', 1) if not a.startswith('-') else a.split('-', 2)[1:]
            previous[font] = (int(first), int(last), int(rest))
    print(f'{total - bad}/{total} word lists match, {lines_total - lines_bad}/{lines_total} line word counts match')


def diff(a, b):
    la = {key(*e[:3], e[3]): e[3] for e in parse(a)}
    lb = {key(*e[:3], e[3]): e[3] for e in parse(b)}
    n = 0
    fields = ['cc', 'sc', 'wc', 'lc', 'words', 'lw', 'lines']
    for k in sorted(set(la) & set(lb)):
        fx, fy = la[k], lb[k]
        if any(fx.get(x) != fy.get(x) for x in fields):
            n += 1
            print(k[0], k[1], repr(unhex(k[2])), k[3])
            print('  orig', ' '.join(f'{x}={fx.get(x)}' for x in fields if x in fx))
            print('  ours', ' '.join(f'{x}={fy.get(x)}' for x in fields if x in fy))
    only = len(set(la) ^ set(lb))
    print(f'{n} differ of {len(set(la) & set(lb))} common lines; {only} lines in one log only (widths where only one player\'s breaks change)')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == 'diff':
        diff(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3 and sys.argv[1] == 'rule':
        rule(sys.argv[2])
    else:
        sys.exit(__doc__)
