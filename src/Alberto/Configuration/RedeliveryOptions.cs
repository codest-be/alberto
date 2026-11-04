namespace Alberto.Configuration;

/// <summary>Which dead-lettered failures come back by themselves.</summary>
public enum RedeliverWhen
{
    /// <summary>Only failures the error classifier calls transient.</summary>
    Transient = 0,

    /// <summary>
    /// Transient failures and those the classifier cannot judge (the default). A permanent
    /// failure never redelivers: running the same event again cannot change the outcome.
    /// </summary>
    TransientOrUnknown = 1,
}

/// <summary>
/// Automatic redelivery of dead-lettered events, for reactors and message subscriptions.
/// A dead letter whose error is redeliverable comes back by itself after a growing wait. When
/// the last redelivery fails it stays parked until an operator retries it, as before.
/// Projections never dead-letter, so this does not apply to them (see <see cref="HaltOptions"/>).
/// </summary>
/// <remarks>
/// With the defaults the waits are 1 min, 4 min, 16 min, about 1 h 4 min, about 4 h 16 min and
/// then 12 h for the remaining five: ten redeliveries over about 2.7 days.
/// </remarks>
public sealed record RedeliveryOptions
{
    /// <summary>Whether dead letters are redelivered automatically. Default true.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Redeliveries before the dead letter stays parked for manual retry. Default 10. Zero parks at once.</summary>
    public int MaxRedeliveries { get; init; } = 10;

    /// <summary>Wait before the first redelivery. Default 1 minute.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Backoff multiplier. 1.0 means a constant wait. Default 4.0.</summary>
    public double BackoffMultiplier { get; init; } = 4.0;

    /// <summary>Upper bound for the wait between redeliveries. Default 12 hours.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Which failures are redelivered. Default <see cref="RedeliverWhen.TransientOrUnknown"/>.</summary>
    public RedeliverWhen RedeliverWhen { get; init; } = RedeliverWhen.TransientOrUnknown;

    /// <summary>Wait before redelivery <paramref name="redeliveryNumber"/> (1-based), capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan CalculateDelay(int redeliveryNumber) =>
        BackoffMath.Delay(FirstDelay, BackoffMultiplier, MaxDelay, redeliveryNumber);
}

/// <summary>Configuration mirror for <see cref="RedeliveryOptions"/>.</summary>
public sealed class RedeliveryOverrides : IAlbertoOverrides<RedeliveryOptions>
{
    /// <summary>Mirror of <see cref="RedeliveryOptions.Enabled"/>.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Mirror of <see cref="RedeliveryOptions.MaxRedeliveries"/>.</summary>
    public int? MaxRedeliveries { get; set; }

    /// <summary>Mirror of <see cref="RedeliveryOptions.FirstDelay"/>.</summary>
    public TimeSpan? FirstDelay { get; set; }

    /// <summary>Mirror of <see cref="RedeliveryOptions.BackoffMultiplier"/>.</summary>
    public double? BackoffMultiplier { get; set; }

    /// <summary>Mirror of <see cref="RedeliveryOptions.MaxDelay"/>.</summary>
    public TimeSpan? MaxDelay { get; set; }

    /// <summary>Mirror of <see cref="RedeliveryOptions.RedeliverWhen"/>.</summary>
    public RedeliverWhen? RedeliverWhen { get; set; }

    /// <inheritdoc />
    public RedeliveryOptions ApplyTo(RedeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            Enabled = Enabled ?? options.Enabled,
            MaxRedeliveries = MaxRedeliveries ?? options.MaxRedeliveries,
            FirstDelay = FirstDelay ?? options.FirstDelay,
            BackoffMultiplier = BackoffMultiplier ?? options.BackoffMultiplier,
            MaxDelay = MaxDelay ?? options.MaxDelay,
            RedeliverWhen = RedeliverWhen ?? options.RedeliverWhen,
        };
    }

    /// <summary>Fields set here win; the rest come from <paramref name="baseline"/>.</summary>
    internal RedeliveryOverrides MergeOver(RedeliveryOverrides? baseline) => new()
    {
        Enabled = Enabled ?? baseline?.Enabled,
        MaxRedeliveries = MaxRedeliveries ?? baseline?.MaxRedeliveries,
        FirstDelay = FirstDelay ?? baseline?.FirstDelay,
        BackoffMultiplier = BackoffMultiplier ?? baseline?.BackoffMultiplier,
        MaxDelay = MaxDelay ?? baseline?.MaxDelay,
        RedeliverWhen = RedeliverWhen ?? baseline?.RedeliverWhen,
    };
}
