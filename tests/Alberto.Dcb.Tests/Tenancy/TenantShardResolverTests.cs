using Alberto.Dcb.Tenancy;
using FluentAssertions;
using Xunit;

namespace Alberto.Dcb.Tests.Tenancy;

public class TenantShardResolverTests
{
    private static readonly HashSet<string> TwoShards = new(["db1", "db2"], StringComparer.Ordinal);

    private static TenantShardResolver Resolver(
        ITenantShardMap map,
        string? defaultShardId = null,
        IReadOnlySet<string>? declaredShards = null) =>
        new("orders", map, declaredShards ?? TwoShards, defaultShardId);

    [Fact]
    public async Task An_assigned_tenant_resolves_to_its_shard()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db2");

        (await Resolver(map).ResolveAsync("acme")).Should().Be("db2");
    }

    [Fact]
    public async Task A_resolved_tenant_is_cached()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db1");
        var resolver = Resolver(map);

        await resolver.ResolveAsync("acme");
        await resolver.ResolveAsync("acme");
        await resolver.ResolveAsync("acme");

        map.ResolveCalls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_first_requests_for_one_tenant_produce_one_catalog_read()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db1");
        map.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = Resolver(map);

        var racers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await resolver.ResolveAsync("acme")))
            .ToArray();

        // Let the first caller through the gate; the other nineteen are queued behind it and must
        // find the answer in the cache rather than each issuing their own query.
        map.Gate.SetResult();
        var results = await Task.WhenAll(racers);

        results.Should().AllBe("db1");
        map.ResolveCalls.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_tenant_is_placed_on_the_default_shard()
    {
        var map = new FakeTenantShardMap();

        (await Resolver(map, defaultShardId: "db1").ResolveAsync("newco")).Should().Be("db1");

        (await map.GetAllAsync()).Should().Contain(new KeyValuePair<string, string>("newco", "db1"));
    }

    [Fact]
    public async Task An_unknown_tenant_without_a_default_shard_is_refused()
    {
        var map = new FakeTenantShardMap();

        var resolve = async () => await Resolver(map).ResolveAsync("newco");

        var exception = await resolve.Should().ThrowAsync<UnknownTenantException>();
        exception.Which.TenantId.Should().Be("newco");
        exception.Which.ModuleKey.Should().Be("orders");
        map.AssignCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_assignment_that_lost_the_race_is_honoured_rather_than_the_one_requested()
    {
        // Another replica got there first and placed the tenant on db2. Using our own db1 would
        // write the tenant's events into a database nothing reads them from.
        var map = new FakeTenantShardMap().Assign("newco", "db2");

        var resolver = Resolver(new WriteOnlyOnFirstRead(map), defaultShardId: "db1");

        (await resolver.ResolveAsync("newco")).Should().Be("db2");
    }

    [Fact]
    public async Task A_tenant_assigned_to_an_undeclared_shard_fails_that_tenant_only()
    {
        var map = new FakeTenantShardMap()
            .Assign("acme", "db1")
            .Assign("retired", "db9");
        var resolver = Resolver(map);

        var resolve = async () => await resolver.ResolveAsync("retired");

        var exception = await resolve.Should().ThrowAsync<ShardUnavailableException>();
        exception.Which.ShardId.Should().Be("db9");

        // The rest of the module keeps working — that is the whole point of failing per tenant.
        (await resolver.ResolveAsync("acme")).Should().Be("db1");
    }

    [Fact]
    public async Task Refresh_picks_up_a_reassignment_without_a_restart()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db1");
        var resolver = Resolver(map);

        (await resolver.ResolveAsync("acme")).Should().Be("db1");

        map.Assign("acme", "db2");
        await resolver.RefreshAsync();

        (await resolver.ResolveAsync("acme")).Should().Be("db2");
    }

    [Fact]
    public async Task Refresh_loads_tenants_nothing_has_asked_for_yet()
    {
        var map = new FakeTenantShardMap().Assign("acme", "db2");
        var resolver = Resolver(map);

        await resolver.RefreshAsync();
        (await resolver.ResolveAsync("acme")).Should().Be("db2");

        map.ResolveCalls.Should().Be(0);
    }

    [Fact]
    public void The_resolver_refuses_to_be_built_without_a_map()
    {
        var construct = () => new TenantShardResolver("orders", null!, TwoShards, null);

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// A map whose <c>ResolveAsync</c> reports no assignment — the state the resolver sees when it
    /// decides to place a tenant — but whose <c>AssignAsync</c> finds one already there.
    /// </summary>
    private sealed class WriteOnlyOnFirstRead(FakeTenantShardMap inner) : ITenantShardMap
    {
        public ValueTask<string?> ResolveAsync(string tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<string> AssignAsync(string tenantId, string shardId, CancellationToken cancellationToken = default) =>
            inner.AssignAsync(tenantId, shardId, cancellationToken);

        public ValueTask<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllAsync(cancellationToken);
    }
}
