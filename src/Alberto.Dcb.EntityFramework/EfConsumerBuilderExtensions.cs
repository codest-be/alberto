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
#pragma warning disable CS0618 // Obsolete — this overload exists for backward compatibility
    [Obsolete("Use DeclareProjection.For<TState>() instead. This type will be removed in a future version.")]
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
#pragma warning restore CS0618

    /// <summary>
    /// Registers an async projection processor using the declaration-based API with Entity Framework storage.
    /// Also registers an <see cref="IProjectionStateClearer"/> for rebuild support.
    /// This is the recommended approach — no reflection, no base classes required.
    /// </summary>
    /// <typeparam name="TEntity">The EF entity type implementing <see cref="IProjectionEntity"/>.</typeparam>
    /// <typeparam name="TDbContext">The DbContext type containing the entity DbSet.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="declaration">
    /// A <see cref="ProjectionDeclaration{TState}"/> produced by
    /// <see cref="DeclareProjection.For{TState}"/>.
    /// </param>
    /// <returns>The consumer builder for chaining.</returns>
    public static ConsumerBuilder AddEfProjection<TEntity, TDbContext>(
        this ConsumerBuilder builder,
        ProjectionDeclaration<TEntity> declaration)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(declaration);

        // Register the projection with the EF state store factory
        builder.AddProjection(declaration, sp =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            return tenantId => new EfStateStore<TEntity, TDbContext>(contextFactory, tenantId);
        });

        // Register clearer for rebuild support (keyed by module key)
        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(
            builder.ModuleKey,
            (sp, _) => new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                declaration.ProcessorId));

        return builder;
    }
}
