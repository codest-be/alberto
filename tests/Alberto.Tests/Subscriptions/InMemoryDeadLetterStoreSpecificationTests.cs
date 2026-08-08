using Alberto.InMemory;
using Alberto.Subscriptions;
using Alberto.Testing.Xunit;
using Xunit;

namespace Alberto.Tests.Subscriptions;

/// <summary>
/// Runs <see cref="ClaimableDeadLetterStoreSpecification"/> against <see cref="InMemoryDeadLetterStore"/>.
/// </summary>
public sealed class InMemoryDeadLetterStoreSpecificationTests : ClaimableDeadLetterStoreSpecification
{
    /// <summary>
    /// InMemory stores <see cref="DeadLetterEntry.TenantId"/> as a plain record field and
    /// always applies tenant filtering, so the multi-tenant isolation facts must run here.
    /// </summary>
    protected override bool SupportsMultiTenantFiltering => true;

    /// <inheritdoc/>
    protected override Task<IClaimableDeadLetterStore> CreateClaimableStore() =>
        Task.FromResult<IClaimableDeadLetterStore>(new InMemoryDeadLetterStore());
}
