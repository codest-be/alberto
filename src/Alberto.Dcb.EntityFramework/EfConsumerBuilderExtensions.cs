using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Extension methods for registering EF-based projections with the consumer.
/// </summary>
public static class EfConsumerBuilderExtensions
{
#pragma warning disable CS0618
    [Obsolete("Use DeclareProjection.For<TState>() instead. This type will be removed in a future version.")]
    public static ConsumerBuilder AddEfProjection<TEntity, TProjection, TDbContext>(
        this ConsumerBuilder builder,
        string? processorId = null)
        where TEntity : class, IProjectionEntity, new()
        where TProjection : Projection<TEntity>, new()
        where TDbContext : DbContext
    {
        var id = processorId ?? typeof(TProjection).Name;

        builder.AddProjection<TEntity, TProjection>(sp =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            return () => new EfStateStore<TEntity, TDbContext>(contextFactory);
        }, id);

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
    public static ConsumerBuilder AddEfProjection<TEntity, TDbContext>(
        this ConsumerBuilder builder,
        ProjectionDeclaration<TEntity> declaration)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(declaration);

        builder.AddProjection(declaration, sp =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            return () => new EfStateStore<TEntity, TDbContext>(contextFactory);
        });

        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(
            builder.ModuleKey,
            (sp, _) => new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                declaration.ProcessorId));

        return builder;
    }
}
