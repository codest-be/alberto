namespace EventStore.InMemory;

public class InMemoryEventStoreBackendFactory(IEventStoreBackend backend) : IEventStoreBackendFactory
{
    public IEventStoreBackend Create() => backend;
}