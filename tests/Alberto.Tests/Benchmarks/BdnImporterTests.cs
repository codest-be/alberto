using Alberto.Benchmarks.Core;
using Xunit;

namespace Alberto.Tests.Benchmarks;

public class BdnImporterTests
{
    private const string ReportJson = """
    {
      "Title": "AppendBenchmarks",
      "Benchmarks": [
        {
          "Namespace": "Alberto.Benchmarks.Workloads",
          "Type": "AppendBenchmarks",
          "Method": "BatchAppend",
          "Parameters": "BatchSize=100&StoreSize=10000",
          "Statistics": { "Mean": 125000.5, "StandardDeviation": 4200.25 },
          "Memory": { "BytesAllocatedPerOperation": 8192 }
        },
        {
          "Namespace": "Alberto.Benchmarks.Workloads",
          "Type": "AppendBenchmarks",
          "Method": "SingleAppend",
          "Parameters": "",
          "Statistics": { "Mean": 2000.0, "StandardDeviation": 50.0 },
          "Memory": { "BytesAllocatedPerOperation": 512 }
        }
      ]
    }
    """;

    private static readonly RunMetadata Metadata =
        new("2026-07-26T02:12:00Z", "a1b2c3d", "ci-3f2a91b8", "0.1.0");

    [Fact]
    public void Every_benchmark_becomes_a_measurement()
    {
        Assert.Equal(2, BdnImporter.Import(ReportJson, Metadata).Measurements.Count());
    }

    [Fact]
    public void The_measurement_id_is_the_type_and_method()
    {
        var run = BdnImporter.Import(ReportJson, Metadata);

        Assert.Equal("AppendBenchmarks.BatchAppend", run.Measurements[0].Id);
    }

    [Fact]
    public void Parameters_are_split_into_a_dictionary()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[0];

        Assert.Equal(2, measurement.Params.Count());
        Assert.Equal("100", measurement.Params["BatchSize"]);
        Assert.Equal("10000", measurement.Params["StoreSize"]);
    }

    [Fact]
    public void An_empty_parameter_string_yields_no_parameters()
    {
        Assert.Empty(BdnImporter.Import(ReportJson, Metadata).Measurements[1].Params);
    }

    [Fact]
    public void Statistics_and_allocations_are_carried_across()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[0];

        Assert.Equal(125000.5, measurement.MeanNs);
        Assert.Equal(4200.25, measurement.StdDevNs);
        Assert.Equal(8192, measurement.AllocatedBytes);
    }

    [Fact]
    public void Ops_per_second_is_derived_from_the_mean()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[1];

        // 2000 ns per op => 500,000 ops/sec
        Assert.InRange(measurement.OpsPerSec, 500_000.0 - 0.01, 500_000.0 + 0.01);
    }

    [Fact]
    public void A_report_with_no_benchmarks_is_rejected()
    {
        var act = () => BdnImporter.Import("""{ "Benchmarks": [] }""", Metadata);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Contains("no benchmarks", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A run over several benchmark classes writes one report per class, so a full nightly
    // run must merge them into a single result document.

    private const string SecondReportJson = """
    {
      "Title": "QueryBenchmarks",
      "Benchmarks": [
        {
          "Type": "QueryBenchmarks",
          "Method": "TailRead",
          "Parameters": "StoreSize=10000",
          "Statistics": { "Mean": 900.0, "StandardDeviation": 10.0 },
          "Memory": { "BytesAllocatedPerOperation": 256 }
        }
      ]
    }
    """;

    [Fact]
    public void Several_reports_merge_into_one_run()
    {
        var run = BdnImporter.ImportMany([ReportJson, SecondReportJson], Metadata);

        Assert.Equal(3, run.Measurements.Count());
        Assert.Contains(run.Measurements, m => m.Id == "QueryBenchmarks.TailRead");
        Assert.Contains(run.Measurements, m => m.Id == "AppendBenchmarks.SingleAppend");
    }

    [Fact]
    public void Merging_reports_that_share_a_measurement_key_is_rejected()
    {
        // Silently keeping one would make the comparer's key lookup ambiguous and quietly
        // drop a benchmark from the baseline.
        var act = () => BdnImporter.ImportMany([ReportJson, ReportJson], Metadata);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Contains("Duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merging_no_reports_is_rejected()
    {
        var act = () => BdnImporter.ImportMany([], Metadata);

        var exception = Assert.ThrowsAny<InvalidOperationException>(act);
        Assert.Contains("no benchmarks", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
