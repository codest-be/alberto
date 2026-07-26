using System.CommandLine;
using Alberto.Dcb.Postgres;
using Npgsql;

namespace Alberto.Cli.Commands;

/// <summary>
/// Reads and edits the tenant → shard catalog in the control database.
/// </summary>
/// <remarks>
/// The catalog holds shard <em>ids</em> only. What an id resolves to lives in
/// <c>.alberto/config.json</c>, so nothing here reads or writes a connection string, and a shard
/// can move host without a row changing.
/// <para>
/// This is a separate top-level group rather than a verb under <c>tenants</c>, which already
/// means tenant leases.
/// </para>
/// </remarks>
public static class ShardsCommand
{
    public static Command Build()
    {
        var command = new Command("shards",
            """
            Inspect and edit the tenant → shard catalog.

            Only a module configured with AcrossPostgresDatabases has one. The catalog decides
            which database a tenant's events are written to and read from.

            Examples:
              alberto shards list
              alberto shards where acme
              alberto shards assign acme --shard db2
            """);

        command.AddCommand(BuildList());
        command.AddCommand(BuildWhere());
        command.AddCommand(BuildAssign());

        return command;
    }

    private static Command BuildList()
    {
        var command = new Command("list",
            """
            List every shard, with how many tenants the catalog files under it.

            A shard the catalog names but the configuration does not declare is called out:
            those tenants cannot be routed anywhere until the shard is added to the config.
            """);

        var (urlOption, schemaOption, moduleOption, jsonOption) = AddCatalogOptions(command);

        command.SetHandler(async (string? url, string? schema, string? module, bool json) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var catalog = Open(session, url, schema, module);
                await using var dataSource = catalog.DataSource;

                var assignments = await catalog.Map.GetAllAsync();
                var counts = assignments.Values
                    .GroupBy(id => id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                var declared = session.DeclaredShardIds();
                var ids = declared
                    .Concat(counts.Keys)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                if (json)
                {
                    output.Json(new
                    {
                        module = catalog.ModuleKey,
                        shards = ids.Select(id => new
                        {
                            shard = id,
                            tenants = counts.GetValueOrDefault(id),
                            configured = declared.Contains(id, StringComparer.Ordinal),
                        }),
                    });
                    return 0;
                }

                if (ids.Length == 0)
                {
                    output.Text(
                        "No shards. Nothing is declared in .alberto/config.json and no tenant is " +
                        "assigned to one.");
                    return 0;
                }

                output.Table(
                    ["Shard", "Tenants", "Configured"],
                    ids.Select(id => new[]
                    {
                        id,
                        counts.GetValueOrDefault(id).ToString(),
                        declared.Contains(id, StringComparer.Ordinal) ? "yes" : "no",
                    }));

                var orphaned = ids.Where(id => !declared.Contains(id, StringComparer.Ordinal)).ToArray();
                if (orphaned.Length > 0)
                {
                    output.Warning(
                        $"Not declared in .alberto/config.json: {string.Join(", ", orphaned)}. " +
                        "Tenants assigned to them cannot be routed until the shards are configured.");
                }

                return 0;
            });
        }, urlOption, schemaOption, moduleOption, jsonOption);

        return command;
    }

    private static Command BuildWhere()
    {
        var command = new Command("where", "Show which shard a tenant's events live in");

        var tenantArgument = new Argument<string>("tenant") { Description = "Tenant ID to look up" };
        command.AddArgument(tenantArgument);
        var (urlOption, schemaOption, moduleOption, jsonOption) = AddCatalogOptions(command);

        command.SetHandler(async (string tenant, string? url, string? schema, string? module, bool json) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;
                var catalog = Open(session, url, schema, module);
                await using var dataSource = catalog.DataSource;

                var shard = await catalog.Map.ResolveAsync(tenant);

                if (json)
                    output.Json(new { module = catalog.ModuleKey, tenant, shard });
                else if (shard is null)
                    output.Text($"Tenant '{tenant}' is not assigned to a shard.");
                else
                    output.Text($"Tenant '{tenant}' is in shard '{shard}'.");

                // Non-zero for an unassigned tenant, so a script can branch on it without
                // parsing the message.
                return shard is null ? 1 : 0;
            });
        }, tenantArgument, urlOption, schemaOption, moduleOption, jsonOption);

        return command;
    }

    private static Command BuildAssign()
    {
        var command = new Command("assign",
            """
            Assign a tenant to a shard, deciding which database its events are written to.

            The assignment is permanent. Nothing in Alberto moves a tenant's events between
            databases, so the only way to undo a wrong one is to move the data by hand.

            The application assigns a tenant on its own the first time it sees one, using the
            placement rule the module was configured with. Use this to place a tenant
            deliberately, before it writes anything.
            """);

        var tenantArgument = new Argument<string>("tenant") { Description = "Tenant ID to assign" };
        var shardOption = new Option<string?>("--shard") { Description = "Shard ID to assign the tenant to" };
        var yesOption = new Option<bool>("--yes") { Description = "Skip confirmation prompt" };

        command.AddArgument(tenantArgument);
        command.AddOption(shardOption);
        command.AddOption(yesOption);
        var (urlOption, schemaOption, moduleOption, jsonOption) = AddCatalogOptions(command);

        command.SetHandler(async (string tenant, string? shardId, bool yes, string? url, string? schema,
            string? module, bool json) =>
        {
            var session = new CliSession(json);
            return await session.RunAsync(async () =>
            {
                var output = session.Output;

                if (string.IsNullOrWhiteSpace(shardId))
                {
                    output.Error("--shard is required. Run 'alberto shards list' to see the shards.");
                    return 1;
                }

                var declared = session.DeclaredShardIds();
                if (declared.Count > 0 && !declared.Contains(shardId, StringComparer.Ordinal))
                {
                    output.Error(
                        $"No shard '{shardId}' is declared in .alberto/config.json. " +
                        $"Declared shards: {string.Join(", ", declared)}.");
                    return 1;
                }

                if (session.Confirm(yes,
                    $"Put tenant '[bold]{tenant}[/]' in shard '[bold]{shardId}[/]'? " +
                    "Its events stay in that database and cannot be moved from here.",
                    "This assignment cannot be undone. Add --yes to confirm.\n" +
                    $"  alberto shards assign {tenant} --shard {shardId} --yes") is { } confirmCode)
                {
                    return confirmCode;
                }

                var catalog = Open(session, url, schema, module);
                await using var dataSource = catalog.DataSource;

                var assigned = await catalog.Map.AssignAsync(tenant, shardId);

                // The catalog is first-writer-wins, so a different id coming back means the
                // tenant was already placed rather than that this call failed.
                if (!string.Equals(assigned, shardId, StringComparison.Ordinal))
                {
                    if (json)
                        output.Json(new { module = catalog.ModuleKey, tenant, shard = assigned, requested = shardId, changed = false });

                    output.Error(
                        $"Tenant '{tenant}' is already in shard '{assigned}'. Its events were written " +
                        "there and this command does not move them.");
                    return 1;
                }

                if (json)
                    output.Json(new { module = catalog.ModuleKey, tenant, shard = assigned, requested = shardId, changed = true });
                else
                    output.Text($"Tenant '{tenant}' is in shard '{assigned}'.");

                return 0;
            });
        }, tenantArgument, shardOption, yesOption, urlOption, schemaOption, moduleOption, jsonOption);

        return command;
    }

    /// <summary>
    /// An open catalog, and the data source behind it for the caller to dispose.
    /// </summary>
    private sealed record Catalog(
        NpgsqlDataSource DataSource,
        PostgresTenantShardMap Map,
        string ModuleKey);

    /// <summary>
    /// Opens the control database and returns the catalog for the module in play.
    /// </summary>
    /// <remarks>
    /// Every verb here talks to exactly one database — the control one. None of them fan out:
    /// the catalog is the thing that decides what the shards are, so it cannot be per-shard.
    /// <para>
    /// Both facts come off <paramref name="session"/>, which read the config once for the whole
    /// invocation. Resolving them through the parameterless overloads instead walked the
    /// directory tree and re-parsed the file once per fact — three times over for
    /// <c>shards list</c>, which also wants the declared ids — and let one command see two
    /// different configs.
    /// </para>
    /// </remarks>
    private static Catalog Open(CliSession session, string? url, string? schema, string? module)
    {
        var target = session.CatalogTarget(url, schema);
        var moduleKey = session.ModuleKey(module);

        var dataSource = new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
        return new Catalog(dataSource, new PostgresTenantShardMap(dataSource, moduleKey, target.Schema), moduleKey);
    }

    private static (Option<string?> Url, Option<string?> Schema, Option<string?> Module, Option<bool> Json)
        AddCatalogOptions(Command command)
    {
        var url = new Option<string?>("--url")
        {
            Description = "Connection string for the control database holding the catalog",
        };
        var schema = new Option<string?>("--schema") { Description = "Database schema name" };
        var module = new Option<string?>("--module")
        {
            Description = "Module key the catalog's rows are filed under. Defaults to the configured one.",
        };
        var json = new Option<bool>("--json") { Description = "Output as JSON" };

        command.AddOption(url);
        command.AddOption(schema);
        command.AddOption(module);
        command.AddOption(json);

        return (url, schema, module, json);
    }
}
