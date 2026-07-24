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

    /// <summary>Retry behaviour for failing handlers.</summary>
    public RetryOptions Retry { get; init; } = new();

    /// <summary>Behaviour of the dead-letter retry loop.</summary>
    public DeadLetterRetryOptions DeadLetterRetry { get; init; } = new();

    /// <summary>Single-writer leasing across replicas.</summary>
    public ProcessorLeaseOptions Leases { get; init; } = new();

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

    /// <summary>Mirror of <see cref="ControlLoopOptions.Retry"/>.</summary>
    public RetryOverrides? Retry { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.DeadLetterRetry"/>.</summary>
    public DeadLetterRetryOverrides? DeadLetterRetry { get; set; }

    /// <summary>Mirror of <see cref="ControlLoopOptions.Leases"/>.</summary>
    public ProcessorLeaseOverrides? Leases { get; set; }

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
            Retry = Retry?.ApplyTo(options.Retry) ?? options.Retry,
            DeadLetterRetry = DeadLetterRetry?.ApplyTo(options.DeadLetterRetry) ?? options.DeadLetterRetry,
            Leases = Leases?.ApplyTo(options.Leases) ?? options.Leases,
        };
    }
}
