using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

public static class DeadLetterOpsCommand
{
    public static Command Build()
    {
        var command = new Command("dead-letters", "Manage dead letter entries");

        command.AddCommand(BuildDismiss());
        command.AddCommand(BuildRetry());
        command.AddCommand(BuildRetryRewind());

        return command;
    }

    private static Command BuildDismiss()
    {
        var command = new Command("dismiss",
            """
            Remove dead letter entries permanently.

            Examples:
              alberto ops dead-letters dismiss --processor my-processor --dry-run
              alberto ops dead-letters dismiss --processor my-processor --yes
              alberto ops dead-letters dismiss --all --dry-run
              alberto ops dead-letters dismiss --all --yes
            """);

        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var processorOption = new Option<string?>("--processor", "Filter by processor ID");
        var allOption = new Option<bool>("--all", "Dismiss all dead letters (required if --processor not specified)");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would be dismissed without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(processorOption);
        command.AddOption(allOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);

        command.SetHandler(async (string? url, string? schema, string? processor, bool all, bool dryRun, bool yes) =>
        {
            IOutput output = new HumanOutput();

            if (string.IsNullOrWhiteSpace(processor) && !all)
            {
                output.Error("Specify --processor <id> or --all to dismiss dead letters.");
                Environment.Exit(1);
                return;
            }

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var count = await data.CountDeadLettersAsync(processor);
                var scope = processor is not null ? $"processor '{processor}'" : "all processors";

                if (count == 0)
                {
                    output.Text($"No dead letters found for {scope}.");
                    return;
                }

                if (dryRun)
                {
                    output.Text($"[Dry run] Would dismiss {count} dead letter(s) for {scope}.");
                    return;
                }

                if (!yes)
                {
                    var confirmed = AnsiConsole.Confirm(
                        $"Dismiss [bold]{count}[/] dead letter(s) for {scope}?",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                var deleted = await data.DismissDeadLettersAsync(processor);
                output.Text($"Dismissed {deleted} dead letter(s).");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, processorOption, allOption, dryRunOption, yesOption);

        return command;
    }

    private static Command BuildRetry()
    {
        var command = new Command("retry", "Retry dead letter entries");

        var processorOption = new Option<string?>("--processor", "Filter by processor ID");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would be retried without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");

        command.AddOption(processorOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);

        command.SetHandler((string? processor, bool dryRun, bool yes) =>
        {
            var output = new HumanOutput();
            output.Warning("Retry is not implemented in the CLI. Use the admin API to retry dead letters.");
            return Task.CompletedTask;
        }, processorOption, dryRunOption, yesOption);

        return command;
    }

    private static Command BuildRetryRewind()
    {
        var command = new Command("retry-rewind", "Rewind a processor checkpoint to replay from its earliest dead letter position, then clear dead letters");

        var processorIdArgument = new Argument<string>("processor-id", "Processor ID to rewind");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");

        command.AddArgument(processorIdArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(yesOption);

        command.SetHandler(async (string processorId, string? url, string? schema, bool yes) =>
        {
            IOutput output = new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var count = await data.CountDeadLettersAsync(processorId);
                if (count == 0)
                {
                    output.Text($"No dead letters found for processor '{processorId}'.");
                    return;
                }

                if (!yes)
                {
                    var confirmed = AnsiConsole.Confirm(
                        $"Rewind processor '[bold]{processorId}[/]' to replay from its earliest dead letter and clear {count} dead letter(s). Continue?",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                var (rewindPosition, deletedCount) = await data.RetryByRewindAsync(processorId);

                output.Text($"Done. Consumer will replay from position {rewindPosition}.");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, processorIdArgument, urlOption, schemaOption, yesOption);

        return command;
    }
}
