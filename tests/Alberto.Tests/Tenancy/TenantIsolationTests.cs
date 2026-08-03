using System.Text.Json;
using Alberto.Messaging;
using Alberto.Admin;
using Alberto.Postgres;
using Alberto.Messaging.Postgres;
using Alberto.Subscriptions;
using Alberto.Tenancy;
using Alberto.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Tenancy;

/// <summary>
/// Multi-tenant fixture on a private database. Runs the multi-tenant migration
/// (without <c>singleTenant: true</c>) and wires up a DI service provider via
/// <see cref="DcbModuleBuilder"/> with tenancy enabled so that
/// <c>TenantEventStoreDecorator</c> is in the request-scoped backend chain.
/// </summary>
public sealed class MultiTenantPostgresFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant)
{
    private const string ModuleKey = "ti_test";

    /// <summary>
    /// DI service provider that owns the keyed <see cref="IEventStore"/> backed by the
    /// multi-tenant Postgres backend. Each test creates its own scope to set the tenant.
    /// </summary>
    public IServiceProvider Services { get; private set; } = null!;

    protected override ValueTask OnInitializedAsync()
    {
        Services = BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddAlberto(ModuleKey, module => module
            .WithTenancy()
            .WithPostgres(o => o with
            {
                ConnectionString = ConnectionString,
                AutoMigrate = false,          // fixture already migrated
                EnableNotifyListener = false, // no background postgres listener in tests
            }));
        return services.BuildServiceProvider();
    }

