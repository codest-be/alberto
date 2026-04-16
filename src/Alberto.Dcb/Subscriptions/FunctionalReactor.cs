using System.Text.Json;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// A reactor that delegates event handling to a function.
/// Used by the <c>ReactTo(...)</c> helpers for declarative side-effect registration.
/// </summary>
public sealed class FunctionalReactor<TEvent>(
    string processorId,
    Func<TEvent, ReactorContext, CancellationToken, Task> handler) : IBatchableProcessor
    where TEvent : class, IEvent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ProcessorId { get; } = processorId;

    public bool IsActive { get; set; } = true;

    public bool IsRebuilding { get; set; }

    public IReadOnlySet<string> HandledEventTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EventTypeAttribute.GetEventTypeId(typeof(TEvent))
    };

    public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
    {
        if (!IsActive || !HandledEventTypes.Contains(@event.EventType.Id))
            return Task.CompletedTask;

        return ProcessAsync(@event, ct);
    }

    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
    {
        if (!IsActive)
            return;

        foreach (var @event in events)
            await ProcessAsync(@event, ct);
    }

    private Task ProcessAsync(IEventEnvelope @event, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TEvent>(@event.EventData, JsonOptions)
                      ?? throw new InvalidOperationException(
                          $"Failed to deserialize event '{@event.EventType.Id}' to '{typeof(TEvent).Name}'.");

        return handler(payload, ReactorContext.FromEnvelope(@event), ct);
    }
}
