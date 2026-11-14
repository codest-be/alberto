using Alberto.Postgres;
using Alberto.Testing.Xunit;
using Xunit;

namespace Alberto.Tests;

/// <summary>
/// PostgreSQL implementation of the event store backend specification tests.
/// Uses a shared Testcontainers PostgreSQL instance across all tests.
/// Runs with single-tenant migrations (no tenant_id on events tables).
/// Test isolation is achieved through unique event tags per test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresEventStoreBackendTests(SingleTenantPostgresFixture fixture)
    : EventStoreBackendSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    [Fact]
    public async Task StreamAllOfTypes_leaves_other_types_on_the_server_and_limits_the_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = await CreateBackend();
        var start = await backend.GetLastPositionAsync(ct);
        var (wanted, other) = ($"kept-{TestId}", $"dropped-{TestId}");

        await backend.AppendAsync([
            CreateEvent(other, $"tag:1{TestId}"),
            CreateEvent(wanted, $"tag:2{TestId}"),
            CreateEvent(other, $"tag:3{TestId}"),
            CreateEvent(wanted, $"tag:4{TestId}"),
            CreateEvent(wanted, $"tag:5{TestId}")
        ], cancellationToken: ct);

        var result = await backend.StreamAllOfTypesAsync(new HashSet<string> { wanted }, start, limit: 2, ct);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal(wanted, e.EventType.Id));
    }

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        return Task.FromResult<IEventStoreBackend>(
            new PostgresEventStoreBackend(fixture.DataSource, TimeProvider));
    }
}
