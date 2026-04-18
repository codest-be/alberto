using System.CommandLine;
using Alberto.Cli.Commands.Ops;

namespace Alberto.Cli.Commands;

public static class OpsCommand
{
    public static Command Build()
    {
        var command = new Command("ops", "Operational management commands");

        command.AddCommand(CheckpointOpsCommand.Build());
        command.AddCommand(DeadLetterOpsCommand.Build());
        command.AddCommand(RebuildCommand.Build());
command.AddCommand(TenantOpsCommand.Build());

        return command;
    }
}
