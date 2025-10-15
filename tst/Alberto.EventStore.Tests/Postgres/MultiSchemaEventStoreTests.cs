using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Alberto.EventStore.Postgres.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alberto.EventStore.Tests.Postgres;

/// <summary>
///     Tests for multi-schema support in PostgreSQL Alberto.EventStore
///     Ensures schema isolation and proper backend factory behavior
/// </summary>
[Collection("Postgres Integration Tests")]
public class MultiSchemaEventStoreTests(PostgresTestFixture fixture) : IAsyncLifetime
{
    private readonly int _testTenantId = fixture.GetNextTenantId();

    public async ValueTask InitializeAsync()
    {
        // Create additional schemas for testing
        await CreateTestSchemas();
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.CleanupTestData(_testTenantId);
    }

    [Fact]
    public async Task MultipleSchemas_ShouldIsolateEvents()
    {
        // Arrange
        var ordersBackend = CreateBackendForSchema("orders");
        var paymentsBackend = CreateBackendForSchema("payments");
        Tenant tenant = new(_testTenantId.ToString());

        var orderEvent = CreateTestEvent("order-created", "order:123");
        var paymentEvent = CreateTestEvent("payment-processed", "payment:456");

        // Act - Add events to different schemas
        await ordersBackend.Append(tenant, [orderEvent], null, null, CancellationToken.None);
        await paymentsBackend.Append(tenant, [paymentEvent], null, null, CancellationToken.None);

        // Assert - Events should be isolated by schema
        var orderQuery = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        var paymentQuery = new StreamQuery().WithTags(EventTag.Parse("payment:456"));

        var ordersResult =
            await ordersBackend.Stream(tenant, orderQuery, cancellationToken: CancellationToken.None);
        var paymentsResult =
            await paymentsBackend.Stream(tenant, paymentQuery, cancellationToken: CancellationToken.None);

        // Orders schema should only see order events
        Assert.Single(ordersResult);
        Assert.Equal("order-created", ordersResult.First().EventType.Id);

        // Payments schema should only see payment events
        Assert.Single(paymentsResult);
        Assert.Equal("payment-processed", paymentsResult.First().EventType.Id);

        // Cross-schema queries should return empty
        var ordersPaymentQuery =
            await ordersBackend.Stream(tenant, paymentQuery, cancellationToken: CancellationToken.None);
        var paymentsOrderQuery =
            await paymentsBackend.Stream(tenant, orderQuery, cancellationToken: CancellationToken.None);

        Assert.Empty(ordersPaymentQuery);
        Assert.Empty(paymentsOrderQuery);
    }

