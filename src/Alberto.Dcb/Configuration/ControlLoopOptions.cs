namespace Alberto.Dcb.Configuration;

/// <summary>
/// Everything that governs the async control loop for one Alberto module.
/// </summary>
public sealed record ControlLoopOptions
{
    /// <summary>How often the consumer polls for new events. Default 250 ms.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum events fetched per poll. Default 100.</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>How often the stable-head tracker refreshes. Default 100 ms.</summary>
    public TimeSpan HeadRefreshInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Size of the in-flight transaction window the head tracker keeps. Default 2000.</summary>
    public int HeadWindowSize { get; init; } = 2000;

    /// <summary>
    /// How long shutdown waits for an in-flight handler to drain before abandoning it. Default 5 s.
    /// <para>
    /// A handler that ignores its <see cref="CancellationToken"/> would otherwise block
    /// <c>StopAsync</c> forever, stalling host shutdown and — under leasing — holding the
    /// processor lease past its expiry. When the timeout elapses the loop stops waiting,
    /// logs a warning and flushes the checkpoint at the last safely-completed position, so
    /// abandoned events are re-delivered on the next start.
    /// </para>
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Retry behaviour for failing handlers.</summary>
    public RetryOptions Retry { get; init; } = new();

    /// <summary>Behaviour of the dead-letter retry loop.</summary>
    public DeadLetterRetryOptions DeadLetterRetry { get; init; } = new();

    /// <summary>Single-writer leasing across replicas.</summary>
    public ProcessorLeaseOptions Leases { get; init; } = new();

    /// <summary>Zero-downtime projection rebuild pipeline. Disabled by default.</summary>
    public RebuildOptions Rebuilds { get; init; } = new();

    /// <summary>The all-defaults control loop.</summary>
    public static ControlLoopOptions Default { get; } = new();
}

/// <summary>Configuration mirror for <see cref="ControlLoopOptions"/>.</summary>
public sealed class ControlLoopOverrides : IAlbertoOverrides<ControlLoopOptions>
{
    /// <summary>Mirror of <see cref="ControlLoopOptions.PollingInterval"/>.</summary>
    public TimeSpan? PollingInterval { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.BatchSize"/>.</summary>
    public int? BatchSize { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.HeadRefreshInterval"/>.</summary>
    public TimeSpan? HeadRefreshInterval { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.HeadWindowSize"/>.</summary>
    public int? HeadWindowSize { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.DrainTimeout"/>.</summary>
    public TimeSpan? DrainTimeout { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.Retry"/>.</summary>
    public RetryOverrides? Retry { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.DeadLetterRetry"/>.</summary>
    public DeadLetterRetryOverrides? DeadLetterRetry { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.Leases"/>.</summary>
    public ProcessorLeaseOverrides? Leases { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.Rebuilds"/>.</summary>
    public RebuildOverrides? Rebuilds { get; set; }

    /// <inheritdoc />
    public ControlLoopOptions ApplyTo(ControlLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            PollingInterval = PollingInterval ?? options.PollingInterval,
            BatchSize = BatchSize ?? options.BatchSize,
            HeadRefreshInterval = HeadRefreshInterval ?? options.HeadRefreshInterval,
            HeadWindowSize = HeadWindowSize ?? options.HeadWindowSize,
            DrainTimeout = DrainTimeout ?? options.DrainTimeout,
            Retry = Retry?.ApplyTo(options.Retry) ?? options.Retry,
            DeadLetterRetry = DeadLetterRetry?.ApplyTo(options.DeadLetterRetry) ?? options.DeadLetterRetry,
            Leases = Leases?.ApplyTo(options.Leases) ?? options.Leases,
            Rebuilds = Rebuilds?.ApplyTo(options.Rebuilds) ?? options.Rebuilds,
        };
    }
}

/// <summary>
/// Settings for the zero-downtime projection rebuild pipeline.
/// </summary>
public sealed record RebuildOptions
{
    /// <summary>Whether the rebuild pipeline is registered and active. Default: <see langword="false"/>.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Promote a finished rebuild automatically as soon as it catches up. Default: <see langword="true"/>.
    /// Set <see langword="false"/> to park finished rebuilds at <c>Ready</c> until an operator promotes them.
    /// </summary>
    public bool AutoPromote { get; init; } = true;

    /// <summary>How often the rebuild coordinator polls for state changes. Default: 5 s.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the live-version cache refreshes. Default: 5 s.</summary>
    public TimeSpan VersionRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Configuration mirror for <see cref="RebuildOptions"/>.</summary>
public sealed class RebuildOverrides : IAlbertoOverrides<RebuildOptions>
{
    /// <summary>Mirror of <see cref="RebuildOptions.Enabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Mirror of <see cref="RebuildOptions.AutoPromote"/>.</summary>
    public bool? AutoPromote { get; set; }

    /// <summary>Mirror of <see cref="RebuildOptions.PollingInterval"/>.</summary>
    public TimeSpan? PollingInterval { get; set; }

    /// <summary>Mirror of <see cref="RebuildOptions.VersionRefreshInterval"/>.</summary>
    public TimeSpan? VersionRefreshInterval { get; set; }

    /// <inheritdoc />
    public RebuildOptions ApplyTo(RebuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            AutoPromote = AutoPromote ?? options.AutoPromote,
            PollingInterval = PollingInterval ?? options.PollingInterval,
            VersionRefreshInterval = VersionRefreshInterval ?? options.VersionRefreshInterval,
        };
    }
}
