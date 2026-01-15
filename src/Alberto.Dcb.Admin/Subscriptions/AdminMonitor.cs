using Alberto.Dcb.Admin.Api.Models;
using Alberto.Dcb.Admin.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alberto.Dcb.Admin.Subscriptions;

/// <summary>
/// Background service that monitors admin data and publishes updates.
/// </summary>
public sealed class AdminMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdminMonitor> _logger;
    private readonly AdminModuleRegistry _registry;
    private readonly IAdminPublisher _publisher;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1);

    // Track last known state to detect changes
    private readonly Dictionary<string, ProcessorStatusDto> _lastProcessors = new();
    private readonly Dictionary<string, CheckpointDto> _lastCheckpoints = new();
    private readonly Dictionary<string, int> _lastDeadLetterCounts = new();
    private readonly Dictionary<string, SystemInfoDto> _lastSystemInfo = new();

    public AdminMonitor(
        IServiceScopeFactory scopeFactory,
        ILogger<AdminMonitor> logger,
        IOptions<AdminModuleRegistry> registry,
        IAdminPublisher publisher)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry.Value;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AdminMonitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorAllModules(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error monitoring admin data");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task MonitorAllModules(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        foreach (var (moduleKey, _) in _registry.Modules)
        {
            var queryService = scope.ServiceProvider.GetKeyedService<IAdminQueryService>(moduleKey);
            if (queryService is null) continue;

            await MonitorProcessors(moduleKey, queryService, ct);
            await MonitorCheckpoints(moduleKey, queryService, ct);
            await MonitorDeadLetters(moduleKey, queryService, ct);
            await MonitorSystemInfo(moduleKey, queryService, ct);
        }
    }

    private async Task MonitorProcessors(string moduleKey, IAdminQueryService queryService, CancellationToken ct)
    {
        var processors = await queryService.GetProcessorsAsync(ct);

        foreach (var processor in processors)
        {
            var key = $"{moduleKey}:{processor.ProcessorId}";
            var hasChanged = !_lastProcessors.TryGetValue(key, out var last) ||
                             HasProcessorChanged(last, processor);

            if (hasChanged)
            {
                _lastProcessors[key] = processor;
                await _publisher.PublishProcessorAsync(moduleKey, processor, ct);
            }
        }
    }

    private async Task MonitorCheckpoints(string moduleKey, IAdminQueryService queryService, CancellationToken ct)
    {
        var checkpoints = await queryService.GetCheckpointsAsync(ct);

        foreach (var checkpoint in checkpoints)
        {
            var key = $"{moduleKey}:{checkpoint.ProcessorId}";
            var hasChanged = !_lastCheckpoints.TryGetValue(key, out var last) ||
                             HasCheckpointChanged(last, checkpoint);

            if (hasChanged)
            {
                _lastCheckpoints[key] = checkpoint;
                await _publisher.PublishCheckpointAsync(moduleKey, checkpoint, ct);
            }
        }
    }

    private async Task MonitorDeadLetters(string moduleKey, IAdminQueryService queryService, CancellationToken ct)
    {
        var key = moduleKey;
        var currentCount = await queryService.GetDeadLetterCountAsync(ct: ct);

        if (!_lastDeadLetterCounts.TryGetValue(key, out var lastCount))
        {
            _lastDeadLetterCounts[key] = currentCount;
            return;
        }

        // Only fetch and publish if count increased (new dead letters)
        if (currentCount > lastCount)
        {
            var deadLetters = await queryService.GetDeadLettersAsync(page: 1, pageSize: currentCount - lastCount, ct: ct);
            foreach (var deadLetter in deadLetters.Items)
            {
                await _publisher.PublishDeadLetterAsync(moduleKey, deadLetter, DeadLetterChangeType.Added, ct);
            }
        }

        _lastDeadLetterCounts[key] = currentCount;
    }

    private async Task MonitorSystemInfo(string moduleKey, IAdminQueryService queryService, CancellationToken ct)
    {
        var info = await queryService.GetSystemInfoAsync(ct);
        var key = moduleKey;

        var hasChanged = !_lastSystemInfo.TryGetValue(key, out var last) ||
                         HasSystemInfoChanged(last, info);

        if (hasChanged)
        {
            _lastSystemInfo[key] = info;
            await _publisher.PublishSystemInfoAsync(moduleKey, info, ct);
        }
    }

    private static bool HasProcessorChanged(ProcessorStatusDto last, ProcessorStatusDto current)
    {
        return last.IsActive != current.IsActive ||
               last.LastPosition != current.LastPosition ||
               last.Lag != current.Lag ||
               last.DeadLetterCount != current.DeadLetterCount;
    }

    private static bool HasCheckpointChanged(CheckpointDto last, CheckpointDto current)
    {
        return last.LastPosition != current.LastPosition;
    }

    private static bool HasSystemInfoChanged(SystemInfoDto last, SystemInfoDto current)
    {
        return last.GlobalPosition != current.GlobalPosition ||
               last.ProcessorCount != current.ProcessorCount ||
               last.DeadLetterCount != current.DeadLetterCount;
    }
}
