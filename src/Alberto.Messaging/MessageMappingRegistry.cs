namespace Alberto.Messaging;

/// <summary>
/// Default in-memory implementation of <see cref="IMessageMappingRegistry"/>.
/// </summary>
public sealed class MessageMappingRegistry : IMessageMappingRegistry
{
    private readonly Dictionary<string, EventToMessageMapper> _mappers = new();

    // The control loop reads MappedEventTypes once per event, so the set is built once and
    // dropped whenever a mapping is added.
    private IReadOnlySet<string>? _mappedEventTypes;

    /// <summary>
    /// Creates a registry bound to <paramref name="moduleKey"/>.
    /// Passing <c>null</c> is valid for scenarios where no keyed <see cref="EventSerializer"/>
    /// exists (e.g. modules without upcasters) — extension-method mappers will resolve
    /// <c>GetKeyedService&lt;EventSerializer&gt;(null)</c> and fall back to raw JSON.
    /// </summary>
    public MessageMappingRegistry(string? moduleKey) => ModuleKey = moduleKey;

    /// <inheritdoc/>
    public string? ModuleKey { get; }

    /// <inheritdoc/>
    public void Map(string eventType, EventToMessageMapper mapper)
    {
        _mappers[eventType] = mapper;
        _mappedEventTypes = null;
    }

    /// <inheritdoc/>
    public void Map<TEvent>(EventToMessageMapper mapper) where TEvent : IEvent
        => Map(EventTypeAttribute.GetEventTypeId(typeof(TEvent)), mapper);

    /// <inheritdoc/>
    public ValueTask<ExternalMessage?> TryMapAsync(
        IEventEnvelope envelope, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => _mappers.TryGetValue(envelope.EventType.Id, out var mapper)
            ? mapper(envelope, serviceProvider, cancellationToken)
            : ValueTask.FromResult<ExternalMessage?>(null);

    /// <inheritdoc/>
    public IReadOnlySet<string> MappedEventTypes => _mappedEventTypes ??= _mappers.Keys.ToHashSet();
}
