using System.Diagnostics.CodeAnalysis;
using Alberto.Configuration;
using Alberto.EntityFramework.Inline;
using Alberto.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alberto.EntityFramework;

/// <summary>
/// Extension methods for registering EF-based projections with a DCB module.
/// </summary>
/// <remarks>
/// Every <see cref="EfStateStore{TEntity,TDbContext}"/> here is built for all tenants at once —
/// the <c>_ =&gt;</c> in each registration below discards the tenant deliberately. An EF projection
/// keeps tenancy as an ordinary column that the projection body writes from
/// <c>ProjectionContext.TenantId</c>, so the store has no tenancy of its own to fix and one
/// instance serves every tenant. The JSONB stores in <c>Alberto.Postgres</c> are the opposite:
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
    /// <param name="documentIds">
    /// Required on a module that declared <c>.WithTenancy()</c> — see
    /// <see cref="EfDocumentIdUniqueness"/>. Ignored on a single-tenant module.
    /// </param>
    /// <returns>The module builder for chaining.</returns>
    [SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "The other overload is not a longer form of this one. It registers a " +
                        "DeclaredAsyncProjection unconditionally, so there is no ProjectionMode for " +
                        "it to take; giving it one would mean accepting a value it must reject.")]
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "Both overloads carry the same trailing documentIds parameter because the " +
                        "tenancy question it answers is a property of the declaration, and both " +
                        "register a store that cannot filter by tenant. They stay unambiguous: the " +
                        "third parameter is an enum here and a delegate there, so no call site can " +
                        "drift between them. Nothing has shipped yet — the whole baseline is " +
                        "Unshipped — so there is no binary compatibility to preserve either.")]
    public static DcbModuleBuilder AddEfProjection<TEntity, TDbContext>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TEntity> declaration,
        ProjectionMode mode = ProjectionMode.Async,
        EfDocumentIdUniqueness documentIds = EfDocumentIdUniqueness.NotDeclared)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declaration);

        // Reject ids that contain ':' or '#' (DI-key separators) or a reserved word before any
        // service-collection writes, including the inline path that skips DeclareProcessor.
        // The rule and error format match the core AddProjection / ReactTo path exactly because
        // they call the same helper.
        DcbModuleBuilderExtensions.ValidateProcessorIdArg(declaration.ProcessorId, builder.ModuleKey);

        if (mode == ProjectionMode.Inline)
        {
            return builder.Register(context =>
            {
                GuardTenancy(context, declaration.ProcessorId, documentIds);

                context.Services.AddKeyedSingleton<IInlineProjection>(context.ModuleKey, (sp, _) =>
                {
                    var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();

                    // An inline projection is not itself rebuildable, but the entity it writes
                    // can be: register the same entity as async as well, rebuild that, and the
                    // table carries two versions per document. The inline path follows the live
                    // version like every reader does, rather than seeing both.
                    var version = ProjectionVersions.LiveVersion(
                        sp, context.ModuleKey, declaration.ProcessorId);

                    return new DeclaredEfInlineProjection<TEntity, TDbContext>(
                        declaration,
                        contextFactory,
                        version,
                        sp.GetService<ILogger<DeclaredEfInlineProjection<TEntity, TDbContext>>>());
                });
            });
        }

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        return builder.Register(context =>
        {
            GuardTenancy(context, declaration.ProcessorId, documentIds);

            var moduleKey = context.ModuleKey;
            context.Services.AddKeyedSingleton<IEventProcessor>(moduleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
                return new DeclaredAsyncProjection<TEntity>(declaration,
                    _ => new EfStateStore<TEntity, TDbContext>(contextFactory, version),
                    serializer: serializer);
            });
            context.Services.AddKeyedSingleton<IProjectionStateClearer>(moduleKey, (sp, _) =>
                new EfProjectionStateClearer<TEntity, TDbContext>(
                    sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                    declaration.ProcessorId));
            context.Services.AddKeyedSingleton(moduleKey, (sp, _) =>
            {
                var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
                return new RebuildableProjection(
                    declaration.ProcessorId,
                    declaration.ProcessorId,
                    version => new DeclaredAsyncProjection<TEntity>(
                        declaration,
                        _ => new EfStateStore<TEntity, TDbContext>(
                            sp.GetRequiredService<IDbContextFactory<TDbContext>>(), version),
                        serializer: serializer));
            });
        });
    }

    /// <summary>
    /// Registers an async projection processor with a post-commit callback.
    /// The <paramref name="afterCommit"/> factory resolves dependencies at startup
    /// and returns a callback invoked after each successful SaveChanges,
    /// receiving only the events the projection actually handled.
    /// </summary>
    /// <remarks>
    /// The callback fires on <b>live processing only</b>. A projection rebuild replays the whole
    /// log into a shadow version and deliberately does not fire it — see the comment on the
    /// <c>RebuildableProjection</c> registration below.
    /// </remarks>
    /// <typeparam name="TEntity">The projection entity type.</typeparam>
    /// <typeparam name="TDbContext">The EF DbContext containing the entity DbSet.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="declaration">The projection declaration.</param>
    /// <param name="afterCommit">
    /// A factory that resolves dependencies at startup and returns the post-commit callback.
    /// Invoked after each successful <c>SaveChanges</c> on the live processor, never during a
    /// rebuild replay.
    /// </param>
    /// <param name="documentIds">
    /// Required on a module that declared <c>.WithTenancy()</c> — see
    /// <see cref="EfDocumentIdUniqueness"/>. Ignored on a single-tenant module.
    /// </param>
    /// <returns>The module builder for chaining.</returns>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "See the matching suppression on the ProjectionMode overload.")]
    public static DcbModuleBuilder AddEfProjection<TEntity, TDbContext>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TEntity> declaration,
        Func<IServiceProvider, Func<IReadOnlyList<IEventEnvelope>, CancellationToken, Task>> afterCommit,
        EfDocumentIdUniqueness documentIds = EfDocumentIdUniqueness.NotDeclared)
        where TEntity : class, IProjectionEntity, new()
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(afterCommit);

        // Same separator / reserved-word guard as the first overload and the core AddProjection path.
        DcbModuleBuilderExtensions.ValidateProcessorIdArg(declaration.ProcessorId, builder.ModuleKey);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        return builder.Register(context =>
        {
            GuardTenancy(context, declaration.ProcessorId, documentIds);

            var moduleKey = context.ModuleKey;
            context.Services.AddKeyedSingleton<IEventProcessor>(moduleKey, (sp, _) =>
            {
                var contextFactory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
                var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
                return new DeclaredAsyncProjection<TEntity>(declaration,
                    _ => new EfStateStore<TEntity, TDbContext>(contextFactory, version),
                    afterCommit: afterCommit(sp),
                    serializer: serializer);
            });
            context.Services.AddKeyedSingleton<IProjectionStateClearer>(moduleKey, (sp, _) =>
                new EfProjectionStateClearer<TEntity, TDbContext>(
                    sp.GetRequiredService<IDbContextFactory<TDbContext>>(),
                    declaration.ProcessorId));
            // afterCommit is deliberately NOT passed here. This factory builds the *shadow*
            // processor, which replays the entire log from position 0 into a version no reader
            // can see yet. A post-commit callback is a side effect on the outside world —
            // notifications, cache invalidation, downstream messages — and firing it per replayed
            // event would re-emit every one of those for the whole of history, once per rebuild,
            // for state that is not even live. That is unrecoverable in a way the opposite
            // omission is not: a callback maintaining something derived can be re-run after
            // promotion, but a webhook that has already fired cannot be unfired.
            //
            // The consequence, stated so it is a choice and not a surprise: anything the callback
            // maintains outside TDbContext is not rebuilt by a projection rebuild, and needs its
            // own path. See the <remarks> on the overload above.
            context.Services.AddKeyedSingleton(moduleKey, (sp, _) =>
            {
                var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
                return new RebuildableProjection(
                    declaration.ProcessorId,
                    declaration.ProcessorId,
                    version => new DeclaredAsyncProjection<TEntity>(
                        declaration,
                        _ => new EfStateStore<TEntity, TDbContext>(
                            sp.GetRequiredService<IDbContextFactory<TDbContext>>(), version),
                        serializer: serializer));
            });
        });
    }

    /// <summary>
    /// Refuses an EF projection on a tenant-enabled module unless the caller has stated that its
    /// document ids are unique across tenants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs inside the deferred registration callback rather than at the call site, because
    /// <c>.WithTenancy()</c> may legitimately appear after <c>AddEfProjection</c> in the chain —
    /// the callbacks do not run until the whole module lambda has completed, so by then the
    /// declaration is final either way.
    /// </para>
    /// <para>
    /// This is the one tenancy question Alberto cannot answer for the caller. A JSONB state
    /// store's tenancy is decided by the schema it was migrated with, so <c>AddProjection</c>
    /// just builds one store per tenant. An EF entity's table belongs to the application, and
    /// <see cref="IProjectionEntity"/> exposes only <c>DocumentId</c> and <c>RebuildVersion</c>
    /// — so whether two tenants can collide is a fact about the declaration's id selectors,
    /// visible to nothing here.
    /// </para>
    /// </remarks>
    private static void GuardTenancy(
        AlbertoModuleContext context,
        string processorId,
        EfDocumentIdUniqueness documentIds)
    {
        if (!context.TenancyEnabled || documentIds == EfDocumentIdUniqueness.AcrossTenants)
            return;

        throw new InvalidOperationException(
            $"ALB0027: Alberto module '{context.ModuleKey}' declared .WithTenancy(), but the EF " +
            $"projection '{processorId}' stores its state keyed by document id alone. " +
            "IProjectionEntity carries no tenant column, so both EfStateStore and the inline " +
            "projection load and write by (DocumentId, RebuildVersion) with no tenant predicate " +
            "— adding a TenantId column to the entity does not change what the store queries on. " +
            "Two tenants that produce the same document id therefore share one row: the last " +
            "write wins, and every read after it returns the other tenant's data." +
            Environment.NewLine +
            "  → If this declaration's document ids are already unique across every tenant — a " +
            "GUID aggregate id, say — state that and the registration proceeds: " +
            ".AddEfProjection<TEntity, TDbContext>(declaration, documentIds: " +
            "EfDocumentIdUniqueness.AcrossTenants)." + Environment.NewLine +
            "  → If they are not, make them so by prefixing a tenant discriminator the event " +
            "itself carries (id: e => $\"{e.TenantId}/{e.OrderId}\"), give each tenant its own " +
            "database with .WithTenancy(t => t.AcrossPostgresDatabases(...)), or use the JSONB " +
            "state store via AddProjection, whose tenancy is part of the migrated schema.");
    }
}
