using Alberto.Dcb.InMemory;

namespace Alberto.Dcb.Tests;

/// <summary>
/// Specification tests for InMemoryEventStoreBackend.
/// </summary>
public class InMemoryEventStoreBackendTests : EventStoreBackendSpecification
{
    private InMemoryEventStoreBackend? _backend;

    protected override Task<IEventStoreBackend> CreateBackend()
    {
        _backend = new InMemoryEventStoreBackend(TimeProvider);
        return Task.FromResult<IEventStoreBackend>(_backend);
    }

    protected override Task CleanupAsync()
    {
        _backend?.Clear();
        return Task.CompletedTask;
    }
}
