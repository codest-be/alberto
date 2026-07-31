using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Runs <see cref="ClaimableDeadLetterStoreSpecification"/> against <see cref="PostgresDeadLetterStore"/>
/// using the shipped single-tenant schema.
/// </summary>
public sealed class PostgresDeadLetterStoreSpecificationTests(SingleTenantPostgresFixture fixture)
    : ClaimableDeadLetterStoreSpecification, IClassFixture<SingleTenantPostgresFixture>
{
    /// <inheritdoc/>
    protected override Task<IClaimableDeadLetterStore> CreateClaimableStore() =>
        Task.FromResult<IClaimableDeadLetterStore>(new PostgresDeadLetterStore(fixture.DataSource));
}