    // Dispose the SP first so it can clean up its own NpgsqlDataSource (owned by
    // WithPostgres); the base class then disposes the fixture's standalone DataSource.
    protected override async ValueTask OnDisposingAsync()
    {
        if (Services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
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
[Trait("Category", "Integration")]
public sealed class TenantIsolationTests(MultiTenantPostgresFixture fixture)
    : IClassFixture<MultiTenantPostgresFixture>
{
    private const string ModuleKey = "ti_test";
    private const string TenantA = "tenanta";
    private const string TenantB = "tenantb";

    [EventType("ti-order-created")]
    private sealed record OrderCreated(Guid OrderId) : IEvent;

    [EventType("ti-order-shipped")]
    private sealed record OrderShipped(Guid OrderId) : IEvent;

    // Used only by Stream_ByTypes_IsTenantScoped: a types-only read cannot be narrowed by a tag,
    // so it would otherwise also see the ti-order-* events of every other test in this class.
    [EventType("ti-probe-alpha")]
    private sealed record ProbeAlpha(Guid OrderId) : IEvent;

    [EventType("ti-probe-beta")]
    private sealed record ProbeBeta(Guid OrderId) : IEvent;

    // Used only by Stream_ByTagAxisShapes_IsTenantScoped, for the same reason: the union shapes
    // match on the type axis without a tag to narrow them.
    [EventType("ti-union-alpha")]
    private sealed record UnionAlpha(Guid OrderId) : IEvent;

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
    /// The multi-tag counterpart of <see cref="CreateEvent"/>, separate rather than an overload
    /// so the existing single-tag call sites keep resolving as they do.
    /// </summary>
    private static EventToPersist CreateTagged<TEvent>(TEvent @event, params EventTag[] tags)
        where TEvent : IEvent
    {
        var eventTypeId = EventTypeAttribute.GetEventTypeId(typeof(TEvent));
        return new EventToPersist
        {
            EventType = new EventType(eventTypeId),
            Tags = [..tags],
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

    // ─── Regression: multi-tenant types-and-tags read SQL ───────────────────

    /// <summary>
    /// Exercises the multi-tenant <c>alberto_read_by_types_and_tags</c> function, which the
    /// shared <c>EventStoreBackendSpecification</c> never reaches — it runs against the
    /// single-tenant schema only. plpgsql resolves table and column references at first
    /// execution rather than at CREATE FUNCTION, so a mistyped tenant predicate in the
    /// multi-tenant script survives migration and fails in production instead.
    /// <para>
    /// Also covers the two behaviours that distinguish the semi-join form from the
    /// INTERSECT it replaced: an event carrying several of the requested tags must be
    /// returned once, and the other tenant's identically-shaped event must not appear.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stream_ByTypesAndTags_IsTenantScoped_AndReturnsEachEventOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var orderTag = new EventTag("ti-tt-order", orderId.ToString());
        var customerTag = new EventTag("ti-tt-customer", orderId.ToString());

        static EventToPersist Tagged<TEvent>(TEvent @event, params EventTag[] tags)
            where TEvent : IEvent => new()
            {
                EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(TEvent))),
                Tags = tags,
                EventData = JsonSerializer.Serialize(@event),
            };

        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeA.AppendAsync(
                [
                    Tagged(new OrderCreated(orderId), orderTag, customerTag), // the only match
                    Tagged(new OrderShipped(orderId), orderTag, customerTag), // right tags, wrong type
                ],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeB = CreateScopeForTenant(TenantB))
        {
            var storeB = scopeB.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeB.AppendAsync(
                [Tagged(new OrderCreated(orderId), orderTag, customerTag)], // right shape, wrong tenant
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeA2 = CreateScopeForTenant(TenantA))
        {
            var storeA2 = scopeA2.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var query = DcbQuery.Empty
                .WithTypes("ti-order-created")
                .WithTags(orderTag.Value, customerTag.Value);

            var events = await storeA2.StreamAsync(query, cancellationToken: ct);

            var ev = Assert.Single(events);
            Assert.Equal(TenantA, ev.TenantId);
            Assert.Equal("ti-order-created", ev.EventType.Id);
        }
    }

    /// <summary>
    /// The test above uses two tags and so exercises the general path. This one uses a
    /// single tag, which migrations 029 and 030 route to a separate branch that omits the
    /// <c>DISTINCT</c> — so it needs its own tenant-scoping guard. plpgsql resolves
    /// references per branch at first execution, meaning a mistyped tenant predicate in the
    /// fast path is invisible to every multi-tag test.
    /// <para>
    /// Both type shapes the branch accepts are asserted: one named type, and several. They
    /// compile to different predicates (<c>= $v</c> against <c>= ANY</c>) on the same
    /// branch, and only the second is reachable after 030.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stream_BySingleTagFastPath_IsTenantScoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var orderTag = new EventTag("ti-fp-order", orderId.ToString());

        static EventToPersist Tagged<TEvent>(TEvent @event, params EventTag[] tags)
            where TEvent : IEvent => new()
            {
                EventType = new EventType(EventTypeAttribute.GetEventTypeId(typeof(TEvent))),
                Tags = tags,
                EventData = JsonSerializer.Serialize(@event),
            };

        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeA.AppendAsync(
                [
                    Tagged(new OrderCreated(orderId), orderTag),  // the only match
                    Tagged(new OrderShipped(orderId), orderTag),  // right tag, wrong type
                ],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeB = CreateScopeForTenant(TenantB))
        {
            var storeB = scopeB.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeB.AppendAsync(
                [Tagged(new OrderCreated(orderId), orderTag)], // right shape, wrong tenant
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeA2 = CreateScopeForTenant(TenantA))
        {
            var storeA2 = scopeA2.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            var query = DcbQuery.Empty
                .WithTypes("ti-order-created")
                .WithTags(orderTag.Value);

            var events = await storeA2.StreamAsync(query, cancellationToken: ct);

            var ev = Assert.Single(events);
            Assert.Equal(TenantA, ev.TenantId);
            Assert.Equal("ti-order-created", ev.EventType.Id);

            // Same branch, `= ANY` on the type axis. Tenant A appended both types under the
            // tag, tenant B only the first, so a leaking predicate shows up as three rows.
            var multiTypeQuery = DcbQuery.Empty
                .WithTypes("ti-order-created", "ti-order-shipped")
                .WithTags(orderTag.Value);

            var multiTypeEvents = await storeA2.StreamAsync(multiTypeQuery, cancellationToken: ct);

            Assert.Equal(2, multiTypeEvents.Count);
            Assert.All(multiTypeEvents, e => Assert.Equal(TenantA, e.TenantId));
        }
    }

    /// <summary>
    /// The types-only read, <c>alberto_read_by_types</c>, which migration 031 rewrote from a
    /// single <c>event_type = ANY($2)</c> scan into one bounded probe per named type. The probes
    /// carry the tenant predicate individually now, so a rewrite that dropped it from one of them
    /// — or that moved the tenant check onto the events row, where <c>global_position</c> already
    /// matches — would leak every other tenant's events of that type.
    /// <para>
    /// This read has no tag to narrow it, so it sees every event of a named type in the database.
    /// <c>ti-probe-*</c> is therefore used by this test and no other, which is what makes the
    /// counts below exact.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stream_ByTypes_IsTenantScoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();

        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeA.AppendAsync(
                [
                    CreateEvent(new ProbeAlpha(orderId)),  // match
                    CreateEvent(new ProbeBeta(orderId)),   // tenant A, unnamed type
                    CreateEvent(new ProbeAlpha(orderId)),  // match
                ],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeB = CreateScopeForTenant(TenantB))
        {
            var storeB = scopeB.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeB.AppendAsync(
                [
                    CreateEvent(new ProbeAlpha(orderId)),  // right type, wrong tenant
                    CreateEvent(new ProbeBeta(orderId)),
                ],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        await using (var scopeA2 = CreateScopeForTenant(TenantA))
        {
            var storeA2 = scopeA2.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);

            // One named type: a single probe, the shape a leaked tenant predicate would widen
            // from two rows to three.
            var single = await storeA2.StreamAsync(DcbQuery.ByTypes("ti-probe-alpha"), cancellationToken: ct);

            Assert.Equal(2, single.Count);
            Assert.All(single, e => Assert.Equal(TenantA, e.TenantId));
            Assert.All(single, e => Assert.Equal("ti-probe-alpha", e.EventType.Id));

            // Several named types: one probe each, every one of which must be scoped.
            var several = await storeA2.StreamAsync(
                DcbQuery.ByTypes("ti-probe-alpha", "ti-probe-beta"), cancellationToken: ct);

            Assert.Equal(3, several.Count);
            Assert.All(several, e => Assert.Equal(TenantA, e.TenantId));
            Assert.Equal(
                several.Select(e => e.GlobalPosition).Order(),
                several.Select(e => e.GlobalPosition));

            // The same type twice, which is what DcbQuery produces from ByTypes(x).WithTypes(x):
            // deduplicated to one probe, so still tenant A's two events and not four rows.
            var repeated = await storeA2.StreamAsync(
                DcbQuery.ByTypes("ti-probe-alpha").WithTypes("ti-probe-alpha"), cancellationToken: ct);

            Assert.Equal(single.Select(e => e.GlobalPosition), repeated.Select(e => e.GlobalPosition));
        }
    }

    /// <summary>
    /// The four tag-axis reads migration 033 rewrote: <c>alberto_read_by_all_tags</c>,
    /// <c>_types_and_all_tags</c>, <c>_types_or_tags</c> and <c>_types_or_all_tags</c>. Each grew
    /// several new scans that must each carry the tenant predicate — a bounded probe per tag in
    /// <c>alberto_pick_all_tags_driver</c>, a driving scan, one <c>EXISTS</c> probe per remaining
    /// tag, and in the union shapes a probe per named type. Dropping the predicate from any one
    /// of them leaks, and the ones easiest to miss are the innermost: the EXISTS probes decide
    /// whether an event carries the other tags, so an unscoped one would admit tenant A's event
    /// on the strength of tenant B's tag row.
    /// <para>
    /// Both tenants therefore write the <em>same</em> tags here. A leak shows up as a count that
    /// is too high; a tenant predicate that matches nothing shows up as one that is too low.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stream_ByTagAxisShapes_IsTenantScoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var orderId = Guid.NewGuid();
        var order = new EventTag("ti-alltags-order", orderId.ToString());
        var region = new EventTag("ti-alltags-region", Guid.NewGuid().ToString());

        // The identical five appends for both tenants, so every assertion below has a
        // same-shaped row in the other tenant that must not be returned.
        EventToPersist[] Batch() =>
        [
            CreateTagged(new OrderCreated(orderId), order, region),   // both tags
            CreateTagged(new OrderShipped(orderId), order, region),   // both tags, other type
            CreateTagged(new OrderCreated(orderId), order),           // one tag short
            CreateTagged(new UnionAlpha(orderId)),                    // type axis only
        ];

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var scope = CreateScopeForTenant(tenant);
            var store = scope.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await store.AppendAsync(Batch(), dcbQuery: null, expectedPosition: null, cancellationToken: ct);
        }

        await using var scopeA = CreateScopeForTenant(TenantA);
        var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);

        // alberto_read_by_all_tags: driver probe, driving scan, one EXISTS probe.
        var allTags = await storeA.StreamAsync(DcbQuery.ByAllTags(order, region), cancellationToken: ct);
        Assert.Equal(2, allTags.Count);
        Assert.All(allTags, e => Assert.Equal(TenantA, e.TenantId));

        // Reversing the tags changes which one drives — and must change nothing else.
        var reversed = await storeA.StreamAsync(DcbQuery.ByAllTags(region, order), cancellationToken: ct);
        Assert.Equal(allTags.Select(e => e.GlobalPosition), reversed.Select(e => e.GlobalPosition));

        // alberto_read_by_types_and_all_tags, one named type: the scalar type-position probe.
        var oneType = await storeA.StreamAsync(
            DcbQuery.ByAllTags(order, region).WithTypes("ti-order-created"), cancellationToken: ct);
        Assert.Equal(TenantA, Assert.Single(oneType).TenantId);

        // The same function, several named types: here the type is tested on the events row,
        // which carries no tenant predicate of its own and relies on the driving scan's.
        var severalTypes = await storeA.StreamAsync(
            DcbQuery.ByAllTags(order, region).WithTypes("ti-order-created", "ti-order-shipped"),
            cancellationToken: ct);
        Assert.Equal(2, severalTypes.Count);
        Assert.All(severalTypes, e => Assert.Equal(TenantA, e.TenantId));

        // alberto_read_by_types_or_tags: a bounded probe per type and per tag, unioned.
        var orTags = await storeA.StreamAsync(
            DcbQuery.ByTags(order).WithTypes("ti-union-alpha").AsUnion(), cancellationToken: ct);
        Assert.Equal(4, orTags.Count);
        Assert.All(orTags, e => Assert.Equal(TenantA, e.TenantId));
        Assert.Equal(
            orTags.Select(e => e.GlobalPosition).Order(),
            orTags.Select(e => e.GlobalPosition));

        // alberto_read_by_types_or_all_tags: the type probes unioned with the all-tags scan.
        var orAllTags = await storeA.StreamAsync(
            DcbQuery.ByAllTags(order, region).WithTypes("ti-union-alpha").AsUnion(), cancellationToken: ct);
        Assert.Equal(3, orAllTags.Count);
        Assert.All(orAllTags, e => Assert.Equal(TenantA, e.TenantId));
    }

