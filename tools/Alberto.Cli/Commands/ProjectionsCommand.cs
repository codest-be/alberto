using System.CommandLine;
using Alberto.Cli.Output;
using Alberto.Dcb.Postgres;
using Npgsql;

namespace Alberto.Cli.Commands;

public static class ProjectionsCommand
{
    public static Command Build()
    {
        var command = new Command("projections", "Query projection state");

        command.AddCommand(BuildTypes());
        command.AddCommand(BuildList());

        return command;
    }

    private static Command BuildTypes()
    {
        var command = new Command("types", "List all distinct projection types");

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

                var types = await admin.GetProjectionTypesAsync();

                if (json)
                {
                    output.Json(new { projectionTypes = types });
                }
                else if (types.Count == 0)
                {
                    output.Text("No projection types found.");
                }
                else
                {
                    output.Table(
                        ["Projection Type"],
                        types.Select(t => new[] { t })
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

    private static Command BuildList()
    {
        var command = new Command("list", "List projection states for a given type");

        var typeArgument = new Argument<string>("type") { Description = "Projection type (e.g. OrderSummary)" };
        var urlOption = new Option<string?>("--url") { Description = "PostgreSQL connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema name" };
        var tenantOption = new Option<string?>("--tenant") { Description = "Filter by tenant ID" };
        var searchOption = new Option<string?>("--search") { Description = "Filter document IDs by substring" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 50, Description = "Maximum number of rows to return" };
        var jsonOption = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddArgument(typeArgument);
        command.AddOption(urlOption);
        command.AddOption(schemaOption);
        command.AddOption(tenantOption);
        command.AddOption(searchOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string type, string? url, string? schema, string? tenant, string? search, int limit, bool json) =>
        {
            IOutput output = json ? new JsonOutput() : new HumanOutput();

            var connStr = ConnectionResolver.ResolveConnectionString(url);
            var schemaName = ConnectionResolver.ResolveSchema(schema);

            try
            {
                await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
                var admin = new PostgresAdminDataAccess(dataSource, schemaName);

                var states = await admin.GetProjectionStatesAsync(type, tenant, search, limit);

                if (json)
                {
                    output.Json(new
                    {
                        projectionType = type,
                        count = states.Count,
                        states = states.Select(s => new
                        {
                            s.DocumentId,
                            s.TenantId,
                            updatedAt = s.UpdatedAt?.ToString("O")
                        })
                    });
                }
                else if (states.Count == 0)
                {
                    output.Text($"No projection states found for type '{type}'.");
                }
                else
                {
                    output.Table(
                        ["Document ID", "Tenant ID", "Updated At"],
                        states.Select(s => new[]
                        {
                            s.DocumentId,
                            s.TenantId,
                            s.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                        })
                    );
                }
            }
            catch (Exception ex)
            {
                output.Error(ex.Message);
                Environment.Exit(1);
            }
        }, typeArgument, urlOption, schemaOption, tenantOption, searchOption, limitOption, jsonOption);

        return command;
    }
}
