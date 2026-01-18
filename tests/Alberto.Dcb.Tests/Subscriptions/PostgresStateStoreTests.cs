using Alberto.Dcb.Postgres;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Integration tests for PostgresStateStore.
/// Uses Testcontainers for real PostgreSQL testing.
/// </summary>
public sealed class PostgresStateStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    #region Test State

    public record OrderState
    {
        public Guid OrderId { get; init; }
        public decimal Amount { get; init; }
        public string Status { get; init; } = "";
    }

    #endregion

    #region LoadManyAsync Tests

    [Fact]
    public async Task LoadManyAsync_EmptyIds_ShouldReturnEmptyDictionary()
    {
        var store = CreateStore<OrderState>();

        var result = await store.LoadManyAsync([], ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadManyAsync_NonExistentIds_ShouldReturnEmptyDictionary()
    {
        var store = CreateStore<OrderState>();

        var result = await store.LoadManyAsync(["non-existent-1", "non-existent-2"], ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadManyAsync_ShouldReturnStoredStates()
    {
        var store = CreateStore<OrderState>();
        var orderId = Guid.NewGuid();
        var state = new OrderState { OrderId = orderId, Amount = 100m, Status = "Created" };

        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [orderId.ToString()] = state }, [], ct: TestContext.Current.CancellationToken);

        var result = await store.LoadManyAsync([orderId.ToString()], ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(orderId, result[orderId.ToString()].OrderId);
        Assert.Equal(100m, result[orderId.ToString()].Amount);
        Assert.Equal("Created", result[orderId.ToString()].Status);
    }

    [Fact]
    public async Task LoadManyAsync_ShouldReturnOnlyExistingIds()
    {
        var store = CreateStore<OrderState>();
        var orderId = Guid.NewGuid();
        var state = new OrderState { OrderId = orderId, Amount = 100m, Status = "Created" };

        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [orderId.ToString()] = state }, [], ct: TestContext.Current.CancellationToken);

        var result = await store.LoadManyAsync([orderId.ToString(), "non-existent"], ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Contains(orderId.ToString(), result.Keys);
    }

    #endregion

    #region ApplyChangesAsync Tests

    [Fact]
    public async Task ApplyChangesAsync_ShouldInsertNewState()
    {
        var store = CreateStore<OrderState>();
        var orderId = Guid.NewGuid();
        var state = new OrderState { OrderId = orderId, Amount = 150m, Status = "Pending" };

        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [orderId.ToString()] = state }, [], ct: TestContext.Current.CancellationToken);

        var loaded = await store.LoadManyAsync([orderId.ToString()], ct: TestContext.Current.CancellationToken);
        Assert.Equal(state, loaded[orderId.ToString()]);
    }

    [Fact]
    public async Task ApplyChangesAsync_ShouldUpdateExistingState()
    {
        var store = CreateStore<OrderState>();
        var orderId = Guid.NewGuid();
        var initial = new OrderState { OrderId = orderId, Amount = 100m, Status = "Created" };
        var updated = new OrderState { OrderId = orderId, Amount = 100m, Status = "Confirmed" };

        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [orderId.ToString()] = initial }, [], ct: TestContext.Current.CancellationToken);

        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [orderId.ToString()] = updated }, [], ct: TestContext.Current.CancellationToken);

        var loaded = await store.LoadManyAsync([orderId.ToString()], ct: TestContext.Current.CancellationToken);
        Assert.Equal("Confirmed", loaded[orderId.ToString()].Status);
    }

    [Fact]
    public async Task ApplyChangesAsync_ShouldDeleteState()
    {
        var store = CreateStore<OrderState>();
        var orderId = Guid.NewGuid();
        var state = new OrderState { OrderId = orderId, Amount = 100m, Status = "Created" };

        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [orderId.ToString()] = state }, [], ct: TestContext.Current.CancellationToken);

        await store.ApplyChangesAsync(new Dictionary<string, OrderState>(), [orderId.ToString()], ct: TestContext.Current.CancellationToken);

        var loaded = await store.LoadManyAsync([orderId.ToString()], ct: TestContext.Current.CancellationToken);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task ApplyChangesAsync_ShouldHandleUpsertAndDeleteTogether()
    {
        var store = CreateStore<OrderState>();
        var order1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var state1 = new OrderState { OrderId = order1, Amount = 100m, Status = "Created" };
        var state2 = new OrderState { OrderId = order2, Amount = 200m, Status = "Created" };

        // Insert both
        await store.ApplyChangesAsync(new Dictionary<string, OrderState>
            {
                [order1.ToString()] = state1,
                [order2.ToString()] = state2
            }, [], ct: TestContext.Current.CancellationToken);

        // Upsert order1, delete order2
        var updated1 = state1 with { Status = "Confirmed" };
        await store.ApplyChangesAsync(new Dictionary<string, OrderState> { [order1.ToString()] = updated1 }, [order2.ToString()], ct: TestContext.Current.CancellationToken);

        var loaded = await store.LoadManyAsync([order1.ToString(), order2.ToString()], ct: TestContext.Current.CancellationToken);
        Assert.Single(loaded);
        Assert.Equal("Confirmed", loaded[order1.ToString()].Status);
    }

    [Fact]
    public async Task ApplyChangesAsync_EmptyChanges_ShouldNotThrow()
    {
        var store = CreateStore<OrderState>();

        await store.ApplyChangesAsync(new Dictionary<string, OrderState>(), [], ct: TestContext.Current.CancellationToken);

        // No exception means success
    }

    #endregion

    #region Tenant Isolation Tests

    [Fact]
    public async Task TenantIsolation_ShouldNotCrossContaminate()
    {
        var tenant1Store = CreateStore<OrderState>("tenant-1");
        var tenant2Store = CreateStore<OrderState>("tenant-2");
        var docId = "shared-doc-id";

        var tenant1State = new OrderState { OrderId = Guid.NewGuid(), Amount = 100m, Status = "Tenant1" };
        var tenant2State = new OrderState { OrderId = Guid.NewGuid(), Amount = 200m, Status = "Tenant2" };

        await tenant1Store.ApplyChangesAsync(new Dictionary<string, OrderState> { [docId] = tenant1State }, [], ct: TestContext.Current.CancellationToken);

        await tenant2Store.ApplyChangesAsync(new Dictionary<string, OrderState> { [docId] = tenant2State }, [], ct: TestContext.Current.CancellationToken);

        var loaded1 = await tenant1Store.LoadManyAsync([docId], ct: TestContext.Current.CancellationToken);
        var loaded2 = await tenant2Store.LoadManyAsync([docId], ct: TestContext.Current.CancellationToken);

        Assert.Equal("Tenant1", loaded1[docId].Status);
        Assert.Equal("Tenant2", loaded2[docId].Status);
    }

    [Fact]
    public async Task TenantIsolation_DeleteInOneTenantShouldNotAffectOther()
    {
        var tenant1Store = CreateStore<OrderState>("tenant-a");
        var tenant2Store = CreateStore<OrderState>("tenant-b");
        var docId = "shared-doc-id";

        var state = new OrderState { OrderId = Guid.NewGuid(), Amount = 100m, Status = "Created" };

        await tenant1Store.ApplyChangesAsync(new Dictionary<string, OrderState> { [docId] = state }, [], ct: TestContext.Current.CancellationToken);

        await tenant2Store.ApplyChangesAsync(new Dictionary<string, OrderState> { [docId] = state }, [], ct: TestContext.Current.CancellationToken);

        // Delete from tenant1 only
        await tenant1Store.ApplyChangesAsync(new Dictionary<string, OrderState>(), [docId], ct: TestContext.Current.CancellationToken);

        var loaded1 = await tenant1Store.LoadManyAsync([docId], ct: TestContext.Current.CancellationToken);
        var loaded2 = await tenant2Store.LoadManyAsync([docId], ct: TestContext.Current.CancellationToken);

        Assert.Empty(loaded1);
        Assert.Single(loaded2);
    }

    #endregion

    #region Projection Type Isolation Tests

    public record DifferentState
    {
        public string Value { get; init; } = "";
    }

    [Fact]
    public async Task ProjectionTypeIsolation_SameDocIdDifferentTypes()
    {
        var tenantId = Guid.NewGuid().ToString();
        var orderStore = CreateStore<OrderState>(tenantId, "OrderProjection");
        var otherStore = CreateStore<DifferentState>(tenantId, "OtherProjection");
        var docId = "same-doc-id";

        var orderState = new OrderState { OrderId = Guid.NewGuid(), Amount = 100m, Status = "Order" };
        var otherState = new DifferentState { Value = "Other" };

        await orderStore.ApplyChangesAsync(new Dictionary<string, OrderState> { [docId] = orderState }, [], ct: TestContext.Current.CancellationToken);

        await otherStore.ApplyChangesAsync(new Dictionary<string, DifferentState> { [docId] = otherState }, [], ct: TestContext.Current.CancellationToken);

        var loadedOrder = await orderStore.LoadManyAsync([docId], ct: TestContext.Current.CancellationToken);
        var loadedOther = await otherStore.LoadManyAsync([docId], ct: TestContext.Current.CancellationToken);

        Assert.Equal("Order", loadedOrder[docId].Status);
        Assert.Equal("Other", loadedOther[docId].Value);
    }

    #endregion

    #region Helper Methods

    private PostgresStateStore<TState> CreateStore<TState>(
        string? tenantId = null,
        string? projectionType = null)
    {
        return new PostgresStateStore<TState>(
            fixture.DataSource,
            tenantId ?? Guid.NewGuid().ToString(),
            projectionType);
    }

    #endregion
}
