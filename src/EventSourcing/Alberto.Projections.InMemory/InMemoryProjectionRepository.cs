using System.Collections.Concurrent;
using Alberto.EventStore.MultiTenant;
using Microsoft.Extensions.Logging;

namespace Alberto.Projections.InMemory;

/// <summary>
/// In-memory implementation of IProjectionRepository using ConcurrentDictionary.
/// Thread-safe and suitable for development, testing, and low-scale production scenarios.
/// </summary>
/// <typeparam name="TKey">The type of the projection key</typeparam>
/// <typeparam name="TState">The projected state type</typeparam>
public sealed class InMemoryProjectionRepository<TKey, TState>(
    ITenantContext tenantContext,
    ILogger<InMemoryProjectionRepository<TKey, TState>> logger)
    : IProjectionRepository<TKey, TState>
    where TKey : notnull
    where TState : new()
{
    private readonly ConcurrentDictionary<string, (TState State, long GlobalVersion)> _store = new();

    /// <inheritdoc />
    public Task<TState?> Get(TKey key, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        return Task.FromResult(_store.TryGetValue(tenantKey, out var entry) ? entry.State : default(TState?));
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<TState>> GetAll(CancellationToken cancellationToken = default)
    {
        var tenantPrefix = $"{tenantContext.Tenant.Id}:";
        var tenantStates = _store
            .Where(kvp => kvp.Key.StartsWith(tenantPrefix))
            .Select(kvp => kvp.Value.State)
            .ToList();
        return Task.FromResult<IReadOnlyCollection<TState>>(tenantStates);
    }

    /// <inheritdoc />
    public Task Upsert(TKey key, TState state, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        _store.AddOrUpdate(
            tenantKey,
            _ => (state, 0L),
            (_, existing) => (state, existing.GlobalVersion));

        logger.LogDebug("Upserted projection {Key} for tenant {TenantId}", key, tenantContext.Tenant.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Update(TKey key, Func<TState, TState> updateFn, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        _store.AddOrUpdate(
            tenantKey,
            _ => (updateFn(new TState()), 0L),
            (_, existing) => (updateFn(existing.State), existing.GlobalVersion));

        logger.LogDebug("Updated projection {Key} for tenant {TenantId}", key, tenantContext.Tenant.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> UpdateWithVersion(TKey key, Func<TState, TState> updateFn, long globalVersion,
        CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        var updated = false;

        _store.AddOrUpdate(
            tenantKey,
            _ =>
            {
                updated = true;
                return (updateFn(new TState()), globalVersion);
            },
            (_, existing) =>
            {
                if (existing.GlobalVersion >= globalVersion)
                {
                    logger.LogDebug(
                        "Skipping projection update for {Key} - event version {EventVersion} <= stored version {StoredVersion}",
                        key, globalVersion, existing.GlobalVersion);
                    return existing;
                }

                updated = true;
                return (updateFn(existing.State), globalVersion);
            });

        if (updated)
        {
            logger.LogDebug(
                "Updated projection {Key} for tenant {TenantId} to version {GlobalVersion}",
                key, tenantContext.Tenant.Id, globalVersion);
        }

        return Task.FromResult(updated);
    }

    /// <inheritdoc />
    public Task Delete(TKey key, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        _store.TryRemove(tenantKey, out _);
        logger.LogDebug("Deleted projection {Key} for tenant {TenantId}", key, tenantContext.Tenant.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> Exists(TKey key, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        return Task.FromResult(_store.ContainsKey(tenantKey));
    }

    /// <inheritdoc />
    public Task Clear(CancellationToken cancellationToken = default)
    {
        var tenantPrefix = $"{tenantContext.Tenant.Id}:";
        var keysToRemove = _store.Keys.Where(k => k.StartsWith(tenantPrefix)).ToList();
        foreach (var key in keysToRemove)
        {
            _store.TryRemove(key, out _);
        }

        logger.LogWarning("Cleared all projections for tenant {TenantId}", tenantContext.Tenant.Id);
        return Task.CompletedTask;
    }

    private string GetTenantKey(TKey key) => $"{tenantContext.Tenant.Id}:{key}";
}