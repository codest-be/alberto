using Alberto.Messaging;
using Rebus.Bus;

namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// Adapts a Rebus <see cref="IBus"/> to Alberto's <see cref="IMessageTransport"/> seam,
/// delivering outbox entries to a Rebus-backed message bus.
/// </summary>
/// <remarks>
/// <para>
/// Copy this class, together with <see cref="AlbertoOutboxPayload"/> and
/// <see cref="AlbertoOutboxSerializer"/>, into your own project to connect an Alberto
/// transactional outbox to a Rebus-backed message bus. No packaged
/// <c>Alberto.Messaging.Rebus</c> project ships with Alberto; the adapter is intentionally
/// delivered as a recipe rather than a binary dependency.
/// </para>
/// <para>
/// <strong>Lifecycle:</strong> <see cref="StartAsync"/> and <see cref="StopAsync"/> are
/// intentional no-ops. The host owns the Rebus <see cref="IBus"/> lifecycle: the bus was
/// started (via <c>AddRebus</c> / <c>IStartableBus.Start</c>) before Alberto's
/// <see cref="OutboxRelay"/> begins, and will be stopped after the relay stops. Alberto
/// calls <see cref="StartAsync"/> and <see cref="StopAsync"/> as part of the relay
/// lifecycle, but must not touch a transport that the host, not Alberto, constructed.
/// </para>
/// <para>
/// <strong>Serializer requirement:</strong> the <see cref="IBus"/> instance MUST be
/// configured with <see cref="AlbertoOutboxSerializer"/> as its serializer. That serializer
/// writes <see cref="ExternalMessage.Payload"/> (already-serialized JSON) as raw UTF-8
/// bytes, bypassing Rebus's default JSON encoding that would otherwise double-encode the
/// payload string.
/// </para>
/// <para>
/// <strong>Routing:</strong> when <see cref="ExternalMessage.Destination"/> is set,
/// <see cref="PublishAsync"/> sends directly to the named queue via
/// <c>bus.Advanced.Routing.Send</c>, bypassing Rebus's type-based router. When
/// <see cref="ExternalMessage.Destination"/> is <see langword="null"/>, it falls back to
/// <c>bus.Send</c>, which delegates to Rebus's configured type-based router. The null
/// fallback requires a routing mapping for <see cref="AlbertoOutboxPayload"/> in the bus
/// configuration:
/// <code>
/// Configure.With(activator)
///     .Routing(r => r.TypeBased().Map&lt;AlbertoOutboxPayload&gt;("target-queue"))
///     ...
/// </code>
/// Without the mapping Rebus throws <see cref="ArgumentException"/> at send time.
/// </para>
/// </remarks>
public sealed class RebusTransport : IMessageTransport
{
    private readonly IBus _bus;

    /// <summary>
    /// Initializes a new instance of <see cref="RebusTransport"/> wrapping the given bus.
    /// </summary>
    public RebusTransport(IBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Wraps the outbox entry in an <see cref="AlbertoOutboxPayload"/> and routes it:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     When <see cref="ExternalMessage.Destination"/> is non-null: sends directly to the
    ///     named queue via <c>bus.Advanced.Routing.Send</c>.
    ///   </description></item>
    ///   <item><description>
    ///     When <see cref="ExternalMessage.Destination"/> is <see langword="null"/>: falls
    ///     back to <c>bus.Send</c>, which uses Rebus's type-based router. A
    ///     <c>.Routing(r => r.TypeBased().Map&lt;AlbertoOutboxPayload&gt;("queue"))</c>
    ///     mapping must be present in the bus configuration.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <see cref="ExternalMessage.Metadata"/> entries are forwarded as optional Rebus
    /// headers so they survive the round trip and are available to the receiving handler.
    /// </para>
    /// </remarks>
    public async Task PublishAsync(ExternalMessage message, CancellationToken ct = default)
    {
        var payload = new AlbertoOutboxPayload(
            message.MessageType,
            message.Version,
            message.Payload);

        // Forward ExternalMessage.Metadata as optional Rebus headers so they survive
        // the round trip and are available to the receiving handler via IMessageContext.Headers.
        var optionalHeaders = message.Metadata.Count > 0
            ? new Dictionary<string, string>(message.Metadata)
            : null;

        if (message.Destination is not null)
        {
            // Explicit destination: bypass type-based routing and send directly to the named queue.
            // bus.Advanced.Routing.Send goes through the registered serializer pipeline, so
            // AlbertoOutboxSerializer intercepts AlbertoOutboxPayload and writes payload.Payload
            // bytes directly, with no re-encoding of the already-serialized JSON string.
            await _bus.Advanced.Routing.Send(message.Destination, payload, optionalHeaders);
        }
        else
        {
            // Null destination: use Rebus's type-based router (bus.Send).
            // Rebus looks up the destination for AlbertoOutboxPayload from the routing config.
            // If no mapping is configured, Rebus throws ArgumentException:
            //   "Cannot get destination for message of type AlbertoOutboxPayload because it has
            //    not been mapped! ... .Map<AlbertoOutboxPayload>(someEndpoint)"
            await _bus.Send(payload, optionalHeaders);
        }
    }

    /// <inheritdoc />
    /// <remarks>No-op. The host owns the Rebus IBus lifecycle; Alberto never starts a transport it did not construct.</remarks>
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>No-op. The host owns the Rebus IBus lifecycle; Alberto never stops a transport it did not construct.</remarks>
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
}
