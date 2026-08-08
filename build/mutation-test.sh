#!/usr/bin/env bash
# Runs Stryker over the mutation-tested core packages and aggregates the per-package
# scores into one number.
#
# Stryker scores one project per run (-p), so a repo-wide figure has to be aggregated
# here rather than read off a single report. The aggregate is mutant-weighted, not a
# mean of percentages: a 40-mutant package must not swing the number as hard as a
# 900-mutant one.
#
# Usage:
#   build/mutation-test.sh                 # full run, all core packages
#   build/mutation-test.sh --since main    # diff-only run, for PR gating
#   build/mutation-test.sh Alberto.csproj  # one package
set -euo pipefail

cd "$(dirname "$0")/.."
. build/build-output-lock.sh

# The mutation-tested set. Alberto.Postgres, Alberto.Messaging.Postgres and
# Alberto.EntityFramework are deliberately absent: their behaviour lives behind the
# Postgres-backed tests that the Category!=Integration filter removes, so scoring them
# against the in-memory suite would report a fabricated number rather than a low one.
# See docs/development/mutation-testing.md.
#
# Ordered cheapest first. Alberto is roughly ten times the size of any other package, so
# running it last means a full sweep reports five scores early rather than nothing for an hour.
PACKAGES=(
  Alberto.Telemetry.csproj
  Alberto.InMemory.csproj
  Alberto.Messaging.csproj
  Alberto.Commands.csproj
  Alberto.Testing.csproj
  Alberto.csproj
)

EXTRA_ARGS=()
SINCE_REF=""
if [[ ${1:-} == "--since" ]]; then
  SINCE_REF="${2:-main}"
  EXTRA_ARGS+=("--since:$SINCE_REF")
  shift 2 || shift 1
elif [[ ${1:-} == *.csproj ]]; then
  PACKAGES=("$1")
  shift
