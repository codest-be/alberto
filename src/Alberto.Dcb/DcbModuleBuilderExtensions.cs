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
    /// <param name="processorId">Optional processor ID. Defaults to "ReactTo-{EventTypeName}".</param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    public static DcbModuleBuilder ReactTo<TEvent>(
        this DcbModuleBuilder builder,
        Func<IServiceProvider, Func<TEvent, CancellationToken, Task>> handlerFactory,
        string? processorId = null,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);

        if (mode == ReactorMode.Sync)
        {
            builder.Services.AddKeyedSingleton<IPostAppendHandler>(builder.ModuleKey, (sp, _) =>
                new SyncReactor<TEvent>(handlerFactory(sp)));
        }
        else
        {
            var id = processorId ?? $"ReactTo-{typeof(TEvent).Name}";
            builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
                new FunctionalReactor<TEvent>(id, handlerFactory(sp)));
        }

        return builder;
    }

    /// <summary>
    /// Registers a functional reactor that reacts to a specific event type,
    /// using a handler class resolved from DI. The method selector picks which
    /// method on the handler to invoke for each event.
    /// </summary>
    /// <typeparam name="TEvent">The event type to react to.</typeparam>
    /// <typeparam name="THandler">The handler class holding dependencies. Registered as scoped if not already registered.</typeparam>
    /// <param name="builder">The module builder.</param>
    /// <param name="methodSelector">Selects the handler method from the resolved handler instance.</param>
    /// <param name="processorId">Optional processor ID. Defaults to "{HandlerTypeName}.{MethodName}".</param>
    /// <param name="mode">
    /// <see cref="ReactorMode.Async"/> (default): runs via background polling.
    /// <see cref="ReactorMode.Sync"/>: runs immediately during <see cref="IEventStore.AppendAsync"/>.
    /// </param>
    public static DcbModuleBuilder ReactTo<TEvent, THandler>(
        this DcbModuleBuilder builder,
        Func<THandler, Func<TEvent, CancellationToken, Task>> methodSelector,
        string? processorId = null,
        ReactorMode mode = ReactorMode.Async)
        where TEvent : class, IEvent
        where THandler : class
    {
        ArgumentNullException.ThrowIfNull(methodSelector);
        builder.Services.TryAddScoped<THandler>();

        if (mode == ReactorMode.Sync)
        {
            builder.Services.AddKeyedSingleton<IPostAppendHandler>(builder.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new SyncReactor<TEvent>(async (e, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                    await methodSelector(handler)(e, ct);
                });
            });
        }
        else
        {
            var id = processorId ?? $"{typeof(THandler).Name}.{typeof(TEvent).Name}";
            builder.Services.AddKeyedSingleton<IEventProcessor>(builder.ModuleKey, (sp, _) =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new FunctionalReactor<TEvent>(id, async (e, ct) =>
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
