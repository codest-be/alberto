using System.Collections.Concurrent;
using Alberto.Tenancy;

namespace Alberto.Tests.Tenancy;

/// <summary>
/// An in-memory catalog with the same concurrency semantics as the Postgres one: the first
/// assignment for a tenant wins, and a later caller learns the winner rather than overwriting it.
/// </summary>
internal sealed class FakeTenantShardMap : ITenantShardMap
{
    private readonly ConcurrentDictionary<string, string> _assignments = new(StringComparer.Ordinal);

    public int ResolveCalls;
    public int AssignCalls;
    public int GetAllCalls;

    /// <summary>Blocks every <see cref="ResolveAsync"/> until released, to test single-flighting.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public FakeTenantShardMap Assign(string tenantId, string shardId)
    {
        _assignments[tenantId] = shardId;
        return this;
    }

    public async ValueTask<string?> ResolveAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref ResolveCalls);

        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken);

        return _assignments.TryGetValue(tenantId, out var shardId) ? shardId : null;
    }

    public ValueTask<string> AssignAsync(string tenantId, string shardId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref AssignCalls);
        return ValueTask.FromResult(_assignments.GetOrAdd(tenantId, shardId));
    }

    public ValueTask<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref GetAllCalls);
        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(_assignments, StringComparer.Ordinal));
    }
}
