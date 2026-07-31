using Alberto.Dcb.Postgres;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing.Xunit;
using Xunit;

namespace Alberto.Dcb.Tests.Postgres;

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
