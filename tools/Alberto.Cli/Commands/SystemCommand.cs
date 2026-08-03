using System.CommandLine;

namespace Alberto.Cli.Commands;

public static class SystemCommand
{
    public static Command Build()
    {
        var command = new Command("system", "Show global system information");

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(targets, admin => admin.GetSystemInfoAsync());
                var showShard = ShardRun.ShowsShard(targets);

                if (json)
                {
                    var payloads = results
                        .Where(r => r.Succeeded)
                        .Select(r => new
                        {
                            shard = showShard ? r.Target.ShardId : null,
                            globalPosition = r.Value!.GlobalPosition,
                            processorCount = r.Value.ProcessorCount,
                            deadLetterCount = r.Value.DeadLetterCount,
                            lastEventAt = r.Value.LastEventAt?.ToString("O")
                        })
                        .ToArray();

                    if (showShard)
                        output.Json(payloads);
                    else if (payloads.Length > 0)
                        output.Json(payloads[0]);
                }
                else
                {
                    foreach (var result in results.Where(r => r.Succeeded))
                    {
                        var info = result.Value!;
                        output.Box(showShard ? $"System — {result.Target.ShardId}" : "System",
                            new Dictionary<string, string>
                            {
                                ["Global Position"] = info.GlobalPosition?.ToString() ?? "(no events)",
                                ["Processors"] = info.ProcessorCount.ToString(),
                                ["Dead Letters"] = info.DeadLetterCount.ToString(),
                                ["Last Event"] = info.LastEventAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                            });
                    }
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
            });
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }
}
