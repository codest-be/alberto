namespace Alberto.Dcb.Messaging;

/// <summary>
/// Abstracts the delivery of external messages to a message broker or bus.
/// </summary>
public interface IMessageTransport
{
    /// <summary>Publishes a single message to the underlying broker.</summary>
    Task PublishAsync(ExternalMessage message, CancellationToken ct = default);

    /// <summary>Called once when the relay starts.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Called once when the relay stops.</summary>
    Task StopAsync(CancellationToken ct = default);
}
