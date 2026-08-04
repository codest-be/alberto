#!/usr/bin/env bash
# Collects line/branch coverage for the shipped libraries and renders a report.
#
# Unlike the mutation run, this runs the WHOLE suite — Postgres-backed classes included.
# Mutation testing has to exclude them because it re-runs the suite per mutant; coverage
# runs the suite once, and the integration tests are ten seconds. Excluding them here
# would report Alberto.Postgres as near-uncovered, which is false.
#
# Usage:
#   build/coverage.sh              # collect + report
#   build/coverage.sh --no-build   # reuse an existing Release build
set -euo pipefail

cd "$(dirname "$0")/.."
. build/build-output-lock.sh

OUT="artifacts/coverage"
rm -rf "$OUT"
mkdir -p "$OUT"

# A failing suite must not skip the report. The old `set -e` exit here meant a single
# failed test left no Cobertura.xml, so the coverage summary printed nothing and the
# artifact upload failed too — two red steps hiding the one that mattered. The status is
# carried to the end instead: the report is rendered from whatever was collected, and
# the script still exits non-zero.
status=0
dotnet test tests/Alberto.Tests/Alberto.Tests.csproj \
  -c Release \
  "$@" \
  --settings build/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --logger "trx;LogFileName=Alberto.Tests.trx" \
  --results-directory "$OUT/raw" || status=$?

# Tolerant for the same reason: a run that died before collecting anything still has a
# test summary worth printing, and reportgenerator's "no reports" error on top of it
# would only bury it.
dotnet reportgenerator \
  -reports:"$OUT/raw/**/coverage.cobertura.xml" \
  -targetdir:"$OUT/report" \
  -reporttypes:"Html;Cobertura;TextSummary;MarkdownSummaryGithub" || true

echo
python3 build/test-summary.py "$OUT/raw"/*.trx
echo
python3 build/coverage-summary.py "$OUT/report/Cobertura.xml" || status=1

exit "$status"
