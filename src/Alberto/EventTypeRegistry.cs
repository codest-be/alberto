using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Alberto;

/// <summary>
/// Reads the <see cref="TagAttribute"/>-marked values off an event, as <c>(concept, value)</c>
/// pairs. A <see langword="null"/> value is skipped; any other value is turned into a tag value by
/// <see cref="EventSerializer.ExtractTags"/>, which formats a <see cref="Guid"/> as <c>"D"</c> and
/// anything else with <see cref="object.ToString"/>.
/// </summary>
/// <remarks>
/// The extractor yields raw values, not <see cref="EventTag"/>s, so that the one formatting rule
/// and the caller's value transform live in a single place whichever way the registry was built.
/// </remarks>
/// <example>
/// <code>
/// EventTagExtractor tags = e =&gt; [new("order", ((OrderPlaced)e).OrderId)];
/// </code>
/// </example>
public delegate IEnumerable<KeyValuePair<string, object?>> EventTagExtractor(IEvent @event);

/// <summary>
/// Everything Alberto needs to know about one event type to write and read it without reflection:
/// its stored id and schema version, the CLR type, the JSON contract, and how to read its tags.
/// </summary>
/// <remarks>
/// A descriptor is what an <see cref="IEventTypeRegistry"/> holds. The reflection scan
/// (<see cref="EventTypeRegistry.FromAssemblies"/>) and a hand-written registry
/// (<see cref="EventTypeRegistry.CreateBuilder"/>) produce the same descriptors for the same
/// types, which is what lets a trimmed or Native AOT application use the second in place of the
/// first.
/// </remarks>
public sealed class EventTypeDescriptor
{
    private readonly Lazy<JsonTypeInfo>? _lazyJsonTypeInfo;
    private readonly JsonTypeInfo? _jsonTypeInfo;
    private readonly Lazy<EventTagExtractor>? _lazyTagExtractor;
    private readonly EventTagExtractor? _tagExtractor;

    /// <summary>Creates a descriptor.</summary>
    /// <param name="id">The stored event type id, the value of <see cref="EventTypeAttribute.Id"/>.</param>
    /// <param name="version">The current schema version, the value of <see cref="EventTypeAttribute.Version"/>.</param>
    /// <param name="upcastingNotRequired">The value of <see cref="EventTypeAttribute.UpcastingNotRequired"/>.</param>
    /// <param name="jsonTypeInfo">
    /// The JSON contract for the event's CLR type, usually from a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>. Its
    /// <see cref="JsonTypeInfo.Type"/> is the descriptor's <see cref="ClrType"/>, and its
    /// <see cref="JsonTypeInfo.Options"/> are what upcaster steps resolve their older shapes from.
    /// </param>
    /// <param name="tagExtractor">Reads the event's tag values. Pass one that yields nothing for an untagged event.</param>
    public EventTypeDescriptor(
        string id,
        int version,
        bool upcastingNotRequired,
        JsonTypeInfo jsonTypeInfo,
        EventTagExtractor tagExtractor)
        : this(id, version, upcastingNotRequired,
               (jsonTypeInfo ?? throw new ArgumentNullException(nameof(jsonTypeInfo))).Type)
    {
        ArgumentNullException.ThrowIfNull(tagExtractor);
        _jsonTypeInfo = jsonTypeInfo;
        _tagExtractor = tagExtractor;
    }

    /// <summary>
    /// The reflection path's constructor: the contract and the extractor are built on first use,
    /// as the serializer did before the registry existed, so scanning a large assembly does not
    /// resolve a JSON contract or compile a getter for types that are never written or read.
    /// </summary>
    internal EventTypeDescriptor(
        string id,
        int version,
        bool upcastingNotRequired,
        Type clrType,
        Func<JsonTypeInfo> jsonTypeInfo,
        Func<EventTagExtractor> tagExtractor)
        : this(id, version, upcastingNotRequired, clrType)
    {
        _lazyJsonTypeInfo = new Lazy<JsonTypeInfo>(jsonTypeInfo);
        _lazyTagExtractor = new Lazy<EventTagExtractor>(tagExtractor);
    }

    private EventTypeDescriptor(string id, int version, bool upcastingNotRequired, Type clrType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(clrType);

        if (!typeof(IEvent).IsAssignableFrom(clrType))
            throw new ArgumentException(
                $"'{clrType.FullName}' does not implement {nameof(IEvent)}.", nameof(clrType));

        Id = id;
        Version = version;
        UpcastingNotRequired = upcastingNotRequired;
        ClrType = clrType;
    }

    /// <summary>The stored event type id.</summary>
    public string Id { get; }

    /// <summary>The schema version events of this type are written at.</summary>
    public int Version { get; }

