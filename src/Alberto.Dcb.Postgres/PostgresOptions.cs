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

    /// <summary>
    /// The PostgreSQL schema to use for all tables and functions.
    /// If null or empty, uses the default PostgreSQL search path (typically "public").
    /// Example: "orders" creates orders.events, orders.append_events(), etc.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Maximum number of connections in the connection pool.
    /// Default is 100 (Npgsql default).
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Minimum number of connections in the connection pool.
    /// Default is 0.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>
    /// How long a tenant lease is valid before expiring.
    /// Leases must be renewed before expiry to maintain ownership.
    /// Default is 60 seconds.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);
}
