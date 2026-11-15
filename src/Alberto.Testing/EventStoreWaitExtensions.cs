namespace Alberto.Testing;

/// <summary>Waits for an event to be appended to the store.</summary>
public static class EventStoreWaitExtensions
{
    /// <summary>
    /// Waits until an event matching <paramref name="query"/> (and <paramref name="predicate"/>, when given)
    /// is in the store and returns it, or throws <see cref="TimeoutException"/> naming <paramref name="what"/>.
    /// </summary>
    /// <remarks>
    /// An event a command appends is in the store when the command returns: read it, no wait needed.
    /// This is for an event a reactor or other background work appends later. When what the test cares
    /// about is a read model, wait on that instead: the event is a step on the way, the read model is the outcome.
    /// Each poll reads only what was appended since the last one. In multi-tenant mode the read is scoped
    /// to the current tenant, as every <see cref="IEventStore.StreamAsync"/> is.
    /// </remarks>
    /// <param name="store">The event store.</param>
    /// <param name="query">Which events count; narrow it to the test's own tag so parallel tests do not match.</param>
    /// <param name="what">Completes "timed out waiting for ...": make it specific.</param>
    /// <param name="predicate">Further condition on a matching event; null takes the first match.</param>
    /// <param name="timeout">Defaults to <see cref="Poll.DefaultTimeout"/>.</param>
    /// <param name="timeProvider">Clock for the deadline; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<IEventEnvelope> WaitForEventAsync(
        this IEventStore store,
        DcbQuery query,
        string what,
        Func<IEventEnvelope, bool>? predicate = null,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);

        long after = 0;
        IEventEnvelope? match = null;
        await Poll.UntilAsync(async () =>
        {
            await foreach (var e in store.StreamAsync(query, after, cancellationToken).ConfigureAwait(false))
            {
                after = e.GlobalPosition;
                if (predicate is null || predicate(e)) { match = e; return true; }
            }
            return false;
        }, what, timeout, timeProvider: timeProvider, cancellationToken: cancellationToken).ConfigureAwait(false);
        return match!;
    }
}
