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
        builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
        {
            var factory = stateStoreFactory(sp);
            return new DeclaredAsyncProjection<TState>(declaration, factory);
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
    public static DcbModuleBuilder ReactTo<TEvent>(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, Func<TEvent, CancellationToken, Task>> handlerFactory,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        if (mode == ReactorMode.Sync)
        {
            builder.Services.AddKeyedSingleton<IPostAppendHandler>(builder.ModuleKey, (sp, _) =>
                new SyncReactor<TEvent>((e, _, ct) => handlerFactory(sp)(e, ct)));
        }
        else
        {
            builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
                new FunctionalReactor<TEvent>(processorId, (e, _, ct) => handlerFactory(sp)(e, ct)));
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
    public static DcbModuleBuilder ReactTo<TEvent>(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, Func<TEvent, ReactorContext, CancellationToken, Task>> handlerFactory,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        if (mode == ReactorMode.Sync)
        {
            builder.Services.AddKeyedSingleton<IPostAppendHandler>(builder.ModuleKey, (sp, _) =>
                new SyncReactor<TEvent>(handlerFactory(sp)));
        }
        else
        {
            builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
                new FunctionalReactor<TEvent>(processorId, handlerFactory(sp)));
        }

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// resolving a single dependency from DI at startup.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <typeparam name="TDep">The dependency type to resolve from DI.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="handler">The static handler function receiving the resolved dependency, event, and cancellation token.</param>
    /// <param name="processorId">A unique processor ID within this module.</param>
    /// <param name="mode">Execution mode.</param>
    public static DcbModuleBuilder ReactTo<TEvent, TDep>(
        this DcbModuleBuilder builder,
        Func<TDep, TEvent, CancellationToken, Task> handler,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where TDep : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        return builder.ReactTo<TEvent>(sp =>
        {
            var dep = sp.GetRequiredService<TDep>();
            return (e, ct) => handler(dep, e, ct);
        }, processorId, mode);
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// resolving a single dependency from DI at startup and exposing a <see cref="ReactorContext"/>.
    /// </summary>
    public static DcbModuleBuilder ReactTo<TEvent, TDep>(
        this DcbModuleBuilder builder,
        Func<TDep, TEvent, ReactorContext, CancellationToken, Task> handler,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where TDep : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        return builder.ReactTo<TEvent>(sp =>
        {
            var dep = sp.GetRequiredService<TDep>();
            return (e, context, ct) => handler(dep, e, context, ct);
        }, processorId, mode);
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// resolving two dependencies from DI at startup.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <typeparam name="TDep1">The first dependency type to resolve from DI.</typeparam>
    /// <typeparam name="TDep2">The second dependency type to resolve from DI.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="handler">The static handler function receiving the resolved dependencies, event, and cancellation token.</param>
    /// <param name="processorId">A unique processor ID within this module.</param>
    /// <param name="mode">Execution mode.</param>
    public static DcbModuleBuilder ReactTo<TEvent, TDep1, TDep2>(
        this DcbModuleBuilder builder,
        Func<TDep1, TDep2, TEvent, CancellationToken, Task> handler,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where TDep1 : notnull
        where TDep2 : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        return builder.ReactTo<TEvent>(sp =>
        {
            var dep1 = sp.GetRequiredService<TDep1>();
            var dep2 = sp.GetRequiredService<TDep2>();
            return (e, ct) => handler(dep1, dep2, e, ct);
        }, processorId, mode);
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// resolving two dependencies from DI at startup and exposing a <see cref="ReactorContext"/>.
    /// </summary>
    public static DcbModuleBuilder ReactTo<TEvent, TDep1, TDep2>(
        this DcbModuleBuilder builder,
        Func<TDep1, TDep2, TEvent, ReactorContext, CancellationToken, Task> handler,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where TDep1 : notnull
        where TDep2 : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        return builder.ReactTo<TEvent>(sp =>
        {
            var dep1 = sp.GetRequiredService<TDep1>();
            var dep2 = sp.GetRequiredService<TDep2>();
            return (e, context, ct) => handler(dep1, dep2, e, context, ct);
        }, processorId, mode);
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// resolving three dependencies from DI at startup.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <typeparam name="TDep1">The first dependency type to resolve from DI.</typeparam>
    /// <typeparam name="TDep2">The second dependency type to resolve from DI.</typeparam>
    /// <typeparam name="TDep3">The third dependency type to resolve from DI.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="handler">The static handler function receiving the resolved dependencies, event, and cancellation token.</param>
    /// <param name="processorId">A unique processor ID within this module.</param>
    /// <param name="mode">Execution mode.</param>
    public static DcbModuleBuilder ReactTo<TEvent, TDep1, TDep2, TDep3>(
        this DcbModuleBuilder builder,
        Func<TDep1, TDep2, TDep3, TEvent, CancellationToken, Task> handler,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where TDep1 : notnull
        where TDep2 : notnull
        where TDep3 : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        return builder.ReactTo<TEvent>(sp =>
        {
            var dep1 = sp.GetRequiredService<TDep1>();
            var dep2 = sp.GetRequiredService<TDep2>();
            var dep3 = sp.GetRequiredService<TDep3>();
            return (e, ct) => handler(dep1, dep2, dep3, e, ct);
        }, processorId, mode);
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// resolving three dependencies from DI at startup and exposing a <see cref="ReactorContext"/>.
    /// </summary>
    public static DcbModuleBuilder ReactTo<TEvent, TDep1, TDep2, TDep3>(
        this DcbModuleBuilder builder,
        Func<TDep1, TDep2, TDep3, TEvent, ReactorContext, CancellationToken, Task> handler,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where TDep1 : notnull
        where TDep2 : notnull
        where TDep3 : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        return builder.ReactTo<TEvent>(sp =>
        {
            var dep1 = sp.GetRequiredService<TDep1>();
            var dep2 = sp.GetRequiredService<TDep2>();
            var dep3 = sp.GetRequiredService<TDep3>();
            return (e, context, ct) => handler(dep1, dep2, dep3, e, context, ct);
        }, processorId, mode);
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
    /// A unique processor ID within this module. Used as the checkpoint key — processors
    /// sharing an ID would share checkpoint state and silently skip events.
    /// </param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    public static DcbModuleBuilder ReactTo<TEvent, THandler>(
        this DcbModuleBuilder builder,
        Func<THandler, Func<TEvent, CancellationToken, Task>> methodSelector,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(methodSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        builder.Services.TryAddScoped<THandler>();

        if (mode == ReactorMode.Sync)
        {
            builder.Services.AddKeyedSingleton<IPostAppendHandler>(builder.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new SyncReactor<TEvent>(async (e, _, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, ct);
                });
            });
        }
        else
        {
            builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(processorId, async (e, _, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, ct);
                });
            });
        }

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// using a handler class resolved from DI per event and exposing a <see cref="ReactorContext"/>.
    /// </summary>
    public static DcbModuleBuilder ReactTo<TEvent, THandler>(
        this DcbModuleBuilder builder,
        Func<THandler, Func<TEvent, ReactorContext, CancellationToken, Task>> methodSelector,
        string processorId,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(methodSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        builder.Services.TryAddScoped<THandler>();

        if (mode == ReactorMode.Sync)
        {
            builder.Services.AddKeyedSingleton<IPostAppendHandler>(builder.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new SyncReactor<TEvent>(async (e, context, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, context, ct);
                });
            });
        }
        else
        {
            builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(processorId, async (e, context, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, context, ct);
                });
            });
        }

        return builder;
    }

    /// <summary>
    /// Configures independent per-processor control loops.
    /// Each registered <see cref="Subscriptions.IEventProcessor"/> gets its own polling loop
    /// that advances its own checkpoint independently up to <see cref="Subscriptions.EventStoreHead.Current"/>.
    /// </summary>
    /// <param name="builder">The module builder.</param>
    /// <param name="configure">Optional action to configure the control loops.</param>
    /// <returns>The module builder for chaining.</returns>
    public static DcbModuleBuilder WithControlLoop(
        this DcbModuleBuilder builder,
        Action<ControlLoopBuilder>? configure = null)
    {
        builder.ControlLoopConfigured = true;
        var loopBuilder = new ControlLoopBuilder(builder);
        configure?.Invoke(loopBuilder);
        loopBuilder.Build();
        return builder;
    }
}
