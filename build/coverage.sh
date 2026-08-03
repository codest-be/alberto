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

dotnet test tests/Alberto.Tests/Alberto.Tests.csproj \
  -c Release \
  "$@" \
  --settings build/coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$OUT/raw"

dotnet reportgenerator \
  -reports:"$OUT/raw/**/coverage.cobertura.xml" \
  -targetdir:"$OUT/report" \
  -reporttypes:"Html;Cobertura;TextSummary;MarkdownSummaryGithub"

echo
python3 build/coverage-summary.py "$OUT/report/Cobertura.xml"
