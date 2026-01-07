using Alberto.Dcb.Postgres;
using Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// PostgreSQL implementation of the event store backend specification tests.
/// Uses a shared Testcontainers PostgreSQL instance across all tests.
/// Test isolation is achieved through unique tenant IDs per test.
/// </summary>
public sealed class PostgresEventStoreBackendTests : EventStoreBackendSpecification, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStoreBackendTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        return Task.FromResult<IEventStoreBackend>(
            new PostgresEventStoreBackend(_fixture.DataSource, TimeProvider));
    }
}
