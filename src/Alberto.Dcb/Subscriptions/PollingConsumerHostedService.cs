using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb.Subscriptions;

/// <summary>
/// Hosted service wrapper for <see cref="PollingConsumer"/>.
/// Enables running event consumers as background services in ASP.NET Core / Generic Host.
/// </summary>
public sealed class PollingConsumerHostedService : IHostedService, IAsyncDisposable
{
    private readonly PollingConsumer _consumer;

    public PollingConsumerHostedService(PollingConsumer consumer)
    {
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
        => _consumer.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
        => _consumer.StopAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => _consumer.DisposeAsync();
}
