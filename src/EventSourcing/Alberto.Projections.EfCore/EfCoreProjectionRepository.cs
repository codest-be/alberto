using Alberto.EventSourcing.Projections;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Projections.EfCore;

/// <summary>
/// Entity Framework Core implementation of IProjectionRepository.
/// Provides a bridge between Alberto's projection abstraction and EF Core DbContext.
/// Supports idempotent updates via IVersionedProjection.
/// </summary>
/// <typeparam name="TKey">The type of the projection's primary key</typeparam>
/// <typeparam name="TState">The projection entity type (must be an EF Core entity)</typeparam>
public class EfCoreProjectionRepository<TKey, TState>(DbContext context) : IProjectionRepository<TKey, TState>
    where TState : class, IHasKey<TKey>, IVersionedProjection, new()
    where TKey : notnull
{
    private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly DbSet<TState> _set = context.Set<TState>();

    public async Task<TState?> Get(TKey key, CancellationToken cancellationToken = default)
    {
        return await _set.FindAsync([key], cancellationToken);
    }

    public async Task<IReadOnlyCollection<TState>> GetAll(CancellationToken cancellationToken = default)
    {
        return await _set.ToListAsync(cancellationToken);
    }

    public async Task<IDictionary<TKey, TState?>> BatchGet(IEnumerable<TKey> keys,
        CancellationToken cancellationToken = default)
    {
        var keyList = keys.ToList();
        var results = new Dictionary<TKey, TState?>();

        foreach (var key in keyList)
        {
            var state = await Get(key, cancellationToken);
            results[key] = state;
        }

        return results;
    }

    public async Task Upsert(TKey key, TState state, CancellationToken cancellationToken = default)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));

        var existing = await Get(key, cancellationToken);

        if (existing == null)
        {
            _set.Add(state);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(state);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task Update(TKey key, Func<TState, TState> updateFn, CancellationToken cancellationToken = default)
    {
        var existing = await Get(key, cancellationToken);
        var updated = existing != null ? updateFn(existing) : updateFn(new TState());

        if (existing == null)
        {
            _set.Add(updated);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(updated);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateWithVersion(TKey key, Func<TState, TState> updateFn, long globalVersion,
        CancellationToken cancellationToken = default)
    {
        var existing = await Get(key, cancellationToken);

        // Check if we should skip due to version
        if (existing != null && existing.GlobalVersion >= globalVersion)
            return false;

        var updated = existing != null ? updateFn(existing) : updateFn(new TState());
        updated.GlobalVersion = globalVersion;

        if (existing == null)
        {
            _set.Add(updated);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(updated);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> BatchUpsertWithVersion(IDictionary<TKey, (TState State, long Version)> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
            return 0;

        var updatedCount = 0;

        foreach (var (key, (state, version)) in updates)
        {
            var existing = await Get(key, cancellationToken);

            // Skip if version is too old
            if (existing != null && existing.GlobalVersion >= version)
                continue;

            state.GlobalVersion = version;

            if (existing == null)
            {
                _set.Add(state);
            }
            else
            {
                _context.Entry(existing).CurrentValues.SetValues(state);
            }

            updatedCount++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return updatedCount;
    }

    public async Task Delete(TKey key, CancellationToken cancellationToken = default)
    {
        var existing = await Get(key, cancellationToken);
        if (existing != null)
        {
            _set.Remove(existing);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> Exists(TKey key, CancellationToken cancellationToken = default)
    {
        return await Get(key, cancellationToken) != null;
    }

    public async Task Clear(CancellationToken cancellationToken = default)
    {
        var all = await _set.ToListAsync(cancellationToken);
        _set.RemoveRange(all);
        await _context.SaveChangesAsync(cancellationToken);
    }
}