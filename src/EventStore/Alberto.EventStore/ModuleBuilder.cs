using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Events;
using Alberto.EventStore.MultiTenant;
using Alberto.EventStore.Serialization;
using Alberto.EventStore.Subscriptions;
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

    public IServiceCollection Services => _services;
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
    /// Marks the backend as configured. Called by backend extension methods (WithPostgres, WithInMemory).
    /// </summary>
    public void MarkBackendConfigured()
    {
        _backendConfigured = true;
    }

    /// <summary>
    /// Registers the EventStore factory for this module as a regular (non-keyed) service.
    /// The backend is keyed, but the EventStore facade is not - users inject the EventStore directly.
    /// </summary>
    public void RegisterEventStore()
    {
        _services.AddScoped<TEventStore>(provider =>
        {
            var backend = provider.GetRequiredKeyedService<IEventStoreBackend>(_moduleKey);
            var tenantContext = provider.GetRequiredService<ITenantContext>();
            var diagnostics = provider.GetService<IDiagnosticsEventListener>();

            return (TEventStore)Activator.CreateInstance(
                typeof(TEventStore),
                tenantContext,
                backend,
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