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
    /// <inheritdoc/>
    protected override Task<IClaimableDeadLetterStore> CreateClaimableStore() =>
        Task.FromResult<IClaimableDeadLetterStore>(new InMemoryDeadLetterStore());
}
