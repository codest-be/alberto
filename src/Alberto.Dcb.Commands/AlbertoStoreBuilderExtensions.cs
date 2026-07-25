using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb;

/// <summary>
/// Extension methods for registering <see cref="AlbertoStore"/> with a DCB module.
/// </summary>
public static class AlbertoStoreBuilderExtensions
{
    /// <summary>
    /// Declares which assembly holds this module's <see cref="EventTypeAttribute"/>-annotated
    /// event types, and registers the <see cref="AlbertoStore"/> command pipeline
    /// (<c>Handle → Load → Decide → Persist</c>) as scoped over the module's event store.
    /// </summary>
    /// <remarks>
    /// The assembly scan builds the module's <see cref="EventSerializer"/> registry, which is what
    /// <see cref="AlbertoStore"/> uses to serialize appended events and deserialize streamed ones.
    /// Projections and reactors do not need this call — they resolve their event types statically
    /// through <c>On&lt;TEvent&gt;</c>.
    /// </remarks>
    /// <param name="builder">The module builder passed to <c>services.AddAlberto(...)</c>.</param>
    /// <param name="eventsAssembly">
    /// Assembly that contains the <see cref="EventTypeAttribute"/>-annotated event types.
    /// </param>
    /// <returns>The builder for continued chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddAlberto("orders", builder => builder
    ///     .WithPostgres(options => options.ConnectionString = "...")
    ///     .WithEventsFrom(typeof(OrderCreated).Assembly)
    ///     .WithControlLoop()
    /// );
    /// </code>
    /// </example>
    public static DcbModuleBuilder WithEventsFrom(
        this DcbModuleBuilder builder,
        Assembly eventsAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(eventsAssembly);

        var serializer = EventSerializer.FromAssembly(eventsAssembly);
        builder.Register(context =>
        {
            var moduleKey = context.ModuleKey;
            context.Services.AddScoped(sp => new AlbertoStore(
                sp.GetRequiredKeyedService<IEventStore>(moduleKey),
                serializer));
        });

        return builder;
    }
}
