using EventStore;
using EventStore.Events;
using EventStore.MultiTenant;
using Xunit;

namespace Eventstore.Testing.Specifications;

/// <summary>
/// Specification tests for IEventStoreBackend implementations
/// These tests define the contract that all implementations must follow
/// </summary>
public abstract class EventStoreBackendSpecification
{
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
        await Assert.ThrowsAsync<Exception>(Result);

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
        var result = await backend.Stream(CurrentTenant(), query, cancellationToken: TestContext.Current.CancellationToken);

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
        var result = await backend.Stream(CurrentTenant(), query, cancellationToken: TestContext.Current.CancellationToken);

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
        var result = await backend.Stream(CurrentTenant(), query, cancellationToken: TestContext.Current.CancellationToken);

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
        await Assert.ThrowsAsync<Exception>(Result); // concurrency conflict expected

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
        await Assert.ThrowsAsync<Exception>(Result); // concurrency conflict expected

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EventsAreOrderedByPosition()
    {
        // Arrange
        await SetupAsync();
        var backend = await CreateBackend();

        var events = new[]
        {
            CreateTestEvent("first", "order:123"), CreateTestEvent("second", "order:123"),
            CreateTestEvent("third", "order:123")
        };

        // Append events one by one to ensure ordering
        foreach (var evt in events)
        {
            await backend.Append(CurrentTenant(), [evt], null, null, TestContext.Current.CancellationToken);
        }

        var query = new StreamQuery().WithTags(EventTag.Parse("order:123"));

        // Act
        var result = await backend.Stream(CurrentTenant(), query, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var resultList = result.ToList();
        Assert.Equal(3, resultList.Count);

        // Events should be in the order they were appended
        Assert.Equal("first", resultList[0].EventType.Id);
        Assert.Equal("second", resultList[1].EventType.Id);
        Assert.Equal("third", resultList[2].EventType.Id);

        // Positions should be increasing
        var positions = resultList.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i] > positions[i - 1], "Positions should be increasing");
        }

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
        var result = await backend.Stream(CurrentTenant(), query, cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Contains("_position", returnedEvent.Metadata.Keys); // Should also have position

        await CleanupAsync();
    }

    // Helper method to create test events
    private static IEventToPersist CreateTestEvent(
        string eventType,
        params string[] tags)
    {
        return CreateTestEvent(eventType, new Dictionary<string, string>(), tags);
    }

    private static IEventToPersist CreateTestEvent(
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
        };
    }
}