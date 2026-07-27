using System.Collections.Immutable;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Captures a failed event as a complete dead-letter entry. Both single-event and batch
/// dispatch cross this seam so tenant identity and retry context cannot drift between them.
/// </summary>
internal static class DeadLetterEntryFactory
{
    public static DeadLetterEntry Create(
        string processorId,
        IEventEnvelope envelope,
        Exception error,
        int attemptCount,
        DateTimeOffset failedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProcessorId = processorId,
            EventId = envelope.Id,
            EventType = envelope.EventType.Id,
            EventData = envelope.EventData,
            ErrorMessage = error.Message,
            StackTrace = error.StackTrace,
            AttemptCount = attemptCount,
            FailedAt = failedAt,
            GlobalPosition = envelope.GlobalPosition,
            TenantId = envelope.TenantId,
            Tags = envelope.Tags.Select(tag => tag.Value).ToImmutableArray(),
            Metadata = envelope.Metadata.ToImmutableDictionary(StringComparer.Ordinal),
            CreatedAt = envelope.CreatedAt.UtcDateTime,
        };
}
