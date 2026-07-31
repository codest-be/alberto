using System.CommandLine;
using Alberto.Admin;
using Alberto.Postgres;

namespace Alberto.Cli.Commands;

public static class ProcessorCommand
{
    public static Command Build()
    {
        var command = new Command("processor",
            """
            Show details for a specific processor including its checkpoint position.

            Examples:
              alberto processor my-processor
              alberto processor my-processor --json
              alberto processor my-processor --shard db2
            """);

        var idArgument = new Argument<string>("id") { Description = "Processor ID" };
        command.AddArgument(idArgument);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string id, string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                // A processor runs once per database, so on a sharded module this is several
                // checkpoints under one name — rendered as rows rather than a box, because the
                // positions belong to different sequences and must not be read as one number.
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(
                    targets,
                    async admin =>
                    {
                        var checkpoint = await admin.GetSingleCheckpointAsync(id);
                        return checkpoint is null
                            ? (IReadOnlyList<CheckpointInfo>)[]
                            : [checkpoint];
                    });

                var found = results.Where(r => r.Succeeded).SelectMany(r => r.Value!).Any();

                if (json)
                {
                    output.Json(ShardRun.Flatten(targets, results, c => new
                    {
                        c.ProcessorId,
                        c.LastPosition,
                        updatedAt = c.UpdatedAt?.ToString("O")
                    }));
                }
                else if (!found)
                {
                    output.Warning($"Processor '{id}' not found.");
                }
                else if (ShardRun.ShowsShard(targets))
                {
                    ShardRun.Table(
                        output, targets, results,
                        ["Processor ID", "Last Position", "Updated At"],
                        c =>
                        [
                            c.ProcessorId,
                            c.LastPosition.ToString(),
                            c.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        ],
                        $"Processor '{id}' not found.");
                }
                else
                {
                    var checkpoint = results[0].Value![0];
                    output.Box($"Processor: {id}", new Dictionary<string, string>
                    {
                        ["Processor ID"] = checkpoint.ProcessorId,
                        ["Last Position"] = checkpoint.LastPosition.ToString(),
                        ["Updated At"] = checkpoint.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                        ["Schema"] = targets[0].Schema
                    });
                }

                return (ShardRun.ReportFailures(output, results) || !found) ? 1 : 0;
            });
        }, idArgument, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }
}
