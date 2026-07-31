using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Alberto.Subscriptions;

/// <summary>
/// Waits for an async processor to catch up, so a read taken straight after a write can see it.
/// </summary>
/// <remarks>
/// <para>
/// Projections are eventually consistent: the control loop polls, so the state store still holds
/// the pre-write value for a moment after <c>Commit</c> returns. The usual workaround is to sleep
/// for a guess, which is either too short — flaky — or too long. This waits for the thing that
/// actually has to happen instead.
/// </para>
/// <para>
/// Resolve it keyed by module: <c>GetRequiredKeyedService&lt;ProjectionCatchUp&gt;("tickets")</c>.
/// </para>
/// <para>
/// It reads the processor's checkpoint, so it can only report progress the checkpoint reflects.
/// That is exact in the process running the control loop — the loop advances the checkpoint in
/// memory as it commits each batch. It is <em>not</em> exact anywhere else: a replica that does
/// not run the processor holds a cached checkpoint that no local loop advances, so a wait there
/// will run to its timeout even though the projection is up to date. Read your writes in the
/// process that does the writing, or use an inline projection.
/// </para>
/// </remarks>
public sealed class ProjectionCatchUp
{
    private readonly IEventStoreBackend _eventStore;
    private readonly ICheckpointStore _checkpoints;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _pollInterval;

    /// <param name="eventStore">Backend the head position is read from.</param>
    /// <param name="checkpoints">Checkpoint store the processors' progress is read from.</param>
    /// <param name="defaultTimeout">
    /// Timeout used when a caller does not give one. Defaults to five seconds.
    /// </param>
    /// <param name="pollInterval">
    /// How often the checkpoint is re-read while waiting. Defaults to 10 ms — small because the
    /// checkpoint store in front of a backend is an in-memory cache the loop writes through.
    /// </param>
    public ProjectionCatchUp(
        IEventStoreBackend eventStore,
        ICheckpointStore checkpoints,
        TimeSpan? defaultTimeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(checkpoints);

        _eventStore = eventStore;
        _checkpoints = checkpoints;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(5);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10);
    }

    /// <summary>
    /// Waits until <paramref name="processorId"/> has consumed everything the store held when
    /// this was called.
    /// </summary>
    /// <remarks>
    /// The head is read once, at the start. It includes whatever other writers had appended by
    /// then, so the wait can cover more than the caller's own events — never fewer, which is
    /// what read-your-writes needs. Re-reading it would be chasing a target that a busy store
    /// keeps moving.
    /// </remarks>
    /// <param name="processorId">The processor to wait for, e.g. a projection's declared id.</param>
    /// <param name="timeout">How long to wait. Defaults to the value given at construction.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">The processor did not catch up in time.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the required position parameter, not by the " +
                        "optional tail: a long never converts to TimeSpan?, so no call can bind to " +
                        "both. The two are genuinely different waits — one for the head as of now, " +
                        "one for a position the caller already holds.")]
    public async Task WaitForProjectionAsync(
        string processorId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        var head = await _eventStore.GetLastPositionAsync(cancellationToken).ConfigureAwait(false);
        await WaitForProjectionAsync(processorId, head, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until <paramref name="processorId"/> has consumed up to <paramref name="position"/>.
    /// </summary>
    /// <param name="processorId">The processor to wait for, e.g. a projection's declared id.</param>
    /// <param name="position">The global position to wait for. Zero returns immediately.</param>
    /// <param name="timeout">How long to wait. Defaults to the value given at construction.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">The processor did not reach the position in time.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the required position parameter, not by the " +
                        "optional tail: a long never converts to TimeSpan?, so no call can bind to " +
                        "both. The two are genuinely different waits — one for the head as of now, " +
                        "one for a position the caller already holds.")]
    public async Task WaitForProjectionAsync(
        string processorId,
        long position,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        var limit = timeout ?? _defaultTimeout;
        var startedAt = Stopwatch.GetTimestamp();
        long? reached;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A processor that has consumed nothing is at position 0, which is where an empty
            // store's head is too — so an empty store is caught up rather than unreachable.
            reached = await _checkpoints.GetAsync(processorId, cancellationToken).ConfigureAwait(false);
            if ((reached ?? 0) >= position)
                return;

            // Checked after the read, so a caller who is already caught up never pays a poll —
            // including with a zero timeout, which is what asking "am I there yet?" looks like.
            if (Stopwatch.GetElapsedTime(startedAt) >= limit)
                break;

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Projection '{processorId}' did not reach position {position} within {limit} " +
            $"(last observed: {reached?.ToString() ?? "nothing consumed"}). The control loop for " +
            "this processor may be stopped, faulted, or running in another process.");
    }
}
