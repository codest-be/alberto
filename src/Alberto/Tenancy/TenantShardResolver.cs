using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alberto.Tenancy;

/// <summary>
/// Answers "which database is this tenant in?" for every read and every append of a sharded
/// module.
/// </summary>
/// <remarks>
/// The answer is on the hot path but almost never changes, so it is served from an immutable
/// snapshot that a background refresh replaces wholesale. A tenant the snapshot has not seen
/// falls through to the catalog behind a per-tenant gate, so a burst of first requests for one
/// tenant produces one query rather than one per request.
/// <para>
/// The cache is an optimisation and nothing more. Resolution happens inside each async call
/// rather than when the event store is constructed, so a cold cache costs a query, never a
/// wrong answer.
/// </para>
/// </remarks>
[Experimental("ALB9001")]
public sealed class TenantShardResolver
{
    private readonly ITenantShardMap _map;
    private readonly IReadOnlySet<string> _declaredShards;
    private readonly string _moduleKey;
    private readonly string? _defaultShardId;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedShards = new(StringComparer.Ordinal);

    private volatile ImmutableDictionary<string, string> _snapshot =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <param name="moduleKey">The logical module key. Used in exception and log messages.</param>
    /// <param name="map">The catalog.</param>
    /// <param name="declaredShards">
    /// The shards this deployment actually registered. A catalog row naming anything else fails
    /// the tenants that reference it rather than the host — an operator's stray INSERT must not
    /// be able to block a deploy.
    /// </param>
    /// <param name="defaultShardId">Where to place a tenant with no row, or null to refuse.</param>
    /// <param name="logger">Optional logger.</param>
    public TenantShardResolver(
        string moduleKey,
        ITenantShardMap map,
        IReadOnlySet<string> declaredShards,
        string? defaultShardId,
        ILogger<TenantShardResolver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(declaredShards);

        _moduleKey = moduleKey;
        _map = map;
        _declaredShards = declaredShards;
        _defaultShardId = defaultShardId;
        _logger = logger ?? NullLogger<TenantShardResolver>.Instance;
    }

    /// <summary>Returns the shard <paramref name="tenantId"/> belongs to.</summary>
    /// <exception cref="UnknownTenantException">
    /// The tenant has no assignment and the module declares no default shard.
    /// </exception>
    /// <exception cref="ShardUnavailableException">
    /// The tenant is assigned to a shard this deployment does not declare.
    /// </exception>
    public async ValueTask<string> ResolveAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_snapshot.TryGetValue(tenantId, out var cached))
            return Verify(tenantId, cached);

        var gate = _gates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check: whoever held the gate before us has already filled this in.
            if (_snapshot.TryGetValue(tenantId, out cached))
                return Verify(tenantId, cached);

            var resolved = await _map.ResolveAsync(tenantId, cancellationToken).ConfigureAwait(false);

            if (resolved is null)
            {
                if (_defaultShardId is null)
                    throw new UnknownTenantException(_moduleKey, tenantId);

                // Another replica may be doing this at the same instant. AssignAsync returns the
                // assignment that won, which is what we must use and cache.
                resolved = await _map.AssignAsync(tenantId, _defaultShardId, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Tenant {TenantId} of module {ModuleKey} had no shard assignment and was placed on shard {ShardId}.",
                    tenantId, _moduleKey, resolved);
            }

            Remember(tenantId, resolved);
            return Verify(tenantId, resolved);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Re-reads the whole catalog and replaces the snapshot, so an operator's reassignment takes
    /// effect without a restart.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var all = await _map.GetAllAsync(cancellationToken).ConfigureAwait(false);
        _snapshot = all.ToImmutableDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }

    private void Remember(string tenantId, string shardId)
    {
        // Replaces the whole reference; a concurrent refresh may win, and losing one entry only
        // costs the next request for that tenant a catalog read.
        _snapshot = _snapshot.SetItem(tenantId, shardId);
    }

    private string Verify(string tenantId, string shardId)
    {
        if (_declaredShards.Contains(shardId))
            return shardId;

        // Logged once per shard id: a whole tenant population pointing at a retired shard would
        // otherwise emit one line per request.
        lock (_warnedShards)
        {
            if (_warnedShards.Add(shardId))
            {
                _logger.LogError(
                    "The catalog assigns tenants of module {ModuleKey} to shard {ShardId}, which this " +
                    "deployment does not declare. Those tenants cannot be served until the shard is " +
                    "declared or they are reassigned.",
                    _moduleKey, shardId);
            }
        }

        throw new ShardUnavailableException(
            _moduleKey, shardId,
            $"tenant '{tenantId}' is assigned to it, but this deployment declares only " +
            $"[{string.Join(", ", _declaredShards.Order(StringComparer.Ordinal))}]");
    }
}
