namespace Alberto.Dcb.Tests.Testing;

/// <summary>
/// Test helper that collects projected events and supports waiting for specific projections.
/// Wire to PollingConsumer.OnProjected to use.
/// </summary>
public sealed class EventCollector
{
    private readonly List<(string ProcessorId, IEventEnvelope Envelope)> _projected = new();
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>
    /// Call this from OnProjected callback.
    /// </summary>
    public void OnProjected(string processorId, IEventEnvelope envelope)
    {
        lock (_projected)
        {
            _projected.Add((processorId, envelope));
        }
        _signal.Release();
    }

    /// <summary>
    /// Wait until a projected event matches the predicate, or timeout.
    /// </summary>
    public async Task<IEventEnvelope> WaitForProjectedAsync(
        Func<string, IEventEnvelope, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_projected)
            {
                var match = _projected.FirstOrDefault(p => predicate(p.ProcessorId, p.Envelope));
                if (match.Envelope is not null) return match.Envelope;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            try { await _signal.WaitAsync(remaining, ct); }
            catch (OperationCanceledException) { break; }
        }

        throw new TimeoutException("Timed out waiting for projected event.");
    }

    /// <summary>
    /// Wait until a specific processor has processed an event of a given type.
    /// </summary>
    public Task<IEventEnvelope> WaitForProjectedAsync(
        string processorId, string eventType,
        TimeSpan? timeout = null, CancellationToken ct = default)
        => WaitForProjectedAsync(
            (pid, env) => pid == processorId && env.EventType.Id == eventType,
            timeout, ct);

    /// <summary>
    /// All projected events collected so far.
    /// </summary>
    public IReadOnlyList<(string ProcessorId, IEventEnvelope Envelope)> Projected
    {
        get { lock (_projected) { return _projected.ToList(); } }
    }
}
