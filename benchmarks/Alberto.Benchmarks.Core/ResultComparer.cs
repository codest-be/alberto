namespace Alberto.Benchmarks.Core;

/// <summary>
/// Regression gates. Deliberately asymmetric: Postgres in a container on a shared runner is a
/// noisy instrument for timing, while allocation counts stay near-deterministic — so the
/// allocation gate is tighter and is not excused by the noise band.
/// </summary>
public sealed record Thresholds(double MeanPercent, double AllocatedPercent)
{
    public static Thresholds Default { get; } = new(MeanPercent: 20.0, AllocatedPercent: 10.0);
}

public enum ComparisonVerdict
{
    Unchanged,
    Improved,
    Regressed,
    Added,
    Removed,
}

public sealed record MeasurementDelta(
    string Key,
    double? BaselineMeanNs,
    double? CandidateMeanNs,
    double? MeanPercent,
    long? BaselineAllocatedBytes,
    long? CandidateAllocatedBytes,
    double? AllocatedPercent,
    ComparisonVerdict Verdict);

public sealed record ComparisonReport(IReadOnlyList<MeasurementDelta> Deltas)
{
    public bool HasRegression => Deltas.Any(d => d.Verdict == ComparisonVerdict.Regressed);
}

public static class ResultComparer
{
    public static ComparisonReport Compare(
        BenchmarkRun baseline,
        BenchmarkRun candidate,
        Thresholds thresholds)
    {
        // A laptop run diffed against a CI baseline looks like data but is not. Refuse
        // rather than warn — a warning in a log is a warning nobody reads.
        if (!string.Equals(baseline.Run.ProfileId, candidate.Run.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to compare results from different machine profiles: "
                + $"baseline '{baseline.Run.ProfileId}' vs candidate '{candidate.Run.ProfileId}'.");
        }

        var baselineByKey = baseline.Measurements.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var candidateByKey = candidate.Measurements.ToDictionary(m => m.Key, StringComparer.Ordinal);

        var deltas = new List<MeasurementDelta>();

        foreach (var (key, candidateMeasurement) in candidateByKey)
        {
            if (baselineByKey.TryGetValue(key, out var baselineMeasurement))
            {
                deltas.Add(Evaluate(key, baselineMeasurement, candidateMeasurement, thresholds));
            }
            else
            {
                deltas.Add(new MeasurementDelta(
                    key, null, candidateMeasurement.MeanNs, null,
                    null, candidateMeasurement.AllocatedBytes, null,
                    ComparisonVerdict.Added));
            }
        }

        foreach (var (key, baselineMeasurement) in baselineByKey)
        {
            if (!candidateByKey.ContainsKey(key))
            {
                deltas.Add(new MeasurementDelta(
                    key, baselineMeasurement.MeanNs, null, null,
                    baselineMeasurement.AllocatedBytes, null, null,
                    ComparisonVerdict.Removed));
            }
        }

        // Worst first, so a truncated report still shows what matters.
        var ordered = deltas
            .OrderByDescending(d => d.Verdict == ComparisonVerdict.Regressed)
            .ThenByDescending(d => d.MeanPercent ?? double.MinValue)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

        return new ComparisonReport(ordered);
    }

    private static MeasurementDelta Evaluate(
        string key, Measurement baseline, Measurement candidate, Thresholds thresholds)
    {
        var meanPercent = Percent(baseline.MeanNs, candidate.MeanNs);
        var allocatedPercent = Percent(baseline.AllocatedBytes, candidate.AllocatedBytes);

        // Timing must clear BOTH the percentage gate and the combined noise band. On a shared
        // runner the band is what stops a slow neighbour from being reported as a regression.
        var noiseBand = baseline.StdDevNs + candidate.StdDevNs;
        var meanRegressed =
            meanPercent > thresholds.MeanPercent
            && (candidate.MeanNs - baseline.MeanNs) > noiseBand;

        // Allocations get no noise band — they do not drift between runs.
        var allocationRegressed = allocatedPercent > thresholds.AllocatedPercent;

        var verdict = meanRegressed || allocationRegressed
            ? ComparisonVerdict.Regressed
            : meanPercent < -thresholds.MeanPercent
                ? ComparisonVerdict.Improved
                : ComparisonVerdict.Unchanged;

        return new MeasurementDelta(
            key,
            baseline.MeanNs,
            candidate.MeanNs,
            meanPercent,
            baseline.AllocatedBytes,
            candidate.AllocatedBytes,
            allocatedPercent,
            verdict);
    }

    private static double Percent(double baseline, double candidate)
        => baseline == 0 ? 0 : (candidate - baseline) / baseline * 100.0;
}
