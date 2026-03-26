using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb.Messaging;

/// <summary>
/// Background service that polls the outbox store for pending entries and delivers them
/// via the configured <see cref="IMessageTransport"/>.
/// </summary>
public sealed class OutboxRelay : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IMessageTransport _transport;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;

    /// <summary>
    /// Creates a new <see cref="OutboxRelay"/>.
    /// </summary>
    /// <param name="store">The outbox store to poll.</param>
    /// <param name="transport">The transport to deliver messages through.</param>
    /// <param name="pollingInterval">How often to poll when the outbox is empty. Defaults to 1 second.</param>
    /// <param name="batchSize">Maximum number of entries to process per poll cycle. Defaults to 100.</param>
    public OutboxRelay(
        IOutboxStore store,
        IMessageTransport transport,
        TimeSpan? pollingInterval = null,
        int batchSize = 100)
    {
        _store = store;
        _transport = transport;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(1);
        _batchSize = batchSize;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _transport.StartAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pending = await _store.GetPendingAsync(_batchSize, ct);

                foreach (var entry in pending)
                {
                    try
                    {
                        await _transport.PublishAsync(
                            new ExternalMessage(entry.MessageType, entry.Version, entry.Payload, entry.Metadata),
                            ct);
                        await _store.MarkDeliveredAsync(entry.Id, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        await _store.MarkFailedAsync(entry.Id, ex.Message, ct);
                    }
                }

                // Back off when there are no more pending entries
                if (pending.Count < _batchSize)
                    await Task.Delay(_pollingInterval, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { await Task.Delay(_pollingInterval, ct); }
        }
    }
}
