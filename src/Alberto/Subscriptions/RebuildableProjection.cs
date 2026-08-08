namespace Alberto.Subscriptions;

/// <summary>
/// Everything the rebuild coordinator needs to stand a projection up a second time, writing
/// into a different version of its own state.
/// </summary>
/// <remarks>
/// <para>
/// Registered by <c>AddProjection</c> and <c>AddEfProjection</c> alongside the live processor.
/// The coordinator calls <see cref="CreateProcessor"/> with a shadow version handle to get a
/// second, independent processor over the same declaration, then runs it from position 0.
/// </para>
/// <para>
/// The rebuild version is a handle rather than a value because a rebuild outlives its own
/// promotion by a few events: the shadow processor keeps running until the coordinator has
/// stopped it, and by then the version it should be writing to may have changed.
/// </para>
/// </remarks>
internal sealed class RebuildableProjection
{
    /// <summary>Creates a registration.</summary>
    /// <param name="processorId">
    /// The live processor's id — the checkpoint key, and what an operator names on the CLI.
    /// </param>
    /// <param name="projectionType">
    /// The key the projection's state rows are stored under. Equal to
    /// <paramref name="processorId"/> unless the projection was registered with an explicit
    /// projection type.
    /// </param>
    /// <param name="createProcessor">
    /// Builds a processor whose state store resolves its rebuild version through the supplied
    /// version handle.
    /// </param>
    public RebuildableProjection(
        string processorId,
        string projectionType,
        Func<ProjectionVersion, IEventProcessor> createProcessor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionType);
        ArgumentNullException.ThrowIfNull(createProcessor);

        ProcessorId = processorId;
        ProjectionType = projectionType;
        CreateProcessor = createProcessor;
    }

    /// <summary>The live processor's id.</summary>
    public string ProcessorId { get; }

    /// <summary>The key this projection's state rows are stored under.</summary>
    public string ProjectionType { get; }

    /// <summary>Builds a processor bound to the supplied version handle.</summary>
    public Func<ProjectionVersion, IEventProcessor> CreateProcessor { get; }

    /// <summary>
    /// The checkpoint key a shadow rebuild loop advances. Separate from the live processor's
    /// so that replaying from the start of history does not drag the live projection back
    /// with it.
    /// </summary>
    /// <remarks>
    /// The version is part of the key, which is what makes a replay's progress belong to the
    /// version it is filling rather than to the processor. A key per processor has to be reset
    /// between rebuilds, and the only moments a coordinator can do that are moments an operator
    /// can start the next rebuild ahead of: a rebuild begun before the previous one's reset
    /// resumes from a checkpoint already at the head of the log, replays nothing, and promotes
    /// an empty projection. Keying by version has no such window — a version nobody has replayed
    /// yet simply has no checkpoint, and one that is being resumed after a restart has exactly
    /// the checkpoint it left off at.
    /// </remarks>
    public static string ShadowProcessorId(string processorId, int rebuildVersion) =>
        $"{processorId}::rebuild::{rebuildVersion}";

    /// <summary>
    /// Returns true when <paramref name="processorId"/> was produced by
    /// <see cref="ShadowProcessorId"/> — that is, it names a shadow rebuild loop rather than
    /// a live processor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator's sweep is the authoritative cleaner for shadow checkpoint rows: after
    /// a version is discarded (by promotion or abort), <c>ClearAsync</c> calls
    /// <see cref="ICheckpointStore.ResetAsync"/> on the shadow key so that no row survives
    /// past its grace period.
    /// </para>
    /// <para>
    /// This predicate is the second line of defence. A row left behind by a crash between the
    /// flip and the sweep, by a host running an older version of Alberto that did not perform
    /// the reset, or by a shadow loop on a different replica that had not yet noticed the abort
    /// cannot brick the next startup because <see cref="OrphanCheckpointHostedService"/>
    /// filters these ids out before comparing against declared processors.
    /// </para>
    /// <para>
    /// Both halves exist because neither alone is sufficient on its own: the reset is
    /// authoritative but can be skipped by a crash; the filter is always reachable but does
    /// not reclaim the row, so a store that accumulates shadow rows indefinitely would grow
    /// unbounded without the reset.
    /// </para>
    /// </remarks>
    public static bool IsShadowProcessorId(string processorId) =>
        processorId.Contains("::rebuild::", StringComparison.Ordinal);
}

/// <summary>
/// Presents a processor under the shadow checkpoint key, so the same declaration can run twice
/// in one host without the two runs sharing a position.
/// </summary>
internal sealed class ShadowProcessor(IEventProcessor inner, string processorId)
    : IBatchableProcessor, IProcessorLifecycle, IAsyncDisposable
{
    public string ProcessorId { get; } = processorId;

    public bool IsActive
    {
        get => inner.IsActive;
        // Route the write through the inner processor's lifecycle interface so that nothing
        // needs to hold a concrete type. A processor that does not implement IProcessorLifecycle
        // simply cannot be deactivated from the outside — the set is silently ignored.
        set { if (inner is IProcessorLifecycle lc) lc.IsActive = value; }
    }

    /// <summary>
    /// Always true. A shadow loop is by definition catching up from behind, and processors use
    /// this to opt out of behaviour that only makes sense at the head of the log.
    /// </summary>
    /// <remarks>
    /// The setter is intentionally a no-op: a shadow processor's rebuild status is structural,
    /// not a flag that can be cleared from outside.
    /// </remarks>
    public bool IsRebuilding { get => true; set { } }

    public IReadOnlySet<string> HandledEventTypes => inner.HandledEventTypes;

    public Task ProcessEventAsync(IEventEnvelope @event, CancellationToken ct = default)
        => inner.ProcessEventAsync(@event, ct);

    /// <summary>
    /// A rebuild replays the whole log, so it takes the batch path whenever the projection
    /// offers one. Projections that do not are dispatched event by event — slower, but the
    /// alternative would be refusing to rebuild them.
    /// </summary>
    public async Task ProcessBatchAsync(
        IReadOnlyList<IEventEnvelope> events, CancellationToken ct = default)
    {
        if (inner is IBatchableProcessor batchable)
        {
            await batchable.ProcessBatchAsync(events, ct);
            return;
        }

        foreach (var e in events)
            await inner.ProcessEventAsync(e, ct);
    }

    public ValueTask DisposeAsync()
        => inner is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
}
