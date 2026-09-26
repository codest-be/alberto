using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alberto.Upcasting;

namespace Alberto;

/// <summary>
/// JSON-based event serializer that maps between stored event type ids and CLR types through an
/// <see cref="IEventTypeRegistry"/>. Build once per module, reuse across the lifetime of the app.
/// </summary>
/// <remarks>
/// Build it by scanning assemblies (<see cref="FromAssemblies"/>) or, under trimming or Native AOT,
/// from a hand-written registry (<see cref="FromRegistry(IEventTypeRegistry)"/>). Either way every
/// id, version, tag and JSON contract is read from the registry.
/// </remarks>
public sealed class EventSerializer
{
    private readonly IEventTypeRegistry _registry;
    private readonly UpcasterRegistry? _upcasters;

    // Set only on the reflection-built paths: the options an unregistered event type is written
    // with, by runtime type. A hand-written registry has no such fallback and refuses instead.
    private readonly JsonSerializerOptions? _unregisteredTypeOptions;

    private EventSerializer(
        IEventTypeRegistry registry,
        UpcasterRegistry? upcasters,
        JsonSerializerOptions? unregisteredTypeOptions)
    {
        _registry = registry;
        _upcasters = upcasters;
        _unregisteredTypeOptions = unregisteredTypeOptions;
    }

    /// <summary>
    /// The options a serializer gets when the caller passes none. Also what an envelope is read
    /// with when no serializer is configured at all (see <see cref="ReflectionJson"/>), so both
    /// paths agree on property-name casing.
    /// </summary>
    internal static JsonSerializerOptions CreateDefaultOptions() => new() { PropertyNameCaseInsensitive = true };

    /// <summary>The registry every id, version, tag and JSON contract is read from.</summary>
    public IEventTypeRegistry Registry => _registry;

    /// <summary>
    /// Creates an EventSerializer by scanning the given assemblies for all
    /// <see cref="IEvent"/> types that have an <see cref="EventTypeAttribute"/>.
    /// </summary>
    [RequiresUnreferencedCode(EventTypeRegistry.ScanMessage)]
    [RequiresDynamicCode(EventTypeRegistry.ScanMessage)]
    public static EventSerializer FromAssemblies(
        JsonSerializerOptions? options = null,
        params Assembly[] assemblies)
    {
        var resolved = options ?? CreateDefaultOptions();
        return FromScannedRegistry(EventTypeRegistry.FromAssemblies(resolved, assemblies), resolved);
    }

    /// <summary>
    /// Creates an EventSerializer by scanning a single assembly.
    /// </summary>
    [RequiresUnreferencedCode(EventTypeRegistry.ScanMessage)]
    [RequiresDynamicCode(EventTypeRegistry.ScanMessage)]
    public static EventSerializer FromAssembly(Assembly assembly, JsonSerializerOptions? options = null)
        => FromAssemblies(options, assembly);

    /// <summary>
    /// The serializer <c>WithEventsFrom</c> registers: over a registry scanned with
    /// <paramref name="options"/>, still writing an unregistered event type by runtime type with
    /// them, exactly as <see cref="FromAssembly"/> does.
    /// </summary>
    [RequiresUnreferencedCode(EventTypeRegistry.ScanMessage)]
    [RequiresDynamicCode(EventTypeRegistry.ScanMessage)]
    internal static EventSerializer FromScannedRegistry(IEventTypeRegistry registry, JsonSerializerOptions options)
        => new(registry, null, ReflectionJson.EnsureResolver(options));

    /// <summary>
    /// Creates an EventSerializer over an existing <see cref="IEventTypeRegistry"/> — typically a
    /// hand-written one from <see cref="EventTypeRegistry.CreateBuilder"/>, which is the path that
    /// is safe under trimming and Native AOT.
    /// </summary>
    public static EventSerializer FromRegistry(IEventTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new EventSerializer(registry, null, null);
    }

