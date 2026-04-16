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
              alberto ops dead-letters dismiss --processor my-processor --yes --json
              alberto ops dead-letters dismiss --all --dry-run
              alberto ops dead-letters dismiss --all --yes
            """);

        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var processorOption = new Option<string?>("--processor", "Filter by processor ID");
        var allOption = new Option<bool>("--all", "Dismiss all dead letters (required if --processor not specified)");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would be dismissed without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(processorOption);
        command.AddOption(allOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string? url, string? schema, string? processor, bool all, bool dryRun, bool yes, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            if (string.IsNullOrWhiteSpace(processor) && !all)
            {
                output.Error("Specify --processor <id> or --all to dismiss dead letters.\n  alberto ops dead-letters dismiss --processor <id> --yes\n  alberto ops dead-letters dismiss --all --yes");
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
                    if (json)
                        output.Json(new { action = "dismiss", dismissed = 0, scope, noOp = true });
                    else
                        output.Text($"No dead letters found for {scope}. No-op.");
                    return;
                }

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "dismiss", count, scope });
                    else
                        output.Text($"[Dry run] Would dismiss {count} dead letter(s) for {scope}.");
                    return;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops dead-letters dismiss {(processor is not null ? $"--processor {processor}" : "--all")} --yes");
                        Environment.Exit(1);
                        return;
                    }

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

                if (json)
                    output.Json(new { action = "dismiss", dismissed = deleted, scope });
                else
                    output.Text($"Dismissed {deleted} dead letter(s).");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, processorOption, allOption, dryRunOption, yesOption, jsonOption);

        return command;
    }

    private static Command BuildRetry()
    {
        var command = new Command("retry",
            """
            Mark dead letter entries for retry. The running processor will reprocess them on its next dead letter retry loop cycle (default: every 1 minute).
            Unlike retry-rewind, this does not change the checkpoint — only the flagged dead letters are reprocessed.

            Examples:
              alberto ops dead-letters retry my-processor --dry-run
              alberto ops dead-letters retry my-processor --yes
              alberto ops dead-letters retry my-processor --yes --json
            """);

        var processorIdArgument = new Argument<string>("processor-id", "Processor ID to retry dead letters for");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would happen without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddArgument(processorIdArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string processorId, string? url, string? schema, bool dryRun, bool yes, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var count = await data.CountDeadLettersAsync(processorId);
                if (count == 0)
                {
                    if (json)
                        output.Json(new { action = "retry", processorId, deadLetters = 0, noOp = true });
                    else
                        output.Text($"No dead letters found for processor '{processorId}'. No-op.");
                    return;
                }

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "retry", processorId, count });
                    else
                        output.Text($"[Dry run] Would mark {count} dead letter(s) for retry for processor '{processorId}'.");
                    return;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops dead-letters retry {processorId} --yes");
                        Environment.Exit(1);
                        return;
                    }

                    var confirmed = AnsiConsole.Confirm(
                        $"Mark [bold]{count}[/] dead letter(s) for retry for processor '[bold]{processorId}[/]'? " +
                        "The processor will reprocess them on its next retry loop cycle.",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return;
                    }
                }

                await data.MarkDeadLettersForRetryAsync(processorId);

                if (json)
                    output.Json(new { action = "retry", processorId, markedForRetry = count });
                else
                    output.Text($"Marked {count} dead letter(s) for retry. The processor will pick them up on its next retry loop cycle.");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, processorIdArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption);

        return command;
    }

    private static Command BuildRetryRewind()
    {
        var command = new Command("retry-rewind",
            """
            Rewind a processor checkpoint to replay from its earliest dead letter position, then clear dead letters.

            Examples:
              alberto ops dead-letters retry-rewind my-processor --dry-run
              alberto ops dead-letters retry-rewind my-processor --yes
              alberto ops dead-letters retry-rewind my-processor --yes --json
            """);

        var processorIdArgument = new Argument<string>("processor-id", "Processor ID to rewind");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would happen without executing");
        var yesOption = new Option<bool>("--yes", "Skip confirmation prompt");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddArgument(processorIdArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string processorId, string? url, string? schema, bool dryRun, bool yes, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var count = await data.CountDeadLettersAsync(processorId);
                if (count == 0)
                {
                    if (json)
                        output.Json(new { action = "retry-rewind", processorId, deadLetters = 0, noOp = true });
                    else
                        output.Text($"No dead letters found for processor '{processorId}'. No-op.");
                    return;
                }

                if (dryRun)
                {
                    if (json)
                        output.Json(new { dryRun = true, action = "retry-rewind", processorId, deadLetterCount = count });
                    else
                        output.Text($"[Dry run] Would rewind processor '{processorId}' to its earliest dead letter and clear {count} dead letter(s).");
                    return;
                }

                if (!yes)
                {
                    if (!AnsiConsole.Profile.Capabilities.Interactive)
                    {
                        output.Error($"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops dead-letters retry-rewind {processorId} --yes");
                        Environment.Exit(1);
                        return;
                    }

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

                if (json)
                    output.Json(new { action = "retry-rewind", processorId, rewindPosition, dismissedDeadLetters = deletedCount });
                else
                    output.Text($"Done. Consumer will replay from position {rewindPosition}. Cleared {deletedCount} dead letter(s).");
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, processorIdArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption);

        return command;
    }
}
