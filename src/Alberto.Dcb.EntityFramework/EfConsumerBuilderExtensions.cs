using Alberto.Dcb;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Extension methods for registering EF-based projections with a DCB module.
/// </summary>
public static class EfConsumerBuilderExtensions
{
    /// <summary>
    /// Registers an async projection processor using the declaration-based API with Entity Framework storage.
    /// Also registers an <see cref="IProjectionStateClearer"/> for rebuild support.
    /// This is the recommended approach — no reflection, no base classes required.
    /// </summary>
    public static DcbModuleBuilder AddEfProjection<TEntity, TDbContext>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TEntity> declaration)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(declaration);

        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            return new DeclaredAsyncProjection<TEntity>(declaration,
                () => new EfStateStore<TEntity, TDbContext>(contextFactory));
        });
        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(builder.ModuleKey, (sp, _) =>
            new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                declaration.ProcessorId));
        return builder;
    }

    /// <summary>
    /// Registers an async projection processor with a post-commit callback.
    /// The <paramref name="afterCommit"/> factory resolves dependencies at startup
    /// and returns a callback invoked after each successful SaveChanges,
    /// receiving only the events the projection actually handled.
    /// </summary>
    public static DcbModuleBuilder AddEfProjection<TEntity, TDbContext>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TEntity> declaration,
        Func<IServiceProvider, Func<IReadOnlyList<IEventEnvelope>, CancellationToken, Task>> afterCommit)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(afterCommit);

        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            return new DeclaredAsyncProjection<TEntity>(declaration,
                () => new EfStateStore<TEntity, TDbContext>(contextFactory),
                afterCommit: afterCommit(sp));
        });
        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(builder.ModuleKey, (sp, _) =>
            new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                declaration.ProcessorId));
        return builder;
    }
}
