using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// An <see cref="IEventProcessor"/> that writes mapped events into the outbox store.
/// Register this processor with the consumer to capture events for reliable delivery.
/// </summary>
internal sealed class OutboxHandler(
    IMessageMappingRegistry registry,
    IOutboxStore store,
    IServiceProvider serviceProvider) : IEventProcessor, IBatchableProcessor
{
    internal const string ProcessorIdValue = "outbox";

    /// <inheritdoc/>
    public string ProcessorId => ProcessorIdValue;

    /// <inheritdoc/>
    public bool IsActive { get; set; } = true;

    /// <inheritdoc/>
    public bool IsRebuilding { get; set; }

    /// <inheritdoc/>
    public IReadOnlySet<string> HandledEventTypes => registry.MappedEventTypes;

    /// <inheritdoc/>
    public async Task ProcessEventAsync(IEventEnvelope envelope, CancellationToken ct)
    {
        var message = await registry.TryMapAsync(envelope, serviceProvider, ct);
        if (message is null) return;
        await store.InsertAsync(BuildEntry(message, envelope.Id), ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Maps all events concurrently — mapper calls are independent I/O (e.g. DB enrichment
    /// queries) so there is no ordering constraint between them. Non-null results are then
    /// inserted concurrently; each insert targets a distinct row identified by its source
    /// event ID, so there are no write conflicts.
    /// </remarks>
    public async Task ProcessBatchAsync(IReadOnlyList<IEventEnvelope> events, CancellationToken ct)
    {
        var messages = await Task.WhenAll(
            events.Select(e => registry.TryMapAsync(e, serviceProvider, ct).AsTask()));

        await Task.WhenAll(
            messages
                .Select((m, i) => (Message: m, EventId: events[i].Id))
                .Where(x => x.Message is not null)
                .Select(x => store.InsertAsync(BuildEntry(x.Message!, x.EventId), ct)));
    }

    private static OutboxEntry BuildEntry(ExternalMessage message, Guid sourceEventId) =>
        new(
            Id: Guid.NewGuid(),
            SourceEventId: sourceEventId,
            MessageType: message.MessageType,
            Version: message.Version,
            Payload: message.Payload,
            Metadata: message.Metadata,
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null);
}