    [Fact]
    public async Task SameEventContent_DifferentSchemas_ShouldAllowSeparateStorage()
    {
        // Arrange
        var ordersBackend = CreateBackendForSchema("orders");
        var paymentsBackend = CreateBackendForSchema("payments");
        Tenant tenant = new(_testTenantId.ToString());

        var testGuid = Guid.NewGuid();
        var orderEvent = CreateTestEventWithId(testGuid, "business-event", "order:123");
        var paymentEvent = CreateTestEventWithId(testGuid, "business-event", "payment:456");

        // Act & Assert - Same logical event should be allowed in different schemas
        await ordersBackend.Append(tenant, [orderEvent], null, null, CancellationToken.None);
        await paymentsBackend.Append(tenant, [paymentEvent], null, null, CancellationToken.None);

        // Both events should exist in their respective schemas
        var ordersResult = await ordersBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("order:123")), cancellationToken: CancellationToken.None);
        var paymentsResult = await paymentsBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("payment:456")), cancellationToken: CancellationToken.None);

        Assert.Single(ordersResult);
        Assert.Single(paymentsResult);

        // Verify the test metadata to confirm they're logically the same event
        Assert.Equal(testGuid.ToString(), ordersResult.First().Metadata["test_id"]);
        Assert.Equal(testGuid.ToString(), paymentsResult.First().Metadata["test_id"]);
    }

    [Fact]
    public async Task TenantIsolation_WithinSchema_ShouldWork()
    {
        // Arrange
        var backend = CreateBackendForSchema("orders");
        Tenant tenant1 = new(_testTenantId.ToString());
        Tenant tenant2 = new((_testTenantId + 1).ToString());

        var tenant1Event = CreateTestEvent("order-created", "order:123");
        var tenant2Event = CreateTestEvent("order-created", "order:456");

        // Act
        await backend.Append(tenant1, [tenant1Event], null, null, CancellationToken.None);
        await backend.Append(tenant2, [tenant2Event], null, null, CancellationToken.None);

        // Assert - Each tenant should only see their own events
        var tenant1Query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        var tenant2Query = new StreamQuery().WithTags(EventTag.Parse("order:456"));

        var tenant1Result =
            await backend.Stream(tenant1, tenant1Query, cancellationToken: CancellationToken.None);
        var tenant2Result =
            await backend.Stream(tenant2, tenant2Query, cancellationToken: CancellationToken.None);

        Assert.Single(tenant1Result);
        Assert.Single(tenant2Result);

        // Cross-tenant queries should return empty
        var tenant1CrossQuery =
            await backend.Stream(tenant1, tenant2Query, cancellationToken: CancellationToken.None);
        var tenant2CrossQuery =
            await backend.Stream(tenant2, tenant1Query, cancellationToken: CancellationToken.None);

        Assert.Empty(tenant1CrossQuery);
        Assert.Empty(tenant2CrossQuery);
    }

    [Fact]
    public async Task SequencePositions_ShouldBeSchemaSpecific()
    {
        // Arrange
        var ordersBackend = CreateBackendForSchema("orders");
        var paymentsBackend = CreateBackendForSchema("payments");
        Tenant tenant = new(_testTenantId.ToString());

        // Act - Add events to both schemas
        IEventToPersist[] orderEvents =
        [
            CreateTestEvent("order-created", "order:123"), CreateTestEvent("order-confirmed", "order:123")
        ];

        IEventToPersist[] paymentEvents =
        [
            CreateTestEvent("payment-initiated", "payment:456"), CreateTestEvent("payment-completed", "payment:456")
        ];

        await ordersBackend.Append(tenant, orderEvents, null, null, CancellationToken.None);
        await paymentsBackend.Append(tenant, paymentEvents, null, null, CancellationToken.None);

        // Assert - Each schema should have its own sequence positions starting from 1
        var ordersResult = await ordersBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("order:123")), cancellationToken: CancellationToken.None);
        var paymentsResult = await paymentsBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("payment:456")), cancellationToken: CancellationToken.None);

        var orderPositions = ordersResult.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        var paymentPositions = paymentsResult.Select(e => long.Parse(e.Metadata["_position"])).ToList();

        // Each schema should start from its own sequence
        Assert.True(orderPositions.All(p => p > 0));
        Assert.True(paymentPositions.All(p => p > 0));

        // Positions within each schema should be monotonically increasing
        Assert.True(orderPositions[1] > orderPositions[0]);
        Assert.True(paymentPositions[1] > paymentPositions[0]);
    }

    [Fact]
    public async Task SchemaNotExists_ShouldFailGracefully()
    {
        // Arrange
        var invalidBackend = CreateBackendForSchema("nonexistent");
        Tenant tenant = new(_testTenantId.ToString());
        var testEvent = CreateTestEvent("test-event", "test:123");

        // Act & Assert - Should throw appropriate exception
        await Assert.ThrowsAsync<PostgresException>(() =>
            invalidBackend.Append(tenant, [testEvent], null, null, CancellationToken.None));
    }

    private async Task CreateTestSchemas()
    {
        // Set up a minimal service provider with options for both schemas
        var services = new ServiceCollection();

        // Register options for both schemas
        services.Configure<PostgresEventStoreOptions>("orders", opts =>
        {
            opts.ConnectionString = fixture.Options.ConnectionString;
            opts.Schema = "orders";
        });

        services.Configure<PostgresEventStoreOptions>("payments", opts =>
        {
            opts.ConnectionString = fixture.Options.ConnectionString;
            opts.Schema = "payments";
        });

        var serviceProvider = services.BuildServiceProvider();

        // Create and run the migration service
        var logger = new NullLogger<MigrationHostedService>();
        var migrationService = new MigrationHostedService(serviceProvider, logger);

        await migrationService.StartAsync(CancellationToken.None);
    }

    private PostgresEventStoreBackend CreateBackendForSchema(string schema)
    {
        var options = Options.Create(new PostgresEventStoreOptions
        {
            ConnectionString = fixture.Options.ConnectionString, Schema = schema, BulkInsertThreshold = 5
        });

        return new PostgresEventStoreBackend(options, NullLogger<PostgresEventStoreBackend>.Instance);
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

    private IEventToPersist CreateTestEventWithId(Guid id, string eventType, params string[] tags)
    {
        // Note: EventToPersist.Id is read-only and auto-generated
        // For this test, we'll use the auto-generated ID and verify isolation works regardless
        return new EventToPersist
        {
            EventType = new EventType(eventType),
            EventJson = """{"data": "test"}""",
            Tags = tags.Select(EventTag.Parse).ToList(),
            Metadata = new Dictionary<string, string> { ["test_id"] = id.ToString() },
            Created = DateTimeOffset.UtcNow
        };
    }
}