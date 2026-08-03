#!/usr/bin/env python3
"""Prints per-package line/branch coverage from a merged Cobertura report, and
optionally fails the build when the total falls under a threshold.

ReportGenerator can render a summary but cannot gate on one, so the gate lives here —
and reading the XML directly means the printed number and the enforced number come
from the same place, which they would not if one were scraped from a text report.

Packages are grouped by assembly name rather than by Cobertura <package>, which for
C# is one entry per namespace and would report `Alberto.Subscriptions` and
`Alberto.Tags` as separate projects.
"""

from __future__ import annotations

import argparse
import pathlib
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

# Cobertura reports rates, not counts, at package level; the counts have to be summed
# from the lines themselves for a weighted total to be correct.
ASSEMBLIES = [
    "Alberto.Commands",
    "Alberto.InMemory",
    "Alberto.Postgres",
    "Alberto.EntityFramework",
    "Alberto.Messaging.Postgres",
    "Alberto.Messaging",
    "Alberto.Telemetry",
    "Alberto.Testing",
    "Alberto",
]


def assembly_for(package_name: str) -> str:
    """Longest matching assembly prefix wins, so Alberto.Messaging.Postgres is not
    swallowed by Alberto.Messaging (nor either of them by Alberto)."""
    for candidate in ASSEMBLIES:
        if package_name == candidate or package_name.startswith(candidate + "."):
            return candidate
    return package_name


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", nargs="?", default="artifacts/coverage/report/Cobertura.xml")
    parser.add_argument("--break", dest="break_at", type=float, default=None,
                        help="Exit 1 when total line coverage falls below this percentage.")
    args = parser.parse_args()

    path = pathlib.Path(args.report)
    if not path.exists():
        print(f"No Cobertura report at {path}")
        return 1

    root = ET.parse(path).getroot()

    # (covered_lines, total_lines, covered_branches, total_branches) per assembly.
    stats: dict[str, list[int]] = defaultdict(lambda: [0, 0, 0, 0])
    seen: set[tuple[str, str, int]] = set()

    for package in root.iter("package"):
        assembly = assembly_for(package.get("name", ""))
        for cls in package.iter("class"):
            filename = cls.get("filename", "")
            for line in cls.iter("line"):
                number = int(line.get("number", "0"))
                # A partial class contributes the same source line under more than one
                # <class>; counting it twice would weight those files up.
                key = (assembly, filename, number)
                if key in seen:
                    continue
                seen.add(key)

                entry = stats[assembly]
                entry[1] += 1
                if int(line.get("hits", "0")) > 0:
                    entry[0] += 1

                # coverlet writes "True", ReportGenerator's Cobertura writer "true". This
                # script reads the latter by default, so a case-sensitive match reported
                # every branch column as n/a.
                if line.get("branch", "").lower() == "true":
                    # e.g. "50% (1/2)"
                    coverage = line.get("condition-coverage", "")
                    if "(" in coverage:
                        covered, total = coverage.split("(")[1].rstrip(")").split("/")
                        entry[2] += int(covered)
                        entry[3] += int(total)

    if not stats:
        print("Report contained no packages.")
        return 1

    def pct(covered: int, total: int) -> str:
        return "n/a" if total == 0 else f"{100.0 * covered / total:6.2f}%"

    width = max(len(name) for name in stats)
    print(f"{'package'.ljust(width)}  {'line':>7}  {'branch':>7}  {'lines':>12}")
    totals = [0, 0, 0, 0]
    for name, (cl, tl, cb, tb) in sorted(stats.items()):
        print(f"{name.ljust(width)}  {pct(cl, tl):>7}  {pct(cb, tb):>7}  {f'{cl}/{tl}':>12}")
        for i, v in enumerate((cl, tl, cb, tb)):
            totals[i] += v

    cl, tl, cb, tb = totals
    print(f"\n{'TOTAL'.ljust(width)}  {pct(cl, tl):>7}  {pct(cb, tb):>7}  {f'{cl}/{tl}':>12}")

    line_rate = 100.0 * cl / tl if tl else 0.0
    if args.break_at is not None and line_rate < args.break_at:
        print(f"\nLine coverage {line_rate:.2f}% is below the required {args.break_at:.2f}%.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
