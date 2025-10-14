using Alberto.CQRS.Registration;
using Alberto.EventStore;

namespace Alberto.CQRS;

/// <summary>
/// Extension methods for adding CQRS support to EventStore modules
/// </summary>
public static class ModuleBuilderExtensions
{
    /// <summary>
    /// Adds CQRS support (command handlers, query handlers, validators) to the EventStore module.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore factory type</typeparam>
    /// <param name="moduleBuilder">The module builder</param>
    /// <param name="configure">Configuration action for CQRS builder</param>
    /// <returns>The module builder for chaining</returns>
    public static ModuleBuilder<TEventStore> WithCQRS<TEventStore>(
        this ModuleBuilder<TEventStore> moduleBuilder,
        Action<CQRSBuilder> configure)
        where TEventStore : EventStoreFactory
    {
        var cqrsBuilder = new CQRSBuilder(moduleBuilder.Services);
        configure(cqrsBuilder);
        cqrsBuilder.Build();

        return moduleBuilder;
    }
}