fi
if [[ $# -gt 0 ]]; then
  EXTRA_ARGS+=("$@")
fi

# --since is silently wrong in a linked git worktree, so refuse to run there.
#
# Stryker resolves the repository through LibGit2Sharp, which follows a worktree's `.git`
# *file* back to the main checkout and reports that as the working directory. The diff it
# computes is therefore main-checkout-relative: every changed path it finds is rooted
# somewhere else on disk, so no path matches a file under the worktree and every mutant is
# binned as "Removed by since filter". The run ends with "0 total mutants will be tested"
# and exits zero.
#
# That is the worst possible failure for a gate: with --allow-empty it is indistinguishable
# from a PR that legitimately touched nothing mutated, so it reads as a pass. CI is not
# affected (actions/checkout produces a plain clone), which is exactly why this would never
# be caught there — only by someone iterating locally who trusts a green run.
#
# A full sweep in a worktree is fine; only the diff filter is broken. Hence the guard is on
# --since alone.
for arg in ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}; do
  if [[ $arg == --since* && -f .git ]]; then
    cat >&2 <<'MSG'
error: --since cannot be used from a linked git worktree.

Stryker resolves the diff against the main checkout, not this worktree, so no changed file
matches and every mutant is dropped by the since filter — the run reports 0 mutants and
exits zero, which looks like a pass.

Run it from a normal clone of the repository, or run the full sweep here instead:

    build/mutation-test.sh
MSG
    exit 2
  fi
done

REPO_ROOT="$PWD"
OUT_ROOT="$REPO_ROOT/artifacts/stryker"
mkdir -p "$OUT_ROOT"

# In --since mode, compute the diff once so the per-package loop can skip packages that
# have no changed .cs source files.  Stryker computes the same merge-base internally
# (via LibGit2Sharp), so the skip decision and Stryker's own mutation filter can never
# disagree about which files count.
#
# If the diff cannot be determined for any reason, SINCE_CHANGED_FILES_KNOWN stays false
# and every package runs — the failure mode is "do the work", not "skip it silently".
SINCE_CHANGED_FILES_KNOWN=false
SINCE_CHANGED_FILES=""
if [[ -n "$SINCE_REF" ]]; then
  if SINCE_MERGE_BASE=$(git merge-base "$SINCE_REF" HEAD 2>/dev/null); then
    SINCE_CHANGED_FILES=$(git diff --name-only "$SINCE_MERGE_BASE" 2>/dev/null || true)
    SINCE_CHANGED_FILES_KNOWN=true
  else
    echo "warning: could not compute merge-base for $SINCE_REF; all packages will run" >&2
  fi
fi

# Stryker runs from the test project's own directory, which is its documented layout and the
# only one that reliably pins the test project down. Run from the repo root instead and it
# auto-discovers every test project that references the target — which drags in
# Alberto.Examples.Tests, since that references Alberto.Testing and (through Orders) Alberto.
# The `test-projects` config key does NOT prevent this. The symptom is not a wrong number but
# a hang: eight Examples test hosts sit at 0% CPU forever, and the run never finishes.
TEST_PROJECT_DIR="$REPO_ROOT/tests/Alberto.Tests"
cd "$TEST_PROJECT_DIR"

# Stryker exits non-zero for two unrelated reasons, and they need opposite handling:
#
#   1. The package scored under `break`. That is a real result — the report exists and
#      counts towards the aggregate. Re-running would just burn an hour for the same number.
#   2. Stryker itself crashed. Version 4.16.0 has a race in its MTP runner that throws a
#      NullReferenceException out of MicrosoftTestPlatformRunnerPool.CaptureCoverage before
#      any mutant is tested, then a second one out of Dispose(). It has hit a different
#      package on each sweep so far — Alberto.Telemetry once, Alberto.Commands the next —
#      and it takes that package's report with it, silently dropping it from the aggregate.
#
# The two are told apart by whether a report was written: a crash in coverage capture happens
# before any reporter runs, so case 2 leaves no mutation-report.json. Case 2 gets one retry;
# case 1 does not. Without the retry the nightly full run fails most nights on a tool bug
# rather than a regression, which is the fastest way to teach everyone to ignore it.
#
# Neither outcome may abort the loop — a single failure would hide every score after it, and
# the gate we actually want is on the aggregate.
failed=()      # genuinely below threshold
crashed=()     # Stryker died, no report, retry exhausted
for proj in "${PACKAGES[@]}"; do
  name="${proj%.csproj}"
  report="$OUT_ROOT/$name/reports/mutation-report.json"

  # In --since mode, skip packages with no changed .cs files under src/<name>/.  Stryker
  # would spin up, build the project, run coverage, find nothing to mutate, and log
  # "unable to calculate a mutation score" — several minutes of dead time that is the
  # proximate cause of the diff job timing out on PRs that touch a single package.
  #
  # The skip is always announced in the log so "we did not look" is never silent.
  # It is suppressed when the changed-file set could not be determined (SINCE_CHANGED_FILES_KNOWN
  # false), because the safe failure mode is to run the package, not to drop it quietly.
  if [[ $SINCE_CHANGED_FILES_KNOWN == true ]]; then
    if ! printf '%s\n' "$SINCE_CHANGED_FILES" | grep -q "^src/$name/.*\.cs$"; then
      echo "=== $name — skipped (no changed .cs files under src/$name/)"
      continue
    fi
  fi

  rm -f "$report"

  # Plain `if`s rather than `[[ ... ]] && cmd`: under `set -e` an and-list whose test fails
  # is itself a failed statement, so the shorthand would abort the whole run on the first
  # first-attempt package.
  for attempt in 1 2; do
    if [[ $attempt -eq 1 ]]; then
      echo "=== $name"
    else
      echo "=== $name (retry — Stryker produced no report)"
    fi

    # ${a[@]+"${a[@]}"} rather than a bare "${a[@]}": macOS ships bash 3.2, where expanding an
    # empty array under `set -u` is an unbound-variable error rather than nothing.
    run_status=0
    dotnet stryker \
      -f "$REPO_ROOT/stryker-config.json" \
      -p "$proj" \
      -O "$OUT_ROOT/$name" \
      --skip-version-check \
      ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} || run_status=$?

    if [[ -f $report ]]; then
      # A report means Stryker completed. Non-zero here is a below-threshold score.
      if [[ $run_status -ne 0 ]]; then
        failed+=("$name")
      fi
      break
    fi

    # No report. Under --since this is also the legitimate "nothing to mutate" case, which
    # exits zero — so only a non-zero exit counts as a crash worth retrying.
    if [[ $run_status -eq 0 ]]; then
      break
    fi
    if [[ $attempt -eq 2 ]]; then
      crashed+=("$name")
    fi
  done
done

echo
echo "=== aggregate"
# `|| status=$?` rather than a bare call: under `set -e` a non-zero exit from the
# summary would abort the script before the failure list below could be printed.
status=0
python3 "$REPO_ROOT/build/mutation-summary.py" "$OUT_ROOT" --allow-empty || status=$?

if [[ ${#failed[@]} -gt 0 ]]; then
  echo "Packages Stryker scored below its own break threshold: ${failed[*]}"
fi

# A crashed package is not a low score, it is a missing number — and the aggregate above
# was computed without it, so it reads higher than it should. Say so loudly rather than
# letting a silently-dropped package pass for a clean run.
if [[ ${#crashed[@]} -gt 0 ]]; then
  echo "Stryker crashed twice and produced no report for: ${crashed[*]}"
  echo "The aggregate above EXCLUDES those packages and is not comparable to a full run."
  status=1
fi

# The aggregate is what CI gates on, and it is enforced by a separate
# mutation-summary.py --break call in the workflow. This script exits non-zero only
# when the reports could not be read at all, or when a package produced none.
exit $status
