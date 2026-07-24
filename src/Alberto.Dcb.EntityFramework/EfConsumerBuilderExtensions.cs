using Alberto.Dcb.Configuration;
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
    /// <returns>The module builder for chaining.</returns>
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
            return builder.Register(context =>
                context.Services.AddKeyedSingleton<IInlineProjection>(context.ModuleKey, (sp, _) =>
                {
                    var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                    return new DeclaredEfInlineProjection<TEntity, TDbContext>(declaration, contextFactory);
                }));
        }

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        return builder.Register(context =>
        {
            context.Services.AddKeyedSingleton<IEventProcessor>(context.ModuleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                return new DeclaredAsyncProjection<TEntity>(declaration,
                    () => new EfStateStore<TEntity, TDbContext>(contextFactory));
            });
            context.Services.AddKeyedSingleton<IProjectionStateClearer>(context.ModuleKey, (sp, _) =>
                new EfProjectionStateClearer<TEntity, TDbContext>(
                    sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                    declaration.ProcessorId));
        });
    }

    /// <summary>
    /// Registers an async projection processor with a post-commit callback.
    /// The <paramref name="afterCommit"/> factory resolves dependencies at startup
    /// and returns a callback invoked after each successful SaveChanges,
    /// receiving only the events the projection actually handled.
    /// </summary>
    /// <typeparam name="TEntity">The projection entity type.</typeparam>
    /// <typeparam name="TDbContext">The EF DbContext containing the entity DbSet.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="declaration">The projection declaration.</param>
    /// <param name="afterCommit">
    /// A factory that resolves dependencies at startup and returns the post-commit callback.
    /// </param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder AddEfProjection<TEntity, TDbContext>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TEntity> declaration,
        Func<IServiceProvider, Func<IReadOnlyList<IEventEnvelope>, CancellationToken, Task>> afterCommit)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(afterCommit);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        return builder.Register(context =>
        {
            context.Services.AddKeyedSingleton<IEventProcessor>(context.ModuleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                return new DeclaredAsyncProjection<TEntity>(declaration,
                    () => new EfStateStore<TEntity, TDbContext>(contextFactory),
                    afterCommit: afterCommit(sp));
            });
            context.Services.AddKeyedSingleton<IProjectionStateClearer>(context.ModuleKey, (sp, _) =>
                new EfProjectionStateClearer<TEntity, TDbContext>(
                    sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                    declaration.ProcessorId));
        });
    }
}
