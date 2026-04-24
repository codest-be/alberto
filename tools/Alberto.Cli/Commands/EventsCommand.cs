using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;

namespace Alberto.Cli.Commands;

public static class EventsCommand
{
    public static Command Build()
    {
        var command = new Command("events",
            """
            List events from the event store.

            Examples:
              alberto events
              alberto events --type OrderPlaced
              alberto events --tag order:123
              alberto events --after 1000 --limit 50
              alberto events --json
            """);

        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var typeOption = new Option<string?>("--type") { Description = "Filter by event type" };
        var tagOption = new Option<string?>("--tag") { Description = "Filter by tag" };
        var afterOption = new Option<long>("--after") { DefaultValueFactory = _ => 0, Description = "Return events after this global position" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 20, Description = "Maximum number of results" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(typeOption);
        command.AddOption(tagOption);
        command.AddOption(afterOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string? url, string? schema, string? type, string? tag, long after, int limit, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                var events = await data.GetEventsAsync(type, tag, after, limit);

                if (json)
                {
                    output.Json(events.Select(e => new
                    {
                        e.GlobalPosition,
                        e.EventType,
                        e.Tags,
                        createdAt = e.CreatedAt?.ToString("O")
                    }));
                }
                else if (events.Count == 0)
                {
                    output.Text("No events found.");
                }
                else
                {
                    output.Table(
                        ["Position", "Event Type", "Tags", "Created At"],
                        events.Select(e => new[]
                        {
                            e.GlobalPosition.ToString(),
                            e.EventType,
                            e.Tags ?? "-",
                            e.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        })
                    );
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, typeOption, tagOption, afterOption, limitOption, jsonOption);

        return command;
    }
}
