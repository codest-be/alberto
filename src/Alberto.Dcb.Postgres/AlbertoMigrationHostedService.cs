using Alberto.Dcb.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Applies Alberto's schema migrations and checks the tenancy mode of the existing schema.
/// This runs at startup rather than during service registration, so building a
/// <see cref="IServiceProvider"/> — in a test, a design-time factory, or a CLI tool — never
/// opens a database connection.
/// </summary>
internal sealed class AlbertoMigrationHostedService(
    string moduleKey,
    IOptionsMonitor<AlbertoModuleDefinition> definitions,
    ILogger<AlbertoMigrationHostedService>? logger = null) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var definition = definitions.Get(moduleKey);

        if (definition.Backend is not PostgresBackendDescriptor descriptor)
            return Task.CompletedTask;

        var options = descriptor.Options;

        if (options.AutoMigrate)
        {
            logger?.LogInformation(
                "Applying Alberto migrations for module {ModuleKey} to schema {Schema}.",
                moduleKey, options.Schema ?? "(default)");

            // Pass singleTenant so the correct migration script folder is selected. The old
            // inline WithPostgres code passed singleTenant: !isTenantMode; the brief's
            // hosted-service snippet omits it (a likely oversight). Omitting it would
            // silently run the multi-tenant scripts for single-tenant modules.
            PostgresMigrator.Migrate(options.ConnectionString, options.Schema, singleTenant: !definition.TenancyEnabled);
        }

        // Catches the case where a schema was created single-tenant and the module is now
        // declared .WithTenancy() (or the reverse) — the tables differ and the mismatch would
        // otherwise surface as a confusing missing-column error on the first append.
        PostgresMigrator.ValidateTenancyMode(
            options.ConnectionString,
            options.Schema,
            singleTenant: !definition.TenancyEnabled);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