    /// <summary>
    /// Whether an envelope stored at an older version may be read without an upcaster.
    /// See <see cref="EventTypeAttribute.UpcastingNotRequired"/>.
    /// </summary>
    public bool UpcastingNotRequired { get; }

    /// <summary>The CLR type events of this id deserialize to.</summary>
    public Type ClrType { get; }

    /// <summary>The <see cref="Alberto.EventType"/> an event of this type is appended as.</summary>
    public EventType EventType => new(Id, Version);

    /// <summary>The JSON contract events of this type are written and read with.</summary>
    public JsonTypeInfo JsonTypeInfo => _jsonTypeInfo ?? _lazyJsonTypeInfo!.Value;

    /// <summary>Reads the event's tag values.</summary>
    public EventTagExtractor TagExtractor => _tagExtractor ?? _lazyTagExtractor!.Value;
}

/// <summary>
/// The set of event types a module writes and reads, keyed both by stored id and by CLR type.
/// </summary>
/// <remarks>
/// <see cref="EventSerializer"/> reads every event type fact through this: the id and version an
/// event is appended under, its tags, its JSON contract, and the version guard on read. Build one
/// by scanning assemblies with <see cref="EventTypeRegistry.FromAssemblies"/>, or, where
/// reflection is unavailable, list the types by hand with <see cref="EventTypeRegistry.CreateBuilder"/>.
/// </remarks>
public interface IEventTypeRegistry
{
    /// <summary>Every registered event type.</summary>
    IReadOnlyCollection<EventTypeDescriptor> Descriptors { get; }

    /// <summary>Looks up an event type by its stored id.</summary>
    bool TryGetById(string id, [NotNullWhen(true)] out EventTypeDescriptor? descriptor);

    /// <summary>Looks up an event type by its CLR type.</summary>
    bool TryGetByType(Type clrType, [NotNullWhen(true)] out EventTypeDescriptor? descriptor);
}

/// <summary>
/// Factories for <see cref="IEventTypeRegistry"/>.
/// </summary>
/// <example>
/// <code>
/// [JsonSerializable(typeof(OrderPlaced))]
/// internal partial class OrdersJsonContext : JsonSerializerContext;
///
/// var registry = EventTypeRegistry.CreateBuilder()
///     .Add(OrdersJsonContext.Default.OrderPlaced,
///          e =&gt; [new("order", e.OrderId)])
///     .Build();
///
/// services.AddAlberto("orders", b =&gt; b.WithPostgres(...).WithEvents(registry));
/// </code>
/// </example>
public static class EventTypeRegistry
{
    internal const string ScanMessage =
        "Discovers event types by scanning assemblies, reads [Tag] properties by reflection and " +
        "resolves JSON contracts through the options' resolver, which is reflection-based unless " +
        "you supply a source-generated one. Under trimming or Native AOT, build the registry with " +
        "EventTypeRegistry.CreateBuilder() and JsonTypeInfo from a JsonSerializerContext instead.";

    /// <summary>Starts a hand-written registry. This path uses no reflection over event types.</summary>
    public static EventTypeRegistryBuilder CreateBuilder() => new();

    /// <summary>
    /// Builds a registry by scanning <paramref name="assemblies"/> for every concrete
    /// <see cref="IEvent"/> type carrying an <see cref="EventTypeAttribute"/>.
    /// </summary>
    /// <param name="options">
    /// The JSON options events are serialized with. Defaults to case-insensitive property names,
    /// the same default <see cref="EventSerializer"/> has always used.
    /// </param>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <exception cref="ArgumentException">Two scanned types declare the same event type id.</exception>
    [RequiresUnreferencedCode(ScanMessage)]
    [RequiresDynamicCode(ScanMessage)]
    public static IEventTypeRegistry FromAssemblies(JsonSerializerOptions? options, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var resolved = options ?? EventSerializer.CreateDefaultOptions();

        var descriptors = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IEvent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Select(t => (type: t, attr: EventTypeAttribute.GetEventType(t)))
            .Where(x => x.attr is not null)
            .Select(x => Reflect(x.attr!.Id, x.type, resolved));

        return Create(descriptors, allowSharedClrType: false);
    }

    /// <summary>
    /// Builds a registry by scanning a single assembly. See <see cref="FromAssemblies"/>.
    /// </summary>
    [RequiresUnreferencedCode(ScanMessage)]
    [RequiresDynamicCode(ScanMessage)]
    public static IEventTypeRegistry FromAssembly(Assembly assembly, JsonSerializerOptions? options = null)
        => FromAssemblies(options, assembly);

