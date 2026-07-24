using System.Text.Json;
using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Alberto.Dcb.Tests.Tenancy;

/// <summary>
/// Shared multi-tenant PostgreSQL fixture. Runs the multi-tenant migration
/// (without <c>singleTenant: true</c>) and wires up a DI service provider via
/// <see cref="DcbModuleBuilder"/> with tenancy enabled so that
/// <c>TenantEventStoreDecorator</c> is in the request-scoped backend chain.
/// </summary>
public sealed class MultiTenantPostgresFixture : IAsyncLifetime
{
    private const string ModuleKey = "ti-test";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// DI service provider that owns the keyed <see cref="IEventStore"/> backed by the
    /// multi-tenant Postgres backend. Each test creates its own scope to set the tenant.
    /// </summary>
    public IServiceProvider Services { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var migrationResult = PostgresMigrator.Migrate(ConnectionString);
        if (!migrationResult.Successful)
        {
            throw new InvalidOperationException(
                $"Multi-tenant database migration failed: {migrationResult.Error?.Message}",
                migrationResult.Error);
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
        Services = BuildServiceProvider();
    }

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        // DcbModuleBuilder has an internal constructor; Alberto.Dcb exposes it to
        // Alberto.Dcb.Tests via InternalsVisibleTo so we can wire tenancy in tests.
        var builder = new DcbModuleBuilder(services, ModuleKey);
        builder.WithTenancy().WithPostgres(opts =>
        {
            opts.ConnectionString = ConnectionString;
            opts.AutoMigrate = false;         // fixture already migrated
            opts.EnableNotifyListener = false; // no background postgres listener in tests
        });
        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose the SP first so it can clean up its own NpgsqlDataSource (owned by
        // WithPostgres), then dispose the fixture's standalone DataSource, then the container.
        if (Services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();

        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// Integration tests for multi-tenant event isolation. Covers:
/// <list type="bullet">
/// <item>TEST-5: <c>TenantEventStoreDecorator</c> routing and cross-tenant event isolation.</item>
/// <item>P1.3: <c>StreamAllAsync</c> guard on the request-scoped decorator.</item>
/// <item>P0.3: Tenant-scoped <c>PostgresStateStore</c> cross-tenant state isolation.</item>
/// <item>P1.2: <c>PostgresDeadLetterStore</c> in multi-tenant mode — storage and retry isolation.</item>
/// </list>
/// </summary>
public sealed class TenantIsolationTests(MultiTenantPostgresFixture fixture)
    : IClassFixture<MultiTenantPostgresFixture>
{
    private const string ModuleKey = "ti-test";
    private const string TenantA = "tenanta";
    private const string TenantB = "tenantb";

    [EventType("ti-order-created")]
    private sealed record OrderCreated(Guid OrderId) : IEvent;

    private sealed record OrderState(string Label, decimal Amount);

    // ─── helpers ────────────────────────────────────────────────────────────

    private static EventToPersist CreateEvent<TEvent>(TEvent @event, EventTag? tag = null)
        where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = tag.HasValue ? [tag.Value] : [],
            EventData = JsonSerializer.Serialize(@event),
        };
    }

    /// <summary>
    /// Creates an async DI scope and immediately sets the active tenant on the
    /// <see cref="TenantContext"/> — the caller is responsible for disposing the scope.
    /// </summary>
    private AsyncServiceScope CreateScopeForTenant(string tenantId)
    {
        var scope = fixture.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        return scope;
    }

    // ─── TEST-5: TenantEventStoreDecorator routing ─────────────────────────

    /// <summary>
    /// Events appended by tenant A must not appear when tenant B streams by the same tag.
    /// Verifies that <c>TenantEventStoreDecorator</c> scopes both writes and reads to the
    /// active tenant from the request-scoped <see cref="TenantContext"/>.
    /// </summary>
    [Fact]
    public async Task TenantA_events_are_not_visible_to_TenantB()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var tag = new EventTag("ti-order", orderId.ToString());

        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeA.AppendAsync(
                [CreateEvent(new OrderCreated(orderId), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeB = CreateScopeForTenant(TenantB))
        {
            var storeB = scopeB.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var events = await storeB.StreamAsync(DcbQuery.ByTags(tag.Value), cancellationToken: ct);

            Assert.Empty(events);
        }
    }

    /// <summary>
    /// When both tenants append events under the same logical tag, each tenant's stream
    /// must return only its own events and each event envelope must carry the correct
    /// <see cref="IEventEnvelope.TenantId"/>.
    /// </summary>
    [Fact]
    public async Task TenantA_stream_returns_only_tenantA_events_not_tenantB()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var tag = new EventTag("ti-shared", orderId.ToString());

        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeA.AppendAsync(
                [CreateEvent(new OrderCreated(orderId), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeB = CreateScopeForTenant(TenantB))
        {
            var storeB = scopeB.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeB.AppendAsync(
                [CreateEvent(new OrderCreated(orderId), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeA2 = CreateScopeForTenant(TenantA))
        {
            var storeA2 = scopeA2.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var events = await storeA2.StreamAsync(DcbQuery.ByTags(tag.Value), cancellationToken: ct);

            var ev = Assert.Single(events);
            Assert.Equal(TenantA, ev.TenantId);
        }
    }

    // ─── P1.3: StreamAll guard ──────────────────────────────────────────────

    /// <summary>
    /// <see cref="IEventStore.StreamAllAsync"/> must throw <see cref="InvalidOperationException"/>
    /// when a request-scoped tenant is active. The guard in <c>TenantEventStoreDecorator</c>
    /// prevents cross-tenant data leaks from calls that would otherwise return all tenants' events.
    /// </summary>
    [Fact]
    public async Task StreamAllAsync_throws_InvalidOperationException_when_request_tenant_is_active()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var scopeA = CreateScopeForTenant(TenantA);
        var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storeA.StreamAllAsync(cancellationToken: ct));
    }

    // ─── P0.3: PostgresStateStore tenant isolation ──────────────────────────

    /// <summary>
    /// State written by tenant A must not be readable by a state store instance scoped to
    /// tenant B with the same <c>projectionType</c> and document ID.
    /// </summary>
    [Fact]
    public async Task StateStore_tenantA_writes_are_not_visible_to_tenantB()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = $"ti-proj-{Guid.NewGuid():N}";
        var docId = Guid.NewGuid().ToString();

        var storeA = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType: projectionType,
            tenantId: TenantA);

        var storeB = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType: projectionType,
            tenantId: TenantB);

        await storeA.ApplyChangesAsync(
            new Dictionary<string, OrderState> { [docId] = new OrderState("tenant-a-order", 42.50m) },
            [],
            ct: ct);

        var resultB = await storeB.LoadManyAsync([docId], ct: ct);
        Assert.Empty(resultB);
    }

    /// <summary>
    /// A write from tenant B under the same document ID must not overwrite or shadow tenant A's
    /// document; the <c>tenant_id</c> column must be part of the primary key.
    /// </summary>
    [Fact]
    public async Task StateStore_tenantB_writes_do_not_overwrite_tenantA()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = $"ti-proj-{Guid.NewGuid():N}";
        var docId = Guid.NewGuid().ToString();

        var storeA = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType: projectionType,
            tenantId: TenantA);

        var storeB = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType: projectionType,
            tenantId: TenantB);

