using System.Collections.Immutable;
using System.Reflection;
using Alberto.Dcb.Configuration;
using Alberto.Dcb.Upcasting;
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
    /// (<c>Handle → Load → Decide → Commit</c>) over the module's event store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assembly scan builds the module's <see cref="EventSerializer"/> registry, which is what
    /// <see cref="AlbertoStore"/> uses to serialize appended events and deserialize streamed ones.
    /// Projections and reactors do not need this call — they resolve their event types statically
    /// through <c>On&lt;TEvent&gt;</c>.
    /// </para>
    /// <para>
    /// The store is registered <b>keyed</b> by module key and scoped, matching how
    /// <see cref="IEventStore"/> is registered. A host running several modules therefore gets one
    /// store per module; an unkeyed registration would leave the last module registered owning the
    /// only <see cref="AlbertoStore"/>, wrapping the wrong log for every other module.
    /// </para>
    /// <para>
    /// The service provider is passed to the store so that
    /// <c>Load&lt;TState&gt;(boundary)</c> can resolve <c>Evolver&lt;TState&gt;</c> from DI.
    /// </para>
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
    ///
    /// var store = sp.GetRequiredKeyedService&lt;AlbertoStore&gt;("orders");
    /// </code>
    /// </example>
    public static DcbModuleBuilder WithEventsFrom(
        this DcbModuleBuilder builder,
        Assembly eventsAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(eventsAssembly);

        // Scan the assembly eagerly (Phase 1) so AlbertoModuleValidator can cross-check
        // upcaster coverage at startup without holding a reference to an Assembly.
        var registeredEventTypes = eventsAssembly
            .GetTypes()
            .Where(t => typeof(IEvent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (t, attr: EventTypeAttribute.GetEventType(t)))
            .Where(x => x.attr is not null)
            .Select(x => new RegisteredEventType(x.attr!.Id, x.attr.Version))
            .ToImmutableArray();

        builder.Configure(d => d with { RegisteredEventTypes = registeredEventTypes });

        // Defer serializer construction to the Register callback so that AddUpcaster calls
        // that come *before or after* WithEventsFrom are both visible here: all Configure
        // callbacks (including the one AddUpcaster uses) run before any Register callback,
        // so context.Definition.UpcasterDeclarations is final when this lambda executes.
        builder.Register(context =>
        {
            var moduleKey = context.ModuleKey;

            // Build the upcaster registry from whatever was declared via AddUpcaster.
            EventSerializer serializer;
            if (context.Definition.UpcasterDeclarations.Length > 0)
            {
                var registryBuilder = UpcasterRegistry.Create();
                foreach (var decl in context.Definition.UpcasterDeclarations)
                    registryBuilder.Add(decl);
                serializer = EventSerializer.FromAssembly(eventsAssembly)
                    .WithUpcasters(registryBuilder.Build());
            }
            else
            {
                serializer = EventSerializer.FromAssembly(eventsAssembly);
            }

            // Register only under the module key.
            // The previous TryAddSingleton(serializer) registered the first module's serializer
            // as the unkeyed singleton, causing outbox mappers in all other modules to silently
            // resolve the wrong module's serializer.  The outbox mapper fix
            // (MessageMappingRegistryExtensions.cs) is tracked as a deferred edit.
            context.Services.AddKeyedSingleton<EventSerializer>(moduleKey, serializer);
            context.Services.AddKeyedScoped(moduleKey, (sp, _) => new AlbertoStore(
                sp.GetRequiredKeyedService<IEventStore>(moduleKey),
                serializer,
                sp));
        });

        return builder;
    }
}
