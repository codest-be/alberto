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
            var version = ProjectionStoreContext.LiveVersion(sp, key, declaration.ProcessorId);
            return new DeclaredAsyncProjection<TEntity>(declaration,
                () => new EfStateStore<TEntity, TDbContext>(contextFactory, version));
        });
        RegisterRebuildSupport<TEntity, TDbContext>(builder, declaration);
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
            var version = ProjectionStoreContext.LiveVersion(sp, key, declaration.ProcessorId);
            return new DeclaredAsyncProjection<TEntity>(declaration,
                () => new EfStateStore<TEntity, TDbContext>(contextFactory, version),
                afterCommit: afterCommit(sp));
        });
        RegisterRebuildSupport<TEntity, TDbContext>(builder, declaration);
        return builder;
    }

    /// <summary>
    /// Registers what a rebuild needs on top of the live processor: a way to stand a second
    /// copy of the projection up against a different version, and a way to delete a version
    /// once it is no longer reachable.
    /// </summary>
    /// <remarks>
    /// EF projections live in the consumer's own tables, which the promotion transaction cannot
    /// reach — hence the clearer. The projection type is the processor id because
    /// <see cref="EfStateStore{TEntity,TDbContext}"/> stores nothing in
    /// <c>alberto_projection_states</c> at all.
    /// </remarks>
    private static void RegisterRebuildSupport<TEntity, TDbContext>(
        DcbModuleBuilder builder, ProjectionDeclaration<TEntity> declaration)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        builder.Services.AddKeyedSingleton<IProjectionStateClearer>(builder.ModuleKey, (sp, _) =>
            new EfProjectionStateClearer<TEntity, TDbContext>(
                sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                declaration.ProcessorId));

        builder.Services.AddKeyedSingleton(builder.ModuleKey, (sp, _) =>
            new RebuildableProjection(
                declaration.ProcessorId,
                declaration.ProcessorId,
                version => new DeclaredAsyncProjection<TEntity>(
                    declaration,
                    () => new EfStateStore<TEntity, TDbContext>(
                        sp.GetRequiredService<IDbContextFactory<TDbContext>>(), version))));
    }
}
