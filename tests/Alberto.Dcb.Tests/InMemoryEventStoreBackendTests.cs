using Alberto.Dcb.InMemory;
using Alberto.Dcb.Testing.Xunit;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Specification tests for InMemoryEventStoreBackend.
/// Test isolation is achieved through unique tenant IDs per test.
/// </summary>
public class InMemoryEventStoreBackendTests : EventStoreBackendSpecification
{
    private readonly InMemoryEventStoreBackend _backend;

    public InMemoryEventStoreBackendTests()
    {
        _backend = new InMemoryEventStoreBackend(TimeProvider);
    }

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        return Task.FromResult<IEventStoreBackend>(_backend);
    }
}
