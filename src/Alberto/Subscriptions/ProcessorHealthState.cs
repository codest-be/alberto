using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Alberto.Subscriptions;

/// <summary>What is currently known about one processor's control loop.</summary>
/// <param name="ProcessorId">The processor.</param>
/// <param name="IsFaulted">Whether the loop stopped on an unrecoverable error.</param>
/// <param name="Lag">Events between the loop's checkpoint and the store head at the last report.</param>
/// <param name="LastHeartbeatAt">When the loop last completed a poll cycle (or faulted).</param>
/// <param name="HasReported">
/// False until the loop's first report. A registered processor whose loop never started —
/// a standby replica that did not win the lease, or a host that has not reached its first
/// poll — has a snapshot with no data, which the health check treats as no news.
/// </param>
[Experimental("ALB9002")]
public sealed record ProcessorSnapshot(
    string ProcessorId,
    bool IsFaulted,
    long Lag,
    DateTimeOffset LastHeartbeatAt,
    bool HasReported)
{
    internal static ProcessorSnapshot NotYetReported(string processorId) =>
        new(processorId, IsFaulted: false, Lag: 0, DateTimeOffset.MinValue, HasReported: false);
}

/// <summary>
/// Records the liveness of a module's control loops.
/// </summary>
/// <remarks>
/// This is observation, not admission control — the direct parallel of
/// <see cref="Alberto.Tenancy.ShardHealth"/>. Each <see cref="ControlLoop"/> writes a snapshot
/// after every poll cycle and when it faults; <see cref="ProcessorHealthCheck"/> reads them.
/// Shadow rebuild loops do not report: their processor ids are transient
/// (<c>{processorId}::rebuild::{version}</c>) and a rebuild's progress is an operator concern
/// served by <c>alberto ops rebuild status</c>, not a liveness signal for the host.
/// </remarks>
[Experimental("ALB9002")]
public sealed class ProcessorHealthState
{
    private readonly ConcurrentDictionary<string, ProcessorSnapshot> _snapshots =
        new(StringComparer.Ordinal);

    internal void Report(string processorId, bool isFaulted, long lag, DateTimeOffset at) =>
        _snapshots[processorId] = new ProcessorSnapshot(processorId, isFaulted, lag, at, HasReported: true);

    /// <summary>
    /// The last reported snapshot for <paramref name="processorId"/>, or a
    /// <see cref="ProcessorSnapshot.HasReported"/> = false placeholder when the loop has
    /// never reported.
    /// </summary>
    public ProcessorSnapshot Get(string processorId) =>
        _snapshots.TryGetValue(processorId, out var snapshot)
            ? snapshot
            : ProcessorSnapshot.NotYetReported(processorId);

    /// <summary>Every processor that has ever reported.</summary>
    public IReadOnlyCollection<string> ProcessorIds => [.. _snapshots.Keys];
}
