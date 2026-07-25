using Alberto.Dcb.Configuration;
using Alberto.Dcb.Tenancy;
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
/// <remarks>
/// A single-database module that cannot migrate fails startup: there is nothing left for the
/// process to serve. One shard of a sharded module is different — the other databases are healthy
/// and their tenants are unaffected — so a shard's failure is recorded on
/// <see cref="ShardHealth"/> and retried in the background instead of taking the host down with
/// it. Requests routed to that shard fail; requests routed anywhere else do not.
/// </remarks>
internal sealed class AlbertoMigrationHostedService(
    string moduleKey,
    IOptionsMonitor<AlbertoModuleDefinition> definitions,
    ILogger<AlbertoMigrationHostedService>? logger = null,
    ShardHealth? shardHealth = null,
    string? shardId = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _retryLoop;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var failure = TryMigrate();

        if (failure is null)
        {
            if (shardId is not null)
                shardHealth?.Report(shardId, null);

            return Task.CompletedTask;
        }

        if (shardId is null || shardHealth is null)
            throw failure;

        logger?.LogError(
            failure,
            "Alberto schema migration failed for shard {ShardId} of module {ModuleKey}. " +
            "The other shards are unaffected; this one will be retried in the background.",
            shardId, moduleKey);

        shardHealth.Report(shardId, failure);
        _retryLoop = RetryUntilMigratedAsync(_stopping.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();

        if (_retryLoop is not null)
        {
            try
            {
                await _retryLoop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown either way.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Runs the migration and the tenancy-mode check. Returns the failure rather than throwing so
    /// the caller decides whether it is fatal.
    /// </summary>
    private Exception? TryMigrate()
    {
        var definition = definitions.Get(moduleKey);

        if (definition.Backend is not PostgresBackendDescriptor descriptor)
            return null;

        var options = descriptor.Options;

        if (options.AutoMigrate)
        {
            logger?.LogInformation(
                "Applying Alberto migrations for {Module} to schema {Schema}.",
                definition.DisplayName, options.Schema ?? "(default)");

            // Pass singleTenant so the correct migration script folder is selected. Omitting it
            // would silently run the multi-tenant scripts for single-tenant modules.
            //
            // DbUp's EnsureDatabase step may throw directly (e.g. NpgsqlException) rather than
            // returning a failed MigrationResult, so both paths become one consistent
            // InvalidOperationException naming the module.
            MigrationResult result;
            try
            {
                result = PostgresMigrator.Migrate(
                    options.ConnectionString, options.Schema, singleTenant: !definition.TenancyEnabled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new InvalidOperationException(
                    $"Alberto schema migration failed for module '{definition.DisplayName}': {ex.Message}", ex);
            }

            if (!result.Successful)
            {
                return new InvalidOperationException(
                    $"Alberto schema migration failed for module '{definition.DisplayName}': " +
                    $"{result.Error?.Message}", result.Error);
            }
        }

        // Catches the case where a schema was created single-tenant and the module is now
        // declared .WithTenancy() (or the reverse) — the tables differ and the mismatch would
        // otherwise surface as a confusing missing-column error on the first append.
        try
        {
            PostgresMigrator.ValidateTenancyMode(
                options.ConnectionString,
                options.Schema,
                singleTenant: !definition.TenancyEnabled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex;
        }

        return null;
    }

    private async Task RetryUntilMigratedAsync(CancellationToken cancellationToken)
    {
        // Yield first so a shard that is down does not hold up the rest of the host's startup.
        await Task.Yield();

        var delay = FirstRetryDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var failure = TryMigrate();
            if (failure is null)
            {
                logger?.LogInformation(
                    "Shard {ShardId} of module {ModuleKey} migrated successfully on retry.",
                    shardId, moduleKey);
                shardHealth!.Report(shardId!, null);
                return;
            }

            logger?.LogWarning(
                failure,
                "Shard {ShardId} of module {ModuleKey} is still unavailable. Retrying in {Delay}.",
                shardId, moduleKey, delay);
            shardHealth!.Report(shardId!, failure);

            delay = delay >= MaxRetryDelay ? MaxRetryDelay : delay + delay;
        }
    }
}
