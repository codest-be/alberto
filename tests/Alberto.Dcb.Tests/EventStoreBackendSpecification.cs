using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Specification tests for IEventStoreBackend implementations.
/// These tests define the contract that all implementations must follow.
/// </summary>
public abstract class EventStoreBackendSpecification
{
    protected const string DefaultTenant = "test-tenant";

    protected FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero));

    /// <summary>
    /// Factory method to create the backend under test.
    /// </summary>
    protected abstract Task<IEventStoreBackend> CreateBackend();

    /// <summary>
    /// Cleanup method called after each test.
    /// </summary>
    protected virtual Task CleanupAsync() => Task.CompletedTask;

    #region Append Tests

    [Fact]
    public async Task Append_SingleEvent_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", "order:123");

        var result = await backend.Append(DefaultTenant, [eventToPersist]);

        Assert.Single(result);
        Assert.Equal(eventToPersist.Id, result.First().Id);
        Assert.Equal(eventToPersist.EventType.Id, result.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_MultipleEvents_ShouldAssignIncreasingPositions()
    {
        var backend = await CreateBackend();
        var events = new[]
        {
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123"),
            CreateEvent("event-c", "order:123")
        };

        var result = await backend.Append(DefaultTenant, events);

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.Equal(3, positions.Count);
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2]);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_ShouldPreserveEventData()
    {
        var backend = await CreateBackend();
        var eventToPersist = CreateEvent("order-placed", "order:123");

        var result = await backend.Append(DefaultTenant, [eventToPersist]);

        var appended = result.First();
        Assert.Equal(eventToPersist.EventData, appended.EventData);
        Assert.Equal(eventToPersist.Tags.Count, appended.Tags.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_ShouldPreserveMetadata()
    {
        var backend = await CreateBackend();
        var metadata = new Dictionary<string, string>
        {
            ["correlation-id"] = "corr-123",
            ["user-id"] = "user-456"
        };
        var eventToPersist = CreateEvent("order-placed", metadata, "order:123");

        var result = await backend.Append(DefaultTenant, [eventToPersist]);

        var appended = result.First();
        Assert.Equal("corr-123", appended.Metadata["correlation-id"]);
        Assert.Equal("user-456", appended.Metadata["user-id"]);

        await CleanupAsync();
    }

    #endregion

    #region Stream Tests

    [Fact]
    public async Task Stream_EmptyStore_ShouldReturnEmpty()
    {
        var backend = await CreateBackend();
        var query = DcbQuery.ByTags("order:123");

        var result = await backend.Stream(DefaultTenant, query);

        Assert.Empty(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_ByTags_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:123"),
            CreateEvent("customer-updated", "customer:456")
        ]);

        var result = await backend.Stream(DefaultTenant, DcbQuery.ByTags("order:123"));

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Contains(e.Tags, t => t.Value == "order:123"));

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_ByTypes_ShouldReturnMatchingEvents()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [
            CreateEvent("order-placed", "order:123"),
            CreateEvent("order-confirmed", "order:123"),
            CreateEvent("order-placed", "order:456")
        ]);

        var result = await backend.Stream(DefaultTenant, DcbQuery.ByTypes("order-placed"));

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("order-placed", e.EventType.Id));

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_ByTypesOrTags_ShouldReturnUnion()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [
            CreateEvent("order-placed", "order:123"),      // matches type
            CreateEvent("order-confirmed", "order:456"),   // matches tag
            CreateEvent("customer-updated", "customer:789") // matches neither
        ]);

        var query = DcbQuery.Empty
            .WithTypes("order-placed")
            .WithTags(new EventTag("order", "456"));

        var result = await backend.Stream(DefaultTenant, query);

        Assert.Equal(2, result.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_EmptyQuery_ShouldReturnAllTenantEvents()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [
            CreateEvent("event-a", "tag:1"),
            CreateEvent("event-b", "tag:2"),
            CreateEvent("event-c", "tag:3")
        ]);

        var result = await backend.Stream(DefaultTenant, DcbQuery.Empty);

        Assert.Equal(3, result.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithAfterPosition_ShouldFilterByPosition()
    {
        var backend = await CreateBackend();
        var firstBatch = await backend.Append(DefaultTenant, [
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123")
        ]);
        await backend.Append(DefaultTenant, [
            CreateEvent("event-c", "order:123")
        ]);

        var afterPosition = firstBatch.Last().GlobalPosition;
        var result = await backend.Stream(DefaultTenant, DcbQuery.ByTags("order:123"), afterPosition);

        Assert.Single(result);
        Assert.Equal("event-c", result.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_WithLimit_ShouldLimitResults()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123"),
            CreateEvent("event-c", "order:123"),
            CreateEvent("event-d", "order:123"),
            CreateEvent("event-e", "order:123")
        ]);

        var result = await backend.Stream(DefaultTenant, DcbQuery.ByTags("order:123"), limit: 3);

        Assert.Equal(3, result.Count);

        await CleanupAsync();
    }

    [Fact]
    public async Task Stream_ShouldOrderByPosition()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [
            CreateEvent("event-c", "order:123"),
            CreateEvent("event-a", "order:123"),
            CreateEvent("event-b", "order:123")
        ]);

        var result = await backend.Stream(DefaultTenant, DcbQuery.ByTags("order:123"));

        var positions = result.Select(e => e.GlobalPosition).ToList();
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)));

        await CleanupAsync();
    }

    #endregion

    #region Tenant Isolation Tests

    [Fact]
    public async Task Stream_ShouldIsolateTenants()
    {
        var backend = await CreateBackend();
        await backend.Append("tenant-a", [CreateEvent("event-a", "order:123")]);
        await backend.Append("tenant-b", [CreateEvent("event-b", "order:123")]);

        var resultA = await backend.Stream("tenant-a", DcbQuery.ByTags("order:123"));
        var resultB = await backend.Stream("tenant-b", DcbQuery.ByTags("order:123"));

        Assert.Single(resultA);
        Assert.Equal("event-a", resultA.First().EventType.Id);
        Assert.Single(resultB);
        Assert.Equal("event-b", resultB.First().EventType.Id);

        await CleanupAsync();
    }

    [Fact]
    public async Task StreamGlobal_ShouldReturnAllTenantEvents()
    {
        var backend = await CreateBackend();
        await backend.Append("tenant-a", [CreateEvent("event-a", "tag:1")]);
        await backend.Append("tenant-b", [CreateEvent("event-b", "tag:2")]);

        var result = await backend.StreamGlobal();

        Assert.Equal(2, result.Count);

        await CleanupAsync();
    }

    #endregion

    #region DCB Consistency Tests

    [Fact]
    public async Task Append_WithDcbCheck_NoConflict_ShouldSucceed()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(DefaultTenant, [CreateEvent("order-placed", "order:123")]);
        var lastPosition = initial.Last().GlobalPosition;

        var dcbQuery = DcbQuery.ByTags("order:123");
        var result = await backend.Append(
            DefaultTenant,
            [CreateEvent("order-confirmed", "order:123")],
            dcbQuery,
            lastPosition);

        Assert.Single(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithDcbCheck_WithConflict_ShouldThrow()
    {
        var backend = await CreateBackend();
        var initial = await backend.Append(DefaultTenant, [CreateEvent("order-placed", "order:123")]);
        var firstPosition = initial.First().GlobalPosition;

        // Add a conflicting event
        await backend.Append(DefaultTenant, [CreateEvent("order-confirmed", "order:123")]);

        var dcbQuery = DcbQuery.ByTags("order:123");

        // Try to append expecting only the first event
        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(
                DefaultTenant,
                [CreateEvent("order-shipped", "order:123")],
                dcbQuery,
                firstPosition));

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithExisting_ShouldThrow()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [CreateEvent("order-placed", "order:123")]);

        var dcbQuery = DcbQuery.ByTags("order:123");

        // Try to append expecting no events (position 0)
        await Assert.ThrowsAsync<DcbConflictException>(() =>
            backend.Append(
                DefaultTenant,
                [CreateEvent("order-created", "order:123")],
                dcbQuery,
                0));

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithDcbCheck_ExpectingNoEvents_WithNone_ShouldSucceed()
    {
        var backend = await CreateBackend();

        var dcbQuery = DcbQuery.ByTags("order:123");

        var result = await backend.Append(
            DefaultTenant,
            [CreateEvent("order-created", "order:123")],
            dcbQuery,
            0);

        Assert.Single(result);

        await CleanupAsync();
    }

    [Fact]
    public async Task Append_WithDcbCheck_DifferentBoundary_ShouldNotConflict()
    {
        var backend = await CreateBackend();
        await backend.Append(DefaultTenant, [CreateEvent("order-placed", "order:123")]);
        await backend.Append(DefaultTenant, [CreateEvent("customer-updated", "customer:456")]);

        // DCB check only on customer boundary, not order
        var dcbQuery = DcbQuery.ByTags("customer:456");
        var lastCustomerPosition = (await backend.Stream(DefaultTenant, dcbQuery)).Last().GlobalPosition;

        var result = await backend.Append(
            DefaultTenant,
            [CreateEvent("customer-verified", "customer:456")],
            dcbQuery,
            lastCustomerPosition);

        Assert.Single(result);

        await CleanupAsync();
    }

    #endregion

    #region Position Tests

    [Fact]
    public async Task GetLastPosition_EmptyTenant_ShouldReturnZero()
    {
        var backend = await CreateBackend();

        var position = await backend.GetLastPosition(DefaultTenant);

        Assert.Equal(0, position);

        await CleanupAsync();
    }

    [Fact]
    public async Task GetLastPosition_WithEvents_ShouldReturnLastPosition()
    {
        var backend = await CreateBackend();
        var appended = await backend.Append(DefaultTenant, [
            CreateEvent("event-a", "tag:1"),
            CreateEvent("event-b", "tag:2")
        ]);

        var position = await backend.GetLastPosition(DefaultTenant);

        Assert.Equal(appended.Last().GlobalPosition, position);

        await CleanupAsync();
    }

    [Fact]
    public async Task GetLastPositionGlobal_ShouldReturnGlobalMax()
    {
        var backend = await CreateBackend();
        await backend.Append("tenant-a", [CreateEvent("event-a", "tag:1")]);
        var lastAppend = await backend.Append("tenant-b", [CreateEvent("event-b", "tag:2")]);

        var position = await backend.GetLastPositionGlobal();

        Assert.Equal(lastAppend.Last().GlobalPosition, position);

        await CleanupAsync();
    }

    #endregion

    #region Helper Methods

    protected IEventToPersist CreateEvent(string eventType, params string[] tags)
        => CreateEvent(eventType, new Dictionary<string, string>(), tags);

    protected IEventToPersist CreateEvent(string eventType, Dictionary<string, string> metadata, params string[] tags)
    {
        return new EventToPersist
        {
            TenantId = DefaultTenant,
            EventType = new EventType(eventType),
            EventData = """{"test": true}""",
            Tags = tags.Select(EventTag.Parse).ToArray(),
            Metadata = metadata
        };
    }

    #endregion
}
