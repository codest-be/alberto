using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.MultiTenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alberto.EventStore;

public static class EventStoreBuilderExtensions
{
    public static EventStoreBuilder AddEventStore(this IServiceCollection services)
    {
        services.AddScoped<EventStoreFactory>();
        services.AddScoped<ITenantContext, SingleTenantContext>();
        services.AddTransient<IDiagnosticsEventListener, NoopDiagnosticsEventListener>();
        return new EventStoreBuilder(services);
    }
    
    public static EventStoreBuilder AddMultiTenancy<TTenantContext>(this IServiceCollection services) where TTenantContext : class, ITenantContext
    {
        services.TryAddScoped<ITenantContext, TTenantContext>();
        return new EventStoreBuilder(services);
    }
}

public class EventStoreBuilder(IServiceCollection services)
{
    public IServiceCollection Services => services;
}