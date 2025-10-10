using System.Reflection;
using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.Polling;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alberto.EventStore.Subscriptions.Registration;

/// <summary>
/// Builder for configuring an event store module with subscriptions
/// </summary>
public sealed class EventStoreModuleBuilder(IServiceCollection services, string moduleKey)
{
    public IServiceCollection Services => services;
    public string ModuleKey => moduleKey;

    public EventStoreModuleBuilder AddPolling(Action<PollingOptions> configureOptions)
    {
        var options = new PollingOptions();
        configureOptions(options);

        // Register polling options for this specific module
        services.AddKeyedSingleton(moduleKey, (_, _) => options);

        // Register as hosted service
        services.AddSingleton<IHostedService>(sp => new SubscriptionPollingService(
            moduleKey,
            sp.GetRequiredKeyedService<EventRouter>(moduleKey),
            sp.GetRequiredKeyedService<PollingOptions>(moduleKey),
            sp,
            sp.GetRequiredService<ILogger<SubscriptionPollingService>>()));

        return this;
    }

    public EventStoreModuleBuilder ConfigurePipeline(Action<PipelineBuilder> configurePipeline)
    {
        var pipelineBuilder = new PipelineBuilder(services, moduleKey);
        configurePipeline(pipelineBuilder);
        return this;
    }

    public EventStoreModuleBuilder AddSubscription<THandler>()
        where THandler : class, IEventHandler
    {
        // Register the handler with module key
        services.AddKeyedScoped<THandler>(moduleKey);

        // Register as IEventHandler for discovery within this module
        services.AddKeyedScoped<IEventHandler>(moduleKey, (sp, _) =>
            sp.GetRequiredKeyedService<THandler>(moduleKey));

        return this;
    }

    public EventStoreModuleBuilder AddSubscriptionsFromAssembly(Assembly assembly)
    {
        var handlerTypes = EventTypeDiscovery.DiscoverHandlerTypes(assembly);

        foreach (var handlerType in handlerTypes)
        {
            // Register the handler with module key
            services.AddKeyedScoped(handlerType, moduleKey);

            // Register as IEventHandler for discovery within this module
            services.AddKeyedScoped<IEventHandler>(moduleKey, (sp, _) =>
                (IEventHandler)sp.GetRequiredKeyedService(handlerType, moduleKey));
        }

        return this;
    }

    public void AddDiagnosticEventListener<TDiagnosticsEventListener>()
        where TDiagnosticsEventListener : class, IDiagnosticsEventListener
    {
        services.AddKeyedSingleton<IDiagnosticsEventListener, TDiagnosticsEventListener>(moduleKey);
    }

    public void AddTraceContextProvider<TTraceContextProvider>()
        where TTraceContextProvider : class, ITraceContextProvider
    {
        services.AddKeyedSingleton<ITraceContextProvider, TTraceContextProvider>(moduleKey);
    }
}

public sealed class PipelineBuilder(IServiceCollection services, string moduleKey)
{
    public PipelineBuilder AddConsumeFilter<TFilter>()
        where TFilter : class, IConsumeFilter
    {
        // Register filter with module key
        services.AddKeyedScoped<TFilter>(moduleKey);
        services.AddKeyedScoped<IConsumeFilter>(moduleKey, (sp, key) =>
            sp.GetRequiredKeyedService<TFilter>(key));
        return this;
    }
}