using Alberto.Dcb.Tenancy;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Tenancy;

public class ShardRoutingTests
{
    private static readonly HashSet<string> TwoShards = new(["db1", "db2"], StringComparer.Ordinal);

    private static readonly IEventToPersist[] OneEvent =
    [
        new EventToPersist
        {
            EventType = new EventType("OrderPlaced"),
            EventData = """{"test": true}""",
            Tags = [EventTag.Parse("order:1")],
        },
    ];

    [Fact]
    public async Task Every_call_lands_on_the_tenants_own_shard()
    {
        var (store, shards) = Store(map: new FakeTenantShardMap().Assign("acme", "db2"), tenantId: "acme");

        await store.AppendAsync(OneEvent);
        await store.StreamAsync(DcbQuery.Empty);
        await store.StreamAllAsync();
        await store.GetLastPositionAsync();

        shards["orders#db2"].Calls.Should().Equal("Append", "Stream", "StreamAll", "LastPosition");
        shards["orders#db1"].Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_tenants_on_two_shards_reach_two_databases()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db1").Assign("globex", "db2");
        var resolver = new TenantShardResolver("orders", map, TwoShards, null);
        var shards = Shards();

        await Store(resolver, shards, "acme").AppendAsync(OneEvent);
        await Store(resolver, shards, "globex").AppendAsync(OneEvent);

        shards["orders#db1"].Calls.Should().Equal("Append");
        shards["orders#db2"].Calls.Should().Equal("Append");
    }

    [Fact]
    public async Task A_reassignment_between_calls_is_picked_up_without_a_new_scope()
    {
        // The shard is resolved inside each call rather than when the store is constructed, so a
        // long-lived scope cannot pin a tenant to the database it started on.
        var map = new FakeTenantShardMap().Assign("acme", "db1");
        var resolver = new TenantShardResolver("orders", map, TwoShards, null);
        var shards = Shards();
        var store = Store(resolver, shards, "acme");

        await store.AppendAsync(OneEvent);

        map.Assign("acme", "db2");
        await resolver.RefreshAsync();
        await store.AppendAsync(OneEvent);

        shards["orders#db1"].Calls.Should().Equal("Append");
        shards["orders#db2"].Calls.Should().Equal("Append");
    }

    [Fact]
    public async Task Routing_without_a_tenant_in_context_fails_before_it_reaches_a_shard()
    {
        var (store, shards) = Store(new FakeTenantShardMap(), tenantId: null);

        var append = async () => await store.AppendAsync(OneEvent);

        await append.Should().ThrowAsync<InvalidOperationException>();
        shards.Values.Should().OnlyContain(s => s.Calls.Count == 0);
    }

    [Fact]
    public async Task An_unknown_tenant_never_reaches_a_shard()
    {
        // Guessing would write the tenant's events into a database it will never be read from.
        var (store, shards) = Store(new FakeTenantShardMap(), tenantId: "newco");

        var append = async () => await store.AppendAsync(OneEvent);

        await append.Should().ThrowAsync<UnknownTenantException>();
        shards.Values.Should().OnlyContain(s => s.Calls.Count == 0);
    }

    [Fact]
    public async Task Each_shard_maintains_its_own_independent_position_sequence()
    {
        // Two tenants on two shards. Both shards happen to have a last position of 42 — a value
        // that exists independently in each shard's own sequence.
        var map = new FakeTenantShardMap().Assign("acme", "db1").Assign("globex", "db2");
        var resolver = new TenantShardResolver("orders", map, TwoShards, null);
        var shards = new Dictionary<string, PositionTrackingStore>(StringComparer.Ordinal)
        {
            ["orders#db1"] = new PositionTrackingStore(lastPosition: 42L),
            ["orders#db2"] = new PositionTrackingStore(lastPosition: 42L),
        };

        var acmeStore = new ShardRoutingEventStore("orders", new StubTenantAccessor("acme"), resolver, key => shards[key]);
        var globexStore = new ShardRoutingEventStore("orders", new StubTenantAccessor("globex"), resolver, key => shards[key]);

        var positionFromAcmeShard = await acmeStore.GetLastPositionAsync();
        var positionFromGlobexShard = await globexStore.GetLastPositionAsync();

        positionFromAcmeShard.Should().Be(42L);
        positionFromGlobexShard.Should().Be(42L);

        // The type system provides no guard: the same long value is accepted as a cursor into
        // a different shard's store without error, even though position 42 on globex's shard
        // refers to a completely different event than position 42 on acme's shard.
        await globexStore.StreamAsync(DcbQuery.Empty, afterPosition: positionFromAcmeShard);

        shards["orders#db2"].LastStreamAfterPosition.Should().Be(42L);
    }

    [Fact]
    public async Task The_backend_router_routes_the_same_way_as_the_store()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db2");
        var shards = Shards();
        var backend = new ShardRoutingEventStoreBackend(
            "orders",
            new StubTenantAccessor("acme"),
            new TenantShardResolver("orders", map, TwoShards, null),
            key => shards[key]);

        await backend.AppendAsync(OneEvent);
        await backend.StreamAllAsync();

        shards["orders#db2"].Calls.Should().Equal("Append", "StreamAll");
        shards["orders#db1"].Calls.Should().BeEmpty();
    }

    private static Dictionary<string, RecordingEventStore> Shards() => new(StringComparer.Ordinal)
    {
        ["orders#db1"] = new RecordingEventStore(),
        ["orders#db2"] = new RecordingEventStore(),
    };

    private static (ShardRoutingEventStore Store, Dictionary<string, RecordingEventStore> Shards) Store(
        FakeTenantShardMap map, string? tenantId)
    {
        var shards = Shards();
        var resolver = new TenantShardResolver("orders", map, TwoShards, null);
        return (Store(resolver, shards, tenantId), shards);
    }

    private static ShardRoutingEventStore Store(
        TenantShardResolver resolver,
        Dictionary<string, RecordingEventStore> shards,
        string? tenantId) =>
        new("orders", new StubTenantAccessor(tenantId), resolver, key => shards[key]);

    private sealed class StubTenantAccessor(string? tenantId) : ITenantAccessor
    {
        public string TenantId => tenantId
            ?? throw new InvalidOperationException("No tenant context is available.");

        public string? TenantIdOrDefault => tenantId;

        public bool HasTenant => tenantId is not null;
    }

    private sealed class RecordingEventStore : IEventStore, IEventStoreBackend
    {
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events,
            DcbQuery? dcbQuery = null,
            long? expectedPosition = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("Append");
            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query,
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("Stream");
            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("StreamAll");
            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("LastPosition");
            return Task.FromResult(0L);
        }
    }

    private sealed class PositionTrackingStore(long lastPosition) : IEventStore
    {
        public long LastStreamAfterPosition { get; private set; }

        public Task<IReadOnlyCollection<IEventEnvelope>> AppendAsync(
            IEnumerable<IEventToPersist> events,
            DcbQuery? dcbQuery = null,
            long? expectedPosition = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAsync(
            DcbQuery query,
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            LastStreamAfterPosition = afterPosition;
            return Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);
        }

        public Task<IReadOnlyCollection<IEventEnvelope>> StreamAllAsync(
            long afterPosition = 0,
            int? limit = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<IEventEnvelope>>([]);

        public Task<long> GetLastPositionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(lastPosition);
    }
}
