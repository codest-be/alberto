using System.Text;
using Rebus.Messages;
using Rebus.Serialization;

namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// Custom Rebus serializer that passes <see cref="AlbertoOutboxPayload"/> payloads through
/// the transport wire without re-encoding the already-serialized JSON body.
/// </summary>
/// <remarks>
/// <para>
/// Alberto's <see cref="Alberto.Messaging.ExternalMessage.Payload"/> is already-serialized
/// JSON. Rebus's default System.Text.Json serializer would encode that string as a JSON
/// string literal, wrapping it in extra quotes and escaping its internal quotes, which
/// is double-encoding. This serializer avoids that path: <see cref="Serialize"/> writes
/// <see cref="AlbertoOutboxPayload.Payload"/> as raw UTF-8 bytes and sets a custom
/// <c>rbs2-msg-type</c> sentinel instead of a .NET CLR type name. <see cref="Deserialize"/>
/// reads the bytes back and reconstructs an <see cref="AlbertoOutboxPayload"/> so that Rebus
/// dispatches by runtime type to any <c>IHandleMessages{AlbertoOutboxPayload}</c> handler.
/// </para>
/// <para>
/// Register this serializer when configuring the Rebus bus used as the transport:
/// <code>
/// Configure.With(activator)
///     .Serialization(s => s.Register(_ => (ISerializer)new AlbertoOutboxSerializer()))
///     ...
/// </code>
/// </para>
/// </remarks>
public sealed class AlbertoOutboxSerializer : ISerializer
{
    // Custom headers forwarded alongside the Rebus built-in headers.
    internal const string AlbertoMessageTypeHeader = "alberto-message-type";
    internal const string AlbertoVersionHeader = "alberto-version";

    // Sentinel placed in rbs2-msg-type so Deserialize can recognize Alberto-originated messages.
    // This is NOT a .NET CLR type name; Rebus never tries to load it as a CLR type when a
    // custom serializer owns the entire serialization/deserialization path.
    private const string AlbertoSentinel = "alberto-outbox";

    /// <inheritdoc />
    public Task<TransportMessage> Serialize(Message message)
    {
        if (message.Body is not AlbertoOutboxPayload payload)
            throw new NotSupportedException(
                $"{nameof(AlbertoOutboxSerializer)} only handles {nameof(AlbertoOutboxPayload)}; " +
                $"got: {message.Body?.GetType().FullName ?? "null"}.");

        var headers = new Dictionary<string, string>(message.Headers)
        {
            [Headers.ContentType] = "application/json;charset=utf-8",
            // Sentinel instead of a CLR type name: prevents Rebus from trying to load
            // "alberto-outbox" as a .NET type when this message is later deserialized.
            [Headers.Type] = AlbertoSentinel,
            [AlbertoMessageTypeHeader] = payload.MessageType,
            [AlbertoVersionHeader] = payload.Version,
        };

        // Write the already-serialized JSON as raw UTF-8 bytes.
        // The bytes are exactly what the outbox stored; no re-encoding occurs.
        var body = Encoding.UTF8.GetBytes(payload.Payload);
        return Task.FromResult(new TransportMessage(headers, body));
    }

    /// <inheritdoc />
    public Task<Message> Deserialize(TransportMessage transportMessage)
    {
        var rawPayload = Encoding.UTF8.GetString(transportMessage.Body);
        var msgType = transportMessage.Headers.GetValueOrDefault(AlbertoMessageTypeHeader)
            ?? string.Empty;
        var version = transportMessage.Headers.GetValueOrDefault(AlbertoVersionHeader)
            ?? string.Empty;

        var body = new AlbertoOutboxPayload(msgType, version, rawPayload);
        return Task.FromResult(new Message(
            new Dictionary<string, string>(transportMessage.Headers),
            body));
    }
}
