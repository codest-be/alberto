using Alberto.EventStore.Events;
using Alberto.EventStore.Exceptions;
using Alberto.EventStore.MultiTenant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.EventStore.Tests.Specifications;

/// <summary>
///     Specification tests for IEventStoreBackend implementations
///     These tests define the contract that all implementations must follow
/// </summary>
public abstract class EventStoreBackendSpecification : AdvancedQueryTests
{
    public new TimeProvider TimeProvider { get; } =
        new FakeTimeProvider(new DateTimeOffset(2025, 3, 21, 11, 47, 12, TimeSpan.FromHours(5)));

    /// <summary>
    ///     Factory method to create the backend under test
    ///     Must be implemented by each concrete test class
    /// </summary>
    protected abstract override Task<IEventStoreBackend> CreateBackend();

    protected abstract override Tenant CurrentTenant();

    /// <summary>
    ///     Setup method called before each test
    ///     Override in concrete classes if needed
    /// </summary>
    protected override Task SetupAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Cleanup method called after each test
    ///     Override in concrete classes if needed
    /// </summary>
    protected override Task CleanupAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Append_SingleEvent_ShouldSucceed()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();
        IEventToPersist eventToPersist = CreateTestEvent("test-event", "order:123");

        // Act
        IEnumerable<IEventEnvelope> result = await backend.Append(
            CurrentTenant(),
            [eventToPersist],
            null,
            null,
            TestContext.Current.CancellationToken);

        // Assert
        IEventEnvelope returnedEvent = result.First();
        Assert.Equal(eventToPersist.Id, returnedEvent.Id);
        Assert.Equal(eventToPersist.EventType, returnedEvent.EventType);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_MultipleEvents_ShouldSucceedInOrder()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();
        IEventToPersist[] events =
        [
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123"),
            CreateTestEvent("event-c", "order:123")
        ];

        // Act
        IEnumerable<IEventEnvelope> result =
            await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Assert
        List<IEventEnvelope> returnedEvents = result.ToList();
        for (int i = 0; i < events.Length; i++) Assert.Equal(events[i].Id, returnedEvents[i].Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_DuplicateEventId_ShouldFail()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();
        IEventToPersist eventToPersist = CreateTestEvent("test-event", "order:123");

        // Act
        await backend.Append(CurrentTenant(), [eventToPersist], null, null, TestContext.Current.CancellationToken);

        Task Result()
        {
            return backend.Append(
                CurrentTenant(),
                [eventToPersist],
                null,
                null,
                TestContext.Current.CancellationToken);
        }

        // Assert
        await Assert.ThrowsAsync<ConcurrencyConflictException>(Result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EmptyStore_ShouldReturnEmpty()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();
        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] orderEvents =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("item-added", "order:123", "product:456")
        ];

        IEventToPersist customerEvent = CreateTestEvent("customer-updated", "customer:789");

