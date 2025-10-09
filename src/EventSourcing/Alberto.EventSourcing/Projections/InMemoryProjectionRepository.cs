using System.Collections.Concurrent;

namespace Alberto.EventSourcing.Projections;

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
    private readonly ConcurrentDictionary<TKey, TState> _store = new();

    /// <inheritdoc />
    public Task<TState?> Get(TKey key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_store.TryGetValue(key, out var state) ? state : default(TState?));
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<TState>> GetAll(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<TState>>(_store.Values.ToList());
    }

    /// <inheritdoc />
    public Task Upsert(TKey key, TState state, CancellationToken cancellationToken = default)
    {
        _store[key] = state;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Update(TKey key, Func<TState, TState> updateFn, CancellationToken cancellationToken = default)
    {
        _store.AddOrUpdate(
            key,
            _ => updateFn(new TState()),
            (_, existing) => updateFn(existing)
        );
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Delete(TKey key, CancellationToken cancellationToken = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> Exists(TKey key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_store.ContainsKey(key));
    }

    /// <inheritdoc />
    public Task Clear(CancellationToken cancellationToken = default)
    {
        _store.Clear();
        return Task.CompletedTask;
    }
}