using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;

namespace Alberto.Cli.Commands;

public static class StatusCommand
{
    public static Command Build()
    {
        var command = new Command("status",
            """
            Show overall event store status including global position, processor count, and dead letter count.

            Examples:
              alberto status
              alberto status --json
              alberto status --schema myschema
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
                var data = new CliDataAccess(dataSource, schemaName);

                var globalPosition = await data.GetGlobalPositionAsync();
                var processors = await data.GetProcessorsAsync();
                var deadLetterCount = await data.CountDeadLettersAsync(null);

                if (json)
                {
                    output.Json(new
                    {
                        globalPosition,
                        processorCount = processors.Count,
                        deadLetterCount,
                        processors = processors.Select(p => new
                        {
                            p.ProcessorId,
                            p.LastPosition,
                            updatedAt = p.UpdatedAt?.ToString("O")
                        })
                    });
                }
                else
                {
                    output.Box("Event Store Status", new Dictionary<string, string>
                    {
                        ["Global Position"] = globalPosition?.ToString() ?? "(no events)",
                        ["Processor Count"] = processors.Count.ToString(),
                        ["Dead Letters"] = deadLetterCount.ToString(),
                        ["Schema"] = schemaName
                    });

                    if (processors.Count > 0)
                    {
                        output.Table(
                            ["Processor ID", "Last Position", "Updated At"],
                            processors.Select(p => new[]
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
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, jsonOption);

        return command;
    }
}
