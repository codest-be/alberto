using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.Dcb.Tenancy;

/// <summary>
/// Keeps a <see cref="TenantShardResolver"/>'s snapshot current, so an operator reassigning a
/// tenant does not have to restart anything.
/// </summary>
/// <remarks>
/// A refresh that fails is logged and retried on the next tick rather than escalated: the
/// resolver still has its previous snapshot, and every tenant already in it keeps being served
/// from the right database while the catalog is unreachable.
/// </remarks>
internal sealed class TenantShardCacheRefresher(
    string moduleKey,
    TenantShardResolver resolver,
    TimeSpan interval,
    ILogger<TenantShardCacheRefresher>? logger = null) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await resolver.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "Could not refresh the tenant shard catalog for module {ModuleKey}. " +
                    "Serving from the previous snapshot; retrying in {Interval}.",
                    moduleKey, interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
