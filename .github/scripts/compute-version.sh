#!/usr/bin/env bash
#
# Works out the version the next release should carry, from the branch it is run on.
#
# Usage: .github/scripts/compute-version.sh <owner/repo> <auto|major|minor|patch> [explicit]
#
#   <explicit>  A literal version (1.0.0). Overrides everything else — this is the escape hatch
#               for cutting a version the rules would not have chosen, 1.0.0 above all.
#
# Prints the computed version on stdout. Everything else goes to stderr, so the caller can do:
#
#   VERSION=$(.github/scripts/compute-version.sh codest-be/alberto auto)
#
# In `auto` mode, over the pull requests merged since the last tag reachable from HEAD:
#
#   * any `breaking-change` label            -> major
#   * else any non-empty PublicAPI.Unshipped -> minor
#   * else                                   -> patch
#
# The second rule reuses an artifact the build already forces to be correct: the public-API
# analyzer fails the build on an undeclared symbol, so Unshipped cannot be quietly stale.
#
# Pre-1.0 mapping: on a 0.y.z line a major and a minor both land in the minor slot, because
# semver gives 0.x no stability guarantee and there is no 0-major to increment. Going to 1.0.0
# is therefore always an explicit act — pass it as <explicit>.
#
# Requires: git, gh (authenticated), and a checkout with tags (actions/checkout fetch-depth: 0).

set -euo pipefail

REPO="${1:?usage: compute-version.sh <owner/repo> <auto|major|minor|patch> [explicit]}"
MODE="${2:?usage: compute-version.sh <owner/repo> <auto|major|minor|patch> [explicit]}"
EXPLICIT="${3:-}"

log() { echo "$*" >&2; }

semver_re='^[0-9]+\.[0-9]+\.[0-9]+$'

# --- Explicit override ----------------------------------------------------------------------

if [ -n "$EXPLICIT" ]; then
    if ! [[ "$EXPLICIT" =~ $semver_re ]]; then
        log "error: explicit version '$EXPLICIT' is not a MAJOR.MINOR.PATCH version."
        exit 1
    fi
    log "Explicit version requested: $EXPLICIT (bump rules not applied)."
    echo "$EXPLICIT"
    exit 0
fi

# --- Current version ------------------------------------------------------------------------

current=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1)

if ! [[ "$current" =~ $semver_re ]]; then
    log "error: could not read a MAJOR.MINOR.PATCH <VersionPrefix> from Directory.Build.props (got '$current')."
    exit 1
fi

IFS=. read -r cur_maj cur_min cur_pat <<<"$current"
log "Current version: $current"

# --- Decide the bump ------------------------------------------------------------------------

bump="$MODE"

if [ "$MODE" = "auto" ]; then
    last_tag=$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)

    if [ -n "$last_tag" ]; then
        range="$last_tag..HEAD"
        log "Inspecting pull requests in $range"
    else
        range="HEAD"
        log "No v* tag reachable from HEAD — inspecting all history."
    fi

    # Squash merges put the PR number in the commit subject: "Some change (#75)".
    prs=$(
        git log "$range" --pretty=%s |
            grep -oE '\(#[0-9]+\)$' |
            grep -oE '[0-9]+' |
            sort -un || true
    )

    breaking=""
    for n in $prs; do
        if gh pr view "$n" --repo "$REPO" --json labels --jq '.labels[].name' 2>/dev/null |
            grep -qxF 'breaking-change'; then
            breaking="$breaking #$n"
        fi
    done

    # Only packable projects count. Alberto.Admin is parked with a large Unshipped file that is
    # deliberately never promoted; letting it vote here would pin every release to minor.
    additive=""
    while read -r pkg; do
        [ -n "$pkg" ] || continue
        f="src/$pkg/PublicAPI.Unshipped.txt"
        [ -f "$f" ] || continue
        if grep -qvE '^(#nullable enable)?[[:space:]]*$' "$f"; then
            additive="$additive $pkg"
        fi
    done < <(grep -vE '^\s*(#|$)' .github/packages.txt)

    if [ -n "$breaking" ]; then
        bump=major
        log "Bump: major — breaking-change on:$breaking"
    elif [ -n "$additive" ]; then
        bump=minor
        log "Bump: minor — unshipped public API in:$additive"
    else
        bump=patch
        log "Bump: patch — no breaking-change labels, no unshipped public API."
    fi
else
    log "Bump: $bump (forced)"
fi

# --- Apply ----------------------------------------------------------------------------------

if [ "$cur_maj" -eq 0 ] && [ "$bump" = "major" ]; then
    log "note: pre-1.0 line — a major lands in the minor slot. Pass an explicit 1.0.0 to cut 1.0."
    bump=minor
fi

case "$bump" in
    major) next="$((cur_maj + 1)).0.0" ;;
    minor) next="$cur_maj.$((cur_min + 1)).0" ;;
    patch) next="$cur_maj.$cur_min.$((cur_pat + 1))" ;;
    *)
        log "error: unknown bump '$bump' (expected auto, major, minor or patch)."
        exit 1
        ;;
esac

log "Next version: $next"
echo "$next"
