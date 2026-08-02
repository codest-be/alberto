using System.Text;
using Rebus.Messages;
using Rebus.Serialization;

namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// Optional Rebus serializer that puts the outbox payload on the wire as the message body,
/// instead of as a string field inside a serialized <see cref="AlbertoMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>You do not need this to use <see cref="RebusTransport"/>.</strong> The adapter works
/// unchanged with Rebus's default serializer, which encodes the whole envelope and leaves
/// <see cref="AlbertoMessage.Payload"/> as a JSON string the consumer parses itself. That is a
/// perfectly ordinary arrangement.
/// </para>
/// <para>
/// Register this instead when you want the wire format decoupled from your .NET types. It buys
/// two things. The body becomes the raw domain JSON, so a consumer on another stack sees
/// <c>{"orderId":...}</c> rather than an envelope wrapping an escaped string. And
/// <c>rbs2-msg-type</c> carries a sentinel rather than a CLR type name, so the receiver needs
/// neither <see cref="AlbertoMessage"/> on its classpath nor a type-name mapping. Alberto's own
/// message type and version travel as headers, which is what a non-.NET consumer would dispatch
/// on anyway.
/// </para>
/// <para>
/// The cost is this file, and that both ends must agree on it. Register it on the bus with:
/// <code>
/// .Serialization(s => s.Register(_ => (ISerializer)new AlbertoOutboxSerializer()))
/// </code>
/// </para>
/// </remarks>
public sealed class AlbertoOutboxSerializer : ISerializer
{
    internal const string AlbertoMessageTypeHeader = "alberto-message-type";
    internal const string AlbertoVersionHeader = "alberto-version";

    // A sentinel, not a .NET type name. Deserialize recognizes it; nothing tries to load it.
    private const string AlbertoSentinel = "alberto-outbox";

    /// <inheritdoc />
    public Task<TransportMessage> Serialize(Message message)
    {
        if (message.Body is not AlbertoMessage payload)
            throw new NotSupportedException(
                $"{nameof(AlbertoOutboxSerializer)} only handles {nameof(AlbertoMessage)}; " +
                $"got: {message.Body?.GetType().FullName ?? "null"}.");

        var headers = new Dictionary<string, string>(message.Headers)
        {
            [Headers.ContentType] = "application/json;charset=utf-8",
            [Headers.Type] = AlbertoSentinel,
            [AlbertoMessageTypeHeader] = payload.MessageType,
            [AlbertoVersionHeader] = payload.Version,
        };

        // Exactly the bytes the outbox stored. No re-encoding.
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

        var body = new AlbertoMessage(msgType, version, rawPayload);
        return Task.FromResult(new Message(
            new Dictionary<string, string>(transportMessage.Headers),
            body));
    }
}
