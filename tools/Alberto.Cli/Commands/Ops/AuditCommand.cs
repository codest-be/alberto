using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;

namespace Alberto.Cli.Commands.Ops;

public static class AuditCommand
{
    public static Command Build()
    {
        var command = new Command("audit",
            """
            Show recent audit log entries for operational actions (rebuilds, checkpoint changes).

            Examples:
              alberto ops audit
              alberto ops audit --limit 20
              alberto ops audit --json
            """);

        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var limitOption = new Option<int>("--limit", () => 50, "Maximum number of results");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string? url, string? schema, int limit, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var data = new CliDataAccess(dataSource, schemaName);

                if (!await data.AuditLogExistsAsync())
                {
                    output.Text("Audit log table (processor_audit_log) does not exist in this schema.");
                    return;
                }

                var entries = await data.GetAuditLogAsync(limit);

                if (json)
                {
                    output.Json(entries.Select(e => new
                    {
                        e.Id,
                        e.Action,
                        e.ProcessorId,
                        e.Operator,
                        occurredAt = e.OccurredAt?.ToString("O"),
                        e.Details
                    }));
                }
                else if (entries.Count == 0)
                {
                    output.Text("No audit log entries found.");
                }
                else
                {
                    output.Table(
                        ["ID", "Action", "Processor ID", "Operator", "Occurred At", "Details"],
                        entries.Select(e => new[]
                        {
                            e.Id.ToString(),
                            e.Action,
                            e.ProcessorId ?? "-",
                            e.Operator ?? "-",
                            e.OccurredAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                            e.Details ?? "-"
                        })
                    );
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, urlOption, schemaOption, limitOption, jsonOption);

        return command;
    }
}
