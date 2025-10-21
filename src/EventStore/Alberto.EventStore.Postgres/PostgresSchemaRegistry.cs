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
    private readonly ConcurrentDictionary<string, string> _schemas = new(StringComparer.OrdinalIgnoreCase);

    private PostgresSchemaRegistry() { }

    /// <summary>
    /// Gets the singleton instance of the schema registry.
    /// </summary>
    public static PostgresSchemaRegistry Instance => _instance;

    /// <summary>
    /// Registers a schema with its connection string.
    /// </summary>
    /// <param name="schema">The schema name (module key)</param>
    /// <param name="connectionString">The PostgreSQL connection string</param>
    public void Register(string schema, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema cannot be null or whitespace.", nameof(schema));

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("ConnectionString cannot be null or whitespace.", nameof(connectionString));

        _schemas[schema] = connectionString;
    }

    /// <summary>
    /// Gets all registered schemas and their connection strings.
    /// </summary>
    /// <returns>Read-only dictionary of schema names to connection strings</returns>
    public IReadOnlyDictionary<string, string> GetAll() => _schemas;

    public void Clear() => _schemas.Clear();
}