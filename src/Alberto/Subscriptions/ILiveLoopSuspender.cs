namespace Alberto.Subscriptions;

/// <summary>
/// Stops one processor's live control loop for the duration of a rebuild promotion.
/// </summary>
/// <remarks>
/// <para>
/// A promotion has two writers to reason about, not one. The shadow loop is stopped directly by
/// <c>RebuildCoordinator</c>, which owns it; the live loop is owned by the hosted control-loop
/// group and is reached through this seam.
/// </para>
/// <para>
/// It has to be stopped because the promotion is not a single instant. Between reading the two
/// checkpoints and refreshing the version cache the coordinator awaits three times, and a live
/// loop left running consumes events throughout — into the version the promotion is about to
/// discard, advancing its own checkpoint past the position the rebuilt version reached. The
/// handoff that follows is monotonic, so it cannot pull that checkpoint back, and those events
/// are never applied to the version that now serves reads.
/// </para>
/// </remarks>
internal interface ILiveLoopSuspender
{
    /// <summary>
    /// Stops the live loop for <paramref name="processorId"/> and returns a handle that restarts
    /// it on disposal. Disposing is not optional: until it happens the processor is not
    /// consuming.
    /// </summary>
    /// <remarks>
    /// A processor with no live loop on this replica — unknown here, or held by another replica
    /// under a lease — yields a handle that does nothing, since there is no local writer to race.
    /// </remarks>
    ValueTask<IAsyncDisposable> SuspendAsync(string processorId, CancellationToken ct);
}

/// <summary>
/// A suspender for a coordinator constructed without live loops to park — tests that drive a
/// rebuild with no control loop running behind it.
/// </summary>
internal sealed class NoLiveLoops : ILiveLoopSuspender
{
    public static readonly NoLiveLoops Instance = new();

    private NoLiveLoops() { }

    public ValueTask<IAsyncDisposable> SuspendAsync(string processorId, CancellationToken ct) =>
        new((IAsyncDisposable)NullLoopSuspension.Instance);
}

/// <summary>A suspension that had nothing to suspend.</summary>
internal sealed class NullLoopSuspension : IAsyncDisposable
{
    public static readonly NullLoopSuspension Instance = new();

    private NullLoopSuspension() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
