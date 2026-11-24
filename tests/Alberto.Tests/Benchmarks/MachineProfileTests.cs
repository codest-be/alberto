using Alberto.Benchmarks.Core;
using Xunit;

namespace Alberto.Tests.Benchmarks;

public class MachineProfileTests
{
    private static MachineProfile Sample(string label = "ci", int cores = 8) => new(
        Label: label,
        Os: "linux",
        Architecture: "X64",
        CpuModel: "AMD EPYC 7763",
        LogicalCores: cores,
        TotalMemoryBytes: 16_000_000_000L,
        DotnetVersion: "10.0.0",
        PostgresImage: "postgres:16-alpine",
        ExternalPostgres: false);

    [Fact]
    public void Identical_hardware_produces_an_identical_profile_id()
    {
        Assert.Equal(Sample().ProfileId, Sample().ProfileId);
    }

    [Fact]
    public void A_different_core_count_produces_a_different_profile_id()
    {
        Assert.NotEqual(Sample(cores: 16).ProfileId, Sample(cores: 8).ProfileId);
    }

    [Fact]
    public void The_profile_id_is_prefixed_with_the_label()
    {
        Assert.StartsWith("ci-", Sample(label: "ci").ProfileId);
    }

    [Fact]
    public void The_profile_id_is_filesystem_safe()
    {
        Assert.Matches("^[a-z0-9-]+$", Sample().ProfileId);
    }

    [Fact]
    public void Capture_reads_the_running_machine()
    {
        var profile = MachineProfile.Capture();

        Assert.True(profile.LogicalCores > 0);
        Assert.False(string.IsNullOrWhiteSpace(profile.DotnetVersion));
        Assert.False(string.IsNullOrWhiteSpace(profile.ProfileId));
    }
}
