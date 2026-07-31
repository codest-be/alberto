using Alberto.Benchmarks.Core;
using FluentAssertions;
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
        Sample().ProfileId.Should().Be(Sample().ProfileId);
    }

    [Fact]
    public void A_different_core_count_produces_a_different_profile_id()
    {
        Sample(cores: 8).ProfileId.Should().NotBe(Sample(cores: 16).ProfileId);
    }

    [Fact]
    public void The_profile_id_is_prefixed_with_the_label()
    {
        Sample(label: "ci").ProfileId.Should().StartWith("ci-");
    }

    [Fact]
    public void The_profile_id_is_filesystem_safe()
    {
        Sample().ProfileId.Should().MatchRegex("^[a-z0-9-]+$");
    }

    [Fact]
    public void Capture_reads_the_running_machine()
    {
        var profile = MachineProfile.Capture();

        profile.LogicalCores.Should().BeGreaterThan(0);
        profile.DotnetVersion.Should().NotBeNullOrWhiteSpace();
        profile.ProfileId.Should().NotBeNullOrWhiteSpace();
    }
}
