using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Postgres;

public class PostgresEventStoreBackendFactory(
    IServiceProvider serviceProvider,
    ISchemaContext schemaContext)
    : IEventStoreBackendFactory
{
    public IEventStoreBackend Create()
    {
        return serviceProvider.GetRequiredKeyedService<IEventStoreBackend>(schemaContext.CurrentSchema);
    }
}