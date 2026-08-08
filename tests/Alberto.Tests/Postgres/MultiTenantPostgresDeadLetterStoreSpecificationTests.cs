using Alberto.Postgres;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Xunit;

namespace Alberto.Tests.Postgres;

/// <summary>
/// Runs <see cref="ClaimableDeadLetterStoreSpecification"/> against <see cref="PostgresDeadLetterStore"/>
/// using the shipped multi-tenant schema.
///
/// <para>
/// <see cref="DeadLetterStoreSpecification.SupportsNullTenantPassthrough"/> is <c>false</c>: in
/// multi-tenant mode the <c>tenant_id</c> column exists and SQL equality semantics apply, so
/// <c>NULL = @tenantId</c> is false and entries stored without a tenant are not returned for a
/// specific-tenant query. The single-tenant-passthrough fact is skipped accordingly.
/// </para>
/// <para>
/// <see cref="DeadLetterStoreSpecification.SupportsMultiTenantFiltering"/> is <c>true</c>: the
/// schema has a <c>tenant_id</c> column, so a specific-tenant query must exclude entries for
/// other tenants.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MultiTenantPostgresDeadLetterStoreSpecificationTests(MultiTenantDbFixture fixture)
    : ClaimableDeadLetterStoreSpecification, IClassFixture<MultiTenantDbFixture>
{
    /// <inheritdoc/>
    protected override bool SupportsNullTenantPassthrough => false;

    /// <inheritdoc/>
    protected override bool SupportsMultiTenantFiltering => true;

    /// <inheritdoc/>
    protected override Task<IClaimableDeadLetterStore> CreateClaimableStore() =>
        Task.FromResult<IClaimableDeadLetterStore>(new PostgresDeadLetterStore(fixture.DataSource));
}
