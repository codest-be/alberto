using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;

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
                // A position is a per-database sequence, so the Shard column is not decoration:
                // two rows for the same processor are two unrelated numbers.
                var targets = ShardResolver.ResolveForRead(shard, url, schema);
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
