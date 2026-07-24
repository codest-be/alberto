using Alberto.Dcb.EntityFramework.Inline;
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
    /// Registers an EF projection using the declaration-based API.
    /// Defaults to <see cref="ProjectionMode.Async"/> — processed via the background polling consumer.
    /// Pass <see cref="ProjectionMode.Inline"/> to instead run the projection inside the
    /// <see cref="IEventStore.AppendAsync"/> call for read-your-writes consistency.
    /// </summary>
    /// <typeparam name="TEntity">The projection entity type.</typeparam>
    /// <typeparam name="TDbContext">The EF DbContext containing the entity DbSet.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="declaration">The projection declaration.</param>
    /// <param name="mode">
    /// <see cref="ProjectionMode.Async"/> (default) registers a polling-consumer processor and a
    /// rebuildable state clearer. <see cref="ProjectionMode.Inline"/> registers an
    /// <see cref="IInlineProjection"/> that runs immediately after each append.
    /// </param>
    public static DcbModuleBuilder AddEfProjection<TEntity, TDbContext>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TEntity> declaration,
        ProjectionMode mode = ProjectionMode.Async)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (mode == ProjectionMode.Inline)
        {
            builder.Services.AddKeyedSingleton<IInlineProjection>(builder.ModuleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                return new DeclaredEfInlineProjection<TEntity, TDbContext>(declaration, contextFactory);
            });
            return builder;
        }

        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, key) =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            var version = LiveVersionSelector(sp, key, declaration.ProcessorId);
            return new DeclaredAsyncProjection<TEntity>(declaration,
                () => new EfStateStore<TEntity, TDbContext>(contextFactory, version));
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

        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, key) =>
        {
            var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
            var version = LiveVersionSelector(sp, key, declaration.ProcessorId);
            return new DeclaredAsyncProjection<TEntity>(declaration,
                () => new EfStateStore<TEntity, TDbContext>(contextFactory, version),
                afterCommit: afterCommit(sp));
        });
        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(builder.ModuleKey, (sp, _) =>
            new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                declaration.ProcessorId));
        return builder;
    }

    /// <summary>
    /// The version selector for the live projection: tracks promotions when the module has a
    /// rebuild pipeline, and resolves to version 1 forever when it does not.
    /// </summary>
    private static Func<int> LiveVersionSelector(
        IServiceProvider sp, object? moduleKey, string processorId)
        => sp.GetKeyedService<ProjectionVersions>(moduleKey)?.ForLive(processorId)
           ?? ProjectionVersions.NeverRebuilt;
}
