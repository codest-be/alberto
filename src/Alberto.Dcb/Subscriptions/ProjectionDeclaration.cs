namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Compiled projection definition produced by <see cref="ProjectionDeclarationBuilder{TState}"/>.
/// All dispatch logic is stored as delegates — no reflection at runtime.
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
public sealed class ProjectionDeclaration<TState> where TState : new()
{
    internal ProjectionDeclaration(
        string processorId,
        string collectionName,
        IReadOnlySet<string> handledEventTypes,
        Func<TState> initialState,
        Func<IEventEnvelope, string?> getDocumentIdFunc,
        Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>> evolveFunc)
    {
        ProcessorId = processorId;
        CollectionName = collectionName;
        HandledEventTypes = handledEventTypes;
        InitialState = initialState;
        GetDocumentIdFunc = getDocumentIdFunc;
        EvolveFunc = evolveFunc;
    }

    /// <summary>Processor ID used for checkpointing.</summary>
    public string ProcessorId { get; }

    /// <summary>Storage collection / table name.</summary>
    public string CollectionName { get; }

    /// <summary>The set of event type IDs this projection handles.</summary>
    public IReadOnlySet<string> HandledEventTypes { get; }

    /// <summary>Factory that produces the initial (empty) state for a new document.</summary>
    public Func<TState> InitialState { get; }

    /// <summary>
    /// Returns the document ID for an event envelope, or <see langword="null"/> to skip the event.
    /// </summary>
    internal Func<IEventEnvelope, string?> GetDocumentIdFunc { get; }

    /// <summary>
    /// Applies an event to a state and returns the projection result.
    /// </summary>
    internal Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>> EvolveFunc { get; }
}

/// <summary>
/// Factory entry point for the declaration-based projection API.
/// Avoids reflection and base classes entirely.
/// </summary>
/// <example>
/// <code>
/// var projection = DeclareProjection.For&lt;OrderSummary&gt;("order-summary")
///     .Handles&lt;OrderCreated&gt;()
///     .Handles&lt;OrderConfirmed&gt;()
///     .DocumentId(e =&gt; e.ParseEvent&lt;OrderCreated&gt;().OrderId.ToString())
///     .Evolve((state, e, ctx) =&gt; e.EventType.Id switch
///     {
///         "order-created" =&gt; new OrderSummary { OrderId = e.ParseEvent&lt;OrderCreated&gt;().OrderId },
///         _               =&gt; state
///     })
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
/// </summary>
/// <typeparam name="TState">The projection state type.</typeparam>
public sealed class ProjectionDeclarationBuilder<TState> where TState : new()
{
    private readonly string _processorId;
    private string _collectionName;
    private readonly HashSet<string> _eventTypes = new();
    private Func<TState> _initialState = static () => new TState();
    private Func<IEventEnvelope, string?>? _getDocumentId;
    private Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>>? _evolve;

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
    /// Declares that this projection handles the given raw event type IDs.
    /// Use this when you do not have a CLR event class available.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Handles(params string[] eventTypes)
    {
        foreach (var t in eventTypes) _eventTypes.Add(t);
        return this;
    }

    /// <summary>
    /// Declares that this projection handles the event type identified by
    /// <typeparamref name="TEvent"/>'s <c>[EventType]</c> attribute.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Handles<TEvent>() where TEvent : IEvent
    {
        _eventTypes.Add(EventTypeAttribute.GetEventTypeId(typeof(TEvent)));
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
    /// Sets the delegate that derives a document ID from an event envelope.
    /// Return <see langword="null"/> to skip the event without processing it.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> DocumentId(Func<IEventEnvelope, string?> getDocumentId)
    {
        ArgumentNullException.ThrowIfNull(getDocumentId);
        _getDocumentId = getDocumentId;
        return this;
    }

    /// <summary>
    /// Sets the evolve function that applies an event to the current state.
    /// </summary>
    public ProjectionDeclarationBuilder<TState> Evolve(
        Func<TState, IEventEnvelope, ProjectionContext, ProjectionResult<TState>> evolve)
    {
        ArgumentNullException.ThrowIfNull(evolve);
        _evolve = evolve;
        return this;
    }

    /// <summary>
    /// Builds the <see cref="ProjectionDeclaration{TState}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="DocumentId"/> or <see cref="Evolve"/> were not configured,
    /// or if no event types were declared.
    /// </exception>
    public ProjectionDeclaration<TState> Build()
    {
        if (_getDocumentId is null)
            throw new InvalidOperationException(
                $"Projection '{_processorId}': DocumentId delegate not set. Call .DocumentId(...) before .Build().");

        if (_evolve is null)
            throw new InvalidOperationException(
                $"Projection '{_processorId}': Evolve delegate not set. Call .Evolve(...) before .Build().");

        if (_eventTypes.Count == 0)
            throw new InvalidOperationException(
                $"Projection '{_processorId}': No event types declared. Call .Handles<TEvent>() at least once before .Build().");

        return new ProjectionDeclaration<TState>(
            processorId: _processorId,
            collectionName: _collectionName,
            handledEventTypes: _eventTypes,
            initialState: _initialState,
            getDocumentIdFunc: _getDocumentId,
            evolveFunc: _evolve);
    }
}
