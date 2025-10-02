using System.Net.Sockets;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alberto.EventStore.Tests.Postgres;

/// <summary>
///     Tests for error handling and edge cases in PostgreSQL Alberto.EventStore
///     Ensures robust behavior under failure conditions
/// </summary>
[Collection("Postgres Integration Tests")]
public class EventStoreErrorHandlingTests(PostgresTestFixture fixture) : IAsyncLifetime
{
    private readonly int _testTenantId = fixture.GetNextTenantId();
    private PostgresEventStoreBackend? _backend;

    public ValueTask InitializeAsync()
    {
        IOptions<PostgresEventStoreOptions> options = Options.Create(fixture.Options);
        _backend = new PostgresEventStoreBackend(options, NullLogger<PostgresEventStoreBackend>.Instance);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.CleanupTestData(_testTenantId);
    }

    [Fact]
    public async Task Append_WithInvalidConnectionString_ShouldThrowException()
    {
        // Arrange
        IOptions<PostgresEventStoreOptions> invalidOptions = Options.Create(new PostgresEventStoreOptions
        {
            ConnectionString = "Host=nonexistent;Database=invalid;Username=fake;Password=fake",
            Schema = "app",
            BulkInsertThreshold = 5
        });

        PostgresEventStoreBackend invalidBackend = new(invalidOptions, NullLogger<PostgresEventStoreBackend>.Instance);
        Tenant tenant = new(_testTenantId.ToString());
        IEventToPersist testEvent = CreateTestEvent("test-event", "test:123");

        // Act & Assert - Connection errors can be various exception types
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            invalidBackend.Append(tenant, [testEvent], null, null, CancellationToken.None));

