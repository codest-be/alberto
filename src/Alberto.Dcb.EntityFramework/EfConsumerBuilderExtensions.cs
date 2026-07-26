using Alberto.Dcb.Configuration;
using Alberto.Dcb.EntityFramework.Inline;
using Alberto.Dcb.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.EntityFramework;

/// <summary>
/// Extension methods for registering EF-based projections with a DCB module.
/// </summary>
/// <remarks>
/// Every <see cref="EfStateStore{TEntity,TDbContext}"/> here is built for all tenants at once —
/// the <c>_ =&gt;</c> in each registration below discards the tenant deliberately. An EF projection
/// keeps tenancy as an ordinary column that the projection body writes from
/// <c>ProjectionContext.TenantId</c>, so the store has no tenancy of its own to fix and one
/// instance serves every tenant. The JSONB stores in <c>Alberto.Dcb.Postgres</c> are the opposite:
/// their tenancy is baked into the table's primary key when the module is migrated, so they need
/// one store per tenant and <c>AddProjection</c> takes a tenant-keyed factory.
/// </remarks>
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
        ArgumentNullException.ThrowIfNull(builder);
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
            var moduleKey = context.ModuleKey;
            context.Services.AddKeyedSingleton<IEventProcessor>(moduleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                return new DeclaredAsyncProjection<TEntity>(declaration,
                    _ => new EfStateStore<TEntity, TDbContext>(contextFactory, version));
            });
            context.Services.AddKeyedSingleton<IProjectionStateClearer>(moduleKey, (sp, _) =>
                new EfProjectionStateClearer<TEntity, TDbContext>(
                    sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                    declaration.ProcessorId));
            context.Services.AddKeyedSingleton(moduleKey, (sp, _) =>
                new RebuildableProjection(
                    declaration.ProcessorId,
                    declaration.ProcessorId,
                    version => new DeclaredAsyncProjection<TEntity>(
                        declaration,
                        _ => new EfStateStore<TEntity, TDbContext>(
                            sp.GetRequiredService<IDbContextFactory<TDbContext>>(), version))));
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
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(afterCommit);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        return builder.Register(context =>
        {
            var moduleKey = context.ModuleKey;
            context.Services.AddKeyedSingleton<IEventProcessor>(moduleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                return new DeclaredAsyncProjection<TEntity>(declaration,
                    _ => new EfStateStore<TEntity, TDbContext>(contextFactory, version),
                    afterCommit: afterCommit(sp));
            });
            context.Services.AddKeyedSingleton<IProjectionStateClearer>(moduleKey, (sp, _) =>
                new EfProjectionStateClearer<TEntity, TDbContext>(
                    sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                    declaration.ProcessorId));
            context.Services.AddKeyedSingleton(moduleKey, (sp, _) =>
                new RebuildableProjection(
                    declaration.ProcessorId,
                    declaration.ProcessorId,
                    version => new DeclaredAsyncProjection<TEntity>(
                        declaration,
                        _ => new EfStateStore<TEntity, TDbContext>(
                            sp.GetRequiredService<IDbContextFactory<TDbContext>>(), version))));
        });
    }
}
