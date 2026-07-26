using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing.Xunit;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

/// <summary>
/// Runs <see cref="DeadLetterStoreSpecification"/> against <see cref="PostgresDeadLetterStore"/>
/// using the shipped single-tenant schema.
/// </summary>
public sealed class PostgresDeadLetterStoreSpecificationTests(SingleTenantPostgresFixture fixture)
    : DeadLetterStoreSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    /// <inheritdoc/>
    protected override Task<IDeadLetterStore> CreateStore() =>
        Task.FromResult<IDeadLetterStore>(new PostgresDeadLetterStore(fixture.DataSource));
}
