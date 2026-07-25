using Alberto.Dcb.Configuration;

namespace Alberto.Dcb.Postgres;

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
        };
    }
}
