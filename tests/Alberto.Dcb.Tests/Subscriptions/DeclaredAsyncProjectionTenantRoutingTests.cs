using System.Collections.Concurrent;
using System.Text.Json;
using Alberto.Dcb.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// A projection is one processor, but a store's tenancy is fixed when the store is built —
/// and the control loop hands one processor every tenant's events. These tests pin the
/// resolution: <see cref="DeclaredAsyncProjection{TState}"/> builds one store per tenant from
/// the factory it was given, and routes each event to the store for the tenant that event
/// carries.
///
/// <para>
/// Before this, a projection held a single store built once, so a tenant-enabled module's
/// projection had to be registered either with one tenant hard-coded (wrong for every other
/// tenant) or with none at all (a write that fails on a tenant-keyed table). Both were
/// reachable by writing perfectly ordinary registration code; neither was a compile error.
/// </para>
/// </summary>
public class DeclaredAsyncProjectionTenantRoutingTests
{
    [EventType("routing-order-created")]
    public record OrderCreated(Guid OrderId, decimal Amount) : IEvent;

    public record OrderSummary
    {
        public Guid OrderId { get; init; }
        public decimal Amount { get; init; }
    }

    private static ProjectionDeclaration<OrderSummary> Declaration() =>
        DeclareProjection.For<OrderSummary>("tenant-routing-v1")
            .On<OrderCreated>(
                id: e => e.OrderId.ToString(),
                apply: (state, e, ctx) => new OrderSummary
                {
                    OrderId = e.OrderId,
                    Amount = state.Amount + e.Amount,
                })
            .Build();

    /// <summary>
    /// Records which tenant each store was built for so a test can assert both what a store
    /// holds and how many were built.
    /// </summary>
    private sealed class RecordingStoreFactory
    {
        public ConcurrentDictionary<string, RecordingStore> Stores { get; } = new();
        public List<string?> Requests { get; } = [];

        public const string NullTenant = "<null>";

        public IStateStore<OrderSummary> Create(string? tenantId)
        {
            Requests.Add(tenantId);
            var store = new RecordingStore();
            Stores[tenantId ?? NullTenant] = store;
            return store;
        }

        public RecordingStore For(string? tenantId) => Stores[tenantId ?? NullTenant];
    }

    private sealed class RecordingStore : IStateStore<OrderSummary>, IAsyncDisposable
    {
        private readonly Dictionary<string, OrderSummary> _store = new();

        public IReadOnlyDictionary<string, OrderSummary> Documents => _store;
        public List<string> WriteOrder { get; } = [];
        public bool Disposed { get; private set; }

        public Task<Dictionary<string, OrderSummary>> LoadManyAsync(
            IEnumerable<string> documentIds,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, OrderSummary>();
            foreach (var id in documentIds)
            {
                if (_store.TryGetValue(id, out var state))
                    result[id] = state;
            }
            return Task.FromResult(result);
        }

        public Task ApplyChangesAsync(
            IReadOnlyDictionary<string, OrderSummary> upserts,
            IReadOnlyCollection<string> deletes,
            CancellationToken ct = default)
        {
            foreach (var (id, state) in upserts)
            {
                _store[id] = state;
                WriteOrder.Add(id);
            }

            foreach (var id in deletes)
                _store.Remove(id);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OrderSummary>> ListRecentAsync(
            int limit = 20,
            CancellationToken ct = default)
        {
            IReadOnlyList<OrderSummary> result = _store.Values.Reverse().Take(limit).ToList();
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static IEventEnvelope Envelope(string? tenantId, IEvent @event, long position) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GlobalPosition = position,
            EventType = new EventType(EventTypeAttribute.GetEventTypeId(@event.GetType())),
            Tags = [],
            EventData = JsonSerializer.Serialize(@event, @event.GetType()),
            Metadata = new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow,
        };

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProcessEvent_WritesToTheStoreForTheEventsTenant()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();

        await projection.ProcessEventAsync(Envelope("acme", new OrderCreated(orderA, 10m), 1), Ct);
        await projection.ProcessEventAsync(Envelope("globex", new OrderCreated(orderB, 20m), 2), Ct);

        factory.For("acme").Documents.Should().ContainKey(orderA.ToString())
            .And.NotContainKey(orderB.ToString());
        factory.For("globex").Documents.Should().ContainKey(orderB.ToString())
            .And.NotContainKey(orderA.ToString());
    }

