using Alberto.Dcb.Subscriptions;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// An <see cref="IEventProcessor"/> that writes mapped events into the outbox store.
/// Register this processor with the consumer to capture events for reliable delivery.
/// </summary>
internal sealed class OutboxHandler(
    IMessageMappingRegistry registry,
    IOutboxStore store,
    IServiceProvider serviceProvider) : IEventProcessor
{
    /// <inheritdoc/>
    public string ProcessorId => "outbox";

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

        var entry = new OutboxEntry(
            Id: Guid.NewGuid(),
            SourceEventId: envelope.Id,
            MessageType: message.MessageType,
            Version: message.Version,
            Payload: message.Payload,
            Metadata: message.Metadata,
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null);

        await store.InsertAsync(entry, ct);
    }
}
