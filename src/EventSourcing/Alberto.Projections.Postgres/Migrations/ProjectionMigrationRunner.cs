using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Alberto.Projections.Postgres.Migrations;

/// <summary>
/// Simple migration runner that executes embedded SQL scripts.
/// Scripts must be idempotent (use CREATE IF NOT EXISTS, etc).
/// </summary>
public sealed class ProjectionMigrationRunner(
    string connectionString,
    ILogger logger) : IProjectionMigrationRunner
{
    private readonly string _connectionString =
        connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<bool> MigrateAsync(string schema, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new ArgumentException("Schema must be specified", nameof(schema));
        }

        _logger.LogInformation("Running Projection migrations for schema: {Schema}", schema);

        try
        {
            // Load embedded SQL scripts
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Migrations.") && name.EndsWith(".sql"))
                .OrderBy(name => name)
                .ToList();

            if (resourceNames.Count == 0)
            {
                _logger.LogWarning("No migration scripts found in assembly");
                return true;
            }

            _logger.LogInformation("Found {Count} migration script(s)", resourceNames.Count);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var resourceName in resourceNames)
            {
                _logger.LogInformation("Executing migration: {Script}", resourceName);

                // Read script from embedded resource
                await using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    _logger.LogError("Failed to load embedded resource: {ResourceName}", resourceName);
                    return false;
                }

                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(cancellationToken);

                // Replace schema variable
                sql = sql.Replace("$schema$", schema);

                // Execute in transaction
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using var command = new NpgsqlCommand(sql, connection, transaction);
                    command.CommandTimeout = 300; // 5 minutes
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation("Migration completed: {Script}", resourceName);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "Migration failed: {Script}", resourceName);
                    throw;
                }
            }

            _logger.LogInformation("Projection migrations completed successfully for schema: {Schema}", schema);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during Projection migration for schema: {Schema}", schema);
            return false;
        }
    }
}

/// <summary>
/// Interface for running database migrations for projection repositories.
/// Implement this interface to use custom migration tools.
/// </summary>
public interface IProjectionMigrationRunner
{
    /// <summary>
    /// Runs pending migrations for the specified schema.
    /// </summary>
    /// <param name="schema">The database schema to migrate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if migrations were successful, false otherwise</returns>
    Task<bool> MigrateAsync(string schema, CancellationToken cancellationToken = default);
}