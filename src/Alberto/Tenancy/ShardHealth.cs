using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Alberto.Tenancy;

/// <summary>What is currently known about one shard's reachability.</summary>
/// <param name="ShardId">The shard.</param>
/// <param name="Healthy">Whether the last attempt to reach it succeeded.</param>
/// <param name="LastError">The message from the last failure, or null while healthy.</param>
/// <param name="LastChangedAt">When the state last flipped.</param>
[Experimental("ALB9001")]
public sealed record ShardState(string ShardId, bool Healthy, string? LastError, DateTimeOffset LastChangedAt);

/// <summary>
/// Records which shards of a module are reachable.
/// </summary>
/// <remarks>
/// This is observation, not admission control. Nothing on the request path consults it before
/// trying a shard: a stale "unhealthy" would fail requests a recovered database could have
/// served, and a stale "healthy" would not have saved the request anyway — the attempt fails on
/// its own and reports here. What it is for is startup and operators: a shard whose migration
/// could not run is recorded here instead of taking the host down, and the health check and CLI
/// read it to say which databases are missing.
/// </remarks>
[Experimental("ALB9001")]
public sealed class ShardHealth(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, ShardState> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Records the outcome of an attempt to reach <paramref name="shardId"/>. A null
    /// <paramref name="failure"/> marks it healthy.
    /// </summary>
    public void Report(string shardId, Exception? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        _states.AddOrUpdate(
            shardId,
            _ => new ShardState(shardId, failure is null, failure?.Message, _time.GetUtcNow()),
            (_, existing) => existing.Healthy == (failure is null)
                // Unchanged state keeps its original timestamp, so LastChangedAt answers
                // "how long has it been broken" rather than "when was it last checked".
                ? existing with { LastError = failure?.Message }
                : new ShardState(shardId, failure is null, failure?.Message, _time.GetUtcNow()));
    }

    /// <summary>
    /// The state of <paramref name="shardId"/>. A shard nothing has reported on is treated as
    /// healthy — no news is the normal case, since only failures report.
    /// </summary>
    public ShardState Get(string shardId) =>
        _states.TryGetValue(shardId, out var state)
            ? state
            : new ShardState(shardId, Healthy: true, LastError: null, _time.GetUtcNow());

    /// <summary>Every shard that has ever reported, keyed by shard id.</summary>
    public IReadOnlyDictionary<string, ShardState> All => _states;

    /// <summary>The shards currently known to be unreachable.</summary>
    public IReadOnlyCollection<string> Unhealthy =>
        _states.Values.Where(s => !s.Healthy).Select(s => s.ShardId).ToArray();
}
