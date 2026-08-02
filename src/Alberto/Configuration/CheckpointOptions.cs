namespace Alberto.Configuration;

/// <summary>
/// What to do about checkpoints in the store that no declared processor claims.
/// </summary>
public enum OrphanCheckpointPolicy
{
    /// <summary>Ignore orphaned checkpoints.</summary>
    Off = 0,

    /// <summary>Log a warning naming each orphan. The default, in every environment.</summary>
    Warn = 1,

    /// <summary>Fail startup. Opt in through configuration.</summary>
    Strict = 2,
}

/// <summary>
/// Checkpoint hygiene settings for one module.
/// </summary>
public sealed record CheckpointOptions
{
    /// <summary>
    /// How to react to checkpoints whose processor id no longer matches any declared processor —
    /// usually the fingerprint of a renamed handler silently restarting from position zero.
    /// Defaults to <see cref="OrphanCheckpointPolicy.Warn"/> in every environment: a leftover
    /// row is inert, and a deleted processor is routine enough that it must not take a
    /// deployment down. Raise it to <see cref="OrphanCheckpointPolicy.Strict"/> through
    /// configuration where a rename slipping through unnoticed is the greater risk.
    /// </summary>
    public OrphanCheckpointPolicy OrphanPolicy { get; init; } = OrphanCheckpointPolicy.Warn;
}

/// <summary>Configuration mirror for <see cref="CheckpointOptions"/>.</summary>
public sealed class CheckpointOverrides : IAlbertoOverrides<CheckpointOptions>
{
    /// <summary>Mirror of <see cref="CheckpointOptions.OrphanPolicy"/>.</summary>
    public OrphanCheckpointPolicy? OrphanPolicy { get; set; }

    /// <inheritdoc />
    public CheckpointOptions ApplyTo(CheckpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            OrphanPolicy = OrphanPolicy ?? options.OrphanPolicy,
        };
    }
}
