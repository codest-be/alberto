using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Entity Framework implementation of <see cref="IProjectionStateClearer"/>.
/// Clears all entities from the backing table during a projection rebuild.
/// </summary>
/// <typeparam name="TEntity">The entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
/// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
internal sealed class EfProjectionStateClearer<TEntity, TDbContext> : IProjectionStateClearer
    where TEntity : class, IProjectionEntity
    where TDbContext : DbContext
{
    private readonly IDbContextFactory<TDbContext> _contextFactory;

    /// <summary>
    /// Creates a new EF projection state clearer.
    /// </summary>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    /// <param name="processorId">The processor ID this clearer is associated with.</param>
    public EfProjectionStateClearer(IDbContextFactory<TDbContext> contextFactory, string processorId)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        ProcessorId = processorId ?? throw new ArgumentNullException(nameof(processorId));
    }

    /// <inheritdoc/>
    public string ProcessorId { get; }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // ExecuteDeleteAsync performs a bulk delete without loading entities into memory
        await context.Set<TEntity>().ExecuteDeleteAsync(ct);
    }
}
