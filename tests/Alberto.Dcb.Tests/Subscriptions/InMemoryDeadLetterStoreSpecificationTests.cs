using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing.Xunit;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Runs <see cref="ClaimableDeadLetterStoreSpecification"/> against <see cref="InMemoryDeadLetterStore"/>.
/// </summary>
public sealed class InMemoryDeadLetterStoreSpecificationTests : ClaimableDeadLetterStoreSpecification
{
    /// <inheritdoc/>
    protected override Task<IClaimableDeadLetterStore> CreateClaimableStore() =>
        Task.FromResult<IClaimableDeadLetterStore>(new InMemoryDeadLetterStore());
}
