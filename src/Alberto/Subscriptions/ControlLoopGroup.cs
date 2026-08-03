using Microsoft.Extensions.Hosting;

namespace Alberto.Subscriptions;

/// <summary>Fan-out IHostedService wrapper for multiple ControlLoops.</summary>
internal sealed class ControlLoopGroup(IReadOnlyList<ControlLoop> loops)
    : IHostedService, IAsyncDisposable, ILiveLoopSuspender
{
    private readonly Dictionary<string, ControlLoop> _byProcessorId =
        loops.ToDictionary(l => l.ProcessorId, StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var loop in loops)
            await loop.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(loops.Select(l => l.StopAsync(cancellationToken)));

    public ValueTask DisposeAsync() =>
        new(Task.WhenAll(loops.Select(l => l.DisposeAsync().AsTask())));

    public async ValueTask<IAsyncDisposable> SuspendAsync(string processorId, CancellationToken ct)
    {
        if (!_byProcessorId.TryGetValue(processorId, out var loop))
            return NullLoopSuspension.Instance;

        await loop.StopAsync(ct);
        return new Suspension(loop);
    }

    private sealed class Suspension(ControlLoop loop) : IAsyncDisposable
    {
        /// <remarks>
        /// Restarted under <see cref="CancellationToken.None"/> rather than the token the
        /// promotion ran under. That token is cancelled at shutdown, and a loop relinked to it
        /// would be born already cancelled — the group's own <see cref="StopAsync"/> is what
        /// ends this loop's life, exactly as it did before the suspension.
        /// </remarks>
        public ValueTask DisposeAsync() => new(loop.StartAsync(CancellationToken.None));
    }
}
