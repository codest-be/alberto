using System.CommandLine;
using Alberto.Admin;

namespace Alberto.Cli.Commands;

public static class StatusCommand
{
    /// <summary>One database's status. Nothing here is meaningful added across shards.</summary>
    private sealed record Status(
        long? GlobalPosition,
        IReadOnlyList<ProcessorInfo> Processors,
        long DeadLetterCount);

    public static Command Build()
    {
        var command = new Command("status",
            """
            Show overall event store status including global position, processor count, and dead letter count.

            Examples:
              alberto status
              alberto status --json
              alberto status --schema myschema
              alberto status --shard db2
            """);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                // One status per database rather than one summed status: a global position is a
                // per-database sequence, so adding two of them would produce a number that means
                // nothing.
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(targets, async admin => new Status(
                    await admin.GetGlobalPositionAsync(),
                    await admin.GetProcessorsAsync(),
                    await admin.CountAllDeadLettersAsync()));

                var showShard = ShardRun.ShowsShard(targets);

                if (json)
                {
                    var payloads = results
                        .Where(r => r.Succeeded)
                        .Select(r => new
                        {
                            shard = showShard ? r.Target.ShardId : null,
                            globalPosition = r.Value!.GlobalPosition,
                            processorCount = r.Value.Processors.Count,
                            deadLetterCount = r.Value.DeadLetterCount,
                            processors = r.Value.Processors.Select(p => new
                            {
                                p.ProcessorId,
                                p.LastPosition,
                                updatedAt = p.UpdatedAt?.ToString("O")
                            })
                        })
                        .ToArray();

                    // Unsharded output stays the single object it has always been. When that one
                    // database could not be reached there is nothing to print — ReportFailures
                    // below says why and sets the exit code.
                    if (showShard)
                        output.Json(payloads);
                    else if (payloads.Length > 0)
                        output.Json(payloads[0]);
                }
                else
                {
                    foreach (var result in results.Where(r => r.Succeeded))
                    {
                        var status = result.Value!;
                        var title = showShard
                            ? $"Event Store Status — {result.Target.ShardId}"
                            : "Event Store Status";

                        output.Box(title, new Dictionary<string, string>
                        {
                            ["Global Position"] = status.GlobalPosition?.ToString() ?? "(no events)",
                            ["Processor Count"] = status.Processors.Count.ToString(),
                            ["Dead Letters"] = status.DeadLetterCount.ToString(),
                            ["Schema"] = result.Target.Schema
                        });

                        if (status.Processors.Count > 0)
                        {
                            output.Table(
                                ["Processor ID", "Last Position", "Updated At"],
                                status.Processors.Select(p => new[]
                                {
                                    p.ProcessorId,
                                    p.LastPosition.ToString(),
                                    p.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                                })
                            );
                        }
                        else
                        {
                            output.Text("No processors found.");
                        }
                    }
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
            });
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }
}
