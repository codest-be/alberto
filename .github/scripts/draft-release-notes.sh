#!/usr/bin/env bash
#
# Drafts a CHANGELOG section from the closed issues in a milestone.
#
# Usage: .github/scripts/draft-release-notes.sh <owner/repo> <milestone> > notes.md
#
# This is a DRAFT, not the final text. Issue titles are written to be found, not to be read as
# release notes, so the release pull request is where this gets edited into prose. That editing
# pass is the whole reason releases open a PR instead of pushing a tag: one pass per release
# instead of a hand-edited CHANGELOG conflict on every contribution.
#
# Requires: gh (authenticated), jq.

set -euo pipefail

REPO="${1:?usage: draft-release-notes.sh <owner/repo> <milestone>}"
MILESTONE="${2:?usage: draft-release-notes.sh <owner/repo> <milestone>}"

# A milestone that does not exist yet is not an error worth stopping a release for — the first
# release cut on a new line routinely has none. Say so in the section and let the maintainer
# write it, rather than failing the workflow three steps from the end.
if ! gh api "repos/$REPO/milestones?state=all&per_page=100" --jq '.[].title' 2>/dev/null |
    grep -qxF "$MILESTONE"; then
    echo "_No milestone \`$MILESTONE\` exists in \`$REPO\`. Write this section by hand._"
    exit 0
fi

issues=$(
    gh issue list \
        --repo "$REPO" \
        --milestone "$MILESTONE" \
        --state closed \
        --limit 500 \
        --json number,title,labels
)

count=$(jq 'length' <<<"$issues")

if [ "$count" -eq 0 ]; then
    echo "_No closed issues in milestone \`$MILESTONE\`. Write this section by hand._"
    exit 0
fi

# Keep a Changelog's headings, in the order that file uses them. An issue lands under the first
# heading whose label it carries, so `breaking-change` always wins over `bug`.
emit_section() {
    local heading="$1" label="$2"
    local body
    body=$(jq -r --arg l "$label" '
        map(select(.labels | map(.name) | index($l)))
        | .[]
        | "- \(.title) (#\(.number))"
    ' <<<"$issues")

    if [ -n "$body" ]; then
        printf '### %s\n\n%s\n\n' "$heading" "$body"
    fi
}

emit_section "Breaking changes" "breaking-change"
emit_section "Added" "enhancement"
emit_section "Fixed" "bug"
emit_section "Documentation" "documentation"

# Anything carrying none of the above still has to appear — a silently dropped issue is worse
# than one filed under a vague heading.
other=$(jq -r '
    map(select(
        (.labels | map(.name)) as $n
        | ($n | index("breaking-change")) == null
        and ($n | index("enhancement"))     == null
        and ($n | index("bug"))             == null
        and ($n | index("documentation"))   == null
    ))
    | .[]
    | "- \(.title) (#\(.number))"
' <<<"$issues")

if [ -n "$other" ]; then
    printf '### Changed\n\n%s\n\n' "$other"
fi

if jq -e 'map(select(.labels | map(.name) | index("breaking-change"))) | length > 0' >/dev/null <<<"$issues"; then
    printf '%s\n\n' \
        "> The migration steps for each breaking change are in the description of the pull request that made it. Fold them into the entries above before merging this release."
fi
