using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alberto.Benchmarks.Core;

/// <summary>Provenance for one benchmark run.</summary>
public sealed record RunMetadata(
    string Timestamp,
    string GitSha,
    string ProfileId,
    string AlbertoVersion);

/// <summary>
/// One measured case. Every producer — the BenchmarkDotNet importer, the macro throughput
/// harness, and (later) the Marten parity harness — projects into this shape, which is what
/// lets a single comparer handle all of them.
/// </summary>
public sealed record Measurement(
    string Id,
    IReadOnlyDictionary<string, string> Params,
    double MeanNs,
    double StdDevNs,
    double OpsPerSec,
    long AllocatedBytes)
{
    /// <summary>
    /// Identity used to pair a candidate measurement with its baseline. Parameters are sorted
    /// so a reordering in the producer does not orphan the whole history.
    /// </summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            if (Params.Count == 0)
            {
                return Id;
            }

            var parts = Params
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}");

            return $"{Id}[{string.Join(',', parts)}]";
        }
    }
}

/// <summary>A complete run: provenance plus every measurement it produced.</summary>
public sealed record BenchmarkRun(
    int SchemaVersion,
    RunMetadata Run,
    IReadOnlyList<Measurement> Measurements)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BenchmarkRun FromJson(string json)
        => JsonSerializer.Deserialize<BenchmarkRun>(json, Options)
           ?? throw new InvalidOperationException("Benchmark result JSON deserialized to null.");
}
