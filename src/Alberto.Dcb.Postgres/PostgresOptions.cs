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

    /// <summary>
    /// When true (default), the subscription head is clamped by an in-flight
    /// visibility barrier (pg_xact_id / pg_snapshot_xmin) so it never advances past
    /// an append whose transaction has not committed yet. This prevents a
    /// slow-committing append from being skipped when later positions commit ahead
    /// of it. The head then lags by at most the duration of concurrently in-flight
    /// appends; a long-running *writing* transaction on the same database can hold
    /// it back further, so set this to false if that trade-off is undesirable.
    /// </summary>
    public bool EnableStableHeadBarrier { get; set; } = true;

    /// <summary>
    /// When true (default), a background listener consumes PostgreSQL LISTEN/NOTIFY
    /// on the events channel and wakes the subscription head immediately on append,
    /// instead of waiting for the polling interval. The interval still applies as a
    /// fallback. Uses one dedicated connection per module.
    /// </summary>
    public bool EnableNotifyListener { get; set; } = true;
}
