namespace Alberto.Subscriptions;

/// <summary>
/// A reactor that delegates event handling to a function.
/// Used by the <c>ReactTo(...)</c> helpers for declarative side-effect registration.
/// </summary>
internal sealed class FunctionalReactor<TEvent>(
    string processorId,
    Func<TEvent, ReactorContext, CancellationToken, Task> handler,
    int maxConcurrency = 1,
    EventSerializer? serializer = null) : IBatchableProcessor, IProcessorLifecycle
    where TEvent : class, IEvent
{
    public string ProcessorId { get; } = processorId;

    public bool IsActive { get; set; } = true;

    public bool IsRebuilding { get; set; }

    public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypeAttribute.GetEventTypeId(typeof(TEvent))
    };

    public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken cancellationToken = default)
    {
        if (!IsActive || !HandledEventTypes.Contains(@event.EventType.Id))
            return Task.CompletedTask;

        return ProcessAsync(@event, cancellationToken);
    }

    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken cancellationToken = default)
    {
        if (!IsActive)
            return;

        if (maxConcurrency <= 1)
        {
            foreach (var @event in events)
                await ProcessAsync(@event, cancellationToken);
            return;
        }

        await Parallel.ForEachAsync(
            events,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (@event, token) => await ProcessAsync(@event, token));
    }

    private Task ProcessAsync(IEventEnvelope @event, CancellationToken cancellationToken)
    {
        var payload = EventEnvelopeExtensions.DeserializeEvent<TEvent>(@event, serializer);
        return handler(payload, ReactorContext.FromEnvelope(@event), cancellationToken);
    }
}
