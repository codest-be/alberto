namespace Alberto.Dcb.Messaging;

/// <summary>
/// Represents an external message derived from a domain event, ready to be published to a transport.
/// </summary>
public record ExternalMessage(string MessageType, string Version, string Payload, Dictionary<string, string> Metadata);
