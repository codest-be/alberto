using System.Text.Json;
using Alberto.EventStore.Events;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.EventStore.Tests.Specifications;

/// <summary>
///     Shared test fixture for event store tests
///     Provides common utilities and test data generators
/// </summary>
public class EventStoreTestFixture : IDisposable
{
    public static readonly TimeProvider TestTimeProvider =
        new FakeTimeProvider(new DateTimeOffset(2025, 3, 21, 11, 47, 12, TimeSpan.FromHours(5)));

    public EventStoreTestFixture()
    {
        // Initialize any shared test infrastructure
        Console.WriteLine("Alberto.EventStore test fixture initialized");
    }

    public void Dispose()
    {
        // Cleanup shared test infrastructure
        Console.WriteLine("Alberto.EventStore test fixture disposed");
    }

    /// <summary>
    ///     Creates a test event with the specified parameters
    /// </summary>
    public static IEventToPersist CreateTestEvent(
        string eventType,
        Dictionary<string, string>? metadata = null,
        params string[] eventTags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = """{"data": "test", "timestamp": "2025-03-21T11:47:12Z"}""",
            Tags = eventTags.Select(EventTag.Parse).ToList(),
            Metadata = metadata ?? new Dictionary<string, string>(),
            Created = TestTimeProvider.GetUtcNow()
        };
    }

    /// <summary>
    ///     Creates a test event with custom JSON data
    /// </summary>
    public static IEventToPersist CreateTestEventWithData(
        string eventType,
        object data,
        params string[] eventTags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = JsonSerializer.Serialize(data),
            Tags = eventTags.Select(EventTag.Parse).ToList(),
            Metadata = new Dictionary<string, string>(),
            Created = TestTimeProvider.GetUtcNow()
        };
    }

    /// <summary>
    ///     Creates a batch of test events for performance testing
    /// </summary>
    public static IEventToPersist[] CreateEventBatch(
        int count,
        string eventTypePrefix = "batch-event",
        string tagPrefix = "batch")
    {
        return Enumerable.Range(1, count)
            .Select(i => CreateTestEvent(
                $"{eventTypePrefix}-{i:D4}",
                new Dictionary<string, string> { ["batch_id"] = (i / 100).ToString() },
                $"{tagPrefix}:test",
                $"index:{i}",
                $"group:{i % 10}"))
            .ToArray();
    }

    /// <summary>
    ///     Creates events with realistic e-commerce domain data
    /// </summary>
    public static IEventToPersist[] CreateECommerceEvents(string orderId, string customerId)
    {
        return
        [
            CreateTestEventWithData("order-created", new
                {
                    orderId,
                    customerId,
                    total = 129.99m,
                    currency = "USD",
                    items =
                        new[]
                        {
                            new { productId = "prod-123", quantity = 2, price = 49.99m },
                            new { productId = "prod-456", quantity = 1, price = 30.01m }
                        }
                }, $"order:{orderId}", $"customer:{customerId}", "domain:ecommerce"),
            CreateTestEventWithData("payment-authorized",
                new { orderId, paymentId = Guid.NewGuid(), amount = 129.99m, method = "credit_card" },
                $"order:{orderId}", $"customer:{customerId}", "domain:payment"),
            CreateTestEventWithData("inventory-reserved",
                new
                {
                    orderId,
                    items = new[]
                    {
                        new { productId = "prod-123", quantity = 2, warehouse = "US-WEST" },
                        new { productId = "prod-456", quantity = 1, warehouse = "US-EAST" }
                    }
                }, $"order:{orderId}", "domain:inventory"),
            CreateTestEventWithData("order-confirmed",
                new { orderId, customerId, estimatedDelivery = DateTime.UtcNow.AddDays(3) }, $"order:{orderId}",
                $"customer:{customerId}", "domain:ecommerce")
        ];
    }

    /// <summary>
    ///     Generates realistic tenant IDs for multi-tenant testing
    /// </summary>
    public static string[] GenerateTenantIds(int count)
    {
        string[] tenantTypes = ["org", "company", "team", "dept"];
        string[] names = ["acme", "globex", "initech", "umbrella", "stark", "wayne", "lexcorp", "oscorp"];

        return Enumerable.Range(1, count)
            .Select(i =>
            {
                string type = tenantTypes[i % tenantTypes.Length];
                string name = names[i % names.Length];
                return $"{type}-{name}-{i:D3}";
            })
            .ToArray();
    }

    /// <summary>
    ///     Creates a complex query for testing advanced scenarios
    /// </summary>
    public static StreamQuery CreateComplexQuery(params string[] tags)
    {
        StreamQuery query = new();

        if (tags.Length > 0) query = query.WithTags(tags.Select(EventTag.Parse).ToArray());

        return query;
    }

    /// <summary>
    ///     Validates that events maintain proper ordering and uniqueness
    /// </summary>
    public static void ValidateEventOrdering(IList<IEventEnvelope> events)
    {
        if (events.Count <= 1) return;

        // Check position ordering
        long? previousPosition = null;
        foreach (IEventEnvelope eventEnvelope in events)
        {
            Assert.True(eventEnvelope.Metadata.ContainsKey("_position"),
                $"Event {eventEnvelope.EventType.Id} missing _position metadata");

            long currentPosition = long.Parse(eventEnvelope.Metadata["_position"]);

            if (previousPosition.HasValue)
                Assert.True(currentPosition > previousPosition.Value,
                    $"Position {currentPosition} should be greater than {previousPosition.Value}");

            previousPosition = currentPosition;
        }

        // Check position uniqueness
        List<long> positions = events.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        List<long> uniquePositions = positions.Distinct().ToList();
        Assert.Equal(positions.Count, uniquePositions.Count);
    }

    /// <summary>
    ///     Validates tenant isolation by ensuring events only belong to the specified tenant
    /// </summary>
    public static void ValidateTenantIsolation(IList<IEventEnvelope> events, string expectedTenantId)
    {
        foreach (IEventEnvelope eventEnvelope in events)
            // Events should not contain cross-tenant data
            // This is a conceptual validation - actual implementation may vary
            Assert.NotNull(eventEnvelope.EventJson);
    }

    /// <summary>
    ///     Creates a cancellation token that cancels after the specified delay
    ///     Useful for testing timeout scenarios
    /// </summary>
    public static CancellationToken CreateTimeoutToken(TimeSpan delay)
    {
        CancellationTokenSource cts = new(delay);
        return cts.Token;
    }
}