using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

public static class RebuildCommand
{
    public static Command Build()
    {
        var command = new Command("rebuild",
            """
            Reset a processor checkpoint to trigger a full rebuild from the beginning.

            Examples:
              alberto ops rebuild my-processor --dry-run
              alberto ops rebuild my-processor --yes
              alberto ops rebuild my-processor --yes --json
              alberto ops rebuild my-processor --yes --operator "deploy-script"
            """);

        var idArgument = new Argument<string>("processor-id") { Description = "Processor ID to rebuild" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var operatorOption = new Option<string?>("--operator") { Description = "Operator name to include in output" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would happen without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(operatorOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string id, string? url, string? schema, string? operatorName, bool dryRun, bool yes, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);
            var resolvedOperator = ConnectionResolver.ResolveOperator(operatorName);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var admin = new PostgresAdminDataAccess(dataSource, schemaName);
                var checkpointStore = new PostgresCheckpointStore(dataSource, schemaName);
                var checkpoint = await admin.GetSingleCheckpointAsync(id);
                var previousPosition = checkpoint?.LastPosition;

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "rebuild", processorId = id, previousPosition, @operator = resolvedOperator });
                    else
                        output.Text($"[Dry run] Would rebuild processor '{id}' (currently at position {previousPosition?.ToString() ?? "none"}).");
                    return;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops rebuild {id} --yes");
                        Environment.Exit(1);
                        return;
                    }

                    var confirmed = AnsiConsole.Confirm(
                        $"[yellow]Rebuild processor '[bold]{id}[/]'? This will reset the checkpoint and trigger a full replay.[/]",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                await checkpointStore.ResetAsync(id);

                if (json)
                    output.Json(new { action = "rebuild", processorId = id, previousPosition, @operator = resolvedOperator });
                else
                {
                    output.Text($"Rebuild initiated for processor '{id}' (was at position {previousPosition?.ToString() ?? "none"}).");
                    output.Warning("Checkpoint reset. The processor will replay all events from the beginning on next start.\nNote: existing projection state rows are NOT cleared, by this command or by anything else. Rebuild orchestration is not implemented — replayed events are applied on top of the current state. Clear the projection tables yourself if the projection is not idempotent.");
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, urlOption, schemaOption, operatorOption, dryRunOption, yesOption, jsonOption);

        return command;
    }
}
