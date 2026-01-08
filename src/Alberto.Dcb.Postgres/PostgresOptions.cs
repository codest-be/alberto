namespace Alberto.Dcb.Postgres;

/// <summary>
/// Configuration options for PostgreSQL backend.
/// </summary>
public sealed class PostgresOptions
{
    /// <summary>
    /// The PostgreSQL connection string.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Whether to automatically run database migrations on startup.
    /// Default is true.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;
}
