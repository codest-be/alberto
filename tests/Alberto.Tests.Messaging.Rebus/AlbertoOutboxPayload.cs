namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// Envelope that carries a pre-serialized Alberto outbox payload through the Rebus
/// serialization pipeline without re-encoding it.
/// </summary>
/// <remarks>
/// <see cref="AlbertoOutboxSerializer"/> intercepts this type in its
/// <see cref="AlbertoOutboxSerializer.Serialize"/> method and writes
/// <see cref="Payload"/> as raw UTF-8 bytes, bypassing Rebus's default JSON
/// serializer that would otherwise double-encode the already-serialized JSON string.
/// On the receiver side, <see cref="AlbertoOutboxSerializer.Deserialize"/> reconstructs
/// an <see cref="AlbertoOutboxPayload"/> from the raw bytes and the alberto-* headers,
/// so handler dispatch is by the .NET type of the body as usual.
/// </remarks>
/// <param name="MessageType">Logical message type name (forwarded as <c>alberto-message-type</c> header).</param>
/// <param name="Version">Schema version (forwarded as <c>alberto-version</c> header).</param>
/// <param name="Payload">Already-serialized JSON body. Written to the transport as-is.</param>
public sealed record AlbertoOutboxPayload(string MessageType, string Version, string Payload);
