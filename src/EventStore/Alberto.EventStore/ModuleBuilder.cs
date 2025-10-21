using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Serialization;
using Alberto.EventStore.Subscriptions;
using Alberto.EventStore.Subscriptions.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Alberto.EventStore;

/// <summary>
/// Fluent builder for configuring an EventStore module with backend, multi-tenancy, subscriptions, and telemetry.
/// </summary>
/// <typeparam name="TEventStore">The EventStore factory type that identifies this module</typeparam>
public class ModuleBuilder<TEventStore> where TEventStore : EventStoreFactory
{
    private readonly string _moduleKey;
    private readonly IServiceCollection _services;
    private bool _backendConfigured;

    internal ModuleBuilder(IServiceCollection services, string moduleKey)
    {
        _services = services;
        _moduleKey = moduleKey;
        _backendConfigured = false;

        // Register core EventStore services for this module
        RegisterCoreServices();
    }

    /// <summary>
    /// Gets the service collection for advanced configuration.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Gets the unique key for this module.
    /// </summary>
    public string ModuleKey => _moduleKey;

    private void RegisterCoreServices()
    {
        // Register single-tenant context by default (can be overridden by WithMultiTenancy)
        _services.TryAddScoped<ITenantContext, SingleTenantContext>();

        // Register event type registry (shared singleton)
        _services.TryAddSingleton<EventTypeRegistry>();

        // Register event deserializer
        _services.TryAddTransient<IEventDeserializer, JsonEventDeserializer>();

        // Register no-op diagnostics by default (can be overridden by WithTelemetry)
        _services.TryAddTransient<IDiagnosticsEventListener, NoopDiagnosticsEventListener>();

        // Register no-op metrics by default (can be overridden by WithTelemetry)
        _services.TryAddSingleton<IMetricsRecorder, NoopMetricsRecorder>();
    }

    /// <summary>
    /// Configures multi-tenancy for this EventStore module.
    /// </summary>
    /// <typeparam name="TTenantContext">The tenant context implementation</typeparam>
    /// <returns>The module builder for chaining</returns>
    public ModuleBuilder<TEventStore> WithMultiTenancy<TTenantContext>()
        where TTenantContext : class, ITenantContext
    {
        _services.AddScoped<ITenantContext, TTenantContext>();
        return this;
    }

    /// <summary>
    /// Configures polling-based event subscriptions for this EventStore module.
    /// </summary>
    /// <param name="configure">Configuration action for subscription pipeline</param>
    /// <returns>The module builder for chaining</returns>
    public ModuleBuilder<TEventStore> WithPollingSubscriptions(
        Action<PollingSubscriptionsBuilder<TEventStore>> configure)
    {
        if (!_backendConfigured)
        {
            throw new InvalidOperationException(
                "Backend must be configured before subscriptions. Call WithPostgres() or WithInMemory() first.");
        }

        var builder = new PollingSubscriptionsBuilder<TEventStore>(_services, _moduleKey);
        configure(builder);
        builder.Build();

        return this;
    }

    /// <summary>
    /// Configures channel-based event subscriptions for this EventStore module.
    /// Supports Sync (immediate), Async (polling), and Hybrid (both) modes.
    /// </summary>
    /// <param name="configure">Configuration action for subscription pipeline</param>
    /// <returns>The module builder for chaining</returns>
    public ModuleBuilder<TEventStore> WithChannelSubscriptions(
        Action<ChannelSubscriptionsBuilder<TEventStore>> configure)
    {
        if (!_backendConfigured)
        {
            throw new InvalidOperationException(
                "Backend must be configured before subscriptions. Call WithPostgres() or WithInMemory() first.");
        }

        // Ensure ChannelSubscriptionRegistry is registered as singleton (shared across all modules)
        _services.TryAddSingleton<ChannelSubscriptionRegistry>();

        var builder = new ChannelSubscriptionsBuilder<TEventStore>(_services, _moduleKey);
        configure(builder);
        builder.Build();

        return this;
    }

    /// <summary>
    /// Marks the backend as configured. Called internally by backend extension methods.
    /// </summary>
    /// <remarks>
    /// This is called by <c>WithPostgres()</c> and <c>WithInMemory()</c> to prevent
    /// subscription configuration before backend setup.
    /// </remarks>
    public void MarkBackendConfigured()
    {
        _backendConfigured = true;
    }

    /// <summary>
    /// Registers the EventStore factory as a scoped service. Called internally by backend extensions.
    /// </summary>
    /// <remarks>
    /// The EventStore is registered as a non-keyed service for direct injection,
    /// while the backend is keyed for module isolation.
    /// </remarks>
    public void RegisterEventStore()
    {
        // Ensure ChannelSubscriptionRegistry is always registered (required dependency)
        _services.TryAddSingleton<ChannelSubscriptionRegistry>();

        _services.AddScoped<TEventStore>(provider =>
        {
            var backend = provider.GetRequiredKeyedService<IEventStoreBackend>(_moduleKey);
            var tenantContext = provider.GetRequiredService<ITenantContext>();
            var channelRegistry = provider.GetRequiredService<ChannelSubscriptionRegistry>();
            var diagnostics = provider.GetService<IDiagnosticsEventListener>();

            return (TEventStore)Activator.CreateInstance(
                typeof(TEventStore),
                tenantContext,
                backend,
                channelRegistry,
                diagnostics)!;
        });
    }
}

/// <summary>
/// Extension methods for adding EventStore modules to the service collection.
/// </summary>
public static class ModuleBuilderExtensions
{
    /// <summary>
    /// Adds a new EventStore module with the specified configuration.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore factory type</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="moduleKey">Unique key for this module (e.g., "orders", "payments")</param>
    /// <param name="configure">Configuration action for the module</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddModule<TEventStore>(
        this IServiceCollection services,
        string moduleKey,
        Action<ModuleBuilder<TEventStore>> configure)
        where TEventStore : EventStoreFactory
    {
        var builder = new ModuleBuilder<TEventStore>(services, moduleKey);
        configure(builder);
        return services;
    }
}