        await storeA.ApplyChangesAsync(
            new Dictionary<string, OrderState> { [docId] = new OrderState("original", 10m) },
            [],
            ct: ct);

        await storeB.ApplyChangesAsync(
            new Dictionary<string, OrderState> { [docId] = new OrderState("overwritten", 99m) },
            [],
            ct: ct);

        var resultA = await storeA.LoadManyAsync([docId], ct: ct);
        Assert.Equal("original", resultA[docId].Label);
        Assert.Equal(10m, resultA[docId].Amount);
    }

    /// <summary>
    /// Deleting a document in tenant A's state store must not affect tenant B's copy of the
    /// same document ID.
    /// </summary>
    [Fact]
    public async Task StateStore_delete_in_tenantA_does_not_affect_tenantB()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = $"ti-proj-{Guid.NewGuid():N}";
        var docId = Guid.NewGuid().ToString();

        var storeA = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType: projectionType,
            tenantId: TenantA);

        var storeB = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType: projectionType,
            tenantId: TenantB);

        await storeA.ApplyChangesAsync(
            new Dictionary<string, OrderState> { [docId] = new OrderState("a-doc", 1m) },
            [],
            ct: ct);

        await storeB.ApplyChangesAsync(
            new Dictionary<string, OrderState> { [docId] = new OrderState("b-doc", 2m) },
            [],
            ct: ct);

        // Delete the document for tenant A only.
        await storeA.ApplyChangesAsync(new Dictionary<string, OrderState>(), [docId], ct: ct);

        var resultA = await storeA.LoadManyAsync([docId], ct: ct);
        Assert.Empty(resultA);

        var resultB = await storeB.LoadManyAsync([docId], ct: ct);
        Assert.Equal("b-doc", resultB[docId].Label);
    }

    // ─── P1.2: PostgresDeadLetterStore tenant isolation ────────────────────

    /// <summary>
    /// In multi-tenant mode <see cref="PostgresDeadLetterStore.StoreAsync"/> must persist
    /// the <c>tenant_id</c> column so that retry workers can determine which tenant each
    /// dead-lettered event belongs to.
    /// </summary>
    [Fact]
    public async Task DeadLetterStore_StoreAsync_persists_tenant_id_in_multiTenant_mode()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var tag = new EventTag("ti-dl", orderId.ToString());

        // Append the source event under tenant A so we have a real GlobalPosition and EventId.
        IEventEnvelope persisted;
        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var appended = await storeA.AppendAsync(
                [CreateEvent(new OrderCreated(orderId), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
            persisted = appended.Single();
        }

        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource, multiTenant: true);
        var entry = new DeadLetterEntry(
            Id: Guid.NewGuid(),
            ProcessorId: $"ti-proc-{Guid.NewGuid():N}",
            EventId: persisted.Id,
            EventType: persisted.EventType.Id,
            EventData: persisted.EventData,
            ErrorMessage: "tenant isolation test",
            StackTrace: null,
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow,
            GlobalPosition: persisted.GlobalPosition,
            TenantId: TenantA);

        await deadLetterStore.StoreAsync(entry, ct);

        // Verify the tenant_id column was written via a direct SQL query.
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tenant_id FROM alberto_dead_letter_events WHERE id = @id";
        cmd.Parameters.AddWithValue("id", entry.Id);
        var storedTenantId = (string?)await cmd.ExecuteScalarAsync(ct);

        Assert.Equal(TenantA, storedTenantId);
    }

    /// <summary>
    /// <see cref="PostgresDeadLetterStore.ClaimRetryRequestedAsync"/> must return the
    /// <c>TenantId</c> populated from the events table JOIN so that retry workers know
    /// which tenant the failed event belongs to.
    /// </summary>
    [Fact]
    public async Task DeadLetterStore_ClaimRetryRequested_returns_correct_tenantId_via_events_join()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var tag = new EventTag("ti-dl-retry", orderId.ToString());

        IEventEnvelope persisted;
        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var appended = await storeA.AppendAsync(
                [CreateEvent(new OrderCreated(orderId), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
            persisted = appended.Single();
        }

        var processorId = $"ti-proc-{Guid.NewGuid():N}";
        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource, multiTenant: true);
        var entry = new DeadLetterEntry(
            Id: Guid.NewGuid(),
            ProcessorId: processorId,
            EventId: persisted.Id,
            EventType: persisted.EventType.Id,
            EventData: persisted.EventData,
            ErrorMessage: "retry tenant test",
            StackTrace: null,
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow,
            GlobalPosition: persisted.GlobalPosition,
            TenantId: TenantA);

        await deadLetterStore.StoreAsync(entry, ct);
        await deadLetterStore.MarkForRetryAsync(processorId, ct);

        var retries = await deadLetterStore.ClaimRetryRequestedAsync(
            processorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "ti-worker",
            ct: ct);

        var retry = Assert.Single(retries);
        Assert.Equal(TenantA, retry.TenantId);
        Assert.Contains(tag.Value, retry.Tags ?? []);
    }

    /// <summary>
    /// P1.2 (not yet fixed): <see cref="PostgresDeadLetterStore.GetAsync"/> does not filter
    /// by <c>tenant_id</c> — entries from all tenants are returned for a given processor,
    /// allowing tenant A to observe tenant B's dead-letter entries. This test documents the
    /// desired isolation behaviour and must be un-skipped once <c>GetAsync</c> accepts a
    /// <c>tenantId</c> parameter and applies the corresponding WHERE clause.
    /// </summary>
    [Fact(Skip = "P1.2 not yet fixed: GetAsync does not filter by tenant_id")]
    public async Task DeadLetterStore_GetAsync_is_scoped_to_active_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var tag = new EventTag("ti-dl-get", orderId.ToString());

        IEventEnvelope persisted;
        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var appended = await storeA.AppendAsync(
                [CreateEvent(new OrderCreated(orderId), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
            persisted = appended.Single();
        }

        var processorId = $"ti-proc-{Guid.NewGuid():N}";
        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource, multiTenant: true);
        var entry = new DeadLetterEntry(
            Id: Guid.NewGuid(),
            ProcessorId: processorId,
            EventId: persisted.Id,
            EventType: persisted.EventType.Id,
            EventData: persisted.EventData,
            ErrorMessage: "get isolation test",
            StackTrace: null,
            AttemptCount: 1,
            FailedAt: DateTimeOffset.UtcNow,
            GlobalPosition: persisted.GlobalPosition,
            TenantId: TenantA);

        await deadLetterStore.StoreAsync(entry, ct);

        // Desired: tenant B's view of this processor must be empty because the entry
        // belongs to tenant A. Currently GetAsync has no tenant_id filter so the entry
        // is returned for any caller.
        // TODO: add tenantId parameter to GetAsync and update this assertion.
        var entriesForB = await deadLetterStore.GetAsync(processorId, ct: ct);
        Assert.Empty(entriesForB);
    }
}
