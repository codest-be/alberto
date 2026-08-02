#!/usr/bin/env python3
"""Print one version's section of CHANGELOG.md, as a GitHub release body.

    .github/scripts/extract-changelog-section.py 1.2.0

CHANGELOG.md is the only place release notes are written: the release pull request is where they
are edited, and this makes the GitHub release a copy of that rather than a second thing to keep
in sync. Two files saying almost the same thing is how one of them goes stale.

Exits 1 when the version has no section. That is the point — it is the same defect as the missing
``[0.1.2]`` section, caught at the moment it matters rather than months later in a compare link.
"""

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CHANGELOG = ROOT / "CHANGELOG.md"

# 0.1.0-beta is a real heading in this file, so the prerelease suffix is optional, not absent.
VERSION_RE = r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?"


def die(msg: str) -> None:
    print(f"::error::{msg}", file=sys.stderr)
    raise SystemExit(1)


def extract(version: str) -> str:
    if not CHANGELOG.is_file():
        die(f"{CHANGELOG} does not exist.")

    lines = CHANGELOG.read_text(encoding="utf-8").splitlines()

    # The trailing `[key]: url` block is where the compare links live. Peeling it off first keeps
    # it out of the body and makes it available for the footer.
    refs: dict[str, str] = {}
    body_end = len(lines)
    ref_re = re.compile(r"^\[([^\]]+)\]:\s*(\S+)\s*$")
    for i in range(len(lines) - 1, -1, -1):
        if not lines[i].strip():
            if refs:
                continue
            body_end = i
            continue
        m = ref_re.match(lines[i])
        if not m:
            break
        refs[m.group(1)] = m.group(2)
        body_end = i
    body = lines[:body_end]

    heading = re.compile(rf"^## \[({VERSION_RE})\]")
    start = None
    for i, line in enumerate(body):
        m = heading.match(line)
        if m and m.group(1) == version:
            start = i
            break

    if start is None:
        die(
            f"CHANGELOG.md has no `## [{version}]` section. Add it before releasing — the "
            f"release notes are not written anywhere else."
        )

    end = len(body)
    for i in range(start + 1, len(body)):
        if re.match(r"^## \[", body[i]):
            end = i
            break

    section = body[start + 1 : end]

    # `---` separates sections in this file and carries no meaning inside one.
    while section and (not section[0].strip() or re.match(r"^-{3,}\s*$", section[0])):
        section.pop(0)
    while section and (not section[-1].strip() or re.match(r"^-{3,}\s*$", section[-1])):
        section.pop()

    if not section:
        die(f"The `## [{version}]` section in CHANGELOG.md is empty.")

    out = "\n".join(section)

    compare = refs.get(version)
    if compare and "/compare/" in compare:
        out += f"\n\n**Full changelog**: {compare}"

    return out + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("version", help="Version to extract, without a leading v (e.g. 1.2.0).")
    args = ap.parse_args()

    if not re.fullmatch(VERSION_RE, args.version):
        die(f"'{args.version}' is not a version. Pass it without the leading `v`.")

    sys.stdout.write(extract(args.version))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
