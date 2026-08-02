using System.CommandLine;
using Alberto.Admin;
using Alberto.Cli.Output;
using Alberto.Postgres;

namespace Alberto.Cli.Commands;

public static class SystemCommand
{
    public static Command Build()
    {
        var command = new Command("system", "Show global system information");

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler((string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return HandleAsync(url, schema, json, shard, session);
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }

    internal static Task<int> HandleAsync(
        string? url, string? schema, bool json, string? shard, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;
            var targets = session.ReadTargets(shard, url, schema);
            var results = await ShardRun.CollectAsync(targets, admin => admin.GetSystemInfoAsync());

            return Render(output, targets, results, json);
        });

    internal static int Render(
        IOutput output,
        IReadOnlyList<ShardTarget> targets,
        IReadOnlyList<ShardResult<SystemInfo>> results,
        bool json)
    {
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
    }
}