        await backend.Append(CurrentTenant(), orderEvents, null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [customerEvent], null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("order-updated", "order:123"),
            CreateTestEvent("item-added", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithEventTypes(new EventType("order-created"));

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123"),
            CreateTestEvent("event-c", "order:123"), CreateTestEvent("event-d", "order:123"),
            CreateTestEvent("event-e", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
            CurrentTenant(),
            query,
            3,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, result.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_NoConflict_ShouldSucceed()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist initialEvent = CreateTestEvent("initial-event", "order:123");
        IEnumerable<IEventEnvelope> initialResult = await backend.Append(
            CurrentTenant(),
            [initialEvent],
            null,
            null,
            TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        IEventToPersist newEvent = CreateTestEvent("new-event", "order:123");

        // Act
        IEnumerable<IEventEnvelope> result = await backend.Append(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist initialEvent = CreateTestEvent("initial-event", "order:123");
        await backend.Append(CurrentTenant(), [initialEvent], null, null, TestContext.Current.CancellationToken);

        // Add another event to create a conflict
        IEventToPersist conflictingEvent = CreateTestEvent("conflicting-event", "order:123");
        await backend.Append(CurrentTenant(), [conflictingEvent], null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        IEventToPersist newEvent = CreateTestEvent("new-event", "order:123");

        // Act - expect the initial event but there's now a conflicting event
        Task Result()
        {
            return backend.Append(
                CurrentTenant(),
                [newEvent],
                query,
                initialEvent.Id,
                TestContext.Current.CancellationToken);
        }

        // Assert
        await Assert.ThrowsAsync<ConcurrencyConflictException>(Result); // concurrency conflict expected

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithConsistencyBoundary_ExpectingNoEvents_WithExistingEvents_ShouldFail()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist existingEvent = CreateTestEvent("existing-event", "order:123");
        await backend.Append(CurrentTenant(), [existingEvent], null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        IEventToPersist newEvent = CreateTestEvent("new-event", "order:123");

        // Act - expect no events but there are existing events
        Task Result()
        {
            return backend.Append(
                CurrentTenant(),
                [newEvent],
                query,
                null,
                TestContext.Current.CancellationToken);
        }

        // Assert
        await Assert.ThrowsAsync<ConcurrencyConflictException>(Result); // concurrency conflict expected

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_RequireAllEventTags_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123", "product:456"),
            CreateTestEvent("event-c", "product:456")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"), EventTag.Parse("product:456"))
            .RequiringAllTags();

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        Dictionary<string, string> metadata = new()
        {
            ["correlation-id"] = "correlation-123", ["user-id"] = "user-456"
        };

        IEventToPersist eventToPersist = CreateTestEvent("test-event", metadata, "order:123");

        // Act
        IEnumerable<IEventEnvelope> appendResult = await backend.Append(
            CurrentTenant(),
            [eventToPersist],
            null,
            null,
            TestContext.Current.CancellationToken);

        IReadOnlyCollection<IEventEnvelope> streamResult = await backend.Stream(
            CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("order:123")),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        IEventEnvelope returnedEvent = streamResult.First();
        Assert.Equal("correlation-123", returnedEvent.Metadata["correlation-id"]);
        Assert.Equal("user-456", returnedEvent.Metadata["user-id"]);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EventsOrderedBySequencePosition_ShouldReturnInCorrectOrder()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("event-c", "order:123"), CreateTestEvent("event-a", "order:123"),
            CreateTestEvent("event-b", "order:123")
        ];

        // Act - append events in one batch
        IEnumerable<IEventEnvelope> appendResult =
            await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        IReadOnlyCollection<IEventEnvelope> streamResult = await backend.Stream(
            CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("order:123")),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - events should be ordered by position (append order), not event type
        List<IEventEnvelope> resultList = streamResult.ToList();
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:123"),
            CreateTestEvent("event-c", "order:123")
        ];

        // Act
        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
            CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("order:123")),
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - positions should be monotonically increasing
        List<IEventEnvelope> resultList = result.ToList();
        long? previousPosition = null;

        foreach (IEventEnvelope eventEnvelope in resultList)
        {
            Assert.True(eventEnvelope.Metadata.ContainsKey("_position"),
                $"Event {eventEnvelope.EventType.Id} missing _position metadata. Available keys: {string.Join(", ", eventEnvelope.Metadata.Keys)}");
            long currentPosition = long.Parse(eventEnvelope.Metadata["_position"]);

            if (previousPosition.HasValue)
                Assert.True(currentPosition > previousPosition.Value,
                    $"Position {currentPosition} should be greater than {previousPosition.Value} for event {eventEnvelope.EventType.Id}");

            previousPosition = currentPosition;
        }

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_SequencePositions_ShouldBeUnique()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("event-a", "order:123"), CreateTestEvent("event-b", "order:456"),
            CreateTestEvent("event-c", "order:789")
        ];

        // Act
        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Query each tag separately and combine results to ensure we get all events
        List<IEventEnvelope> results = [];
        foreach (string tag in new[] { "order:123", "order:456", "order:789" })
        {
            IReadOnlyCollection<IEventEnvelope> tagResult = await backend.Stream(
                CurrentTenant(),
                new StreamQuery().WithTags(EventTag.Parse(tag)),
                cancellationToken: TestContext.Current.CancellationToken);
            results.AddRange(tagResult);
        }

        List<IEventEnvelope> result = results;

        // Assert - all positions should be unique
        List<long> positions = result.Select(e =>
        {
            Assert.True(e.Metadata.ContainsKey("_position"),
                $"Event {e.EventType.Id} missing _position metadata. Available keys: {string.Join(", ", e.Metadata.Keys)}");
            return long.Parse(e.Metadata["_position"]);
        }).ToList();
        List<long> uniquePositions = positions.Distinct().ToList();

        Assert.Equal(positions.Count, uniquePositions.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithMultipleEventTypes_ShouldReturnMatchingEvents()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("payment-processed", "order:123"),
            CreateTestEvent("item-shipped", "order:123"), CreateTestEvent("notification-sent", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery()
            .WithEventTypes(new EventType("order-created"), new EventType("payment-processed"))
            .WithTags(EventTag.Parse("order:123"));

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return events matching any of the specified types
        Assert.Equal(2, result.Count);
        List<string> eventTypes = result.Select(e => e.EventType.Id).ToList();
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("payment-processed", "order:123"),
            CreateTestEvent("item-shipped", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123")); // No event type filter

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - should return all events when no event type filter is specified
        Assert.Equal(3, result.Count);
        List<string> eventTypes = result.Select(e => e.EventType.Id).ToList();
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
        IEventStoreBackend backend = await CreateBackend();

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        IEventToPersist newEvent = CreateTestEvent("initial-event", "order:123");

        // Act - expect no events and there are none
        IEnumerable<IEventEnvelope> result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            null,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithComplexConsistencyBoundary_ShouldWork()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        // Create events in different streams
        IEventToPersist orderEvent = CreateTestEvent("order-created", "order:123");
        IEventToPersist paymentEvent = CreateTestEvent("payment-processed", "payment:456");

        await backend.Append(CurrentTenant(), [orderEvent], null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [paymentEvent], null, null, TestContext.Current.CancellationToken);

        // Now append with consistency boundary on the order stream only
        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        IEventToPersist newEvent = CreateTestEvent("order-updated", "order:123");

        // Act
        IEnumerable<IEventEnvelope> result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            orderEvent.Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        // Verify the new event was added
        IReadOnlyCollection<IEventEnvelope> streamResult = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        // Create multiple events in the consistency boundary
        IEventToPersist[] initialEvents =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("order-confirmed", "order:123"),
            CreateTestEvent("payment-processed", "order:123")
        ];

        IEnumerable<IEventEnvelope> initialResult = await backend.Append(CurrentTenant(), initialEvents, null, null,
            TestContext.Current.CancellationToken);
        IEventEnvelope lastEvent = initialResult.Last();

        StreamQuery query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        IEventToPersist newEvent = CreateTestEvent("order-shipped", "order:123");

        // Act - expect the last event from the boundary
        IEnumerable<IEventEnvelope> result = await backend.Append(
            CurrentTenant(),
            [newEvent],
            query,
            lastEvent.Id,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(result);

        // Verify we now have 4 events
        IReadOnlyCollection<IEventEnvelope> streamResult = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        // Create events of different types
        IEventToPersist orderEvent = CreateTestEvent("order-created", "order:123");
        IEventToPersist paymentEvent = CreateTestEvent("payment-processed", "order:123");
        IEventToPersist notificationEvent = CreateTestEvent("notification-sent", "order:123");

        await backend.Append(CurrentTenant(), [orderEvent], null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [paymentEvent], null, null, TestContext.Current.CancellationToken);
        await backend.Append(CurrentTenant(), [notificationEvent], null, null, TestContext.Current.CancellationToken);

        // Create consistency boundary that only considers order events
        StreamQuery query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"));

        IEventToPersist newEvent = CreateTestEvent("order-updated", "order:123");

        // Act - should succeed because the query only looks at order-created events
        IEnumerable<IEventEnvelope> result = await backend.Append(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("payment-processed", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"))
            .RequiringAllEventTypes();

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("payment-processed", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        StreamQuery query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"))
            .WithEventTypes(new EventType("order-created"), new EventType("payment-processed"))
            .RequiringAllEventTypes();

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events =
        [
            CreateTestEvent("order-created", "order:123", "customer:456"),
            CreateTestEvent("payment-processed", "order:123", "payment:789"),
            CreateTestEvent("notification-sent", "customer:456", "notification:abc"),
            CreateTestEvent("order-updated", "order:123")
        ];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Query for order events that also have customer tag
        StreamQuery query = new StreamQuery()
            .WithTags(EventTag.Parse("order:123"), EventTag.Parse("customer:456"))
            .WithEventTypes(new EventType("order-created"))
            .RequiringAllTags();

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
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
        IEventStoreBackend backend = await CreateBackend();

        IEventToPersist[] events = [CreateTestEvent("order-created", "order:123")];

        await backend.Append(CurrentTenant(), events, null, null, TestContext.Current.CancellationToken);

        // Create query with no filters (should match nothing due to implementation)
        StreamQuery query = new();

        // Act
        IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(
            CurrentTenant(),
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert - implementation may return empty or all events depending on design
        // This test verifies it doesn't crash
        Assert.NotNull(result);

        await CleanupAsync();
    }


    [Fact]
    public async Task Concurrency_ParallelAppends_ShouldMaintainConsistency()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        // Act - Simulate concurrent appends from different "clients" (small scale for correctness)
        IEnumerable<Task<IEnumerable<IEventEnvelope>>> tasks = Enumerable.Range(1, 3).Select(async clientId =>
        {
            IEventToPersist[] clientEvents = Enumerable.Range(1, 5)
                .Select(i => CreateTestEvent($"client-{ToLetters(clientId)}-event-{ToLetters(i)}",
                    $"client:{ToLetters(clientId)}", "concurrent:test"))
                .ToArray();

            return await backend.Append(CurrentTenant(), clientEvents, null, null,
                TestContext.Current.CancellationToken);
        });

        IEnumerable<IEventEnvelope>[] results = await Task.WhenAll(tasks);

        // Assert - All appends should succeed
        List<IEventEnvelope> totalEvents = results.SelectMany(r => r).ToList();
        Assert.Equal(15, totalEvents.Count); // 3 clients * 5 events each

        // Verify all events are accessible
        IReadOnlyCollection<IEventEnvelope> streamResult = await backend.Stream(CurrentTenant(),
            new StreamQuery().WithTags(EventTag.Parse("concurrent:test")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(15, streamResult.Count);

        // Verify position uniqueness and ordering
        List<long> positions = streamResult.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        Assert.Equal(positions.Count, positions.Distinct().Count()); // All positions unique
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p))); // Results are ordered

        await CleanupAsync();
    }

    [Fact]
    public async Task Scale_ManyTenants_ShouldIsolateCorrectly()
    {
        // Arrange
        await SetupAsync();
        IEventStoreBackend backend = await CreateBackend();

        // Create events for 5 different tenants (small scale for correctness)
        IEnumerable<Task<Tenant>> tenantTasks = Enumerable.Range(1, 5).Select(async tenantId =>
        {
            Tenant tenant = new(tenantId.ToString());
            IEventToPersist[] tenantEvents = Enumerable.Range(1, 3)
                .Select(i => CreateTestEvent($"tenant-{ToLetters(tenantId)}-event-{ToLetters(i)}", $"tenant:{tenantId}",
                    "scale:test"))
                .ToArray();

            await backend.Append(tenant, tenantEvents, null, null, TestContext.Current.CancellationToken);
            return tenant;
        });

        Tenant[] tenants = await Task.WhenAll(tenantTasks);

        // Act - Verify each tenant only sees their own events
        var verificationTasks = tenants.Select(async tenant =>
        {
            StreamQuery query = new StreamQuery().WithTags(EventTag.Parse($"tenant:{tenant.Id}"));
            IReadOnlyCollection<IEventEnvelope> result = await backend.Stream(tenant, query,
                cancellationToken: TestContext.Current.CancellationToken);
            return new { Tenant = tenant, EventCount = result.Count };
        });

        var verificationResults = await Task.WhenAll(verificationTasks);

        // Assert
        foreach (var result in
                 verificationResults)
            Assert.Equal(3, result.EventCount); // Each tenant should see exactly their 3 events

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

    /// <summary>
    ///     Converts a number to a base-26 letter string (e.g., 0 = A, 1 = B, ..., 25 = Z, 26 = AA, etc.)
    /// </summary>
    private static string ToLetters(int number)
    {
        string result = string.Empty;
        number++;
        while (number > 0)
        {
            number--;
            result = (char)('a' + (number % 26)) + result;
            number /= 26;
        }

        return result;
    }
}