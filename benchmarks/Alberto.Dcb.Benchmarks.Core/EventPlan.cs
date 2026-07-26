using Alberto.Dcb;

namespace Alberto.Dcb.Benchmarks.Core;

/// <summary>
/// Deterministic event generation for benchmark seeding.
///
/// Pure and database-free on purpose: seeding determinism is the property that makes today's
/// reseeded template comparable to yesterday's, and it is only testable cheaply if generation
/// does not need a Postgres.
/// </summary>
public static class EventPlan
{
    /// <summary>The event types seeded stores are built from.</summary>
    public static IReadOnlyList<string> TypeIds { get; } =
        ["order-placed", "order-cancelled", "payment-received"];

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
                // FromStorage skips the validation regex. These ids are generated, not
                // user-supplied, and seeding cost is not what the suite is measuring.
                Tags = [EventTag.FromStorage("order", orderId)],
                EventData = $$"""{"orderId":"{{orderId}}","seq":{{i}},"amount":9.99}""",
            };
        }

        return events;
    }
}
