using EventStore;
using EventStore.Events;
using EventStore.Exceptions;
using EventStore.MultiTenant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Eventstore.Tests.Specifications;

/// <summary>
/// Specification tests for IEventStoreBackend implementations
/// These tests define the contract that all implementations must follow
/// </summary>
public abstract class EventStoreBackendSpecification
{
    public TimeProvider TimeProvider { get; } =
        new FakeTimeProvider(new DateTimeOffset(2025, 3, 21, 11, 47, 12, TimeSpan.FromHours(5)));

    /// <summary>
    /// Factory method to create the backend under test
    /// Must be implemented by each concrete test class
    /// </summary>
    protected abstract Task<IEventStoreBackend> CreateBackend();

    protected abstract Tenant CurrentTenant();

    /// <summary>
    /// Setup method called before each test
    /// Override in concrete classes if needed
    /// </summary>
    protected virtual Task SetupAsync() => Task.CompletedTask;

    /// <summary>
    /// Cleanup method called after each test
    /// Override in concrete classes if needed
    /// </summary>
    protected virtual Task CleanupAsync() => Task.CompletedTask;

    [Fact]
    public async Task Append_SingleEvent_ShouldSucceed()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var eventToPersist = CreateTestEvent("test-event", "order:123");

        // Act
        var result = await backend.Append(
            CurrentTenant(),
            [eventToPersist],
            consistencyBoundary: null,
            expectedLastEventId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var returnedEvent = result.First();
        Assert.Equal(eventToPersist.Id, returnedEvent.Id);
        Assert.Equal(eventToPersist.EventType, returnedEvent.EventType);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_MultipleEvents_ShouldSucceedInOrder()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var events = new[]
        {
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123"),
            CreateTestEvent("event-c", "order:123")
        };

        // Act
        var result = await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Assert
        var returnedEvents = result.ToList();
        for (var i = 0; i < events.Length; i++)
        {
            Assert.Equal(events[i].Id, returnedEvents[i].Id);
        }

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_DuplicateEventId_ShouldFail()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var eventToPersist = CreateTestEvent("test-event", "order:123");

        // Act
        await backend.Append(CurrentTenant(), [eventToPersist], null, null, TestContext.Current.CancellationToken);

        Task Result() => backend.Append(
            CurrentTenant(),
            [eventToPersist],
            null,
            null,
            TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<ConcurrencyConflictException>(Result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EmptyStore_ShouldReturnEmpty()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();
        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithEventTagFilter_ShouldReturnMatchingEvents()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var orderEvents = new[]
        {
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("item-added", "order:123", "product:456")
        };

        var customerEvent = CreateTestEvent("customer-updated", "customer:789");

        await backend.Append(CurrentTenant(), orderEvents, null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [customerEvent], null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithEventTypeFilter_ShouldReturnMatchingEvents()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("order-updated", "order:123"),
            CreateTestEvent("item-added", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithEventTypes(new EventType("order-created"));

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(result);
        Assert.Equal("order-created", result.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithMaxCount_ShouldLimitResults()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123"),
            CreateTestEvent("event-c", "order:123"), CreateTestEvent("event-d", "order:123"),
            CreateTestEvent("event-e", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            maxCount: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, result.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_NoConflict_ShouldSucceed()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var initialEvent = CreateTestEvent("initial-event", "order:123");
        var initialResult = await backend.Append(
            CurrentTenant(),
            [initialEvent],
            null,
            null,
            TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        var newEvent = CreateTestEvent("new-event", "order:123");

        // Act
        var result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            initialResult.First().Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_WithConflict_ShouldFail()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var initialEvent = CreateTestEvent("initial-event", "order:123");
        await backend.Append(CurrentTenant(), [initialEvent], null, null, TestContext.Current.CancellationToken);

        // Add another event to create a conflict
        var conflictingEvent = CreateTestEvent("conflicting-event", "order:123");
        await backend.Append(CurrentTenant(), [conflictingEvent], null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        var newEvent = CreateTestEvent("new-event", "order:123");

        // Act - expect the initial event but there's now a conflicting event
        Task Result() =>
            backend.Append(
                CurrentTenant(),
                [newEvent],
                query,
                initialEvent.Id,
                TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<ConcurrencyConflictException>(Result); // concurrency conflict expected

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_ExpectingNoEvents_WithExistingEvents_ShouldFail()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var existingEvent = CreateTestEvent("existing-event", "order:123");
        await backend.Append(CurrentTenant(), [existingEvent], null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        var newEvent = CreateTestEvent("new-event", "order:123");

        // Act - expect no events but there are existing events
        Task Result() =>
            backend.Append(
                CurrentTenant(),
                [newEvent],
                query,
                expectedLastEventId: null,
                cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<ConcurrencyConflictException>(Result); // concurrency conflict expected

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_RequireAllEventTags_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123", "product:456"),
            CreateTestEvent("event-c", "product:456")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"), EventTag.Parse("product:456"))
            .RequiringAllTags();

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(result); // Only event-2 has both identifiers
        Assert.Equal("event-b", result.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Metadata_ShouldBePreserved()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var metadata = new Dictionary<string, string>
        {
            ["correlation-id"] = "correlation-123",
            ["user-id"] = "user-456"
        };

        var eventToPersist = CreateTestEvent("test-event", metadata: metadata, "order:123");

        // Act
        var appendResult = await backend.Append(
            CurrentTenant(),
            [eventToPersist],
            null,
            null,
            TestContext.Current.CancellationToken);

        var streamResult = await backend.Stream(
            CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("order:123")),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var returnedEvent = streamResult.First();
        Assert.Equal("correlation-123", returnedEvent.Metadata["correlation-id"]);
        Assert.Equal("user-456", returnedEvent.Metadata["user-id"]);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EventsOrderedBySequencePosition_ShouldReturnInCorrectOrder()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("event-c", "order:123"),
            CreateTestEvent("event-a", "order:123"),
            CreateTestEvent("event-b", "order:123")
        };

        // Act - append events in one batch
        var appendResult = await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var streamResult = await backend.Stream(
            CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("order:123")),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - events should be ordered by position (append order), not event type
        var resultList = streamResult.ToList();
        Assert.Equal(3, resultList.Count);
        Assert.Equal("event-c", resultList[0].EventType.Id);
        Assert.Equal("event-a", resultList[1].EventType.Id);
        Assert.Equal("event-b", resultList[2].EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_SequencePositions_ShouldBeMonotonicallyIncreasing()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("event-a", "order:123"),
            CreateTestEvent("event-b", "order:123"),
            CreateTestEvent("event-c", "order:123")
        };

        // Act
        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var result = await backend.Stream(
            CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("order:123")),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - positions should be monotonically increasing
        var resultList = result.ToList();
        long? previousPosition = null;

        foreach (var eventEnvelope in resultList)
        {
            Assert.True(eventEnvelope.Metadata.ContainsKey("_position"),
                $"Event {eventEnvelope.EventType.Id} missing _position metadata. Available keys: {string.Join(", ", eventEnvelope.Metadata.Keys)}");
            var currentPosition = long.Parse(eventEnvelope.Metadata["_position"]);

            if (previousPosition.HasValue)
            {
                Assert.True(currentPosition > previousPosition.Value,
                    $"Position {currentPosition} should be greater than {previousPosition.Value} for event {eventEnvelope.EventType.Id}");
            }

            previousPosition = currentPosition;
        }

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_SequencePositions_ShouldBeUnique()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("event-a", "order:123"),
            CreateTestEvent("event-b", "order:456"),
            CreateTestEvent("event-c", "order:789")
        };

        // Act
        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Query each tag separately and combine results to ensure we get all events
        var results = new List<IEventEnvelope>();
        foreach (var tag in new[] { "order:123", "order:456", "order:789" })
        {
            var tagResult = await backend.Stream(
                CurrentTenant(),
                new StreamQuery().WithTags(EventTag.Parse(tag)),
                cancellationToken: TestContext.Current.CancellationToken);
            results.AddRange(tagResult);
        }
        var result = results;

        // Assert - all positions should be unique
        var positions = result.Select(e => {
            Assert.True(e.Metadata.ContainsKey("_position"),
                $"Event {e.EventType.Id} missing _position metadata. Available keys: {string.Join(", ", e.Metadata.Keys)}");
            return long.Parse(e.Metadata["_position"]);
        }).ToList();
        var uniquePositions = positions.Distinct().ToList();

        Assert.Equal(positions.Count, uniquePositions.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithMultipleEventTypes_ShouldReturnMatchingEvents()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("payment-processed", "order:123"),
            CreateTestEvent("item-shipped", "order:123"),
            CreateTestEvent("notification-sent", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery()
            .WithEventTypes(new EventType("order-created"), new EventType("payment-processed"))
            .WithTags(EventTag.Parse("order:123"));

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return events matching any of the specified types
        Assert.Equal(2, result.Count);
        var eventTypes = result.Select(e => e.EventType.Id).ToList();
        Assert.Contains("order-created", eventTypes);
        Assert.Contains("payment-processed", eventTypes);
        Assert.DoesNotContain("item-shipped", eventTypes);
        Assert.DoesNotContain("notification-sent", eventTypes);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithNoEventTypeFilter_ShouldReturnAllEvents()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("payment-processed", "order:123"),
            CreateTestEvent("item-shipped", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123")); // No event type filter

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return all events when no event type filter is specified
        Assert.Equal(3, result.Count);
        var eventTypes = result.Select(e => e.EventType.Id).ToList();
        Assert.Contains("order-created", eventTypes);
        Assert.Contains("payment-processed", eventTypes);
        Assert.Contains("item-shipped", eventTypes);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_ExpectingNoEventsButHaveNone_ShouldSucceed()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        var newEvent = CreateTestEvent("initial-event", "order:123");

        // Act - expect no events and there are none
        var result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            expectedLastEventId: null,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithComplexConsistencyBoundary_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        // Create events in different streams
        var orderEvent = CreateTestEvent("order-created", "order:123");
        var paymentEvent = CreateTestEvent("payment-processed", "payment:456");

        await backend.Append(CurrentTenant(), [orderEvent], null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [paymentEvent], null, null, TestContext.Current.CancellationToken);

        // Now append with consistency boundary on the order stream only
        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        var newEvent = CreateTestEvent("order-updated", "order:123");

        // Act
        var result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            orderEvent.Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        // Verify the new event was added
        var streamResult = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, streamResult.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_MultipleEventsInBoundary_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        // Create multiple events in the consistency boundary
        var initialEvents = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("order-confirmed", "order:123"),
            CreateTestEvent("payment-processed", "order:123")
        };

        var initialResult = await backend.Append(CurrentTenant(), initialEvents, null, null, TestContext.Current.CancellationToken);
        var lastEvent = initialResult.Last();

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        var newEvent = CreateTestEvent("order-shipped", "order:123");

        // Act - expect the last event from the boundary
        var result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            lastEvent.Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        // Verify we now have 4 events
        var streamResult = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, streamResult.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_EventTypesFilter_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        // Create events of different types
        var orderEvent = CreateTestEvent("order-created", "order:123");
        var paymentEvent = CreateTestEvent("payment-processed", "order:123");
        var notificationEvent = CreateTestEvent("notification-sent", "order:123");

        await backend.Append(CurrentTenant(), [orderEvent], null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [paymentEvent], null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [notificationEvent], null, null, TestContext.Current.CancellationToken);

        // Create consistency boundary that only considers order events
        var query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"));

        var newEvent = CreateTestEvent("order-updated", "order:123");

        // Act - should succeed because the query only looks at order-created events
        var result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            orderEvent.Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithRequireAllEventTypes_SingleType_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("payment-processed", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"))
            .RequiringAllEventTypes();

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return only order-created events
        Assert.Single(result);
        Assert.Equal("order-created", result.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithRequireAllEventTypes_MultipleTypes_ShouldReturnEmpty()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123"),
            CreateTestEvent("payment-processed", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        var query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"), new EventType("payment-processed"))
            .RequiringAllEventTypes();

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return empty because single events can't have multiple types
        Assert.Empty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithComplexTagsAndEventTypes_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123", "customer:456"),
            CreateTestEvent("payment-processed", "order:123", "payment:789"),
            CreateTestEvent("notification-sent", "customer:456", "notification:abc"),
            CreateTestEvent("order-updated", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Query for order events that also have customer tag
        var query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"), EventTag.Parse("customer:456"))
            .WithEventTypes(new EventType("order-created"))
            .RequiringAllTags();

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return only the order-created event that has both tags
        Assert.Single(result);
        Assert.Equal("order-created", result.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EmptyQuery_ShouldHandleGracefully()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("order-created", "order:123")
        };

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Create query with no filters (should match nothing due to implementation)
        var query = new StreamQuery();

        // Act
        var result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - implementation may return empty or all events depending on design
        // This test verifies it doesn't crash
        Assert.NotNull(result);

        await CleanupAsync();
    }

    // Helper method to create test events
    private IEventToPersist CreateTestEvent(
        string eventType,
        params string[] tags)
    {
        return CreateTestEvent(eventType, new Dictionary<string, string>(), tags);
    }

    private IEventToPersist CreateTestEvent(
        string eventType,
        Dictionary<string, string>? metadata = null,
        params string[] eventTags)
    {
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = """{"data": "test"}""",
            Tags = eventTags.Select(EventTag.Parse).ToList(),
            Metadata = metadata ?? new Dictionary<string, string>(),
            Created = TimeProvider.GetUtcNow()
        };
    }
}