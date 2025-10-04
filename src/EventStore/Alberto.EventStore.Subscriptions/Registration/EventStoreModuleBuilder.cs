using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Alberto.EventStore.Subscriptions.Filters;
using Alberto.EventStore.Subscriptions.Subscriptions;
using Alberto.EventStore.Subscriptions.Polling;
using System.Reflection;

namespace Alberto.EventStore.Subscriptions.Registration;

/// <summary>
/// Builder for configuring an event store module with subscriptions
/// </summary>
public interface IEventStoreModuleBuilder
{
    IEventStoreModuleBuilder AddEventPolling(
        string pollingId,
        Action<PollingOptions> configureOptions
    );

    IEventStoreModuleBuilder Pipeline(
        Action<IPipelineBuilder> configurePipeline
    );

    IEventStoreModuleBuilder AddEventHandler<THandler>()
        where THandler : class, IEventHandler;

    IEventStoreModuleBuilder AddEventHandlersFromAssembly(Assembly assembly);
}

public interface IPipelineBuilder
{
    IPipelineBuilder AddConsumeFilter<TFilter>()
        where TFilter : class, IConsumeFilter;
}

internal sealed class EventStoreModuleBuilder(IServiceCollection services, string moduleKey) : IEventStoreModuleBuilder
{
    public IEventStoreModuleBuilder AddEventPolling(
        string pollingId,
        Action<PollingOptions> configureOptions)
    {
        var options = new PollingOptions();
        configureOptions(options);

        // Register polling options for this specific module
        services.AddKeyedSingleton(moduleKey, (sp, key) => options);

        // Register as hosted service
        services.AddSingleton<IHostedService>(sp => new SubscriptionPollingService(
            moduleKey,
            sp.GetRequiredKeyedService<EventRouter>(moduleKey),
            sp.GetRequiredKeyedService<PollingOptions>(moduleKey),
            sp,
            sp.GetRequiredService<ILogger<SubscriptionPollingService>>()));

        return this;
    }

    public IEventStoreModuleBuilder Pipeline(Action<IPipelineBuilder> configurePipeline)
    {
        var pipelineBuilder = new PipelineBuilder(services, moduleKey);
        configurePipeline(pipelineBuilder);
        return this;
    }

    public IEventStoreModuleBuilder AddEventHandler<THandler>()
        where THandler : class, IEventHandler
    {
        // Register the handler with module key
        services.AddKeyedScoped<THandler>(moduleKey);

        // Register as IEventHandler for discovery within this module
        services.AddKeyedScoped<IEventHandler>(moduleKey, (sp, key) =>
            sp.GetRequiredKeyedService<THandler>(key));

        return this;
    }

    public IEventStoreModuleBuilder AddEventHandlersFromAssembly(Assembly assembly)
    {
        var handlerTypes = EventTypeDiscovery.DiscoverHandlerTypes(assembly);

        foreach (var handlerType in handlerTypes)
        {
            // Register the handler with module key
            services.AddKeyedScoped(handlerType, moduleKey);

            // Register as IEventHandler for discovery within this module
            services.AddKeyedScoped<IEventHandler>(moduleKey, (sp, key) =>
                (IEventHandler)sp.GetRequiredKeyedService(handlerType, key));
        }

        return this;
    }
}

internal sealed class PipelineBuilder(IServiceCollection services, string moduleKey) : IPipelineBuilder
{
    public IPipelineBuilder AddConsumeFilter<TFilter>()
        where TFilter : class, IConsumeFilter
    {
        // Register filter with module key
        services.AddKeyedScoped<TFilter>(moduleKey);
        services.AddKeyedScoped<IConsumeFilter>(moduleKey, (sp, key) =>
            sp.GetRequiredKeyedService<TFilter>(key));
        return this;
    }
}