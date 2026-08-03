using System.CommandLine;
using Alberto.Admin;
using Alberto.Cli.Output;
using Alberto.Postgres;

namespace Alberto.Cli.Commands;

public static class DeadLettersCommand
{
    public static Command Build()
    {
        var command = new Command("dead-letters",
            """
            List dead letter entries — events that failed processing and were not retried.

            Examples:
              alberto dead-letters
              alberto dead-letters --processor my-processor
              alberto dead-letters --type OrderPlaced
              alberto dead-letters --tenant tenant_a
              alberto dead-letters --limit 50 --json
              alberto dead-letters --shard db2
            """);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var processorOption = new Option<string?>("--processor") { Description = "Filter by processor ID" };
        var typeOption = new Option<string?>("--type") { Description = "Filter by event type" };
        var tenantOption = new Option<string?>("--tenant") { Description = "Filter by tenant ID (multi-tenant stores only)" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 20, Description = "Maximum number of results" };

        command.AddOption(processorOption);
        command.AddOption(typeOption);
        command.AddOption(tenantOption);
        command.AddOption(limitOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler((string? url, string? schema, string? processor, string? type, string? tenant, int limit, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return HandleAsync(url, schema, processor, type, tenant, limit, json, shard, session);
        }, urlOption, schemaOption, processorOption, typeOption, tenantOption, limitOption, jsonOption, shardOption);

        return command;
    }

    internal static Task<int> HandleAsync(
        string? url, string? schema, string? processor, string? type, string? tenant, int limit,
        bool json, string? shard, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;

            // --limit applies per database, not across them: a failing processor on one shard
            // should not push another shard's dead letters out of the answer.
            var targets = session.ReadTargets(shard, url, schema);
            var results = await ShardRun.CollectAsync(
                targets,
                async admin => (IReadOnlyList<DeadLetterInfo>)
                    await admin.GetDeadLettersAsync(processor, type, tenant, limit));

            return Render(output, targets, results, json);
        });

    internal static int Render(
        IOutput output,
        IReadOnlyList<ShardTarget> targets,
        IReadOnlyList<ShardResult<IReadOnlyList<DeadLetterInfo>>> results,
        bool json)
    {
        if (json)
        {
            output.Json(ShardRun.Flatten(targets, results, d => new
            {
                d.Id,
                d.ProcessorId,
                d.EventType,
                d.GlobalPosition,
                d.ErrorMessage,
                d.TenantId,
                failedAt = d.FailedAt?.ToString("O")
            }));
        }
        else
        {
            ShardRun.Table(
                output, targets, results,
                ["ID", "Processor ID", "Event Type", "Position", "Tenant ID", "Error", "Failed At"],
                d =>
                [
                    d.Id.ToString(),
                    d.ProcessorId,
                    d.EventType ?? "-",
                    d.GlobalPosition?.ToString() ?? "-",
                    d.TenantId ?? "-",
                    TruncateError(d.ErrorMessage),
                    d.FailedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                ],
                "No dead letters found.");
        }

        return ShardRun.ReportFailures(output, results) ? 1 : 0;
    }

    private static string TruncateError(string? error)
    {
        if (error is null) return "-";
        return error.Length > 60 ? error[..60] + "..." : error;
    }
}
