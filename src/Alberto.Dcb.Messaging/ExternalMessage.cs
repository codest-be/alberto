namespace Alberto.Dcb.Messaging;

/// <summary>
/// Represents an external message derived from a domain event, ready to be published to a transport.
/// </summary>
/// <param name="MessageType">Logical message type name used by consumers to route and deserialize.</param>
/// <param name="Version">Schema version; allows consumers to handle multiple shapes of the same message type.</param>
/// <param name="Payload">Serialized message body.</param>
/// <param name="Metadata">Key-value pairs for transport headers, correlation ids, etc.</param>
/// <param name="Destination">
/// Optional transport address (exchange, topic, queue, …) this message should be routed to.
/// Null means "use the transport's default routing for this message type".
/// </param>
/// <param name="RoutingHint">
/// Optional hint for the transport layer — e.g. a partition key, a routing key, or a
/// message-group id. Interpretation is transport-specific and ignored when null.
/// </param>
public record ExternalMessage(
    string MessageType,
    string Version,
    string Payload,
    Dictionary<string, string> Metadata,
    string? Destination = null,
    string? RoutingHint = null);
