using Alberto.EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alberto.EventStore;

public static class EventStoreBuilderExtensions
{
    public static EventStoreBuilder AddEventStore(this IServiceCollection services)
    {
        services.AddScoped<EventStoreFactory>();
        services.AddTransient<IDiagnosticsEventListener, NoopDiagnosticsEventListener>();
        return new EventStoreBuilder(services);
    }
}

public class EventStoreBuilder(IServiceCollection services)
{
    public IServiceCollection Services => services;
}