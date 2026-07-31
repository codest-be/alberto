namespace Alberto.Messaging;

/// <summary>
/// Declares that this class is the public contract for an external message published via the transactional outbox.
/// </summary>
/// <example>
/// <code>
/// [Message("order-placed", 1)]
/// public record OrderPlacedMessage(Guid OrderId, decimal Amount);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessageAttribute(string messageType, int version) : Attribute
{
    /// <summary>The external message type identifier (e.g., "order-placed").</summary>
    public string MessageType { get; } = messageType;

    /// <summary>The schema version of this message contract.</summary>
    public int Version { get; } = version;
}
