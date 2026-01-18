using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Extension methods for registering EF-based projections with the consumer.
/// </summary>
public static class EfConsumerBuilderExtensions
{
    /// <summary>
    /// Registers an async projection processor that uses Entity Framework for storage.
    /// Also registers an <see cref="IProjectionStateClearer"/> for rebuild support.
    /// </summary>
    /// <typeparam name="TEntity">The EF entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="processorId">Optional processor ID. Defaults to projection type name.</param>
    /// <returns>The consumer builder for chaining.</returns>
    public static ConsumerBuilder AddEfProjection<TEntity, TProjection, TDbContext>(
        this ConsumerBuilder builder,
        string? processorId = null)
        where TEntity : class, IProjectionEntity, new()
        where TProjection : Projection<TEntity>, new()
        where TDbContext : DbContext
    {
        var id = processorId ?? typeof(TProjection).Name;

        // Register the projection with the state store factory
        builder.AddProjection<TEntity, TProjection>(sp =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            return tenantId => new EfStateStore<TEntity, TDbContext>(contextFactory, tenantId);
        }, id);

        // Register clearer for rebuild support (keyed by module key)
        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(
            builder.ModuleKey,
            (sp, _) => new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                id));

        return builder;
    }

}
