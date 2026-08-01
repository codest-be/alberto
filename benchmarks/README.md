# Benchmarks

Postgres-backed BenchmarkDotNet suite. Latest results and interpretation:
[docs/benchmarks/results.md](../docs/benchmarks/results.md)

## Running

Everything (needs Docker; takes 30–60 minutes cold). The `--filter '*'` is required —
without a filter BenchmarkDotNet prompts for a selection instead of running:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --filter '*'

Part of that time is warm-up: every case drives its own measured method up to 2000 times, or
15 seconds, before BenchmarkDotNet starts timing. It is load-bearing rather than padding —
see [Harness/Warmup.cs](Alberto.Benchmarks/Harness/Warmup.cs) for what it fixes and the
measurements behind the two constants.

One family:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --anyCategories=append

Against an existing Postgres rather than Testcontainers:

    ALBERTO_BENCH_POSTGRES="Host=localhost;Database=bench;Username=postgres;Password=postgres" \
      dotnet run -c Release --project benchmarks/Alberto.Benchmarks -- --filter '*'

## Comparing

Normalize a BenchmarkDotNet report, then diff it against the committed baseline. Point
`--import` at the whole results directory — a full run writes one report per benchmark
class, and importing a single file would compare a fraction of the suite:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks.Compare -- \
      --import BenchmarkDotNet.Artifacts/results --postgres-image postgres:16-alpine \
      --out candidate.json

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json

`--postgres-image` is required, and must name the image the run actually used
(`postgres:16-alpine`, from `BenchmarkDatabase`) — it is part of the machine profile, so
importing with the wrong value produces a profile no baseline matches. Use
`--external-postgres` instead when the run went against `ALBERTO_BENCH_POSTGRES`.

Exit code 1 means a regression. Thresholds: mean +20% (and outside the combined standard
deviation band), allocations +10% (no noise band — allocation counts do not drift).

## Baselines

Results are keyed by machine profile. Comparing across profiles is refused, not warned about,
so your laptop's numbers never silently diff against CI's.

Promotion is manual and deliberate:

    dotnet run -c Release --project benchmarks/Alberto.Benchmarks.Compare -- \
      --baseline benchmarks/results/<profileId>/baseline.json --candidate candidate.json --accept

CI appends to `history/` on every nightly run but never touches `baseline.json`.