    /// <summary>
    /// Creates an EventSerializer from an explicitly constructed type registry.
    /// Prefer <see cref="FromAssemblies"/> or <see cref="FromAssembly"/> for normal use;
    /// use this factory when you need tight control over which types are included
    /// (e.g. in tests that scan a large assembly with duplicate event-type IDs).
    /// </summary>
    [RequiresUnreferencedCode(EventTypeRegistry.ScanMessage)]
    [RequiresDynamicCode(EventTypeRegistry.ScanMessage)]
    internal static EventSerializer FromRegistry(
        IReadOnlyDictionary<string, Type> registry,
        JsonSerializerOptions? options = null,
        UpcasterRegistry? upcasters = null)
    {
        var resolved = options ?? CreateDefaultOptions();
        return new(
            EventTypeRegistry.Create(
                registry.Select(kv => EventTypeRegistry.Reflect(kv.Key, kv.Value, resolved)),
                allowSharedClrType: true),
            upcasters,
            ReflectionJson.EnsureResolver(resolved));
    }

    /// <summary>
    /// Returns a new <see cref="EventSerializer"/> that applies the given
    /// <see cref="UpcasterRegistry"/> during <see cref="Deserialize"/>.
    /// </summary>
    public EventSerializer WithUpcasters(UpcasterRegistry upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        return new EventSerializer(_registry, upcasters, _unregisteredTypeOptions);
    }

    /// <summary>
    /// Deserializes an event envelope to its concrete CLR type using the registry.
    /// If an <see cref="UpcasterRegistry"/> is configured and the envelope carries a version
    /// lower than the chain's current version, the upcast chain is applied first.
    /// Throws <see cref="InvalidOperationException"/> if the event type is not registered, or if
    /// the envelope is older than the type's declared version and nothing covers the gap.
    /// </summary>
    /// <remarks>
    /// The version guard is the runtime counterpart of <c>ALB0018</c>/<c>ALB0020</c>. Those run at
    /// startup and only on the DI path; a serializer built by hand — a migration script, a test
    /// helper, a one-off tool — never meets the validator, so the check is repeated here where it
    /// cannot be bypassed. Opt out per event type with
    /// <c>[EventType(..., UpcastingNotRequired = true)]</c>.
    /// </remarks>
    public IEvent Deserialize(IEventEnvelope envelope)
    {
        if (!_registry.TryGetById(envelope.EventType.Id, out var descriptor))
            throw new InvalidOperationException($"No registered type for event '{envelope.EventType.Id}'. " +
                $"Ensure the type has [EventType(\"{envelope.EventType.Id}\")] and its assembly was included when building the serializer.");

        var version = envelope.EventType.Version;
        var type = descriptor.ClrType;
        var declared = (descriptor.Version, descriptor.UpcastingNotRequired);

        // Apply upcasting if the envelope is at an older schema version.
        UpcasterDeclaration? upcaster = null;
        if (_upcasters is not null && _upcasters.TryGet(envelope.EventType.Id, out var declaration))
            upcaster = declaration;

        if (upcaster is not null)
        {
            if (version > upcaster.CurrentVersion)
                throw new InvalidOperationException(
                    $"Upcaster for '{envelope.EventType.Id}' has no step for version {version}. " +
                    $"The chain covers up to version {upcaster.CurrentVersion}. " +
                    "This event was written by a newer version of the application.");

            if (version < upcaster.CurrentVersion)
                return upcaster.Apply(version, envelope.EventData, descriptor.JsonTypeInfo.Options);

            // version == currentVersion: fall through, but the chain may still stop short of the
            // version the CLR type declares — the guard below is what catches that.
        }

        // Nothing brought the envelope up to the shape the CLR type describes. Deserializing it
        // anyway would not fail: JSON leaves every member the older payload lacks at its CLR
        // default, so the caller folds a 0, an empty string or a null as though it had been
        // stored. Refuse instead — a read that throws is recoverable, state built from defaults
        // is not.
        if (version < declared.Version && !declared.UpcastingNotRequired)
        {
            var cause = upcaster is null
                ? "No upcaster is registered for it"
                : $"Its upcaster chain stops at version {upcaster.CurrentVersion}";

            throw new InvalidOperationException(
                $"Event '{envelope.EventType.Id}' is stored at schema version {version}, but " +
                $"'{type.Name}' declares [EventType(Version = {declared.Version})]. {cause}, so the " +
                "stored payload would be deserialized straight into the current shape and every " +
                $"member added since version {version} would silently take its default value." +
                Environment.NewLine +
                $"  → Register an upcaster covering versions {version}..{declared.Version - 1}: " +
                $".AddUpcaster(DeclareUpcaster.For<{type.Name}>(\"{envelope.EventType.Id}\")" +
                $".From<{type.Name}V{version}>({version}, ...).Build())." + Environment.NewLine +
                "  → Or, if this bump only added optional members whose defaults are already the " +
                "right values for older events, say so at the declaration site: " +
                $"[EventType(\"{envelope.EventType.Id}\", Version = {declared.Version}, " +
                "UpcastingNotRequired = true)].");
        }

        return (IEvent)(JsonSerializer.Deserialize(envelope.EventData, descriptor.JsonTypeInfo)
            ?? throw new InvalidOperationException($"Failed to deserialize event '{envelope.EventType.Id}'."));
    }

