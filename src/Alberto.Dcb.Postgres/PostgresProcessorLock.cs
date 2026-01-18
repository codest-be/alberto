using System.Security.Cryptography;
using System.Text;
using Alberto.Dcb.Subscriptions;
using Npgsql;

namespace Alberto.Dcb.Postgres;

/// <summary>
/// PostgreSQL advisory lock implementation for processor leadership.
/// Uses pg_try_advisory_lock with a hash of the consumer ID.
/// Only one instance can hold the lock at a time.
/// </summary>
public sealed class PostgresProcessorLock : IProcessorLock
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a new PostgresProcessorLock.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    public PostgresProcessorLock(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string consumerId, CancellationToken ct = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct);
        var lockId = GetLockId(consumerId);

        try
        {
            var acquired = await TryLockAsync(connection, lockId, ct);
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new AdvisoryLockLease(connection, lockId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static long GetLockId(string consumerId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(consumerId));
        return BitConverter.ToInt64(hash, 0);
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection conn, long lockId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_id)", conn);
        cmd.Parameters.AddWithValue("lock_id", lockId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool acquired && acquired;
    }

    private sealed class AdvisoryLockLease(NpgsqlConnection connection, long lockId) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_id)", connection);
                cmd.Parameters.AddWithValue("lock_id", lockId);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
