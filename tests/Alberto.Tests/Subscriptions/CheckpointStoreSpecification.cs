using Alberto.InMemory;
using Alberto.Postgres;
using Alberto.Checkpoints;
using Alberto.Testing.Xunit;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Tests for PostgresCheckpointStore.
/// Uses a shared Testcontainers PostgreSQL instance.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresCheckpointStoreTests(PostgresFixture fixture)
    : CheckpointStoreSpecification, IClassFixture<PostgresFixture>
{
    protected override Task<ICheckpointStore> CreateStore()
    {
        return Task.FromResult<ICheckpointStore>(
            new PostgresCheckpointStore(fixture.DataSource));
    }
}

/// <summary>
/// Tests for CachingCheckpointStore wrapping an InMemoryFencedCheckpointStore (no Docker required).
/// The flush interval is set to one hour so no timer tick fires during a test; all GetAsync
/// calls are served from the in-process cache that SaveAsync populates synchronously.
/// ResetAsync and RewindAsync write through the cache immediately, so no explicit flush is needed.
/// </summary>
public class CachingCheckpointStoreSpecificationTests : CheckpointStoreSpecification, IAsyncDisposable
{
    private readonly InMemoryFencedCheckpointStore _inner = new();
    private readonly CachingCheckpointStore _cache;

    public CachingCheckpointStoreSpecificationTests()
    {
        _cache = new CachingCheckpointStore(_inner, flushInterval: TimeSpan.FromHours(1));
    }

    protected override Task<ICheckpointStore> CreateStore()
    {
        return Task.FromResult<ICheckpointStore>(_cache);
    }

    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}
