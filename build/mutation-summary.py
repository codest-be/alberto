#!/usr/bin/env python3
"""Aggregates the per-package Stryker JSON reports into one mutation score.

Stryker scores a single project per run, so the repo-wide figure is computed here.
It is mutant-weighted rather than an average of percentages: averaging would let a
40-mutant package move the number as much as a 900-mutant one.

The score follows Stryker's own definition:

    (killed + timeout) / (killed + timeout + survived + no-coverage)

`Ignored` and `CompileError` are outside the denominator, which is deliberate and
matches Stryker. An ignored mutant was filtered out on purpose, and a compile-error
mutant is not a behaviour a test could ever have caught — counting either would move
the score for reasons unrelated to test quality. `NoCoverage` *is* in the denominator:
code no test reaches is exactly what this number exists to surface.

Exit code is 1 when the aggregate falls under --break, so CI can gate on it.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys
from collections import Counter

KILLED = ("Killed", "Timeout")
MISSED = ("Survived", "NoCoverage")
SCORED = KILLED + MISSED


def score(counts: Counter) -> float | None:
    """Mutation score for one counter, or None when nothing was scoreable."""
    denominator = sum(counts[s] for s in SCORED)
    if denominator == 0:
        return None
    return 100.0 * sum(counts[s] for s in KILLED) / denominator


def collect(root: pathlib.Path) -> dict[str, Counter]:
    """One Counter of mutant statuses per package, keyed by package name."""
    packages: dict[str, Counter] = {}
    for report in sorted(root.glob("*/reports/mutation-report.json")):
        name = report.parent.parent.name
        data = json.loads(report.read_text())
        counts = Counter()
        for file_report in data.get("files", {}).values():
            for mutant in file_report.get("mutants", []):
                counts[mutant["status"]] += 1
        packages[name] = counts
    return packages


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", nargs="?", default="artifacts/stryker")
    parser.add_argument(
        "--break",
        dest="break_at",
        type=float,
        default=None,
        help="Exit 1 when the aggregate score falls below this percentage.",
    )
    parser.add_argument(
        "--json",
        dest="json_out",
        default=None,
        help="Also write the aggregate as JSON to this path.",
    )
    parser.add_argument(
        "--allow-empty",
        action="store_true",
        help="Treat 'nothing to score' as success. Required for --since runs: a PR that "
             "touches no mutated package produces no mutants, and that is a pass, not a "
             "failure to meet the threshold.",
    )
    args = parser.parse_args()

    root = pathlib.Path(args.root)
    packages = collect(root)
    if not packages:
        print(f"No Stryker reports found under {root}/*/reports/mutation-report.json")
        return 0 if args.allow_empty else 1

    total = Counter()
    for counts in packages.values():
        total.update(counts)

    width = max(len(name) for name in packages)
    print(f"{'package'.ljust(width)}  {'score':>7}  {'killed':>6} {'surv':>5} "
          f"{'nocov':>5} {'t/out':>5} {'ignd':>5} {'cerr':>5}")
    for name, counts in sorted(packages.items()):
        pkg_score = score(counts)
        rendered = "n/a" if pkg_score is None else f"{pkg_score:6.2f}%"
        print(f"{name.ljust(width)}  {rendered:>7}  "
              f"{counts['Killed']:>6} {counts['Survived']:>5} "
              f"{counts['NoCoverage']:>5} {counts['Timeout']:>5} "
              f"{counts['Ignored']:>5} {counts['CompileError']:>5}")

    aggregate = score(total)
    if aggregate is None:
        print("\nNo scoreable mutants across any package.")
        return 0 if args.allow_empty else 1

    print(f"\n{'AGGREGATE'.ljust(width)}  {aggregate:6.2f}%  "
          f"{total['Killed']:>6} {total['Survived']:>5} "
          f"{total['NoCoverage']:>5} {total['Timeout']:>5} "
          f"{total['Ignored']:>5} {total['CompileError']:>5}")
    print(f"\nScored mutants: {sum(total[s] for s in SCORED)}  "
          f"(killed+timeout {sum(total[s] for s in KILLED)}, "
          f"survived+nocoverage {sum(total[s] for s in MISSED)})")

    if args.json_out:
        payload = {
            "aggregate": round(aggregate, 2),
            "packages": {
                name: {
                    "score": None if score(c) is None else round(score(c), 2),
                    **{k: c[k] for k in ("Killed", "Survived", "NoCoverage",
                                         "Timeout", "Ignored", "CompileError")},
                }
                for name, c in sorted(packages.items())
            },
        }
        pathlib.Path(args.json_out).write_text(json.dumps(payload, indent=2) + "\n")

    if args.break_at is not None and aggregate < args.break_at:
        print(f"\nAggregate mutation score {aggregate:.2f}% is below the "
              f"required {args.break_at:.2f}%.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
