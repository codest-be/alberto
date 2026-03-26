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

        var idArgument = new Argument<string>("processor-id", "Processor ID");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

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

        var idArgument = new Argument<string>("processor-id", "Processor ID");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would be reset without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

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

        var idArgument = new Argument<string>("processor-id", "Processor ID");
        var positionArgument = new Argument<long>("position", "Global position to set");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would change without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

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
}
