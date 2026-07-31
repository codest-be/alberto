namespace Alberto.Messaging;

/// <summary>
/// Abstracts the delivery of external messages to a message broker or bus.
/// </summary>
public interface IMessageTransport
{
    /// <summary>
    /// Publishes a single message to the underlying broker after <see cref="StartAsync"/> has
    /// completed successfully. Calls may be concurrent when one transport instance is shared
    /// by several outbox registrations.
    /// </summary>
    Task PublishAsync(ExternalMessage message, CancellationToken ct = default);

    /// <summary>
    /// Starts the transport before the relay claims any messages. Alberto makes one bounded
    /// <see cref="StopAsync"/> call even when this method partially initializes and then throws.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the transport once after the last relay using it has stopped. Implementations must
    /// tolerate cleanup after a partially successful <see cref="StartAsync"/> call.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}
