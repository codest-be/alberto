using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alberto.EventStore;

public static class EventStoreBuilderExtensions
{
    public static EventStoreBuilder AddEventStore(this IServiceCollection services)
    {
        services.AddScoped<ITenantContext, SingleTenantContext>();
        services.AddTransient<IDiagnosticsEventListener, NoopDiagnosticsEventListener>();
        services.AddSingleton(new EventTypeRegistry());
        services.AddTransient<IEventDeserializer, JsonEventDeserializer>();
        return new EventStoreBuilder(services);
    }

    public static EventStoreBuilder AddMultiTenancy<TTenantContext>(this EventStoreBuilder builder)
        where TTenantContext : class, ITenantContext
    {
        builder.Services.TryAddScoped<ITenantContext, TTenantContext>();
        return builder;
    }
}

public class EventStoreBuilder(IServiceCollection services)
{
    public IServiceCollection Services => services;
}