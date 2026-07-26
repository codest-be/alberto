using Alberto.Dcb.Messaging;
using Alberto.Dcb.Testing;
using Alberto.Dcb.Testing.Xunit;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Alberto.Dcb.Tests.Testing;

/// <summary>
/// Runs <see cref="OutboxStoreSpecification"/> against <see cref="InMemoryOutboxStore"/>
/// with a <c>FakeTimeProvider</c>, enabling all lease-expiry facts.
/// </summary>
public sealed class InMemoryOutboxStoreSpecificationTests : OutboxStoreSpecification
{
    private readonly FakeTimeProvider _timeProvider = new();

    /// <inheritdoc/>
    protected override TimeProvider TimeProvider => _timeProvider;

    /// <inheritdoc/>
    protected override Task<IOutboxStore> CreateStore() =>
        Task.FromResult<IOutboxStore>(new InMemoryOutboxStore(_timeProvider));
}
