using System.CommandLine;
using Alberto.Cli.Commands;

var rootCommand = new RootCommand("alberto — operational management CLI for Alberto DCB Event Store");

rootCommand.AddCommand(StatusCommand.Build());
rootCommand.AddCommand(SystemCommand.Build());
rootCommand.AddCommand(ProcessorCommand.Build());
rootCommand.AddCommand(CheckpointsCommand.Build());
rootCommand.AddCommand(DeadLettersCommand.Build());
rootCommand.AddCommand(EventsCommand.Build());
rootCommand.AddCommand(ProjectionsCommand.Build());
rootCommand.AddCommand(TenantsCommand.Build());
rootCommand.AddCommand(ShardsCommand.Build());
rootCommand.AddCommand(OpsCommand.Build());

return await rootCommand.InvokeAsync(args);
