namespace Alberto.Benchmarks.Harness;

/// <summary>
/// The seeded store sizes benchmarks run against.
///
/// Applied only where table size can plausibly change the answer. Index and over-scan
/// problems are invisible at 10k and obvious at 1M; appends that never read do not get
/// this axis, because paying for it there would triple runtime for no signal.
/// </summary>
public static class StoreSizes
{
    public const int Small = 10_000;
    public const int Medium = 100_000;
    public const int Large = 1_000_000;
}
