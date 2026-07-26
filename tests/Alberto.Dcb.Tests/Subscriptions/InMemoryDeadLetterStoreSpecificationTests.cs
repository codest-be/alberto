using Alberto.Dcb.InMemory;
using Alberto.Dcb.Subscriptions;
using Alberto.Dcb.Testing.Xunit;
using Xunit;

namespace Alberto.Dcb.Tests.Subscriptions;

/// <summary>
/// Runs <see cref="DeadLetterStoreSpecification"/> against <see cref="InMemoryDeadLetterStore"/>.
/// </summary>
public sealed class InMemoryDeadLetterStoreSpecificationTests : DeadLetterStoreSpecification
{
    /// <inheritdoc/>
    protected override Task<IDeadLetterStore> CreateStore() =>
        Task.FromResult<IDeadLetterStore>(new InMemoryDeadLetterStore());
}
