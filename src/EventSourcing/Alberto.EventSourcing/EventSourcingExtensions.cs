using Alberto.EventSourcing.Projectors;
using Alberto.EventStore;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.EventSourcing;

/// <summary>
/// Extension methods for registering event sourcing components in dependency injection.
/// </summary>
public static class EventSourcingExtensions
{
    /// <summary>
    /// Registers an event-sourced repository for a specific state type with its projector.
    /// The repository will use the EventStoreFactory registered in DI (schema-aware via typed subclass).
    /// </summary>
    /// <typeparam name="TState">The aggregate state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEventSourcedRepository<TState, TProjector>(
        this IServiceCollection services)
        where TState : new()
        where TProjector : class, IProjector<TState>
    {
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<IEventSourcedRepository<TState>, EventSourcedRepository<TState>>();

        return services;
    }

    /// <summary>
    /// Registers an event-sourced repository for a specific state type with a  projector.
    /// Useful when working with multiple schemas/modules (e.g., OrderEventStore, PaymentEventStore).
    /// </summary>
    /// <typeparam name="TState">The aggregate state type</typeparam>
    /// <typeparam name="TProjector">The projector implementation for this state</typeparam>
    /// <typeparam name="TEventStore">The typed EventStoreFactory (e.g., OrderEventStore)</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEventSourcedRepository<TState, TProjector, TEventStore>(
        this IServiceCollection services)
        where TState : new()
        where TProjector : class, IProjector<TState>
        where TEventStore : EventStoreFactory
    {
        services.AddScoped<IProjector<TState>, TProjector>();
        services.AddScoped<TProjector>();

        services.AddScoped<IEventSourcedRepository<TState>, EventSourcedRepository<TState>>(sp =>
            new EventSourcedRepository<TState>(
                sp.GetRequiredService<TEventStore>(),
                sp.GetRequiredService<IProjector<TState>>()
            ));

        return services;
    }
}