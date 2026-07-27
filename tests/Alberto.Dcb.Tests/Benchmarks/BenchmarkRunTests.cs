using Alberto.Dcb.Benchmarks.Core;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Benchmarks;

public class BenchmarkRunTests
{
    private static Measurement Measure(string id, params (string Key, string Value)[] parameters) => new(
        Id: id,
        Params: parameters.ToDictionary(p => p.Key, p => p.Value),
        MeanNs: 1200.0,
        StdDevNs: 40.0,
        OpsPerSec: 833_333.0,
        AllocatedBytes: 512);

    [Fact]
    public void A_measurement_key_combines_the_id_and_its_parameters()
    {
        var measurement = Measure("Append.Batch", ("BatchSize", "100"), ("StoreSize", "10000"));

        measurement.Key.Should().Be("Append.Batch[BatchSize=100,StoreSize=10000]");
    }

    [Fact]
    public void Measurement_keys_are_stable_regardless_of_parameter_ordering()
    {
        var forward = Measure("Append.Batch", ("BatchSize", "100"), ("StoreSize", "10000"));
        var reversed = Measure("Append.Batch", ("StoreSize", "10000"), ("BatchSize", "100"));

        forward.Key.Should().Be(reversed.Key);
    }

    [Fact]
    public void A_measurement_without_parameters_keys_on_its_id_alone()
    {
        Measure("Query.StreamAll").Key.Should().Be("Query.StreamAll");
    }

    [Fact]
    public void A_run_round_trips_through_json()
    {
        var original = new BenchmarkRun(
            SchemaVersion: 1,
            Run: new RunMetadata("2026-07-26T02:12:00Z", "a1b2c3d", "ci-3f2a91b8", "0.1.0"),
            Measurements: [Measure("Append.Single"), Measure("Query.StreamAll")]);

        var restored = BenchmarkRun.FromJson(original.ToJson());

        restored.SchemaVersion.Should().Be(1);
        restored.Run.ProfileId.Should().Be("ci-3f2a91b8");
        restored.Run.GitSha.Should().Be("a1b2c3d");
        restored.Measurements.Should().HaveCount(2);
        restored.Measurements[0].MeanNs.Should().Be(1200.0);
        restored.Measurements[0].AllocatedBytes.Should().Be(512);
    }

    [Fact]
    public void Json_is_written_indented_so_diffs_are_readable()
    {
        var run = new BenchmarkRun(
            SchemaVersion: 1,
            Run: new RunMetadata("2026-07-26T02:12:00Z", "a1b2c3d", "ci-3f2a91b8", "0.1.0"),
            Measurements: [Measure("Append.Single")]);

        run.ToJson().Should().Contain("\n  ");
    }
}
