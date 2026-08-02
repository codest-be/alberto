using Alberto.Messaging;
using Rebus.Bus;

namespace Alberto.Tests.Messaging.Rebus;

/// <summary>
/// Adapts a Rebus <see cref="IBus"/> to Alberto's <see cref="IMessageTransport"/> seam.
/// </summary>
/// <remarks>
/// <para>
/// Copy this into your own project. Alberto ships no broker binding, so the adapter is
/// delivered as a recipe rather than a package. Nothing here is specific to Rebus except the
/// two client calls; the same shape works against MassTransit, Wolverine or a client you wrote.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> Both lifecycle methods are deliberately empty. The host
/// constructed the bus and the container disposes it, so Alberto must not start or stop a
/// client it does not own.
/// </para>
/// <para>
/// <strong>Routing.</strong> A non-null <see cref="ExternalMessage.Destination"/> goes straight
/// to that queue. Null means "use the transport's default routing", which for Rebus is its
/// type-based router and needs a mapping in the bus configuration:
/// <c>.Routing(r =&gt; r.TypeBased().Map&lt;AlbertoMessage&gt;("target-queue"))</c>. Without one,
/// Rebus throws <see cref="ArgumentException"/> at send time. Note that the mapping is on the
/// envelope, which every Alberto message shares, so the fallback can only ever reach a single
/// queue. Set <see cref="ExternalMessage.Destination"/> when different messages need different
/// queues.
/// </para>
/// </remarks>
public sealed class RebusTransport(IBus bus) : IMessageTransport
{
    /// <inheritdoc />
    public Task PublishAsync(ExternalMessage message, CancellationToken ct = default)
    {
        var body = new AlbertoMessage(message.MessageType, message.Version, message.Payload);
        var headers = message.Metadata.Count > 0
            ? new Dictionary<string, string>(message.Metadata)
            : null;

        return message.Destination is not null
            ? bus.Advanced.Routing.Send(message.Destination, body, headers)
            : bus.Send(body, headers);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
}
