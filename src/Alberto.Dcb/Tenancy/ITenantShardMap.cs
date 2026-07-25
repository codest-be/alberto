namespace Alberto.Dcb.Tenancy;

/// <summary>
/// The tenant-to-shard catalog: which database each tenant's data lives in.
/// </summary>
/// <remarks>
/// Deliberately small, and deliberately not a cache. Callers go through
/// <see cref="TenantShardResolver"/>, which does the caching; an implementation here should do
/// the plainest possible read and write. Swap the implementation with
/// <c>.WithShardMap&lt;TMap&gt;()</c> when the mapping already lives somewhere else — a tenant
/// registry service, say — rather than in Alberto's own catalog table.
/// </remarks>
public interface ITenantShardMap
{
    /// <summary>
    /// Returns the shard <paramref name="tenantId"/> is assigned to, or null when it has no
    /// assignment yet.
    /// </summary>
    ValueTask<string?> ResolveAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns <paramref name="tenantId"/> to <paramref name="shardId"/> if it has no assignment,
    /// and returns the assignment that is now in force.
    /// </summary>
    /// <remarks>
    /// The return value is the assignment that <em>won</em>, which is not necessarily
    /// <paramref name="shardId"/>: two replicas can see the same new tenant at the same moment,
    /// and only one of them writes. Callers must use what comes back, not what they asked for —
    /// otherwise the loser writes the tenant's events into a database nothing will read them from.
    /// </remarks>
    ValueTask<string> AssignAsync(string tenantId, string shardId, CancellationToken cancellationToken = default);

    /// <summary>Returns every assignment, keyed by tenant id.</summary>
    ValueTask<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);
}
