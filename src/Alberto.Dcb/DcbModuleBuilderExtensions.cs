using Alberto.Dcb.Configuration;
using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// <param name="stateStoreFactory">A delegate that, given the service provider, returns a state store factory.</param>
    public static DcbModuleBuilder AddProjection<TState>(
        this DcbModuleBuilder builder,
        ProjectionDeclaration<TState> declaration,
        Func<IServiceProvider, Func<IStateStore<TState>>> stateStoreFactory)
        where TState : new()
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(stateStoreFactory);

        builder.DeclareProcessor(new ProcessorDeclaration
        {
            ProcessorId = declaration.ProcessorId,
            Kind = ProcessorKind.Projection,
        });

        builder.Register(context => context.Services.AddKeyedSingleton<IEventProcessor>(context.ModuleKey, (sp, _) =>
        {
            var factory = stateStoreFactory(sp);
            return new DeclaredAsyncProjection<TState>(declaration, factory);
        }));
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
                return new SyncReactor<TEvent>(async (e, _, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
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
                return new FunctionalReactor<TEvent>(resolvedProcessorId, async (e, _, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
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
                    await using var scope = scopeFactory.CreateAsyncScope();
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
                    await using var scope = scopeFactory.CreateAsyncScope();
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
