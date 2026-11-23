using Alberto.Benchmarks.Core;
using Xunit;

namespace Alberto.Tests.Benchmarks;

public class ResultComparerTests
{
    private const string Profile = "ci-3f2a91b8";

    private static Measurement Measure(
        string id, double meanNs, double stdDevNs = 1.0, long allocated = 500) =>
        new(id, new Dictionary<string, string>(), meanNs, stdDevNs, 0.0, allocated);

    private static BenchmarkRun Run(string profileId, params Measurement[] measurements) =>
        new(1, new RunMetadata("2026-07-26T02:12:00Z", "sha", profileId, "0.1.0"), measurements);

    [Fact]
    public void Comparing_across_machine_profiles_is_refused()
    {
        var baseline = Run("ci-3f2a91b8", Measure("A", 100));
        var candidate = Run("local-9f3e0011", Measure("A", 100));

        var act = () => ResultComparer.Compare(baseline, candidate, Thresholds.Default);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Contains("different machine profiles", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unchanged_measurement_is_reported_as_unchanged()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100)),
            Run(Profile, Measure("A", 101)),
            Thresholds.Default);

        Assert.Equal(ComparisonVerdict.Unchanged, Assert.Single(report.Deltas).Verdict);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void A_mean_increase_beyond_the_threshold_and_outside_the_noise_band_regresses()
    {
        // +50% mean, and the 50ns delta dwarfs the 2ns combined stddev band.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 1.0)),
            Run(Profile, Measure("A", 150, stdDevNs: 1.0)),
            Thresholds.Default);

        Assert.Equal(ComparisonVerdict.Regressed, Assert.Single(report.Deltas).Verdict);
        Assert.True(report.HasRegression);
    }

    [Fact]
    public void A_mean_increase_inside_the_noise_band_does_not_regress()
    {
        // +50% mean, but the run is so noisy that the delta sits inside the combined stddev.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 40.0)),
            Run(Profile, Measure("A", 150, stdDevNs: 40.0)),
            Thresholds.Default);

        Assert.Equal(ComparisonVerdict.Unchanged, Assert.Single(report.Deltas).Verdict);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void A_faster_measurement_is_reported_as_improved()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 200, stdDevNs: 1.0)),
            Run(Profile, Measure("A", 100, stdDevNs: 1.0)),
            Thresholds.Default);

        Assert.Equal(ComparisonVerdict.Improved, Assert.Single(report.Deltas).Verdict);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void An_allocation_increase_beyond_its_tighter_threshold_regresses()
    {
        // Mean is unchanged; allocations grow 20% against a 10% gate.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, allocated: 1000)),
            Run(Profile, Measure("A", 100, allocated: 1200)),
            Thresholds.Default);

        Assert.Equal(ComparisonVerdict.Regressed, Assert.Single(report.Deltas).Verdict);
    }

    [Fact]
    public void Allocation_growth_is_not_excused_by_the_noise_band()
    {
        // Allocation counts are near-deterministic, so no stddev gate applies to them.
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 500.0, allocated: 1000)),
            Run(Profile, Measure("A", 100, stdDevNs: 500.0, allocated: 1200)),
            Thresholds.Default);

        Assert.Equal(ComparisonVerdict.Regressed, Assert.Single(report.Deltas).Verdict);
    }

    [Fact]
    public void A_new_measurement_is_reported_but_never_fails_the_run()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100)),
            Run(Profile, Measure("A", 100), Measure("B", 100)),
            Thresholds.Default);

        Assert.Contains(report.Deltas, d => d.Key == "B" && d.Verdict == ComparisonVerdict.Added);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void A_removed_measurement_is_reported_but_never_fails_the_run()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100), Measure("B", 100)),
            Run(Profile, Measure("A", 100)),
            Thresholds.Default);

        Assert.Contains(report.Deltas, d => d.Key == "B" && d.Verdict == ComparisonVerdict.Removed);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void Deltas_are_ordered_worst_first()
    {
        var report = ResultComparer.Compare(
            Run(Profile, Measure("A", 100, stdDevNs: 1.0), Measure("B", 100, stdDevNs: 1.0)),
            Run(Profile, Measure("A", 105, stdDevNs: 1.0), Measure("B", 300, stdDevNs: 1.0)),
            Thresholds.Default);

        Assert.Equal("B", report.Deltas[0].Key);
    }

    [Fact]
    public void The_default_thresholds_are_twenty_percent_mean_and_ten_percent_allocations()
    {
        Assert.Equal(20.0, Thresholds.Default.MeanPercent);
        Assert.Equal(10.0, Thresholds.Default.AllocatedPercent);
    }
}
