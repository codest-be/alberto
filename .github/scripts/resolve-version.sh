#!/usr/bin/env bash
#
# Resolves the version a release will carry, from the milestone it is releasing.
#
# Usage: .github/scripts/resolve-version.sh <owner/repo> <milestone> [explicit]
#
#   <milestone>  The milestone being released. Its title IS the version — `0.2.0`, `1.0.2`.
#   <explicit>   A literal version, for a release with no milestone. Bypasses every check below
#                except the shape of the version itself.
#
# Prints the version on stdout. Everything else goes to stderr, so the caller can do:
#
#   VERSION=$(.github/scripts/resolve-version.sh codest-be/alberto 0.2.0)
#
# This script does not *derive* a version. It used to: it read `breaking-change` labels off the
# pull requests merged since the last tag, inspected PublicAPI.Unshipped, and worked out a bump
# from them. That was a second source of truth. `check-pr-policy.sh` already refuses to merge a
# pull request whose issue has no milestone naming an exact version, so by release time every
# merged change has a version written on it — re-deriving one from squash-commit subjects could
# only ever agree with that or contradict it, and it contradicted it silently.
#
# So the milestone is the input, and the notes are drafted from the same milestone. One source.
# What is left here is validation: the checks that catch a version which is *wrong*, rather than
# machinery that picks one.
#
# Requires: gh (authenticated) and jq. No tags or git history needed.

set -euo pipefail

REPO="${1:?usage: resolve-version.sh <owner/repo> <milestone> [explicit]}"
MILESTONE="${2:-}"
EXPLICIT="${3:-}"

log() { echo "$*" >&2; }
die() { echo "::error::$*" >&2; exit 1; }

semver_re='^[0-9]+\.[0-9]+\.[0-9]+$'

# --- Explicit override ------------------------------------------------------------------------

if [ -n "$EXPLICIT" ]; then
    [[ "$EXPLICIT" =~ $semver_re ]] ||
        die "Explicit version '$EXPLICIT' is not a MAJOR.MINOR.PATCH version."
    log "Explicit version: $EXPLICIT — releasing without a milestone, checks below skipped."
    echo "$EXPLICIT"
    exit 0
fi

[ -n "$MILESTONE" ] ||
    die "Pass the milestone to release. Its title is the version. Use the explicit version input only for a release that has no milestone."

# --- The milestone is the version ---------------------------------------------------------------

[[ "$MILESTONE" =~ $semver_re ]] ||
    die "Milestone '$MILESTONE' is not a version. Milestones are named for the exact version they ship — \`1.1.0\`, \`1.0.2\`. \`Backlog\` is not releasable."

version="$MILESTONE"

# state=all: a milestone closed just before cutting the release is still the right one to release.
ms=$(gh api "repos/$REPO/milestones?state=all&per_page=100" \
        --jq ".[] | select(.title == \"$version\")" 2>/dev/null || true)

[ -n "$ms" ] ||
    die "No milestone \`$version\` in $REPO. Create it and triage the issues onto it before releasing."

open_issues=$(jq -r '.open_issues' <<<"$ms")
closed_issues=$(jq -r '.closed_issues' <<<"$ms")

log "Milestone $version: $closed_issues closed, $open_issues open."

# A warning, not an error. Punting an unfinished issue to the next milestone is a normal thing to
# do *during* a release, and the notes only ever draw on the closed ones anyway — but releasing a
# milestone still holding open work is far more often a mistake than a decision.
if [ "$open_issues" -gt 0 ]; then
    log "::warning::Milestone $version still has $open_issues open issue(s). They will not appear in the release notes. Close them, or move them to the next milestone."
fi

[ "$closed_issues" -gt 0 ] ||
    die "Milestone \`$version\` has no closed issues, so there is nothing to release and nothing to write notes from."

# --- It has to be a step forwards -----------------------------------------------------------------

current=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1)

[[ "$current" =~ $semver_re ]] ||
    die "Could not read a MAJOR.MINOR.PATCH <VersionPrefix> from Directory.Build.props (got '$current')."

IFS=. read -r cur_maj cur_min cur_pat <<<"$current"
IFS=. read -r new_maj new_min new_pat <<<"$version"

log "Current version: $current"

# Strictly greater, and nothing tighter. Jumping 0.1.2 -> 0.3.0 to abandon a version number is a
# legitimate thing to want; re-releasing one, or going backwards, never is.
if ! { [ "$new_maj" -gt "$cur_maj" ] ||
       { [ "$new_maj" -eq "$cur_maj" ] && [ "$new_min" -gt "$cur_min" ]; } ||
       { [ "$new_maj" -eq "$cur_maj" ] && [ "$new_min" -eq "$cur_min" ] && [ "$new_pat" -gt "$cur_pat" ]; }; }; then
    die "Milestone \`$version\` is not ahead of the current version \`$current\`. Releasing it would move the version backwards, or republish one already cut. Check the branch — a patch for an older line is released from that line's \`release/**\` branch, where VersionPrefix is the line's own."
fi

# --- New public API cannot ship in a patch --------------------------------------------------------

# The one check that survived the old derivation, because nothing else performs it.
# check-pr-policy.sh reconciles `breaking-change` against the milestone at merge time, but nothing
# looks at the analyzer's output — so "added public API, aimed at a patch" was the one semver
# mistake that could reach a release unremarked. The analyzer fails the build on an undeclared
# public symbol, which is what makes a non-empty Unshipped trustworthy rather than merely stale.
#
# Only packable projects count. Alberto.Admin is parked with a large Unshipped file that is
# deliberately never promoted; letting it vote here would block every patch release.
additive=""
while read -r pkg; do
    [ -n "$pkg" ] || continue
    f="src/$pkg/PublicAPI.Unshipped.txt"
    [ -f "$f" ] || continue
    if grep -qvE '^(#nullable enable)?[[:space:]]*$' "$f"; then
        additive="$additive $pkg"
    fi
done < <(grep -vE '^\s*(#|$)' .github/packages.txt)

if [ -n "$additive" ]; then
    if [ "$new_maj" -eq "$cur_maj" ] && [ "$new_min" -eq "$cur_min" ]; then
        die "Milestone \`$version\` is a patch of \`$current\`, but there is unshipped public API in:$additive. Added API is at least a minor — retarget to \`$cur_maj.$((cur_min + 1)).0\`, or use the explicit version input if those entries are not actually new surface."
    fi
    log "Unshipped public API in:$additive — this release promotes it."
fi

log "Version: $version"
echo "$version"
