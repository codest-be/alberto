using System.Text.Json;
using Alberto.Postgres;
using Alberto.Tenancy;
using Alberto.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// A private multi-tenant database with a tenancy-enabled event store, so events are
/// appended the way production appends them and the <c>alberto_tenants</c> trigger fires
/// for real.
/// </summary>
/// <remarks>
/// Private rather than shared because <see cref="PostgresTenantProcessorLock.GetKnownTenantsAsync"/>
/// returns the whole catalog with nothing to scope it by — on a shared database it would
/// also return every tenant any other test in the assembly happened to append for.
/// </remarks>
public sealed class KnownTenantsFixture(PostgresCluster cluster)
    : PostgresDatabaseFixture(cluster, PostgresTemplates.MultiTenant)
{
    public const string ModuleKey = "kt_test";

    public IServiceProvider Services { get; private set; } = null!;

    protected override ValueTask OnInitializedAsync()
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
        Services = services.BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnDisposingAsync()
    {
        if (Services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
    }
}

/// <summary>
/// Tests for <see cref="PostgresTenantProcessorLock.GetKnownTenantsAsync"/>, which reads the
/// <c>alberto_tenants</c> catalog rather than scanning the event log.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KnownTenantsTests(KnownTenantsFixture fixture)
    : IClassFixture<KnownTenantsFixture>
{
    [EventType("kt-order-placed")]
    private sealed record OrderPlaced(Guid OrderId) : IEvent;

    private PostgresTenantProcessorLock CreateLock() =>
        new(fixture.DataSource, null);

    private async Task AppendForTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);

        var store = scope.ServiceProvider.GetRequiredKeyedService<IEventStore>(
            KnownTenantsFixture.ModuleKey);

        var orderId = Guid.NewGuid();
        await store.AppendAsync(
            [
                new EventToPersist
                {
                    EventType = new EventType(
                        EventTypeAttribute.GetEventTypeId(typeof(OrderPlaced))),
                    Tags = [new EventTag("kt-order", orderId.ToString())],
                    EventData = JsonSerializer.Serialize(new OrderPlaced(orderId)),
                },
            ],
            dcbQuery: null,
            expectedPosition: null,
            cancellationToken: ct);
    }

    private async Task<int> CountEventsForTenantAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await fixture.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM alberto_events WHERE tenant_id = @tenantId", conn);
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task GetKnownTenants_ReturnsEveryTenantThatHasAppended_OrderedById()
    {
        var ct = TestContext.Current.CancellationToken;

        // The catalog has no per-test key and every test in this class shares the database,
        // so this asserts on its own prefix rather than on the whole list. Filtering preserves
        // relative order, which is the part under test.
        var run = $"kt_ord_{Guid.NewGuid():N}";

        // Appended out of alphabetical order, because the method promises an ordering while
        // the catalog's own insertion order is first-seen.
        await AppendForTenantAsync($"{run}_initech", ct);
        await AppendForTenantAsync($"{run}_acme", ct);
        await AppendForTenantAsync($"{run}_globex", ct);

        var tenants = (await CreateLock().GetKnownTenantsAsync(ct))
            .Where(t => t.StartsWith(run, StringComparison.Ordinal))
            .ToList();

        Assert.Equal([$"{run}_acme", $"{run}_globex", $"{run}_initech"], tenants);
    }

    [Fact]
    public async Task GetKnownTenants_SeesATenantAsSoonAsItsFirstAppendCommits()
    {
        // The catalog is maintained by an AFTER INSERT statement-level trigger inside the
        // appender's own transaction, so there is no window in which a tenant has durable
        // events but is not yet discoverable. Distribution runs at startup and would
        // otherwise skip a tenant entirely until the next pass.
        var ct = TestContext.Current.CancellationToken;
        var tenantId = $"kt_new_{Guid.NewGuid():N}";
        var lockManager = CreateLock();

        Assert.DoesNotContain(tenantId, await lockManager.GetKnownTenantsAsync(ct));

        await AppendForTenantAsync(tenantId, ct);

        Assert.Contains(tenantId, await lockManager.GetKnownTenantsAsync(ct));
    }

    [Fact]
    public async Task GetKnownTenants_StillReturnsATenantWhoseEventsWerePurged()
    {
        // The one case where the catalog and a SELECT DISTINCT over the log disagree, pinned
        // deliberately rather than left to be discovered: the catalog has no delete path, so
        // distribution hands out a lease for a tenant with no work. That costs a lease row and
        // an empty pass — cheaper than re-scanning the entire event log on every startup.
        var ct = TestContext.Current.CancellationToken;
        var tenantId = $"kt_purged_{Guid.NewGuid():N}";
        var lockManager = CreateLock();

        await AppendForTenantAsync(tenantId, ct);

        await using (var conn = await fixture.DataSource.OpenConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM alberto_events WHERE tenant_id = @tenantId", conn);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(0, await CountEventsForTenantAsync(tenantId, ct));
        Assert.Contains(tenantId, await lockManager.GetKnownTenantsAsync(ct));
    }
}
