using System.CommandLine;
using Alberto.Admin;
using Alberto.Postgres;
using Npgsql;

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
              alberto ops dead-letters dismiss --all --shard db2 --yes
              alberto ops dead-letters dismiss --all --all-shards --yes
            """);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var processorOption = new Option<string?>("--processor") { Description = "Filter by processor ID" };
        var allOption = new Option<bool>("--all") { Description = "Dismiss all dead letters (required if --processor not specified)" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would be dismissed without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddOption(processorOption);
        command.AddOption(allOption);
        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler((string? url, string? schema, string? processor, bool all, bool dryRun, bool yes, bool json, string? shard, bool allShards) =>
        {
            var session = new CliSession(json);
            return HandleDismissAsync(url, schema, processor, all, dryRun, yes, json, shard, allShards, session);
        }, urlOption, schemaOption, processorOption, allOption, dryRunOption, yesOption, jsonOption, shardOption, allShardsOption);

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
              alberto ops dead-letters retry my-processor --all-shards --yes
            """);

        var processorIdArgument = new Argument<string>("processor-id") { Description = "Processor ID to retry dead letters for" };
        command.AddArgument(processorIdArgument);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would happen without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler((string processorId, string? url, string? schema, bool dryRun, bool yes, bool json, string? shard, bool allShards) =>
        {
            var session = new CliSession(json);
            return HandleRetryAsync(processorId, url, schema, dryRun, yes, json, shard, allShards, session);
        }, processorIdArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption, shardOption, allShardsOption);

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
              alberto ops dead-letters retry-rewind my-processor --all-shards --yes

            Each database rewinds to its own earliest dead letter, so a sharded run produces one
            rewind position per shard.
            """);

        var processorIdArgument = new Argument<string>("processor-id") { Description = "Processor ID to rewind" };
        command.AddArgument(processorIdArgument);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Show what would happen without executing" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddOption(dryRunOption);
        command.AddOption(yesOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler((string processorId, string? url, string? schema, bool dryRun, bool yes, bool json, string? shard, bool allShards) =>
        {
            var session = new CliSession(json);
            return HandleRetryRewindAsync(processorId, url, schema, dryRun, yes, json, shard, allShards, session);
        }, processorIdArgument, urlOption, schemaOption, dryRunOption, yesOption, jsonOption, shardOption, allShardsOption);

        return command;
    }

    internal static Task<int> HandleDismissAsync(
        string? url, string? schema, string? processor, bool all, bool dryRun, bool yes, bool json,
        string? shard, bool allShards, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;

            if (string.IsNullOrWhiteSpace(processor) && !all)
            {
                output.Error("Specify --processor <id> or --all to dismiss dead letters.\n  alberto ops dead-letters dismiss --processor <id> --yes\n  alberto ops dead-letters dismiss --all --yes");
                return 1;
            }

            var targets = session.MutationTargets(shard, allShards, url, schema);
            var scope = processor is not null ? $"processor '{processor}'" : "all processors";

            // Counted everywhere before anything is dismissed, so the one prompt can state the
            // whole run's total rather than asking once per database.
            var counts = await ShardRun.ProbeAsync(targets, (dataSource, target) =>
                CountAsync(dataSource, target.Schema, processor));
            var probeFailed = ShardRun.ReportFailures(output, counts);
            var total = counts.Where(r => r.Succeeded).Sum(r => r.Value);

            if (total == 0)
            {
                if (json)
                    output.Json(new { action = "dismiss", dismissed = 0, scope, noOp = true });
                else
                    output.Text($"No dead letters found for {scope}{ShardRun.Scope(targets)}. No-op.");

                return probeFailed ? 1 : 0;
            }

            if (dryRun)
            {
                if (json)
                    output.Json(new { dryRun = true, action = "dismiss", count = total, scope });
                else
                    output.Text($"[Dry run] Would dismiss {total} dead letter(s) for {scope}{ShardRun.Scope(targets)}.");

                return probeFailed ? 1 : 0;
            }

            if (session.Confirm(yes,
                $"Dismiss [bold]{total}[/] dead letter(s) for {scope}{ShardRun.Scope(targets)}?",
                $"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops dead-letters dismiss {(processor is not null ? $"--processor {processor}" : "--all")} --yes") is { } confirmCode)
            {
                return confirmCode;
            }

            var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
            {
                IAdminOperator operations = new PostgresAdminOperator(dataSource, target.Schema);
                var dismissed = !string.IsNullOrWhiteSpace(processor)
                    ? await operations.ClearDeadLettersForProcessorAsync(processor, CliSession.OperatorId)
                    : await operations.ClearAllDeadLettersAsync(CliSession.OperatorId);

                if (json)
                    output.Json(new { action = "dismiss", shard = target.ShardId, dismissed, scope });
                else
                    output.Text($"Dismissed {dismissed} dead letter(s).");
            });

            return (failed || probeFailed) ? 1 : 0;
        });

    /// <summary>
    /// How many dead letters one database holds for the given scope. Per-processor goes through
    /// <c>IDeadLetterStore</c>; the whole-store count is only on the admin surface.
    /// </summary>
    private static async Task<int> CountAsync(NpgsqlDataSource dataSource, string schema, string? processor)
    {
        if (!string.IsNullOrWhiteSpace(processor))
            return await new PostgresDeadLetterStore(dataSource, schema).CountAsync(processor);

        return await new PostgresAdminDataAccess(dataSource, schema).CountAllDeadLettersAsync();
    }

    internal static Task<int> HandleRetryAsync(
        string processorId, string? url, string? schema, bool dryRun, bool yes, bool json,
        string? shard, bool allShards, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;
            var targets = session.MutationTargets(shard, allShards, url, schema);

            var counts = await ShardRun.ProbeAsync(targets, (dataSource, target) =>
                new PostgresDeadLetterStore(dataSource, target.Schema).CountAsync(processorId));
            var probeFailed = ShardRun.ReportFailures(output, counts);
            var total = counts.Where(r => r.Succeeded).Sum(r => r.Value);

            if (total == 0)
            {
                if (json)
                    output.Json(new { action = "retry", processorId, deadLetters = 0, noOp = true });
                else
                    output.Text($"No dead letters found for processor '{processorId}'{ShardRun.Scope(targets)}. No-op.");

                return probeFailed ? 1 : 0;
            }

            if (dryRun)
            {
                if (json)
                    output.Json(new { dryRun = true, action = "retry", processorId, count = total });
                else
                    output.Text($"[Dry run] Would mark {total} dead letter(s) for retry for processor '{processorId}'{ShardRun.Scope(targets)}.");

                return probeFailed ? 1 : 0;
            }

            if (session.Confirm(yes,
                $"Mark [bold]{total}[/] dead letter(s) for retry for processor '[bold]{processorId}[/]'{ShardRun.Scope(targets)}? " +
                "The processor will reprocess them on its next retry loop cycle.",
                $"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops dead-letters retry {processorId} --yes") is { } confirmCode)
            {
                return confirmCode;
            }

            var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
            {
                IAdminOperator operations = new PostgresAdminOperator(dataSource, target.Schema);
                var count = await operations.MarkDeadLettersForRetryAsync(processorId, CliSession.OperatorId);

                if (json)
                    output.Json(new { action = "retry", shard = target.ShardId, processorId, markedForRetry = count });
                else
                    output.Text($"Marked {count} dead letter(s) for retry. The processor will pick them up on its next retry loop cycle.");
            });

            return (failed || probeFailed) ? 1 : 0;
        });

    internal static Task<int> HandleRetryRewindAsync(
        string processorId, string? url, string? schema, bool dryRun, bool yes, bool json,
        string? shard, bool allShards, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;
            var targets = session.MutationTargets(shard, allShards, url, schema);

            var counts = await ShardRun.ProbeAsync(targets, (dataSource, target) =>
                new PostgresDeadLetterStore(dataSource, target.Schema).CountAsync(processorId));
            var probeFailed = ShardRun.ReportFailures(output, counts);
            var total = counts.Where(r => r.Succeeded).Sum(r => r.Value);

            if (total == 0)
            {
                if (json)
                    output.Json(new { action = "retry-rewind", processorId, deadLetters = 0, noOp = true });
                else
                    output.Text($"No dead letters found for processor '{processorId}'{ShardRun.Scope(targets)}. No-op.");

                return probeFailed ? 1 : 0;
            }

            if (dryRun)
            {
                if (json)
                    output.Json(new { dryRun = true, action = "retry-rewind", processorId, deadLetterCount = total });
                else
                    output.Text($"[Dry run] Would rewind processor '{processorId}'{ShardRun.Scope(targets)} to its earliest dead letter and clear {total} dead letter(s).");

                return probeFailed ? 1 : 0;
            }

            if (session.Confirm(yes,
                $"Rewind processor '[bold]{processorId}[/]'{ShardRun.Scope(targets)} to replay from its earliest dead letter and clear {total} dead letter(s). Continue?",
                $"Destructive operation requires confirmation. Add --yes to confirm.\n  alberto ops dead-letters retry-rewind {processorId} --yes") is { } confirmCode)
            {
                return confirmCode;
            }

            var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
            {
                IAdminOperator operations = new PostgresAdminOperator(dataSource, target.Schema);
                var (rewindPosition, deletedCount) =
                    await operations.RetryByRewindAsync(processorId, CliSession.OperatorId);

                if (rewindPosition is null)
                {
                    // Dead letters were cleared by another operator between the count and the rewind.
                    if (json)
                        output.Json(new { action = "retry-rewind", shard = target.ShardId, processorId, rewindPosition, dismissedDeadLetters = 0 });
                    else
                        output.Text($"No dead letters remain for processor '{processorId}'. Checkpoint left unchanged.");
                    return;
                }

                if (json)
                    output.Json(new { action = "retry-rewind", shard = target.ShardId, processorId, rewindPosition, dismissedDeadLetters = deletedCount });
                else
                    output.Text($"Done. Consumer will replay from position {rewindPosition}. Cleared {deletedCount} dead letter(s).");
            });

            return (failed || probeFailed) ? 1 : 0;
        });
}
