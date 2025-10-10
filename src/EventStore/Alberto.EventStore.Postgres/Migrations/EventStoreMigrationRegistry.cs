// ReSharper disable InconsistentNaming

namespace Alberto.EventStore.Postgres.Migrations;

/// <summary>
/// Registry for tracking EventStore schemas that require database migrations.
/// Used by EventStoreMigrationHostedService to run migrations at application startup.
/// Uses a static backing store to support registration during service configuration.
/// </summary>
public sealed class EventStoreMigrationRegistry
{
    private static readonly HashSet<SchemaRegistration> _registrations = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Registers a schema for migration at application startup.
    /// </summary>
    /// <param name="connectionString">Database connection string</param>
    /// <param name="schema">Schema name to migrate</param>
    public void Register(string connectionString, string schema)
    {
        lock (_lock)
        {
            _registrations.Add(new SchemaRegistration(connectionString, schema));
        }
    }

    /// <summary>
    /// Gets all registered schemas that need migration.
    /// </summary>
    public IReadOnlyCollection<SchemaRegistration> GetRegistrations()
    {
        lock (_lock)
        {
            return _registrations.ToList();
        }
    }

    /// <summary>
    /// Represents a schema registration for migration.
    /// </summary>
    public sealed record SchemaRegistration(string ConnectionString, string Schema);
}