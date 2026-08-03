using Alberto.Configuration;

namespace Alberto.Postgres;

/// <summary>
/// Settings for the PostgreSQL event store backend.
/// </summary>
public sealed record PostgresOptions
{
    /// <summary>The Npgsql connection string. Required.</summary>
    public string ConnectionString { get; init; } = "";

    /// <summary>Whether Alberto applies its DbUp migrations at startup. Default true.</summary>
    public bool AutoMigrate { get; init; } = true;

    /// <summary>The schema Alberto's tables live in. Null means the connection's default schema.</summary>
    public string? Schema { get; init; }

    /// <summary>Maximum Npgsql pool size. Default 100.</summary>
    public int MaxPoolSize { get; init; } = 100;

    /// <summary>Minimum Npgsql pool size. Default 0.</summary>
    public int MinPoolSize { get; init; }

    /// <summary>How long a processor lease is held before it can be stolen. Default 60 seconds.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether consumers wait behind the stable-head visibility barrier. Default true.</summary>
    public bool EnableStableHeadBarrier { get; init; } = true;

    /// <summary>Whether the LISTEN/NOTIFY push-wakeup listener runs. Default true.</summary>
    public bool EnableNotifyListener { get; init; } = true;

    /// <summary>
    /// Maximum number of SQL statements Npgsql will keep as server-side prepared statements per
    /// connection. Default 50.
    /// <para>
    /// Alberto's per-module statement set totals roughly 45–50 distinct SQL strings across stream
    /// reads (8), append calls (4), advisory lock (1), position and stable-head queries (2),
    /// checkpoint store (~6), dead letter store (~8), lease manager (~4), tenant lock (~7), and
    /// projection rebuild store (~6). A ceiling of 50 covers the full active set — a given data
    /// source issues either the tenanted or the non-tenanted SQL variants, never both — while
    /// leaving no room for unbounded growth.
    /// </para>
    /// <para>
    /// Set to 0 to disable automatic statement preparation entirely. Takes precedence over any
    /// <c>Max Auto Prepare</c> value in the connection string.
    /// </para>
    /// </summary>
    public int MaxAutoPrepare { get; init; } = 50;

    /// <summary>
    /// Number of times a statement must be executed before Npgsql promotes it to a server-side
    /// prepared statement. Default 5.
    /// <para>
    /// Hot paths (stream reads, appends) cross this threshold within the first second of
    /// operation. Administrative one-off operations (dead-letter clears, rebuild promotions) that
    /// run rarely will not be promoted, which is the correct outcome — prepared statement slots
    /// are finite resources and should go to the steady-state workload.
    /// </para>
    /// </summary>
    public int AutoPrepareMinUsages { get; init; } = 5;
}

/// <summary>Configuration mirror for <see cref="PostgresOptions"/>.</summary>
public sealed class PostgresOverrides : IAlbertoOverrides<PostgresOptions>
{
    /// <summary>Nullable mirror of <see cref="PostgresOptions.ConnectionString"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.AutoMigrate"/>.</summary>
    public bool? AutoMigrate { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.Schema"/>.</summary>
    public string? Schema { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.MaxPoolSize"/>.</summary>
    public int? MaxPoolSize { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.MinPoolSize"/>.</summary>
    public int? MinPoolSize { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.LeaseDuration"/>.</summary>
    public TimeSpan? LeaseDuration { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.EnableStableHeadBarrier"/>.</summary>
    public bool? EnableStableHeadBarrier { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.EnableNotifyListener"/>.</summary>
    public bool? EnableNotifyListener { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.MaxAutoPrepare"/>.</summary>
    public int? MaxAutoPrepare { get; set; }

    /// <summary>Nullable mirror of <see cref="PostgresOptions.AutoPrepareMinUsages"/>.</summary>
    public int? AutoPrepareMinUsages { get; set; }

    /// <inheritdoc />
    public PostgresOptions ApplyTo(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            ConnectionString = ConnectionString ?? options.ConnectionString,
            AutoMigrate = AutoMigrate ?? options.AutoMigrate,
            Schema = Schema ?? options.Schema,
            MaxPoolSize = MaxPoolSize ?? options.MaxPoolSize,
            MinPoolSize = MinPoolSize ?? options.MinPoolSize,
            LeaseDuration = LeaseDuration ?? options.LeaseDuration,
            EnableStableHeadBarrier = EnableStableHeadBarrier ?? options.EnableStableHeadBarrier,
            EnableNotifyListener = EnableNotifyListener ?? options.EnableNotifyListener,
            MaxAutoPrepare = MaxAutoPrepare ?? options.MaxAutoPrepare,
            AutoPrepareMinUsages = AutoPrepareMinUsages ?? options.AutoPrepareMinUsages,
        };
    }
}
