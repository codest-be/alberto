using Alberto.Dcb.Admin.Api.Models;
using Alberto.Dcb.Admin.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Background service that monitors processor status and publishes updates.
/// </summary>
public sealed class ProcessorStatusMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessorStatusMonitor> _logger;
    private readonly AdminModuleRegistry _registry;
    private readonly IProcessorStatusPublisher _publisher;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, ProcessorStatusDto> _lastStatus = new();

    public ProcessorStatusMonitor(
        IServiceScopeFactory scopeFactory,
        ILogger<ProcessorStatusMonitor> logger,
        IOptions<AdminModuleRegistry> registry,
        IProcessorStatusPublisher publisher)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry.Value;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProcessorStatusMonitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndPublishUpdates(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error monitoring processor status");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task CheckAndPublishUpdates(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        foreach (var (moduleKey, _) in _registry.Modules)
        {
            var queryService = scope.ServiceProvider.GetKeyedService<IAdminQueryService>(moduleKey);
            if (queryService is null) continue;

            var processors = await queryService.GetProcessorsAsync(ct);

            foreach (var processor in processors)
            {
                var key = $"{moduleKey}:{processor.ProcessorId}";
                var hasChanged = !_lastStatus.TryGetValue(key, out var last) ||
                                 HasStatusChanged(last, processor);

                if (hasChanged)
                {
                    _lastStatus[key] = processor;
                    await _publisher.PublishAsync(moduleKey, processor, ct);
                }
            }
        }
    }

    private static bool HasStatusChanged(ProcessorStatusDto last, ProcessorStatusDto current)
    {
        return last.IsActive != current.IsActive ||
               last.LastPosition != current.LastPosition ||
               last.Lag != current.Lag ||
               last.DeadLetterCount != current.DeadLetterCount;
    }
}
