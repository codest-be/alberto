namespace EventStore;

public interface IEventStoreBackendFactory
{
    IEventStoreBackend Create();
}