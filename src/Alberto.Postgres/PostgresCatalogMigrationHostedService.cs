using Alberto.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Postgres;

/// <summary>
/// Creates the tenant shard catalog's table at startup.
/// </summary>
/// <remarks>
/// Unlike a shard's migration this one is fatal when it fails. A shard being unreachable costs
/// its own tenants; the catalog being unreachable costs every tenant of the module, because no
/// request can be routed at all.
/// </remarks>
internal sealed class PostgresCatalogMigrationHostedService(
    string moduleKey,
    IOptionsMonitor<AlbertoModuleDefinition> definitions,
    ILogger<PostgresCatalogMigrationHostedService>? logger = null) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var definition = definitions.Get(moduleKey);

        if (definition.Tenancy.Catalog is not PostgresBackendDescriptor descriptor)
            return Task.CompletedTask;

        var options = descriptor.Options;
        if (!options.AutoMigrate)
            return Task.CompletedTask;

        logger?.LogInformation(
            "Applying the tenant shard catalog migration for module {ModuleKey} to schema {Schema}.",
            moduleKey, options.Schema ?? "(default)");

        MigrationResult result;
        try
        {
            result = PostgresCatalogMigrator.Migrate(options.ConnectionString, options.Schema);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The tenant shard catalog migration failed for module '{moduleKey}': {ex.Message}", ex);
        }

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"The tenant shard catalog migration failed for module '{moduleKey}': " +
                $"{result.Error?.Message}", result.Error);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
