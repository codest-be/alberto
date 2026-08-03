#!/usr/bin/env python3
"""Apply a release to the working tree.

Three edits, all of them things a human would otherwise do by hand and occasionally forget:

  1. ``<VersionPrefix>`` in ``Directory.Build.props`` — the single source of truth that
     ``publish-packages.yml`` checks the git tag against.
  2. ``CHANGELOG.md`` — rename ``[Unreleased]`` to the release, insert a fresh empty
     ``[Unreleased]``, and fix up the compare links.
  3. ``PublicAPI.Unshipped.txt`` -> ``PublicAPI.Shipped.txt`` for every *packable* project.

Usage::

    .github/scripts/apply-release.py --version 1.1.0 --notes notes.md
    .github/scripts/apply-release.py --version 1.1.0 --notes notes.md --check

``--check`` prints what would change and writes nothing, which is what the release workflow's
``dry_run`` mode runs.

Deliberately has no network calls and no ``gh`` dependency: it is pure file surgery, so it can be
run and inspected locally against a dirty tree.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import re
import subprocess
import sys
from pathlib import Path

REPO_URL = "https://github.com/codest-be/alberto"

ROOT = Path(__file__).resolve().parents[2]
PROPS = ROOT / "Directory.Build.props"
CHANGELOG = ROOT / "CHANGELOG.md"
PACKAGES = ROOT / ".github" / "packages.txt"

SEMVER = re.compile(r"^\d+\.\d+\.\d+$")


def die(msg: str) -> None:
    print(f"error: {msg}", file=sys.stderr)
    raise SystemExit(1)


def packable_projects() -> list[str]:
    """The packages that ship to nuget.org.

    Read from .github/packages.txt rather than globbing src/*: Alberto.Admin is parked and
    carries a large PublicAPI.Unshipped.txt that must never be promoted to Shipped.
    """
    out = []
    for line in PACKAGES.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            out.append(line)
    if not out:
        die(f"{PACKAGES} lists no packages")
    return out


# --- 1. Directory.Build.props ---------------------------------------------------------------


def bump_props(version: str, check: bool) -> str:
    text = PROPS.read_text(encoding="utf-8")
    m = re.search(r"<VersionPrefix>(.*?)</VersionPrefix>", text)
    if not m:
        die("no <VersionPrefix> in Directory.Build.props")
    current = m.group(1)

    if current == version:
        print(f"  VersionPrefix already {version} — unchanged")
        return current

    print(f"  VersionPrefix {current} -> {version}")
    if not check:
        PROPS.write_text(
            text[: m.start(1)] + version + text[m.end(1) :], encoding="utf-8"
        )
    return current


# --- 2. CHANGELOG.md ------------------------------------------------------------------------


def _split_link_refs(lines: list[str]) -> tuple[list[str], dict[str, str]]:
    """Peel the trailing ``[key]: url`` block off the end of the file."""
    ref = re.compile(r"^\[([^\]]+)\]:\s*(\S+)\s*$")
    refs: dict[str, str] = {}
    end = len(lines)
    for i in range(len(lines) - 1, -1, -1):
        line = lines[i]
        if not line.strip():
            if refs:
                continue
            end = i
            continue
        m = ref.match(line)
        if m:
            refs[m.group(1)] = m.group(2)
            end = i
            continue
        break
    return lines[:end], refs


# Keep a Changelog's headings in the order that file uses them, with Documentation appended
# because this project uses it. Anything unrecognised keeps its first-seen position, after these.
HEADING_ORDER = [
    "Breaking changes",
    "Added",
    "Changed",
    "Deprecated",
    "Removed",
    "Fixed",
    "Security",
    "Documentation",
]


def _parse_sections(text: str) -> tuple[list[str], dict[str, list[str]], list[str]]:
    """Split changelog prose into (preamble, {heading: lines}, heading order)."""
    preamble: list[str] = []
    sections: dict[str, list[str]] = {}
    order: list[str] = []
    current: str | None = None

    for line in text.splitlines():
        m = re.match(r"^###\s+(.*?)\s*$", line)
        if m:
            current = m.group(1)
            if current not in sections:
                sections[current] = []
                order.append(current)
            continue
        (preamble if current is None else sections[current]).append(line)

    return preamble, sections, order


def merge_bodies(carried: str, notes: str) -> str:
    """Fold hand-written ``[Unreleased]`` content into the drafted notes.

    Both sides use ``### Added``-style headings, so concatenating them would emit the same
    heading twice. Merge under one heading each instead, carried entries first.
    """
    pre_a, sec_a, order_a = _parse_sections(carried)
    pre_b, sec_b, order_b = _parse_sections(notes)

    seen = list(dict.fromkeys(order_a + order_b))
    ordered = [h for h in HEADING_ORDER if h in seen]
    ordered += [h for h in seen if h not in HEADING_ORDER]

    out: list[str] = []
    for block in (pre_a, pre_b):
        block = [ln for ln in block if ln.strip()]
        if block:
            out += block + [""]

    for heading in ordered:
        entries = [
            ln for ln in sec_a.get(heading, []) + sec_b.get(heading, []) if ln.strip()
        ]
        if entries:
            out += [f"### {heading}", ""] + entries + [""]

    return "\n".join(out).strip()


def _version_key(name: str) -> tuple:
    """Sort key placing real versions first, descending; anything else last."""
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)$", name)
    if m:
        return (0, tuple(-int(p) for p in m.groups()))
    return (1, name)


def warn_if_previous_disagrees_with_tags(previous: str | None) -> None:
    """Warn when the changelog's newest version is not the newest reachable tag.

    The compare link is built from the newest ``## [X.Y.Z]`` heading, because that is what the
    file itself claims shipped last. When a release is tagged but its section never lands — which
    has happened — the heading falls behind the tags and every compare link from then on spans
    the wrong range. Git knows the truth, so ask it.

    This warns rather than fails: a shallow clone has no tags, and the first release has no
    previous one.
    """
    try:
        latest = subprocess.run(
            ["git", "describe", "--tags", "--abbrev=0", "--match", "v[0-9]*"],
            capture_output=True, text=True, check=True,
        ).stdout.strip().lstrip("v")
    except (subprocess.CalledProcessError, FileNotFoundError):
        return

    if latest and latest != previous:
        print(
            f"::warning::Newest reachable tag is v{latest}, but the newest CHANGELOG.md section "
            f"is {'[' + previous + ']' if previous else 'absent'}. The compare link for this "
            f"release will span the wrong range. Add the missing section before releasing.",
            file=sys.stderr,
        )


def cut_changelog(version: str, notes: str, date: str, check: bool) -> None:
    lines = CHANGELOG.read_text(encoding="utf-8").splitlines()
    body, refs = _split_link_refs(lines)

    unreleased = None
    next_section = None
    for i, line in enumerate(body):
        if re.match(r"^## \[Unreleased\]", line):
            unreleased = i
        elif unreleased is not None and line.startswith("## ["):
            next_section = i
            break

    if unreleased is None:
        die("no '## [Unreleased]' heading in CHANGELOG.md")
    if next_section is None:
        next_section = len(body)

    # Carry any hand-written [Unreleased] content into the release rather than dropping it.
    # Policy says pull requests should not put anything here, but silently discarding a note
    # someone did write is the wrong way to enforce that.
    carried = "\n".join(body[unreleased + 1 : next_section]).strip()
    carried = re.sub(r"^-{3,}\s*$", "", carried, flags=re.M).strip()

    if carried:
        print("  carrying existing [Unreleased] content into the release section")

    previous = None
    for line in body[next_section:]:
        m = re.match(r"^## \[(\d+\.\d+\.\d+)\]", line)
        if m:
            previous = m.group(1)
            break

    warn_if_previous_disagrees_with_tags(previous)

    body_text = merge_bodies(carried, notes) if carried else notes.strip()

    section = [
        "## [Unreleased]",
        "",
        "---",
        "",
        f"## [{version}] - {date}",
        "",
        body_text,
        "",
        "---",
        "",
    ]

    new_body = body[:unreleased] + section + body[next_section:]

    refs["Unreleased"] = f"{REPO_URL}/compare/v{version}...HEAD"
    refs[version] = (
        f"{REPO_URL}/compare/v{previous}...v{version}"
        if previous
        else f"{REPO_URL}/releases/tag/v{version}"
    )

    ordered = ["Unreleased"] + sorted(
        (k for k in refs if k != "Unreleased"), key=_version_key
    )
    ref_block = [f"[{k}]: {refs[k]}" for k in ordered]

    while new_body and not new_body[-1].strip():
        new_body.pop()

    out = "\n".join(new_body + [""] + ref_block) + "\n"

    print(f"  CHANGELOG.md: new section [{version}] — {date}"
          + (f" (compare from v{previous})" if previous else ""))
    if not check:
        CHANGELOG.write_text(out, encoding="utf-8")


# --- 3. PublicAPI promotion -----------------------------------------------------------------


HEADER = "#nullable enable"

# A deletion is recorded in PublicAPI.Unshipped.txt as the removed member's line prefixed with
# this marker. The marker is a *pending* instruction, not an entry: promoting it verbatim into
# PublicAPI.Shipped.txt is what RS0024 ("the shipped API file can't have removed members")
# rejects, and it fails the build of the very package the release is preparing. Promotion has to
# read the marker and delete the member instead.
REMOVED_PREFIX = "*REMOVED*"


def promote_public_api(check: bool) -> None:
    moved_any = False
    for pkg in packable_projects():
        unshipped = ROOT / "src" / pkg / "PublicAPI.Unshipped.txt"
        shipped = ROOT / "src" / pkg / "PublicAPI.Shipped.txt"
        if not unshipped.exists() or not shipped.exists():
            continue

        new_entries = [
            ln
            for ln in unshipped.read_text(encoding="utf-8").splitlines()
            if ln.strip() and ln.strip() != HEADER
        ]
        if not new_entries:
            continue

        added = [ln for ln in new_entries if not ln.startswith(REMOVED_PREFIX)]
        removed = [ln[len(REMOVED_PREFIX):] for ln in new_entries if ln.startswith(REMOVED_PREFIX)]

        moved_any = True
        existing = [
            ln
            for ln in shipped.read_text(encoding="utf-8").splitlines()
            if ln.strip() and ln.strip() != HEADER
        ]

        # A *REMOVED* line that names nothing in the shipped file is a ledger that has already
        # drifted. Say so here rather than let the release pull request fail its own build.
        orphans = [ln for ln in removed if ln not in set(existing)]
        if orphans:
            raise SystemExit(
                f"{pkg}: PublicAPI.Unshipped.txt marks members removed that are not in "
                f"PublicAPI.Shipped.txt:\n  " + "\n  ".join(orphans)
            )

        # C-locale sort, matching how these files are already ordered, so the diff shows only
        # the added entries rather than a reshuffle of all 1000-odd lines.
        merged = sorted((set(existing) | set(added)) - set(removed))

        print(f"  {pkg}: promoting {len(added)} public API entr"
              f"{'y' if len(added) == 1 else 'ies'}"
              + (f", removing {len(removed)}" if removed else ""))
        if not check:
            shipped.write_text("\n".join([HEADER] + merged) + "\n", encoding="utf-8")
            unshipped.write_text(HEADER + "\n", encoding="utf-8")

    if not moved_any:
        print("  no unshipped public API to promote")


# --- main -----------------------------------------------------------------------------------


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--version", required=True)
    ap.add_argument("--notes", required=True, type=Path)
    ap.add_argument("--date", default=None, help="release date, defaults to today (UTC)")
    ap.add_argument("--check", action="store_true", help="report only, write nothing")
    args = ap.parse_args()

    if not SEMVER.match(args.version):
        die(f"'{args.version}' is not a MAJOR.MINOR.PATCH version")
    if not args.notes.is_file():
        die(f"notes file not found: {args.notes}")

    date = args.date or _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%d")
    notes = args.notes.read_text(encoding="utf-8")

    print(f"{'Would apply' if args.check else 'Applying'} release {args.version} ({date}):")
    bump_props(args.version, args.check)
    cut_changelog(args.version, notes, date, args.check)
    promote_public_api(args.check)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
