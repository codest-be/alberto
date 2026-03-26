using System.CommandLine;
using Alberto.Cli.Data;
using Alberto.Cli.Output;
using Npgsql;

namespace Alberto.Cli.Commands;

public static class TenantsCommand
{
    public static Command Build()
    {
        var command = new Command("tenants", "List current tenant lease assignments");

        var urlOption = new Option<string?>("--url", "PostgreSQL connection string");
        var schemaOption = new Option<string?>("--schema", "Database schema name");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

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

                var tableExists = await data.TenantLeasesTableExistsAsync();
                if (!tableExists)
                {
                    if (json)
                    {
                        output.Json(new { singleTenantMode = true, leases = Array.Empty<object>() });
                    }
                    else
                    {
                        output.Text("No tenant leases found (single-tenant mode).");
                    }
                    return;
                }

                var leases = await data.GetTenantLeasesAsync();

                if (json)
                {
                    output.Json(new
                    {
                        singleTenantMode = false,
                        count = leases.Count,
                        leases = leases.Select(l => new
                        {
                            l.TenantId,
                            l.ConsumerId,
                            l.ReplicaId,
                            expiresAt = l.ExpiresAt?.ToString("O")
                        })
                    });
                }
                else if (leases.Count == 0)
                {
                    output.Text("No active tenant leases.");
                }
                else
                {
                    output.Table(
                        ["Tenant ID", "Consumer ID", "Replica ID", "Expires At"],
                        leases.Select(l => new[]
                        {
                            l.TenantId,
                            l.ConsumerId,
                            l.ReplicaId ?? "-",
                            l.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
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
