using System.Collections.Concurrent;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// In-memory transport implementation, useful for testing and development.
/// Collects all published messages in an in-process bag.
/// </summary>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly ConcurrentBag<ExternalMessage> _published = new();

    /// <summary>All messages that have been published since this transport was created.</summary>
    public IReadOnlyCollection<ExternalMessage> Published => _published;

    /// <inheritdoc/>
    public Task PublishAsync(ExternalMessage message, CancellationToken ct)
    {
        _published.Add(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
