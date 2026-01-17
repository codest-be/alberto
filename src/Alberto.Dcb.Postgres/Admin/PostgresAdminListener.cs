using System.Text.RegularExpressions;
using System.Threading.Channels;
using Alberto.Dcb.Admin;
using Alberto.Dcb.Admin.Api.Models;
using Alberto.Dcb.Admin.Internal;
using Alberto.Dcb.Admin.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Alberto.Dcb.Postgres.Admin;

/// <summary>
/// Background service that listens for PostgreSQL NOTIFY events and publishes admin updates.
/// Uses debouncing to batch rapid notifications and reduce database queries.
/// </summary>
public sealed partial class PostgresAdminListener : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PostgresAdminListener> _logger;
    private readonly AdminModuleRegistry _registry;
    private readonly IAdminPublisher _publisher;
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(100);

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex ValidSchemaPattern();

    private static string ValidateSchemaName(string schema)
    {
        if (!ValidSchemaPattern().IsMatch(schema))
            throw new ArgumentException($"Invalid schema name: {schema}. Must be alphanumeric with underscores.", nameof(schema));
        return schema;
    }

    public PostgresAdminListener(
        IServiceScopeFactory scopeFactory,
        ILogger<PostgresAdminListener> logger,
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
        _logger.LogInformation("PostgresAdminListener starting for {ModuleCount} modules", _registry.Modules.Count);

        var listenerTasks = _registry.Modules
            .Select(kvp => ListenForModule(kvp.Key, stoppingToken))
            .ToList();

        await Task.WhenAll(listenerTasks);
    }

    private async Task ListenForModule(string moduleKey, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dataSource = scope.ServiceProvider.GetKeyedService<NpgsqlDataSource>(moduleKey);

        if (dataSource is null)
        {
            _logger.LogWarning("No NpgsqlDataSource found for module {ModuleKey}, skipping admin listener", moduleKey);
            return;
        }

        var adminDataAccess = scope.ServiceProvider.GetKeyedService<IAdminDataAccess>(moduleKey);
        var schema = adminDataAccess is PostgresAdminDataAccess pgAccess ? pgAccess.Schema : moduleKey;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenWithConnection(moduleKey, dataSource, schema, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in PostgresAdminListener for module {ModuleKey}, reconnecting in 5s", moduleKey);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ListenWithConnection(
        string moduleKey,
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var safeSchema = ValidateSchemaName(schema);
        var eventsChannel = $"{safeSchema}_events";
        var checkpointsChannel = $"{safeSchema}_checkpoints";
        var deadLettersChannel = $"{safeSchema}_dead_letters";

        await using var listenEvents = new NpgsqlCommand($"LISTEN {eventsChannel}", connection);
        await using var listenCheckpoints = new NpgsqlCommand($"LISTEN {checkpointsChannel}", connection);
        await using var listenDeadLetters = new NpgsqlCommand($"LISTEN {deadLettersChannel}", connection);

        await listenEvents.ExecuteNonQueryAsync(ct);
        await listenCheckpoints.ExecuteNonQueryAsync(ct);
        await listenDeadLetters.ExecuteNonQueryAsync(ct);

        _logger.LogInformation(
            "PostgresAdminListener connected for module {ModuleKey}, listening on channels: {Events}, {Checkpoints}, {DeadLetters}",
            moduleKey, eventsChannel, checkpointsChannel, deadLettersChannel);

        // Use a bounded channel for debouncing notifications
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        var notificationChannel = Channel.CreateBounded<(string Channel, string Payload)>(options);

        connection.Notification += (_, args) =>
        {
            notificationChannel.Writer.TryWrite((args.Channel, args.Payload));
        };

        // Start the debounced processor
        var processorTask = ProcessNotificationsAsync(moduleKey, notificationChannel.Reader, ct);

        // Keep connection alive and process notifications
        while (!ct.IsCancellationRequested)
        {
            await connection.WaitAsync(ct);
        }

        notificationChannel.Writer.Complete();
        await processorTask;
    }

    private async Task ProcessNotificationsAsync(
        string moduleKey,
        ChannelReader<(string Channel, string Payload)> reader,
        CancellationToken ct)
    {
        var pendingEvents = false;
        var pendingCheckpoints = new Dictionary<string, long>();
        var pendingDeadLetters = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for first notification or cancellation
                if (!await reader.WaitToReadAsync(ct))
                    break;

                // Collect notifications during debounce window
                var debounceEnd = DateTime.UtcNow + _debounceInterval;

                while (DateTime.UtcNow < debounceEnd)
                {
                    while (reader.TryRead(out var notification))
                    {
                        if (notification.Channel.EndsWith("_events"))
                        {
                            pendingEvents = true;
                        }
                        else if (notification.Channel.EndsWith("_checkpoints"))
                        {
                            var parts = notification.Payload.Split(':');
                            if (parts.Length == 2 && long.TryParse(parts[1], out var position))
                            {
                                pendingCheckpoints[parts[0]] = position;
                            }
                        }
                        else if (notification.Channel.EndsWith("_dead_letters"))
                        {
                            pendingDeadLetters = true;
                        }
                    }

                    // Short wait before checking again
                    await Task.Delay(10, ct);
                }

                // Process batched notifications
                await ProcessBatchAsync(moduleKey, pendingEvents, pendingCheckpoints, pendingDeadLetters, ct);

                // Reset state
                pendingEvents = false;
                pendingCheckpoints.Clear();
                pendingDeadLetters = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing notifications for module {ModuleKey}", moduleKey);
            }
        }
    }

    private async Task ProcessBatchAsync(
        string moduleKey,
        bool hasEvents,
        Dictionary<string, long> checkpoints,
        bool hasDeadLetters,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var queryService = scope.ServiceProvider.GetKeyedService<IAdminQueryService>(moduleKey);

        if (queryService is null) return;

        // Publish checkpoint updates (no DB query needed)
        foreach (var (processorId, position) in checkpoints)
        {
            var checkpoint = new CheckpointDto(processorId, position, DateTimeOffset.UtcNow);
            await _publisher.PublishCheckpointAsync(moduleKey, checkpoint, ct);
        }

        // If events or checkpoints changed, fetch and publish processor status
        if (hasEvents || checkpoints.Count > 0)
        {
            var processors = await queryService.GetProcessorsAsync(ct);
            foreach (var processor in processors)
            {
                await _publisher.PublishProcessorAsync(moduleKey, processor, ct);
            }
        }

        // If events or dead letters changed, fetch and publish system info
        if (hasEvents || hasDeadLetters)
        {
            var info = await queryService.GetSystemInfoAsync(ct);
            await _publisher.PublishSystemInfoAsync(moduleKey, info, ct);
        }

        // If dead letters changed, fetch latest
        if (hasDeadLetters)
        {
            var deadLetters = await queryService.GetDeadLettersAsync(page: 1, pageSize: 10, ct: ct);
            foreach (var dl in deadLetters.Items)
            {
                await _publisher.PublishDeadLetterAsync(moduleKey, dl, DeadLetterChangeType.Added, ct);
            }
        }
    }
}