    /// <summary>
    /// Serializes an event to its JSON representation, with the contract its registry entry holds.
    /// </summary>
    /// <remarks>
    /// A serializer built by assembly scan still writes an event whose CLR type it did not
    /// register, by runtime type with its options, as it always has. One built from a registry
    /// refuses: its contracts come from the registry, and there is nothing to resolve one from.
    /// </remarks>
    public string Serialize(IEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return JsonSerializer.Serialize(@event, ResolveTypeInfo(@event.GetType()));
    }

    /// <summary>
    /// Returns all event type IDs registered in this serializer.
    /// </summary>
    public IEnumerable<string> RegisteredTypeIds => _registry.Descriptors.Select(d => d.Id);

    /// <summary>
    /// The <see cref="EventType"/> (id and version) <paramref name="event"/> is appended under:
    /// from the registry when its type is registered, otherwise from its
    /// <see cref="EventTypeAttribute"/>.
    /// </summary>
    internal EventType GetEventType(IEvent @event)
        => _registry.TryGetByType(@event.GetType(), out var d) ? d.EventType : EventType.FromType(@event.GetType());

    private JsonTypeInfo ResolveTypeInfo(Type type)
    {
        if (_registry.TryGetByType(type, out var descriptor))
            return descriptor.JsonTypeInfo;

        return _unregisteredTypeOptions?.GetTypeInfo(type)
               ?? throw new InvalidOperationException(
                   $"'{type.FullName}' is not registered in this serializer's event type registry. " +
                   "Add it to the registry the serializer was built from.");
    }

    /// <summary>
    /// Extracts <see cref="EventTag"/>s from an event using <see cref="TagAttribute"/> on properties,
    /// and appends the reserved schema-version tag <c>_version:N</c> derived from the type's
    /// <see cref="EventTypeAttribute.Version"/>.
    /// </summary>
    /// <param name="event">The event to extract tags from.</param>
    /// <param name="valueTransform">
    /// Optional transform: (concept, rawValue) => tagValue.
    /// Use this for values that need hashing or other normalization before becoming valid tag IDs.
    /// </param>
    public IReadOnlyCollection<EventTag> ExtractTags(
        IEvent @event,
        Func<string, string, string>? valueTransform = null)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // A registered type reads its tags and version from the registry. An unregistered one —
        // appended through a serializer whose scan did not include its assembly — falls back to
        // reflection, as it always has; IEvent's annotation keeps its properties under trimming.
        var eventType = @event.GetType();
        EventTagExtractor extractor;
        int schemaVersion;
        if (_registry.TryGetByType(eventType, out var descriptor))
        {
            extractor = descriptor.TagExtractor;
            schemaVersion = descriptor.Version;
        }
        else
        {
            extractor = ReflectionTagExtractor.For(eventType);
            schemaVersion = EventTypeAttribute.GetEventType(eventType)?.Version ?? 1;
        }

        var tags = new List<EventTag>();

        foreach (var (concept, value) in extractor(@event))
        {
            if (value is null) continue;

            var rawValue = value is Guid g ? g.ToString("D") : value.ToString()!;
            var tagValue = valueTransform is not null ? valueTransform(concept, rawValue) : rawValue;
            tags.Add(new EventTag(concept, tagValue));
        }

        // Stamp the reserved schema-version tag LAST. EventTag.ForVersion uses FromStorage so
        // it never triggers the public-constructor reservation guard.
        tags.Add(EventTag.ForVersion(schemaVersion));

        return tags;
    }
}
