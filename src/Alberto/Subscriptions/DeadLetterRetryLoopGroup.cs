using Microsoft.Extensions.Hosting;

namespace Alberto.Subscriptions;

/// <summary>Fan-out IHostedService wrapper for multiple DeadLetterRetryLoops.</summary>
internal sealed class DeadLetterRetryLoopGroup(IReadOnlyList<DeadLetterRetryLoop> loops)
    : IHostedService, IAsyncDisposable
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var loop in loops)
            await loop.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(loops.Select(l => l.StopAsync(cancellationToken)));

    public ValueTask DisposeAsync() =>
        new(Task.WhenAll(loops.Select(l => l.DisposeAsync().AsTask())));
}
