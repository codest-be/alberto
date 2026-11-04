namespace Alberto.Configuration;

/// <summary>
/// How a projection behaves when it cannot process an event. A projection never dead-letters
/// and never skips: it stays on the failing event and retries that event in place on this
/// backoff until it succeeds, so the read model never moves past an event it did not apply.
/// </summary>
/// <remarks>
/// Applies to projections and the outbox handler. The in-place retries start after the fast
/// <see cref="RetryOptions"/> attempts are used up. With the defaults the waits are
/// 1 s, 2 s, 4 s, 8 s, 16 s, 32 s and then 1 minute for as long as the failure lasts.
/// </remarks>
public sealed record HaltOptions
{
    /// <summary>Wait before the first in-place retry. Default 1 second.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Backoff multiplier. 1.0 means a constant delay. Default 2.0.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>Upper bound for the wait between in-place retries. Default 1 minute.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Wait before in-place retry <paramref name="attemptNumber"/> (1-based), capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan CalculateDelay(int attemptNumber) =>
        BackoffMath.Delay(InitialDelay, BackoffMultiplier, MaxDelay, attemptNumber);
}

/// <summary>Configuration mirror for <see cref="HaltOptions"/>.</summary>
public sealed class HaltOverrides : IAlbertoOverrides<HaltOptions>
{
    /// <summary>Mirror of <see cref="HaltOptions.InitialDelay"/>.</summary>
    public TimeSpan? InitialDelay { get; set; }

    /// <summary>Mirror of <see cref="HaltOptions.BackoffMultiplier"/>.</summary>
    public double? BackoffMultiplier { get; set; }

    /// <summary>Mirror of <see cref="HaltOptions.MaxDelay"/>.</summary>
    public TimeSpan? MaxDelay { get; set; }

    /// <inheritdoc />
    public HaltOptions ApplyTo(HaltOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options with
        {
            InitialDelay = InitialDelay ?? options.InitialDelay,
            BackoffMultiplier = BackoffMultiplier ?? options.BackoffMultiplier,
            MaxDelay = MaxDelay ?? options.MaxDelay,
        };
    }

    /// <summary>Fields set here win; the rest come from <paramref name="baseline"/>.</summary>
    internal HaltOverrides MergeOver(HaltOverrides? baseline) => new()
    {
        InitialDelay = InitialDelay ?? baseline?.InitialDelay,
        BackoffMultiplier = BackoffMultiplier ?? baseline?.BackoffMultiplier,
        MaxDelay = MaxDelay ?? baseline?.MaxDelay,
    };
}

internal static class BackoffMath
{
    /// <summary>
    /// <paramref name="first"/> × <paramref name="multiplier"/>^(attempt-1), capped at
    /// <paramref name="max"/>. Computed in doubles: far enough out the product overflows
    /// <see cref="TimeSpan"/>, and FromMilliseconds throws instead of saturating.
    /// </summary>
    internal static TimeSpan Delay(TimeSpan first, double multiplier, TimeSpan max, int attempt)
    {
        if (attempt <= 1)
            return first < max ? first : max;

        var milliseconds = first.TotalMilliseconds * Math.Pow(multiplier, attempt - 1);

        return double.IsFinite(milliseconds) && milliseconds < max.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : max;
    }
}