    // ─── Regression: multi-tenant stable-head barrier SQL ───────────────────

    /// <summary>
    /// Regression guard for the multi-tenant stable-head barrier. The SQL evaluates
    /// <c>pg_snapshot_xmin(pg_current_snapshot())::TEXT::BIGINT</c>; the <c>::TEXT</c>
    /// round-trip is required because PostgreSQL has no direct xid8→bigint cast, and
    /// "simplifying" it to <c>::BIGINT</c> throws "cannot cast type xid8 to bigint" at
    /// runtime. The fake-backend head tests cannot catch this because they never touch
    /// the real SQL; this exercises it against the multi-tenant schema with a committed row.
    /// </summary>
    [Fact]
    public async Task GetStableHeadGlobal_AfterCommittedAppend_ExecutesBarrierSql()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = new EventTag("ti-stablehead", Guid.NewGuid().ToString());

        // Commit at least one row so the barrier's ascending index scan has a row to evaluate.
        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var storeA = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await storeA.AppendAsync(
                [CreateEvent(new OrderCreated(Guid.NewGuid()), tag)],
                dcbQuery: null,
                expectedPosition: null,
                cancellationToken: ct);
        }

        var backend = new PostgresTenantEventStoreBackend(fixture.DataSource);

        // The append has committed, so nothing is in flight: the barrier must not throw
        // "cannot cast type xid8 to bigint" and must return a non-negative head.
        var stableHead = await backend.GetStableHeadGlobalAsync(0, ct);

        Assert.True(stableHead >= 0, $"expected non-negative stable head, got {stableHead}");
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

        // Resolve the registered adapter: this guards the composition seam as well as the SQL.
        var deadLetterStore = fixture.Services.GetRequiredKeyedService<IDeadLetterStore>(ModuleKey);
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = $"ti-proc-{Guid.NewGuid():N}",
            EventId = persisted.Id,
            EventType = persisted.EventType.Id,
            EventData = persisted.EventData,
            ErrorMessage = "tenant isolation test",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            GlobalPosition = persisted.GlobalPosition,
            TenantId = TenantA,
        };

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
    public async Task DeadLetterStore_LegacyFalseHint_StillUsesMultiTenantSchemaForClaims()
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
        // Already-compiled callers of the former optional-parameter constructor embed false at
        // the call site. That legacy value must not override the migrated schema after upgrade.
        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource, multiTenant: false);
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = processorId,
            EventId = persisted.Id,
            EventType = persisted.EventType.Id,
            EventData = persisted.EventData,
            ErrorMessage = "retry tenant test",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            GlobalPosition = persisted.GlobalPosition,
            TenantId = TenantA,
        };

        await deadLetterStore.StoreAsync(entry, ct);
        await deadLetterStore.MarkForRetryAsync(processorId, ct);

        var retries = await deadLetterStore.ClaimRetryRequestedAsync(
            processorId,
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            claimedBy: "ti-worker",
            ct: ct);

        var retry = Assert.Single(retries);
        Assert.Equal(TenantA, retry.Entry.TenantId);
        Assert.Contains(tag.Value, retry.Entry.Tags ?? []);
    }

    [Fact]
    public async Task OutboxStore_CarriesTenantIdentityThroughPersistenceAndClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new PostgresOutboxStore(fixture.DataSource);
        var entry = new OutboxEntry(
            Id: Guid.NewGuid(),
            SourceEventId: Guid.NewGuid(),
            MessageType: "tenant-message",
            Version: "1",
            Payload: "{}",
            Metadata: [],
            Status: OutboxEntryStatus.Pending,
            RetryCount: 0,
            LastError: null,
            CreatedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            TenantId: TenantA);

        await store.InsertAsync(entry, ct);
        var claim = (await store.ClaimPendingAsync(
                100,
                TimeSpan.FromMinutes(5),
                "tenant-test",
                ct))
            .Single(c => c.Entry.Id == entry.Id);

        Assert.Equal(TenantA, claim.Entry.TenantId);
    }

    [Fact]
    public async Task AdminInspection_Events_PreservesAndFiltersTenantIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = new EventTag("ti-admin-events", Guid.NewGuid().ToString());

        await using (var scopeA = CreateScopeForTenant(TenantA))
        {
            var store = scopeA.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await store.AppendAsync(
                [CreateEvent(new OrderCreated(Guid.NewGuid()), tag)],
                cancellationToken: ct);
        }

        await using (var scopeB = CreateScopeForTenant(TenantB))
        {
            var store = scopeB.ServiceProvider.GetRequiredKeyedService<IEventStore>(ModuleKey);
            await store.AppendAsync(
                [CreateEvent(new OrderCreated(Guid.NewGuid()), tag)],
                cancellationToken: ct);
        }

        var admin = new PostgresAdminDataAccess(fixture.DataSource);
        var events = await admin.GetEventsAsync(
            type: null,
            tag: tag.Value,
            tenant: TenantA,
            afterPosition: 0,
            limit: 20,
            ct);

        var @event = Assert.Single(events);
        Assert.Equal(TenantA, @event.TenantId);
    }

    [Fact]
    public async Task AdminInspection_DeadLetters_PreservesAndFiltersTenantIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var processorId = $"ti-admin-dl-{Guid.NewGuid():N}";
        var deadLetters = new PostgresDeadLetterStore(fixture.DataSource);

        await deadLetters.StoreAsync(new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = processorId,
            EventId = Guid.NewGuid(),
            EventType = "admin-test",
            EventData = "{}",
            ErrorMessage = "tenant A",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            GlobalPosition = 1,
            TenantId = TenantA,
        }, ct);
        await deadLetters.StoreAsync(new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = processorId,
            EventId = Guid.NewGuid(),
            EventType = "admin-test",
            EventData = "{}",
            ErrorMessage = "tenant B",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            GlobalPosition = 2,
            TenantId = TenantB,
        }, ct);

        var admin = new PostgresAdminDataAccess(fixture.DataSource);
        var entries = await admin.GetDeadLettersAsync(
            processorId,
            type: null,
            tenant: TenantB,
            limit: 20,
            ct);

        var entry = Assert.Single(entries);
        Assert.Equal(TenantB, entry.TenantId);
        Assert.Equal("tenant B", entry.ErrorMessage);
    }

    [Fact]
    public async Task AdminInspection_ProjectionAndLeaseInventory_AreTopologyAware()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectionType = $"ti-admin-projection-{Guid.NewGuid():N}";
        var documentId = Guid.NewGuid().ToString();
        var stateStore = new PostgresStateStore<OrderState>(
            fixture.DataSource,
            projectionType,
            tenantId: TenantA);
        await stateStore.ApplyChangesAsync(
            new Dictionary<string, OrderState>
            {
                [documentId] = new("tenant A", 10m),
            },
            [],
            ct: ct);

        var admin = new PostgresAdminDataAccess(fixture.DataSource);
        var states = await admin.GetProjectionStatesAsync(
            projectionType,
            TenantA,
            search: null,
            limit: 20,
            ct);
        var inventory = await admin.GetTenantLeaseInventoryAsync(ct);

        Assert.Equal(TenantA, Assert.Single(states).TenantId);
        Assert.Equal(AdminTenancyMode.MultiTenant, inventory.TenancyMode);
    }

    /// <summary>
    /// P1.2: <see cref="PostgresDeadLetterStore.GetAsync"/> scopes to <c>tenant_id</c> when a
    /// tenant is supplied, so tenant A cannot observe tenant B's dead-letter entries (which
    /// carry full event payloads). Passing <see langword="null"/> keeps the cross-tenant
    /// operator view used by the CLI.
    /// </summary>
    [Fact]
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
        var deadLetterStore = new PostgresDeadLetterStore(fixture.DataSource);
        var entry = new DeadLetterEntry
        {
            Id = Guid.NewGuid(),
            ProcessorId = processorId,
            EventId = persisted.Id,
            EventType = persisted.EventType.Id,
            EventData = persisted.EventData,
            ErrorMessage = "get isolation test",
            StackTrace = null,
            AttemptCount = 1,
            FailedAt = DateTimeOffset.UtcNow,
            GlobalPosition = persisted.GlobalPosition,
            TenantId = TenantA,
        };

        await deadLetterStore.StoreAsync(entry, ct);

        // Tenant B's view of this processor must be empty because the entry belongs to tenant A.
        var entriesForB = await deadLetterStore.GetAsync(processorId, tenantId: TenantB, ct: ct);
        Assert.Empty(entriesForB);

        // Tenant A must still see its own entry — otherwise a filter that always matches
        // nothing would satisfy the assertion above.
        var entriesForA = await deadLetterStore.GetAsync(processorId, tenantId: TenantA, ct: ct);
        Assert.Equal(entry.Id, Assert.Single(entriesForA).Id);

        // No tenant supplied keeps the cross-tenant operator view the CLI depends on.
        var entriesForOperator = await deadLetterStore.GetAsync(processorId, ct: ct);
        Assert.Equal(entry.Id, Assert.Single(entriesForOperator).Id);
    }
}
