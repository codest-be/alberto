using Alberto.EventStore.Diagnostics;
using Alberto.EventStore.Subscriptions.Registration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Alberto.EventStore.Telemetry;

public static class ServiceCollectionExtensions
{
    public static EventStoreModuleBuilder AddOpenTelemetry(this EventStoreModuleBuilder builder)
    {
        builder.AddTelemetry<ActivityDiagnosticEventListener>();
        return builder;
    }

    public static TracerProviderBuilder AddEventStoreTelemetry(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoActivitySource.Name);
    }

    /// <summary>
    /// Adds OpenTelemetry trace context provider for end-to-end subscription tracing
    /// </summary>
    public static IServiceCollection AddEventStoreTelemetryTracing(this IServiceCollection services)
    {
        // Replace the default no-op trace context provider with OpenTelemetry implementation
        services.AddSingleton<ITraceContextProvider, ActivityTraceContextProvider>();
        return services;
    }
}