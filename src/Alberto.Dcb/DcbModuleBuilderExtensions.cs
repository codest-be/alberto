using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Alberto.Dcb.Upcasting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for <see cref="DcbModuleBuilder"/>.
/// </summary>
public static class DcbModuleBuilderExtensions
{
    /// <summary>
    /// Registers an async projection processor using the declaration-based API.
    /// </summary>
    /// <typeparam name="TState">The projection state type.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="declaration">The projection declaration.</param>
    /// <param name="stateStoreFactory">
    /// Builds the projection's state stores. The <see cref="ProjectionStoreContext"/> carries the
    /// service provider and the rebuild-version selector the store must resolve on every
    /// operation; the delegate it returns is called once per tenant, with that tenant's id
    /// (<see langword="null"/> in a module that declared no tenancy).
    /// <code>
    /// // one document set per tenant:
    /// .AddProjection(PaymentSummaryProjection.Declaration, ctx =>
    /// {
    ///     var dataSource = ctx.Services.GetRequiredKeyedService&lt;NpgsqlDataSource&gt;(ModuleKey);
    ///     return tenantId => new PostgresStateStore&lt;PaymentSummary&gt;(
    ///         dataSource, nameof(PaymentSummaryProjection), "payments",
    ///         ctx.RebuildVersion, tenantId);
    /// })
    ///
    /// // one document for all tenants:
    /// .AddProjection(OrdersOverviewProjection.Declaration, ctx =>
    /// {
    ///     var dataSource = ctx.Services.GetRequiredKeyedService&lt;NpgsqlDataSource&gt;(ModuleKey);
    ///     return tenantId => new PostgresStateStore&lt;OrdersOverview&gt;(
    ///         dataSource, nameof(OrdersOverviewProjection), "orders",
    ///         ctx.RebuildVersion, TenantScope.CrossTenantFor(tenantId));
    /// })
    /// </code>
    /// The tenant is an argument of the inner delegate rather than a field of the context so
    /// that everything resolved in the outer one is shared across tenants — a cross-tenant
    /// projection backed by a single in-memory dictionary depends on that.
    /// <para>
    /// A store that ignores <see cref="ProjectionStoreContext.RebuildVersion"/> still works — it
    /// simply cannot be rebuilt without downtime. A store that ignores the tenant does not:
    /// against a tenant-enabled schema it writes nothing and dead-letters every event — see the
    /// remarks on <c>PostgresStateStore</c>.
    /// </para>
    /// </param>
    /// <param name="projectionType">
    /// The key this projection's state rows are stored under, when it differs from the
    /// processor id. Only the rebuild pipeline needs this: promotion deletes the superseded
    /// version's rows from the state table, which is keyed by projection type.
    /// </param>
    /// <remarks>
    /// <para>
    /// In addition to the two <see cref="IEventProcessor"/> registrations (live and
    /// rebuild), this method registers the same
    /// <c>Func&lt;string?, IStateStore&lt;TState&gt;&gt;</c> the writer uses, keyed by
    /// <c>"{moduleKey}:{declaration.ProcessorId}"</c>. Read-side code — GraphQL
    /// resolvers, REST controllers, background jobs — should resolve this factory from DI
    /// rather than constructing a <see cref="Alberto.Dcb.Subscriptions.IStateStore{TState}"/>
    /// directly. It is literally the writer's factory, so the reader cannot disagree with the
    /// writer about tenancy, schema, or projection type; the reader chooses only which tenant
    /// to read, by passing one. Constructing a store independently is what produced the
    /// silent-empty-result class of bug this registration exists to prevent.
    /// </para>
    /// <para>
    /// Resolution is checked on first read, not at startup: a resolver that asks for an
    /// unregistered key throws when it first runs, not when the host is built.
    /// </para>
    /// <code>
    /// // per-tenant read (e.g. PaymentQueries.GetRecentPayments):
    /// var factory = sp.GetRequiredKeyedService&lt;Func&lt;string?, IStateStore&lt;PaymentSummary&gt;&gt;&gt;(
    ///     $"{PaymentsModule.ModuleKey}:{nameof(PaymentSummaryProjection)}");
    /// var store = factory(tenantId);
    ///
    /// // cross-tenant read (e.g. OrderQueries.GetOrdersOverview):
    /// var store = factory(TenantScope.CrossTenant);
    /// </code>
    /// </remarks>
    public static DcbModuleBuilder AddProjection<TState>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TState> declaration,
        Func<ProjectionStoreContext, Func<string?, IStateStore<TState>>> stateStoreFactory,
        string? projectionType = null)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);

        // Validate now, at registration time, before anything is written to the service
        // collection. A ':' or '#' in the processor id would make the composed reader store
        // factory key '{moduleKey}:{processorId}' ambiguous or unparseable; a reserved word
        // would shadow an internal Alberto registration under that same key.
        ValidateProcessorIdArg(declaration.ProcessorId, builder.ModuleKey);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        builder.Register(context =>
        {
            var moduleKey = context.ModuleKey;

            context.Services.AddKeyedSingleton<IEventProcessor>(moduleKey, (sp, _) =>
            {
                var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                var factory = stateStoreFactory(new ProjectionStoreContext { Services = sp, RebuildVersion = version });
                var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
                return new DeclaredAsyncProjection<TState>(declaration, factory, serializer: serializer);
            });

            context.Services.AddKeyedSingleton(moduleKey, (sp, _) =>
            {
                var serializer = sp.GetKeyedService<EventSerializer>(moduleKey);
                return new RebuildableProjection(
                    declaration.ProcessorId,
                    projectionType ?? declaration.ProcessorId,
                    version => new DeclaredAsyncProjection<TState>(
                        declaration,
                        stateStoreFactory(new ProjectionStoreContext { Services = sp, RebuildVersion = version }),
                        serializer: serializer));
            });

            // Reader store factory — keyed by "{moduleKey}:{processorId}".
            // Read-side code resolves this instead of constructing a store directly, so
            // the tenancy mode and schema are always inherited from the writer's factory.
            context.Services.AddKeyedSingleton<Func<string?, IStateStore<TState>>>(
                $"{moduleKey}:{declaration.ProcessorId}",
                (sp, _) =>
                {
                    var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                    return stateStoreFactory(new ProjectionStoreContext { Services = sp, RebuildVersion = version });
                });
        });

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type.
    /// The factory receives an <see cref="IServiceProvider"/> and returns an event handler function.
    /// Dependencies are resolved once at startup, not per event.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="handlerFactory">A factory that resolves dependencies and returns the event handler.</param>
    /// <param name="processorId">
    /// A unique processor ID within this module. Used as the checkpoint key — processors
    /// sharing an ID would share checkpoint state and silently skip events.
    /// </param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    /// <param name="configure">
    /// Optional per-processor execution settings for the async control loop.
    /// Ignored for sync reactors.
    /// </param>
    public static DcbModuleBuilder ReactTo<TEvent>(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, Func<TEvent, CancellationToken, Task>> handlerFactory,
        string processorId,
        ReactorMode mode = ReactorMode.Async,
        Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        // Fail fast: a ':' or '#' in the processor id makes the composed DI key ambiguous.
        // A reserved word shadows an Alberto internal service registration.
        ValidateProcessorIdArg(processorId, builder.ModuleKey);

        var executionOptions = BuildProcessorExecutionOptions(mode, configure);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = processorId,
            Kind = ProcessorKind.Reactor,
            Execution = executionOptions,
        });

        if (mode == ReactorMode.Sync)
        {
            ValidateSyncExecutionOptions(processorId, executionOptions);
            builder.Register(context =>
            {
                var mKey = context.ModuleKey;
                context.Services.AddKeyedSingleton<IPostAppendHandler>(mKey, (sp, _) =>
                    new SyncReactor<TEvent>(
                        (e, _, ct) => handlerFactory(sp)(e, ct),
                        sp.GetKeyedService<EventSerializer>(mKey)));
            });
        }
        else
        {
            RegisterAsyncProcessor(
                builder,
                processorId,
                executionOptions,
                (sp, mKey) => new FunctionalReactor<TEvent>(
                    processorId,
                    (e, _, ct) => handlerFactory(sp)(e, ct),
                    executionOptions.MaxConcurrency,
                    sp.GetKeyedService<EventSerializer>(mKey)));
        }

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type and
    /// receives a <see cref="ReactorContext"/> with event metadata.
    /// Dependencies are resolved once at startup, not per event.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="handlerFactory">A factory that resolves dependencies and returns the event handler.</param>
    /// <param name="processorId">
    /// A unique processor ID within this module. Used as the checkpoint key — processors
    /// sharing an ID would share checkpoint state and silently skip events.
    /// </param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    /// <param name="configure">
    /// Optional per-processor execution settings for the async control loop.
    /// Ignored for sync reactors.
    /// </param>
    public static DcbModuleBuilder ReactTo<TEvent>(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, Func<TEvent, ReactorContext, CancellationToken, Task>> handlerFactory,
        string processorId,
        ReactorMode mode = ReactorMode.Async,
        Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        // Fail fast: a ':' or '#' in the processor id makes the composed DI key ambiguous.
        // A reserved word shadows an Alberto internal service registration.
        ValidateProcessorIdArg(processorId, builder.ModuleKey);

        var executionOptions = BuildProcessorExecutionOptions(mode, configure);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = processorId,
            Kind = ProcessorKind.Reactor,
            Execution = executionOptions,
        });

        if (mode == ReactorMode.Sync)
        {
            ValidateSyncExecutionOptions(processorId, executionOptions);
            builder.Register(context =>
            {
                var mKey = context.ModuleKey;
                context.Services.AddKeyedSingleton<IPostAppendHandler>(mKey, (sp, _) =>
                    new SyncReactor<TEvent>(
                        handlerFactory(sp),
                        sp.GetKeyedService<EventSerializer>(mKey)));
            });
        }
        else
        {
            RegisterAsyncProcessor(
                builder,
                processorId,
                executionOptions,
                (sp, mKey) => new FunctionalReactor<TEvent>(
                    processorId,
                    handlerFactory(sp),
                    executionOptions.MaxConcurrency,
                    sp.GetKeyedService<EventSerializer>(mKey)));
        }

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// using a handler class resolved from DI per event. The method selector picks which
    /// method on the handler to invoke for each event.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <typeparam name="THandler">The handler class holding dependencies. Registered as scoped if not already registered.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="methodSelector">Selects the handler method from the resolved handler instance.</param>
    /// <param name="processorId">
    /// Optional. When omitted, the id is derived from <typeparamref name="THandler"/> via
    /// <see cref="ProcessorId.For{T}"/> (reading <see cref="ProcessorIdAttribute"/> if present,
    /// otherwise building a qualified name from the type hierarchy). Must be unique within the
    /// module — processors sharing an id share checkpoint state and silently skip events.
    /// </param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    /// <param name="configure">Optional per-processor execution settings for the async control loop.</param>
    public static DcbModuleBuilder ReactTo<TEvent, THandler>(
        this DcbModuleBuilder builder,
        Func<THandler, Func<TEvent, CancellationToken, Task>> methodSelector,
        string? processorId = null,
        ReactorMode mode = ReactorMode.Async,
        Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null)
        where TEvent : class, IEvent
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(methodSelector);

        var resolvedProcessorId = processorId ?? ProcessorId.For<THandler>();
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedProcessorId);

        // Fail fast: a ':' or '#' in the processor id makes the composed DI key ambiguous.
        // A reserved word shadows an Alberto internal service registration.
        // When the id was derived from the handler type, the error message names the type and
        // tells the user to add [ProcessorId("valid_id")] to it.
        ValidateProcessorIdArg(resolvedProcessorId, builder.ModuleKey,
            processorId is null ? typeof(THandler) : null);

        builder.Register(context => context.Services.TryAddScoped<THandler>());

        var executionOptions = BuildProcessorExecutionOptions(mode, configure);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = resolvedProcessorId,
            Kind = ProcessorKind.Reactor,
            Execution = executionOptions,
            HandlerType = typeof(THandler),
        });

        if (mode == ReactorMode.Sync)
        {
            ValidateSyncExecutionOptions(resolvedProcessorId, executionOptions);
            builder.Register(context =>
            {
                var mKey = context.ModuleKey;
                context.Services.AddKeyedSingleton<IPostAppendHandler>(mKey, (sp, _) =>
                {
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    return new SyncReactor<TEvent>(async (e, reactorContext, ct) =>
                    {
                        await using var scope = EventProcessingScope.Create(
                            scopeFactory,
                            reactorContext.TenantId);
                        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                        await methodSelector(handler)(e, ct);
                    }, sp.GetKeyedService<EventSerializer>(mKey));
                });
            });
        }
        else
        {
            RegisterAsyncProcessor(builder, resolvedProcessorId, executionOptions, (sp, mKey) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(resolvedProcessorId, async (e, reactorContext, ct) =>
                {
                    await using var scope = EventProcessingScope.Create(
                        scopeFactory,
                        reactorContext.TenantId);
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, ct);
                }, executionOptions.MaxConcurrency, sp.GetKeyedService<EventSerializer>(mKey));
            });
        }

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// using a handler class resolved from DI per event and exposing a <see cref="ReactorContext"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <typeparam name="THandler">The handler class holding dependencies. Registered as scoped if not already registered.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="methodSelector">Selects the handler method from the resolved handler instance.</param>
    /// <param name="processorId">
    /// Optional. When omitted, the id is derived from <typeparamref name="THandler"/> via
    /// <see cref="ProcessorId.For{T}"/> (reading <see cref="ProcessorIdAttribute"/> if present,
    /// otherwise building a qualified name from the type hierarchy). Must be unique within the
    /// module — processors sharing an id share checkpoint state and silently skip events.
    /// </param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    /// <param name="configure">Optional per-processor execution settings for the async control loop.</param>
    public static DcbModuleBuilder ReactTo<TEvent, THandler>(
        this DcbModuleBuilder builder,
        Func<THandler, Func<TEvent, ReactorContext, CancellationToken, Task>> methodSelector,
        string? processorId = null,
        ReactorMode mode = ReactorMode.Async,
        Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure = null)
        where TEvent : class, IEvent
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(methodSelector);

        var resolvedProcessorId = processorId ?? ProcessorId.For<THandler>();
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedProcessorId);

        // Fail fast: a ':' or '#' in the processor id makes the composed DI key ambiguous.
        // A reserved word shadows an Alberto internal service registration.
        // When the id was derived from the handler type, the error message names the type and
        // tells the user to add [ProcessorId("valid_id")] to it.
        ValidateProcessorIdArg(resolvedProcessorId, builder.ModuleKey,
            processorId is null ? typeof(THandler) : null);

        builder.Register(context => context.Services.TryAddScoped<THandler>());

        var executionOptions = BuildProcessorExecutionOptions(mode, configure);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = resolvedProcessorId,
            Kind = ProcessorKind.Reactor,
            Execution = executionOptions,
            HandlerType = typeof(THandler),
        });

        if (mode == ReactorMode.Sync)
        {
            ValidateSyncExecutionOptions(resolvedProcessorId, executionOptions);
            builder.Register(context =>
            {
                var mKey = context.ModuleKey;
                context.Services.AddKeyedSingleton<IPostAppendHandler>(mKey, (sp, _) =>
                {
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    return new SyncReactor<TEvent>(async (e, reactorContext, ct) =>
                    {
                        await using var scope = EventProcessingScope.Create(
                            scopeFactory,
                            reactorContext.TenantId);
                        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                        await methodSelector(handler)(e, reactorContext, ct);
                    }, sp.GetKeyedService<EventSerializer>(mKey));
                });
            });
        }
        else
        {
            RegisterAsyncProcessor(builder, resolvedProcessorId, executionOptions, (sp, mKey) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(resolvedProcessorId, async (e, reactorContext, ct) =>
                {
                    await using var scope = EventProcessingScope.Create(
                        scopeFactory,
                        reactorContext.TenantId);
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, reactorContext, ct);
                }, executionOptions.MaxConcurrency, sp.GetKeyedService<EventSerializer>(mKey));
            });
        }

        return builder;
    }

    /// <summary>
    /// Configures the async control loop. Called implicitly with defaults when omitted.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">
    /// Transforms the current options. Use a <c>with</c> expression:
    /// <c>o => o with { BatchSize = 500 }</c>. Anything set here is still overridable from
    /// <c>Alberto:Modules:{moduleKey}:ControlLoop</c>.
    /// </param>
    public static DcbModuleBuilder WithControlLoop(
        this DcbModuleBuilder builder,
        Func<ControlLoopOptions, ControlLoopOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Configure(d => d with
            {
                ControlLoop = configure(d.ControlLoop)
                    ?? throw new InvalidOperationException("WithControlLoop configurator returned null."),
            });
        }

        if (!builder.ControlLoopConfigured)
        {
            builder.ControlLoopConfigured = true;
            builder.Register(ControlLoopRegistration.Register);
        }

        return builder;
    }

    /// <summary>
    /// Enables zero-downtime projection rebuilds for this module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rebuild replays the whole log into a second, invisible copy of a projection's state
    /// while the live one keeps serving reads, then swaps the two in a single transaction.
    /// Nothing happens until an operator starts one — <c>alberto ops rebuild start
    /// &lt;processor&gt;</c> — and this method is what makes the application able to carry one out.
    /// </para>
    /// <para>
    /// Requires a backend that registers an <see cref="IProjectionRebuildStore"/>; Postgres does.
    /// Projections must resolve their rebuild version through
    /// <see cref="ProjectionStoreContext.RebuildVersion"/>. A projection that ignores it still
    /// works — it just cannot be rebuilt without downtime.
    /// </para>
    /// </remarks>
    /// <param name="builder">The module builder.</param>
    /// <param name="autoPromote">
    /// Promote a rebuild automatically as soon as it catches up. Default: <see langword="true"/>.
    /// Set <see langword="false"/> to park finished rebuilds at <c>Ready</c> until an operator
    /// runs <c>alberto ops rebuild promote</c>.
    /// </param>
    /// <param name="pollingInterval">
    /// How often the coordinator re-reads the rebuild state machine. Default: 5 seconds.
    /// </param>
    public static DcbModuleBuilder WithRebuilds(
        this DcbModuleBuilder builder,
        bool autoPromote = true,
        TimeSpan? pollingInterval = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Update the options record so the validator and coordinator both see the intent.
        builder.WithControlLoop(o =>
        {
            var rebuilds = o.Rebuilds with { Enabled = true, AutoPromote = autoPromote };

            if (pollingInterval.HasValue)
                rebuilds = rebuilds with
                {
                    PollingInterval = pollingInterval.Value,
                    VersionRefreshInterval = pollingInterval.Value,
                };

            return o with { Rebuilds = rebuilds };
        });

        // Register the rebuild pipeline services (deferred — factories read IOptionsMonitor).
        builder.Register(context =>
        {
            var services = context.Services;
            var moduleKey = context.ModuleKey;

            static ControlLoopOptions Options(IServiceProvider sp, string key) =>
                sp.GetRequiredService<IOptionsMonitor<AlbertoModuleDefinition>>().Get(key).ControlLoop;

            static IErrorClassifier Classifier(IServiceProvider sp, string key) =>
                sp.GetKeyedService<IErrorClassifier>(key) ?? DefaultErrorClassifier.Instance;

            static IEventStoreBackend Backend(IServiceProvider sp, string key) =>
                sp.GetKeyedService<IEventStoreBackend>($"{key}:consumer")
                ?? sp.GetKeyedService<IEventStoreBackend>(key)
                ?? throw new InvalidOperationException(
                    $"No event store backend registered for Alberto module '{key}'.");

            // ProjectionVersions: tracks which rebuild version is live for each processor.
            // GetKeyedService<ProjectionVersions> returns null when not registered, so
            // ProjectionVersions.LiveVersion falls back to NeverRebuilt for modules that
            // never call WithRebuilds() — safe to omit registration entirely when disabled.
            services.AddKeyedSingleton<ProjectionVersions>(moduleKey, (sp, _) =>
            {
                var opts = Options(sp, moduleKey).Rebuilds;
                var store = sp.GetKeyedService<IProjectionRebuildStore>(moduleKey)
                    ?? throw new InvalidOperationException(
                        $"Alberto module '{moduleKey}' enables projection rebuilds but its backend " +
                        "does not register an IProjectionRebuildStore. Call .WithPostgres() on the module.");
                return new ProjectionVersions(store, opts.VersionRefreshInterval);
            });

            // ShadowControlLoopFactory: builds a control loop for a shadow rebuild processor.
            // ControlLoopAssembler is constructed once (module-level middleware list is stable)
            // and captured by the factory delegate so each shadow loop gets the same middleware
            // chain as the live loops — the assembler is the single composition seam shared by
            // both call sites, preventing live/shadow middleware drift.
            services.AddKeyedSingleton<ShadowControlLoopFactory>(moduleKey, (sp, _) =>
            {
                var opts = Options(sp, moduleKey);

                var assembler = new ControlLoopAssembler(
                    sp.GetKeyedServices<ConsumeMiddleware>(moduleKey).ToList(),
                    sp.GetKeyedServices<BatchConsumeMiddleware>(moduleKey).ToList(),
                    opts.Retry,
                    Classifier(sp, moduleKey),
                    sp.GetKeyedService<IDeadLetterStore>(moduleKey),
                    sp.GetService<TimeProvider>());

                return new ShadowControlLoopFactory(processor =>
                {
                    var head = sp.GetRequiredKeyedService<EventStoreHead>(moduleKey);
                    var backend = Backend(sp, moduleKey);
                    var checkpoints = sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey);

                    return assembler.Create(
                        processor, head, backend, checkpoints,
                        opts.PollingInterval, opts.BatchSize, moduleKey,
                        ProcessorExecutionOptions.Default,
                        sp.GetService<ILogger<ControlLoop>>());
                });
            });

            // RebuildCoordinator: drives rebuild state machine transitions.
            services.AddSingleton<IHostedService>(sp =>
            {
                var opts = Options(sp, moduleKey).Rebuilds;
                var projectionVersions = sp.GetRequiredKeyedService<ProjectionVersions>(moduleKey);

                // Twice the version-cache refresh interval. One interval is the guarantee — a
                // reader that resolved the superseded version has refreshed by then — and the
                // second is margin for a refresh that had to retry, and for clock skew between
                // the database that stamps the transition and the host that times the grace.
                var coordinatorOptions = new RebuildCoordinatorOptions(
                    opts.PollingInterval,
                    opts.AutoPromote,
                    projectionVersions.RefreshInterval * 2);

                return new RebuildCoordinator(
                    sp.GetKeyedServices<RebuildableProjection>(moduleKey).ToList(),
                    sp.GetRequiredKeyedService<IProjectionRebuildCoordinatorStore>(moduleKey),
                    projectionVersions,
                    sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey),
                    sp.GetRequiredKeyedService<ShadowControlLoopFactory>(moduleKey),
                    sp.GetKeyedServices<IProjectionStateClearer>(moduleKey).ToList(),
                    coordinatorOptions,
                    sp.GetService<TimeProvider>(),
                    sp.GetService<ILogger<RebuildCoordinator>>());
            });
        });

        return builder;
    }

    /// <summary>
    /// Adds a middleware to the per-event consume pipeline. Middlewares run in registration order
    /// (first added is outermost). The built-in retry-and-dead-letter middleware is always the
    /// innermost layer, so custom middleware observes the outcome of the whole retry sequence.
    /// </summary>
    public static DcbModuleBuilder AddConsumeMiddleware(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, ConsumeMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton(context.ModuleKey, (sp, _) => factory(sp)));
    }

    /// <summary>
    /// Adds a middleware to the batch consume pipeline. A per-event middleware without a batch
    /// counterpart forces the control loop back onto per-event dispatch, so register both when a
    /// processor should keep batching.
    /// </summary>
    public static DcbModuleBuilder AddBatchConsumeMiddleware(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, BatchConsumeMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton(context.ModuleKey, (sp, _) => factory(sp)));
    }

    /// <summary>
    /// Replaces the classifier that decides whether a handler failure is transient (retry) or
    /// permanent (dead-letter immediately). Defaults to <see cref="DefaultErrorClassifier"/>.
    /// </summary>
    public static DcbModuleBuilder UseErrorClassifier(
        this DcbModuleBuilder builder,
        IErrorClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(classifier);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton(context.ModuleKey, classifier));
    }

    /// <summary>
    /// Replaces the error classifier with one resolved from the container, so it can take
    /// dependencies. Defaults to <see cref="DefaultErrorClassifier"/>.
    /// </summary>
    public static DcbModuleBuilder UseErrorClassifier<TClassifier>(this DcbModuleBuilder builder)
        where TClassifier : class, IErrorClassifier
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Register(context =>
            context.Services.AddKeyedSingleton<IErrorClassifier, TClassifier>(context.ModuleKey));
    }

    private static readonly ProcessorExecutionOptions SyncExecutionDefault =
        new() { BatchingMode = ProcessorBatchingMode.Disabled };

    private static ProcessorExecutionOptions BuildProcessorExecutionOptions(
        ReactorMode mode,
        Func<ProcessorExecutionOptions, ProcessorExecutionOptions>? configure)
    {
        var options = mode == ReactorMode.Sync
            ? SyncExecutionDefault
            : ProcessorExecutionOptions.Default;

        if (configure is null)
            return options;

        return configure(options)
            ?? throw new InvalidOperationException("Processor execution configurator returned null.");
    }

    private static void RegisterAsyncProcessor(
        DcbModuleBuilder builder,
        string processorId,
        ProcessorExecutionOptions executionOptions,
        Func<IServiceProvider, string, IEventProcessor> processorFactory)
    {
        builder.Register(context =>
        {
            var mKey = context.ModuleKey;
            context.Services.AddKeyedSingleton<IEventProcessor>(
                mKey,
                (sp, _) => processorFactory(sp, mKey));
            context.Services.AddKeyedSingleton<ProcessorExecutionRegistration>(
                mKey,
                (_, _) => new ProcessorExecutionRegistration(processorId, executionOptions));
        });
    }

    private static void ValidateSyncExecutionOptions(
        string processorId,
        ProcessorExecutionOptions executionOptions)
    {
        if (executionOptions.BatchingMode == ProcessorBatchingMode.Disabled)
            return;

        throw new InvalidOperationException(
            $"Processor '{processorId}' is registered as {ReactorMode.Sync} and cannot enable async batching.");
    }

    /// <summary>
    /// Registers an upcaster declaration that migrates stored events at an older schema version
    /// to the current type before they reach any handler, projector, or reactor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upcasters are declared the same way projections are: via an explicit
    /// <see cref="UpcasterDeclaration"/> built with <see cref="DeclareUpcaster"/>. Assembly
    /// scanning is deliberately not used — the same reason projections are not discovered by
    /// scanning.
    /// </para>
    /// <para>
    /// <b>Order-independence</b>: all <c>Configure</c> callbacks (including this one and the one
    /// inside <c>WithEventsFrom</c> that records the assembly's event types) run before any
    /// <c>Register</c> callback (where <c>WithEventsFrom</c> builds the serializer). Therefore
    /// calling <c>.AddUpcaster(...)</c> before or after <c>.WithEventsFrom(...)</c> always
    /// produces the same serializer.
    /// </para>
    /// </remarks>
    /// <param name="builder">The module builder.</param>
    /// <param name="declaration">
    /// The upcast chain for one event type, produced by
    /// <c>DeclareUpcaster.For&lt;TEvent&gt;(...).From&lt;TOld&gt;(...).Build()</c>.
    /// </param>
    /// <returns>The builder for continued chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", builder => builder
    ///     .WithPostgres(options => options.ConnectionString = "...")
    ///     .WithEventsFrom(typeof(OrderPlaced).Assembly)
    ///     .AddUpcaster(DeclareUpcaster
    ///         .For&lt;OrderPlaced&gt;("order-placed")
    ///         .From&lt;OrderPlacedV1&gt;(1, v1 => new OrderPlaced(v1.OrderId, v1.Amount, "default"))
    ///         .Build())
    /// );
    /// </code>
    /// </example>
    public static DcbModuleBuilder AddUpcaster(
        this DcbModuleBuilder builder,
        UpcasterDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declaration);

        return builder.Configure(d => d with
        {
            UpcasterDeclarations = d.UpcasterDeclarations.Add(declaration),
        });
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="processorId"/> is not safe to
    /// use in a composed DI service key <c>{moduleKey}:{processorId}</c>.
    /// </summary>
    /// <param name="processorId">The id to validate.</param>
    /// <param name="moduleKey">The module the processor belongs to, for the error message.</param>
    /// <param name="handlerType">
    /// When the id was derived from a handler type rather than supplied explicitly, the type is
    /// included in the error message and the remedy text tells the user to add
    /// <c>[ProcessorId("valid_id")]</c> to it. Pass <see langword="null"/> for an explicitly
    /// supplied id.
    /// </param>
    /// <remarks>
    /// Processor ids do not have to be lowercase: they are often PascalCase type-derived names
    /// (e.g., <c>OrderSummaryProjection</c>) or dotted attribute values (e.g.,
    /// <c>orders.summary</c>). The restriction is narrower: a <c>:</c> or <c>#</c> makes the
    /// composed key structurally ambiguous, and the reserved words "consumer", "catalog", and
    /// "tenant-raw" collide with Alberto's own internal DI key suffixes.
    /// </remarks>
    internal static void ValidateProcessorIdArg(
        string processorId,
        string moduleKey,
        Type? handlerType = null)
    {
        if (IdentifierRules.IsValidProcessorId(processorId))
            return;

        // Build a source description: either the handler type name or nothing (explicit arg).
        var source = handlerType is not null
            ? $" (derived from handler type '{handlerType.Name}')"
            : string.Empty;

        // Append a targeted remedy for type-derived ids so the user knows exactly how to fix it.
        var remedy = handlerType is not null
            ? $" Add [ProcessorId(\"your_lowercase_id\")] to {handlerType.Name} to pin it to a safe name."
            : string.Empty;

        // Distinguish the two failure modes with a more specific error sentence so the user does
        // not have to read the rules to understand why their id was rejected.
        string reason;
        if (processorId.Contains(':', StringComparison.Ordinal)
            || processorId.Contains('#', StringComparison.Ordinal))
        {
            reason = "It contains ':' or '#', which Alberto uses as separators inside composed DI " +
                     "service keys. A processor id containing either character makes the composed key " +
                     $"'{moduleKey}:{{processorId}}' structurally ambiguous.";
        }
        else if (IdentifierRules.ReservedProcessorIds.Contains(processorId))
        {
            reason = $"It is a reserved word. Alberto registers the internal service " +
                     $"'{moduleKey}:{processorId}' using this suffix; a processor with the same id " +
                     "would shadow that registration and cause the wrong service to be resolved at runtime.";
        }
        else
        {
            reason = IdentifierRules.ProcessorIdRule;
        }

        throw new ArgumentException(
            $"Processor id '{processorId}'{source} in module '{moduleKey}' is not valid. {reason}{remedy}",
            nameof(processorId));
    }
}
