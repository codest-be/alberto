using Alberto.EventStore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Alberto.EventStore.Telemetry;

public static class ServiceCollectionExtensions
{
    public static EventStoreBuilder AddEventStoreTelemetry(this EventStoreBuilder builder)
    {
        builder.Services.AddSingleton<IDiagnosticsEventListener, ActivityDiagnosticEventListener>();
        return builder;
    }

    public static TracerProviderBuilder AddEventStoreTelemetry(this TracerProviderBuilder builder)
    {
        return builder.AddSource(AlbertoActivitySource.Name);
    }
}