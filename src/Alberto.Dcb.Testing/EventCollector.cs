using System.Diagnostics.CodeAnalysis;

namespace Alberto.Dcb.Testing;

/// <summary>
/// Test helper that collects projected events and supports waiting for specific projections.
/// Wire it by registering a <c>ConsumeMiddleware</c> via <c>AddConsumeMiddleware</c> that calls
/// <see cref="OnProjected"/> after the downstream handler runs.
/// </summary>
/// <remarks>
/// <para>
/// This class is thread-safe. <see cref="OnProjected"/> may be called from any thread;
/// <see cref="WaitForProjectedAsync(Func{string,IEventEnvelope,bool},TimeSpan?,CancellationToken)"/>
/// may be called concurrently with it.
/// </para>
/// <para>
/// Dispose the collector when the test finishes to release the underlying semaphore.
/// </para>
/// </remarks>
public sealed class EventCollector : IDisposable
{
    private readonly List<(string ProcessorId, IEventEnvelope Envelope)> _projected = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a collector.
    /// </summary>
    /// <param name="timeProvider">
    /// Clock used for the wait deadline. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake to test timeout behaviour without spending real time.
    /// </param>
    public EventCollector(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Call this from the <c>OnProjected</c> callback to record a projected event.
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
    /// Waits until a projected event matches the predicate, or throws <see cref="TimeoutException"/>.
    /// </summary>
    /// <param name="predicate">Called for each collected event. Return <see langword="true"/> to match.</param>
    /// <param name="timeout">How long to wait. Defaults to five seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">No matching event arrived within <paramref name="timeout"/>.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by their leading parameters — a predicate against " +
                        "a processor id and event type — not by the optional tail. A string never " +
                        "converts to the predicate delegate, so no call can bind to both.")]
    public async Task<IEventEnvelope> WaitForProjectedAsync(
        Func<string, IEventEnvelope, bool> predicate,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = _timeProvider.GetUtcNow() + (timeout ?? TimeSpan.FromSeconds(5));

        while (_timeProvider.GetUtcNow() < deadline)
        {
            lock (_projected)
            {
                var match = _projected.FirstOrDefault(p => predicate(p.ProcessorId, p.Envelope));
                if (match.Envelope is not null) return match.Envelope;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;

            // Bounded by the poll interval rather than by `remaining`, so a fake clock that
            // jumps past the deadline is noticed instead of being slept through.
            try { await _signal.WaitAsync(TimeSpan.FromMilliseconds(25), ct); }
            catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); break; }
        }

        throw new TimeoutException("Timed out waiting for projected event.");
    }

    /// <summary>
    /// Waits until a specific processor has processed an event of a given type.
    /// </summary>
    /// <param name="processorId">The processor ID to match.</param>
    /// <param name="eventType">The event type ID to match (e.g. <c>"order-placed"</c>).</param>
    /// <param name="timeout">How long to wait. Defaults to five seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">No matching event arrived within <paramref name="timeout"/>.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by their leading parameters — a predicate against " +
                        "a processor id and event type — not by the optional tail. A string never " +
                        "converts to the predicate delegate, so no call can bind to both.")]
    public Task<IEventEnvelope> WaitForProjectedAsync(
        string processorId, string eventType,
        TimeSpan? timeout = null, CancellationToken ct = default)
        => WaitForProjectedAsync(
            (pid, env) => pid == processorId && env.EventType.Id == eventType,
            timeout, ct);

    /// <summary>
    /// All projected events collected so far, in arrival order.
    /// </summary>
    public IReadOnlyList<(string ProcessorId, IEventEnvelope Envelope)> Projected
    {
        get { lock (_projected) { return _projected.ToList(); } }
    }

    /// <summary>Releases the underlying semaphore.</summary>
    public void Dispose() => _signal.Dispose();
}
