using Alberto;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Alberto.Configuration;
using Alberto.Upcasting;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Commands;

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
    [RequiresUnreferencedCode(ScanMessage)]
    [RequiresDynamicCode(ScanMessage)]
    public static DcbModuleBuilder WithEventsFrom(
        this DcbModuleBuilder builder,
        Assembly eventsAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(eventsAssembly);

        // Scan the assembly eagerly (Phase 1) so AlbertoModuleValidator can cross-check
        // upcaster coverage at startup without holding a reference to an Assembly.
        var options = EventSerializer.CreateDefaultOptions();
        var registry = EventTypeRegistry.FromAssembly(eventsAssembly, options);

        // The scanned serializer, not FromRegistry: it keeps writing an event type the scan did
        // not register, by runtime type, as WithEventsFrom always has.
        return RegisterEvents(builder, registry, r => EventSerializer.FromScannedRegistry(r, options));
    }

    /// <summary>
    /// Declares this module's event types from an <see cref="IEventTypeRegistry"/>, and registers
    /// the <see cref="AlbertoStore"/> command pipeline over the module's event store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same registration as <see cref="WithEventsFrom"/>, minus the assembly scan: the
    /// module's <see cref="EventSerializer"/> is built over <paramref name="registry"/>, and the
    /// startup upcaster checks (<c>ALB0018</c>–<c>ALB0020</c>) read its descriptors. Pair it with
    /// a registry from <see cref="EventTypeRegistry.CreateBuilder"/> in a trimmed or Native AOT
    /// application, where assembly scanning is unavailable.
    /// </para>
    /// </remarks>
    /// <param name="builder">The module builder passed to <c>services.AddAlberto(...)</c>.</param>
    /// <param name="registry">The module's event types.</param>
    /// <returns>The builder for continued chaining.</returns>
    /// <example>
    /// <code>
    /// var events = EventTypeRegistry.CreateBuilder()
    ///     .Add(OrdersJsonContext.Default.OrderCreated, e =&gt; [new("order", ((OrderCreated)e).OrderId)])
    ///     .Build();
    ///
    /// services.AddAlberto("orders", builder =&gt; builder
    ///     .WithPostgres(options =&gt; options.ConnectionString = "...")
    ///     .WithEvents(events));
    /// </code>
    /// </example>
    public static DcbModuleBuilder WithEvents(
        this DcbModuleBuilder builder,
        IEventTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registry);

        return RegisterEvents(builder, registry, EventSerializer.FromRegistry);
    }

    private static DcbModuleBuilder RegisterEvents(
        DcbModuleBuilder builder,
        IEventTypeRegistry registry,
        Func<IEventTypeRegistry, EventSerializer> createSerializer)
    {
        var registeredEventTypes = registry.Descriptors
            .Select(d => new RegisteredEventType(d.Id, d.Version, d.UpcastingNotRequired))
            .ToImmutableArray();

        builder.Configure(d => d with { RegisteredEventTypes = registeredEventTypes });

        // Defer serializer construction to the Register callback so that AddUpcaster calls
        // that come *before or after* WithEvents are both visible here: all Configure
        // callbacks (including the one AddUpcaster uses) run before any Register callback,
        // so context.Definition.UpcasterDeclarations is final when this lambda executes.
        builder.Register(context =>
        {
            var moduleKey = context.ModuleKey;

            // Build the upcaster registry from whatever was declared via AddUpcaster.
            var serializer = createSerializer(registry);
            if (context.Definition.UpcasterDeclarations.Length > 0)
            {
                var upcasters = UpcasterRegistry.Create();
                foreach (var decl in context.Definition.UpcasterDeclarations)
                    upcasters.Add(decl);
                serializer = serializer.WithUpcasters(upcasters.Build());
            }

            // Register only under the module key.
            // The previous TryAddSingleton(serializer) registered the first module's serializer
            // as the unkeyed singleton, causing outbox mappers in all other modules to silently
            // resolve the wrong module's serializer.
            context.Services.AddKeyedSingleton<EventSerializer>(moduleKey, serializer);
            context.Services.AddKeyedScoped(moduleKey, (sp, _) => new AlbertoStore(
                sp.GetRequiredKeyedService<IEventStore>(moduleKey),
                serializer,
                sp));
        });

        return builder;
    }

    private const string ScanMessage =
        "Scans the assembly for [EventType] types and reads their [Tag] properties and JSON " +
        "contracts by reflection. Under trimming or Native AOT, use WithEvents(IEventTypeRegistry) " +
        "with a registry from EventTypeRegistry.CreateBuilder().";
}
