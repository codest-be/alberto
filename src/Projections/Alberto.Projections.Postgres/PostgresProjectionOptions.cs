namespace Alberto.Projections.Postgres;

/// <summary>
/// Configuration options for PostgreSQL projection repository.
/// </summary>
public sealed class PostgresProjectionOptions
{
    /// <summary>
    /// PostgreSQL connection string.
    /// </summary>
    public required string ConnectionString { get; set; }

    public required string Schema { get; set; } = "default";
}