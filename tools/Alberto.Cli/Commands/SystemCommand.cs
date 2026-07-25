using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;

namespace Alberto.Cli.Commands;

public static class SystemCommand
{
    public static Command Build()
    {
        var command = new Command("system", "Show global system information");

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(jsonOption);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? url, string? schema, bool json, string? shard) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            try
            {
                var targets = ShardResolver.ResolveForRead(shard, url, schema);
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

                if (ShardRun.ReportFailures(output, results))
                    Environment.Exit(1);
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }
}
