namespace Alberto.EventStore.Postgres;

public class PostgresEventStoreOptions
{
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
    /// When true, automatically runs database migrations on startup.
    /// Set to false if you want to manage migrations separately.
    /// Default is false for production safety.
    /// </summary>
    public bool RunMigrations { get; set; } = false;
}