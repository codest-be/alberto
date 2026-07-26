using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
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
    /// Builds the projection's state store. The <see cref="ProjectionStoreContext"/> carries the
    /// service provider and the rebuild-version selector the store must resolve on every
    /// operation:
    /// <code>
    /// .AddProjection(OrdersOverviewProjection.Declaration, ctx =>
    /// {
    ///     var dataSource = ctx.Services.GetRequiredKeyedService&lt;NpgsqlDataSource&gt;(ModuleKey);
    ///     return () => new PostgresStateStore&lt;OrdersOverview&gt;(
    ///         dataSource, nameof(OrdersOverviewProjection), "orders", ctx.RebuildVersion);
    /// })
    /// </code>
    /// A store that ignores <see cref="ProjectionStoreContext.RebuildVersion"/> still works — it
    /// simply cannot be rebuilt without downtime.
    /// </param>
    /// <param name="projectionType">
    /// The key this projection's state rows are stored under, when it differs from the
    /// processor id. Only the rebuild pipeline needs this: promotion deletes the superseded
    /// version's rows from the state table, which is keyed by projection type.
    /// </param>
    /// <remarks>
    /// <para>
    /// In addition to the two <see cref="IEventProcessor"/> registrations (live and
    /// rebuild), this method registers a <c>Func&lt;IStateStore&lt;TState&gt;&gt;</c>
    /// keyed by <c>"{moduleKey}:{declaration.ProcessorId}"</c>. Read-side code — GraphQL
    /// resolvers, REST controllers, background jobs — should resolve this factory from DI
    /// rather than constructing a <see cref="Alberto.Dcb.Subscriptions.IStateStore{TState}"/>
    /// directly. Using the registered factory guarantees that the reader inherits the same
    /// tenancy mode and schema as the writer, so writer/reader disagreement (the source of
    /// silent empty results) becomes a loud resolution failure on the first read instead of a
    /// query that quietly returns nothing forever. Note this is first-read, not startup:
    /// a resolver that asks for an unregistered key throws when it first runs, not when the
    /// host is built.
    /// </para>
    /// <code>
    /// // resolver (e.g. OrderQueries.GetOrdersOverview):
    /// var factory = sp.GetRequiredKeyedService&lt;Func&lt;IStateStore&lt;OrdersOverview&gt;&gt;&gt;(
    ///     $"{OrdersModule.ModuleKey}:{nameof(OrdersOverviewProjection)}");
    /// var store = factory();
    /// </code>
    /// </remarks>
    public static DcbModuleBuilder AddProjection<TState>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TState> declaration,
        Func<ProjectionStoreContext, Func<IStateStore<TState>>> stateStoreFactory,
        string? projectionType = null)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);

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
                var factory = stateStoreFactory(new ProjectionStoreContext(sp, version));
                return new DeclaredAsyncProjection<TState>(declaration, factory);
            });

            context.Services.AddKeyedSingleton(moduleKey, (sp, _) =>
                new RebuildableProjection(
                    declaration.ProcessorId,
                    projectionType ?? declaration.ProcessorId,
                    version => new DeclaredAsyncProjection<TState>(
                        declaration, stateStoreFactory(new ProjectionStoreContext(sp, version)))));

            // Reader store factory — keyed by "{moduleKey}:{processorId}".
            // Read-side code resolves this instead of constructing a store directly, so
            // the tenancy mode and schema are always inherited from the writer's factory.
            context.Services.AddKeyedSingleton<Func<IStateStore<TState>>>(
                $"{moduleKey}:{declaration.ProcessorId}",
                (sp, _) =>
                {
                    var version = ProjectionVersions.LiveVersion(sp, moduleKey, declaration.ProcessorId);
                    return stateStoreFactory(new ProjectionStoreContext(sp, version));
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
            builder.Register(context => context.Services.AddKeyedSingleton<IPostAppendHandler>(context.ModuleKey, (sp, _) =>
                new SyncReactor<TEvent>((e, _, ct) => handlerFactory(sp)(e, ct))));
        }
        else
        {
            RegisterAsyncProcessor(
                builder,
                processorId,
                executionOptions,
                sp => new FunctionalReactor<TEvent>(
                    processorId,
                    (e, _, ct) => handlerFactory(sp)(e, ct),
                    executionOptions.MaxConcurrency));
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
            builder.Register(context => context.Services.AddKeyedSingleton<IPostAppendHandler>(context.ModuleKey, (sp, _) =>
                new SyncReactor<TEvent>(handlerFactory(sp))));
        }
        else
        {
            RegisterAsyncProcessor(
                builder,
                processorId,
                executionOptions,
                sp => new FunctionalReactor<TEvent>(
                    processorId,
                    handlerFactory(sp),
                    executionOptions.MaxConcurrency));
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
            builder.Register(context => context.Services.AddKeyedSingleton<IPostAppendHandler>(context.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new SyncReactor<TEvent>(async (e, reactorContext, ct) =>
                {
                    await using var scope = EventProcessingScope.Create(
                        scopeFactory,
                        reactorContext.TenantId);
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, ct);
                });
            }));
        }
        else
        {
            RegisterAsyncProcessor(builder, resolvedProcessorId, executionOptions, sp =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(resolvedProcessorId, async (e, reactorContext, ct) =>
                {
                    await using var scope = EventProcessingScope.Create(
                        scopeFactory,
                        reactorContext.TenantId);
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, ct);
                }, executionOptions.MaxConcurrency);
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
            builder.Register(context => context.Services.AddKeyedSingleton<IPostAppendHandler>(context.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new SyncReactor<TEvent>(async (e, reactorContext, ct) =>
                {
                    await using var scope = EventProcessingScope.Create(
                        scopeFactory,
                        reactorContext.TenantId);
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, reactorContext, ct);
                });
            }));
        }
        else
        {
            RegisterAsyncProcessor(builder, resolvedProcessorId, executionOptions, sp =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(resolvedProcessorId, async (e, reactorContext, ct) =>
                {
                    await using var scope = EventProcessingScope.Create(
                        scopeFactory,
                        reactorContext.TenantId);
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, reactorContext, ct);
                }, executionOptions.MaxConcurrency);
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
                    sp.GetKeyedService<IDeadLetterStore>(moduleKey));

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
                var coordinatorOptions = new RebuildCoordinatorOptions(opts.PollingInterval, opts.AutoPromote);

                return new RebuildCoordinator(
                    sp.GetKeyedServices<RebuildableProjection>(moduleKey).ToList(),
                    sp.GetRequiredKeyedService<IProjectionRebuildCoordinatorStore>(moduleKey),
                    sp.GetRequiredKeyedService<ProjectionVersions>(moduleKey),
                    sp.GetRequiredKeyedService<ICheckpointStore>(moduleKey),
                    sp.GetRequiredKeyedService<ShadowControlLoopFactory>(moduleKey),
                    sp.GetKeyedServices<IProjectionStateClearer>(moduleKey).ToList(),
                    coordinatorOptions,
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
        new(ProcessorBatchingMode.Disabled);

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
        Func<IServiceProvider, IEventProcessor> processorFactory)
    {
        builder.Register(context =>
        {
            context.Services.AddKeyedSingleton<IEventProcessor>(
                context.ModuleKey,
                (sp, _) => processorFactory(sp));
            context.Services.AddKeyedSingleton<ProcessorExecutionRegistration>(
                context.ModuleKey,
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
}
