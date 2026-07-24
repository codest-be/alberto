using Alberto.Dcb.Subscriptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// Consumes PostgreSQL LISTEN/NOTIFY on the events channel and pulses an
/// <see cref="IEventAppendedSignal"/> so <see cref="EventStoreHead"/> refreshes as
/// soon as an append commits, rather than waiting for its polling interval. The
/// trigger that raises the notification is defined in the initial schema migration
/// (<c>alberto_notify_events</c> → <c>pg_notify('{schema}_events', ...)</c>).
///
/// Holds one dedicated connection. Reconnects with a short backoff on failure and
/// pulses once on (re)connect so anything appended during a gap is picked up by the
/// next poll.
/// </summary>
internal sealed class PostgresEventListener : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _channel;
    private readonly IEventAppendedSignal _signal;
    private readonly ILogger<PostgresEventListener>? _logger;

    public PostgresEventListener(
        NpgsqlDataSource dataSource,
        string? schema,
        IEventAppendedSignal signal,
        ILogger<PostgresEventListener>? logger = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger;

        // Must match alberto_notify_events(): pg_notify('$schema$_events', ...),
        // where $schema$ is the schema name or "public" when none is configured.
        var schemaName = string.IsNullOrWhiteSpace(schema) ? "public" : schema;
        _channel = $"{schemaName}_events";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += OnNotification;
                try
                {
                    await using (var cmd = new NpgsqlCommand($"LISTEN {QuoteIdentifier(_channel)}", connection))
                        await cmd.ExecuteNonQueryAsync(stoppingToken);

                    // Catch up on anything appended before this connection was ready.
                    _signal.Signal();

                    while (!stoppingToken.IsCancellationRequested)
                        await connection.WaitAsync(stoppingToken);
                }
                finally
                {
                    connection.Notification -= OnNotification;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Postgres event listener on channel {Channel} disconnected; reconnecting", _channel);
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e) => _signal.Signal();

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