    /// <summary>
    /// Stores are built once per tenant and reused. A store built per event would be a
    /// connection (or a whole DbContext factory) per event, and any state a store holds —
    /// InMemoryStateStore's dictionary, for one — would be thrown away between events.
    /// </summary>
    [Fact]
    public async Task StoresAreBuiltOncePerTenant_NotOncePerEvent()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);

        for (var i = 0; i < 3; i++)
            await projection.ProcessEventAsync(
                Envelope("acme", new OrderCreated(Guid.NewGuid(), 1m), i + 1), Ct);
        await projection.ProcessEventAsync(
            Envelope("globex", new OrderCreated(Guid.NewGuid(), 1m), 4), Ct);

        factory.Requests.Should().Equal("acme", "globex");
    }

    /// <summary>
    /// The same routing in the batch path. Batches arrive straight off the event log, so a
    /// batch spanning several tenants is the normal case, not an edge one.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_RoutesEachEventToItsOwnTenantsStore()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();

        await projection.ProcessBatchAsync(
        [
            Envelope("acme", new OrderCreated(orderA, 10m), 1),
            Envelope("globex", new OrderCreated(orderB, 20m), 2),
        ], Ct);

        factory.For("acme").Documents.Should().ContainKey(orderA.ToString())
            .And.NotContainKey(orderB.ToString());
        factory.For("globex").Documents.Should().ContainKey(orderB.ToString())
            .And.NotContainKey(orderA.ToString());
    }

    /// <summary>
    /// A batch is split into contiguous same-tenant runs rather than grouped by tenant, so two
    /// events for one tenant keep their relative order even when another tenant's event sits
    /// between them. Grouping would reorder the log, which a projection folding events into a
    /// running total cannot survive.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_InterleavedTenants_PreservesPerTenantOrder()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await projection.ProcessBatchAsync(
        [
            Envelope("acme", new OrderCreated(first, 10m), 1),
            Envelope("globex", new OrderCreated(Guid.NewGuid(), 99m), 2),
            Envelope("acme", new OrderCreated(second, 20m), 3),
        ], Ct);

        factory.For("acme").WriteOrder.Should().Equal(first.ToString(), second.ToString());
        // An interrupted run must reuse the store already built for that tenant, so "acme"
        // is asked for once even though it owns two of the three runs.
        factory.Requests.Should().Equal("acme", "globex");
    }

    /// <summary>
    /// Events for one tenant that are split across runs still fold into one document — the
    /// store is the same instance, so the second run loads what the first wrote.
    /// </summary>
    [Fact]
    public async Task ProcessBatch_InterleavedTenants_FoldsIntoOneDocument()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);
        var orderId = Guid.NewGuid();

        await projection.ProcessBatchAsync(
        [
            Envelope("acme", new OrderCreated(orderId, 10m), 1),
            Envelope("globex", new OrderCreated(Guid.NewGuid(), 99m), 2),
            Envelope("acme", new OrderCreated(orderId, 20m), 3),
        ], Ct);

        factory.For("acme").Documents[orderId.ToString()].Amount.Should().Be(30m);
    }

    /// <summary>
    /// A module without tenancy sends events carrying no tenant, and the factory is asked for
    /// exactly that: <c>null</c>, not a stand-in string. A store built for a non-tenant schema
    /// must not be handed a tenant it would then put in a <c>WHERE</c> clause.
    /// </summary>
    [Fact]
    public async Task NullTenant_IsPassedThroughAsNull_AndCachedLikeAnyOther()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);

        await projection.ProcessEventAsync(
            Envelope(null, new OrderCreated(Guid.NewGuid(), 1m), 1), Ct);
        await projection.ProcessEventAsync(
            Envelope(null, new OrderCreated(Guid.NewGuid(), 1m), 2), Ct);

        factory.Requests.Should().Equal([null]);
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryTenantsStore()
    {
        var factory = new RecordingStoreFactory();
        var projection = new DeclaredAsyncProjection<OrderSummary>(Declaration(), factory.Create);

        await projection.ProcessEventAsync(
            Envelope("acme", new OrderCreated(Guid.NewGuid(), 1m), 1), Ct);
        await projection.ProcessEventAsync(
            Envelope("globex", new OrderCreated(Guid.NewGuid(), 1m), 2), Ct);

        await projection.DisposeAsync();

        factory.For("acme").Disposed.Should().BeTrue();
        factory.For("globex").Disposed.Should().BeTrue();
    }
}