    /// <summary>
    /// The reflection-built descriptor for <paramref name="type"/> registered under
    /// <paramref name="id"/>: version and opt-out from its <see cref="EventTypeAttribute"/> (or
    /// version 1 without one), JSON contract from <paramref name="options"/>, tags from its
    /// <see cref="TagAttribute"/> properties. Both of the last two are resolved on first use.
    /// </summary>
    [RequiresUnreferencedCode(ScanMessage)]
    [RequiresDynamicCode(ScanMessage)]
    internal static EventTypeDescriptor Reflect(string id, Type type, JsonSerializerOptions options)
    {
        var attr = EventTypeAttribute.GetEventType(type);
        return new EventTypeDescriptor(
            id,
            attr?.Version ?? 1,
            attr?.UpcastingNotRequired ?? false,
            type,
            () => ReflectionJson.EnsureResolver(options).GetTypeInfo(type),
            () => ReflectionTagExtractor.For(type));
    }

    /// <summary>
    /// Builds the registry both factories end in. <paramref name="allowSharedClrType"/> exists for
    /// the internal <c>EventSerializer.FromRegistry</c> test seam, which maps ids to types by hand
    /// and has always tolerated two ids on one type; the first one wins the by-type index there.
    /// </summary>
    internal static IEventTypeRegistry Create(IEnumerable<EventTypeDescriptor> descriptors, bool allowSharedClrType)
    {
        var byId = new Dictionary<string, EventTypeDescriptor>(StringComparer.Ordinal);
        var byType = new Dictionary<Type, EventTypeDescriptor>();

        foreach (var d in descriptors)
        {
            if (!byId.TryAdd(d.Id, d))
                throw new ArgumentException(
                    $"Event type id '{d.Id}' is declared by both '{byId[d.Id].ClrType.FullName}' " +
                    $"and '{d.ClrType.FullName}'. Event type ids must be unique within a registry.");

            if (!byType.TryAdd(d.ClrType, d) && !allowSharedClrType)
                throw new ArgumentException(
                    $"'{d.ClrType.FullName}' is registered under both '{byType[d.ClrType].Id}' and " +
                    $"'{d.Id}'. A CLR type is written under exactly one event type id.");
        }

        return new Registry(byId.ToFrozenDictionary(StringComparer.Ordinal), byType.ToFrozenDictionary());
    }

    private sealed class Registry(
        FrozenDictionary<string, EventTypeDescriptor> byId,
        FrozenDictionary<Type, EventTypeDescriptor> byType) : IEventTypeRegistry
    {
        public IReadOnlyCollection<EventTypeDescriptor> Descriptors => byId.Values;

        public bool TryGetById(string id, [NotNullWhen(true)] out EventTypeDescriptor? descriptor)
            => byId.TryGetValue(id, out descriptor);

        public bool TryGetByType(Type clrType, [NotNullWhen(true)] out EventTypeDescriptor? descriptor)
            => byType.TryGetValue(clrType, out descriptor);
    }
}

/// <summary>
/// Fluent builder for a hand-written <see cref="IEventTypeRegistry"/>: one <c>Add</c> per event
/// type, each with the <see cref="JsonTypeInfo{T}"/> from your
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>. Nothing here reflects over
/// event types, so the result is safe under trimming and Native AOT.
/// </summary>
public sealed class EventTypeRegistryBuilder
{
    private static readonly EventTagExtractor NoTags = static _ => [];

    private readonly List<EventTypeDescriptor> _descriptors = [];

    internal EventTypeRegistryBuilder() { }

