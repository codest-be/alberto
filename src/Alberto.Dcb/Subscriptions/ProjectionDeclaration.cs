namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Internal handler contract for a single event type within a projection.
/// Wraps typed user functions behind an untyped interface for dictionary dispatch.
/// </summary>
internal interface IProjectionEventHandler<TState>
{
    /// <summary>Derives the document ID from a raw event envelope.</summary>
    string? GetDocumentId(IEventEnvelope envelope);

    /// <summary>Applies the event to the current state.</summary>
    ProjectionResult<TState> Apply(TState state, IEventEnvelope envelope, ProjectionContext ctx);

    /// <summary>Derives the document ID from an already-parsed event (for testing).</summary>
    string? GetDocumentId(object parsedEvent);

    /// <summary>Applies an already-parsed event to the current state (for testing).</summary>
    ProjectionResult<TState> Apply(TState state, object parsedEvent, ProjectionContext ctx);
}

/// <summary>
/// Typed handler that deserializes the event from the envelope and delegates
/// to user-provided <c>id</c> and <c>apply</c> functions.
/// </summary>
internal sealed class ProjectionEventHandler<TState, TEvent>(
    Func<TEvent, string?> getId,
    Func<TState, TEvent, ProjectionContext, ProjectionResult<TState>> apply)
    : IProjectionEventHandler<TState>
    where TEvent : IEvent
{
    public string? GetDocumentId(IEventEnvelope envelope)
        => getId(envelope.ParseEvent<TEvent>());

    public ProjectionResult<TState> Apply(TState state, IEventEnvelope envelope, ProjectionContext ctx)
        => apply(state, envelope.ParseEvent<TEvent>(), ctx);

    public string? GetDocumentId(object parsedEvent) => getId((TEvent)parsedEvent);

    public ProjectionResult<TState> Apply(TState state, object parsedEvent, ProjectionContext ctx)
        => apply(state, (TEvent)parsedEvent, ctx);
}

/// <summary>
/// Compiled projection definition produced by <see cref="ProjectionDeclarationBuilder{TState}"/>.
/// All dispatch logic is stored as typed handlers — no reflection, no string-matching at runtime.
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
public sealed class ProjectionDeclaration<TState> where TState : new()
{
    internal ProjectionDeclaration(
        string processorId,
        string collectionName,
        IReadOnlySet<string> handledEventTypes,
        Func<TState> initialState,
        IReadOnlyDictionary<string, IProjectionEventHandler<TState>> handlers)
    {
        ProcessorId = processorId;
        CollectionName = collectionName;
        HandledEventTypes = handledEventTypes;
        InitialState = initialState;
        Handlers = handlers;
    }

    /// <summary>Processor ID used for checkpointing.</summary>
    public string ProcessorId { get; }

    /// <summary>Storage collection / table name.</summary>
    public string CollectionName { get; }

    /// <summary>The set of event type IDs this projection handles.</summary>
    public IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>Factory that produces the initial (empty) state for a new document.</summary>
    public Func<TState> InitialState { get; }

    /// <summary>Typed handlers keyed by event type ID.</summary>
    internal IReadOnlyDictionary<string, IProjectionEventHandler<TState>> Handlers { get; }

    /// <summary>
    /// Returns the document ID for the given event envelope, or <see langword="null"/> to skip.
    /// Used by <see cref="DeclaredAsyncProjection{TState}"/> at runtime.
    /// </summary>
    public string? GetDocumentId(IEventEnvelope envelope)
        => GetHandler(envelope.EventType.Id).GetDocumentId(envelope);

    /// <summary>
    /// Applies an event envelope to the given state and returns the result.
    /// Used by <see cref="DeclaredAsyncProjection{TState}"/> at runtime.
    /// </summary>
    public ProjectionResult<TState> Apply(TState state, IEventEnvelope envelope, ProjectionContext context)
        => GetHandler(envelope.EventType.Id).Apply(state, envelope, context);

    /// <summary>
    /// Returns the document ID for a typed event. No envelope construction needed — for testing.
    /// </summary>
    public string? GetDocumentId<TEvent>(TEvent @event) where TEvent : IEvent
        => GetHandler(EventTypeAttribute.GetEventTypeId(typeof(TEvent))).GetDocumentId(@event!);

    /// <summary>
    /// Applies a typed event to the given state. No envelope construction needed — for testing.
    /// </summary>
    public ProjectionResult<TState> Apply<TEvent>(TState state, TEvent @event, ProjectionContext context)
        where TEvent : IEvent
        => GetHandler(EventTypeAttribute.GetEventTypeId(typeof(TEvent))).Apply(state, @event!, context);