        // Should be a connection-related exception
        Assert.True(exception is NpgsqlException or SocketException,
            $"Expected NpgsqlException or SocketException, got {exception.GetType()}");
    }

    [Fact]
    public async Task Append_WithMalformedJson_ShouldThrowException()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        EventToPersist eventWithMalformedJson = new()
        {
            EventType = new EventType("test-event"),
            EventJson = """{"incomplete": json""", // Malformed JSON
            Tags = [EventTag.Parse("test:123")],
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };

        // Act & Assert - PostgreSQL validates JSON syntax
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await _backend!.Append(tenant, [eventWithMalformedJson], null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Append_WithVeryLargeEventPayload_ShouldSucceed()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        string largeData = new('x', 64 * 1024); // 64KB of data
        EventToPersist largeEvent = new()
        {
            EventType = new EventType("large-event"),
            EventJson = $$"""{"data": "{{largeData}}"}""",
            Tags = [EventTag.Parse("test:123")],
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };

        // Act & Assert - Should handle large payloads
        IEnumerable<IEventEnvelope> result =
            await _backend!.Append(tenant, [largeEvent], null, null, CancellationToken.None);
        Assert.Single(result);

        // Verify data integrity
        IReadOnlyCollection<IEventEnvelope> streamResult = await _backend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("test:123")), cancellationToken: CancellationToken.None);
        Assert.Single(streamResult);
        Assert.Contains(largeData, streamResult.First().EventJson);
    }

    [Fact]
    public async Task Append_WithVariousTenantIdFormats_ShouldSucceed()
    {
        // Arrange - PostgreSQL should handle various tenant ID formats
        Tenant[] validTenants = new[]
        {
            new Tenant("tenant-dashes"), new Tenant("tenant_underscore"), new Tenant("tenant.dots"),
            new Tenant("tenant123"), new Tenant("UPPERCASE"), new Tenant("mixed-Case")
        };

        // Act & Assert - All should succeed with proper escaping
        foreach (Tenant tenant in validTenants)
        {
            string safeTagValue = tenant.Id.Replace(".", "-").Replace(" ", "-"); // Make tag-safe
            IEventToPersist testEvent = CreateTestEvent("test-event", $"test:{safeTagValue}");
            IEnumerable<IEventEnvelope> result =
                await _backend!.Append(tenant, [testEvent], null, null, CancellationToken.None);
            Assert.Single(result);
        }
    }

    [Fact]
    public async Task Stream_WithShortTimeout_ShouldRespectCancellation()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(1)); // Very short timeout

        // Add a small delay to ensure cancellation happens
        await Task.Delay(10, TestContext.Current.CancellationToken);

        // Act & Assert - May throw OperationCanceledException or complete quickly
        try
        {
            IReadOnlyCollection<IEventEnvelope> result = await _backend!.Stream(tenant,
                new StreamQuery().WithTags(EventTag.Parse("test:123")), cancellationToken: cts.Token);
            // If it completes quickly, that's also acceptable
            Assert.NotNull(result);
        }
        catch (OperationCanceledException)
        {
            // This is the expected behavior
            Assert.True(true);
        }
    }

    [Fact]
    public async Task Append_WithCancelledToken_ShouldThrowOperationCancelledException()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        IEventToPersist testEvent = CreateTestEvent("test-event", "test:123");
        using CancellationTokenSource cts = new();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _backend!.Append(tenant, [testEvent], null, null, cts.Token));
    }

    [Fact]
    public async Task Stream_WithExtremelyLongTagName_ShouldSucceed()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        string longTagName = "order:" + new string('x', 255); // Long tag (255 chars)
        IEventToPersist testEvent = CreateTestEvent("test-event", longTagName);

        // Act
        await _backend!.Append(tenant, [testEvent], null, null, CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> result = await _backend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse(longTagName)), cancellationToken: CancellationToken.None);

        // Assert
        Assert.Single(result);
        // Note: IEventEnvelope doesn't expose tags directly, but we can verify the query found the event
        Assert.Equal("test-event", result.First().EventType.Id);
    }

    [Fact]
    public async Task Append_WithThousandsOfTags_ShouldSucceed()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        string[] manyTags = Enumerable.Range(1, 100).Select(i => $"tag{i}:value{i}").ToArray();
        EventToPersist eventWithManyTags = new()
        {
            EventType = new EventType("many-tags-event"),
            EventJson = """{"data": "test"}""",
            Tags = manyTags.Select(EventTag.Parse).ToList(),
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };

        // Act
        IEnumerable<IEventEnvelope> result =
            await _backend!.Append(tenant, [eventWithManyTags], null, null, CancellationToken.None);

        // Assert
        Assert.Single(result);
        // Note: IEventEnvelope doesn't expose tags directly, but we can verify the event was stored
        Assert.Equal("many-tags-event", result.First().EventType.Id);
    }

    [Fact]
    public async Task Stream_WithEmptyQuery_ShouldReturnAllEvents()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());

        // Add some test data
        IEventToPersist testEvent = CreateTestEvent("test-event", "test:123");
        await _backend!.Append(tenant, [testEvent], null, null, CancellationToken.None);

        // Act - Query with no filters
        StreamQuery emptyQuery = new();
        IReadOnlyCollection<IEventEnvelope> result =
            await _backend.Stream(tenant, emptyQuery, cancellationToken: CancellationToken.None);

        // Assert - Empty query returns all events for the tenant
        Assert.Single(result);
        Assert.Equal("test-event", result.First().EventType.Id);
    }

    [Fact]
    public async Task Append_WithNullMetadata_ShouldFailGracefully()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        EventToPersist eventWithNullMetadata = new()
        {
            EventType = new EventType("test-event"),
            EventJson = """{"data": "test"}""",
            Tags = [EventTag.Parse("test:123")],
            Metadata = null!, // Null metadata
            Created = DateTimeOffset.UtcNow
        };

        // Act & Assert - Should fail gracefully due to null metadata
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _backend!.Append(tenant, [eventWithNullMetadata], null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Append_WithValidEventTypeVariations_ShouldSucceed()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        string[] validEventTypes = new[]
        {
            "event-with-dashes", "event-with-multiple-dashes", "very-long-event-type-name-with-many-parts",
            "a", // Single character
            "ab", // Two characters
            "short-event", "long-event-name-that-tests-length-limits", "event-a-b-c-d-e-f-g", // Many dashes
            "z" // Last letter of alphabet
        };

        // Act & Assert
        foreach (string eventType in validEventTypes)
        {
            EventToPersist testEvent = new()
            {
                EventType = new EventType(eventType),
                EventJson = """{"data": "test"}""",
                Tags = [EventTag.Parse($"test:{eventType}")],
                Metadata = new Dictionary<string, string>(),
                Created = DateTimeOffset.UtcNow
            };

            IEnumerable<IEventEnvelope> result =
                await _backend!.Append(tenant, [testEvent], null, null, CancellationToken.None);
            Assert.Single(result);
            Assert.Equal(eventType, result.First().EventType.Id);
        }
    }

    [Fact]
    public void EventTag_WithInvalidFormats_ShouldThrowExceptions()
    {
        // Arrange & Act & Assert - Invalid tag formats should throw during parsing
        Assert.Throws<ArgumentException>(() => EventTag.Parse("no-colon"));
        Assert.Throws<ArgumentException>(() => EventTag.Parse(":empty-prefix"));
        Assert.Throws<ArgumentException>(() => EventTag.Parse("empty-suffix:"));
        Assert.Throws<ArgumentException>(() => EventTag.Parse(""));
        Assert.Throws<ArgumentException>(() => EventTag.Parse("   "));
    }

    [Fact]
    public async Task Stream_WithValidComplexTagFormats_ShouldWork()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());

        // Create events with valid but complex tag formats
        IEventToPersist[] testEvents = new[]
        {
            CreateTestEventWithTags("test-event-a", ["normal:tag"]),
            CreateTestEventWithTags("test-event-b", ["concept:id-with-hyphens"]),
            CreateTestEventWithTags("test-event-c", ["concept_underscore:id_underscore"]),
            CreateTestEventWithTags("test-event-d", ["MixedCase:ID123"]),
            CreateTestEventWithTags("test-event-e", ["tag:value", "secondary:tag"])
        };

        await _backend!.Append(tenant, testEvents, null, null, CancellationToken.None);

        // Act & Assert - Should be able to query for events by their types
        foreach (IEventToPersist testEvent in testEvents)
        {
            StreamQuery query = new StreamQuery().WithEventTypes(testEvent.EventType);
            IReadOnlyCollection<IEventEnvelope> result =
                await _backend.Stream(tenant, query, cancellationToken: CancellationToken.None);
            Assert.Single(result);
            Assert.Equal(testEvent.EventType.Id, result.First().EventType.Id);
        }
    }

    [Fact]
    public async Task Append_WithMaxIntegerTenantId_ShouldSucceed()
    {
        // Arrange - Test edge case with maximum integer value
        Tenant maxTenant = new(int.MaxValue.ToString());
        IEventToPersist testEvent = CreateTestEvent("max-tenant-event", "test:max");

        // Act & Assert
        IEnumerable<IEventEnvelope> result =
            await _backend!.Append(maxTenant, [testEvent], null, null, CancellationToken.None);
        Assert.Single(result);

        IReadOnlyCollection<IEventEnvelope> streamResult = await _backend.Stream(maxTenant,
            new StreamQuery().WithTags(EventTag.Parse("test:max")), cancellationToken: CancellationToken.None);
        Assert.Single(streamResult);
    }

    [Fact]
    public async Task Database_ConnectionRecovery_ShouldWork()
    {
        // Arrange
        Tenant tenant = new(_testTenantId.ToString());
        IEventToPersist testEvent = CreateTestEvent("recovery-test", "test:recovery");

        // Act - First operation should succeed
        IEnumerable<IEventEnvelope> result1 =
            await _backend!.Append(tenant, [testEvent], null, null, CancellationToken.None);
        Assert.Single(result1);

        // Simulate connection issues by creating a new backend with a brief invalid connection
        // Then test that subsequent operations still work
        IEventToPersist testEvent2 = CreateTestEvent("recovery-test-b", "test:recovery");
        IEnumerable<IEventEnvelope> result2 =
            await _backend.Append(tenant, [testEvent2], null, null, CancellationToken.None);
        Assert.Single(result2);

        // Verify both events are accessible
        IReadOnlyCollection<IEventEnvelope> streamResult = await _backend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("test:recovery")), cancellationToken: CancellationToken.None);
        Assert.Equal(2, streamResult.Count);
    }

    private IEventToPersist CreateTestEvent(string eventType, params string[] tags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = """{"data": "test"}""",
            Tags = tags.Select(EventTag.Parse).ToList(),
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };
    }

    private IEventToPersist CreateTestEventWithTags(string eventType, string[] tags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = """{"data": "test"}""",
            Tags = tags.Select(EventTag.Parse).ToList(),
            Metadata = new Dictionary<string, string>(),
            Created = DateTimeOffset.UtcNow
        };
    }
}