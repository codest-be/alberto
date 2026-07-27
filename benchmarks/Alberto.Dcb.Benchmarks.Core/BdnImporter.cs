using System.Text.Json;

namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// Projects BenchmarkDotNet's full JSON report into the normalized <see cref="BenchmarkRun"/>.
///
/// BDN's own schema is a reporting format, not a stable contract, so this is the single
/// place that knows its shape. Everything downstream sees only normalized measurements.
/// </summary>
public static class BdnImporter
{
    public static BenchmarkRun Import(string bdnJson, RunMetadata metadata)
        => ImportMany([bdnJson], metadata);

    /// <summary>
    /// Merges several reports into one run. A full nightly run spans multiple benchmark
    /// classes and BenchmarkDotNet writes one report per class, so merging is the normal
    /// path — importing a single report is the special case.
    /// </summary>
    public static BenchmarkRun ImportMany(IEnumerable<string> bdnJsonDocuments, RunMetadata metadata)
    {
        var measurements = new List<Measurement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bdnJson in bdnJsonDocuments)
        {
            using var document = JsonDocument.Parse(bdnJson);

            if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks)
                || benchmarks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var benchmark in benchmarks.EnumerateArray())
            {
                var measurement = ToMeasurement(benchmark);

                // Two measurements sharing a key would make the comparer's lookup ambiguous
                // and silently drop one from the baseline.
                if (!seen.Add(measurement.Key))
                {
                    throw new InvalidOperationException(
                        $"Duplicate measurement key '{measurement.Key}' across BenchmarkDotNet reports.");
                }

                measurements.Add(measurement);
            }
        }

        if (measurements.Count == 0)
        {
            throw new InvalidOperationException(
                "BenchmarkDotNet reports contained no benchmarks. The run produced no results.");
        }

        return new BenchmarkRun(BenchmarkRun.CurrentSchemaVersion, metadata, measurements);
    }

    private static Measurement ToMeasurement(JsonElement benchmark)
    {
        var type = benchmark.GetProperty("Type").GetString() ?? "Unknown";
        var method = benchmark.GetProperty("Method").GetString() ?? "Unknown";

        var statistics = benchmark.GetProperty("Statistics");
        var meanNs = statistics.GetProperty("Mean").GetDouble();
        var stdDevNs = statistics.TryGetProperty("StandardDeviation", out var sd) ? sd.GetDouble() : 0.0;

        var allocated = 0L;
        if (benchmark.TryGetProperty("Memory", out var memory)
            && memory.ValueKind == JsonValueKind.Object
            && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
            && bytes.ValueKind == JsonValueKind.Number)
        {
            allocated = bytes.GetInt64();
        }

        var parameters = ParseParameters(
            benchmark.TryGetProperty("Parameters", out var p) ? p.GetString() : null);

        // A zero mean would be a degenerate result; guard rather than divide by zero.
        var opsPerSec = meanNs > 0 ? 1_000_000_000.0 / meanNs : 0.0;

        return new Measurement($"{type}.{method}", parameters, meanNs, stdDevNs, opsPerSec, allocated);
    }

    /// <summary>
    /// BDN renders parameters as a flat "Name=Value&amp;Name=Value" string. The separator is
    /// an ampersand, not a comma — confirmed against real 0.14.0 report output, not the docs.
    /// </summary>
    private static Dictionary<string, string> ParseParameters(string? parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(parameters))
        {
            return result;
        }

        foreach (var pair in parameters.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        return result;
    }
}
