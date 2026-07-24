using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

public static class CheckpointOpsCommand
{
    public static Command Build()
    {
        var command = new Command("checkpoint", "Manage processor checkpoints");

        command.AddCommand(BuildGet());
        command.AddCommand(BuildReset());
        command.AddCommand(BuildSet());
        command.AddCommand(BuildRename());

        return command;
    }

    private static Command BuildGet()
    {
        var command = new Command("get",
            """
            Show the checkpoint for a specific processor.

            Examples:
              alberto ops checkpoint get my-processor
              alberto ops checkpoint get my-processor --json
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string id, string? url, string? schema, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);
                var checkpoint = await data.GetSingleCheckpointAsync(id);

                if (checkpoint is null)
                {
                    output.Warning($"No checkpoint found for processor '{id}'.");
                    Environment.Exit(1);
                    return;
                }

                if (json)
                {
                    output.Json(new
                    {
                        checkpoint.ProcessorId,
                        checkpoint.LastPosition,
                        updatedAt = checkpoint.UpdatedAt?.ToString("O")
                    });
                }
                else
                {
                    output.Box($"Checkpoint: {id}", new Dictionary<string, string>
                    {
                        ["Processor ID"] = checkpoint.ProcessorId,
                        ["Last Position"] = checkpoint.LastPosition.ToString(),
                        ["Updated At"] = checkpoint.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                    });
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, urlOption, schemaOption, jsonOption);

        return command;
    }

    private static Command BuildReset()
    {
        var command = new Command("reset",
            """
            Delete the checkpoint for a processor, triggering a full replay from the beginning.

            Examples:
              alberto ops checkpoint reset my-processor --dry-run
              alberto ops checkpoint reset my-processor --yes
              alberto ops checkpoint reset my-processor --yes --json
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would be reset without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string id, string? url, string? schema, bool dryRun, bool yes, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);
                var checkpoint = await data.GetSingleCheckpointAsync(id);
                var previousPosition = checkpoint?.LastPosition;

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "reset", processorId = id, previousPosition });
                    else
                        output.Text($"[Dry run] Would reset checkpoint for '{id}' (currently at position {previousPosition?.ToString() ?? "none"}).");
                    return;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops checkpoint reset {id} --yes");
                        Environment.Exit(1);
                        return;
                    }

                    var confirmed = AnsiConsole.Confirm(
                        $"[yellow]Reset checkpoint for processor '[bold]{id}[/]'? This will trigger a full replay.[/]",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                await data.ResetCheckpointAsync(id);

                if (json)
                    output.Json(new { action = "reset", processorId = id, previousPosition });
                else
                    output.Text($"Checkpoint for '{id}' has been reset (was at position {previousPosition?.ToString() ?? "none"}).");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption);

        return command;
    }

    private static Command BuildSet()
    {
        var command = new Command("set",
            """
            Set the checkpoint position for a processor.

            Examples:
              alberto ops checkpoint set my-processor 1000 --dry-run
              alberto ops checkpoint set my-processor 1000 --yes
              alberto ops checkpoint set my-processor 1000 --yes --json
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID" };
        var positionArgument = new Argument<long>("position") { Description = "Global position to set" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would change without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddArgument(positionArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string id, long position, string? url, string? schema, bool dryRun, bool yes, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);
                var checkpoint = await data.GetSingleCheckpointAsync(id);
                var previousPosition = checkpoint?.LastPosition;

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "set", processorId = id, previousPosition, newPosition = position });
                    else
                        output.Text($"[Dry run] Would set checkpoint for '{id}' from {previousPosition?.ToString() ?? "none"} to {position}.");
                    return;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops checkpoint set {id} {position} --yes");
                        Environment.Exit(1);
                        return;
                    }

                    var confirmed = AnsiConsole.Confirm(
                        $"Set checkpoint for '[bold]{id}[/]' to position [bold]{position}[/]?",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                await data.SetCheckpointAsync(id, position);

                if (json)
                    output.Json(new { action = "set", processorId = id, previousPosition, newPosition = position });
                else
                    output.Text($"Checkpoint for '{id}' set to position {position} (was {previousPosition?.ToString() ?? "none"}).");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, positionArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption);

        return command;
    }

    private static Command BuildRename()
    {
        var command = new Command("rename",
            """
            Rename a checkpoint by copying its position to a new processor id and removing the old one.

            Use this after renaming a handler class to carry the stored position to the new derived id,
            preventing a full replay from the beginning.

            Examples:
              alberto ops checkpoint rename --from OldHandlerName --to NewHandlerName
              alberto ops checkpoint rename --module orders --from OldHandlerName --to NewHandlerName
            """);

        var moduleOption = new Option<string?>("--module") { Description = "Module key (for context; shown in startup warnings)" };
        var fromOption = new Option<string?>("--from") { Description = "Old processor id (the orphaned checkpoint key)" };
        var toOption = new Option<string?>("--to") { Description = "New processor id (the current handler's derived id)" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };

        command.AddOption(moduleOption);
        command.AddOption(fromOption);
        command.AddOption(toOption);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);

        command.SetHandler(async (string? module, string? from, string? to, string? url, string? schema) =>
        {
            if (string.IsNullOrWhiteSpace(from))
            {
                Console.Error.WriteLine("--from is required.");
                Environment.Exit(1);
                return;
            }

            if (string.IsNullOrWhiteSpace(to))
            {
                Console.Error.WriteLine("--to is required.");
                Environment.Exit(1);
                return;
            }

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var sourceCheckpoint = await data.GetSingleCheckpointAsync(from);
                if (sourceCheckpoint is null)
                {
                    var moduleHint = module is not null ? $" in module '{module}'" : string.Empty;
                    Console.Error.WriteLine($"No checkpoint named '{from}' exists{moduleHint}.");
                    Environment.Exit(1);
                    return;
                }

                var destinationCheckpoint = await data.GetSingleCheckpointAsync(to);
                if (destinationCheckpoint is not null)
                {
                    var moduleHint = module is not null ? $" --module {module}" : string.Empty;
                    Console.Error.WriteLine(
                        $"'{to}' already has a checkpoint at position {destinationCheckpoint.LastPosition}. " +
                        "Reset it first if you really mean to overwrite it: " +
                        $"alberto ops checkpoint reset {to} --yes{moduleHint}");
                    Environment.Exit(1);
                    return;
                }

                await data.SetCheckpointAsync(to, sourceCheckpoint.LastPosition);
                await data.ResetCheckpointAsync(from);

                Console.WriteLine($"Renamed checkpoint '{from}' to '{to}' at position {sourceCheckpoint.LastPosition}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(1);
            }
        }, moduleOption, fromOption, toOption, urlOption, schemaOption);

        return command;
    }
}
