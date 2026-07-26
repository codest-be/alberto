using System.CommandLine;
using Alberto.Dcb.Postgres;

namespace Alberto.Cli.Commands;

public static class EventsCommand
{
    public static Command Build()
    {
        var command = new Command("events",
            """
            List events from the event store.

            Examples:
              alberto events
              alberto events --type OrderPlaced
              alberto events --tag order:123
              alberto events --tenant tenant_a
              alberto events --after 1000 --limit 50
              alberto events --json
              alberto events --shard db2
            """);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var typeOption = new Option<string?>("--type") { Description = "Filter by event type" };
        var tagOption = new Option<string?>("--tag") { Description = "Filter by tag" };
        var tenantOption = new Option<string?>("--tenant") { Description = "Filter by tenant ID (multi-tenant stores only)" };
        var afterOption = new Option<long>("--after") { DefaultValueFactory = _ => 0, Description = "Return events after this global position (per database — positions are not comparable across shards)" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 20, Description = "Maximum number of results" };

        command.AddOption(typeOption);
        command.AddOption(tagOption);
        command.AddOption(tenantOption);
        command.AddOption(afterOption);
        command.AddOption(limitOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? url, string? schema, string? type, string? tag, string? tenant, long after, int limit, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                // Each database has its own position sequence, so --after is applied within each
                // one and the rows are grouped by shard rather than merged into one ordering.
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(
                    targets,
                    async admin => (IReadOnlyList<EventInfo>)
                        await admin.GetEventsAsync(type, tag, tenant, after, limit));

                if (json)
                {
                    output.Json(ShardRun.Flatten(targets, results, e => new
                    {
                        e.GlobalPosition,
                        e.EventType,
                        e.Tags,
                        e.TenantId,
                        createdAt = e.CreatedAt?.ToString("O")
                    }));
                }
                else
                {
                    ShardRun.Table(
                        output, targets, results,
                        ["Position", "Event Type", "Tags", "Tenant ID", "Created At"],
                        e =>
                        [
                            e.GlobalPosition.ToString(),
                            e.EventType,
                            e.Tags ?? "-",
                            e.TenantId ?? "-",
                            e.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        ],
                        "No events found.");
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
            });
        }, urlOption, schemaOption, typeOption, tagOption, tenantOption, afterOption, limitOption, jsonOption, shardOption);

        return command;
    }
}
