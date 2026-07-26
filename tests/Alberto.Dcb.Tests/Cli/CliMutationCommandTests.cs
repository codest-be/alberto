using System.CommandLine;
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

    private static Command Named(Command parent, string name) =>
        parent.Subcommands.Single(command => command.Name == name);
}
