using System.CommandLine;
using Alberto.Admin;

namespace Alberto.Cli.Commands;

public static class ProjectionsCommand
{
    public static Command Build()
    {
        var command = new Command("projections", "Query projection state");

        command.AddCommand(BuildTypes());
        command.AddCommand(BuildList());

        return command;
    }

    private static Command BuildTypes()
    {
        var command = new Command("types", "List all distinct projection types");

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                // Every shard runs the same projections, so the types are deduplicated into one
                // list rather than repeated per database.
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(
                    targets, async admin => (IReadOnlyList<string>)await admin.GetProjectionTypesAsync());

                var types = results
                    .Where(r => r.Succeeded)
                    .SelectMany(r => r.Value!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                if (json)
                {
                    output.Json(new { projectionTypes = types });
                }
                else if (types.Length == 0)
                {
                    output.Text("No projection types found.");
                }
                else
                {
                    output.Table(
                        ["Projection Type"],
                        types.Select(t => new[] { t })
                    );
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
            });
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }

    private static Command BuildList()
    {
        var command = new Command("list", "List projection states for a given type");

        var typeArgument = new Argument<string>("type") { Description = "Projection type (e.g. OrderSummary)" };
        command.AddArgument(typeArgument);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var tenantOption = new Option<string?>("--tenant") { Description = "Filter by tenant ID" };
        var searchOption = new Option<string?>("--search") { Description = "Filter document IDs by substring" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 50, Description = "Maximum number of rows to return" };

        command.AddOption(tenantOption);
        command.AddOption(searchOption);
        command.AddOption(limitOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string type, string? url, string? schema, string? tenant, string? search, int limit, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(
                    targets,
                    async admin => (IReadOnlyList<ProjectionState>)
                        await admin.GetProjectionStatesAsync(type, tenant, search, limit));

                var showShard = ShardRun.ShowsShard(targets);
                var states = results
                    .Where(r => r.Succeeded)
                    .SelectMany(r => r.Value!.Select(s => (r.Target.ShardId, State: s)))
                    .ToArray();

                if (json)
                {
                    output.Json(new
                    {
                        projectionType = type,
                        count = states.Length,
                        states = states.Select(row => new
                        {
                            shard = showShard ? row.ShardId : null,
                            row.State.DocumentId,
                            row.State.TenantId,
                            updatedAt = row.State.UpdatedAt?.ToString("O")
                        })
                    });
                }
                else
                {
                    ShardRun.Table(
                        output, targets, results,
                        ["Document ID", "Tenant ID", "Updated At"],
                        s =>
                        [
                            s.DocumentId,
                            s.TenantId ?? "-",
                            s.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        ],
                        $"No projection states found for type '{type}'.");
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
            });
        }, typeArgument, urlOption, schemaOption, tenantOption, searchOption, limitOption, jsonOption, shardOption);

        return command;
    }
}