    private IProjectionEventHandler<TState> GetHandler(string eventTypeId)
        => Handlers.TryGetValue(eventTypeId, out var handler)
            ? handler
            : throw new InvalidOperationException(
                $"Projection '{ProcessorId}': no handler registered for event type '{eventTypeId}'.");
}

/// <summary>
/// Factory entry point for the declaration-based projection API.
/// </summary>
/// <example>
/// <code>
/// var projection = DeclareProjection.For&lt;OrderSummary&gt;("order-summary")
///     .On&lt;OrderCreated&gt;(
///         id: e =&gt; e.OrderId.ToString(),
///         apply: (state, e, ctx) =&gt; new OrderSummary { OrderId = e.OrderId })
///     .On&lt;OrderConfirmed&gt;(
///         id: e =&gt; e.OrderId.ToString(),
///         apply: (state, e, ctx) =&gt; state with { Confirmed = true })
///     .Build();
/// </code>
/// </example>
public static class DeclareProjection
{
    /// <summary>
    /// Starts building a projection declaration for the given state type.
    /// </summary>
    /// <typeparam name="TState">The projection state type. Must have a parameterless constructor.</typeparam>
    /// <param name="processorId">
    /// Unique processor identifier used for checkpointing and dead-letter tracking.
    /// </param>
    public static ProjectionDeclarationBuilder<TState> For<TState>(string processorId)
        where TState : new()
        => new(processorId);
}

/// <summary>
/// Fluent builder for <see cref="ProjectionDeclaration{TState}"/>.
/// Each event type is registered with <see cref="On{TEvent}"/> which binds the document ID
/// extraction and state evolution together — no disconnected switch expressions.
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
public sealed class ProjectionDeclarationBuilder<TState> where TState : new()
{
    private readonly string _processorId;
    private string _collectionName;
    private readonly HashSet<string> _eventTypes = new();
    private Func<TState> _initialState = static () => new TState();
    private readonly Dictionary<string, IProjectionEventHandler<TState>> _handlers = new();

    internal ProjectionDeclarationBuilder(string processorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        _processorId = processorId;
        _collectionName = processorId;
    }

    /// <summary>
    /// Overrides the storage collection name (defaults to the processor ID).
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Collection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _collectionName = name;
        return this;
    }

    /// <summary>
    /// Overrides the factory used to produce the initial state for a new document.
    /// The default factory calls <c>new TState()</c>.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> InitialState(Func<TState> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _initialState = factory;
        return this;
    }

    /// <summary>
    /// Registers a typed handler for <typeparamref name="TEvent"/>.
    /// The event is deserialized once and passed as a strongly-typed object to both
    /// <paramref name="id"/> and <paramref name="apply"/> — no string matching, no manual parsing.
    /// </summary>
    /// <typeparam name="TEvent">The event type, must have an <c>[EventType]</c> attribute.</typeparam>
    /// <param name="id">
    /// Extracts the document ID from the parsed event.
    /// Return <see langword="null"/> to skip the event without processing it.
    /// </param>
    /// <param name="apply">
    /// Applies the event to the current state and returns a <see cref="ProjectionResult{TState}"/>.
    /// Return the state directly (implicit Set), <see cref="ProjectionResults.Delete{TState}"/>,
    /// or <see cref="ProjectionResults.Unchanged{TState}"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the same event type is registered more than once.
    /// </exception>
    public ProjectionDeclarationBuilder<TState> On<TEvent>(
        Func<TEvent, string?> id,
        Func<TState, TEvent, ProjectionContext, ProjectionResult<TState>> apply)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(apply);

        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));

        if (!_eventTypes.Add(eventTypeId))
            throw new InvalidOperationException(
                $"Projection '{_processorId}': event type '{eventTypeId}' ({typeof(TEvent).Name}) " +
                $"is already registered. Each event type can only have one handler.");

        _handlers[eventTypeId] = new ProjectionEventHandler<TState, TEvent>(id, apply);
        return this;
    }

    /// <summary>
    /// Builds the <see cref="ProjectionDeclaration{TState}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no event handlers were registered via <see cref="On{TEvent}"/>.
    /// </exception>
    public ProjectionDeclaration<TState> Build()
    {
        if (_handlers.Count == 0)
            throw new InvalidOperationException(
                $"Projection '{_processorId}': no event handlers registered. " +
                $"Call .On<TEvent>(...) at least once before .Build().");

        return new ProjectionDeclaration<TState>(
            processorId: _processorId,
            collectionName: _collectionName,
            handledEventTypes: _eventTypes,
            initialState: _initialState,
            handlers: new Dictionary<string, IProjectionEventHandler<TState>>(_handlers));
    }
}