    /// <summary>
    /// Registers <typeparamref name="TEvent"/> under the id, version and opt-out its
    /// <see cref="EventTypeAttribute"/> declares.
    /// </summary>
    /// <param name="jsonTypeInfo">The contract for <typeparamref name="TEvent"/>, e.g. <c>MyContext.Default.OrderPlaced</c>.</param>
    /// <param name="tags">
    /// Reads the event's tag values. Omit for an event with no <see cref="TagAttribute"/>
    /// properties. It must yield exactly what the <see cref="TagAttribute"/>s declare: an
    /// extractor that disagrees writes events a consistency boundary will not match.
    /// </param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> has no <see cref="EventTypeAttribute"/>.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "The typed sibling below is not a longer form of this one: it has the " +
                        "same arity and a required tags parameter of a different delegate shape. " +
                        "OverloadResolutionPriority, not parameter count, is what keeps existing " +
                        "cast-style calls bound here.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads differ by the leading required id; no call binds to both.")]
    public EventTypeRegistryBuilder Add<TEvent>(JsonTypeInfo<TEvent> jsonTypeInfo, EventTagExtractor? tags = null)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        // Reading an attribute off a statically known type is trim- and AOT-safe: the type is
        // rooted by the generic instantiation and its custom attributes are kept with it.
        var attr = EventTypeAttribute.GetEventType(typeof(TEvent))
            ?? throw new InvalidOperationException(
                $"Type '{typeof(TEvent).FullName}' does not have an [EventType] attribute. " +
                "Add one, or register it with an explicit id: Add<TEvent>(id, jsonTypeInfo, ...).");

        return Add(new EventTypeDescriptor(attr.Id, attr.Version, attr.UpcastingNotRequired, jsonTypeInfo, tags ?? NoTags));
    }

    /// <summary>Registers <typeparamref name="TEvent"/> under an explicit id and version.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "The typed sibling below is not a longer form of this one: it has the " +
                        "same arity and a required tags parameter of a different delegate shape. " +
                        "OverloadResolutionPriority, not parameter count, is what keeps existing " +
                        "cast-style calls bound here.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads differ by the leading required id; no call binds to both.")]
    public EventTypeRegistryBuilder Add<TEvent>(
        string id,
        JsonTypeInfo<TEvent> jsonTypeInfo,
        EventTagExtractor? tags = null,
        int version = 1,
        bool upcastingNotRequired = false)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return Add(new EventTypeDescriptor(id, version, upcastingNotRequired, jsonTypeInfo, tags ?? NoTags));
    }

    /// <summary>
    /// Registers <typeparamref name="TEvent"/> under the id, version and opt-out its
    /// <see cref="EventTypeAttribute"/> declares, reading its tags with a typed extractor.
    /// </summary>
    /// <param name="jsonTypeInfo">The contract for <typeparamref name="TEvent"/>, e.g. <c>MyContext.Default.OrderPlaced</c>.</param>
    /// <param name="tags">
    /// Reads the event's tag values, as <c>(concept, value)</c> pairs, from the typed event:
    /// <c>e =&gt; [new("order", e.OrderId)]</c>. It must yield exactly what the
    /// <see cref="TagAttribute"/>s declare; <c>EventTypeRegistryVerifier</c> in Alberto.Testing
    /// checks that in a test. <see langword="null"/> means no tags, as on the untyped overload:
    /// a literal <c>null</c> binds here, and must keep meaning what it meant before this existed.
    /// </param>
    /// <remarks>
    /// A lambda that also compiles against <see cref="IEvent"/> (the untyped
    /// <see cref="EventTagExtractor"/> shape, which usually casts) binds here too; the
    /// <see cref="System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute"/> makes this
    /// overload win instead of the call being ambiguous, and the cast is then merely redundant.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEvent"/> has no <see cref="EventTypeAttribute"/>.</exception>
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public EventTypeRegistryBuilder Add<TEvent>(
        JsonTypeInfo<TEvent> jsonTypeInfo,
        Func<TEvent, IEnumerable<KeyValuePair<string, object?>>>? tags)
        where TEvent : IEvent
        => Add(jsonTypeInfo, Untyped(tags));

    /// <summary>
    /// Registers <typeparamref name="TEvent"/> under an explicit id and version, reading its tags
    /// with a typed extractor.
    /// </summary>
    /// <remarks>See the attribute-driven typed overload for how it binds against the untyped one.</remarks>
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "Separated from the untyped overload by the delegate shape of the required tags parameter.")]
    public EventTypeRegistryBuilder Add<TEvent>(
        string id,
        JsonTypeInfo<TEvent> jsonTypeInfo,
        Func<TEvent, IEnumerable<KeyValuePair<string, object?>>>? tags,
        int version = 1,
        bool upcastingNotRequired = false)
        where TEvent : IEvent
        => Add(id, jsonTypeInfo, Untyped(tags), version, upcastingNotRequired);

    // Null passes through so the untyped overload's "no tags" default applies. The registry only
    // ever hands an extractor an event of the descriptor's own CLR type (EventSerializer looks the
    // descriptor up by @event.GetType()), so the cast cannot fail there. Anything else calling TagExtractor with the wrong type gets an InvalidCastException,
    // which is the same failure the hand-written cast it replaces would have produced.
    private static EventTagExtractor? Untyped<TEvent>(Func<TEvent, IEnumerable<KeyValuePair<string, object?>>>? tags)
        where TEvent : IEvent
        => tags is null ? null : e => tags((TEvent)e);

    /// <summary>Registers a descriptor built elsewhere.</summary>
    public EventTypeRegistryBuilder Add(EventTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptors.Add(descriptor);
        return this;
    }

    /// <summary>Builds the registry.</summary>
    /// <exception cref="ArgumentException">Two registrations share an id, or a CLR type is registered twice.</exception>
    public IEventTypeRegistry Build() => EventTypeRegistry.Create(_descriptors, allowSharedClrType: false);
}
