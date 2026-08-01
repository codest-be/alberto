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
public sealed class PostgresEventStoreBackendTests(SingleTenantPostgresFixture fixture)
    : EventStoreBackendSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    protected override Task<IEventStoreBackend> CreateBackend()
    {
        return Task.FromResult<IEventStoreBackend>(
            new PostgresEventStoreBackend(fixture.DataSource, TimeProvider));
    }
}
