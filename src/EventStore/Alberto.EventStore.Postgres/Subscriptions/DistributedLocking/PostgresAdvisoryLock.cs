using System.Security.Cryptography;
using System.Text;
using Alberto.EventStore.Subscriptions.DistributedLocking;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.EventStore.Postgres.Subscriptions.DistributedLocking;

/// <summary>
/// PostgreSQL advisory lock implementation for distributed locking.
/// Uses session-level advisory locks to ensure only one polling instance is active.
/// </summary>
public sealed class PostgresAdvisoryLock : IDistributedLock
{
    private readonly string _connectionString;
    private readonly long _lockKey;
    private readonly ILogger<PostgresAdvisoryLock> _logger;
    private bool _disposed;
    private bool _isLockHeld;
    private NpgsqlConnection? _lockConnection;

    public PostgresAdvisoryLock(
        string connectionString,
        string moduleKey,
        ILogger<PostgresAdvisoryLock> logger)
    {
        _connectionString = connectionString;
        _lockKey = GenerateLockKey(moduleKey);
        _logger = logger;
    }

    /// <summary>
    /// Gets whether this instance currently holds the lock.
    /// </summary>
    public bool IsLockHeld => _isLockHeld && !_disposed;

    /// <summary>
    /// Attempts to acquire the distributed lock using PostgreSQL advisory lock.
    /// </summary>
    public async ValueTask<bool> TryAcquireLockAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isLockHeld)
        {
            _logger.LogDebug("Lock already held with key {LockKey}", _lockKey);
            return true;
        }

        try
        {
            // Create dedicated connection for the lock (cannot use pooled connection)
            // Lock is bound to the connection and released when connection closes
            _lockConnection = new NpgsqlConnection(_connectionString);
            await _lockConnection.OpenAsync(cancellationToken);

            // Try to acquire advisory lock (non-blocking)
            var acquired = await _lockConnection.QuerySingleAsync<bool>(
                "SELECT pg_try_advisory_lock(@LockKey)",
                new { LockKey = _lockKey }
            );

            if (acquired)
            {
                _isLockHeld = true;
                _logger.LogInformation(
                    "Acquired distributed lock with key {LockKey}. This instance is now the polling leader.",
                    _lockKey
                );
                return true;
            }

            // Failed to acquire lock, close connection
            await _lockConnection.DisposeAsync();
            _lockConnection = null;

            _logger.LogDebug(
                "Failed to acquire distributed lock with key {LockKey}. Another instance is the leader.",
                _lockKey
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring distributed lock with key {LockKey}", _lockKey);

            // Clean up connection on error
            if (_lockConnection != null)
            {
                await _lockConnection.DisposeAsync();
                _lockConnection = null;
            }

            return false;
        }
    }

    /// <summary>
    /// Releases the distributed lock if held.
    /// </summary>
    public async ValueTask ReleaseLockAsync(CancellationToken cancellationToken = default)
    {
        if (!_isLockHeld || _lockConnection == null)
        {
            return;
        }

        try
        {
            // Explicitly release advisory lock
            await _lockConnection.ExecuteAsync(
                "SELECT pg_advisory_unlock(@LockKey)",
                new { LockKey = _lockKey }
            );

            _logger.LogInformation("Released distributed lock with key {LockKey}", _lockKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error releasing distributed lock with key {LockKey}", _lockKey);
        }
        finally
        {
            _isLockHeld = false;

            // Close connection (will also auto-release lock if unlock failed)
            if (_lockConnection != null)
            {
                await _lockConnection.DisposeAsync();
                _lockConnection = null;
            }
        }
    }

    /// <summary>
    /// Disposes the lock and releases it if held.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ReleaseLockAsync();
    }

    /// <summary>
    /// Generates a stable 64-bit lock key from the module key using SHA256.
    /// </summary>
    private static long GenerateLockKey(string moduleKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(moduleKey));
        return BitConverter.ToInt64(hash, 0);
    }
}