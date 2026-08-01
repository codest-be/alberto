using Alberto;

namespace Alberto.Benchmarks.Core;

/// <summary>
/// Deterministic event generation for benchmark seeding.
///
/// Pure and database-free on purpose: seeding determinism is the property that makes today's
/// reseeded template comparable to yesterday's, and it is only testable cheaply if generation
/// does not need a Postgres.
/// </summary>
public static class EventPlan
{
    /// <summary>
    /// The event types seeded stores are built from, drawn uniformly.
    ///
    /// Twenty of them, and the count is the point. With three, a boundary naming three types
    /// selects the entire corpus, so any measurement of "does filtering by type pay off" is
    /// really measuring a filter that matches everything. Twenty uniform types make the named
    /// type count a selectivity knob: naming k of them selects k/20 of the log.
    /// </summary>
    public static IReadOnlyList<string> TypeIds { get; } =
    [
        "order-placed", "order-cancelled", "payment-received",
        "order-confirmed", "order-shipped", "order-delivered",
        "order-returned", "order-line-added", "order-line-removed",
        "order-address-changed", "order-discount-applied", "order-note-added",
        "payment-authorized", "payment-captured", "payment-refunded",
        "payment-failed", "invoice-issued", "invoice-settled",
        "shipment-dispatched", "shipment-delayed",
    ];

    /// <summary>Distinct order tags. Models the tag fan-out of a busy service.</summary>
    public const int DistinctOrders = 100;

    public static IReadOnlyList<EventToPersist> Build(int count, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var random = new Random(seed);
        var types = TypeIds.Select(id => new EventType(id)).ToArray();
        var events = new EventToPersist[count];

        for (var i = 0; i < count; i++)
        {
            var type = types[random.Next(types.Length)];
            var orderId = (i % DistinctOrders + 1).ToString();

            events[i] = new EventToPersist
            {
                EventType = type,
                // The validating constructor, not the internal FromStorage: seeding runs
                // outside every measured region, so the regex costs the suite nothing, and
                // a benchmark harness has no business reaching for framework internals.
                Tags = [new EventTag("order", orderId)],
                EventData = $$"""{"orderId":"{{orderId}}","seq":{{i}},"amount":9.99}""",
            };
        }

        return events;
    }
}
