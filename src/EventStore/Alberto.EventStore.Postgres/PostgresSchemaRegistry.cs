using System.Collections.Concurrent;

namespace Alberto.EventStore.Postgres;

/// <summary>
/// Registry that tracks all PostgreSQL EventStore schema registrations.
/// Used by MigrationHostedService to discover which schemas need migrations.
/// Uses a singleton pattern to maintain state across service collection building.
/// </summary>
public sealed class PostgresSchemaRegistry
{
    // ReSharper disable once InconsistentNaming
    private static readonly PostgresSchemaRegistry _instance = new();
    private readonly ConcurrentDictionary<string, (string Schema, string ConnectionString)> _moduleSchemas = new();

    private PostgresSchemaRegistry()
    {
    }

    /// <summary>
    /// Gets the singleton instance of the schema registry.
    /// </summary>
    public static PostgresSchemaRegistry Instance => _instance;

    /// <summary>
    /// Registers a module's schema with its connection string.
    /// </summary>
    /// <param name="moduleKey">The module key (EventStore type full name)</param>
    /// <param name="schema">The PostgreSQL schema name</param>
    /// <param name="connectionString">The PostgreSQL connection string</param>
    public void Register(string moduleKey, string schema, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(moduleKey))
            throw new ArgumentException("Module key cannot be null or whitespace.", nameof(moduleKey));

        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema cannot be null or whitespace.", nameof(schema));

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("ConnectionString cannot be null or whitespace.", nameof(connectionString));

        _moduleSchemas[moduleKey] = (schema, connectionString);
    }

    /// <summary>
    /// Gets all registered module keys with their schemas and connection strings.
    /// </summary>
    /// <returns>Read-only dictionary of module keys to (schema, connectionString) tuples</returns>
    public IReadOnlyDictionary<string, (string Schema, string ConnectionString)> GetAllWithModuleKeys() =>
        _moduleSchemas;

    /// <summary>
    /// Gets all registered schemas and their connection strings (backward compatibility).
    /// </summary>
    /// <returns>Read-only dictionary of schema names to connection strings</returns>
    [Obsolete("Use GetAllWithModuleKeys() instead for full module information")]
    public IReadOnlyDictionary<string, string> GetAll() =>
        _moduleSchemas.Values
            .DistinctBy(x => x.Schema)
            .ToDictionary(x => x.Schema, x => x.ConnectionString);

    public void Clear() => _moduleSchemas.Clear();
}