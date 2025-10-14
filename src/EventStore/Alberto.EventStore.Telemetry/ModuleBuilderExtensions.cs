using Alberto.EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Alberto.EventStore.Telemetry;

/// <summary>
/// Extension methods for adding OpenTelemetry support to EventStore modules
/// </summary>
public static class ModuleBuilderExtensions
{
    /// <summary>
    /// Enables OpenTelemetry diagnostics for this EventStore module.
    /// Replaces the default no-op diagnostics listener with ActivityDiagnosticEventListener
    /// and registers the ActivityTraceContextProvider for distributed tracing.
    /// </summary>
    /// <typeparam name="TEventStore">The EventStore factory type</typeparam>
    /// <param name="moduleBuilder">The module builder</param>
    /// <returns>The module builder for chaining</returns>
    public static ModuleBuilder<TEventStore> WithTelemetry<TEventStore>(
        this ModuleBuilder<TEventStore> moduleBuilder)
        where TEventStore : EventStoreFactory
    {
        // Replace no-op diagnostics with telemetry implementation
        moduleBuilder.Services.AddTransient<IDiagnosticsEventListener, ActivityDiagnosticEventListener>();

        // Register trace context provider for subscription filters
        moduleBuilder.Services.AddSingleton<ITraceContextProvider, ActivityTraceContextProvider>();

        return moduleBuilder;
    }
}