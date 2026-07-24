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
            """);

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string? url, string? schema, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var admin = new PostgresAdminDataAccess(dataSource, schemaName);

                var checkpoints = await admin.GetCheckpointsAsync();

                if (json)
                {
                    output.Json(checkpoints.Select(c => new
                    {
                        c.ProcessorId,
                        c.LastPosition,
                        updatedAt = c.UpdatedAt?.ToString("O")
                    }));
                }
                else if (checkpoints.Count == 0)
                {
                    output.Text("No checkpoints found.");
                }
                else
                {
                    output.Table(
                        ["Processor ID", "Last Position", "Updated At"],
                        checkpoints.Select(c => new[]
                        {
                            c.ProcessorId,
                            c.LastPosition.ToString(),
                            c.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        })
                    );
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, jsonOption);

        return command;
    }
}
