using System.CommandLine;
using Alberto.Admin;
using Alberto.Postgres;

namespace Alberto.Cli.Commands.Ops;

public static class OutboxOpsCommand
{
    public static Command Build()
    {
        var command = new Command("outbox", "Manage the transactional outbox");

        command.AddCommand(BuildPurge());

        return command;
    }

    private static Command BuildPurge()
    {
        var command = new Command(
            "purge",
            "Permanently remove delivered outbox entries older than a cutoff");

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var beforeOption = new Option<DateTimeOffset?>("--before")
        {
            Description = "Delete entries delivered before this timestamp (ISO 8601). Defaults to 7 days ago."
        };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(beforeOption);
        command.AddOption(yesOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler((string? url, string? schema, DateTimeOffset? before, bool yes, string? shard, bool allShards) =>
        {
            // purge produces human-readable output only — no --json flag.
            var session = new CliSession(json: false);
            return HandlePurgeAsync(url, schema, before, yes, shard, allShards, session);
        }, urlOption, schemaOption, beforeOption, yesOption, shardOption, allShardsOption);

        return command;
    }

    internal static Task<int> HandlePurgeAsync(
        string? url, string? schema, DateTimeOffset? before, bool yes,
        string? shard, bool allShards, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;
            var cutoff = before ?? DateTimeOffset.UtcNow - TimeSpan.FromDays(7);

            // Each shard has its own outbox in its own database, so the cutoff has to be
            // applied to each one separately.
            var targets = session.MutationTargets(shard, allShards, url, schema);

            if (session.Confirm(
                    yes,
                    $"Permanently delete outbox entries delivered before {cutoff:u}" +
                    $"{ShardRun.Scope(targets)}? Undelivered entries are never removed.",
                    "Destructive operation requires confirmation. Add --yes to confirm.\n" +
                    "  alberto ops outbox purge --yes") is { } confirmCode)
            {
                return confirmCode;
            }

            var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
            {
                IAdminOperator operations = new PostgresAdminOperator(dataSource, target.Schema);
                var deleted = await operations.PurgeOutboxAsync(cutoff, CliSession.OperatorId);
                output.Text($"Purged {deleted} delivered outbox entr{(deleted == 1 ? "y" : "ies")}.");
            });

            return failed ? 1 : 0;
        });
}
