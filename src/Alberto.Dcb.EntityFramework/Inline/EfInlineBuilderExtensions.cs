using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CS0618 // Obsolete projection types used intentionally for backward-compatibility
namespace Alberto.Dcb.EntityFramework.Inline;

/// <summary>
/// Extension methods for registering inline EF projections.
/// </summary>
public static class EfInlineBuilderExtensions
{
    /// <summary>
    /// Registers an inline EF projection that runs synchronously after event persistence.
    /// </summary>
    /// <typeparam name="TEntity">The EF entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
    /// <param name="configurator">The event store configurator (setup-time surface of the event store).</param>
    /// <param name="contextFactory">Factory for creating DbContext instances.</param>
    public static void RegisterEfInlineProjection<TEntity, TProjection, TDbContext>(
        this IEventStoreConfigurator configurator,
        IDbContextFactory<TDbContext> contextFactory)
        where TEntity : class, IProjectionEntity, new()
        where TProjection : Projection<TEntity>, new()
        where TDbContext : DbContext
    {
        var stateStore = new EfInlineStateStoreAdapter<TEntity, TDbContext>(contextFactory);
        configurator.RegisterInlineProjection<TEntity, TProjection>(stateStore);
    }

    /// <summary>
    /// Registers an inline EF projection that runs synchronously after event persistence.
    /// Uses the service provider to resolve the DbContext factory.
    /// </summary>
    /// <typeparam name="TEntity">The EF entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
    /// <param name="configurator">The event store configurator (setup-time surface of the event store).</param>
    /// <param name="serviceProvider">The service provider.</param>
    public static void RegisterEfInlineProjection<TEntity, TProjection, TDbContext>(
        this IEventStoreConfigurator configurator,
        IServiceProvider serviceProvider)
        where TEntity : class, IProjectionEntity, new()
        where TProjection : Projection<TEntity>, new()
        where TDbContext : DbContext
    {
        var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<TDbContext>>();
        configurator.RegisterEfInlineProjection<TEntity, TProjection, TDbContext>(contextFactory);
    }
}

/// <summary>
/// Adapter that bridges EfStateStore to IStateStore for inline projections.
/// Note: This is a simplified adapter that uses the first tenant from loaded documents.
/// For inline projections, tenant context comes from the event envelope.
/// </summary>
internal sealed class EfInlineStateStoreAdapter<TEntity, TDbContext>(IDbContextFactory<TDbContext> contextFactory)
    : IStateStore<TEntity>
    where TEntity : class, IProjectionEntity, new()
    where TDbContext : DbContext
{
    public async Task<Dictionary<string, TEntity>> LoadManyAsync(
        IEnumerable<string> documentIds,
        CancellationToken ct = default)
    {
        // This adapter is used via InlineProjection which passes events grouped by tenant
        // The inline projection code doesn't pass tenant via this interface, so we return empty
        // The actual EF inline projection uses EfInlineProjection which handles tenant correctly
        var ids = documentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<string, TEntity>();

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Load entities by document ID only (tenant context must come from elsewhere)
        var entities = await context.Set<TEntity>()
            .AsNoTracking()
            .Where(e => ids.Contains(e.DocumentId))
            .ToListAsync(ct);

        return entities.ToDictionary(e => e.DocumentId);
    }

    public async Task ApplyChangesAsync(
        IReadOnlyDictionary<string, TEntity> upserts,
        IReadOnlyCollection<string> deletes,
        CancellationToken ct = default)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        await using var context = await contextFactory.CreateDbContextAsync(ct);

        // Process deletes
        foreach (var docId in deletes)
        {
            var existing = await context.Set<TEntity>()
                .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);
            if (existing != null)
            {
                context.Set<TEntity>().Remove(existing);
            }
        }

        // Process upserts
        foreach (var (docId, entity) in upserts)
        {
            entity.DocumentId = docId;
            entity.UpdatedAt = DateTimeOffset.UtcNow;

            var existing = await context.Set<TEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.DocumentId == docId, ct);

            if (existing != null)
            {
                context.Set<TEntity>().Update(entity);
            }
            else
            {
                context.Set<TEntity>().Add(entity);
            }
        }

        await context.SaveChangesAsync(ct);
    }

}
