using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Reports a sharded module's shards to the host's health-check pipeline.
/// </summary>
/// <remarks>
/// Healthy when every shard is up, Degraded when some are, Unhealthy when none are. The middle
/// state is the point of the whole exercise: one database being unreachable costs its own tenants
/// and nobody else, and a load balancer that pulls the instance out over it would only spread the
/// outage to the tenants that were fine.
/// </remarks>
/// <param name="moduleKey">The logical module key, used in the reported description.</param>
/// <param name="health">The module's shard health.</param>
/// <param name="shardIds">Every shard declared for the module, healthy or not.</param>
[Experimental("ALB9001")]
public sealed class ShardHealthCheck(
    string moduleKey,
    ShardHealth health,
    IReadOnlyCollection<string> shardIds) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var unhealthy = shardIds.Where(id => !health.Get(id).Healthy).ToArray();

        if (unhealthy.Length == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"All {shardIds.Count} shards of module '{moduleKey}' are available."));
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var shardId in shardIds)
        {
            var state = health.Get(shardId);
            data[shardId] = state.Healthy ? "healthy" : state.LastError ?? "unavailable";
        }

        var description =
            $"{unhealthy.Length} of {shardIds.Count} shards of module '{moduleKey}' are " +
            $"unavailable: {string.Join(", ", unhealthy)}.";

        return Task.FromResult(unhealthy.Length == shardIds.Count
            ? HealthCheckResult.Unhealthy(description, data: data)
            : HealthCheckResult.Degraded(description, data: data));
    }
}
