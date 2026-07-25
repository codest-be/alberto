namespace Alberto.Dcb.Configuration;

/// <summary>
/// Retry behaviour applied to a failing event handler before it is dead-lettered.
/// </summary>
public sealed record RetryOptions
{
    /// <summary>Maximum retry attempts before escalating. Default 3.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay before the first retry. Default 1 second.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Exponential backoff multiplier. 1.0 means a constant delay. Default 2.0.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>Upper bound for the backed-off delay. Default 30 seconds.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether exhausted events are dead-lettered (true) or skipped (false). Default true.</summary>
    public bool DeadLetterOnMaxRetries { get; init; } = true;

    /// <summary>Delay before attempt <paramref name="attemptNumber"/> (1-based), capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan CalculateDelay(int attemptNumber)
    {
        if (attemptNumber <= 1)
            return RetryDelay;

        var multiplier = Math.Pow(BackoffMultiplier, attemptNumber - 1);
        var delay = TimeSpan.FromMilliseconds(RetryDelay.TotalMilliseconds * multiplier);

        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }
}

/// <summary>Configuration mirror for <see cref="RetryOptions"/>.</summary>
public sealed class RetryOverrides : IAlbertoOverrides<RetryOptions>
{
    /// <summary>Mirror of <see cref="RetryOptions.MaxRetries"/>.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Mirror of <see cref="RetryOptions.RetryDelay"/>.</summary>
    public TimeSpan? RetryDelay { get; set; }

    /// <summary>Mirror of <see cref="RetryOptions.BackoffMultiplier"/>.</summary>
    public double? BackoffMultiplier { get; set; }

    /// <summary>Mirror of <see cref="RetryOptions.MaxRetryDelay"/>.</summary>
    public TimeSpan? MaxRetryDelay { get; set; }

    /// <summary>Mirror of <see cref="RetryOptions.DeadLetterOnMaxRetries"/>.</summary>
    public bool? DeadLetterOnMaxRetries { get; set; }

    /// <inheritdoc />
    public RetryOptions ApplyTo(RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            MaxRetries = MaxRetries ?? options.MaxRetries,
            RetryDelay = RetryDelay ?? options.RetryDelay,
            BackoffMultiplier = BackoffMultiplier ?? options.BackoffMultiplier,
            MaxRetryDelay = MaxRetryDelay ?? options.MaxRetryDelay,
            DeadLetterOnMaxRetries = DeadLetterOnMaxRetries ?? options.DeadLetterOnMaxRetries,
        };
    }
}

/// <summary>
/// Behaviour of the background loop that re-attempts dead-lettered events.
/// </summary>
public sealed record DeadLetterRetryOptions
{
    /// <summary>How often the retry loop polls for due dead letters. Default 1 minute.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Dead letters claimed per poll. Default 10.</summary>
    public int BatchSize { get; init; } = 10;

    /// <summary>How long a claimed dead letter stays claimed. Default 15 minutes.</summary>
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Configuration mirror for <see cref="DeadLetterRetryOptions"/>.</summary>
public sealed class DeadLetterRetryOverrides : IAlbertoOverrides<DeadLetterRetryOptions>
{
    /// <summary>Mirror of <see cref="DeadLetterRetryOptions.PollingInterval"/>.</summary>
    public TimeSpan? PollingInterval { get; set; }

    /// <summary>Mirror of <see cref="DeadLetterRetryOptions.BatchSize"/>.</summary>
    public int? BatchSize { get; set; }

    /// <summary>Mirror of <see cref="DeadLetterRetryOptions.ClaimLease"/>.</summary>
    public TimeSpan? ClaimLease { get; set; }

    /// <inheritdoc />
    public DeadLetterRetryOptions ApplyTo(DeadLetterRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            PollingInterval = PollingInterval ?? options.PollingInterval,
            BatchSize = BatchSize ?? options.BatchSize,
            ClaimLease = ClaimLease ?? options.ClaimLease,
        };
    }
}

/// <summary>
/// Single-writer processor leasing, used when more than one replica runs the same module.
/// </summary>
public sealed record ProcessorLeaseOptions
{
    /// <summary>Whether processors acquire a fenced lease before consuming. Default false.</summary>
    public bool Enabled { get; init; }

    /// <summary>Stable identity for this replica. Defaults to the machine name when null.</summary>
    public string? ReplicaId { get; init; }
}

/// <summary>Configuration mirror for <see cref="ProcessorLeaseOptions"/>.</summary>
public sealed class ProcessorLeaseOverrides : IAlbertoOverrides<ProcessorLeaseOptions>
{
    /// <summary>Mirror of <see cref="ProcessorLeaseOptions.Enabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Mirror of <see cref="ProcessorLeaseOptions.ReplicaId"/>.</summary>
    public string? ReplicaId { get; set; }

    /// <inheritdoc />
    public ProcessorLeaseOptions ApplyTo(ProcessorLeaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            ReplicaId = ReplicaId ?? options.ReplicaId,
        };
    }
}
