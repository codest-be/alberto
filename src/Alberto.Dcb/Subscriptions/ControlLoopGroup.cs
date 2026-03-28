using Microsoft.Extensions.Hosting;

namespace Alberto.Dcb.Subscriptions;

/// <summary>Fan-out IHostedService wrapper for multiple ControlLoops.</summary>
internal sealed class ControlLoopGroup(IReadOnlyList<ControlLoop> loops)
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
