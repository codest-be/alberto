using System.CommandLine;
using Alberto.Dcb.Postgres;

namespace Alberto.Cli.Commands;

public static class CheckpointsCommand
{
    public static Command Build()
    {
        var command = new Command("checkpoints",
            """
            List all processor checkpoints with their current positions.

            Examples:
              alberto checkpoints
              alberto checkpoints --json
              alberto checkpoints --shard db2
            """);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler(async (string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                // A position is a per-database sequence, so the Shard column is not decoration:
                // two rows for the same processor are two unrelated numbers.
                var targets = session.ReadTargets(shard, url, schema);
                var results = await ShardRun.CollectAsync(
                    targets, async admin => (IReadOnlyList<CheckpointInfo>)await admin.GetCheckpointsAsync());

                if (json)
                {
                    output.Json(ShardRun.Flatten(targets, results, c => new
                    {
                        c.ProcessorId,
                        c.LastPosition,
                        updatedAt = c.UpdatedAt?.ToString("O")
                    }));
                }
                else
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
                        "No checkpoints found.");
                }

                return ShardRun.ReportFailures(output, results) ? 1 : 0;
            });
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }
}
