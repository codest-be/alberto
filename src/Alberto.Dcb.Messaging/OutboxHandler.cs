using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// An <see cref="IEventProcessor"/> that writes mapped events into the outbox store.
/// Register this processor with the consumer to capture events for reliable delivery.
/// </summary>
internal sealed class OutboxHandler(
    IMessageMappingRegistry registry,
    IOutboxStore store,
    IServiceScopeFactory scopeFactory) : IEventProcessor, IBatchableProcessor
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
        await store.InsertAsync(BuildEntry(message, envelope.Id, envelope.TenantId), ct);
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
                .Select((m, i) => (
                    Message: m,
                    EventId: events[i].Id,
                    TenantId: events[i].TenantId))
                .Where(x => x.Message is not null)
                .Select(x => store.InsertAsync(
                    BuildEntry(x.Message!, x.EventId, x.TenantId),
                    ct)));
    }

    private async Task<ExternalMessage?> MapAsync(IEventEnvelope envelope, CancellationToken ct)
    {
        await using var scope = EventProcessingScope.Create(scopeFactory, envelope.TenantId);
        return await registry.TryMapAsync(envelope, scope.ServiceProvider, ct);
    }

    private static OutboxEntry BuildEntry(
        ExternalMessage message,
        Guid sourceEventId,
        string? tenantId) =>
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
            DeliveredAt: null,
            TenantId: tenantId);
}
