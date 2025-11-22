using Alberto.EventStore.Postgres.Migrations;

namespace Alberto.EventStore.Postgres;

public class PostgresEventStoreOptions
{
    private int _commandTimeoutSeconds = 30;
    private string _connectionString = null!;
    private string _schema = "default";

    /// <summary>
    /// PostgreSQL connection string.
    /// </summary>
    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("ConnectionString cannot be null or whitespace.", nameof(ConnectionString));
            _connectionString = value;
        }
    }

    /// <summary>
    /// Database schema name. Must not be null or whitespace.
    /// </summary>
    public string Schema
    {
        get => _schema;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Schema cannot be null or whitespace.", nameof(Schema));
            _schema = value;
        }
    }

    public int BulkInsertThreshold { get; set; } = 5;

    /// <summary>
    /// Command timeout in seconds for all Postgres operations. Default: 30 seconds.
    /// </summary>
    public int CommandTimeoutSeconds
    {
        get => _commandTimeoutSeconds;
        set => _commandTimeoutSeconds = value > 0 ? value : 30;
    }

    /// <summary>
    /// Migration strategy for EventStore schema management.
    /// Default: AutoMigrationStrategy (runs migrations at startup - use only for development).
    /// For production, use NoMigrationStrategy or ScriptOnlyMigrationStrategy.
    /// </summary>
    public IMigrationStrategy MigrationStrategy { get; set; } = new AutoMigrationStrategy();
}