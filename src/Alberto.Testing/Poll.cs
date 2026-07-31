using System.Diagnostics.CodeAnalysis;

namespace Alberto.Testing;

/// <summary>
/// Waits for an asynchronous system to reach a condition.
/// </summary>
/// <remarks>
/// Alberto's control loop is asynchronous by design, so a test that appends an event and
/// immediately asserts on a projection is asserting on a race. This is the one sanctioned way
/// to wait for it. It throws <see cref="TimeoutException"/> rather than failing an assertion,
/// because this package must stay usable from any test framework.
/// </remarks>
public static class Poll
{
    /// <summary>The timeout used when a caller does not supply one.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The interval between evaluations when a caller does not supply one.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Evaluates <paramref name="condition"/> until it returns <see langword="true"/>.
    /// </summary>
    /// <param name="condition">The condition to wait for. Evaluated immediately, then every <paramref name="interval"/>.</param>
    /// <param name="what">
    /// What is being waited for, in a form that completes the sentence "timed out waiting for ...".
    /// This is the only diagnostic a timeout can offer, so make it specific.
    /// </param>
    /// <param name="timeout">How long to wait. Defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="interval">How long to wait between evaluations. Defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="timeProvider">
    /// Clock used for both the deadline and the delay. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake to drive a test that must not spend real time.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">The condition did not hold within <paramref name="timeout"/>.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the condition delegate — Func<bool> against " +
                        "Func<ValueTask<bool>> — not by the optional tail. A lambda returning bool " +
                        "does not convert to the ValueTask form, so no call can bind to both.")]
    public static async Task UntilAsync(
        Func<ValueTask<bool>> condition,
        string what,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        var clock = timeProvider ?? TimeProvider.System;
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var effectiveInterval = interval ?? DefaultInterval;
        var deadline = clock.GetUtcNow() + effectiveTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Evaluate before delaying: a condition that already holds must cost nothing.
            if (await condition().ConfigureAwait(false))
                return;

            if (clock.GetUtcNow() >= deadline)
                throw new TimeoutException(
                    $"Timed out after {effectiveTimeout} waiting for {what}.");

            await Task.Delay(effectiveInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="UntilAsync(Func{ValueTask{bool}}, string, TimeSpan?, TimeSpan?, TimeProvider?, CancellationToken)"/>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
        Justification = "The overloads are separated by the condition delegate — Func<bool> against " +
                        "Func<ValueTask<bool>> — not by the optional tail. A lambda returning bool " +
                        "does not convert to the ValueTask form, so no call can bind to both.")]
    public static Task UntilAsync(
        Func<bool> condition,
        string what,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return UntilAsync(
            () => new ValueTask<bool>(condition()), what, timeout, interval, timeProvider, ct);
    }
}
