using System.CommandLine;
using Alberto.Dcb.Postgres;
using Spectre.Console;

namespace Alberto.Cli.Commands.Ops;

public static class TenantOpsCommand
{
    public static Command Build()
    {
        var command = new Command("tenants", "Manage tenant leases");

        command.AddCommand(BuildRelease());

        return command;
    }

    private static Command BuildRelease()
    {
        var command = new Command("release", "Release tenant leases, forcing the application to reacquire them");

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var processorIdOption = new Option<string?>("--processor-id") { Description = "Release leases only for this consumer ID" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(processorIdOption);
        command.AddOption(yesOption);
        var (shardOption, allShardsOption) = ShardRun.AddMutationOptions(command);

        command.SetHandler(async (string? url, string? schema, string? processorId, bool yes, string? shard, bool allShards) =>
        {
            // release produces human-readable output only — no --json flag.
            var session = new CliSession(json: false);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                var scope = processorId is not null
                    ? $"consumer '{processorId}'"
                    : "all consumers";

                // Leases live in the same database as the events they fence, so a sharded module
                // holds a separate set per shard and each has to be released on its own.
                var targets = session.MutationTargets(shard, allShards, url, schema);

                if (!yes)
                {
                    // NOTE: intentionally no non-interactive guard here — this is a pre-existing
                    // inconsistency with other destructive commands that is preserved as-is.
                    var confirmed = AnsiConsole.Confirm(
                        $"Release tenant leases for {scope}{ShardRun.Scope(targets)}? The running application will reacquire them.",
                        defaultValue: false);

                    if (!confirmed)
                    {
                        output.Text("Aborted.");
                        return 0;
                    }
                }

                var failed = await ShardRun.ApplyAsync(output, targets, async (dataSource, target) =>
                {
                    var admin = new PostgresAdminDataAccess(dataSource, target.Schema);
                    var deleted = await admin.ReleaseTenantLeasesAsync(processorId);
                    output.Text($"Released {deleted} tenant lease(s) for {scope}.");
                });

                return failed ? 1 : 0;
            });
        }, urlOption, schemaOption, processorIdOption, yesOption, shardOption, allShardsOption);

        return command;
    }
}
