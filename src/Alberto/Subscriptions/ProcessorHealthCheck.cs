using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Alberto.Subscriptions;

/// <summary>
/// Reports the liveness of a module's control loops to the host's health-check pipeline.
/// </summary>
/// <remarks>
/// A faulted loop (an unrecoverable exception escaped the middleware chain and the loop stopped
/// permanently) and a stale loop (no completed poll cycle within <paramref name="stalenessThreshold"/>
/// — a handler wedged on a call that never returns, for example) are both Unhealthy: projections
/// behind them are frozen while the host keeps accepting traffic. A loop that heartbeats but has
/// fallen more than <paramref name="degradedLagThreshold"/> events behind is Degraded. Processors
/// that have never reported are skipped: a standby replica that did not win the lease never starts
/// its loop, and that is normal, not a failure.
/// </remarks>
/// <param name="moduleKey">The logical module key, used in the reported description.</param>
/// <param name="state">The module's processor health state.</param>
/// <param name="stalenessThreshold">
/// How long a loop may go without a heartbeat before it is reported wedged. Registration derives
/// it from the module's polling interval (5× with a 5-second floor), so slow-polling modules get
/// proportional slack.
/// </param>
/// <param name="degradedLagThreshold">
/// Lag above which a live loop is reported Degraded. Pass <see cref="long.MaxValue"/> to disable —
/// lag is noisy standalone (a restarting host catches up legitimately), so disabled is the default
/// the registration uses.
/// </param>
/// <param name="timeProvider">Clock for staleness evaluation. Defaults to the system clock.</param>
[Experimental("ALB9002")]
public sealed class ProcessorHealthCheck(
    string moduleKey,
    ProcessorHealthState state,
    TimeSpan stalenessThreshold,
    long degradedLagThreshold,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var unhealthy = new List<string>();
        var degraded = new List<string>();

        foreach (var processorId in state.ProcessorIds)
        {
            var snapshot = state.Get(processorId);
            if (!snapshot.HasReported)
                continue; // standby (lease not held) or host just started — no news, not a failure

            if (snapshot.IsFaulted)
            {
                unhealthy.Add($"Processor '{processorId}' faulted and stopped");
                data[processorId] = "faulted";
            }
            else if (now - snapshot.LastHeartbeatAt > stalenessThreshold)
            {
                var elapsed = now - snapshot.LastHeartbeatAt;
                unhealthy.Add($"Processor '{processorId}' last reported {elapsed} ago — possible wedge");
                data[processorId] = $"stale ({elapsed})";
            }
            else if (snapshot.Lag > degradedLagThreshold)
            {
                degraded.Add($"Processor '{processorId}' is {snapshot.Lag} events behind");
                data[processorId] = $"lagging ({snapshot.Lag})";
            }
            else
            {
                data[processorId] = "healthy";
            }
        }

        if (unhealthy.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Module '{moduleKey}': {string.Join("; ", unhealthy)}.", data: data));
        }

        if (degraded.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Module '{moduleKey}': {string.Join("; ", degraded)}.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"All reporting processors of module '{moduleKey}' are live.", data: data));
    }
}
