using EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace EventStore.Telemetry;

public static class ServiceCollectionExtensions
{
    public static EventStoreBuilder AddTelemetry(this EventStoreBuilder builder)
    {
        builder.Services.AddSingleton<IDiagnosticsEventListener, ActivityDiagnosticEventListener>();
        return builder;
    }
    
    public static TracerProviderBuilder AddEventStoreTelemetry(this TracerProviderBuilder builder)
        => builder.AddSource(AlbertoActivitySource.Name);
}