using Alberto.Diagnostics;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Messaging;

/// <summary>
/// An <see cref="IEventProcessor"/> that writes mapped events into the outbox store.
/// Register this processor with the consumer to capture events for reliable delivery.
/// </summary>
internal sealed class OutboxHandler(
    IMessageMappingRegistry registry,
    IOutboxStore store,
    IServiceScopeFactory scopeFactory) : IEventProcessor, IBatchableProcessor, IProcessorLifecycle
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
        var message = await MapAsync(envelope, ct);
        if (message is null) return;
        await store.InsertAsync(BuildEntry(message, envelope), ct);
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
            events.Select(e => MapAsync(e, ct)));

        await Task.WhenAll(
            messages
                .Select((m, i) => (Message: m, Event: events[i]))
                .Where(x => x.Message is not null)
                .Select(x => store.InsertAsync(BuildEntry(x.Message!, x.Event), ct)));
    }

    private async Task<ExternalMessage?> MapAsync(IEventEnvelope envelope, CancellationToken ct)
    {
        await using var scope = EventProcessingScope.Create(scopeFactory, envelope.TenantId);
        return await registry.TryMapAsync(envelope, scope.ServiceProvider, ct);
    }

    private static OutboxEntry BuildEntry(
        ExternalMessage message,
        IEventEnvelope source) =>
        new(
            Id: Guid.NewGuid(),
            SourceEventId: source.Id,
            MessageType: message.MessageType,
            Version: message.Version,
            Payload: message.Payload,
            Metadata: WithTraceContext(message.Metadata, source),
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            TenantId: source.TenantId,
            Destination: message.Destination,
            RoutingHint: message.RoutingHint);

    // The relay's span continues the trace of the append that produced the event.
    private static Dictionary<string, string> WithTraceContext(
        Dictionary<string, string> metadata, IEventEnvelope source)
    {
        var copy = new Dictionary<string, string>(metadata);
        TraceContextPropagation.CarryOver(source.Metadata, copy);
        return copy;
    }
}
