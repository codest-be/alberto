using Alberto.InMemory;
using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Alberto.Tests.Postgres;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Runs <see cref="ProjectionRebuildStoreSpecification"/> against
/// <see cref="InMemoryProjectionRebuildStore"/>.
/// </summary>
public sealed class InMemoryProjectionRebuildStoreSpecificationTests
    : ProjectionRebuildStoreSpecification
{
    protected override Task<IProjectionRebuildStore> CreateStore() =>
        Task.FromResult<IProjectionRebuildStore>(new InMemoryProjectionRebuildStore());
}

/// <summary>
/// Runs <see cref="ProjectionRebuildStoreSpecification"/> against
/// <see cref="PostgresProjectionRebuildStore"/>.
/// Uses a shared Testcontainers PostgreSQL instance.
/// </summary>
public sealed class PostgresProjectionRebuildStoreSpecificationTests(
    PostgresProjectionRebuildStoreFixture fixture)
    : ProjectionRebuildStoreSpecification,
      IClassFixture<PostgresProjectionRebuildStoreFixture>
{
    protected override Task<IProjectionRebuildStore> CreateStore() =>
        Task.FromResult<IProjectionRebuildStore>(
            new PostgresProjectionRebuildStore(fixture.DataSource));
}
