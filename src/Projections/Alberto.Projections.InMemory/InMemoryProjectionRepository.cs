using System.Collections.Concurrent;
using Alberto.EventStore.MultiTenant;

namespace Alberto.Projections.InMemory;

/// <summary>
/// In-memory implementation of IProjectionRepository using ConcurrentDictionary.
/// Thread-safe and suitable for development, testing, and low-scale production scenarios.
/// </summary>
/// <typeparam name="TKey">The type of the projection key</typeparam>
/// <typeparam name="TState">The projected state type</typeparam>
public sealed class InMemoryProjectionRepository<TKey, TState> : IProjectionRepository<TKey, TState>
    where TKey : notnull
    where TState : new()
{
    private readonly ConcurrentDictionary<string, TState> _store = new();
    private readonly ITenantContext _tenantContext;

    public InMemoryProjectionRepository(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public Task<TState?> Get(TKey key, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        return Task.FromResult(_store.TryGetValue(tenantKey, out var state) ? state : default(TState?));
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<TState>> GetAll(CancellationToken cancellationToken = default)
    {
        var tenantPrefix = $"{_tenantContext.Tenant.Id}:";
        var tenantStates = _store
            .Where(kvp => kvp.Key.StartsWith(tenantPrefix))
            .Select(kvp => kvp.Value)
            .ToList();
        return Task.FromResult<IReadOnlyCollection<TState>>(tenantStates);
    }

    /// <inheritdoc />
    public Task Upsert(TKey key, TState state, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        _store[tenantKey] = state;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Update(TKey key, Func<TState, TState> updateFn, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        _store.AddOrUpdate(
            tenantKey,
            _ => updateFn(new TState()),
            (_, existing) => updateFn(existing)
        );
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Delete(TKey key, CancellationToken cancellationToken = default)
    {
        var tenantKey = GetTenantKey(key);
        _store.TryRemove(tenantKey, out _);
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
        var tenantPrefix = $"{_tenantContext.Tenant.Id}:";
        var keysToRemove = _store.Keys.Where(k => k.StartsWith(tenantPrefix)).ToList();
        foreach (var key in keysToRemove)
        {
            _store.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private string GetTenantKey(TKey key) => $"{_tenantContext.Tenant.Id}:{key}";
}