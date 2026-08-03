using System.CommandLine;
using Alberto.Admin;
using Alberto.Cli.Output;
using Alberto.Postgres;

namespace Alberto.Cli.Commands;

public static class TenantsCommand
{
    public static Command Build()
    {
        var command = new Command("tenants",
            """
            List current tenant lease assignments.

            Examples:
              alberto tenants
              alberto tenants --json
              alberto tenants --shard db2
            """);

        var (urlOption, schemaOption, jsonOption) = CliOptions.AddConnectionOptions(command);
        var shardOption = ShardRun.AddReadOption(command);

        command.SetHandler((string? url, string? schema, bool json, string? shard) =>
        {
            var session = new CliSession(json);
            return HandleAsync(url, schema, json, shard, session);
        }, urlOption, schemaOption, jsonOption, shardOption);

        return command;
    }

    internal static Task<int> HandleAsync(
        string? url, string? schema, bool json, string? shard, CliSession session) =>
        session.RunAsync(async () =>
        {
            var output = session.Output;

            // Leases are held per database — a tenant on db2 is leased by the consumer running
            // against db2 — so this reads every shard rather than one.
            var targets = session.ReadTargets(shard, url, schema);
            var results = await ShardRun.CollectAsync(
                targets, admin => admin.GetTenantLeaseInventoryAsync());

            return Render(output, targets, results, json);
        });

    internal static int Render(
        IOutput output,
        IReadOnlyList<ShardTarget> targets,
        IReadOnlyList<ShardResult<TenantLeaseInventory>> results,
        bool json)
    {
        var showShard = ShardRun.ShowsShard(targets);
        var succeeded = results.Where(r => r.Succeeded).ToArray();

        // Single-tenant is a property of a database, so it is only the whole answer when
        // every database Alberto looked at reported it.
        var allSingleTenant = succeeded.Length > 0
            && succeeded.All(r => r.Value!.TenancyMode is AdminTenancyMode.SingleTenant);

        var leaseRows = succeeded
            .Where(r => r.Value!.TenancyMode is not AdminTenancyMode.SingleTenant)
            .SelectMany(r => r.Value!.Leases.Select(l => (r.Target.ShardId, Lease: l)))
            .ToArray();

        if (json)
        {
            output.Json(new
            {
                singleTenantMode = allSingleTenant,
                count = leaseRows.Length,
                leases = leaseRows.Select(row => new
                {
                    shard = showShard ? row.ShardId : null,
                    row.Lease.TenantId,
                    row.Lease.ConsumerId,
                    row.Lease.ReplicaId,
                    expiresAt = row.Lease.ExpiresAt?.ToString("O")
                })
            });
        }
        else if (allSingleTenant)
        {
            output.Text("No tenant leases found (single-tenant mode).");
        }
        else if (leaseRows.Length == 0)
        {
            output.Text("No active tenant leases.");
        }
        else
        {
            output.Table(
                showShard
                    ? ["Shard", "Tenant ID", "Consumer ID", "Replica ID", "Expires At"]
                    : ["Tenant ID", "Consumer ID", "Replica ID", "Expires At"],
                leaseRows.Select(row =>
                {
                    string[] cells =
                    [
                        row.Lease.TenantId,
                        row.Lease.ConsumerId,
                        row.Lease.ReplicaId ?? "-",
                        row.Lease.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"
                    ];
                    return showShard ? [row.ShardId!, .. cells] : cells;
                }));
        }

        return ShardRun.ReportFailures(output, results) ? 1 : 0;
    }
}
