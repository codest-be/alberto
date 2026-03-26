using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;

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
            """);

        var idArgument = new Argument<string>("id", "Processor ID");
        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddArgument(idArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string id, string? url, string? schema, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var checkpoint = await data.GetSingleCheckpointAsync(id);

                if (checkpoint is null)
                {
                    output.Warning($"Processor '{id}' not found.");
                    Environment.Exit(1);
                    return;
                }

                if (json)
                {
                    output.Json(new
                    {
                        checkpoint.ProcessorId,
                        checkpoint.LastPosition,
                        updatedAt = checkpoint.UpdatedAt?.ToString("O")
                    });
                }
                else
                {
                    output.Box($"Processor: {id}", new Dictionary<string, string>
                    {
                        ["Processor ID"] = checkpoint.ProcessorId,
                        ["Last Position"] = checkpoint.LastPosition.ToString(),
                        ["Updated At"] = checkpoint.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                        ["Schema"] = schemaName
                    });
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, idArgument, urlOption, schemaOption, jsonOption);

        return command;
    }
}
