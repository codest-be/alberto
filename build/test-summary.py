#!/usr/bin/env python3
"""Summarises one or more .trx files: counts per assembly, and every failure with its
message.

A failing `dotnet test` says which test failed only in the raw console log, which on a
run of ~2000 tests means scrolling a job log to find one line. The summary this prints
goes to the step summary instead, so the failure is on the run's front page.

With --annotate it also emits `::error::` workflow commands, which is what puts a
failure on the Files-changed tab next to the test that produced it.

Reads the trx rather than the console output because the console format is a rendering
choice of whichever logger was enabled, while the trx is the recorded result.
"""

from __future__ import annotations

import argparse
import glob
import html
import pathlib
import sys
import xml.etree.ElementTree as ET

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# A stack trace is worth reading, but 40 frames of xUnit plumbing in a step summary is
# not. Failures keep their message in full and their stack trimmed to this.
STACK_LINES = 6


class Failure:
    def __init__(self, assembly: str, name: str, message: str, stack: str) -> None:
        self.assembly = assembly
        self.name = name
        self.message = message
        self.stack = stack


def counters(root: ET.Element) -> dict[str, int]:
    node = root.find("./t:ResultSummary/t:Counters", TRX_NS)
    if node is None:
        return {}
    return {k: int(v) for k, v in node.attrib.items() if v.isdigit()}


def failures(root: ET.Element, assembly: str) -> list[Failure]:
    found = []
    for result in root.iter(f"{{{TRX_NS['t']}}}UnitTestResult"):
        if result.get("outcome") != "Failed":
            continue
        info = result.find("./t:Output/t:ErrorInfo", TRX_NS)
        message = stack = ""
        if info is not None:
            message = (info.findtext("./t:Message", "", TRX_NS) or "").strip()
            stack = (info.findtext("./t:StackTrace", "", TRX_NS) or "").strip()
        found.append(Failure(assembly, result.get("testName", "?"), message, stack))
    return found


def trim(stack: str) -> str:
    lines = [line for line in stack.splitlines() if line.strip()]
    if len(lines) <= STACK_LINES:
        return "\n".join(lines)
    return "\n".join(lines[:STACK_LINES] + [f"   ... {len(lines) - STACK_LINES} more frames"])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("paths", nargs="*", default=["artifacts/**/*.trx"],
                        help="trx files or globs. Defaults to artifacts/**/*.trx")
    parser.add_argument("--annotate", action="store_true",
                        help="Also emit ::error:: workflow commands for each failure.")
    args = parser.parse_args()

    files: list[pathlib.Path] = []
    for pattern in args.paths:
        path = pathlib.Path(pattern)
        if path.is_file():
            files.append(path)
        else:
            files.extend(pathlib.Path(p) for p in glob.glob(pattern, recursive=True))

    if not files:
        # Not an error: a job that failed before any test ran has nothing to summarise,
        # and saying so beats a stack trace from this script on top of the real failure.
        print("No .trx files found — no tests ran.")
        return 0

    rows = []
    all_failures: list[Failure] = []
    for path in sorted(set(files)):
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as err:
            print(f"Could not read {path}: {err}")
            continue
        # dotnet names the file after the assembly unless told otherwise; the run title
        # is more reliable when it was.
        assembly = path.stem.split(".net")[0]
        count = counters(root)
        rows.append((assembly, count))
        all_failures.extend(failures(root, assembly))

    total = sum(c.get("total", 0) for _, c in rows)
    passed = sum(c.get("passed", 0) for _, c in rows)
    failed = sum(c.get("failed", 0) for _, c in rows)
    skipped = sum(c.get("total", 0) - c.get("executed", 0) for _, c in rows)

    verdict = "🔴 failed" if failed else "🟢 passed"
    print(f"**{verdict}** — {passed}/{total} passed, {failed} failed, {skipped} skipped")
    print()
    print("| assembly | passed | failed | skipped |")
    print("|---|---:|---:|---:|")
    for assembly, count in sorted(rows):
        print(f"| {assembly} | {count.get('passed', 0)} | {count.get('failed', 0)} "
              f"| {count.get('total', 0) - count.get('executed', 0)} |")

    if all_failures:
        print()
        print(f"### Failures ({len(all_failures)})")
        for failure in all_failures:
            print()
            print(f"<details><summary><code>{html.escape(failure.name)}</code></summary>")
            print()
            print("```")
            print(failure.message or "(no message)")
            if failure.stack:
                print()
                print(trim(failure.stack))
            print("```")
            print()
            print("</details>")

    if args.annotate:
        for failure in all_failures:
            # Newlines would end the workflow command at the first one.
            first = (failure.message or "test failed").splitlines()[0]
            print(f"::error title={failure.name}::{first}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    sys.exit(main())
