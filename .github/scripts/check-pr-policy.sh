#!/usr/bin/env bash
#
# Enforces the contribution policy on a pull request:
#
#   1. it closes at least one issue;
#   2. every issue it closes carries a milestone naming a real version;
#   3. a `breaking-change` label agrees with that milestone.
#
# The milestone is what decides which version a change ships in (see docs/releasing.md), so this
# script is the load-bearing half of the versioning strategy rather than a formatting check. If
# the link were optional, the target version would be too.
#
# Usage: .github/scripts/check-pr-policy.sh <owner/repo> <pr-number>
# Requires: gh (authenticated), jq, perl.
#
# Runnable locally against any open PR:
#   .github/scripts/check-pr-policy.sh codest-be/alberto 123

set -euo pipefail

REPO="${1:?usage: check-pr-policy.sh <owner/repo> <pr-number>}"
PR="${2:?usage: check-pr-policy.sh <owner/repo> <pr-number>}"

FAILED=0
SUMMARY=""

fail() {
    echo "::error::$*" >&2
    SUMMARY+="- ❌ $*"$'\n'
    FAILED=1
}

pass() {
    echo "ok: $*"
    SUMMARY+="- ✅ $*"$'\n'
}

skip() {
    echo "skipped: $*"
    SUMMARY+="- ⏭️ $*"$'\n'
}

emit_summary() {
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        {
            echo "### PR policy"
            echo
            printf '%s' "$SUMMARY"
        } >>"$GITHUB_STEP_SUMMARY"
    fi
}
trap emit_summary EXIT

pr_json=$(gh pr view "$PR" --repo "$REPO" --json title,body,labels,author)
title=$(jq -r '.title // ""' <<<"$pr_json")
body=$(jq -r '.body // ""' <<<"$pr_json")
author=$(jq -r '.author.login // ""' <<<"$pr_json")
labels=$(jq -r '.labels[].name' <<<"$pr_json")

has_label() { grep -qxF "$1" <<<"$labels"; }

# --- Bypasses -------------------------------------------------------------------------------
#
# Deliberately narrow. Dependabot cannot open an issue for itself, and the release PR is opened
# by release.yml with the `release` label already on it. Nothing else is exempt — a bypass that
# a contributor can apply to themselves is not a policy.

case "$author" in
    dependabot | dependabot\[bot\] | app/dependabot)
        skip "Dependabot pull request — policy not applied."
        exit 0
        ;;
esac

if has_label release; then
    skip "\`release\` label — generated release pull request, policy not applied."
    exit 0
fi

# --- 1. Issue link --------------------------------------------------------------------------
#
# HTML comments are stripped first so the guidance in pull_request_template.md cannot satisfy
# the check on a contributor's behalf. The bare `Closes #` the template ships carries no digits
# and so matches nothing either way.

body_stripped=$(perl -0777 -pe 's/<!--.*?-->//gs' <<<"$body")

refs=$(
    printf '%s\n%s\n' "$title" "$body_stripped" |
        grep -oiE '\b(close[sd]?|fix(e[sd])?|resolve[sd]?)[[:space:]]+#[0-9]+' |
        grep -oE '[0-9]+' |
        sort -un || true
)

if [ -z "$refs" ]; then
    fail "This pull request does not close an issue. Add \`Closes #123\` to the description. Every change ships in a version, and the issue's milestone is what names it — see docs/releasing.md."
    exit 1
fi

pass "Closes issue(s): $(tr '\n' ' ' <<<"$refs" | sed 's/ $//' | sed 's/\([0-9]\+\)/#\1/g')"

# --- 2. Milestone ---------------------------------------------------------------------------

milestone=""
for n in $refs; do
    if ! issue=$(gh api "repos/$REPO/issues/$n" 2>/dev/null); then
        fail "Referenced #$n does not exist in $REPO."
        continue
    fi

    if jq -e 'has("pull_request")' >/dev/null <<<"$issue"; then
        fail "Referenced #$n is a pull request, not an issue. A pull request cannot carry the milestone that decides the target version."
        continue
    fi

    ms=$(jq -r '.milestone.title // ""' <<<"$issue")

    if [ -z "$ms" ]; then
        fail "Issue #$n has no milestone. Triage it to a target version before merging — the milestone is what decides which release this ships in."
        continue
    fi

    if [ "$ms" = "Backlog" ]; then
        fail "Issue #$n is still in the \`Backlog\` milestone. \`Backlog\` is not a version; the issue has to be scheduled into a real one first."
        continue
    fi

    if ! [[ "$ms" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        fail "Issue #$n has milestone \`$ms\`, which is not a version. Milestones are named for the exact version they ship — \`1.1.0\`, \`1.0.1\`."
        continue
    fi

    if [ -n "$milestone" ] && [ "$milestone" != "$ms" ]; then
        fail "Referenced issues disagree on milestone (\`$milestone\` vs \`$ms\`). A pull request ships in exactly one version — split it, or align the issues."
        continue
    fi

    milestone="$ms"
done

[ "$FAILED" -eq 0 ] || exit 1

pass "Target version: **$milestone**"

# --- 3. breaking-change vs milestone --------------------------------------------------------
#
# Only this direction is constrained: a major may of course contain non-breaking changes. What
# must not happen is a break landing in a version whose number does not warn anyone about it.
#
# Pre-1.0 the minor slot is where breaks go, under semver's 0.y.z clause.

if has_label breaking-change; then
    IFS=. read -r maj min pat <<<"$milestone"

    if [ "$maj" -ge 1 ]; then
        if [ "$min" -eq 0 ] && [ "$pat" -eq 0 ]; then
            pass "\`breaking-change\` lands in major **$milestone**."
        else
            fail "\`breaking-change\` cannot ship in **$milestone**. At 1.0 and above a break requires a major — retarget the issue to \`$((maj + 1)).0.0\`, or drop the label if nothing actually breaks."
        fi
    else
        if [ "$pat" -eq 0 ]; then
            pass "\`breaking-change\` lands in pre-1.0 minor **$milestone**."
        else
            fail "\`breaking-change\` cannot ship in patch **$milestone**. On a 0.x line breaks go in the minor slot — retarget the issue to \`0.$((min + 1)).0\`."
        fi
    fi
else
    pass "No \`breaking-change\` label."
fi

exit "$FAILED"
