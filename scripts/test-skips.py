#!/usr/bin/env python3
"""The unit tests a run skipped, from the xunit XML reports of its suites (-xml FILE), grouped
by reason, with each reason's classes and how many of their tests it skipped.

    scripts/test-skips.py [--fail-on-game] REPORT.xml...

A test that needs the game install is skipped without it, for a reason starting with one of
GAME_REASONS (tests/Shared/GameInstall.cs): the CI build has only the game's reference
assemblies and lists those skips (scripts/ci-unit-tests.sh). --fail-on-game is for a machine
with the game (scripts/check.sh): such a skip means a test did not run that should have, and
the script names the tests and exits 1. Other skips (a font or library not installed, a bench
that runs on request) are listed either way.
"""
import argparse
import collections
import sys
import xml.etree.ElementTree as ET

GAME_REASONS = ("needs game data: ", "needs game assembly: ")


def skips(paths):
    """(assembly, class, test, reason) of each skipped test of the reports."""
    found = []
    for path in paths:
        for assembly in ET.parse(path).getroot().iter("assembly"):
            name = assembly.get("name", path).replace("\\", "/").rsplit("/", 1)[-1]
            for test in assembly.iter("test"):
                if test.get("result") == "Skip":
                    found.append((name, test.get("type", ""), test.get("name", ""), (test.findtext("reason") or "").strip()))
    return found


def needs_game(reason):
    return reason.startswith(GAME_REASONS)


def report(found):
    """The skips grouped by reason (the game's first), then by class, as lines."""
    by_reason = collections.defaultdict(collections.Counter)
    for _, cls, _, reason in found:
        by_reason[reason][cls] += 1
    lines = [f"skipped: {len(found)} tests"]
    for reason in sorted(by_reason, key=lambda r: (not needs_game(r), r)):
        classes = by_reason[reason]
        lines.append(f"  {reason} ({sum(classes.values())} tests, {len(classes)} classes)")
        lines.extend(f"    {cls} ({n})" for cls, n in sorted(classes.items()))
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fail-on-game", action="store_true", help="exit 1 when a test was skipped for want of the game")
    parser.add_argument("reports", nargs="+")
    args = parser.parse_args(argv)
    found = skips(args.reports)
    if args.fail_on_game:
        game = [s for s in found if needs_game(s[3])]
        if game:
            print(f"{len(game)} tests skipped for want of the game, which this run should have:")
            for assembly, _, test, reason in game:
                print(f"  {assembly}: {test}: {reason}")
            return 1
        return 0
    print("\n".join(report(found)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
