using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL advisory lock implementation for tenant-level processor leadership.
/// Uses pg_try_advisory_lock with a hash of the consumer ID and tenant ID.
/// Allows multiple instances to each hold locks for different tenants.
/// Each tenant lock uses its own connection since advisory locks are connection-scoped.
/// </summary>
public sealed class PostgresTenantProcessorLock : ITenantProcessorLock
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ConcurrentDictionary<string, TenantLockLease> _activeLeases = new();

    /// <summary>
    /// Creates a new PostgresTenantProcessorLock.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    public PostgresTenantProcessorLock(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> TryAcquireForTenantAsync(
        string consumerId, string tenantId, CancellationToken ct = default)
    {
        var leaseKey = GetLeaseKey(consumerId, tenantId);

        // Check if we already hold this lease
        if (_activeLeases.TryGetValue(leaseKey, out var existingLease) && !existingLease.IsDisposed)
        {
            return existingLease;
        }

        var connection = await _dataSource.OpenConnectionAsync(ct);
        var lockId = GetLockId(consumerId, tenantId);

        try
        {
            var acquired = await TryLockAsync(connection, lockId, ct);
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }

            var lease = new TenantLockLease(connection, lockId, leaseKey, RemoveLease);
            _activeLeases[leaseKey] = lease;
            return lease;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void RemoveLease(string leaseKey)
    {
        _activeLeases.TryRemove(leaseKey, out _);
    }

    private static string GetLeaseKey(string consumerId, string tenantId)
        => $"{consumerId}|{tenantId}";

    /// <summary>
    /// Gets the deterministic lock ID for a consumer and tenant combination.
    /// Uses SHA256 hash of "{consumerId}|{tenantId}" for consistent distribution.
    /// </summary>
    public static long GetLockId(string consumerId, string tenantId)
    {
        var input = $"{consumerId}|{tenantId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToInt64(hash, 0);
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection conn, long lockId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_id)", conn);
        cmd.Parameters.AddWithValue("lock_id", lockId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool acquired && acquired;
    }

    private sealed class TenantLockLease : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly long _lockId;
        private readonly string _leaseKey;
        private readonly Action<string> _onDispose;
        private int _disposed;

        public bool IsDisposed => _disposed != 0;

        public TenantLockLease(
            NpgsqlConnection connection,
            long lockId,
            string leaseKey,
            Action<string> onDispose)
        {
            _connection = connection;
            _lockId = lockId;
            _leaseKey = leaseKey;
            _onDispose = onDispose;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _onDispose(_leaseKey);

                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_id)", _connection);
                cmd.Parameters.AddWithValue("lock_id", _lockId);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }
}
