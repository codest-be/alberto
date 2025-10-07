using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Postgres;
using Dapper;
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
        PostgresEventStoreBackend ordersBackend = CreateBackendForSchema("orders");
        PostgresEventStoreBackend paymentsBackend = CreateBackendForSchema("payments");
        Tenant tenant = new(_testTenantId.ToString());

        IEventToPersist orderEvent = CreateTestEvent("order-created", "order:123");
        IEventToPersist paymentEvent = CreateTestEvent("payment-processed", "payment:456");

        // Act - Add events to different schemas
        await ordersBackend.Append(tenant, [orderEvent], null, null, CancellationToken.None);
        await paymentsBackend.Append(tenant, [paymentEvent], null, null, CancellationToken.None);

        // Assert - Events should be isolated by schema
        StreamQuery orderQuery = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        StreamQuery paymentQuery = new StreamQuery().WithTags(EventTag.Parse("payment:456"));

        IReadOnlyCollection<IEventEnvelope> ordersResult =
            await ordersBackend.Stream(tenant, orderQuery, cancellationToken: CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> paymentsResult =
            await paymentsBackend.Stream(tenant, paymentQuery, cancellationToken: CancellationToken.None);

        // Orders schema should only see order events
        Assert.Single(ordersResult);
        Assert.Equal("order-created", ordersResult.First().EventType.Id);

        // Payments schema should only see payment events
        Assert.Single(paymentsResult);
        Assert.Equal("payment-processed", paymentsResult.First().EventType.Id);

        // Cross-schema queries should return empty
        IReadOnlyCollection<IEventEnvelope> ordersPaymentQuery =
            await ordersBackend.Stream(tenant, paymentQuery, cancellationToken: CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> paymentsOrderQuery =
            await paymentsBackend.Stream(tenant, orderQuery, cancellationToken: CancellationToken.None);

        Assert.Empty(ordersPaymentQuery);
        Assert.Empty(paymentsOrderQuery);
    }

    [Fact]
    public async Task SameEventContent_DifferentSchemas_ShouldAllowSeparateStorage()
    {
        // Arrange
        PostgresEventStoreBackend ordersBackend = CreateBackendForSchema("orders");
        PostgresEventStoreBackend paymentsBackend = CreateBackendForSchema("payments");
        Tenant tenant = new(_testTenantId.ToString());

        Guid testGuid = Guid.NewGuid();
        IEventToPersist orderEvent = CreateTestEventWithId(testGuid, "business-event", "order:123");
        IEventToPersist paymentEvent = CreateTestEventWithId(testGuid, "business-event", "payment:456");

        // Act & Assert - Same logical event should be allowed in different schemas
        await ordersBackend.Append(tenant, [orderEvent], null, null, CancellationToken.None);
        await paymentsBackend.Append(tenant, [paymentEvent], null, null, CancellationToken.None);

        // Both events should exist in their respective schemas
        IReadOnlyCollection<IEventEnvelope> ordersResult = await ordersBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("order:123")), cancellationToken: CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> paymentsResult = await paymentsBackend.Stream(tenant,
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
        PostgresEventStoreBackend backend = CreateBackendForSchema("orders");
        Tenant tenant1 = new(_testTenantId.ToString());
        Tenant tenant2 = new((_testTenantId + 1).ToString());

        IEventToPersist tenant1Event = CreateTestEvent("order-created", "order:123");
        IEventToPersist tenant2Event = CreateTestEvent("order-created", "order:456");

        // Act
        await backend.Append(tenant1, [tenant1Event], null, null, CancellationToken.None);
        await backend.Append(tenant2, [tenant2Event], null, null, CancellationToken.None);

        // Assert - Each tenant should only see their own events
        StreamQuery tenant1Query = new StreamQuery().WithTags(EventTag.Parse("order:123"));
        StreamQuery tenant2Query = new StreamQuery().WithTags(EventTag.Parse("order:456"));

        IReadOnlyCollection<IEventEnvelope> tenant1Result =
            await backend.Stream(tenant1, tenant1Query, cancellationToken: CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> tenant2Result =
            await backend.Stream(tenant2, tenant2Query, cancellationToken: CancellationToken.None);

        Assert.Single(tenant1Result);
        Assert.Single(tenant2Result);

        // Cross-tenant queries should return empty
        IReadOnlyCollection<IEventEnvelope> tenant1CrossQuery =
            await backend.Stream(tenant1, tenant2Query, cancellationToken: CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> tenant2CrossQuery =
            await backend.Stream(tenant2, tenant1Query, cancellationToken: CancellationToken.None);

        Assert.Empty(tenant1CrossQuery);
        Assert.Empty(tenant2CrossQuery);
    }

    [Fact]
    public async Task SequencePositions_ShouldBeSchemaSpecific()
    {
        // Arrange
        PostgresEventStoreBackend ordersBackend = CreateBackendForSchema("orders");
        PostgresEventStoreBackend paymentsBackend = CreateBackendForSchema("payments");
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
        IReadOnlyCollection<IEventEnvelope> ordersResult = await ordersBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("order:123")), cancellationToken: CancellationToken.None);
        IReadOnlyCollection<IEventEnvelope> paymentsResult = await paymentsBackend.Stream(tenant,
            new StreamQuery().WithTags(EventTag.Parse("payment:456")), cancellationToken: CancellationToken.None);

        List<long> orderPositions = ordersResult.Select(e => long.Parse(e.Metadata["_position"])).ToList();
        List<long> paymentPositions = paymentsResult.Select(e => long.Parse(e.Metadata["_position"])).ToList();

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
        PostgresEventStoreBackend invalidBackend = CreateBackendForSchema("nonexistent");
        Tenant tenant = new(_testTenantId.ToString());
        IEventToPersist testEvent = CreateTestEvent("test-event", "test:123");

        // Act & Assert - Should throw appropriate exception
        await Assert.ThrowsAsync<PostgresException>(() =>
            invalidBackend.Append(tenant, [testEvent], null, null, CancellationToken.None));
    }

    private async Task CreateTestSchemas()
    {
        await using NpgsqlConnection connection = new(fixture.Options.ConnectionString);
        await connection.OpenAsync();

        // Create schemas
        await connection.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS orders");
        await connection.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS payments");

        // Run migrations for each schema
        string migrationSql = await LoadMigrationFromFile();

        await connection.ExecuteAsync($"SET search_path TO orders, public;\n\n{migrationSql}");
        await connection.ExecuteAsync($"SET search_path TO payments, public;\n\n{migrationSql}");
    }

    private PostgresEventStoreBackend CreateBackendForSchema(string schema)
    {
        IOptions<PostgresEventStoreOptions> options = Options.Create(new PostgresEventStoreOptions
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

    private async Task<string> LoadMigrationFromFile()
    {
        string currentDirectory = AppContext.BaseDirectory;
        DirectoryInfo? solutionDirectory = Directory.GetParent(currentDirectory);

        while (solutionDirectory != null &&
               !Directory.Exists(Path.Combine(solutionDirectory.FullName, "src", "EventStore",
                   "Alberto.EventStore.Postgres")))
            solutionDirectory = solutionDirectory.Parent;

        if (solutionDirectory == null)
            throw new DirectoryNotFoundException(
                "Could not locate src/EventStore/Alberto.EventStore.Postgres directory");

        string migrationPath = Path.Combine(solutionDirectory.FullName, "src", "EventStore",
            "Alberto.EventStore.Postgres", "Migrations",
            "CreateEventStoreSchema.sql");
        return await File.ReadAllTextAsync(migrationPath);
    }
}