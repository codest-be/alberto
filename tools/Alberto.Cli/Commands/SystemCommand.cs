using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;

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

        command.SetHandler(async (string? url, string? schema, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var admin = new PostgresAdminDataAccess(dataSource, schemaName);

                var info = await admin.GetSystemInfoAsync();

                if (json)
                {
                    output.Json(new
                    {
                        globalPosition = info.GlobalPosition,
                        processorCount = info.ProcessorCount,
                        deadLetterCount = info.DeadLetterCount,
                        lastEventAt = info.LastEventAt?.ToString("O")
                    });
                }
                else
                {
                    output.Box("System", new Dictionary<string, string>
                    {
                        ["Global Position"] = info.GlobalPosition?.ToString() ?? "(no events)",
                        ["Processors"] = info.ProcessorCount.ToString(),
                        ["Dead Letters"] = info.DeadLetterCount.ToString(),
                        ["Last Event"] = info.LastEventAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                    });
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
