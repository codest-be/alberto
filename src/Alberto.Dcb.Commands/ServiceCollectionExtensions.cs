using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for registering <see cref="AlbertoStore"/> with DI.
/// </summary>
public static class AlbertoStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AlbertoStore"/> as a singleton for this module.
    /// Chain this from the <c>AddAlberto</c> builder so the keyed module key is captured
    /// automatically and the registration stays alongside all other module configuration.
    /// </summary>
    /// <param name="builder">The module builder returned by <c>services.AddAlberto(...)</c>.</param>
    /// <param name="eventsAssembly">
    /// Assembly that contains the <see cref="EventTypeAttribute"/>-annotated event types.
    /// Used to build the <see cref="EventSerializer"/> registry.
    /// </param>
    /// <returns>The builder for continued chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", builder => builder
    ///     .WithPostgres(options => options.ConnectionString = "...")
    ///     .AddAlbertoStore(typeof(OrderCreated).Assembly)
    ///     .WithControlLoop()
    /// );
    /// </code>
    /// </example>
    public static DcbModuleBuilder AddAlbertoStore(
        this DcbModuleBuilder builder,
        Assembly eventsAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(eventsAssembly);

        var moduleKey = builder.ModuleKey;
        builder.Services.AddSingleton(sp => new AlbertoStore(
            sp.GetRequiredKeyedService<IEventStore>(moduleKey),
            EventSerializer.FromAssembly(eventsAssembly)));

        return builder;
    }

    /// <summary>
    /// Registers <see cref="AlbertoStore"/> as a singleton using an explicit module key.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleKey">The keyed-service key used when calling <c>AddAlberto</c>.</param>
    /// <param name="eventsAssembly">Assembly containing the event types.</param>
    /// <returns>The service collection.</returns>
    [Obsolete(
        "Use builder.AddAlbertoStore(eventsAssembly) inside your AddAlberto(...) builder callback " +
        "so the module key is captured automatically. " +
        "This overload will be removed in a future version.",
        error: false)]
    public static IServiceCollection AddAlbertoStore(
        this IServiceCollection services,
        object moduleKey,
        Assembly eventsAssembly) =>
        services.AddSingleton(sp => new AlbertoStore(
            sp.GetRequiredKeyedService<IEventStore>(moduleKey),
            EventSerializer.FromAssembly(eventsAssembly)));
}
