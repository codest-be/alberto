using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class BdnImporterTests
{
    private const string ReportJson = """
    {
      "Title": "AppendBenchmarks",
      "Benchmarks": [
        {
          "Namespace": "Alberto.Dcb.Benchmarks.Workloads",
          "Type": "AppendBenchmarks",
          "Method": "BatchAppend",
          "Parameters": "BatchSize=100, StoreSize=10000",
          "Statistics": { "Mean": 125000.5, "StandardDeviation": 4200.25 },
          "Memory": { "BytesAllocatedPerOperation": 8192 }
        },
        {
          "Namespace": "Alberto.Dcb.Benchmarks.Workloads",
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
        BdnImporter.Import(ReportJson, Metadata).Measurements.Should().HaveCount(2);
    }

    [Fact]
    public void The_measurement_id_is_the_type_and_method()
    {
        var run = BdnImporter.Import(ReportJson, Metadata);

        run.Measurements[0].Id.Should().Be("AppendBenchmarks.BatchAppend");
    }

    [Fact]
    public void Parameters_are_split_into_a_dictionary()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[0];

        measurement.Params.Should().HaveCount(2);
        measurement.Params["BatchSize"].Should().Be("100");
        measurement.Params["StoreSize"].Should().Be("10000");
    }

    [Fact]
    public void An_empty_parameter_string_yields_no_parameters()
    {
        BdnImporter.Import(ReportJson, Metadata).Measurements[1].Params.Should().BeEmpty();
    }

    [Fact]
    public void Statistics_and_allocations_are_carried_across()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[0];

        measurement.MeanNs.Should().Be(125000.5);
        measurement.StdDevNs.Should().Be(4200.25);
        measurement.AllocatedBytes.Should().Be(8192);
    }

    [Fact]
    public void Ops_per_second_is_derived_from_the_mean()
    {
        var measurement = BdnImporter.Import(ReportJson, Metadata).Measurements[1];

        // 2000 ns per op => 500,000 ops/sec
        measurement.OpsPerSec.Should().BeApproximately(500_000.0, 0.01);
    }

    [Fact]
    public void A_report_with_no_benchmarks_is_rejected()
    {
        var act = () => BdnImporter.Import("""{ "Benchmarks": [] }""", Metadata);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no benchmarks*");
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

        run.Measurements.Should().HaveCount(3);
        run.Measurements.Should().Contain(m => m.Id == "QueryBenchmarks.TailRead");
        run.Measurements.Should().Contain(m => m.Id == "AppendBenchmarks.SingleAppend");
    }

    [Fact]
    public void Merging_reports_that_share_a_measurement_key_is_rejected()
    {
        // Silently keeping one would make the comparer's key lookup ambiguous and quietly
        // drop a benchmark from the baseline.
        var act = () => BdnImporter.ImportMany([ReportJson, ReportJson], Metadata);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate*");
    }

    [Fact]
    public void Merging_no_reports_is_rejected()
    {
        var act = () => BdnImporter.ImportMany([], Metadata);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no benchmarks*");
    }
}
