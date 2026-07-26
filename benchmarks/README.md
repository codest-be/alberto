# Benchmarks

Postgres-backed BenchmarkDotNet suite. Design:
[docs/superpowers/specs/2026-07-26-benchmark-suite-design.md](../docs/superpowers/specs/2026-07-26-benchmark-suite-design.md)

## Running

Everything (needs Docker; takes 30–60 minutes cold):

    dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks

One family:

    dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks -- --anyCategories=append

Against an existing Postgres rather than Testcontainers:

    ALBERTO_BENCH_POSTGRES="Host=localhost;Database=bench;Username=postgres;Password=postgres" \
      dotnet run -c Release --project benchmarks/Alberto.Dcb.Benchmarks

## Comparing

Normalize a BenchmarkDotNet report, then diff it against the committed baseline:

    dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
      --import BenchmarkDotNet.Artifacts/results/<report>-report-full.json --out candidate.json

    dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json

Exit code 1 means a regression. Thresholds: mean +20% (and outside the combined standard
deviation band), allocations +10% (no noise band — allocation counts do not drift).

## Baselines

Results are keyed by machine profile. Comparing across profiles is refused, not warned about,
so your laptop's numbers never silently diff against CI's.

Promotion is manual and deliberate:

    dotnet run --project benchmarks/Alberto.Dcb.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json --accept

CI appends to `history/` on every nightly run but never touches `baseline.json`.
