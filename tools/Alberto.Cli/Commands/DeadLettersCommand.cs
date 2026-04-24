using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;

namespace Alberto.Cli.Commands;

public static class DeadLettersCommand
{
    public static Command Build()
    {
        var command = new Command("dead-letters",
            """
            List dead letter entries — events that failed processing and were not retried.

            Examples:
              alberto dead-letters
              alberto dead-letters --processor my-processor
              alberto dead-letters --type OrderPlaced
              alberto dead-letters --limit 50 --json
            """);

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var processorOption = new Option<string?>("--processor") { Description = "Filter by processor ID" };
        var typeOption = new Option<string?>("--type") { Description = "Filter by event type" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 20, Description = "Maximum number of results" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(processorOption);
        command.AddOption(typeOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string? url, string? schema, string? processor, string? type, int limit, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var deadLetters = await data.GetDeadLettersAsync(processor, type, limit);

                if (json)
                {
                    output.Json(deadLetters.Select(d => new
                    {
                        d.Id,
                        d.ProcessorId,
                        d.EventType,
                        d.GlobalPosition,
                        d.ErrorMessage,
                        failedAt = d.FailedAt?.ToString("O")
                    }));
                }
                else if (deadLetters.Count == 0)
                {
                    output.Text("No dead letters found.");
                }
                else
                {
                    output.Table(
                        ["ID", "Processor ID", "Event Type", "Position", "Error", "Failed At"],
                        deadLetters.Select(d => new[]
                        {
                            d.Id.ToString(),
                            d.ProcessorId,
                            d.EventType ?? "-",
                            d.GlobalPosition?.ToString() ?? "-",
                            TruncateError(d.ErrorMessage),
                            d.FailedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        })
                    );
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, processorOption, typeOption, limitOption, jsonOption);

        return command;
    }

    private static string TruncateError(string? error)
    {
        if (error is null) return "-";
        return error.Length > 60 ? error[..60] + "..." : error;
    }
}
