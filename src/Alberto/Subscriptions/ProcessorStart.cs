namespace Alberto.Subscriptions;

/// <summary>
/// Where a processor that has no checkpoint yet starts reading the log. Only consulted when no
/// checkpoint exists, so it is safe to leave in place after the first run.
/// </summary>
public sealed class ProcessorStart
{
    private ProcessorStart() { }

    /// <summary>Replays the whole log. The default, and what a projection needs.</summary>
    public static ProcessorStart Beginning { get; } = new();

    /// <summary>
    /// Skips the history: the first replica that takes the lease records the current stable head
    /// as the checkpoint, and the processor handles only what is appended after it.
    /// </summary>
    public static ProcessorStart Head { get; } = new() { AtHead = true };

    /// <summary>
    /// Continues where other processors stopped: a rename names the old id, a merge names every
    /// id it replaces. The lowest of their checkpoints wins, so none of them leaves a gap; one
    /// that was ahead repeats its tail. An id with no checkpoint counts as position 0.
    /// </summary>
    public static ProcessorStart CheckpointOf(params string[] processorIds)
    {
        ArgumentNullException.ThrowIfNull(processorIds);
        if (processorIds.Length == 0)
            throw new ArgumentException("Name at least one processor id to take over from.", nameof(processorIds));
        foreach (var id in processorIds)
            ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(processorIds));

        return new() { TakesOverFrom = [.. processorIds] };
    }

    /// <summary>True for <see cref="Head"/>.</summary>
    public bool AtHead { get; private init; }

    /// <summary>The processor ids <see cref="CheckpointOf"/> takes over from; empty otherwise.</summary>
    public IReadOnlyList<string> TakesOverFrom { get; private init; } = [];

    internal bool IsBeginning => !AtHead && TakesOverFrom.Count == 0;
}
