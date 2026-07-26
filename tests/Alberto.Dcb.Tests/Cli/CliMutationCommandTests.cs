using System.CommandLine;
using Alberto.Cli;
using Alberto.Cli.Commands.Ops;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Cli;

/// <summary>
/// Guards the operator contract that every destructive command exposes the common
/// non-interactive confirmation escape hatch.
/// </summary>
public sealed class CliMutationCommandTests
{
    [Fact]
    public void CheckpointRename_RequiresTheSharedYesOption()
    {
        var rename = Named(CheckpointOpsCommand.Build(), "rename");

        rename.Options.Should().Contain(option => option.Name == "--yes");
    }

    [Fact]
    public void TenantLeaseRelease_RequiresTheSharedYesOption()
    {
        var release = Named(TenantOpsCommand.Build(), "release");

        release.Options.Should().Contain(option => option.Name == "--yes");
    }

    [Fact]
    public void RebuildStart_RequiresTheSharedYesOption()
    {
        var start = Named(RebuildCommand.Build(), "start");

        start.Options.Should().Contain(option => option.Name == "--yes");
    }

    [Fact]
    public async Task PlannedMutation_ProbesAllTargets_ThenGatesOnce_ThenAppliesAllTargets()
    {
        var operations = new List<string>();
        var gateCalls = 0;

        var exitCode = await PlannedMutation.RunAsync(
            ["db1", "db2"],
            target =>
            {
                operations.Add($"probe:{target}");
                return Task.FromResult($"plan:{target}");
            },
            (_, _) => throw new Xunit.Sdk.XunitException("No probe should fail."),
            plans =>
            {
                gateCalls++;
                operations.Add("gate");
                plans.Select(plan => plan.Plan).Should().Equal("plan:db1", "plan:db2");
                return null;
            },
            (target, _) =>
            {
                operations.Add($"apply:{target}");
                return Task.CompletedTask;
            },
            (_, _) => throw new Xunit.Sdk.XunitException("No apply should fail."));

        exitCode.Should().Be(0);
        gateCalls.Should().Be(1);
        operations.Should().Equal("probe:db1", "probe:db2", "gate", "apply:db1", "apply:db2");
    }

    [Fact]
    public async Task PlannedMutation_ProbeFailure_StopsBeforeGateAndWrites_AfterProbingAllTargets()
    {
        var operations = new List<string>();

        var exitCode = await PlannedMutation.RunAsync<string, string>(
            ["db1", "db2"],
            target =>
            {
                operations.Add($"probe:{target}");
                return target == "db1"
                    ? Task.FromException<string>(new InvalidOperationException("offline"))
                    : Task.FromResult($"plan:{target}");
            },
            (target, _) => operations.Add($"probe-failed:{target}"),
            _ =>
            {
                operations.Add("gate");
                return null;
            },
            (target, _) =>
            {
                operations.Add($"apply:{target}");
                return Task.CompletedTask;
            },
            (_, _) => { });

        exitCode.Should().Be(1);
        operations.Should().Equal("probe:db1", "probe-failed:db1", "probe:db2");
    }

    [Fact]
    public async Task PlannedMutation_ApplyFailure_DoesNotSkipRemainingPlannedTargets()
    {
        var operations = new List<string>();

        var exitCode = await PlannedMutation.RunAsync(
            ["db1", "db2"],
            target => Task.FromResult($"plan:{target}"),
            (_, _) => { },
            _ => null,
            (target, _) =>
            {
                operations.Add($"apply:{target}");
                return target == "db1"
                    ? Task.FromException(new InvalidOperationException("write failed"))
                    : Task.CompletedTask;
            },
            (target, _) => operations.Add($"apply-failed:{target}"));

        exitCode.Should().Be(1);
        operations.Should().Equal("apply:db1", "apply-failed:db1", "apply:db2");
    }

    private static Command Named(Command parent, string name) =>
        parent.Subcommands.Single(command => command.Name == name);
}
