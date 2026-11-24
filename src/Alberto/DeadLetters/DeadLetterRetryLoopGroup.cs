using Microsoft.Extensions.Hosting;

namespace Alberto.DeadLetters;

/// <summary>Fan-out IHostedService wrapper for multiple DeadLetterRetryLoops and their shared gate.</summary>
internal sealed class DeadLetterRetryLoopGroup(
    IReadOnlyList<DeadLetterRetryLoop> loops,
    DeadLetterDueGate? gate = null)
    : IHostedService, IAsyncDisposable
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var loop in loops)
            await loop.StartAsync(cancellationToken);
        gate?.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(loops.Select(l => l.StopAsync(cancellationToken)));
        if (gate is not null)
            await gate.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(loops.Select(l => l.DisposeAsync().AsTask()));
        if (gate is not null)
            await gate.DisposeAsync();
    }
}
